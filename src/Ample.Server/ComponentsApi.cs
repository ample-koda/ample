using System.Text.Json;
using Ample.Audio.IO;
using Ample.Engine.Modules;
using Ample.Storage.BlackBox;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server;

/// <summary>The components a song can see (the palette and the property panel read this), and the edits of their text: seal, unseal and a new version (docs/language/19-components.md).</summary>
internal static class ComponentsApi
{
    private static ModuleService Service(ProjectSession s) => new(s.Root, () => s.Library, s.Manifest.Id.ToString("D"));

    /// <summary>The components of a song with every version and its status, the problems of putting them together, and the files that can be imported.</summary>
    public static async Task<object> ListAsync(ProjectSession s, string path, CancellationToken ct)
    {
        DocSession doc = await s.DocAsync(path, ct).ConfigureAwait(false);
        ModuleService service = Service(s);
        (IReadOnlyList<ComponentListing> components, IReadOnlyList<Diagnostic> issues) = ComponentCatalog.List(service, doc.Text, s.Spec);
        return new
        {
            path,
            components = components.Select(c => new
            {
                c.Path,
                c.Id,
                c.Title,
                c.Category,
                c.Description,
                c.Experimental,
                c.Kind,
                c.ClientPreview,
                c.NoPreview,
                c.LatencyMs,
                c.Processors,
                c.Tags,
                ui = c.Ui,
                c.LivePreview,
                c.NoLive,
                c.OutputNodes,
                c.DefaultVersion,
                inputs = c.Inputs,
                properties = c.Properties,
                outputs = c.Outputs,
                versions = c.Versions.Select(v => new { v.Version, v.Current, v.Prerelease, v.Origin, v.Seal, v.SealReason, v.SignedBy, v.SignatureValid, v.Semantic, v.Payload, v.Deprecated, v.Replacement, v.Migrations, v.Line }),
            }),
            issues = issues.Select(i => new { i.Code, severity = i.Severity.ToString().ToLowerInvariant(), i.Message, line = i.Line }),
            files = Files(s),
        };
    }

    /// <summary>The internal graph of an audio instance: its processors with their parameters and links, the blocks they become (provenance), the latency and what the browser can preview. Nothing is rendered.</summary>
    public static async Task<object> GraphAsync(ProjectSession s, string path, string block, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        string text = (await s.DocAsync(path, ct).ConfigureAwait(false)).Text;
        (ComponentDefinition def, AudioGraph graph) = ComponentCatalog.GraphOf(Service(s), text, s.Spec, block, overrides);
        return new
        {
            block,
            component = def.Name,
            id = def.Id,
            version = def.Version.ToString(),
            nodes = graph.Nodes.Select(n => new
            {
                n.Id,
                n.Processor,
                n.Template,
                version = n.Version,
                block = block + "__" + n.Id,
                parameters = n.Parameters.Select(p => new { name = p.Name, value = p.Value }),
                inputs = n.Inputs.Select(i => new { port = i.Port, source = i.Source }),
                output = n.OutputPort,
                line = n.Line + 1,
            }),
            outputs = graph.Outputs,
            latencyMs = graph.LatencyMs,
            clientPreview = graph.ClientPreview,
            livePreview = graph.LivePreview,
            liveSource = graph.LiveSource,
            noLive = graph.NoLive,
            noPreview = graph.NoPreview,
            issues = graph.Issues.Select(i => new { i.Code, severity = i.Severity.ToString().ToLowerInvariant(), i.Message, line = i.Line + 1 }),
        };
    }

    /// <summary>The <c>.amplec</c> files of the project (the ones an import can name).</summary>
    /// <summary>The suggestions at a place in the text of a component file (attributes after #[, statements at the start of a line, types after a colon, the words of the ui block, processors after |>): the same completion the song editor gets from the language server, for a file that has no session of its own.</summary>
    /// <param name="s">The project.</param>
    /// <param name="text">The text of the editor.</param>
    /// <param name="line">The 0-based line of the caret.</param>
    /// <param name="character">The 0-based column of the caret.</param>
    /// <returns>The items.</returns>
    public static object Complete(ProjectSession s, string text, int line, int character) => Complete(s.Spec, DiskReader(s), text, line, character);

    /// <summary>The suggestions at a place in the text of a component file, with the files of the project given by a reader (the project of the session, or the files of a Lite project).</summary>
    /// <param name="spec">The language data.</param>
    /// <param name="read">Reads a project file by its relative path, or gives null.</param>
    /// <param name="text">The text of the editor.</param>
    /// <param name="line">The 0-based line of the caret.</param>
    /// <param name="character">The 0-based column of the caret.</param>
    /// <returns>The items.</returns>
    public static object Complete(SpecData spec, Func<string, string?> read, string text, int line, int character)
    {
        // an attribute that is still being typed (#[ or #[Wid) is not closed, so the component around it does not read yet: its names are the same wherever it is
        string[] rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string before = line >= 0 && line < rows.Length ? rows[line][..Math.Min(character, rows[line].Length)] : string.Empty;
        int open = before.LastIndexOf("#[", StringComparison.Ordinal);
        if (open >= 0 && before.IndexOf(']', open) < 0 && before[..open].Trim().Length == 0)
        {
            string inside = before[(open + 2)..];
            if (inside.Contains('(', StringComparison.Ordinal))
            {
                string attribute = inside[..inside.IndexOf('(', StringComparison.Ordinal)].Split(',')[^1].Trim();
                return new { items = attribute == "Widget" ? ComponentUiCatalog.Widgets.Select(w => new { label = w.Key, detail = w.Value, kind = "widget" }).ToArray() : [] };
            }

            return new { items = ComponentAttributes.All.Values.Select(a => new { label = a.Name, detail = a.Doc, kind = "attribute" }).ToArray() };
        }

        AmpleDocument doc = Parser.Parse(text, spec, new ParseOptions { RequireProjectLine = false, ReadFile = read });
        return new { items = Queries.CompletionAt(doc, spec, line + 1, character + 1).Select(c => new { label = c.Label, detail = c.Detail, kind = c.Kind, insert = c.Insert }) };
    }

    private static Func<string, string?> DiskReader(ProjectSession s) => target => { string full = Path.GetFullPath(Path.Combine(s.Root, target)); return full.StartsWith(Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? File.ReadAllText(full) : null; };

    /// <summary>The problems of the text of a component file while it is typed (nothing is read from or written to disk): the diagnostics of the parser in the shape of the language server (0-based ranges), so the editor shows them like those of a song.</summary>
    public static object CheckText(ProjectSession s, string text) => CheckText(s.Spec, DiskReader(s), text);

    /// <summary>The problems of the text of a component file, with the files of the project given by a reader.</summary>
    /// <param name="spec">The language data.</param>
    /// <param name="read">Reads a project file by its relative path, or gives null.</param>
    /// <param name="text">The text.</param>
    /// <returns>The diagnostics.</returns>
    public static object CheckText(SpecData spec, Func<string, string?> read, string text)
    {
        AmpleDocument doc = Parser.Parse(text, spec, new ParseOptions { RequireProjectLine = false, ReadFile = read });
        return new
        {
            diagnostics = doc.Diagnostics.Select(d => new
            {
                range = new { start = new { line = Math.Max(0, d.Line - 1), character = Math.Max(0, d.Col - 1) }, end = new { line = Math.Max(0, d.Line - 1), character = Math.Max(1, d.EndCol - 1) } },
                severity = d.Severity == Severity.Error ? 1 : d.Severity == Severity.Warning ? 2 : 3,
                code = d.Code,
                message = d.Message,
            }),
        };
    }

    /// <summary>The library of the project: every component of every .amplec file, which of them the song imports, with a cache per file.</summary>
    public static async Task<object> LibraryAsync(ProjectSession s, string path, bool refresh, CancellationToken ct)
    {
        string text = path.Length == 0 ? string.Empty : (await s.DocAsync(path, ct).ConfigureAwait(false)).Text;
        return ComponentLibrary.Build(s, text, refresh);
    }

    /// <summary>What adding a component of a file to a song does (the import, optionally an instance) and the new text; nothing is written. A name that is already taken by other code (AMP2212 to AMP2214) refuses it.</summary>
    public static async Task<object> ImportAsync(ProjectSession s, JsonElement body, CancellationToken ct)
    {
        string path = body.GetProperty("path").GetString()!;
        string file = body.GetProperty("file").GetString()!;
        string? name = body.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        bool addBlock = body.TryGetProperty("add", out JsonElement add) && add.ValueKind == JsonValueKind.True;
        string? alias = body.TryGetProperty("alias", out JsonElement al) && al.ValueKind == JsonValueKind.String && al.GetString() is { Length: > 0 } given ? given : null;
        string full = Path.GetFullPath(Path.Combine(s.Root, file));
        if (!full.StartsWith(Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || !file.EndsWith(ModuleService.ComponentExtension, StringComparison.OrdinalIgnoreCase))
            throw new AmpleException("AMP5285", $"\"{file}\" is not a component file of this project.");
        DocSession doc = await s.DocAsync(path, ct).ConfigureAwait(false);
        file = Path.GetRelativePath(s.Root, full).Replace('\\', '/');   // the path as the project writes it, whatever was sent
        ComponentImportAdd result = ComponentEmbedding.AddImport(doc.Text, file, name, addBlock, s.Spec, alias);
        string[] conflicts = [];
        if (result.Added.Count > 0)
        {
            (_, IReadOnlyList<Diagnostic> issues) = ComponentCatalog.List(Service(s), result.SongText, s.Spec);
            conflicts = [.. issues.Where(i => i.Code is "AMP2212" or "AMP2213" or "AMP2214" or "AMP2216" or "AMP2217").Select(i => i.Code + ": " + i.Message)];
        }

        // a name that is taken by other code: an alias that makes the import work is offered (import "file" as alias), only when it was tried and is clean
        string? suggested = null;
        if (conflicts.Length > 0 && alias is null)
        {
            (IReadOnlyList<ComponentListing> visible, _) = ComponentCatalog.List(Service(s), doc.Text, s.Spec);
            string candidate = ComponentLibrary.SuggestAlias(file, new HashSet<string>(visible.Select(c => c.Path.Split('.')[0]), StringComparer.Ordinal));
            ComponentImportAdd trial = ComponentEmbedding.AddImport(doc.Text, file, name, addBlock, s.Spec, candidate);
            (_, IReadOnlyList<Diagnostic> after) = ComponentCatalog.List(Service(s), trial.SongText, s.Spec);
            if (!after.Any(i => i.Code is "AMP2212" or "AMP2213" or "AMP2214" or "AMP2216" or "AMP2217")) suggested = candidate;
        }

        return new { ok = result.Ok && conflicts.Length == 0, suggestedAlias = suggested, message = conflicts.Length > 0 ? "This cannot be imported: " + conflicts[0] : result.Message, added = result.Added, block = result.Block, conflicts, text = conflicts.Length > 0 ? doc.Text : result.SongText };
    }

    public static IReadOnlyList<object> Files(ProjectSession s)
    {
        List<object> found = [];
        foreach (string f in Directory.EnumerateFiles(s.Root, "*" + ModuleService.ComponentExtension, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(s.Root, f).Replace('\\', '/');
            if (relative.StartsWith(".ample/", StringComparison.Ordinal)) continue;
            found.Add(new { file = relative, name = Path.GetFileName(f), components = ComponentReader.Read(ReadLines(f), new Dictionary<int, DocComment>(), relative).Definitions.Select(d => new { d.Name, version = d.Version.ToString() }) });
        }

        return found;
    }

    private static string[] ReadLines(string file) => File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>Seals, unseals or makes a new version of a component. For a song the new text is returned and nothing is written (the editor applies it); for a <c>.amplec</c> file of the project it is written through the command bus.</summary>
    public static async Task<object> EditAsync(ProjectSession s, string action, JsonElement body, CancellationToken ct)
    {
        string name = Read(body, "name"), version = Read(body, "version"), file = Read(body, "file"), path = Read(body, "path");
        if (name.Length == 0) throw new AmpleException("AMP2200", "Name the component (name).");
        string text;
        string? relative = null;
        if (file.Length > 0)
        {
            if (!file.EndsWith(ModuleService.ComponentExtension, StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP2216", "Only a .amplec file of the project can be edited here.");
            string full = ProjectPath.Resolve(s.Root, file);
            if (!File.Exists(full)) throw new AmpleException("AMP2216", $"The file {file} is not in the project.");
            text = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
            relative = file;
        }
        else
        {
            text = (await s.DocAsync(path, ct).ConfigureAwait(false)).Text;
        }

        ComponentEditResult result = action switch
        {
            "seal" => ComponentEdits.Seal(text, name, version.Length == 0 ? null : version, s.Spec),
            "unseal" => ComponentEdits.Unseal(text, name, version.Length == 0 ? null : version, s.Spec),
            "new-version" => ComponentEdits.NewVersion(text, name, version.Length == 0 ? null : version, Read(body, "newVersion"), s.Spec),
            _ => throw new AmpleException("AMP2200", $"There is no component action \"{action}\"."),
        };
        if (!result.Ok) throw new AmpleException("AMP2208", result.Message);
        bool written = false;
        if (relative is not null)
        {
            await s.Bus.ExecuteAsync(new WriteFileCommand(relative, result.Text), ct).ConfigureAwait(false);
            written = true;
        }

        return new { text = result.Text, message = result.Message, file = relative, written };
    }

    /// <summary>Embeds, switches the source, extracts or detaches components of a song. The new text of the song is returned (the editor applies it, so it is one undoable edit); extract also writes the new <c>.amplec</c> file through the command bus. <c>embed-preview</c> only says what would be done.</summary>
    public static async Task<object> SourceAsync(ProjectSession s, string action, JsonElement body, CancellationToken ct)
    {
        string path = Read(body, "path"), name = Read(body, "name"), version = Read(body, "version");
        if (path.Length == 0) throw new AmpleException("AMP2200", "Name the song (path).");
        string text = (await s.DocAsync(path, ct).ConfigureAwait(false)).Text;
        ModuleService service = Service(s);
        (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(text, s.Spec);
        string? requested = version.Length == 0 ? null : version;
        if (action is "migrate-preview" or "migrate")
        {
            string block = Read(body, "block");
            MigrationPlan plan = ComponentMigrations.Plan(text, imported, block, Read(body, "target"), s.Spec);
            if (!plan.Ok) throw new AmpleException(plan.Problem.StartsWith("AMP2221", StringComparison.Ordinal) ? "AMP2221" : "AMP2222", plan.Problem);
            int Count(string song)
            {
                AmpleDocument expanded = service.ParseExpanded(song, s.Spec);
                string notes = expanded.Blocks.FirstOrDefault(b => b.Name == block)?.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty;
                return notes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            }

            bool acknowledged = body.TryGetProperty("acknowledge", out JsonElement ack) && ack.ValueKind == JsonValueKind.True;
            object migration = new
            {
                impact = plan.Impact.ToString(),
                needsAcknowledgement = plan.NeedsAcknowledgement,
                steps = plan.Steps.Select(st => new { st.FromId, st.From, st.ToId, st.To, impact = st.Impact.ToString(), st.Stated, st.Message }),
                changes = plan.Changes,
                messages = plan.Messages,
                warnings = plan.Warnings,
                blockType = plan.BlockType,
                blockVersion = plan.BlockVersion,
                notesBefore = Count(text),
                notesAfter = Count(plan.Text),
            };
            if (action == "migrate-preview") return new { preview = migration };
            if (plan.NeedsAcknowledgement && !acknowledged) throw new AmpleException("AMP2222", $"This migration has the impact {plan.Impact}: look at the preview and send acknowledge true to apply it. Nothing was changed.");
            return new { text = plan.Text, message = $"Migrated {block} to {plan.BlockType} {plan.BlockVersion} (impact {plan.Impact}).", preview = migration };
        }

        if (action == "detach")
        {
            ComponentEditResult detached = ComponentEmbedding.Detach(text, name, requested, s.Spec);
            if (!detached.Ok) throw new AmpleException("AMP2208", detached.Message);
            return new { text = detached.Text, message = detached.Message };
        }

        if (action == "use-template")
        {
            ComponentDefinition? template = imported.FirstOrDefault(d => d.DeclaredName == name && (requested is null || (SemVer.TryParse(requested, out SemVer v) && d.Version.SameAs(v))));
            if (template is null) throw new AmpleException("AMP2216", $"No imported file has the component \"{name}\"{(requested is null ? string.Empty : " " + requested)}: add an import of the template first.");
            ComponentEditResult switched = ComponentEmbedding.UseTemplate(text, template, s.Spec);
            if (!switched.Ok) throw new AmpleException("AMP2212", switched.Message);
            return new { text = switched.Text, message = switched.Message };
        }

        if (action == "extract")
        {
            string file = Read(body, "file");
            if (!file.EndsWith(ModuleService.ComponentExtension, StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP2216", "The file must be a .amplec file of the project (file).");
            string full = ProjectPath.Resolve(s.Root, file);
            if (File.Exists(full)) throw new AmpleException("AMP2216", $"The file {file} exists already: choose another name, nothing is overwritten.");
            ComponentExtraction extracted = ComponentEmbedding.Extract(text, name, requested, file, s.Spec);
            if (!extracted.Ok) throw new AmpleException("AMP2208", extracted.Message);
            await s.Bus.ExecuteAsync(new WriteFileCommand(file, extracted.FileText), ct).ConfigureAwait(false);
            return new { text = extracted.SongText, message = extracted.Message, file };
        }

        // embed: the components of some instances (blocks), or the named ones; whole = everything of the files they come from
        AmpleDocument doc = Language.Parser.Parse(text, s.Spec, new ParseOptions { Components = imported, RequireProjectLine = false });
        List<ComponentDefinition> roots = [];
        foreach (string block in Strings(body, "blocks"))
        {
            BlockNode? node = doc.Blocks.FirstOrDefault(b => b.Name == block) ?? throw new AmpleException("AMP2200", $"There is no block \"{block}\".");
            List<ComponentDefinition> family = [.. imported.Where(d => d.Name == node.Type)];
            ComponentDefinition? pick = family.Count == 0 ? null : VersionRequest.TryParse(node.Version, out VersionRequest request) ? ComponentVersions.Pick(family, request) : ComponentVersions.Default(family);
            if (pick is null) throw new AmpleException("AMP2217", $"The block \"{block}\" is not an instance of an imported component.");
            roots.Add(pick);
        }

        foreach (string component in Strings(body, "components"))
        {
            ComponentDefinition? pick = ComponentVersions.Default(imported.Where(d => d.DeclaredName == component || d.Name == component));
            if (pick is null) throw new AmpleException("AMP2216", $"No imported file has the component \"{component}\".");
            roots.Add(pick);
        }

        if (roots.Count == 0) throw new AmpleException("AMP2200", "Name the blocks (blocks) or the components (components) to embed.");
        IReadOnlyList<ComponentDefinition> closure = body.TryGetProperty("whole", out JsonElement whole) && whole.ValueKind == JsonValueKind.True
            ? [.. imported.Where(d => roots.Any(r => r.Origin == d.Origin))]
            : ComponentEmbedding.Closure(imported, roots);
        EmbeddingPreview preview = ComponentEmbedding.Preview(text, closure, s.Spec);
        object shown = new { components = preview.Components, alreadyThere = preview.AlreadyThere, differs = preview.Differs };
        if (action == "embed-preview") return new { preview = shown };
        string moduleName = Read(body, "module").Length > 0 ? Read(body, "module") : Path.GetFileNameWithoutExtension(roots[0].Origin) is { Length: > 0 } stem ? stem : "embedded";
        ComponentEditResult embedded = ComponentEmbedding.Embed(text, closure, moduleName, Read(body, "moduleId") is { Length: > 0 } mid ? mid : null, Read(body, "moduleVersion") is { Length: > 0 } mv ? mv : null, s.Spec);
        if (!embedded.Ok) throw new AmpleException("AMP2212", embedded.Message);
        return new { text = embedded.Text, message = embedded.Message, preview = shown };
    }

    private static IEnumerable<string> Strings(JsonElement body, string name) => body.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()) : [];

    private static string Read(JsonElement body, string name) => body.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
}
