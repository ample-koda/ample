using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>A file of declarations (a <c>.amplec</c> file) with the components it holds: where a component that a song imports comes from.</summary>
/// <param name="Origin">The name of the file (or <c>song</c>, <c>embedded:name</c>).</param>
/// <param name="Text">The text of the file.</param>
/// <param name="Definitions">The components read from it.</param>
public sealed record ComponentSource(string Origin, string Text, IReadOnlyList<ComponentDefinition> Definitions);

/// <summary>A component that a song used, as the lock file and the build log know it.</summary>
/// <param name="Name">The exported name.</param>
/// <param name="Id">The canonical id.</param>
/// <param name="Version">The version that was resolved.</param>
/// <param name="Semantic">The semantic hash.</param>
/// <param name="Payload">The payload hash.</param>
/// <param name="Origin">Where the definition came from.</param>
/// <param name="Seal">The state of its seal.</param>
/// <param name="Line">The 0-based line of the first instance in the song.</param>
/// <param name="SealReason">Why the seal is void (empty otherwise).</param>
/// <param name="Deprecated">The deprecation message when the version is deprecated, else null.</param>
/// <param name="SignedBy">Who signed the seal, or null.</param>
/// <param name="PublicKey">The public key of the signature, or null.</param>
/// <param name="Signature">The signature over the payload hash, or null.</param>
public sealed record ComponentUsage(string Name, string Id, string Version, string Semantic, string Payload, string Origin, SealState Seal, int Line, string SealReason = "", string? Deprecated = null, string? SignedBy = null, string? PublicKey = null, string? Signature = null);

/// <summary>The song with every instance of a component replaced by what it makes.</summary>
/// <param name="Text">The expanded text.</param>
/// <param name="Diagnostics">Problems with the components of the song (the same shape as the problems of modules).</param>
/// <param name="Used">The components that were used.</param>
/// <param name="LineOrigin">For every line of the expanded text the 0-based line of the original it stands for.</param>
public sealed record ComponentExpansion(string Text, IReadOnlyList<ModuleDiagnostic> Diagnostics, IReadOnlyList<ComponentUsage> Used, IReadOnlyList<int> LineOrigin)
{
    /// <summary>Gets the observation options of the instances by instance name: the side channel that the lowering must not turn into parameters of the generated block.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BlockDirective>> Directives { get; init; } = new Dictionary<string, IReadOnlyList<BlockDirective>>();

    /// <summary>Gets the original process sites of imported Notes component helpers.</summary>
    public IReadOnlyDictionary<string, SourceSite> ObservationHelperSites { get; init; } = new Dictionary<string, SourceSite>();

    /// <summary>Gets the events that the expansion made (the warnings of audio processes), produced through an observation sink with its budgets.</summary>
    public IReadOnlyList<LogEvent> Observations { get; init; } = [];
}

/// <summary>
/// Lowers the instances of components (docs/language/19-components.md): an instance <c>koda.notes.tripleNotes intro stable@1 { copies 3  from a.notes -> source }</c> becomes a <c>midi-clip</c> of the same name whose notes
/// are the call of the helper that the process of its component is, with the connected notes and the properties as arguments. So everything that works on a clip (the note flow, bakes, Jam, previews, the build and the plan
/// identity) works on an instance without a change, and a build that gets the same notes from it builds the same takes. The definitions of components that come from imported files are added at the end of the text.
/// </summary>
public static partial class ComponentExpander
{
    [GeneratedRegex(@"^(\s*)\S+\s+(?:""[^""]*""|\S+)(?:\s+(?:stable|pinned|live)(?:@\S+)?(?=\s|\{))?(.*?)\{\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderRest();

    [GeneratedRegex(@"^(?:component|import|embedded\s+module)\s", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Declares();

    /// <summary>Expands the instances of components in a song text. A song without any comes back unchanged.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="sources">The files that components can come from (the resolved imports), or null.</param>
    /// <param name="session">The session layer of the observation options, or null.</param>
    /// <returns>The expansion.</returns>
    /// <param name="project">The project layer of the observation options, or null.</param>
    public static ComponentExpansion Expand(string songText, SpecData spec, IReadOnlyList<ComponentSource>? sources = null, ConfigLayerInput? session = null, ConfigLayerInput? project = null)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(spec);
        string[] lines = songText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // a song that declares or imports no component has no instance: no need to read it again
        if ((sources is null || sources.Count == 0) && !Declares().IsMatch(songText)) return new ComponentExpansion(songText, [], [], [.. Enumerable.Range(0, lines.Length)]);
        IReadOnlyList<ComponentDefinition> external = [.. (sources ?? []).SelectMany(s => s.Definitions)];
        AmpleDocument doc = Parser.Parse(songText, spec, new ParseOptions { RequireProjectLine = false, Components = external });
        SpecData effective = doc.Spec ?? spec;
        List<BlockNode> instances = [.. doc.Blocks.Where(b => !b.Skipped && b.CloseLine >= 0 && effective.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Component)];
        if (instances.Count == 0) return new ComponentExpansion(songText, [], [], [.. Enumerable.Range(0, lines.Length)]);

        List<ModuleDiagnostic> diagnostics = [];
        ObservationSink sink = new();   // the events of the expansion: the same budgets and rules as the events of the song
        Dictionary<string, (string Value, int Line)> songOptions = doc.Settings.Where(pair => ObservationConfig.IsOption(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (pair.Value, doc.SettingLines.GetValueOrDefault(pair.Key)), StringComparer.Ordinal);
        List<ConfigLayerInput> songLayers = [.. project is null ? [] : new[] { project }, new ConfigLayerInput(new ConfigSource(ConfigLayer.Song), songOptions)];
        Dictionary<string, ComponentUsage> used = [];
        List<ComponentDefinition> all = [.. doc.Components, .. external];
        Dictionary<string, ComponentDefinition> resolved = [];
        HashSet<string> instanceNames = [.. instances.Select(b => b.Name)];
        Dictionary<int, string[]> replacement = [];
        Dictionary<string, AudioPlan> audioPlans = new(StringComparer.Ordinal);
        HashSet<ComponentSource> neededSources = [];
        Dictionary<ComponentDefinition, ComponentSource?> home = [];
        foreach (BlockNode b in instances)
        {
            if (!VersionRequest.TryParse(b.Version, out VersionRequest request))
            {
                diagnostics.Add(new ModuleDiagnostic("AMP2223", $"\"{b.Version}\" is not a version request for {b.Type} (1.8, 1.8.0, ^1.2, ~1.2 or 4.0.0-beta.1).", b.Line, b.Type));
                continue;
            }

            List<ComponentDefinition> family = [.. all.Where(c => c.Name == b.Type)];
            ComponentDefinition? def = ComponentVersions.Pick(family, request);
            if (def is null)
            {
                diagnostics.Add(new ModuleDiagnostic("AMP2215", $"No version of {b.Type} matches @{request}. Versions: {string.Join(", ", family.Select(f => f.Version.ToString()).Order(StringComparer.Ordinal))}.", b.Line, b.Type));
                continue;
            }

            resolved[b.Name] = def;
            if (!home.TryGetValue(def, out ComponentSource? source))
            {
                source = (sources ?? []).FirstOrDefault(s => s.Definitions.Contains(def));
                home[def] = source;
            }

            if (source is not null) neededSources.Add(source);
            string key = def.Id + "@" + def.Version;
            if (!used.ContainsKey(key))
            {
                IReadOnlyList<string> deps = def.IsAudio ? ComponentHashing.DependenciesOf(def, effective, () => [], all) : Dependencies(def, source, doc, spec, all, sources, []);
                SealCheck seal = ComponentHashing.Check(def, deps);
                used[key] = new ComponentUsage(def.Name, def.Id, def.Version.ToString(), seal.Semantic, seal.Payload, def.Origin, seal.State, b.Line, seal.Reason, def.Deprecation?.Message, def.Seal?.SignedBy, def.Seal?.Key, def.Seal?.Signature);
            }

            if (def.IsAudio)
            {
                AudioPlan plan = PlanAudio(b, def, effective, doc.Project?.Tempo ?? 120, diagnostics, all, sink, songLayers, session);
                audioPlans[b.Name] = plan;
                replacement[b.Line] = [];
            }
            else replacement[b.Line] = Instance(b, def, instanceNames, instances, resolved, lines, diagnostics);
        }

        // the lines of an instance are replaced by the expansion, so a wrong setting name on them (a private property set from outside, AMP2261;
        // a name the component does not have, AMP2003) would be lost. The parser checks an instance against the newest version, so a problem is kept
        // only when the version this instance really uses agrees (a block pinned at 1.0 may set what 1.0 has)
        foreach (Diagnostic d in doc.Diagnostics.Where(d => d.Code is "AMP2261" or "AMP2003"))
        {
            BlockNode? owner = instances.FirstOrDefault(b => d.Line - 1 >= b.Line && d.Line - 1 <= b.CloseLine);
            if (owner is null || !resolved.TryGetValue(owner.Name, out ComponentDefinition? picked) || diagnostics.Any(m => m.Code == d.Code && m.Line == d.Line - 1)) continue;
            string? name = owner.Parameters.FirstOrDefault(p => p.Line == d.Line - 1)?.Name;
            bool isPrivate = name is not null && picked.PrivateProperties.Any(p => p.Name == name);
            if (name is null || picked.Properties.Any(p => p.Name == name) || (d.Code == "AMP2261") != isPrivate) continue;
            diagnostics.Add(new ModuleDiagnostic(d.Code, d.Message + (d.Hint.Length > 0 ? " " + d.Hint : string.Empty), d.Line - 1, owner.Type, d.Severity));
        }

        // the files of the components that these components call (and so on) are needed too: the helper of the caller calls their helpers
        Queue<ComponentDefinition> toVisit = new(home.Keys);
        HashSet<ComponentDefinition> visited = [];
        while (toVisit.Count > 0)
        {
            ComponentDefinition current = toVisit.Dequeue();
            if (!visited.Add(current)) continue;
            foreach (ComponentDefinition callee in ComponentNesting.Callees(current, all))
            {
                ComponentSource? calleeSource = (sources ?? []).FirstOrDefault(x => x.Definitions.Contains(callee));
                if (calleeSource is not null) neededSources.Add(calleeSource);
                toVisit.Enqueue(callee);
            }
        }

        // an audio instance becomes the blocks of its processors; they are written once every plan is known, because instances can feed each other
        foreach (BlockNode b in instances.Where(b => audioPlans.ContainsKey(b.Name))) replacement[b.Line] = LowerAudio(b, audioPlans[b.Name], audioPlans, lines[b.Line], diagnostics, name => doc.Blocks.FirstOrDefault(x => x.Name == name) is { } source ? PlacementOf(lines[source.Line]) : string.Empty);
        Dictionary<int, string> audioRewrite = AudioConsumers(doc, audioPlans, replacement, diagnostics);

        // the song may write an imported component with the alias of its import; the expanded text has no alias any more, so the calls in the components of the song itself are written with the declared name
        Dictionary<string, string> aliasCalls = external.Where(c => !c.IsAudio && c.Name != c.DeclaredName).GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DeclaredName, StringComparer.Ordinal);
        foreach (IGrouping<string, ComponentDefinition> clash in external.Where(c => !c.IsAudio && c.Name != c.DeclaredName).GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() > 1))
            diagnostics.Add(new ModuleDiagnostic("AMP2213", $"The path \"{clash.Key}\" is given to different components by different imports ({string.Join(", ", clash.Select(c => c.Id).Distinct(StringComparer.Ordinal))}): use another alias for one of them.", 0, clash.Key));
        Regex? aliasPattern = aliasCalls.Count == 0 ? null : new(@"(?<![\w.\-])(" + string.Join("|", aliasCalls.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?=\s*\()", RegexOptions.None, TimeSpan.FromSeconds(1));

        HashSet<int> skip = [];
        foreach (BlockNode b in instances.Where(b => replacement.ContainsKey(b.Line))) for (int l = b.Line + 1; l <= b.CloseLine; l++) skip.Add(l);
        Dictionary<int, FromLine> rewrite = doc.Blocks.Where(b => !instanceNames.Contains(b.Name) || !replacement.ContainsKey(b.Line)).SelectMany(b => b.Froms).Where(f => instanceNames.Contains(f.Block) && resolved.TryGetValue(f.Block, out ComponentDefinition? d) && d.Output is { } o && f.Output != "notes").ToDictionary(f => f.Line);
        List<string> output = [];
        List<int> origin = [];
        for (int i = 0; i < lines.Length; i++)
        {
            if (replacement.TryGetValue(i, out string[]? generated))
            {
                output.AddRange(generated);
                origin.AddRange(generated.Select(_ => i));
                continue;
            }

            if (skip.Contains(i)) continue;
            string line = lines[i];
            if (aliasPattern is not null) line = ComponentNesting.ReplaceInCode(line, aliasPattern, v => aliasCalls[v]);
            if (audioRewrite.TryGetValue(i, out string? audioLine))
            {
                output.Add(new string(' ', line.Length - line.TrimStart().Length) + audioLine);
                origin.Add(i);
                continue;
            }

            if (rewrite.TryGetValue(i, out FromLine? f))
            {
                int indent = line.Length - line.TrimStart().Length;
                line = new string(' ', indent) + $"from {f.RawBlock}.notes -> {f.RawInput}";
            }

            output.Add(line);
            origin.Add(i);
        }

        // the files the used components come from are added whole (their helpers, constants and types come with them), once
        Dictionary<string, SourceSite> observationSites = new(StringComparer.OrdinalIgnoreCase);
        foreach (ComponentSource source in neededSources)
        {
            foreach (ComponentDefinition definition in source.Definitions.Where(d => !d.IsAudio && d.ProcessLine >= 0))
                observationSites.TryAdd(definition.HelperName, new SourceSite(source.Origin, definition.ProcessLine + 1, 1, 1));
            int first = used.Values.Where(u => source.Definitions.Any(d => d.Id == u.Id && d.Version.ToString() == u.Version)).Select(u => u.Line).DefaultIfEmpty(0).Min();
            output.Add(string.Empty);
            origin.Add(first);
            foreach (string line in source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                output.Add(line);
                origin.Add(first);
            }
        }

        Dictionary<string, IReadOnlyList<BlockDirective>> directives = instances.Where(b => b.Directives.Count > 0).ToDictionary(b => b.Name,
            b => (IReadOnlyList<BlockDirective>)[.. b.Directives], StringComparer.Ordinal);
        return new ComponentExpansion(string.Join('\n', output), diagnostics, [.. used.Values.OrderBy(u => u.Name,
            StringComparer.Ordinal).ThenBy(u => u.Version, StringComparer.Ordinal)], origin)
        { Directives = directives, ObservationHelperSites = observationSites, Observations = sink.Events };
    }

    /// <summary>The hashes of what the process of a component uses, from the helper table of the file the component lives in, and of the components it calls.</summary>
    private static List<string> Dependencies(ComponentDefinition def, ComponentSource? source, AmpleDocument songDoc, SpecData spec, IReadOnlyList<ComponentDefinition> all, IReadOnlyList<ComponentSource>? sources, HashSet<ComponentDefinition> seen)
    {
        seen.Add(def);
        string process = string.Join('\n', ComponentNesting.Rewrite(def, all));
        List<string> deps = source is null ? [.. songDoc.HelperTable.ClosureHashes(process)] : [.. Parser.Parse(source.Text, spec, new ParseOptions { RequireProjectLine = false }).HelperTable.ClosureHashes(process)];
        foreach (ComponentDefinition callee in ComponentNesting.Callees(def, all).Where(c => !seen.Contains(c)))
        {
            ComponentSource? calleeSource = (sources ?? []).FirstOrDefault(x => x.Definitions.Contains(callee));
            deps.Add("component=" + callee.Name + "@" + callee.Version + ":" + ComponentHashing.Semantic(callee, Dependencies(callee, calleeSource, songDoc, spec, all, sources, seen)));
        }

        return deps;
    }

    private static string[] Instance(BlockNode b, ComponentDefinition def, HashSet<string> instanceNames, List<BlockNode> instances, Dictionary<string, ComponentDefinition> resolved, string[] lines, List<ModuleDiagnostic> diagnostics)
    {
        string header = lines[b.Line];
        Match rest = HeaderRest().Match(header);
        string indent = rest.Success ? rest.Groups[1].Value : string.Empty;
        string placement = rest.Success ? rest.Groups[2].Value.TrimEnd() : string.Empty;
        List<string> args = [];
        foreach (ComponentPort input in def.Inputs)
        {
            FromLine? from = b.Froms.FirstOrDefault(f => !f.Disabled && f.Input == input.Name);
            string notes;
            if (from is not null)
            {
                // an instance of a component offers its notes on the port notes of the clip it becomes
                string port = instanceNames.Contains(from.Block) && resolved.ContainsKey(from.Block) ? "notes" : from.RawOutput;
                notes = from.RawBlock + "." + port;
            }
            else if (input.Fallback is not null) notes = NotesText(b, def.Property(input.Fallback), true);
            else
            {
                if (!input.Optional) diagnostics.Add(new ModuleDiagnostic("AMP2218", $"The input \"{input.Name}\" of {b.Type} \"{b.Name}\" is not connected. Add: from <block>.notes -> {input.Name}", b.Line, b.Type));
                notes = "emptyNotes()";
            }

            args.Add(notes);
            if (input.Optional || input.Fallback is not null) args.Add(from is not null ? "true" : "false");
        }

        foreach (ComponentProperty p in def.Properties)
        {
            args.Add(p.Type.TrimEnd('?') == "Notes" ? NotesText(b, p, false) : ValueText(b, p));
        }

        string call = $"{def.HelperName}({string.Join(", ", args)})";
        return [$"{indent}midi-clip {b.RawName}{(placement.Length > 0 ? placement + " " : " ")}{{", $"{indent}  notes {call}", $"{indent}}}"];
    }

    private static string ValueText(BlockNode b, ComponentProperty p)
    {
        Parameter? given = b.Parameters.LastOrDefault(x => x.Name == p.Name);
        string text = given?.Value.Trim() ?? p.Default ?? string.Empty;
        return text.Length == 0 ? "0" : text;
    }

    private static string NotesText(BlockNode b, ComponentProperty? p, bool fallback)
    {
        if (p is null) return "emptyNotes()";
        Parameter? given = b.Parameters.LastOrDefault(x => x.Name == p.Name);
        string text = (given?.Value ?? p.Default ?? string.Empty).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        _ = fallback;
        return text.Length == 0 ? "emptyNotes()" : text;
    }
}
