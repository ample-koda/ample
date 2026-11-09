using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>What an embed or an extract would do, said before anything is written.</summary>
/// <param name="Components">The components that are written into (or taken out of) the song, as <c>name version</c>.</param>
/// <param name="AlreadyThere">The components of the closure that are embedded in the song already (exactly the same payload), so nothing is written for them.</param>
/// <param name="Differs">The components of the closure that exist in the song with another payload (the embed is refused for them).</param>
public sealed record EmbeddingPreview(IReadOnlyList<string> Components, IReadOnlyList<string> AlreadyThere, IReadOnlyList<string> Differs);

/// <summary>The result of extracting a component out of a song.</summary>
/// <param name="Ok">Whether it was made.</param>
/// <param name="SongText">The new text of the song.</param>
/// <param name="FileText">The text of the new <c>.amplec</c> file (empty when it was not made).</param>
/// <param name="Message">One plain sentence.</param>
public sealed record ComponentExtraction(bool Ok, string SongText, string FileText, string Message);

/// <summary>The result of adding an import (and optionally an instance) to a song.</summary>
/// <param name="Ok">Whether it can be done.</param>
/// <param name="SongText">The song text after it (unchanged when it cannot be done).</param>
/// <param name="Message">One plain sentence.</param>
/// <param name="Added">The lines that are added, for the preview.</param>
/// <param name="Block">The name of the instance that was added, or null.</param>
public sealed record ComponentImportAdd(bool Ok, string SongText, string Message, IReadOnlyList<string> Added, string? Block);

/// <summary>Embedding components in a song, using the template source again, extracting them to a file and detaching a sealed one for editing (docs/language/19-components.md, "Embedding and extraction"). They change text only; the host writes it through the command path.</summary>
public static class ComponentEmbedding
{
    private static ComponentEditResult No(string text, string message) => new(false, text, message);

    private static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    private static bool SamePayload(ComponentDefinition a, ComponentDefinition b) => a.Seal is { } x && b.Seal is { } y && x.Payload.Length > 0 && x.Payload == y.Payload;

    private static string Label(ComponentDefinition d) => $"{d.DeclaredName} {d.Version}";

    /// <summary>Gets the payload hash of a module: the payload hashes of its sealed components, in order of id and version.</summary>
    /// <param name="definitions">The components of the module.</param>
    /// <returns>The hash, or an empty text when a component is not sealed (an unsealed module has no payload).</returns>
    public static string ModulePayload(IEnumerable<ComponentDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        List<ComponentDefinition> all = [.. definitions];
        if (all.Count == 0 || all.Any(d => d.Seal is null || d.Seal.Payload.Length == 0)) return string.Empty;
        string text = string.Join('\n', all.OrderBy(d => d.Id, StringComparer.Ordinal).ThenBy(d => d.Version).Select(d => $"{d.Id}@{d.Version}:{d.Seal!.Payload}"));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Calculates the transitive closure of the components some instances need: the components themselves and every component of the same sources whose name is written in their process. Each definition is listed once, however many instances use it.</summary>
    /// <param name="available">Everything that can be embedded (the imported components).</param>
    /// <param name="roots">The components the instances use (the resolved versions).</param>
    /// <returns>The closure, in a stable order.</returns>
    public static IReadOnlyList<ComponentDefinition> Closure(IReadOnlyList<ComponentDefinition> available, IEnumerable<ComponentDefinition> roots)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(roots);
        List<ComponentDefinition> result = [];
        Queue<ComponentDefinition> queue = new(roots);
        while (queue.Count > 0)
        {
            ComponentDefinition d = queue.Dequeue();
            if (result.Any(r => r.Id == d.Id && r.Version.SameAs(d.Version))) continue;
            result.Add(d);
            string body = string.Join('\n', d.ProcessBody);
            foreach (ComponentDefinition other in available)
            {
                if (other.Id == d.Id || !body.Contains(other.DeclaredName, StringComparison.Ordinal)) continue;
                ComponentDefinition? pick = ComponentVersions.Default(available.Where(a => a.Id == other.Id));
                if (pick is not null) queue.Enqueue(pick);
            }
        }

        return [.. result.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Version)];
    }

    /// <summary>Says what embedding would do.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="closure">The components to embed.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The preview.</returns>
    public static EmbeddingPreview Preview(string songText, IReadOnlyList<ComponentDefinition> closure, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(spec);
        AmpleDocument song = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        List<string> add = [], there = [], differs = [];
        foreach (ComponentDefinition d in closure)
        {
            ComponentDefinition? same = song.Components.FirstOrDefault(c => c.Id == d.Id && c.Version.SameAs(d.Version));
            if (same is null) add.Add(Label(d));
            else if (SamePayload(same, d)) there.Add(Label(d));
            else differs.Add(Label(d));
        }

        return new EmbeddingPreview(add, there, differs);
    }

    /// <summary>Writes components into the song, inside an <c>embedded module</c> that keeps their module boundary. A component that is in the song already with exactly the same payload is not written twice; one with another payload stops the embed.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="closure">The components to embed (see <see cref="Closure"/>).</param>
    /// <param name="moduleName">The name of the module (an existing embedded module of that name gets the components).</param>
    /// <param name="moduleId">The canonical id of the module, or null.</param>
    /// <param name="moduleVersion">The version of the module, or null.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult Embed(string songText, IReadOnlyList<ComponentDefinition> closure, string moduleName, string? moduleId, string? moduleVersion, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(spec);
        if (!Regex.IsMatch(moduleName ?? string.Empty, @"^[A-Za-z_][\w.-]*$", RegexOptions.None, TimeSpan.FromSeconds(1))) return No(songText, "Name the module with letters, digits, dots, hyphens and underscores.");
        EmbeddingPreview preview = Preview(songText, closure, spec);
        if (preview.Differs.Count > 0) return No(songText, $"The song has {string.Join(", ", preview.Differs)} with other code already: nothing is embedded (use the template source, or give the embedded one a new version).");
        List<ComponentDefinition> write = [.. closure.Where(d => preview.Components.Contains(Label(d)))];
        if (write.Count == 0) return No(songText, "Everything is embedded already.");
        List<string> lines = Lines(songText);
        List<string> body = [];
        foreach (ComponentDefinition d in write)
        {
            if (body.Count > 0) body.Add(string.Empty);
            body.AddRange(Lines(d.Text).Select(l => l.Length == 0 ? l : "  " + l));
        }

        AmpleDocument song = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        EmbeddedModule? existing = song.EmbeddedModules.FirstOrDefault(m => m.Name == moduleName);
        if (existing is not null)
        {
            // components are added before the closing brace; an old module seal no longer matches, so it is dropped (seal the module again)
            lines.InsertRange(existing.End, ["", .. body]);
        }
        else
        {
            List<string> block = ["", $"embedded module {moduleName} {{"];
            if (!string.IsNullOrWhiteSpace(moduleId)) block.Add($"  id \"{moduleId}\"");
            if (!string.IsNullOrWhiteSpace(moduleVersion)) block.Add($"  version \"{moduleVersion}\"");
            if (block.Count > 2) block.Add(string.Empty);
            block.AddRange(body);
            block.Add("}");
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(block);
            lines.Add(string.Empty);
        }

        return new ComponentEditResult(true, string.Join('\n', lines), $"Embedded {string.Join(", ", write.Select(Label))} in the module {moduleName}.");
    }

    /// <summary>Uses the template source again: removes an embedded component from the song when a template has exactly the same id, version and payload (nothing changes in what the song builds). Another payload is never switched silently.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="template">The template component (from a <c>.amplec</c> the song imports).</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult UseTemplate(string songText, ComponentDefinition template, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(spec);
        AmpleDocument song = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        ComponentDefinition? embedded = song.Components.FirstOrDefault(c => c.Id == template.Id && c.Version.SameAs(template.Version));
        if (embedded is null) return No(songText, $"{Label(template)} is not embedded in this song.");
        if (!SamePayload(embedded, template)) return No(songText, $"The embedded {Label(embedded)} and the template are not the same code (the payload hashes differ, or one of them is not sealed): they are not switched. Make a new version of one of them.");
        EmbeddedModule? module = song.EmbeddedModules.FirstOrDefault(m => embedded.StartLine >= m.Start && embedded.EndLine <= m.End);
        List<string> lines = Lines(songText);
        int from = embedded.StartLine, to = embedded.EndLine;
        // the blank line in front of the component goes with it
        if (from > 0 && lines[from - 1].Trim().Length == 0) from--;
        lines.RemoveRange(from, to - from + 1);
        string note = string.Empty;
        if (module is not null && module.Components.Count <= 1)
        {
            AmpleDocument after = Parser.Parse(string.Join('\n', lines), spec, new ParseOptions { RequireProjectLine = false });
            EmbeddedModule? left = after.EmbeddedModules.FirstOrDefault(m => m.Name == module.Name);
            if (left is not null && left.Components.Count == 0)
            {
                lines.RemoveRange(left.Start, left.End - left.Start + 1);
                note = $" The module {module.Name} was empty and is gone.";
            }

        }

        return new ComponentEditResult(true, string.Join('\n', lines), $"{Label(template)} comes from the template again; the build does not change." + note);
    }

    /// <summary>Extracts an embedded component to a <c>.amplec</c> file: the file gets the declaration exactly as it is (seal and identity are kept), the song loses it and imports the file.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="name">The component (as declared).</param>
    /// <param name="version">The version, or null when the component has one.</param>
    /// <param name="importPath">The path the song imports the file by (<c>components/fx.amplec</c>).</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The new text of the song and of the file.</returns>
    public static ComponentExtraction Extract(string songText, string name, string? version, string importPath, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(importPath)) return new ComponentExtraction(false, songText, string.Empty, "Name the file to extract to.");
        AmpleDocument song = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        List<ComponentDefinition> found = [.. song.Components.Where(c => c.DeclaredName == name && (version is null || (SemVer.TryParse(version, out SemVer v) && c.Version.SameAs(v))))];
        if (found.Count == 0) return new ComponentExtraction(false, songText, string.Empty, $"There is no component \"{name}\"{(version is null ? string.Empty : " " + version)} in this song.");
        if (found.Count > 1) return new ComponentExtraction(false, songText, string.Empty, $"\"{name}\" has several versions ({string.Join(", ", found.Select(f => f.Version))}): say which one.");
        ComponentDefinition d = found[0];
        string fileText = string.Join('\n', Lines(d.Text).Select(l => l.StartsWith("  ", StringComparison.Ordinal) && song.EmbeddedModules.Any(m => d.StartLine >= m.Start && d.EndLine <= m.End) ? l[2..] : l)) + "\n";
        List<string> lines = Lines(songText);
        int from = d.StartLine, to = d.EndLine;
        if (from > 0 && lines[from - 1].Trim().Length == 0) from--;
        lines.RemoveRange(from, to - from + 1);
        string import = $"import \"{importPath.Trim()}\"";
        if (!lines.Any(l => l.Trim() == import))
        {
            int last = song.ComponentImports.Count > 0 ? song.ComponentImports.Max(i => i.Line) : -1;
            int project = lines.FindIndex(l => l.TrimStart().StartsWith("project ", StringComparison.Ordinal) || l.TrimStart().StartsWith("composition ", StringComparison.Ordinal));
            int at = last >= 0 ? last + 1 : project >= 0 ? EndOfBlock(lines, project) + 1 : 0;
            lines.InsertRange(Math.Min(at, lines.Count), last >= 0 ? [import] : ["", import]);
        }

        return new ComponentExtraction(true, string.Join('\n', lines), fileText, $"Extracted {Label(d)} to {importPath.Trim()}; the song imports it. Identity, version and seal are kept.");
    }

    /// <summary>Adds the import of a component file to a song, and with <paramref name="addBlock"/> an instance of the component at the end of the song. Nothing else changes; an import that is already there is not written again.</summary>
    /// <param name="songText">The song.</param>
    /// <param name="importPath">The path of the .amplec file as the song writes it (relative to the project).</param>
    /// <param name="componentName">The exported name of the component (for the instance), or null for the import only.</param>
    /// <param name="addBlock">True to add an instance.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="alias">An alias for the import (<c>import "file" as alias</c>): the first segment of the exported names is replaced by it, which is how two components with the same name are told apart; or null.</param>
    /// <param name="builtIn">True for a component that ships with AMPLE: no import line is written, only the instance.</param>
    /// <returns>The new text and what was added.</returns>
    public static ComponentImportAdd AddImport(string songText, string importPath, string? componentName, bool addBlock, SpecData spec, string? alias = null, bool builtIn = false)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(importPath);
        ArgumentNullException.ThrowIfNull(spec);
        AmpleDocument song = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        List<string> lines = Lines(songText);
        List<string> added = [];
        string import = $"import \"{importPath.Trim()}\"" + (string.IsNullOrWhiteSpace(alias) ? string.Empty : $" as {alias.Trim()}");
        if (!builtIn && !lines.Any(l => l.Trim() == import))   // a built-in component needs no import line: its name is known to every song
        {
            int last = song.ComponentImports.Count > 0 ? song.ComponentImports.Max(i => i.Line) : -1;
            int project = lines.FindIndex(l => l.TrimStart().StartsWith("project ", StringComparison.Ordinal) || l.TrimStart().StartsWith("composition ", StringComparison.Ordinal));
            int at = last >= 0 ? last + 1 : project >= 0 ? EndOfBlock(lines, project) + 1 : 0;
            lines.InsertRange(Math.Min(at, lines.Count), last >= 0 ? [import] : ["", import]);
            added.Add(import);
        }

        // the name an instance is written with: through an alias the first segment of the exported name is the alias
        if (!string.IsNullOrWhiteSpace(alias) && componentName is not null)
        {
            int dot = componentName.IndexOf('.', StringComparison.Ordinal);
            componentName = dot < 0 ? alias.Trim() + "." + componentName : alias.Trim() + componentName[dot..];
        }

        string? block = null;
        if (addBlock && !string.IsNullOrWhiteSpace(componentName))
        {
            string tail = componentName[(componentName.LastIndexOf('.') + 1)..];
            string stem = char.ToLowerInvariant(tail[0]) + tail[1..];
            HashSet<string> taken = [.. song.Blocks.Select(b => b.Name)];
            block = stem;
            for (int n = 2; taken.Contains(block); n++) block = stem + n.ToString(CultureInfo.InvariantCulture);
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(["", $"{componentName} {block} {{", "}", string.Empty]);
            added.Add($"{componentName} {block} {{");
            added.Add("}");
        }

        if (added.Count == 0) return new ComponentImportAdd(true, songText, builtIn ? "This component is built in: nothing needs to be imported." : "The song imports this file already.", [], null);
        return new ComponentImportAdd(true, string.Join('\n', lines), builtIn ? $"Added the block {block}." : block is null ? "Added the import." : $"Added the import and the block {block}.", added, block);
    }

    private static int EndOfBlock(List<string> lines, int start)
    {
        int depth = 0;
        for (int i = start; i < lines.Count; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }

            if (depth <= 0 && lines[i].Contains('}', StringComparison.Ordinal)) return i;
            if (depth <= 0 && lines[i].Contains('{', StringComparison.Ordinal)) return i;
        }

        return start;
    }

    /// <summary>Detaches a sealed embedded component for editing: its seal is removed, so it is a development version again that still builds; reuse of sealed results is off until it is sealed again or becomes a new version.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="name">The component.</param>
    /// <param name="version">The version, or null when the component has one.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The edit.</returns>
    public static ComponentEditResult Detach(string songText, string name, string? version, SpecData spec)
    {
        ComponentEditResult result = ComponentEdits.Unseal(songText, name, version, spec);
        return result.Ok ? result with { Message = result.Message + " It is detached for editing: seal it again, or make a new version, when it is ready." } : result;
    }
}
