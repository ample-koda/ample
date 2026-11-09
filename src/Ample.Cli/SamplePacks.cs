using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ample.Cli;

/// <summary>One sample pack of the catalog: a set of CC0 or CC BY recordings that AMPLE's sampled instruments play.</summary>
/// <param name="Id">The pack id (also the folder name).</param>
/// <param name="Tier">free (CC0, no conditions) or licensed (the licence asks for a credit; the pack carries it).</param>
/// <param name="Title">The name to show.</param>
/// <param name="License">The licence as the source states it.</param>
/// <param name="Description">What the pack holds.</param>
/// <param name="Instruments">The instruments in it (search words).</param>
/// <param name="Bytes">The size of the zip.</param>
/// <param name="Files">The number of files in it.</param>
/// <param name="Sha256">The SHA-256 of the zip, pinned in this program.</param>
/// <param name="Asset">The file name of the zip on the download server.</param>
internal sealed record SamplePack(string Id, string Tier, string Title, string License, string Description, string[] Instruments, long Bytes, int Files, string Sha256, string Asset);

/// <summary>
/// The sample packs AMPLE can download (<c>ample packs available</c>, <c>ample packs install &lt;id&gt;</c>, <c>ample packs remove &lt;id&gt;</c>): the catalog is part of this program with the SHA-256 of every zip,
/// so a zip that was changed on the server is refused. A pack that is installed already is shown and kept: only what is missing is downloaded. Nothing needs Node or administrator rights.
/// </summary>
internal static class SamplePacks
{
    /// <summary>Where the zips are (a release of the public repository); <c>--from</c> names another place with the same file names.</summary>
    internal const string BaseUrl = "https://github.com/ample-koda/ample/releases/download/audio-packs-1/";

    /// <summary>The AMPLE mirror of the same zips; <c>--mirror</c> uses it. The SHA-256 is checked as well.</summary>
    internal const string MirrorUrl = "https://get.ample-koda.dev/mirror/audio-packs/";

    private const long MaxZipBytes = 1024L * 1024 * 1024;
    private const long MaxUnpackedBytes = 3L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly Lazy<IReadOnlyList<SamplePack>> Embedded = new(() => Parse(ReadEmbedded()));

    /// <summary>The catalog that ships with this program.</summary>
    internal static IReadOnlyList<SamplePack> Catalog => Embedded.Value;

    internal static string TierFolder(string home, SamplePack pack) => Path.Combine(home, "audio-packs", pack.Tier);

    internal static string PackFolder(string home, SamplePack pack) => Path.Combine(TierFolder(home, pack), pack.Id);

    /// <summary>True when the pack is in the user's audio-packs folder (its folder holds files).</summary>
    internal static bool IsInstalled(string home, SamplePack pack)
    {
        string folder = PackFolder(home, pack);
        return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
    }

    /// <summary>The size of an installed pack on disk, in bytes (0 when it is not installed).</summary>
    internal static long SizeOnDisk(string home, SamplePack pack)
    {
        string folder = PackFolder(home, pack);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
    }

    /// <summary>Reads a catalog from JSON (an array of packs); anything that is not a valid entry is left out.</summary>
    internal static IReadOnlyList<SamplePack> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<SamplePack> list = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        foreach (JsonElement e in document.RootElement.EnumerateArray())
        {
            string id = Text(e, "id");
            string tier = Text(e, "tier");
            string sha = Text(e, "sha256");
            string asset = Text(e, "asset");
            if (!ValidId(id) || tier is not ("free" or "licensed") || sha.Length != 64 || !ValidId(Path.GetFileNameWithoutExtension(asset)) || !asset.EndsWith(".zip", StringComparison.Ordinal)) continue;
            string[] instruments = e.TryGetProperty("instruments", out JsonElement i) && i.ValueKind == JsonValueKind.Array ? [.. i.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0)] : [];
            list.Add(new SamplePack(id, tier, Text(e, "title").Length > 0 ? Text(e, "title") : id, Text(e, "license"), Text(e, "description"), instruments, e.TryGetProperty("bytes", out JsonElement b) && b.TryGetInt64(out long bytes) ? bytes : 0, e.TryGetProperty("files", out JsonElement f) && f.TryGetInt32(out int files) ? files : 0, sha.ToLowerInvariant(), asset));
        }

        return list;
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string ReadEmbedded()
    {
        Assembly assembly = typeof(SamplePacks).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("audio-packs-index.json");
        if (stream is null) return string.Empty;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    internal static SamplePack? Find(string id, IReadOnlyList<SamplePack>? catalog = null) => (catalog ?? Catalog).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Prints the packs with what is installed (a table, or JSON for the setup wizard).</summary>
    internal static int Available(TextWriter output, string home, bool json, IReadOnlyList<SamplePack>? catalog = null)
    {
        IReadOnlyList<SamplePack> packs = catalog ?? Catalog;
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(packs.Select(p => new { id = p.Id, tier = p.Tier, title = p.Title, license = p.License, description = p.Description, instruments = p.Instruments, bytes = p.Bytes, installed = IsInstalled(home, p), installedBytes = SizeOnDisk(home, p) }), JsonOut));
            return 0;
        }

        if (packs.Count == 0)
        {
            output.WriteLine("This build has no sample packs in its catalog.");
            return 0;
        }

        foreach (SamplePack p in packs)
        {
            output.WriteLine($"{(IsInstalled(home, p) ? "installed" : "         ")}  {p.Id,-26} {p.Tier,-8} {p.Bytes / 1024.0 / 1024.0,7:0.0} MB  {p.Title}");
        }

        return 0;
    }

    /// <summary>Downloads and unpacks the packs that are not installed yet (<paramref name="what"/> is a pack id or <c>all</c>). Installed packs are shown and kept.</summary>
    internal static async Task<int> InstallAsync(TextWriter output, string home, string what, string? baseUrl, CancellationToken token, Action<int, string>? progress = null, IReadOnlyList<SamplePack>? catalog = null)
    {
        IReadOnlyList<SamplePack> packs = catalog ?? Catalog;
        List<SamplePack> wanted = string.Equals(what, "all", StringComparison.OrdinalIgnoreCase) ? [.. packs] : Find(what, packs) is { } one ? [one] : [];
        if (wanted.Count == 0)
        {
            output.WriteLine($"There is no sample pack '{what}'. Run 'ample packs available' to see the packs.");
            return 2;
        }

        int failed = 0;
        foreach (SamplePack pack in wanted)
        {
            if (IsInstalled(home, pack))
            {
                output.WriteLine($"installed  {pack.Id} (kept; only what is missing is downloaded)");
                continue;
            }

            string? problem = await InstallOneAsync(output, home, pack, baseUrl ?? BaseUrl, progress, token);
            if (problem is not null)
            {
                output.WriteLine($"FAILED  {pack.Id}: {problem}");
                failed++;
            }
            else
            {
                output.WriteLine($"installed  {pack.Id}  {PackFolder(home, pack)}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task<string?> InstallOneAsync(TextWriter output, string home, SamplePack pack, string baseUrl, Action<int, string>? progress, CancellationToken token)
    {
        string tierFolder = TierFolder(home, pack);
        Directory.CreateDirectory(tierFolder);
        string partial = Path.Combine(tierFolder, pack.Id + ".zip.part");
        string stage = Path.Combine(tierFolder, pack.Id + ".stage");
        try
        {
            string address = baseUrl.TrimEnd('/') + "/" + pack.Asset;
            progress?.Invoke(-1, $"Connecting to {new Uri(address).Host} for {pack.Title}");
            using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(30) };
            using HttpResponseMessage response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return $"the download failed ({(int)response.StatusCode}).";
            long total = response.Content.Headers.ContentLength ?? pack.Bytes;
            if (total > MaxZipBytes || pack.Bytes > MaxZipBytes) return "the zip exceeds its size bound.";
            using (Stream source = await response.Content.ReadAsStreamAsync(token))
            using (FileStream target = new(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1 << 16];
                long read = 0;
                int last = -1, n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;
                    if (read > MaxZipBytes) return "the zip exceeds its size bound.";
                    int percent = total > 0 ? (int)Math.Min(99, read * 100 / total) : -1;
                    if (percent != last) { last = percent; progress?.Invoke(percent, $"{pack.Title}: {read / 1024 / 1024} of {Math.Max(total, read) / 1024 / 1024} MB"); }
                }
            }

            progress?.Invoke(-1, $"Checking {pack.Title}");
            string actual = await HashAsync(partial, token);
            if (!string.Equals(actual, pack.Sha256, StringComparison.OrdinalIgnoreCase)) return "the SHA-256 of the download does not match the one pinned in this program; it was not installed.";
            progress?.Invoke(-1, $"Unpacking {pack.Title}");
            string? refused = Unpack(partial, stage, pack.Id);
            if (refused is not null) return refused;
            string target2 = PackFolder(home, pack);
            if (Directory.Exists(target2)) Directory.Delete(target2, true);   // an empty or half folder of an earlier try
            Directory.Move(Path.Combine(stage, pack.Id), target2);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            return exception.Message;
        }
        finally
        {
            TryDelete(partial);
            TryDeleteFolder(stage);
        }
    }

    /// <summary>Unpacks a pack zip into <paramref name="stage"/> after checking every entry: all under <c>&lt;id&gt;/</c>, no absolute path, no drive, no '..', a bounded size. Returns the reason when it is refused, else null.</summary>
    internal static string? Unpack(string zipPath, string stage, string id)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = entry.FullName;
            string[] parts = path.Split('/', '\\');
            if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':') || parts.Contains("..") || path.Contains('\0')) return $"the zip holds an unsafe path ('{path}').";
            if (parts[0] != id) return $"the zip holds a file outside {id}/ ('{path}').";
            total += entry.Length;
            if (total > MaxUnpackedBytes) return "the zip would unpack to more than 3 GB.";
        }

        string stageFull = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        TryDeleteFolder(stage);
        Directory.CreateDirectory(stage);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            if (!target.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase)) return $"the zip holds a path outside its folder ('{entry.FullName}').";
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }

        return Directory.Exists(Path.Combine(stage, id)) ? null : "the zip is empty.";
    }

    /// <summary>Removes an installed pack (its folder under audio-packs/&lt;tier&gt;/&lt;id&gt;), only a pack of the catalog and only below the user's audio-packs folder.</summary>
    internal static int Remove(TextWriter output, string home, string id, IReadOnlyList<SamplePack>? catalog = null)
    {
        SamplePack? pack = Find(id, catalog ?? Catalog);
        if (pack is null)
        {
            output.WriteLine($"There is no sample pack '{id}' to remove. Run 'ample packs available' to see the packs.");
            return 2;
        }

        string folder = Path.GetFullPath(PackFolder(home, pack));
        string packsRoot = Path.GetFullPath(Path.Combine(home, "audio-packs")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(packsRoot, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("That pack folder is not below the audio-packs folder; it is left alone.");
            return 1;
        }

        if (!Directory.Exists(folder))
        {
            output.WriteLine($"{pack.Id} is not installed.");
            return 0;
        }

        FileAttributes attributes = new DirectoryInfo(folder).Attributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            output.WriteLine($"{pack.Id} is a link to a folder elsewhere; it is not removed (remove the link yourself).");
            return 1;
        }

        Directory.Delete(folder, true);
        output.WriteLine($"removed  {pack.Id}");
        return 0;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover in the audio-packs folder is harmless */ }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { /* a leftover in the audio-packs folder is harmless */ }
    }
}
