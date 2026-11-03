using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>What an attribute may be written on.</summary>
[Flags]
public enum AttributeTarget
{
    /// <summary>The component itself.</summary>
    Component = 1,

    /// <summary>An input.</summary>
    Input = 2,

    /// <summary>An output.</summary>
    Output = 4,

    /// <summary>A property.</summary>
    Property = 8,

    /// <summary>Anything.</summary>
    Any = 15,
}

/// <summary>One built-in attribute: where it may stand, on which types of property, and how many arguments of what kind it takes. Only these exist (docs/language/19-components.md).</summary>
/// <param name="Name">The attribute name.</param>
/// <param name="Targets">Where it may stand.</param>
/// <param name="Types">The property types it applies to when it stands on a property (null: every type).</param>
/// <param name="MinArgs">The fewest arguments.</param>
/// <param name="MaxArgs">The most arguments (-1: any number).</param>
/// <param name="Kind">What an argument is: number (a number with an optional unit), text (a string), word (a name).</param>
/// <param name="Doc">What it does.</param>
public sealed record AttributeSpec(string Name, AttributeTarget Targets, IReadOnlySet<string>? Types, int MinArgs, int MaxArgs, string Kind, string Doc);

/// <summary>The built-in attributes of components and the check of their use.</summary>
public static class ComponentAttributes
{
    /// <summary>The types that are ordered, so that a minimum, a maximum and a step make sense.</summary>
    public static readonly IReadOnlySet<string> Ordered = new HashSet<string>(StringComparer.Ordinal) { "Int", "Float", "Number", "Percent", "Probability", "Duration", "Length", "Time", "Interval", "Gain", "Pitch", "Octave", "Velocity" };

    private static readonly IReadOnlySet<string> Texts = new HashSet<string>(StringComparer.Ordinal) { "String", "Text" };

    /// <summary>Gets the attributes by name.</summary>
    public static IReadOnlyDictionary<string, AttributeSpec> All { get; } = new Dictionary<string, AttributeSpec>(StringComparer.Ordinal)
    {
        ["MinValue"] = new("MinValue", AttributeTarget.Property, Ordered, 1, 1, "number", "The lowest value the property takes."),
        ["MaxValue"] = new("MaxValue", AttributeTarget.Property, Ordered, 1, 1, "number", "The highest value the property takes."),
        ["Step"] = new("Step", AttributeTarget.Property, Ordered, 1, 1, "number", "How much an arrow of the property panel changes the value."),
        ["Options"] = new("Options", AttributeTarget.Property, Texts, 1, -1, "text", "The values a text property may take; the panel offers them as a choice."),
        ["Editor"] = new("Editor", AttributeTarget.Property, new HashSet<string>(StringComparer.Ordinal) { "Notes" }, 1, 1, "word", "Which editor edits a Notes property (jam)."),
        ["DisplayName"] = new("DisplayName", AttributeTarget.Any, null, 1, 1, "text", "The name shown to people (the palette, the property panel); never part of the identity of the component."),
        ["Description"] = new("Description", AttributeTarget.Any, null, 1, 1, "text", "One sentence of help; shown in the panel and the hover."),
        ["Category"] = new("Category", AttributeTarget.Component, null, 1, 1, "text", "The group of the palette."),
        ["Tags"] = new("Tags", AttributeTarget.Component, null, 1, 16, "text", "Words the library search also looks in (#echo, #space): #[Tags(\"echo\", \"space\")]. Lower case; never part of the identity of the component."),
        ["Widget"] = new("Widget", AttributeTarget.Property, null, 1, 1, "word", "Which widget the layout shows for the property (knob, slider, toggle, choice, jam, curve, readout, field); the shortcut for a ui block when one widget per property is enough."),
        ["Group"] = new("Group", AttributeTarget.Property, null, 1, 1, "text", "The titled box of the layout this property goes in: #[Group(\"Envelope\")]. Neighbours with the same title share one box."),
        ["Tint"] = new("Tint", AttributeTarget.Property, null, 1, 1, "any", "The colour of the widget of this property: accent, green, yellow, orange, red, pink, purple, blue, teal, gray or a text such as \"#ff8800\"."),
        ["LivePreview"] = new("LivePreview", AttributeTarget.Component, null, 0, 0, "none", "Asks for the live preview (the sound changes while a knob moves, in the browser, advisory); the shortcut for preview live in the ui block."),
        ["Experimental"] = new("Experimental", AttributeTarget.Component, null, 0, 0, "none", "Marks a component that may still change in ways its version does not say."),
        ["KodaFact"] = new("KodaFact", AttributeTarget.Any, null, 1, 2, "text", "A fact for the assistant (advisory: it never changes the build). Written as a text, or text=\"...\", appliesTo=\"property\"."),
    };

    /// <summary>The attributes that change nothing about what the component makes (they are in the payload of a seal, not in the semantic hash).</summary>
    public static readonly IReadOnlySet<string> Presentation = new HashSet<string>(StringComparer.Ordinal) { "DisplayName", "Description", "Category", "Tags", "Widget", "Group", "Tint", "LivePreview", "Experimental", "KodaFact", "Editor", "Options" };

    private static readonly Regex Number = new(@"^[+-]?\d+(?:\.\d+)?[A-Za-z%]*$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Checks one attribute against the catalogue.</summary>
    /// <param name="a">The attribute.</param>
    /// <param name="target">Where it stands.</param>
    /// <param name="type">The type of the property it stands on, or null.</param>
    /// <param name="issues">Receives the problems.</param>
    public static void Check(ComponentAnnotation a, AttributeTarget target, string? type, List<ComponentIssue> issues)
    {
        if (!All.TryGetValue(a.Name, out AttributeSpec? spec))
        {
            string? near = All.Keys.Select(k => (k, d: NotePipeline.Distance(a.Name.ToLowerInvariant(), k.ToLowerInvariant()))).Where(x => x.d <= 2).OrderBy(x => x.d).Select(x => x.k).FirstOrDefault();
            issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2205", Severity.Error, $"Unknown attribute \"{a.Name}\"." + (near is null ? string.Empty : $" Did you mean {near}?"), "The attributes are: " + string.Join(", ", All.Keys) + ". Attributes of your own are not allowed."));
            return;
        }

        if ((spec.Targets & target) == 0)
        {
            issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2206", Severity.Error, $"{a.Name} cannot be written on {TargetWord(target)}: it goes on {Where(spec.Targets)}.", string.Empty));
            return;
        }

        string? bare = type?.TrimEnd('?');
        if (target == AttributeTarget.Property && spec.Types is not null && bare is not null && !spec.Types.Contains(bare))
        {
            issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2206", Severity.Error, $"{a.Name} does not apply to a property of type {bare} (it applies to {string.Join(", ", spec.Types.Order(StringComparer.Ordinal))}).", "Use the type that already says it (a Percent for a percentage, a Duration for a length) instead of an attribute."));
            return;
        }

        if (a.Args.Count < spec.MinArgs || (spec.MaxArgs >= 0 && a.Args.Count > spec.MaxArgs))
        {
            issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2207", Severity.Error, $"{a.Name} takes {(spec.MaxArgs == spec.MinArgs ? spec.MinArgs.ToString(CultureInfo.InvariantCulture) : spec.MaxArgs < 0 ? "at least " + spec.MinArgs.ToString(CultureInfo.InvariantCulture) : spec.MinArgs.ToString(CultureInfo.InvariantCulture) + " to " + spec.MaxArgs.ToString(CultureInfo.InvariantCulture))} argument(s), not {a.Args.Count.ToString(CultureInfo.InvariantCulture)}.", string.Empty));
            return;
        }

        foreach (string arg in a.Args)
        {
            string value = arg.Contains('=', StringComparison.Ordinal) && !arg.StartsWith('"') ? arg[(arg.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim() : arg;
            bool ok = spec.Kind switch
            {
                "number" => Number.IsMatch(value),
                "text" => value.Length >= 2 && value[0] == '"' && value[^1] == '"',
                "word" => Regex.IsMatch(value, @"^[A-Za-z_][\w-]*$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                _ => true,
            };
            if (!ok) issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2207", Severity.Error, $"{a.Name} expects {(spec.Kind == "number" ? "a number (with its unit when the type has one)" : spec.Kind == "text" ? "a text in quotes" : "a name")}, not {arg}.", string.Empty));
        }
    }

    private static string TargetWord(AttributeTarget t) => t switch { AttributeTarget.Component => "a component", AttributeTarget.Input => "an input", AttributeTarget.Output => "an output", _ => "a property" };

    private static string Where(AttributeTarget t) => string.Join(", ", new[] { AttributeTarget.Component, AttributeTarget.Input, AttributeTarget.Output, AttributeTarget.Property }.Where(x => (t & x) != 0).Select(TargetWord));
}

/// <summary>The checks of the contract of a component (types, names, defaults, attributes, process) and the template an instance is checked against.</summary>
public static class ComponentChecker
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal) { "process", "input", "output", "property", "ui", "id", "version", "current", "requires", "deprecated", "migrate", "seal", "connected" };

    private static readonly HashSet<string> Capabilities = new(StringComparer.Ordinal) { "clock", "os-random", "environment", "network", "filesystem", "external-process", "external-plugin" };

    /// <summary>Whether a type may be written in the contract of a component: every type of the musical type model and the built-in types, and List and Range of those.</summary>
    /// <param name="type">The type as written.</param>
    /// <returns>True when public.</returns>
    public static bool IsPublicType(string type)
    {
        string t = type.Trim().TrimEnd('?');
        if (IsControlType(t)) return true;
        int open = t.IndexOf('<', StringComparison.Ordinal);
        if (open > 0 && t.EndsWith('>')) return t[..open] is "List" or "Range" && DocTypes.SplitTop(t[(open + 1)..^1], ',').All(IsPublicType);
        return NoteCatalog.Types.ContainsKey(t) || Comments.BuiltinTypes.Contains(t) || t is "Bool" or "Seed" or "RandomSource" or "StableSeed" or "Audio" || IsControlType(t) || t == "Region";
    }

    /// <summary>Checks the contract and the attributes of a component (not the process: the helper checker reads that).</summary>
    /// <param name="d">The component.</param>
    /// <param name="spec">The language data (the native block types are reserved names).</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<ComponentIssue> Check(ComponentDefinition d, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(spec);
        List<ComponentIssue> issues = [];
        issues.AddRange(ComponentMigrations.Check(d));
        int headerEnd = d.HeaderLine;
        if (!d.Name.Contains('.', StringComparison.Ordinal) && spec.Templates.ContainsKey(d.Name))
            issues.Add(new ComponentIssue(d.HeaderLine, 0, d.Name.Length + 10, "AMP2209", Severity.Error, $"\"{d.Name}\" is the name of a native block type: a component cannot take it.", "Give the component a namespace (koda.fx." + d.Name + "); a native name is never shadowed."));
        if (d.IdWritten && !Regex.IsMatch(d.Id, @"^[a-z0-9][a-z0-9.-]*\.[a-z0-9.-]*[a-z0-9]$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2224", Severity.Error, $"The id \"{d.Id}\" is not a canonical id: lower-case letters, digits, dots and hyphens, with at least one dot (demo.koda.effects.canon-maker).", string.Empty));

        // names in the contract: inputs and properties are the parameters of the process, so they share one list of names
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach ((string name, int line, string kind) in d.Inputs.Select(p => (p.Name, p.Line, "input")).Concat(d.Properties.Select(p => (p.Name, p.Line, "property"))))
        {
            if (Words.Contains(name)) issues.Add(new ComponentIssue(line, 0, 1, "AMP2201", Severity.Error, $"\"{name}\" is a word of the component language: it cannot be the name of an {kind}.", string.Empty));
            else if (!taken.Add(name)) issues.Add(new ComponentIssue(line, 0, 1, "AMP2201", Severity.Error, $"The name \"{name}\" is used twice in this component (inputs and properties share their names).", "Rename one of them."));
        }

        foreach (IGrouping<string, ComponentPort> g in d.Outputs.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            issues.Add(new ComponentIssue(g.Skip(1).First().Line, 0, 1, "AMP2201", Severity.Error, $"The output \"{g.Key}\" is declared twice.", string.Empty));

        foreach (ComponentPort p in d.Inputs.Concat(d.Outputs))
        {
            if (!IsPublicType(p.Type)) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2202", Severity.Error, $"\"{p.Type}\" is not a type a component can use for a port.", "Use a type of the musical type model (Notes, Int, Duration ...)."));
            else if (p.Type.TrimEnd('?') is not ("Notes" or "Audio") && p.Type.TrimEnd('?') != "Region" && !IsControlType(p.Type.TrimEnd('?'))) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2219", Severity.Error, $"A port of type {p.Type} is reserved for later: the ports of a component are Notes or Audio. Use a property for a value.", string.Empty));
        }

        // Notes go in and sound comes out (an instrument with its effects); an output that is Notes next to Audio ports is not offered: the process makes audio
        if (d.IsAudio && d.Outputs.Any(p => p.Type.TrimEnd('?') == "Notes"))
            issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2230", Severity.Error, $"The component \"{d.Name}\" has a Notes output next to Audio ports: a component that has Audio ports makes audio. Notes may go IN (an instrument turns them into sound), they do not come out.", "Split it in a Notes component and an Audio component; the song connects them."));
        if (d.IsAudio && d.Inputs.Any(i => i.Fallback is not null))
            issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2230", Severity.Error, "An Audio input has no fallback: leave it unconnected (optional true) and the processors that read it are skipped.", string.Empty));
        if (d.Outputs.Count == 0) issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2208", Severity.Error, $"The component \"{d.Name}\" has no output: add output notes: Notes (or output audio: Audio).", string.Empty));
        else if (d.Outputs.Count > 1 && !d.IsAudio) issues.Add(new ComponentIssue(d.Outputs[1].Line, 0, 1, "AMP2219", Severity.Error, "A component has one output in this version.", "Split it in two components, or make the second a property."));
        if (d.ProcessLine < 0) issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2208", Severity.Error, $"The component \"{d.Name}\" has no process { "{ ... }" }: it says what the component makes.", "Add process { emit source }."));

        foreach (ComponentPort input in d.Inputs.Where(i => i.Fallback is not null))
        {
            ComponentProperty? fallback = d.Property(input.Fallback!);
            if (fallback is null) issues.Add(new ComponentIssue(input.Line, 0, 1, "AMP2226", Severity.Error, $"The fallback of the input \"{input.Name}\" is \"{input.Fallback}\", which is not a property of this component.", "Name a property of type Notes."));
            else if (fallback.Type.TrimEnd('?') != "Notes") issues.Add(new ComponentIssue(input.Line, 0, 1, "AMP2226", Severity.Error, $"The fallback \"{input.Fallback}\" is a {fallback.Type}: the notes that stand in for an input are a property of type Notes.", string.Empty));
            if (input.Fallback is not null && !input.Optional) issues.Add(new ComponentIssue(input.Line, 0, 1, "AMP2226", Severity.Warning, $"The input \"{input.Name}\" has a fallback, so it is optional: write optional true.", string.Empty));
        }

        foreach (string capability in d.Requires.Where(c => !Capabilities.Contains(c)))
            issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2227", Severity.Error, $"\"{capability}\" is not a capability (clock, os-random, environment, network, filesystem, external-process, external-plugin).", string.Empty));

        foreach (ComponentAnnotation a in d.Attributes) ComponentAttributes.Check(a, AttributeTarget.Component, null, issues);
        foreach (ComponentPort p in d.Inputs) foreach (ComponentAnnotation a in p.Attributes) ComponentAttributes.Check(a, AttributeTarget.Input, p.Type, issues);
        foreach (ComponentPort p in d.Outputs) foreach (ComponentAnnotation a in p.Attributes) ComponentAttributes.Check(a, AttributeTarget.Output, p.Type, issues);
        foreach (ComponentProperty p in d.Properties)
        {
            if (!IsPublicType(p.Type)) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2202", Severity.Error, $"\"{p.Type}\" is not a type a component can use for a property.", "Use a type of the musical type model (Int, Percent, Duration, Interval, Notes ...)."));
            foreach (ComponentAnnotation a in p.Attributes) ComponentAttributes.Check(a, AttributeTarget.Property, p.Type, issues);
            if (p.Default is null && p.Type.TrimEnd('?') != "Notes" && !p.Type.EndsWith('?')) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2204", Severity.Error, $"The property \"{p.Name}\" needs a default: property {p.Name}: {p.Type} = ...", "Only a Notes property (or an optional type) may start without a value."));
            CheckRange(p, issues);
        }

        _ = headerEnd;
        ComponentUiReader.Check(d, issues);
        return issues;
    }

    private static void CheckRange(ComponentProperty p, List<ComponentIssue> issues)
    {
        static double? Value(string text)
        {
            Match m = Regex.Match(text.Trim(), @"^([+-]?\d+(?:\.\d+)?)", RegexOptions.None, TimeSpan.FromSeconds(1));
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        double? min = p.Attributes.FirstOrDefault(a => a.Name == "MinValue") is { Args.Count: 1 } lo ? Value(lo.Args[0]) : null;
        double? max = p.Attributes.FirstOrDefault(a => a.Name == "MaxValue") is { Args.Count: 1 } hi ? Value(hi.Args[0]) : null;
        double? step = p.Attributes.FirstOrDefault(a => a.Name == "Step") is { Args.Count: 1 } st ? Value(st.Args[0]) : null;
        if (min is { } a && max is { } b && a > b) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2207", Severity.Error, $"MinValue ({a.ToString(CultureInfo.InvariantCulture)}) is higher than MaxValue ({b.ToString(CultureInfo.InvariantCulture)}) on \"{p.Name}\".", string.Empty));
        if (step is <= 0) issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2207", Severity.Error, $"Step must be more than 0 on \"{p.Name}\".", string.Empty));
        if (p.Default is not null && Value(p.Default) is { } d && ((min is { } lo2 && d < lo2) || (max is { } hi2 && d > hi2)))
            issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2204", Severity.Error, $"The default {p.Default} of \"{p.Name}\" is outside its range.", "Change the default, or the range."));
    }

    /// <summary>Checks the default values of the properties by evaluating them with the type of the property (the expression language that block properties use).</summary>
    /// <param name="d">The component.</param>
    /// <param name="tempo">The tempo of the song as a decimal text.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<ComponentIssue> CheckDefaults(ComponentDefinition d, string tempo)
    {
        ArgumentNullException.ThrowIfNull(d);
        List<ComponentIssue> issues = [];
        foreach (ComponentProperty p in d.Properties.Where(p => p.Default is not null && IsPublicType(p.Type) && p.Type.TrimEnd('?') != "Notes"))
        {
            PropertyResult result = NotePipeline.EvaluateProperty(p.Default!, new NotePipeline.Context { Tempo = tempo }, new PropertyShape(p.Name, p.Type.TrimEnd('?'), null, null, null, null));
            if (result.Errors.Count > 0)
                issues.Add(new ComponentIssue(p.Line, 0, 1, "AMP2204", Severity.Error, $"The default {p.Default} of \"{p.Name}\" is not a {p.Type}: {result.Errors[0].Message}.", string.Empty));
        }

        return issues;
    }

    /// <summary>Makes the template that instances of a component are checked against (and that the palette, the property panel and the completion read).</summary>
    /// <param name="d">The component.</param>
    /// <returns>The template; its id is the exported name.</returns>
    public static TemplateSpec TemplateOf(ComponentDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        string Description(DocComment? doc, IReadOnlyList<ComponentAnnotation> a) => a.FirstOrDefault(x => x.Name == "Description") is { Args.Count: > 0 } x ? x.Args[0].Trim('"') : doc?.Summary ?? string.Empty;
        List<PortSpec> inputs = [.. d.Inputs.Select(p => new PortSpec(p.Name, StreamOf(p.Type), !p.Optional && p.Fallback is null, DisplayOf(p.Attributes, p.Name), Description(p.Doc, p.Attributes)))];
        List<PortSpec> outputs = [.. d.Outputs.Take(d.IsAudio ? 8 : 1).Select(p => new PortSpec(p.Name, StreamOf(p.Type), false, DisplayOf(p.Attributes, p.Name), Description(p.Doc, p.Attributes)))];
        Dictionary<string, string> parameters = [];
        Dictionary<string, TemplateParameterSpec> specs = [];
        foreach (ComponentProperty p in d.Properties)
        {
            string type = p.Type.TrimEnd('?');
            string value = p.Default ?? string.Empty;
            // a length is evaluated in beats: 128bar is 512, not 128 (a bound in a unit that needs the tempo, such as ms, is not checked)
            double? min = Bound(p.Attributes, "MinValue", type), max = Bound(p.Attributes, "MaxValue", type), step = Bound(p.Attributes, "Step", type);
            string[] options = p.Attributes.FirstOrDefault(a => a.Name == "Options") is { } o ? [.. o.Args.Select(x => x.Trim('"'))] : [];
            NoteTypeInfo? info = NoteCatalog.Types.GetValueOrDefault(type);
            string kind = type is "Int" or "Float" or "Number" ? "number" : type == "Bool" ? "choice" : options.Length > 0 ? "choice" : info is { Kind: "vocabulary" } ? "choice" : "text";
            if (type == "Bool") options = ["true", "false"];
            else if (options.Length == 0 && info is { Kind: "vocabulary" }) options = [.. info.Values];
            string label = DisplayOf(p.Attributes, p.Name);
            string doc = Description(p.Doc, p.Attributes);
            parameters[p.Name] = value;
            specs[p.Name] = new TemplateParameterSpec(p.Name, label, type, value, doc.Length == 0 ? null : doc, type == "Notes", type == "Notes", kind, options.Length == 0 ? null : options, min, max, step, null, type == "Notes" ? "notes, for example 1:0:1:60:100 (or connect an input)" : null, null, 0);
        }

        return new TemplateSpec(d.Name, d.Title, "stable", d.Version.ToString(), false, null, inputs, outputs, parameters, specs, Description(d.Doc, d.Attributes).Length > 0 ? Description(d.Doc, d.Attributes) : "A component.") { Component = true };
    }

    /// <summary>A port of a component that carries a control: <c>Control</c> (a level) or <c>Control&lt;Hz&gt;</c> (a value in a unit).</summary>
    public static bool IsControlType(string type) => type == "Control" || (type.StartsWith("Control<", StringComparison.Ordinal) && type.EndsWith('>') && ControlTypes.Units.Contains(type[8..^1]));

    private static string StreamOf(string type)
    {
        string t = type.TrimEnd('?');
        return t == "Audio" ? "audio" : t == "Region" ? "region" : IsControlType(t) ? ControlTypes.Typed(t.StartsWith("Control<", StringComparison.Ordinal) ? t[8..^1] : null) : "midi";
    }

    private static string DisplayOf(IReadOnlyList<ComponentAnnotation> attributes, string fallback) => attributes.FirstOrDefault(a => a.Name == "DisplayName") is { Args.Count: > 0 } a ? a.Args[0].Trim('"') : fallback;

    /// <summary>The number of a range attribute in the unit the evaluator of the property uses: for a length (Duration, Length) bars and beats become beats, other units are not bounds the editor can check without the tempo; every other type keeps the number as written.</summary>
    private static double? Bound(IReadOnlyList<ComponentAnnotation> attributes, string name, string type)
    {
        double? number = Number(attributes, name);
        if (number is null || type is not ("Duration" or "Length")) return number;
        string text = attributes.First(x => x.Name == name).Args[0].Trim();
        Match unit = Regex.Match(text, @"^[+-]?\d+(?:\.\d+)?\s*([A-Za-z]*)$", RegexOptions.None, TimeSpan.FromSeconds(1));
        return unit.Success ? unit.Groups[1].Value.ToLowerInvariant() switch { "bar" or "bars" => number * 4, "beat" or "beats" or "" => number, _ => null } : null;
    }

    private static double? Number(IReadOnlyList<ComponentAnnotation> attributes, string name)
    {
        ComponentAnnotation? a = attributes.FirstOrDefault(x => x.Name == name);
        if (a is not { Args.Count: > 0 }) return null;
        Match m = Regex.Match(a.Args[0], @"^([+-]?\d+(?:\.\d+)?)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
