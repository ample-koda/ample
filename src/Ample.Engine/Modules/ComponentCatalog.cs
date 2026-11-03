using Ample.Core.Abstractions;
using System.Text;
using Ample.Language;

namespace Ample.Engine.Modules;

/// <summary>A port or property of a component as the palette and the property panel show it.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The type.</param>
/// <param name="Default">The default as written, or null.</param>
/// <param name="Optional">For an input: whether it may stay unconnected.</param>
/// <param name="Fallback">For an input: the property that stands in for it, or null.</param>
/// <param name="Description">One sentence of help.</param>
/// <param name="Editor">The editor a Notes property opens in (jam), or empty.</param>
public sealed record ComponentMember(string Name, string Type, string? Default, bool Optional, string? Fallback, string Description, string Editor = "")
{
    /// <summary>Gets what kind of control fits the property, from its type and attributes: <c>number</c>, <c>choice</c>, <c>toggle</c>, <c>notes</c> or <c>text</c>.</summary>
    public string Kind { get; init; } = "text";

    /// <summary>Gets the friendly name of the property (<c>#[DisplayName("Echo")]</c>), or empty: the panel, the knobs and the rows show it, the name stays in the tooltip.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the unit a number is written with (<c>%</c>, <c>dB</c>, <c>Hz</c>), or null.</summary>
    public string? Unit { get; init; }

    /// <summary>Gets the lowest value (<c>MinValue</c>), or null.</summary>
    public double? Min { get; init; }

    /// <summary>Gets the highest value (<c>MaxValue</c>), or null.</summary>
    public double? Max { get; init; }

    /// <summary>Gets the size of a step (<c>Step</c>), or null.</summary>
    public double? Step { get; init; }

    /// <summary>Gets the values of a choice (<c>Options</c>), or an empty list.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>One version of a component, with its status.</summary>
/// <param name="Version">The version.</param>
/// <param name="Current">Whether it is marked current.</param>
/// <param name="Prerelease">Whether it is a pre-release.</param>
/// <param name="Origin">Where it comes from: <c>song</c> or a file.</param>
/// <param name="Seal">The state of its seal.</param>
/// <param name="SealReason">Why the seal is void, or empty.</param>
/// <param name="SignedBy">Who signed it, or null.</param>
/// <param name="SignatureValid">Whether the signature matches (null when there is none).</param>
/// <param name="Semantic">The semantic hash.</param>
/// <param name="Payload">The payload hash.</param>
/// <param name="Deprecated">The deprecation message, or null.</param>
/// <param name="Replacement">What to use instead, or null.</param>
/// <param name="Migrations">The versions or components an automatic migration leads to.</param>
/// <param name="Line">The 0-based line of the declaration in its file.</param>
public sealed record ComponentVersionInfo(string Version, bool Current, bool Prerelease, string Origin, string Seal, string SealReason, string? SignedBy, bool? SignatureValid, string Semantic, string Payload, string? Deprecated, string? Replacement, IReadOnlyList<string> Migrations, int Line);

/// <summary>A component with all its visible versions.</summary>
/// <param name="Path">The exported path.</param>
/// <param name="Id">The canonical id.</param>
/// <param name="Title">The title of the palette.</param>
/// <param name="Category">The group of the palette, or empty.</param>
/// <param name="Description">One sentence, or empty.</param>
/// <param name="Experimental">Whether it is marked experimental.</param>
/// <param name="DefaultVersion">The version a reference without a version gets.</param>
/// <param name="Versions">The versions, highest first.</param>
/// <param name="Inputs">The inputs of the default version.</param>
/// <param name="Properties">The properties of the default version.</param>
/// <param name="Outputs">The outputs of the default version.</param>
public sealed record ComponentListing(string Path, string Id, string Title, string Category, string Description, bool Experimental, string DefaultVersion, IReadOnlyList<ComponentVersionInfo> Versions, IReadOnlyList<ComponentMember> Inputs, IReadOnlyList<ComponentMember> Properties, IReadOnlyList<ComponentMember> Outputs)
{
    /// <summary>Gets what the component is made of, derived from the types of its ports: <c>Notes</c>, <c>Audio</c> or <c>Mixed</c>.</summary>
    public string Kind { get; init; } = "Notes";

    /// <summary>Gets a value indicating whether the browser can preview the component (derived: every processor must be previewable).</summary>
    public bool ClientPreview { get; init; } = true;

    /// <summary>Gets the processors that the browser cannot preview.</summary>
    public IReadOnlyList<string> NoPreview { get; init; } = [];

    /// <summary>Gets the latency an audio component adds (the longest path), in milliseconds; not compensated.</summary>
    public double LatencyMs { get; init; }

    /// <summary>Gets the layout of the component (its <c>ui</c> block, or the one made from the attributes of its properties): what the panel, the card and the popup show, and whether a live preview is asked for.</summary>
    public ComponentUi Ui { get; init; } = ComponentUi.Empty;

    /// <summary>Gets whether an audio component can be previewed in real time in the browser while a knob moves (derived: every processor has a live chain).</summary>
    public bool LivePreview { get; init; }

    /// <summary>Gets the processors that stop a live preview.</summary>
    public IReadOnlyList<string> NoLive { get; init; } = [];

    /// <summary>Gets the tags of the component (<c>#[Tags("echo", "space")]</c>): lower case, without a leading #, no duplicates. The library search looks in them.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the processors an audio component uses, with the version of their blocks.</summary>
    public IReadOnlyList<string> Processors { get; init; } = [];

    /// <summary>Gets, for an audio component, the block name part of the processor that gives each output (the instance builds as <c>instance__node</c>), so the board can play the instance with the take of that processor.</summary>
    public IReadOnlyDictionary<string, string> OutputNodes { get; init; } = new Dictionary<string, string>();
}

/// <summary>What a song can see of components, in the shape the palette, the property panel, the assistant and the command line need.</summary>
public static class ComponentCatalog
{
    private static string Attribute(ComponentDefinition d, string name) => d.Attributes.FirstOrDefault(a => a.Name == name) is { Args.Count: > 0 } a ? a.Args[0].Trim('"') : string.Empty;

    private static string[] TagsOf(ComponentDefinition d) => [.. d.Attributes.Where(a => a.Name == "Tags").SelectMany(a => a.Args).Select(t => t.Trim().Trim('"').TrimStart('#').Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)];

    private static ComponentMember Member(ComponentPort p) => new(p.Name, p.Type, null, p.Optional, p.Fallback, p.Attributes.FirstOrDefault(a => a.Name == "Description") is { Args.Count: > 0 } a ? a.Args[0].Trim('"') : p.Doc?.Summary ?? string.Empty);

    private static ComponentMember Member(ComponentProperty p)
    {
        ComponentControl control = ComponentControl.Of(p);
        return new ComponentMember(p.Name, p.Type, p.Default, false, null, p.Attributes.FirstOrDefault(a => a.Name == "Description") is { Args.Count: > 0 } a ? a.Args[0].Trim('"') : p.Doc?.Summary ?? string.Empty, p.Attributes.FirstOrDefault(a => a.Name == "Editor") is { Args.Count: > 0 } e ? e.Args[0].Trim('"') : string.Empty)
        {
            Title = p.Attributes.FirstOrDefault(a => a.Name == "DisplayName") is { Args.Count: > 0 } named ? named.Args[0].Trim().Trim('"') : string.Empty,
            Kind = control.Kind,
            Unit = control.Unit,
            Min = control.Min,
            Max = control.Max,
            Step = control.Step,
            Options = control.Options,
        };
    }

    /// <summary>The internal graph of an instance of an Audio component, with the values the instance gives its properties.</summary>
    /// <param name="service">The module service of the project.</param>
    /// <param name="songText">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="block">The instance.</param>
    /// <param name="overrides">Values for properties that are not written in the song yet (a knob that is being moved), or null.</param>
    /// <returns>The component and its graph.</returns>
    public static (ComponentDefinition Definition, AudioGraph Graph) GraphOf(ModuleService service, string songText, SpecData spec, string block, IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(spec);
        (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(songText, spec);
        AmpleDocument doc = Parser.Parse(songText, spec, new ParseOptions { Components = imported, RequireProjectLine = false });
        BlockNode node = doc.Blocks.FirstOrDefault(b => b.Name == block) ?? throw new AmpleException("AMP2200", $"There is no block \"{block}\".");
        List<ComponentDefinition> family = [.. imported.Concat(doc.Components).Where(d => d.Name == node.Type)];
        ComponentDefinition? def = family.Count == 0 ? null : VersionRequest.TryParse(node.Version, out VersionRequest request) ? ComponentVersions.Pick(family, request) : ComponentVersions.Default(family);
        if (def is null || !def.IsAudio) throw new AmpleException("AMP2217", $"The block \"{block}\" is not an instance of an Audio component.");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (ComponentProperty p in def.Properties) values[p.Name] = (node.Parameters.LastOrDefault(x => x.Name == p.Name)?.Value ?? p.Default ?? string.Empty).Trim().Trim('"');
        // values that are not written in the song yet (a knob that is being moved): the graph the song would have with them
        if (overrides is not null) foreach ((string name, string value) in overrides) if (def.Properties.Any(p => p.Name == name)) values[name] = value.Trim().Trim('"');
        return (def, AudioGraphBuilder.Build(def, doc.Spec ?? spec, values, doc.Project?.Tempo ?? 120, [.. imported, .. doc.Components]));
    }

    private static string KindOf(ComponentDefinition d)
    {
        bool audio = d.Inputs.Concat(d.Outputs).Any(p => p.Type.TrimEnd('?') == "Audio");
        bool notes = d.Inputs.Concat(d.Outputs).Any(p => p.Type.TrimEnd('?') == "Notes");
        return audio && notes ? "Mixed" : audio ? "Audio" : "Notes";
    }

    /// <summary>Lists the components a song can see: its own and the imported ones, each with every version and its status.</summary>
    /// <param name="service">The module service of the project.</param>
    /// <param name="songText">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The listings by path, and the problems of putting them together.</returns>
    public static (IReadOnlyList<ComponentListing> Components, IReadOnlyList<Diagnostic> Issues) List(ModuleService service, string songText, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(spec);
        (IReadOnlyList<ComponentDefinition> imported, IReadOnlyList<Diagnostic> issues) = service.ComponentsForEditors(songText, spec);
        AmpleDocument own = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false });
        Dictionary<string, AmpleDocument> documents = new(StringComparer.Ordinal) { ["song"] = own };
        ComponentRegistryResult registry = service.Components(songText, spec);
        List<ComponentDefinition> all = [.. own.Components, .. imported];
        AudioGraph? Graph(ComponentDefinition d) => d.IsAudio ? AudioGraphBuilder.Build(d, spec, new Dictionary<string, string>(StringComparer.Ordinal), 120, all) : null;
        IReadOnlyList<string> Deps(ComponentDefinition d)
        {
            string origin = own.Components.Contains(d) ? "song" : registry.Sources.FirstOrDefault(s => s.Definitions.Contains(d))?.Origin ?? "song";
            if (!documents.TryGetValue(origin, out AmpleDocument? doc))
            {
                ComponentSource? source = registry.Sources.FirstOrDefault(s => s.Origin == origin);
                documents[origin] = doc = source is null ? own : Parser.Parse(source.Text, spec, new ParseOptions { RequireProjectLine = false });
            }

            return ComponentHashing.DependenciesOf(d, spec, () => doc.HelperTable.ClosureHashes(string.Join('\n', d.ProcessBody)), all);
        }

        List<ComponentListing> listings = [];
        foreach (IGrouping<string, ComponentDefinition> family in all.GroupBy(d => d.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            ComponentDefinition basis = ComponentVersions.Default(family)!;
            List<ComponentVersionInfo> versions = [];
            foreach (ComponentDefinition d in family.OrderByDescending(x => x.Version))
            {
                SealCheck check = ComponentHashing.Check(d, Deps(d));
                bool? signature = d.Seal is { Signature: not null, Key: not null } s && check.State == SealState.Valid ? Ample.Storage.Security.SigningKeys.Verify(s.Key, Encoding.UTF8.GetBytes(check.Payload), s.Signature) : null;
                versions.Add(new ComponentVersionInfo(d.Version.ToString(), d.Current, d.Version.IsPrerelease, d.Origin, check.State.ToString().ToLowerInvariant(), check.Reason, d.Seal?.SignedBy, signature, check.Semantic, check.Payload, d.Deprecation?.Message, d.Deprecation?.Replacement, [.. d.Migrations.Select(m => m.To)], d.HeaderLine));
            }

            listings.Add(new ComponentListing(
                family.Key,
                basis.Id,
                basis.Title,
                Attribute(basis, "Category"),
                Attribute(basis, "Description") is { Length: > 0 } text ? text : basis.Doc?.Summary ?? string.Empty,
                basis.Attributes.Any(a => a.Name == "Experimental"),
                basis.Version.ToString(),
                versions,
                [.. basis.Inputs.Select(Member)],
                [.. basis.Properties.Select(Member)],
                [.. basis.Outputs.Select(Member)])
            {
                Kind = KindOf(basis),
                Tags = TagsOf(basis),
                Ui = ComponentUiReader.Effective(basis),
                LivePreview = Graph(basis)?.LivePreview ?? false,
                NoLive = Graph(basis)?.NoLive ?? [],
                ClientPreview = Graph(basis)?.ClientPreview ?? true,
                NoPreview = Graph(basis)?.NoPreview ?? [],
                LatencyMs = Graph(basis)?.LatencyMs ?? 0,
                Processors = Graph(basis)?.Dependencies.Select(d => d["processor=".Length..]).ToArray() ?? [],
                OutputNodes = Graph(basis)?.Outputs.Where(o => o.Value.StartsWith("node:", StringComparison.Ordinal)).ToDictionary(o => o.Key, o => o.Value[5..], StringComparer.Ordinal) ?? new Dictionary<string, string>(),
            });
        }

        return (listings, issues);
    }
}
