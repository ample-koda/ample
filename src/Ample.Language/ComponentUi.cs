using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>One item of the layout of a component: a container (<c>row</c>, <c>column</c>, <c>group</c>) or a widget bound to a property.</summary>
/// <param name="Kind">The word: row, column, group, knob, slider, toggle, choice, xy, curve, meter, readout, jam or field.</param>
/// <param name="Property">The property (or, for a meter, the output) the widget is bound to; for xy the x property; null for a container.</param>
/// <param name="Second">For xy the y property, else null.</param>
/// <param name="Label">The label that replaces the property name, or the title of a group; null when none was written.</param>
/// <param name="Size">The size word (s, m, l), or null.</param>
/// <param name="Children">The items of a container.</param>
/// <param name="Line">The 0-based line of the word.</param>
/// <param name="Col">The 0-based start column.</param>
/// <param name="EndCol">The 0-based end column (exclusive).</param>
/// <param name="Tint">The colour of the widget or the group: a name of <see cref="ComponentUiCatalog.Colors"/> or <c>#rrggbb</c>, or null.</param>
public sealed record ComponentUiNode(string Kind, string? Property, string? Second, string? Label, string? Size, IReadOnlyList<ComponentUiNode> Children, int Line, int Col, int EndCol, string? Tint = null);

/// <summary>The layout of a component: what the properties panel shows, the compact face of the board card, and whether a live preview is asked for. Presentation only: nothing here changes what the component makes.</summary>
/// <param name="Panel">The items of the properties panel (and of the popup), in order.</param>
/// <param name="Card">The items of the board card (the <c>card</c> block), or none.</param>
/// <param name="Preview">The live preview: <c>live</c> (asked for), <c>off</c> (said no) or empty (not said).</param>
public sealed record ComponentUi(IReadOnlyList<ComponentUiNode> Panel, IReadOnlyList<ComponentUiNode> Card, string Preview)
{
    /// <summary>Gets a layout with nothing in it.</summary>
    public static ComponentUi Empty { get; } = new([], [], string.Empty);

    /// <summary>Gets whether the layout says anything.</summary>
    public bool IsEmpty => Panel.Count == 0 && Card.Count == 0 && Preview.Length == 0;
}

/// <summary>What control fits a property, derived from its type and attributes (one place: the catalog for the web and the checker of the layout read the same).</summary>
/// <param name="Kind">number, choice, toggle, notes or text.</param>
/// <param name="Unit">The unit a number is written with (%, dB, Hz), or null.</param>
/// <param name="Min">The lowest value (MinValue), or null.</param>
/// <param name="Max">The highest value (MaxValue), or null.</param>
/// <param name="Step">The size of a step (Step), or null.</param>
/// <param name="Options">The values of a choice (Options).</param>
public sealed record ComponentControl(string Kind, string? Unit, double? Min, double? Max, double? Step, IReadOnlyList<string> Options)
{
    private static readonly HashSet<string> Numbers = new(StringComparer.Ordinal) { "Int", "Float", "Number", "Velocity", "Octave", "Percent", "Probability", "Gain", "Frequency" };

    /// <summary>Works out the control of a property.</summary>
    /// <param name="p">The property.</param>
    /// <returns>The control.</returns>
    public static ComponentControl Of(ComponentProperty p)
    {
        ArgumentNullException.ThrowIfNull(p);
        string type = p.Type.TrimEnd('?');
        string[] options = [.. p.Attributes.Where(a => a.Name == "Options").SelectMany(a => a.Args).Select(x => x.Trim().Trim('"'))];
        double? Number(string name) => p.Attributes.FirstOrDefault(a => a.Name == name) is { Args.Count: > 0 } a && Regex.Match(a.Args[0].Trim(), @"^[+-]?\d+(?:\.\d+)?", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } m ? double.Parse(m.Value, CultureInfo.InvariantCulture) : null;
        string kind = options.Length > 0 ? "choice" : type == "Bool" ? "toggle" : type == "Notes" ? "notes" : Numbers.Contains(type) ? "number" : "text";
        string? unit = kind != "number" ? null : type switch { "Percent" => "%", "Gain" => "dB", "Frequency" => "Hz", _ => null };
        return new ComponentControl(kind, unit, kind == "number" ? Number("MinValue") : null, kind == "number" ? Number("MaxValue") : null, kind == "number" ? Number("Step") : null, options);
    }
}

/// <summary>The words of the layout language: one catalog for the reader, the checker, the completion and the documentation.</summary>
public static class ComponentUiCatalog
{
    /// <summary>The containers.</summary>
    public static IReadOnlyDictionary<string, string> Containers { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["row"] = "Puts its items side by side.",
        ["column"] = "Puts its items one under the other.",
        ["group"] = "A titled box: group \"Envelope\" { ... }.",
        ["card"] = "The compact face on the board card (top level only): card { knob cutoff  knob mix }.",
    };

    /// <summary>The widgets, with what they are bound to.</summary>
    public static IReadOnlyDictionary<string, string> Widgets { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["knob"] = "A knob for a number property: knob cutoff [size s|m|l] [label \"Cutoff\"]. The range comes from MinValue and MaxValue.",
        ["slider"] = "A slider for a number property: slider mix [label \"Wet\"].",
        ["toggle"] = "An on/off switch for a Bool property: toggle bypass.",
        ["choice"] = "A choice for a property with Options (or a Bool): choice mode.",
        ["xy"] = "An XY pad over two number properties: xy x=cutoff y=resonance.",
        ["curve"] = "A curve editor for a text property that holds points, written x:y pairs from 0 to 1 (\"0:0 0.5:1 1:0\"): curve shape.",
        ["meter"] = "A level meter on an audio output of the component: meter audio.",
        ["readout"] = "A read-only value of a property: readout latency.",
        ["jam"] = "The note editor button for a Notes property: jam melody.",
        ["field"] = "The standard field of the panel for a property of any type: field name.",
    };

    /// <summary>The options a widget can have after its property.</summary>
    public static IReadOnlySet<string> Options { get; } = new HashSet<string>(StringComparer.Ordinal) { "size", "label", "tint" };

    /// <summary>The colour names a widget or a group can be tinted with (the web draws them in the theme's own shades).</summary>
    public static IReadOnlySet<string> Colors { get; } = new HashSet<string>(StringComparer.Ordinal) { "accent", "green", "yellow", "orange", "red", "pink", "purple", "blue", "teal", "gray" };

    /// <summary>Whether a text is a colour a layout can use: a name of the list or <c>#rrggbb</c>.</summary>
    /// <param name="text">The colour as written.</param>
    /// <returns>True when it is one.</returns>
    public static bool IsColor(string text) => Colors.Contains(text) || Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>The sizes of a widget.</summary>
    public static IReadOnlySet<string> Sizes { get; } = new HashSet<string>(StringComparer.Ordinal) { "s", "m", "l" };

    /// <summary>The deepest nesting of containers.</summary>
    public const int MaxDepth = 4;

    /// <summary>The most items in one layout.</summary>
    public const int MaxNodes = 64;

    /// <summary>Whether a widget fits a property.</summary>
    /// <param name="widget">The widget word.</param>
    /// <param name="control">The control of the property.</param>
    /// <returns>True when it fits.</returns>
    public static bool Fits(string widget, ComponentControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return widget switch
        {
            "knob" or "slider" or "xy" => control.Kind == "number",
            "toggle" => control.Kind == "toggle",
            "choice" => control.Kind is "choice" or "toggle",
            "jam" => control.Kind == "notes",
            "curve" => control.Kind == "text",
            _ => true,
        };
    }

    /// <summary>Gets what a widget needs, in words, for a message.</summary>
    /// <param name="widget">The widget word.</param>
    /// <returns>For example "a number property".</returns>
    public static string Needs(string widget) => widget switch
    {
        "knob" or "slider" or "xy" => "a number property (Int, Float, Percent, Gain, Frequency, ...)",
        "toggle" => "a Bool property",
        "choice" => "a property with Options or a Bool",
        "jam" => "a Notes property",
        "curve" => "a text property",
        _ => "a property",
    };
}

/// <summary>Reads the <c>ui { }</c> block of a component into a tree, and works out the layout a component really has (the block, or one made from the attributes of its properties).</summary>
public static class ComponentUiReader
{
    private sealed record Token(string Text, bool IsString, int Line, int Col, int EndCol);

    private static List<Token> Tokenize(IReadOnlyList<string> lines, int from, int to)
    {
        List<Token> tokens = [];
        for (int i = from; i <= to && i < lines.Count; i++)
        {
            string line = lines[i];
            int start = 0, end = line.Length;
            if (i == from)
            {
                int open = line.IndexOf('{', StringComparison.Ordinal);
                if (open < 0) return tokens;
                start = open + 1;
            }

            if (i == to)
            {
                int close = line.LastIndexOf('}');
                if (close >= start) end = close;
            }

            for (int c = start; c < end;)
            {
                char ch = line[c];
                if (char.IsWhiteSpace(ch)) { c++; continue; }
                if (ch == '#') break;
                if (ch == '"')
                {
                    int k = c + 1;
                    System.Text.StringBuilder text = new();
                    while (k < end && line[k] != '"') { if (line[k] == '\\' && k + 1 < end) k++; text.Append(line[k]); k++; }
                    tokens.Add(new Token(text.ToString(), true, i, c, Math.Min(k + 1, line.Length)));
                    c = k + 1;
                    continue;
                }

                if (ch is '{' or '}' or '=') { tokens.Add(new Token(ch.ToString(), false, i, c, c + 1)); c++; continue; }
                int w = c;
                while (w < end && !char.IsWhiteSpace(line[w]) && line[w] is not ('{' or '}' or '=' or '"' or '#')) w++;
                tokens.Add(new Token(line[c..w], false, i, c, w));
                c = w;
            }
        }

        return tokens;
    }

    /// <summary>Reads the block that starts at the line of the <c>ui</c> word and ends at <paramref name="to"/>.</summary>
    /// <param name="lines">The lines of the file (comments are not blanked: a # starts a comment).</param>
    /// <param name="from">The 0-based line of the <c>ui</c> word.</param>
    /// <param name="to">The 0-based line of the closing brace.</param>
    /// <param name="issues">Receives the problems of the syntax.</param>
    /// <returns>The layout.</returns>
    public static ComponentUi Read(IReadOnlyList<string> lines, int from, int to, List<ComponentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(issues);
        List<Token> tokens = Tokenize(lines, from, to);
        int at = 0, count = 0;
        string preview = string.Empty;
        List<ComponentUiNode> panel = [];
        List<ComponentUiNode> card = [];

        void Problem(Token t, string message, string hint = "") => issues.Add(new ComponentIssue(t.Line, t.Col, t.EndCol, "AMP2240", Severity.Error, message, hint));

        string? Word(string what)
        {
            if (at < tokens.Count && !tokens[at].IsString && tokens[at].Text is not ("{" or "}" or "="))
            {
                return tokens[at++].Text;
            }

            if (at < tokens.Count) Problem(tokens[at], $"Expected {what}.");
            return null;
        }

        List<ComponentUiNode> Body(int depth, bool topLevel)
        {
            List<ComponentUiNode> nodes = [];
            while (at < tokens.Count)
            {
                Token t = tokens[at];
                if (t.Text == "}" && !t.IsString) return nodes;
                if (t.IsString || t.Text is "{" or "=")
                {
                    Problem(t, $"Unexpected \"{t.Text}\" in the layout.", "An item is a widget (knob cutoff) or a container (row { ... }).");
                    at++;
                    continue;
                }

                at++;
                if (++count > ComponentUiCatalog.MaxNodes)
                {
                    if (count == ComponentUiCatalog.MaxNodes + 1) issues.Add(new ComponentIssue(t.Line, t.Col, t.EndCol, "AMP2243", Severity.Error, $"A layout has at most {ComponentUiCatalog.MaxNodes} items.", "Leave out what nobody touches, or put it in a second component."));
                    continue;
                }

                if (t.Text == "preview")
                {
                    Token? value = at < tokens.Count ? tokens[at] : null;
                    if (value is { IsString: false, Text: "live" or "off" }) { preview = value.Text; at++; }
                    else Problem(t, "Write preview live or preview off.");
                    continue;
                }

                if (ComponentUiCatalog.Containers.ContainsKey(t.Text))
                {
                    string? label = null;
                    if (t.Text == "group")
                    {
                        if (at < tokens.Count && tokens[at].IsString) label = tokens[at++].Text;
                        else Problem(t, "A group has a title: group \"Envelope\" { ... }.");
                    }

                    string? containerTint = null;
                    if (t.Text == "group" && at + 1 < tokens.Count && tokens[at].Text == "tint" && !tokens[at].IsString)
                    {
                        Token option = tokens[at++];
                        if (ComponentUiCatalog.IsColor(tokens[at].Text)) containerTint = tokens[at++].Text;
                        else issues.Add(new ComponentIssue(option.Line, option.Col, option.EndCol, "AMP2246", Severity.Error, "A tint is a colour: " + string.Join(", ", ComponentUiCatalog.Colors.Order(StringComparer.Ordinal)) + " or a text such as \"#ff8800\".", string.Empty));
                    }

                    if (at >= tokens.Count || tokens[at].Text != "{" || tokens[at].IsString) { Problem(t, $"{t.Text} is followed by braces: {t.Text} {{ ... }}."); continue; }
                    at++;
                    if (t.Text == "card" && !topLevel) Problem(t, "card goes at the top level of the ui block, not inside another container.");
                    if (depth >= ComponentUiCatalog.MaxDepth)
                    {
                        issues.Add(new ComponentIssue(t.Line, t.Col, t.EndCol, "AMP2243", Severity.Error, $"Containers are nested at most {ComponentUiCatalog.MaxDepth} deep.", string.Empty));
                    }

                    List<ComponentUiNode> children = Body(depth + 1, false);
                    if (at < tokens.Count && tokens[at].Text == "}" && !tokens[at].IsString) at++;
                    else Problem(t, $"The {t.Text} is never closed: add a }}.");
                    ComponentUiNode container = new(t.Text, null, null, label, null, children, t.Line, t.Col, t.EndCol, containerTint);
                    if (t.Text == "card" && topLevel) card.AddRange(children);
                    else nodes.Add(container);
                    continue;
                }

                if (ComponentUiCatalog.Widgets.ContainsKey(t.Text))
                {
                    string? property, second = null;
                    if (t.Text == "xy")
                    {
                        property = Axis("x");
                        second = Axis("y");
                    }
                    else property = Word($"the property for the {t.Text}");

                    string? label = null, size = null, tint = null;
                    while (at < tokens.Count && !tokens[at].IsString && ComponentUiCatalog.Options.Contains(tokens[at].Text))
                    {
                        Token option = tokens[at++];
                        if (option.Text == "label" && at < tokens.Count && tokens[at].IsString) label = tokens[at++].Text;
                        else if (option.Text == "size" && at < tokens.Count && ComponentUiCatalog.Sizes.Contains(tokens[at].Text)) size = tokens[at++].Text;
                        else if (option.Text == "tint" && at < tokens.Count && ComponentUiCatalog.IsColor(tokens[at].Text)) tint = tokens[at++].Text;
                        else if (option.Text == "tint") issues.Add(new ComponentIssue(option.Line, option.Col, option.EndCol, "AMP2246", Severity.Error, "A tint is a colour: " + string.Join(", ", ComponentUiCatalog.Colors.Order(StringComparer.Ordinal)) + " or a text such as \"#ff8800\".", string.Empty));
                        else Problem(option, option.Text == "size" ? "A size is s, m or l." : "A label is a text in quotes.");
                    }

                    if (property is not null) nodes.Add(new ComponentUiNode(t.Text, property, second, label, size, [], t.Line, t.Col, t.EndCol, tint));
                    continue;
                }

                Problem(t, $"\"{t.Text}\" is not a word of the layout.", "Widgets: " + string.Join(", ", ComponentUiCatalog.Widgets.Keys) + ". Containers: " + string.Join(", ", ComponentUiCatalog.Containers.Keys) + ". And preview live.");
            }

            return nodes;
        }

        string? Axis(string axis)
        {
            if (at + 2 < tokens.Count && tokens[at].Text == axis && tokens[at + 1].Text == "=" && !tokens[at + 2].IsString)
            {
                at += 2;
                return tokens[at++].Text;
            }

            if (at < tokens.Count) Problem(tokens[at], $"An xy needs both axes: xy x=cutoff y=resonance (missing {axis}=).");
            return null;
        }

        while (at < tokens.Count)
        {
            int before = at;
            panel.AddRange(Body(1, true));
            if (at < tokens.Count && tokens[at].Text == "}" && !tokens[at].IsString) { Problem(tokens[at], "A } without a container to close."); at++; }
            if (at == before) at++;
        }

        return new ComponentUi(panel, card, preview);
    }

    /// <summary>Gets the layout a component really has: its <c>ui</c> block when it has one, else one made from the attributes of its properties (<c>#[Widget(knob)]</c>, <c>#[Group("Envelope")]</c>; a property without them is a plain <c>field</c>). <c>#[LivePreview]</c> asks for the live preview when the block does not say.</summary>
    /// <param name="d">The component.</param>
    /// <returns>The layout.</returns>
    public static ComponentUi Effective(ComponentDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        ComponentUi written = d.UiTree ?? ComponentUi.Empty;
        string preview = written.Preview.Length > 0 ? written.Preview : d.Attributes.Any(a => a.Name == "LivePreview") ? "live" : string.Empty;
        if (written.Panel.Count > 0) return written with { Preview = preview };
        List<ComponentUiNode> panel = [];
        string? group = null;
        List<ComponentUiNode>? members = null;
        foreach (ComponentProperty p in d.Properties)
        {
            string? widget = p.Attributes.FirstOrDefault(a => a.Name == "Widget") is { Args.Count: > 0 } w ? w.Args[0].Trim() : null;
            string? title = p.Attributes.FirstOrDefault(a => a.Name == "Group") is { Args.Count: > 0 } g ? g.Args[0].Trim().Trim('"') : null;
            string? tint = p.Attributes.FirstOrDefault(a => a.Name == "Tint") is { Args.Count: > 0 } ti && ComponentUiCatalog.IsColor(ti.Args[0].Trim().Trim('"')) ? ti.Args[0].Trim().Trim('"') : null;
            ComponentUiNode node = new(widget is not null && ComponentUiCatalog.Widgets.ContainsKey(widget) ? widget : "field", p.Name, null, null, null, [], p.Line, 0, 0, tint);
            if (title is null) { group = null; members = null; panel.Add(node); continue; }
            if (title != group || members is null)
            {
                group = title;
                members = [];
                panel.Add(new ComponentUiNode("group", null, null, title, null, members, p.Line, 0, 0));
            }

            members.Add(node);
        }

        return new ComponentUi(panel, written.Card, preview);
    }

    /// <summary>Checks the layout against the contract: the properties and outputs it names, whether a widget fits the type, the widget attributes, and a live preview that cannot be.</summary>
    /// <param name="d">The component.</param>
    /// <param name="issues">Receives the problems.</param>
    public static void Check(ComponentDefinition d, List<ComponentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(issues);
        Dictionary<string, ComponentProperty> properties = d.Properties.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (ComponentProperty p in d.Properties)
        {
            foreach (ComponentAnnotation a in p.Attributes.Where(a => a.Name == "Tint" && a.Args.Count > 0 && !ComponentUiCatalog.IsColor(a.Args[0].Trim().Trim('"'))))
                issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2246", Severity.Error, $"\"{a.Args[0].Trim().Trim('"')}\" is not a colour.", "Use " + string.Join(", ", ComponentUiCatalog.Colors.Order(StringComparer.Ordinal)) + " or a text such as \"#ff8800\"."));
            foreach (ComponentAnnotation a in p.Attributes.Where(a => a.Name == "Widget" && a.Args.Count > 0))
            {
                string widget = a.Args[0].Trim();
                if (!ComponentUiCatalog.Widgets.ContainsKey(widget)) issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2240", Severity.Error, $"\"{widget}\" is not a widget.", "The widgets are: " + string.Join(", ", ComponentUiCatalog.Widgets.Keys) + "."));
                else if (!ComponentUiCatalog.Fits(widget, ComponentControl.Of(p))) issues.Add(new ComponentIssue(a.Line, a.Col, a.Col + a.Name.Length, "AMP2242", Severity.Error, $"A {widget} needs {ComponentUiCatalog.Needs(widget)}; \"{p.Name}\" is {p.Type}.", "Pick a widget that fits the type, or use field."));
            }
        }

        HashSet<string> shown = new(StringComparer.Ordinal);
        void Walk(IEnumerable<ComponentUiNode> nodes, bool panel)
        {
            foreach (ComponentUiNode n in nodes)
            {
                if (ComponentUiCatalog.Containers.ContainsKey(n.Kind)) { Walk(n.Children, panel); continue; }
                foreach (string? name in n.Kind == "xy" ? new[] { n.Property, n.Second } : [n.Property])
                {
                    if (name is null) continue;
                    if (n.Kind == "meter")
                    {
                        if (!d.Outputs.Any(o => o.Name == name)) issues.Add(new ComponentIssue(n.Line, n.Col, n.EndCol, "AMP2241", Severity.Error, $"A meter shows an output of the component; there is no output \"{name}\".", "Outputs: " + string.Join(", ", d.Outputs.Select(o => o.Name)) + "."));
                        continue;
                    }

                    if (!properties.TryGetValue(name, out ComponentProperty? p))
                    {
                        if (n.Kind == "readout" && d.Outputs.Concat(d.Inputs).Any(o => o.Name == name)) continue;
                        string? near = properties.Keys.Select(k => (k, dist: NotePipeline.Distance(name.ToLowerInvariant(), k.ToLowerInvariant()))).Where(x => x.dist <= 2).OrderBy(x => x.dist).Select(x => x.k).FirstOrDefault();
                        issues.Add(new ComponentIssue(n.Line, n.Col, n.EndCol, "AMP2241", Severity.Error, $"There is no property \"{name}\" in this component." + (near is null ? string.Empty : $" Did you mean {near}?"), "Properties: " + string.Join(", ", properties.Keys) + "."));
                        continue;
                    }

                    if (!ComponentUiCatalog.Fits(n.Kind, ComponentControl.Of(p))) issues.Add(new ComponentIssue(n.Line, n.Col, n.EndCol, "AMP2242", Severity.Error, $"A {n.Kind} needs {ComponentUiCatalog.Needs(n.Kind)}; \"{name}\" is {p.Type}.", "Pick a widget that fits the type, or use field."));
                    else if (panel && n.Kind != "readout" && !shown.Add(name)) issues.Add(new ComponentIssue(n.Line, n.Col, n.EndCol, "AMP2244", Severity.Warning, $"\"{name}\" is in the layout twice: both widgets edit the same value.", "Keep one, or make the second a readout."));
                }
            }
        }

        if (d.UiTree is { } written)
        {
            Walk(written.Panel, panel: true);
            Walk(written.Card, panel: false);
        }

        if (Effective(d).Preview == "live" && !d.IsAudio)
            issues.Add(new ComponentIssue(d.HeaderLine, 0, 1, "AMP2245", Severity.Info, "A live preview is for audio components: a Notes component is not played while a knob moves.", "Take preview live out, or make the component an audio component."));
    }
}
