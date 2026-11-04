using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>
/// The latest known summary of a project file (the waveform of an audio file, the notes of a MIDI file), kept on disk in <c>.ample/previews</c> so it survives a restart.
/// There is ONE entry per file path and it only ever holds the latest state of that file: when the file changes the entry is replaced, and when it goes back to an
/// earlier state it is worked out again (nothing older is kept). An entry is still good when the size and the last-write time are the same; when only the time differs the
/// content hash decides (a file that was touched but not changed keeps its entry). Entries of files that are gone are removed.
/// </summary>
internal sealed class ProjectPreviewStore
{
    private const int Version = 1;
    private static readonly ConcurrentDictionary<string, ProjectPreviewStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly string folder;
    private readonly string root;
    private int pruned;

    private ProjectPreviewStore(string root)
    {
        this.root = root;
        folder = Path.Combine(root, ProjectLayout.AmpleFolder, "previews");
    }

    /// <summary>Gets the store of a project folder.</summary>
    public static ProjectPreviewStore For(string root) => Stores.GetOrAdd(Path.GetFullPath(root), r => new ProjectPreviewStore(r));

    /// <summary>The summary of the file: the kept one when it is still good, else the one made by <paramref name="create"/> (and then kept in its place).</summary>
    public async Task<JsonNode> GetAsync(string relative, string full, string kind, Func<Task<object>> create)
    {
        FileInfo info = new(full);
        long size = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
        string entryFile = EntryFile(relative);
        Entry? kept = Read(entryFile, relative, kind);
        if (kept is not null && kept.Size == size)
        {
            if (kept.Ticks == ticks)
            {
                return kept.Value;
            }

            // the time is different but the size is the same: the content decides
            if (FileHashCache.Sha256Hex(full) == kept.Hash)
            {
                Write(entryFile, kept with { Ticks = ticks });
                return kept.Value;
            }
        }

        object made = await create().ConfigureAwait(false);
        JsonNode value = JsonSerializer.SerializeToNode(made) ?? new JsonObject();
        FileInfo after = new(full);
        if (after.Length == size && after.LastWriteTimeUtc.Ticks == ticks)
        {
            // the one entry of this file is replaced (the file did not change while it was read)
            Write(entryFile, new Entry(relative, kind, size, ticks, FileHashCache.Sha256Hex(full), value));
        }

        PruneOnce();
        return value;
    }

    /// <summary>The same for a summary that is worked out at once (no waiting).</summary>
    public JsonNode Get(string relative, string full, string kind, Func<object> create) => GetAsync(relative, full, kind, () => Task.FromResult(create())).GetAwaiter().GetResult();

    private string EntryFile(string relative)
    {
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant())))[..32];
        return Path.Combine(folder, name + ".json");
    }

    private static Entry? Read(string file, string relative, string kind)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            JsonNode? node = JsonNode.Parse(File.ReadAllText(file));
            if (node is not JsonObject o || (int?)o["v"] != Version || (string?)o["path"] != relative || (string?)o["kind"] != kind || o["value"] is null)
            {
                return null;
            }

            return new Entry(relative, kind, (long)o["size"]!, (long)o["ticks"]!, (string)o["hash"]!, o["value"]!.DeepClone());
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return null;   // an entry that cannot be read is simply made again
        }
    }

    private void Write(string file, Entry entry)
    {
        try
        {
            Directory.CreateDirectory(folder);
            JsonObject o = new() { ["v"] = Version, ["path"] = entry.Path, ["kind"] = entry.Kind, ["size"] = entry.Size, ["ticks"] = entry.Ticks, ["hash"] = entry.Hash, ["value"] = entry.Value.DeepClone() };
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, o.ToJsonString());
            File.Move(temp, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a summary that cannot be kept is only made again next time
        }
    }

    /// <summary>Once per run: removes the entries of project files that are not there any more (a file that was deleted or moved).</summary>
    private void PruneOnce()
    {
        if (!Directory.Exists(folder) || Interlocked.Exchange(ref pruned, 1) == 1)
        {
            return;
        }

        _ = Task.Run(Prune);
    }

    /// <summary>Removes the entries of project files that are not there any more.</summary>
    internal void Prune()
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(folder, "*.json").Take(5000))
            {
                string? path = null;
                try { path = (string?)JsonNode.Parse(File.ReadAllText(file))?["path"]; }
                catch (Exception ex) when (ex is IOException or JsonException) { /* an unreadable entry goes too */ }
                if (path is null || !File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the next run tries again
        }
    }

    /// <summary>Removes every kept summary (for a test, or when the folder is cleared by hand).</summary>
    public void Clear()
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as it is */ }
    }

    private sealed record Entry(string Path, string Kind, long Size, long Ticks, string Hash, JsonNode Value);
}
