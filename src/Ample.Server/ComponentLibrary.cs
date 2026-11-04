using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Server;

/// <summary>What the library of a project knows about one component of a <c>.amplec</c> file (the list the Library tab shows).</summary>
/// <param name="Name">The name the component is exported with.</param>
/// <param name="Id">The canonical id.</param>
/// <param name="Title">The friendly name (<c>DisplayName</c>).</param>
/// <param name="Kind">Notes, Audio or Mixed.</param>
/// <param name="Description">The description (<c>Description</c> or the doc comment), or empty.</param>
/// <param name="Tags">The tags (<c>#[Tags(...)]</c>).</param>
/// <param name="Version">The default version.</param>
/// <param name="Versions">All versions.</param>
/// <param name="Seal">The seal state of the default version.</param>
/// <param name="Processors">The processors of an Audio component.</param>

internal sealed record LibraryComponent(string Name, string Id, string Title, string Kind, string Description, IReadOnlyList<string> Tags, string Version, IReadOnlyList<string> Versions, string Seal, IReadOnlyList<string> Processors);

/// <summary>The components of every <c>.amplec</c> file of a project, for the Library tab: found by reading the files (not by what a song imports), cached per file, and read again only when a file or one of the files it imports changes (path, modification time and size).</summary>
internal static partial class ComponentLibrary
{
    private sealed record Cached(string Stamp, IReadOnlyList<LibraryComponent> Components);

    private const int MaxCached = 2000;

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*import\s+""([^""]+)""", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImportLine();

    /// <summary>The .amplec files under a folder, in order, without the folders that start with a dot (.ample, .git, ...): a project can be large and those never hold components.</summary>
    /// <param name="root">The folder.</param>
    /// <returns>The full paths.</returns>
    internal static IEnumerable<string> ComponentFiles(string root)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return new System.IO.Enumeration.FileSystemEnumerable<string>(root, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(ModuleService.ComponentExtension, StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.FileName.StartsWith('.'),
        }.Order(StringComparer.Ordinal);
    }

    private static string StampOf(string full)
    {
        FileInfo f = new(full);
        return f.Exists ? $"{f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{f.Length.ToString(CultureInfo.InvariantCulture)}" : "missing";
    }

    /// <summary>Gets the stamp of a file and of everything it imports (one level at a time, with a limit), so that a change in an imported file makes this entry stale too.</summary>
    private static string DependencyStamp(string root, string relative)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> todo = new([relative]);
        List<string> parts = [];
        while (todo.Count > 0 && seen.Count < 64)
        {
            string current = todo.Pop();
            if (!seen.Add(current)) continue;
            string full = Path.GetFullPath(Path.Combine(root, current));
            parts.Add(current + "=" + StampOf(full));
            if (!File.Exists(full)) continue;
            string text;
            try { text = File.ReadAllText(full); }
            catch (IOException) { continue; }
            foreach (Match m in ImportLine().Matches(text)) todo.Push(m.Groups[1].Value);
        }

        return string.Join('|', parts.Order(StringComparer.Ordinal));
    }

    /// <summary>An alias for an import whose names are taken by other code: the name of the file in camel case (triple-notes.amplec: tripleNotes), made unique among the first segments the song already uses.</summary>
    /// <param name="file">The path of the .amplec file.</param>
    /// <param name="taken">The first segments of the names the song can see.</param>
    /// <returns>The alias.</returns>
    public static string SuggestAlias(string file, IReadOnlySet<string> taken)
    {
        string[] words = Path.GetFileNameWithoutExtension(file).Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        string stem = words.Length == 0 ? "lib" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        if (!char.IsLetter(stem[0])) stem = "lib" + stem;
        string alias = stem;
        for (int n = 2; taken.Contains(alias); n++) alias = stem + n.ToString(CultureInfo.InvariantCulture);
        return alias;
    }

    /// <summary>The library of a project: the files with their components, which of them the song already imports, and how many files were read now and how many came from the cache.</summary>
    /// <param name="s">The project.</param>
    /// <param name="songText">The text of the song the "imported" marks are for (empty: none).</param>
    /// <param name="refresh">True to read every file again.</param>
    /// <returns>The files, the entries and the cache figures.</returns>
    public static object Build(ProjectSession s, string songText, bool refresh) => Build(new ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")), s.Root, s.Spec, songText, refresh);

    /// <summary>The same for any folder of components (the project, or the working copy of an assistant task).</summary>
    /// <param name="service">The module service of that folder.</param>
    /// <param name="root">The folder.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="songText">The text of the song the "imported" marks are for (empty: none).</param>
    /// <param name="refresh">True to read every file again.</param>
    /// <returns>The files, the entries and the cache figures.</returns>
    public static object Build(ModuleService service, string root, SpecData spec, string songText, bool refresh)
    {
        IReadOnlyList<ComponentListing> visible = songText.Length == 0 ? [] : ComponentCatalog.List(service, songText, spec).Components;
        HashSet<string> imported = [.. visible.Select(c => c.Id)];
        Dictionary<string, string> visibleNames = visible.ToDictionary(c => c.Path, c => c.Id, StringComparer.Ordinal);
        HashSet<string> firstSegments = [.. visible.Select(c => c.Path.Split('.')[0])];
        List<object> files = [];
        int read = 0, reused = 0;
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + "|";
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (string f in ComponentFiles(root))
        {
            string relative = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (relative.StartsWith(".ample/", StringComparison.Ordinal)) continue;
            string key = prefix + relative;
            present.Add(key);
            string stamp = DependencyStamp(root, relative);
            if (!refresh && Cache.TryGetValue(key, out Cached? known) && known.Stamp == stamp) reused++;
            else
            {
                read++;
                (IReadOnlyList<ComponentListing> listed, _) = ComponentCatalog.List(service, $"import \"{relative}\"\n", spec);
                Cache[key] = known = new Cached(stamp, [.. listed.Select(c => new LibraryComponent(c.Path, c.Id, c.Title, c.Kind, c.Description, c.Tags, c.DefaultVersion, [.. c.Versions.Select(v => v.Version)], c.Versions.FirstOrDefault(v => v.Version == c.DefaultVersion)?.Seal ?? "unsealed", c.Processors))]);
            }

            files.Add(new { file = relative, name = Path.GetFileName(f), components = known.Components.Select(c => new { c.Name, c.Id, c.Title, c.Kind, c.Description, c.Tags, c.Version, c.Versions, c.Seal, c.Processors, imported = imported.Contains(c.Id), nameTaken = !imported.Contains(c.Id) && visibleNames.ContainsKey(c.Name), suggestedAlias = !imported.Contains(c.Id) && visibleNames.ContainsKey(c.Name) ? SuggestAlias(relative, firstSegments) : null }) });
        }

        // forget the files that are gone, and keep the cache small (the working copies of assistant tasks come and go)
        foreach (string gone in Cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !present.Contains(k)).ToList()) Cache.TryRemove(gone, out _);
        if (Cache.Count > MaxCached) foreach (string key in Cache.Keys.Take(Cache.Count - MaxCached).ToList()) Cache.TryRemove(key, out _);
        return new { files, scanned = read, cached = reused, at = DateTimeOffset.UtcNow };
    }
}
