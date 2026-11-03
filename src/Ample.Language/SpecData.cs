using System.Text.Json;

namespace Ample.Language;

/// <summary>A port of a block template: its name, stream type and whether it must be connected, with an optional friendly title and a description.</summary>
/// <param name="Name">Port name.</param>
/// <param name="Type">Port type.</param>
/// <param name="Required">True for a required input.</param>
/// <param name="Title">A friendly name for the port (empty: the port name).</param>
/// <param name="Description">What the port is for, for the tooltip and the assistant (empty: none).</param>
/// <param name="Stream">The name of the stream the port carries, which a v1 song writes (the spec file's <c>name</c>); <paramref name="Name"/> is the older internal name (empty: the same).</param>
public sealed record PortSpec(string Name, string Type, bool Required, string Title = "", string Description = "", string Stream = "");

/// <summary>Editor and documentation metadata for a block parameter.</summary>
/// <param name="Name">Language parameter name.</param>
/// <param name="Label">Short user-facing label.</param>
/// <param name="Type">Language value type.</param>
/// <param name="Default">Default source value.</param>
/// <param name="Documentation">Short help text.</param>
/// <param name="ModalEditor">True when the property panel offers the expanded editor.</param>
/// <param name="Multiline">True when embedded line breaks are allowed.</param>
/// <param name="Kind">How the property panel edits it: text, number, choice (a fixed list), combo (a list or your own text), seed, sampleset, file, pattern, channels (one value per input) or engine (the place and model of an ACE block).</param>
/// <param name="Options">The list of a choice, a combo or the channels.</param>
/// <param name="Min">Lowest number.</param>
/// <param name="Max">Highest number.</param>
/// <param name="Step">Step of the arrows.</param>
/// <param name="Unit">Unit written after the number (dB, ms, %, LUFS).</param>
/// <param name="Placeholder">Hint shown in an empty field.</param>
/// <param name="Accept">File extensions a file field offers.</param>
/// <param name="Count">Number of values of a channels field.</param>
/// <param name="AffectsOutput">False marks an execution-policy toggle (how the renderer works, e.g. a time/memory trade-off) that never changes the
/// rendered sound: every value of the parameter gives byte-identical output. True (the default) is an ordinary parameter that can change the sound.
/// A UI may group these apart from the musical controls; a renderer must prove the claim (same input, same output, for every option) before setting it.</param>
/// <param name="Automatable">True when the parameter can change over the song (an automation expression or a control); it then has a control input of its own.</param>
/// <param name="Aliases">Extra names the parser accepts for this parameter (rewritten to the real name), or null.</param>
public sealed record TemplateParameterSpec(string Name, string Label, string Type, string Default, string? Documentation, bool ModalEditor, bool Multiline, string Kind = "text", IReadOnlyList<string>? Options = null, double? Min = null, double? Max = null, double? Step = null, string? Unit = null, string? Placeholder = null, IReadOnlyList<string>? Accept = null, int Count = 0, bool AffectsOutput = true, bool Automatable = false, IReadOnlyList<ParameterAlias>? Aliases = null);

/// <summary>Another name a parameter is accepted under; the parser rewrites it to the real name.</summary>
/// <param name="Name">The extra name.</param>
/// <param name="Convert">How the value is converted to the real parameter: <c>hz-to-period-ms</c> (a rate in Hz becomes a period in ms), or null for the same value.</param>
public sealed record ParameterAlias(string Name, string? Convert);

/// <summary>A block template from <c>spec/templates</c>.</summary>
/// <param name="Id">Block type id.</param>
/// <param name="Title">Display title.</param>
/// <param name="Class">Default class.</param>
/// <param name="Version">Current version.</param>
/// <param name="Dynamic">True when results can change between builds.</param>
/// <param name="Plugin">Required plugin id, or null.</param>
/// <param name="Inputs">Input ports.</param>
/// <param name="Outputs">Output ports.</param>
/// <param name="Parameters">Parameter names with defaults.</param>
/// <param name="ParameterSpecs">Parameter editor and documentation metadata.</param>
/// <param name="Description">One-line description.</param>
public sealed record TemplateSpec(string Id, string Title, string Class, string Version, bool Dynamic, string? Plugin, IReadOnlyList<PortSpec> Inputs, IReadOnlyList<PortSpec> Outputs, IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, TemplateParameterSpec> ParameterSpecs, string Description)
{
    /// <summary>Gets whether the template comes from a component (a reusable typed block of Notes code) and not from the catalogue of the spec.</summary>
    public bool Component { get; init; }

    /// <summary>
    /// Gets the AMPLE version since which this definition reads the same (<c>"stableSince"</c> in the template, 1 when it says nothing). It is the definition that says it, never a song: a song written for an older version than this has to be migrated before the block can be edited, because the block was changed after it.
    /// </summary>
    public int StableSince { get; init; } = 1;

    /// <summary>
    /// Gets the control inputs of the block: one for every automatable parameter (<c>"automatable": true</c> in the template), named like the parameter and typed by its unit, so <c>from traffic.level -> cutoff</c> steers the cutoff.
    /// They are not part of <see cref="Inputs"/> (a graph shows them only when something is connected).
    /// </summary>
    public IReadOnlyList<PortSpec> ControlInputs { get; init; } = [];

    /// <summary>Gets the delay in milliseconds that this block adds to its signal (<c>"latencyMs"</c> in the template), 0 when it adds none. The renderers of AMPLE work on whole files and look ahead without delaying (the limiter included), so no built-in template declares a latency; a processor that really delays its output must declare it.</summary>
    public double LatencyMs { get; init; }

    /// <summary>Gets whether the browser previews this block itself (<c>"clientPreview": true</c> in the template), which decides the preview of an audio component.</summary>
    public bool ClientPreview { get; init; }

    /// <summary>Gets whether the browser has a real-time chain for this block (<c>"livePreview": true</c> in the template), which decides whether an audio component can be previewed while a knob moves.</summary>
    public bool LivePreview { get; init; }
}

/// <summary>A lint rule from <c>spec/lint-rules.json</c>.</summary>
/// <param name="Code">Rule code.</param>
/// <param name="Name">Rule name.</param>
/// <param name="DefaultLevel">Default level: off, info, warning, error.</param>
/// <param name="Description">What the rule finds.</param>
public sealed record LintRuleSpec(string Code, string Name, string DefaultLevel, string Description);

/// <summary>One catalogue entry of <c>spec/diagnostics.json</c>.</summary>
/// <param name="Code">Code.</param>
/// <param name="Severity">Severity word.</param>
/// <param name="Title">Title.</param>
/// <param name="Explanation">Explanation.</param>
/// <param name="Stage">static or runtime.</param>
public sealed record DiagnosticSpec(string Code, string Severity, string Title, string Explanation, string Stage);

/// <summary>The language data read from <c>spec/</c>. The library does no I/O: the host supplies the file contents.</summary>
public sealed class SpecData
{
    private SpecData()
    {
    }

    /// <summary>Gets the spec version.</summary>
    public string Version { get; private init; } = string.Empty;

    /// <summary>Gets the templates by block type id, in catalogue order.</summary>
    public IReadOnlyDictionary<string, TemplateSpec> Templates { get; private set; } = new Dictionary<string, TemplateSpec>();

    /// <summary>Gets a copy in which a template declares a delay (for a host that adds a processor with a real delay, and for tests of the delay compensation).</summary>
    /// <param name="template">The block type.</param>
    /// <param name="milliseconds">The delay it adds to its signal.</param>
    /// <returns>The copy, or this data when the block type is unknown.</returns>
    public SpecData WithLatency(string template, double milliseconds)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!Templates.TryGetValue(template, out TemplateSpec? existing)) return this;
        Dictionary<string, TemplateSpec> changed = new(Templates, StringComparer.Ordinal) { [template] = existing with { LatencyMs = milliseconds } };
        SpecData copy = (SpecData)MemberwiseClone();
        copy.Templates = changed;
        return copy;
    }

    /// <summary>Gets a copy without the block types the predicate selects (a host that does not offer them: they no longer appear in completion and are unknown block types).</summary>
    /// <param name="remove">True for a template that must go.</param>
    /// <returns>The copy.</returns>
    public SpecData WithoutTemplates(Func<TemplateSpec, bool> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        Dictionary<string, TemplateSpec> kept = new(Templates.Where(t => !remove(t.Value)), StringComparer.Ordinal);
        SpecData copy = (SpecData)MemberwiseClone();
        copy.Templates = kept;
        return copy;
    }

    /// <summary>Gets a copy of this data that also has the given templates (the components a song uses); a native block type is never replaced.</summary>
    /// <param name="extra">The templates to add.</param>
    /// <returns>The copy.</returns>
    public SpecData WithTemplates(IEnumerable<TemplateSpec> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        Dictionary<string, TemplateSpec> merged = new(Templates, StringComparer.Ordinal);
        foreach (TemplateSpec t in extra)
        {
            if (!merged.TryGetValue(t.Id, out TemplateSpec? existing) || existing.Component) merged[t.Id] = t;
        }

        SpecData copy = (SpecData)MemberwiseClone();
        copy.Templates = merged;
        return copy;
    }

    /// <summary>Gets the lint rules by code, in order.</summary>
    public IReadOnlyDictionary<string, LintRuleSpec> LintRules { get; private init; } = new Dictionary<string, LintRuleSpec>();

    /// <summary>Gets the diagnostic catalogue by code.</summary>
    public IReadOnlyDictionary<string, DiagnosticSpec> Diagnostics { get; private init; } = new Dictionary<string, DiagnosticSpec>();

    /// <summary>Gets the opt-in module names.</summary>
    public IReadOnlyList<string> Modules { get; private init; } = [];

    /// <summary>Gets the keywords.</summary>
    public IReadOnlyList<string> Keywords { get; private init; } = [];

    /// <summary>Gets the minimum tempo.</summary>
    public double MinTempo { get; private init; }

    /// <summary>Gets the maximum tempo.</summary>
    public double MaxTempo { get; private init; }

    /// <summary>Gets the maximum seed.</summary>
    public ulong MaxSeed { get; private init; }

    /// <summary>Gets the bar after which a part triggers a warning.</summary>
    public int MaxBar { get; private init; }

    /// <summary>Builds the data from the contents of the files under <c>spec/</c>.</summary>
    /// <param name="files">Maps relative file names (for example <c>diagnostics.json</c> or <c>templates/drums.json</c>) to their text.</param>
    public static SpecData FromFiles(IReadOnlyDictionary<string, string> files)
    {
        using JsonDocument version = JsonDocument.Parse(files["version.json"]);
        using JsonDocument tokens = JsonDocument.Parse(files["tokens.json"]);
        using JsonDocument diagnostics = JsonDocument.Parse(files["diagnostics.json"]);
        using JsonDocument lint = JsonDocument.Parse(files["lint-rules.json"]);

        Dictionary<string, TemplateSpec> templates = [];
        foreach (string name in files.Keys.Where(k => k.StartsWith("templates/", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(files[name]);
            JsonElement t = doc.RootElement;
            Dictionary<string, string> parameters = [];
            Dictionary<string, TemplateParameterSpec> parameterSpecs = [];
            foreach (JsonElement p in t.GetProperty("parameters").EnumerateArray())
            {
                string parameterName = p.GetProperty("name").GetString()!;
                string value = p.GetProperty("default").ToString();
                parameters[parameterName] = value;
                parameterSpecs[parameterName] = new TemplateParameterSpec(parameterName, p.TryGetProperty("label", out JsonElement label) ? label.GetString()! : parameterName, p.GetProperty("type").GetString()!, value, p.TryGetProperty("doc", out JsonElement documentation) ? documentation.GetString() : null, p.TryGetProperty("modalEditor", out JsonElement modalEditor) && modalEditor.GetBoolean(), p.TryGetProperty("multiline", out JsonElement multiline) && multiline.GetBoolean(), p.TryGetProperty("kind", out JsonElement kind) ? kind.GetString()! : p.GetProperty("type").GetString() == "number" ? "number" : "text", p.TryGetProperty("options", out JsonElement options) ? [.. options.EnumerateArray().Select(o => o.GetString()!)] : null, p.TryGetProperty("min", out JsonElement min) ? min.GetDouble() : null, p.TryGetProperty("max", out JsonElement max) ? max.GetDouble() : null, p.TryGetProperty("step", out JsonElement step) ? step.GetDouble() : null, p.TryGetProperty("unit", out JsonElement unit) ? unit.GetString() : null, p.TryGetProperty("placeholder", out JsonElement placeholder) ? placeholder.GetString() : null, p.TryGetProperty("accept", out JsonElement accept) ? [.. accept.EnumerateArray().Select(a => a.GetString()!)] : null, p.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 0, !p.TryGetProperty("affectsOutput", out JsonElement affectsOutput) || affectsOutput.GetBoolean(), p.TryGetProperty("automatable", out JsonElement automatable) && automatable.GetBoolean(), p.TryGetProperty("aliases", out JsonElement aliasList) ? [.. aliasList.EnumerateArray().Select(a => new ParameterAlias(a.GetProperty("name").GetString()!, a.TryGetProperty("convert", out JsonElement conv) ? conv.GetString() : null))] : null);
            }

            string id = t.GetProperty("id").GetString()!;
            templates[id] = new TemplateSpec(
                id,
                t.GetProperty("title").GetString()!,
                t.GetProperty("class").GetString()!,
                t.GetProperty("version").GetString()!,
                t.GetProperty("dynamic").GetBoolean(),
                t.TryGetProperty("plugin", out JsonElement plugin) && plugin.ValueKind == JsonValueKind.String ? plugin.GetString() : null,
                [.. t.GetProperty("inputs").EnumerateArray().Select(p => new PortSpec(p.TryGetProperty("internalName", out JsonElement inner) ? inner.GetString()! : p.GetProperty("name").GetString()!, p.GetProperty("type").GetString()!, p.GetProperty("required").GetBoolean(), p.TryGetProperty("title", out JsonElement ti) ? ti.GetString() ?? string.Empty : string.Empty, p.TryGetProperty("description", out JsonElement de) ? de.GetString() ?? string.Empty : string.Empty, p.GetProperty("name").GetString()!))],
                [.. t.GetProperty("outputs").EnumerateArray().Select(p => new PortSpec(p.TryGetProperty("internalName", out JsonElement inner) ? inner.GetString()! : p.GetProperty("name").GetString()!, p.GetProperty("type").GetString()!, false, p.TryGetProperty("title", out JsonElement ti2) ? ti2.GetString() ?? string.Empty : string.Empty, p.TryGetProperty("description", out JsonElement de2) ? de2.GetString() ?? string.Empty : string.Empty, p.GetProperty("name").GetString()!))],
                parameters,
                parameterSpecs,
                t.GetProperty("description").GetString()!)
            {
                ControlInputs = [.. parameterSpecs.Values.Where(x => x.Automatable).Select(x => new PortSpec(x.Name, ControlTypes.Typed(x.Unit), false, x.Label, "A control that steers " + x.Label + "."))],
                StableSince = t.TryGetProperty("stableSince", out JsonElement stableSince) && stableSince.ValueKind == JsonValueKind.Number ? stableSince.GetInt32() : 1,
                ClientPreview = t.TryGetProperty("clientPreview", out JsonElement clientPreview) && clientPreview.GetBoolean(),
                LivePreview = t.TryGetProperty("livePreview", out JsonElement livePreview) && livePreview.GetBoolean(),
                LatencyMs = t.TryGetProperty("latencyMs", out JsonElement latency) && latency.ValueKind == JsonValueKind.Number ? latency.GetDouble() : 0,
            };
        }

        JsonElement limits = tokens.RootElement.GetProperty("limits");
        return new SpecData
        {
            Version = version.RootElement.GetProperty("specVersion").GetString()!,
            Templates = templates,
            LintRules = lint.RootElement.GetProperty("rules").EnumerateArray().ToDictionary(r => r.GetProperty("code").GetString()!, r => new LintRuleSpec(r.GetProperty("code").GetString()!, r.GetProperty("name").GetString()!, r.GetProperty("defaultLevel").GetString()!, r.GetProperty("description").GetString()!)),
            Diagnostics = diagnostics.RootElement.GetProperty("diagnostics").EnumerateArray().ToDictionary(d => d.GetProperty("code").GetString()!, d => new DiagnosticSpec(d.GetProperty("code").GetString()!, d.GetProperty("severity").GetString()!, d.GetProperty("title").GetString()!, d.GetProperty("explanation").GetString()!, d.GetProperty("stage").GetString()!)),
            Modules = [.. tokens.RootElement.GetProperty("modules").EnumerateArray().Select(e => e.GetString()!)],
            Keywords = [.. tokens.RootElement.GetProperty("keywords").EnumerateArray().Select(e => e.GetString()!)],
            MinTempo = limits.GetProperty("tempo")[0].GetDouble(),
            MaxTempo = limits.GetProperty("tempo")[1].GetDouble(),
            MaxSeed = limits.GetProperty("seed")[1].GetUInt64(),
            MaxBar = limits.GetProperty("maxBar").GetInt32(),
        };
    }
}
