using System.Collections.Concurrent;
using Ample.Engine.Modules;
using Ample.Storage.BlackBox;
using Ample.Audio.IO;

namespace Ample.Server;

/// <summary>
/// The working copies of the text files of a project that are not songs (component files, lyrics, sequences, plain text), kept on the server like the working copy of a song
/// (plan/editor-tabs.md, part 1). A working copy that differs from its file is "unsaved": a build, check or play of a song that reaches the file reads the working copy, not the disk.
/// </summary>
internal sealed class WorkingTexts
{
    internal sealed class Entry(string saved)
    {
        internal string Text { get; set; } = saved;
        internal string Saved { get; set; } = saved;
        internal long Version { get; set; }
        internal bool Dirty => !string.Equals(Text, Saved, StringComparison.Ordinal);
    }

    private readonly ProjectSession session;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    internal WorkingTexts(ProjectSession session)
    {
        this.session = session;
        // the module and component readers of this project look here first
        ModuleService.WorkingTextReaders[Path.GetFullPath(session.Root)] = Read;
    }

    private static string Key(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>The unsaved text of a file, or null when it has none (the file on disk is the truth then).</summary>
    internal string? Read(string path) => entries.TryGetValue(Key(path), out Entry? entry) && entry.Dirty ? entry.Text : null;

    /// <summary>The paths of the files with unsaved changes.</summary>
    internal IReadOnlyList<string> Dirty() => [.. entries.Where(e => e.Value.Dirty).Select(e => e.Key).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The unsaved files with the SHA-256 of the text that a build would use (for the plan of a build and its log).</summary>
    internal IReadOnlyList<(string Path, string Sha256)> DirtyWithHashes() => [.. entries.Where(e => e.Value.Dirty).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => (e.Key, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.Value.Text)))))];

    private Entry Open(string path)
    {
        string key = Key(path);
        return entries.GetOrAdd(key, _ =>
        {
            string full = ProjectPath.Resolve(session.Root, key);
            return new Entry(File.Exists(full) ? File.ReadAllText(full) : string.Empty);
        });
    }

    /// <summary>Sets the working text of a file (opening it from the disk first).</summary>
    internal (long Version, bool Dirty, string Saved) Set(string path, string text)
    {
        Entry entry = Open(path);
        lock (entry)
        {
            entry.Text = text;
            entry.Version++;
            return (entry.Version, entry.Dirty, entry.Saved);
        }
    }

    /// <summary>Gives the working text (the saved one when there is no unsaved change) with its version.</summary>
    internal (string Text, string Saved, long Version, bool Dirty) Get(string path)
    {
        Entry entry = Open(path);
        lock (entry) return (entry.Text, entry.Saved, entry.Version, entry.Dirty);
    }

    /// <summary>Writes the working text to the file through the command bus and makes it the saved text.</summary>
    internal async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        Entry entry = Open(path);
        string text;
        lock (entry) text = entry.Text;
        await session.Bus.ExecuteAsync(new WriteFileCommand(Key(path), text), cancellationToken).ConfigureAwait(false);
        lock (entry) { entry.Saved = text; entry.Version++; }
    }

    /// <summary>Throws the unsaved changes away: the working text is the file's text again.</summary>
    internal (string Text, long Version) Revert(string path)
    {
        Entry entry = Open(path);
        string full = ProjectPath.Resolve(session.Root, Key(path));
        lock (entry)
        {
            entry.Saved = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
            entry.Text = entry.Saved;
            entry.Version++;
            return (entry.Text, entry.Version);
        }
    }

    /// <summary>Forgets a file (it was closed without changes, or deleted).</summary>
    internal void Forget(string path) => entries.TryRemove(Key(path), out _);
}
