using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Server;

/// <summary>The components a song of the proposal can see, for the assistant (ample_components).</summary>
internal sealed partial class AiService
{
    /// <summary>Lists the components of a song of the working copy: every version with its seal, signature and deprecation, the contract of the default version, and the problems of putting them together.</summary>
    /// <param name="token">The context token of the task.</param>
    /// <param name="path">The song, or empty for the song of the task.</param>
    /// <returns>The listing.</returns>
    public JsonObject ComponentsTool(string token, string path)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new Ample.Core.Abstractions.AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path ?? string.Empty);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase)) rel = rel[8..];
        if (rel.Length == 0) rel = task.Request.Song;
        string file = ProjectPath.Resolve(task.ProjectDir, rel);
        if (!File.Exists(file)) throw new Ample.Core.Abstractions.AmpleException("AMP5458", $"There is no song '{rel}' in the proposal (check the path with ample_project).");
        string root = Path.Combine(task.Workspace, "project");
        (IReadOnlyList<ComponentListing> components, IReadOnlyList<Diagnostic> issues) = ComponentCatalog.List(new ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")), File.ReadAllText(file), s.Spec);
        JsonArray list = [];
        foreach (ComponentListing c in components)
        {
            JsonArray versions = [];
            foreach (ComponentVersionInfo v in c.Versions)
            {
                JsonObject version = new() { ["version"] = v.Version, ["seal"] = v.Seal, ["current"] = v.Current, ["prerelease"] = v.Prerelease, ["origin"] = v.Origin };
                if (v.SealReason.Length > 0) version["sealReason"] = v.SealReason;
                if (v.SignedBy is not null) version["signedBy"] = v.SignedBy;
                if (v.SignatureValid is { } valid) version["signatureValid"] = valid;
                if (v.Deprecated is not null) version["deprecated"] = v.Deprecated;
                if (v.Replacement is not null) version["replacement"] = v.Replacement;
                if (v.Migrations.Count > 0) version["migratesTo"] = new JsonArray([.. v.Migrations.Select(m => (JsonNode)JsonValue.Create(m)!)]);
                versions.Add(version);
            }

            list.Add(new JsonObject
            {
                ["path"] = c.Path,
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["description"] = c.Description,
                ["tags"] = new JsonArray([.. c.Tags.Select(t => (JsonNode?)t)]),
                ["ui"] = JsonSerializer.SerializeToNode(c.Ui),
                ["defaultVersion"] = c.DefaultVersion,
                ["kind"] = c.Kind,
                ["processors"] = new JsonArray([.. c.Processors.Select(p => (JsonNode?)p)]),
                ["clientPreview"] = c.ClientPreview,
                ["livePreview"] = c.LivePreview,
                ["latencyMs"] = c.LatencyMs,
                ["inputs"] = new JsonArray([.. c.Inputs.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["type"] = i.Type, ["optional"] = i.Optional, ["fallback"] = i.Fallback })]),
                ["properties"] = new JsonArray([.. c.Properties.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["default"] = p.Default, ["description"] = p.Description })]),
                ["outputs"] = new JsonArray([.. c.Outputs.Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["type"] = o.Type })]),
                ["versions"] = versions,
            });
        }

        return new JsonObject
        {
            ["song"] = rel,
            ["components"] = list,
            ["problems"] = new JsonArray([.. issues.Select(i => (JsonNode)new JsonObject { ["code"] = i.Code, ["message"] = i.Message, ["line"] = i.Line })]),
            ["hint"] = components.Count == 0 ? "This song has no components. Declare one with component <name> version <x.y.z> { ... } or import a .amplec file (see ample_lookup topic components)." : "Use an instance as a block: <name> <instance> stable[@version] { <properties>  from <block>.notes -> <input> }. A sealed version is immutable: make a new version instead of changing it.",
        };
    }

    /// <summary>Searches the library of the working copy: every component of every .amplec file of the proposal's project (not only what a song imports), whether the song imports it, and what to write to import it (with an alias when the name is taken).</summary>
    /// <param name="token">The context token of the task.</param>
    /// <param name="path">The song the "imported" marks are for, or empty for the song of the task.</param>
    /// <param name="query">Words that must all appear in the name, full name, description, file or #tags (empty: everything).</param>
    /// <param name="tag">A tag the component must have (without #), or empty.</param>
    /// <param name="imported">"yes" for the components the song imports, "no" for those it does not, empty for all.</param>
    /// <returns>The matching components with the lines to write.</returns>
    public JsonObject ComponentLibraryTool(string token, string path, string query, string tag, string imported)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new Ample.Core.Abstractions.AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path ?? string.Empty);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase)) rel = rel[8..];
        if (rel.Length == 0) rel = task.Request.Song;
        string file = ProjectPath.Resolve(task.ProjectDir, rel);
        string root = Path.Combine(task.Workspace, "project");
        string songText = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        JsonNode library = JsonSerializer.SerializeToNode(ComponentLibrary.Build(new ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")), root, s.Spec, songText, refresh: false))!;
        string[] words = (query ?? string.Empty).ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        string wantedTag = (tag ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();
        JsonArray found = [];
        int total = 0;
        foreach (JsonNode? f in library["files"]!.AsArray())
        {
            string fileName = f!["file"]!.GetValue<string>();
            foreach (JsonNode? c in f["components"]!.AsArray())
            {
                total++;
                bool isImported = c!["imported"]!.GetValue<bool>();
                if ((imported == "yes" && !isImported) || (imported == "no" && isImported)) continue;
                string[] tags = [.. c["tags"]!.AsArray().Select(x => x!.GetValue<string>())];
                if (wantedTag.Length > 0 && !tags.Contains(wantedTag)) continue;
                string name = c["name"]!.GetValue<string>();
                string haystack = string.Join(' ', c["title"]!.GetValue<string>(), name, c["description"]!.GetValue<string>(), fileName, c["kind"]!.GetValue<string>(), string.Join(' ', tags.Select(x => "#" + x))).ToLowerInvariant();
                if (!words.All(haystack.Contains)) continue;
                string? alias = c["suggestedAlias"]?.GetValue<string>();
                int dot = name.IndexOf('.', StringComparison.Ordinal);
                found.Add(new JsonObject
                {
                    ["name"] = name,
                    ["title"] = c["title"]!.GetValue<string>(),
                    ["kind"] = c["kind"]!.GetValue<string>(),
                    ["description"] = c["description"]!.GetValue<string>(),
                    ["tags"] = new JsonArray([.. tags.Select(x => (JsonNode?)x)]),
                    ["file"] = fileName,
                    ["version"] = c["version"]!.GetValue<string>(),
                    ["seal"] = c["seal"]!.GetValue<string>(),
                    ["imported"] = isImported,
                    ["nameTaken"] = c["nameTaken"]!.GetValue<bool>(),
                    ["importLine"] = isImported ? null : alias is null ? $"import \"{fileName}\"" : $"import \"{fileName}\" as {alias}",
                    ["instanceType"] = isImported || alias is null ? name : alias + (dot >= 0 ? name[dot..] : "." + name),
                });
            }
        }

        return new JsonObject
        {
            ["song"] = rel,
            ["total"] = total,
            ["matches"] = found,
            ["hint"] = "importLine is the line to add to the song (after the project line, with the other imports) when imported is false; instanceType is what to write as the block type. When nameTaken is true another component with the same name is already visible: the import line carries an alias that was checked to work. To make a new component, write a .amplec file (see ample_lookup topic components) and add the import; give it #[DisplayName], a description and #[Tags(...)] so it can be found.",
        };
    }
}
