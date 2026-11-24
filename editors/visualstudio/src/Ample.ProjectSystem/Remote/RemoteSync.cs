using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Ample.VisualStudio.Remote;

/// <summary>What a pull or a push did, file by file (project-relative paths with '/').</summary>
public sealed class SyncResult
{
    /// <summary>Gets the files that were fetched from the server (new there, or changed there while the local copy had no changes).</summary>
    public List<string> Fetched { get; } = [];

    /// <summary>Gets the files that were written to the server.</summary>
    public List<string> Pushed { get; } = [];

    /// <summary>Gets the files that changed on both sides: the local one went to <c>_conflicts/&lt;id&gt;/</c> (pull) or was not sent (push).</summary>
    public List<string> Conflicts { get; } = [];

    /// <summary>Gets the files that are gone on the server but still here (they are kept).</summary>
    public List<string> GoneOnServer { get; } = [];

    /// <summary>Gets the files over the size limit that were not fetched.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Gets the files the server listed that were refused: a path that leaves the cache or names a file the cache keeps for itself, or bytes that do not match the listed hash.</summary>
    public List<string> Refused { get; } = [];

    /// <summary>Gets the id of the conflict folder (null when nothing conflicted).</summary>
    public string? ConflictId { get; internal set; }
}

/// <summary>
/// Keeps a local folder in step with the project of a server's AMPLE (the cache that Visual Studio works on). A manifest in
/// <c>.amplerm/manifest.txt</c> holds the SHA-256 of every file as it was at the last pull or push: a file is "changed here" when its hash differs from the
/// manifest, "changed there" when the server's hash does. Changed on one side only: it goes to the other side. Changed on both sides and different: the local
/// copy is moved to <c>_conflicts/&lt;id&gt;/&lt;path&gt;</c> (a pull) or not sent (a push), nothing is lost and nothing is deleted.
/// </summary>
public sealed class RemoteSync
{
    private static readonly string[] SkipFolders = [".amplerm", "_conflicts", ".vs", "bin", "obj", "Rules"];
    private static readonly string[] SkipFiles = ["Ample.targets"];
    private readonly RemoteClient client;
    private readonly string root;
    private readonly Dictionary<string, string> manifest = new(System.StringComparer.Ordinal);

    /// <param name="client">The connection.</param>
    /// <param name="localRoot">The local folder (the cache).</param>
    public RemoteSync(RemoteClient client, string localRoot)
    {
        this.client = client;
        root = System.IO.Path.GetFullPath(localRoot);
        LoadManifest();
    }

    /// <summary>Gets or sets the size above which a file is not fetched (it is listed as skipped); 0 means no limit.</summary>
    public long MaxFetchBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Brings the server's changes here.</summary>
    public async System.Threading.Tasks.Task<SyncResult> PullAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        SyncResult result = new();
        List<FileEntry> server = await ListAsync(cancellationToken).ConfigureAwait(false);
        string? conflictFolder = null;
        foreach (FileEntry entry in server)
        {
            string relative = entry.Path;
            string? local = SafeLocal(relative);
            if (local is null)
            {
                result.Refused.Add(relative);
                continue;
            }

            manifest.TryGetValue(relative, out string? baseSha);
            string? localSha = System.IO.File.Exists(local) ? Sha(local) : null;
            if (localSha == entry.Sha256)
            {
                manifest[relative] = entry.Sha256;
                continue;
            }

            bool localChanged = localSha is not null && localSha != baseSha;
            bool serverChanged = entry.Sha256 != baseSha;
            if (!serverChanged) continue;   // only changed here: Push sends it
            if (MaxFetchBytes > 0 && entry.Size > MaxFetchBytes)
            {
                result.Skipped.Add(relative);
                continue;
            }

            if (localChanged)
            {
                result.ConflictId ??= NewId();
                conflictFolder ??= System.IO.Path.Combine(root, "_conflicts", result.ConflictId);
                string aside = System.IO.Path.Combine(conflictFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aside)!);
                System.IO.File.Move(local, aside);
                result.Conflicts.Add(relative);
            }

            byte[] bytes = await client.GetBytesAsync("/api/project/export?path=" + System.Uri.EscapeDataString(relative), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Hex(SHA256Of(bytes)), entry.Sha256, System.StringComparison.OrdinalIgnoreCase))
            {
                result.Refused.Add(relative);   // changed on the server while it was being fetched, or not what it was listed as: nothing is written
                continue;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
            System.IO.File.WriteAllBytes(local, bytes);
            manifest[relative] = entry.Sha256;
            result.Fetched.Add(relative);
        }

        HashSet<string> onServer = new(server.Select(e => e.Path), System.StringComparer.Ordinal);
        foreach (string relative in manifest.Keys.Where(k => !onServer.Contains(k) && SafeLocal(k) is string kept && System.IO.File.Exists(kept)).ToList()) result.GoneOnServer.Add(relative);
        SaveManifest();
        return result;
    }

    /// <summary>Sends the local changes to the server (new files and files that changed here only).</summary>
    public async System.Threading.Tasks.Task<SyncResult> PushAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        SyncResult result = new();
        Dictionary<string, FileEntry> server = (await ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.Path, System.StringComparer.Ordinal);
        foreach (string full in LocalFiles())
        {
            string relative = full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
            byte[] content = System.IO.File.ReadAllBytes(full);
            string sha = Hex(SHA256Of(content));
            manifest.TryGetValue(relative, out string? baseSha);
            if (sha == baseSha) continue;
            if (server.TryGetValue(relative, out FileEntry? there))
            {
                if (there.Sha256 == sha)
                {
                    manifest[relative] = sha;
                    continue;
                }

                if (there.Sha256 != baseSha)
                {
                    result.Conflicts.Add(relative);   // changed on both sides: not sent, a pull sets the local one aside
                    continue;
                }
            }

            string directory = System.IO.Path.GetDirectoryName(relative.Replace('/', System.IO.Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
            string name = System.IO.Path.GetFileName(relative);
            try
            {
                await client.PostBytesAsync(
                    "/api/project/upload?name=" + System.Uri.EscapeDataString(name) + "&dir=" + System.Uri.EscapeDataString(directory)
                    + "&onConflict=overwrite-record&toWav=0&ifMatch=" + (baseSha ?? "none"),
                    content,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteRequestException ex) when (ex.Status == 409)
            {
                result.Conflicts.Add(relative);   // the server saw the file change since the list: it refused, nothing was written
                continue;
            }

            manifest[relative] = sha;
            result.Pushed.Add(relative);
        }

        SaveManifest();
        return result;
    }

    private async System.Threading.Tasks.Task<List<FileEntry>> ListAsync(System.Threading.CancellationToken cancellationToken)
    {
        string json = await client.GetStringAsync("/api/project/files?hashes=1", cancellationToken).ConfigureAwait(false);
        DataContractJsonSerializer serializer = new(typeof(FileEntry[]));
        using System.IO.MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return [.. (FileEntry[])serializer.ReadObject(stream)!];
    }

    private IEnumerable<string> LocalFiles()
    {
        Stack<string> folders = new([root]);
        while (folders.Count > 0)
        {
            string folder = folders.Pop();
            foreach (string sub in System.IO.Directory.EnumerateDirectories(folder))
            {
                string name = System.IO.Path.GetFileName(sub);
                if (folder == root && SkipFolders.Contains(name, System.StringComparer.OrdinalIgnoreCase)) continue;
                if (name.Length > 0 && name[0] == '.') continue;
                if ((System.IO.File.GetAttributes(sub) & System.IO.FileAttributes.ReparsePoint) != 0) continue;   // never follow a link out of the cache
                folders.Push(sub);
            }

            foreach (string file in System.IO.Directory.EnumerateFiles(folder))
            {
                string name = System.IO.Path.GetFileName(file);
                if (folder == root && KeptForItself(name)) continue;
                yield return file;
            }
        }
    }

    /// <summary>Whether a file of the cache root is the extension's own (project files, the solution, the targets): the server never writes them and a push never sends them.</summary>
    private static bool KeptForItself(string name) =>
        SkipFiles.Contains(name, System.StringComparer.OrdinalIgnoreCase) || name.EndsWith(".ampleproj", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ampleproj.user", System.StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sln", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The local file for a path the server listed, or null when the path is not safe: empty, rooted, with a drive or stream colon, a "..", a hidden or
    /// reserved first folder, an empty or dotted/spaced segment, a name the cache keeps for itself, or when it would end outside the cache folder.
    /// The server is not trusted with where a file goes (it could otherwise replace the project file that names the program to run, or the connection settings).
    /// </summary>
    private string? SafeLocal(string relative)
    {
        if (relative.Length == 0 || relative.Length > 400 || relative.IndexOfAny([':', '\\']) >= 0 || relative[0] == '/') return null;
        string[] parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == "." || part == ".." || part[part.Length - 1] == '.' || part[part.Length - 1] == ' ') return null;
            if (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
        }

        if (parts[0].Length > 0 && parts[0][0] == '.') return null;
        if (parts.Length > 1 && SkipFolders.Contains(parts[0], System.StringComparer.OrdinalIgnoreCase)) return null;
        if (parts.Length == 1 && KeptForItself(parts[0])) return null;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), System.StringComparison.Ordinal) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static byte[] SHA256Of(byte[] bytes)
    {
#if NET5_0_OR_GREATER
        return SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#endif
    }

    private static string Hex(byte[] bytes) => ToHex(bytes);

    private static string Sha(string file)
    {
        using System.IO.FileStream stream = System.IO.File.OpenRead(file);
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    private static string ToHex(byte[] bytes)
    {
        StringBuilder sb = new();
        foreach (byte b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string NewId() => ToHex(RandomNumberGenerator_Bytes(3)) + "-" + System.DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] RandomNumberGenerator_Bytes(int count)
    {
        byte[] bytes = new byte[count];
        using RandomNumberGenerator random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return bytes;
    }

    private string ManifestPath => System.IO.Path.Combine(root, ".amplerm", "manifest.txt");

    private void LoadManifest()
    {
        if (!System.IO.File.Exists(ManifestPath)) return;
        foreach (string line in System.IO.File.ReadAllLines(ManifestPath))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0) manifest[line.Substring(tab + 1)] = line.Substring(0, tab);
        }
    }

    private void SaveManifest()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ManifestPath)!);
        System.IO.File.WriteAllLines(ManifestPath, manifest.OrderBy(m => m.Key, System.StringComparer.Ordinal).Select(m => m.Value + "\t" + m.Key));
    }

    [DataContract]
    private sealed class FileEntry
    {
        [DataMember(Name = "path")] public string Path { get; set; } = string.Empty;

        [DataMember(Name = "size")] public long Size { get; set; }

        [DataMember(Name = "sha256")] public string Sha256 { get; set; } = string.Empty;
    }
}
