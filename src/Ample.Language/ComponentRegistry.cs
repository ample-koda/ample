using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>A problem found while the components a song can see were put together.</summary>
/// <param name="Code">The diagnostic code (AMP22xx).</param>
/// <param name="Severity">The severity.</param>
/// <param name="Message">One plain sentence.</param>
/// <param name="Line">The 0-based line of the song or file it belongs to (the import or reexport line, or the declaration).</param>
/// <param name="File">The file it belongs to, or null for the song itself.</param>
public sealed record RegistryIssue(string Code, Severity Severity, string Message, int Line, string? File);

/// <summary>The components of one exported path: every version that is visible, and which one a reference without a version gets.</summary>
/// <param name="Path">The exported path.</param>
/// <param name="Id">The canonical id.</param>
/// <param name="Versions">The versions, highest first.</param>
/// <param name="Default">The default version (the current one, else the highest release).</param>
public sealed record ComponentFamily(string Path, string Id, IReadOnlyList<ComponentDefinition> Versions, ComponentDefinition Default);

/// <summary>The components that a song can see, with the files they come from and the problems of putting them together.</summary>
/// <param name="Visible">Every visible version of every component, each under the path the song writes (aliases and reexports applied).</param>
/// <param name="Sources">The files the visible components come from (not the song itself); their text is what an expansion adds.</param>
/// <param name="Issues">The problems.</param>
public sealed record ComponentRegistryResult(IReadOnlyList<ComponentDefinition> Visible, IReadOnlyList<ComponentSource> Sources, IReadOnlyList<RegistryIssue> Issues)
{
    /// <summary>Gets the visible components by exported path, as families of versions.</summary>
    public IReadOnlyList<ComponentFamily> Families => [.. Visible.GroupBy(v => v.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new ComponentFamily(g.Key, g.First().Id, [.. g.OrderByDescending(v => v.Version)], ComponentVersions.Default(g)!))];
}

/// <summary>
/// Puts the components of a song together: its own declarations and what its <c>import</c> lines bring (docs/language/19-components.md, section 5). Namespaces are paths and independent of files: several files can add to one
/// namespace; an import with an alias replaces the root of the exported paths (<c>import "a.amplec" as custom</c> turns <c>koda.fx.canonMaker</c> into <c>custom.fx.canonMaker</c>); a reexport offers a symbol or a namespace of an
/// import again under another path and keeps its canonical id. The same canonical id, version and payload from two places is one component; the same id and version with another payload is AMP2212; two canonical ids on one
/// path is AMP2213; more than one <c>current</c> version in a family is AMP2214; the last import never wins.
/// </summary>
public static class ComponentRegistry
{
    /// <summary>Reads a file that an import names: its text, or null when it cannot be found or read.</summary>
    /// <param name="target">The name as written in the import.</param>
    /// <returns>The text, or null.</returns>
    public delegate string? FileReader(string target);

    private sealed record Entry(string Path, ComponentDefinition Definition, Node Source);

    private sealed class Node(string target, string text, ComponentFile file)
    {
        public string Target { get; } = target;

        public string Text { get; } = text;

        public ComponentFile File { get; } = file;

        public List<(ComponentImport Import, Node? Child)> Imports { get; } = [];

        public List<Entry>? Visible { get; set; }

        public List<Entry>? Exports { get; set; }
    }

    private static string Remap(string path, string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return path;
        int dot = path.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? alias + "." + path : alias + path[dot..];
    }

    private static ComponentFile ReadText(string text, string origin)
    {
        string[] original = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] lines = original.Any(l => l.Contains("/*", StringComparison.Ordinal)) ? Comments.BlankBlocks(string.Join('\n', original), out _).Split('\n') : original;
        return ComponentReader.Read(lines, Comments.DocsOf(original), origin);
    }

    /// <summary>Puts together what a song can see.</summary>
    /// <param name="songText">The text of the song (or of the file whose imports are resolved).</param>
    /// <param name="songOrigin">How the song is named in messages.</param>
    /// <param name="read">Reads an imported file.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The visible components and the problems.</returns>
    public static ComponentRegistryResult Resolve(string songText, string songOrigin, FileReader read, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(spec);
        List<RegistryIssue> issues = [];
        Dictionary<string, Node> loaded = new(StringComparer.Ordinal);
        Node root = new(songOrigin, songText, ReadText(songText, songOrigin));

        Node? Load(string target, int line, string? from, List<string> chain)
        {
            if (chain.Contains(target, StringComparer.Ordinal))
            {
                issues.Add(new RegistryIssue("AMP2217", Severity.Error, $"The imports go in a circle: {string.Join(" -> ", chain.Append(target))}.", line, from));
                return null;
            }

            if (loaded.TryGetValue(target, out Node? known)) return known;
            string? text = read(target);
            if (text is null)
            {
                issues.Add(new RegistryIssue("AMP2216", Severity.Error, $"The file \"{target}\" of this import cannot be found or read.", line, from));
                return null;
            }

            Node node = new(target, text, ReadText(text, target));
            loaded[target] = node;
            foreach (ComponentIssue i in node.File.Issues) issues.Add(new RegistryIssue(i.Code, i.Severity, i.Message, i.Line, target));
            chain.Add(target);
            foreach (ComponentImport import in node.File.Imports) node.Imports.Add((import, Load(import.Target, import.Line, target, chain)));
            chain.RemoveAt(chain.Count - 1);
            return node;
        }

        foreach (ComponentImport import in root.File.Imports) root.Imports.Add((import, Load(import.Target, import.Line, null, [songOrigin])));

        List<Entry> VisibleOf(Node n)
        {
            if (n.Visible is not null) return n.Visible;
            n.Visible = [];
            foreach (ComponentDefinition d in n.File.Definitions) n.Visible.Add(new Entry(d.Name, d, n));
            foreach ((ComponentImport import, Node? child) in n.Imports)
            {
                if (child is null) continue;
                foreach (Entry e in ExportsOf(child)) n.Visible.Add(new Entry(Remap(e.Path, import.Alias), e.Definition, e.Source));
            }

            return n.Visible;
        }

        List<Entry> ExportsOf(Node n)
        {
            if (n.Exports is not null) return n.Exports;
            n.Exports = [.. n.File.Definitions.Select(d => new Entry(d.Name, d, n))];
            List<Entry> visible = VisibleOf(n);
            foreach (ComponentReexport r in n.File.Reexports)
            {
                if (r.Source.EndsWith(".*", StringComparison.Ordinal))
                {
                    string from = r.Source[..^1], to = r.Target.EndsWith(".*", StringComparison.Ordinal) ? r.Target[..^1] : r.Target + ".";
                    List<Entry> matches = [.. visible.Where(e => e.Path.StartsWith(from, StringComparison.Ordinal))];
                    if (matches.Count == 0) issues.Add(new RegistryIssue("AMP2217", Severity.Error, $"The reexport {r.Source} finds no component.", r.Line, n.Target == songOrigin ? null : n.Target));
                    foreach (Entry e in matches) n.Exports.Add(new Entry(to + e.Path[from.Length..], e.Definition, e.Source));
                    continue;
                }

                List<Entry> exact = [.. visible.Where(e => e.Path == r.Source)];
                if (exact.Count == 0) issues.Add(new RegistryIssue("AMP2217", Severity.Error, $"The reexport {r.Source} names a component that does not exist ({(visible.Count == 0 ? "nothing is imported" : "it sees: " + string.Join(", ", visible.Select(v => v.Path).Distinct().Order(StringComparer.Ordinal).Take(8)))}).", r.Line, n.Target == songOrigin ? null : n.Target));
                foreach (Entry e in exact) n.Exports.Add(new Entry(r.Target, e.Definition, e.Source));
            }

            return n.Exports;
        }

        // the files that were read also report their own reexport problems and conflicts once
        foreach (Node n in loaded.Values) _ = ExportsOf(n);
        List<Entry> entries = VisibleOf(root);

        // conflicts, per path
        Dictionary<Node, Dictionary<ComponentDefinition, IReadOnlyList<string>>> dependencies = [];
        IReadOnlyList<string> Deps(Entry e)
        {
            if (!dependencies.TryGetValue(e.Source, out Dictionary<ComponentDefinition, IReadOnlyList<string>>? byDef)) dependencies[e.Source] = byDef = [];
            if (byDef.TryGetValue(e.Definition, out IReadOnlyList<string>? known)) return known;
            if (e.Definition.IsAudio) return byDef[e.Definition] = ComponentHashing.DependenciesOf(e.Definition, spec, () => [], [.. entries.Select(x => x.Definition)]);
            AmpleDocument own = Parser.Parse(e.Source.Text, spec, new ParseOptions { RequireProjectLine = false });
            return byDef[e.Definition] = own.HelperTable.ClosureHashes(string.Join('\n', e.Definition.ProcessBody));
        }

        List<Entry> kept = [];
        foreach (IGrouping<string, Entry> path in entries.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            List<string> ids = [.. path.Select(e => e.Definition.Id).Distinct(StringComparer.Ordinal)];
            if (ids.Count > 1)
            {
                Entry second = path.First(e => e.Definition.Id != path.First().Definition.Id);
                issues.Add(new RegistryIssue("AMP2213", Severity.Error, $"The path {path.Key} is used by different components ({string.Join(", ", ids)}). Give one of them another path with import ... as, or another reexport.", second.Definition.HeaderLine, second.Source == root ? null : second.Source.Target));
                continue;
            }

            foreach (IGrouping<string, Entry> version in path.GroupBy(e => e.Definition.Version.ToString(), StringComparer.Ordinal))
            {
                Entry first = version.First();
                foreach (Entry other in version.Skip(1))
                {
                    if (ReferenceEquals(other.Definition, first.Definition) || other.Definition.Text == first.Definition.Text && ReferenceEquals(other.Source, first.Source)) continue;
                    string a = ComponentHashing.Payload(first.Definition, Deps(first)), b = ComponentHashing.Payload(other.Definition, Deps(other));
                    if (a != b) issues.Add(new RegistryIssue("AMP2212", Severity.Error, $"{path.Key} {version.Key} is declared twice with a different content ({ComponentHashing.Abbreviate(a)} in {first.Source.Target}, {ComponentHashing.Abbreviate(b)} in {other.Source.Target}). The same version must be the same component: change the version of one of them.", other.Definition.HeaderLine, other.Source == root ? null : other.Source.Target));
                }

                kept.Add(first);
            }

            if (kept.Where(e => e.Path == path.Key && e.Definition.Current).Select(e => e.Definition.Version.ToString()).Distinct(StringComparer.Ordinal).Count() > 1)
                issues.Add(new RegistryIssue("AMP2214", Severity.Error, $"More than one version of {path.Key} is marked current ({string.Join(", ", kept.Where(e => e.Path == path.Key && e.Definition.Current).Select(e => e.Definition.Version))}). At most one version is the default.", path.First().Definition.HeaderLine, null));
        }

        // the components a file calls are written with the names that file gives them (an alias): the process is rewritten to the declared names, so another file that uses the same alias for another component does not clash
        (Dictionary<string, string> Renames, Regex? Pattern) RenamesOf(Node n)
        {
            Dictionary<string, string> renames = new(StringComparer.Ordinal);
            foreach (Entry e in VisibleOf(n).Where(x => x.Source != n && !x.Definition.IsAudio && x.Path != x.Definition.DeclaredName)) renames[e.Path] = e.Definition.DeclaredName;
            Regex? names = renames.Count == 0 ? null : new(@"(?<![\w.\-])(" + string.Join("|", renames.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?=\s*\()", RegexOptions.None, TimeSpan.FromSeconds(1));
            return (renames, names);
        }

        ComponentDefinition Scoped(ComponentDefinition d, Node n)
        {
            if (d.IsAudio || n == root) return d;
            (Dictionary<string, string> renames, Regex? names) = RenamesOf(n);
            if (names is null || !d.ProcessBody.Any(l => renames.Keys.Any(k => l.Contains(k, StringComparison.Ordinal)))) return d;
            return d.WithProcess([.. d.ProcessBody.Select(l => ComponentNesting.ReplaceInCode(l, names, v => renames[v]))]);
        }

        // the text that an expansion adds is the file with those calls written the same way
        string TextOf(Node n)
        {
            (Dictionary<string, string> renames, Regex? names) = RenamesOf(n);
            return names is null ? n.Text : ComponentNesting.ReplaceInCode(n.Text, names, v => renames[v]);
        }

        // the definitions the song sees carry the path it writes; the files they come from are what an expansion adds
        List<ComponentDefinition> visible = [];
        Dictionary<Node, List<ComponentDefinition>> perFile = [];
        foreach (Entry e in kept)
        {
            ComponentDefinition seen = Scoped(e.Definition.Name == e.Path ? e.Definition : e.Definition.Renamed(e.Path), e.Source);
            visible.Add(seen);
            if (e.Source != root)
            {
                if (!perFile.TryGetValue(e.Source, out List<ComponentDefinition>? list)) perFile[e.Source] = list = [];
                list.Add(seen);
            }
        }

        // a component file can import files of its own, for the components that its components call: those travel along with it, under the names that file writes, without the song seeing them
        Queue<Node> pending = new(perFile.Keys);
        HashSet<Node> walked = [];
        while (pending.Count > 0)
        {
            Node file = pending.Dequeue();
            if (!walked.Add(file)) continue;
            foreach (Entry e in VisibleOf(file).Where(x => x.Source != file && x.Source != root))
            {
                ComponentDefinition seen = Scoped(e.Definition.Name == e.Path ? e.Definition : e.Definition.Renamed(e.Path), e.Source);
                if (!perFile.TryGetValue(e.Source, out List<ComponentDefinition>? list)) perFile[e.Source] = list = [];
                if (!list.Any(d => d.Name == seen.Name && d.Id == seen.Id && d.Version == seen.Version)) list.Add(seen);
                pending.Enqueue(e.Source);
            }
        }

        List<ComponentSource> sources = [.. perFile.Select(p => new ComponentSource(p.Key.Target, TextOf(p.Key), p.Value))];
        // the song's own components are not external, but the song is also a source for the lock and the lists: its definitions are already in visible
        return new ComponentRegistryResult([.. visible.Where(v => root.File.Definitions.All(d => !ReferenceEquals(d, v)))], sources, issues);
    }

    /// <summary>Whether a text is the kind of file that can hold components (the name ends with <c>.amplec</c>).</summary>
    /// <param name="name">The file name.</param>
    /// <returns>True for a component file.</returns>
    public static bool IsComponentFile(string name) => name.EndsWith(".amplec", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(name, @"\.amplec$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}
