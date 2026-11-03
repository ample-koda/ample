using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>One processor in the internal graph of an audio component.</summary>
/// <param name="Id">The stable node id: the processor name and its number in the source (<c>delay1</c>).</param>
/// <param name="Processor">The processor name in the pipeline (<c>delay</c>, <c>gain</c>, <c>mix</c>).</param>
/// <param name="Template">The block type it becomes.</param>
/// <param name="Class">The update class of the block header.</param>
/// <param name="Version">The template version of the block header.</param>
/// <param name="Parameters">The block parameters as they are written (<c>time 250 ms</c>), in order.</param>
/// <param name="Inputs">The audio inputs: the block port and where it comes from (<c>in:audio</c> for an input of the component, <c>node:delay1</c> for another node).</param>
/// <param name="OutputPort">The output port of the block (<c>out</c>, <c>mix</c> for a bus).</param>
/// <param name="Line">The 0-based line of the call in the source.</param>
public sealed record AudioNode(string Id, string Processor, string Template, string Class, string Version, IReadOnlyList<(string Name, string Value)> Parameters, IReadOnlyList<(string Port, string Source)> Inputs, string OutputPort, int Line);

/// <summary>The internal graph of an audio component: a reusable typed subgraph. The user sees one block; the engine keeps this.</summary>
/// <param name="Nodes">The processors that an output needs, in the order they are made.</param>
/// <param name="Outputs">For each output of the component, where it comes from (<c>node:reverb1</c> or <c>in:audio</c>).</param>
/// <param name="Issues">The problems found.</param>
/// <param name="LatencyMs">The latency of the longest path (not compensated).</param>
/// <param name="ClientPreview">Whether every processor can be previewed in the browser.</param>
/// <param name="NoPreview">The processors that cannot.</param>
public sealed record AudioGraph(IReadOnlyList<AudioNode> Nodes, IReadOnlyDictionary<string, string> Outputs, IReadOnlyList<ComponentIssue> Issues, double LatencyMs, bool ClientPreview, IReadOnlyList<string> NoPreview)
{
    /// <summary>Gets the dependencies a semantic hash must contain: every processor with the version of its block.</summary>
    public IReadOnlyList<string> Dependencies => [.. Nodes.Select(n => $"processor={n.Template}@{n.Version}").Concat(NestedDependencies).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Gets the audio components this one calls (<c>component=name@version:semantic hash</c>): their code is part of the hash of this one, so a change in a nested component is a change here (and breaks a seal).</summary>
    public IReadOnlyList<string> NestedDependencies { get; init; } = [];

    /// <summary>Gets whether the browser can run the whole graph in real time (every processor has a live chain; a mix is a sum).</summary>
    public bool LivePreview { get; init; }

    /// <summary>Gets the processors that have no real-time chain.</summary>
    public IReadOnlyList<string> NoLive { get; init; } = [];

    /// <summary>Gets the node whose sound the server renders and the live preview loops (the one instrument of a component with Notes in; the effects after it run live), or null.</summary>
    public string? LiveSource { get; init; }
}

/// <summary>Reads the <c>process</c> of an audio component into a graph of processors. The pipeline operator is the same <c>|&gt;</c> as for Notes, typed by what is in front of it: Audio goes into the processors of the catalog. Nothing is rendered: the graph only describes what the build will do.</summary>
public static partial class AudioGraphBuilder
{
    /// <summary>The most processors one audio component can have.</summary>
    public const int MaxNodes = 64;

    [GeneratedRegex(@"^[A-Za-z_][\w-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();

    private sealed record Statement(int Line, string Kind, string Text);

    private sealed class State
    {
        public required ComponentDefinition Definition { get; init; }
        public required IReadOnlyDictionary<string, AudioProcessor> Catalog { get; init; }
        public IReadOnlyDictionary<string, AudioProcessor> Instruments { get; init; } = new Dictionary<string, AudioProcessor>(StringComparer.Ordinal);
        public HashSet<string> NotesSources { get; } = new(StringComparer.Ordinal);
        public required SpecData Spec { get; init; }
        public required Dictionary<string, string> Scope { get; init; }
        public required double Tempo { get; init; }
        public IReadOnlyList<ComponentDefinition> Visible { get; init; } = [];
        public int Depth { get; init; }
        public IReadOnlyList<string> Chain { get; init; } = [];
        public List<string> NestedDeps { get; } = [];
        public List<AudioNode> Nodes { get; } = [];
        public Dictionary<string, string> AudioNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        public List<ComponentIssue> Issues { get; } = [];
        public int Line { get; set; }
    }

    private static string Strip(string line)
    {
        bool inString = false;
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '"' && (k == 0 || line[k - 1] != '\\')) inString = !inString;
            else if (!inString && line[k] == '#') return line[..k];
        }

        return line;
    }

    private static List<Statement> Statements(ComponentDefinition d, List<ComponentIssue> issues)
    {
        List<Statement> result = [];
        for (int i = 0; i < d.ProcessBody.Count; i++)
        {
            string line = Strip(d.ProcessBody[i]).Trim();
            int absolute = d.ProcessLine + 1 + i;
            if (line.Length == 0) continue;
            // if (condition) { ... } else if (condition) { ... } else { ... }: the branch that is taken is known when the graph is built, because the properties are
            if (Regex.Match(line, @"^if\s*\((.+)\)\s*\{$", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } openIf) { result.Add(new Statement(absolute, "if", openIf.Groups[1].Value.Trim())); continue; }
            if (Regex.Match(line, @"^\}\s*else\s+if\s*\((.+)\)\s*\{$", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } elseIf) { result.Add(new Statement(absolute, "elseif", elseIf.Groups[1].Value.Trim())); continue; }
            if (Regex.IsMatch(line, @"^\}\s*else\s*\{$", RegexOptions.None, TimeSpan.FromSeconds(1))) { result.Add(new Statement(absolute, "else", string.Empty)); continue; }
            if (line == "}") { result.Add(new Statement(absolute, "end", string.Empty)); continue; }
            if (line.StartsWith("let ", StringComparison.Ordinal)) result.Add(new Statement(absolute, "let", line[4..].Trim()));
            else if (line.StartsWith("emit ", StringComparison.Ordinal)) result.Add(new Statement(absolute, "emit", line[5..].Trim()));
            else if (result.Count > 0 && (line.StartsWith("|>", StringComparison.Ordinal) || line.StartsWith(')') || line.StartsWith(',') || result[^1].Text.EndsWith("|>", StringComparison.Ordinal) || result[^1].Text.EndsWith('(') || result[^1].Text.EndsWith(',')))
                result[^1] = result[^1] with { Text = result[^1].Text + " " + line };
            else issues.Add(new ComponentIssue(absolute, 0, d.ProcessBody[i].Length, "AMP2231", Severity.Error, $"\"{line}\" is not a statement of an audio process (let name = ..., emit ...).", "Write let name = audio |> processor(...) or emit audio |> processor(...)."));
        }

        return result;
    }

    private static string FormatNumber(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Reads the process of an audio component.</summary>
    /// <param name="d">The component.</param>
    /// <param name="spec">The language data (the block templates are the processors).</param>
    /// <param name="values">The value of every property (an instance's, or the defaults).</param>
    /// <param name="tempo">The tempo of the song, to turn beats and bars into milliseconds.</param>
    /// <returns>The graph and the problems.</returns>
    /// <param name="visible">The components that can be called as a processor (nested components), or null.</param>
    /// <param name="depth">How deep this graph is nested (the outermost is 0).</param>
    /// <param name="chain">The names of the components that are being built around this one, outermost first (a component that calls one of them is a circle).</param>
    public static AudioGraph Build(ComponentDefinition d, SpecData spec, IReadOnlyDictionary<string, string> values, double tempo, IReadOnlyList<ComponentDefinition>? visible = null, int depth = 0, IReadOnlyList<string>? chain = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(values);
        State st = new() { Definition = d, Catalog = AudioProcessors.Catalog(spec), Instruments = AudioProcessors.Instruments(spec), Spec = spec, Scope = new Dictionary<string, string>(values, StringComparer.Ordinal), Tempo = tempo > 0 ? tempo : 120, Visible = visible ?? [], Depth = depth, Chain = [.. chain ?? [], d.Name] };
        foreach (ComponentPort input in d.Inputs.Where(i => i.Type.TrimEnd('?') == "Audio")) st.AudioNames[input.Name] = "in:" + input.Name;
        // a Notes input is a value of its own kind: only an instrument turns it into audio
        foreach (ComponentPort input in d.Inputs.Where(i => i.Type.TrimEnd('?') == "Notes")) { st.AudioNames[input.Name] = "in:" + input.Name; st.NotesSources.Add("in:" + input.Name); }
        foreach (ComponentProperty p in d.Properties) if (!st.Scope.ContainsKey(p.Name) && p.Default is not null) st.Scope[p.Name] = p.Default;
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        List<Statement> statements = Statements(d, st.Issues);
        Stack<(bool ParentActive, bool Taken, bool Active)> branches = new();
        bool? Truth(string condition, int line)
        {
            string? value = ComponentMigrations.EvaluateScoped(condition, st.Scope, out string? why);
            if (value is "true") return true;
            if (value is "false") return false;
            if (value is not null && double.TryParse(new string(value.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number != 0;
            st.Issues.Add(new ComponentIssue(line, 0, 1, "AMP2232", Severity.Error, $"The condition of an if cannot be read: {why ?? value}. Use a Bool property, a number (0 is false) or a calculation.", condition));
            return null;
        }

        foreach (Statement s in statements)
        {
            st.Line = s.Line;
            if (s.Kind is "if" or "elseif" or "else" or "end")
            {
                if (s.Kind == "if")
                {
                    bool parent = branches.Count == 0 || branches.Peek().Active;
                    bool take = parent && Truth(s.Text, s.Line) == true;
                    branches.Push((parent, take, take));
                }
                else if (branches.Count == 0) st.Issues.Add(new ComponentIssue(s.Line, 0, 1, "AMP2231", Severity.Error, s.Kind == "end" ? "A } closes nothing here." : $"{s.Kind} comes without an if before it.", string.Empty));
                else if (s.Kind == "end") branches.Pop();
                else
                {
                    (bool parent, bool taken, _) = branches.Pop();
                    bool take = parent && !taken && (s.Kind == "else" || Truth(s.Text, s.Line) == true);
                    branches.Push((parent, taken || take, take));
                }

                continue;
            }

            if (branches.Count > 0 && !branches.Peek().Active) continue;
            if (s.Kind == "let")
            {
                Match m = Regex.Match(s.Text, @"^([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
                if (!m.Success) { st.Issues.Add(Issue(st, "AMP2231", "A let is written let name = expression.", s.Text)); continue; }
                string name = m.Groups[1].Value, text = m.Groups[2].Value.Trim();
                if (LooksAudio(st, text))
                {
                    string? source = Pipe(st, text);
                    if (source is not null) st.AudioNames[name] = source;
                }
                else
                {
                    string? value = ComponentMigrations.EvaluateScoped(text, st.Scope, out string? failure);
                    if (value is null) st.Issues.Add(Issue(st, "AMP2232", $"The value of {name} cannot be calculated: {failure}", text));
                    else st.Scope[name] = value;
                }

                continue;
            }

            string target = string.Empty;
            string expression = s.Text;
            int arrow = expression.LastIndexOf("->", StringComparison.Ordinal);
            if (arrow > 0 && Word().IsMatch(expression[(arrow + 2)..].Trim()))
            {
                target = expression[(arrow + 2)..].Trim();
                expression = expression[..arrow].Trim();
            }

            List<ComponentPort> audioOutputs = [.. d.Outputs.Where(o => o.Type.TrimEnd('?') is "Audio" or "Region" || ComponentChecker.IsControlType(o.Type.TrimEnd('?')))];
            if (target.Length == 0)
            {
                if (audioOutputs.Count != 1) { st.Issues.Add(Issue(st, "AMP2233", $"The component has {audioOutputs.Count} outputs: say which one, emit ... -> {audioOutputs.FirstOrDefault()?.Name ?? "name"}.", s.Text)); continue; }
                target = audioOutputs[0].Name;
            }

            if (audioOutputs.All(o => o.Name != target)) { st.Issues.Add(Issue(st, "AMP2231", $"There is no Audio output \"{target}\".", s.Text)); continue; }
            string? from = Pipe(st, expression);
            if (from is not null && st.NotesSources.Contains(from)) { st.Issues.Add(Issue(st, "AMP2232", "Notes are not audio: an output of the component is audio, so the notes go through an instrument first (emit notes |> piano()).", expression)); continue; }
            if (from is not null && d.Outputs.FirstOrDefault(o => o.Name == target)?.Type.TrimEnd('?') == "Region")
            {
                AudioProcessor? volume = st.Catalog.Values.FirstOrDefault(p => p.Template == "volume");
                if (volume is null) { st.Issues.Add(Issue(st, "AMP2234", "A Region output needs the volume block, which is not in this build.", s.Text)); continue; }
                string regionId = "region" + (st.Counts["region"] = st.Counts.GetValueOrDefault("region") + 1).ToString(CultureInfo.InvariantCulture);
                st.Nodes.Add(new AudioNode(regionId, "volume", "volume", volume.Class, volume.Version, [("gain", "0 dB")], [("in", from)], "region", st.Line));
                from = "node:" + regionId;
            }

            if (from is not null) outputs[target] = from;
        }

        if (branches.Count > 0) st.Issues.Add(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2231", Severity.Error, "An if is not closed: add a } at the end of it.", string.Empty));
        foreach (ComponentPort o in d.Outputs.Where(o => (o.Type.TrimEnd('?') is "Audio" or "Region" || ComponentChecker.IsControlType(o.Type.TrimEnd('?'))) && !outputs.ContainsKey(o.Name)))
            st.Issues.Add(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2233", Severity.Error, $"The output \"{o.Name}\" is never given: write emit <audio> in the process.", string.Empty));
        if (st.Nodes.Count > MaxNodes) st.Issues.Add(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2233", Severity.Error, $"An audio component has at most {MaxNodes} processors (this one has {st.Nodes.Count}).", "Split it in two components."));
        if (d.Outputs.Count > 8) st.Issues.Add(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2233", Severity.Error, "An audio component has at most 8 outputs.", string.Empty));

        // only what an output needs is part of the graph
        HashSet<string> needed = [];
        Stack<string> todo = new(outputs.Values.Where(v => v.StartsWith("node:", StringComparison.Ordinal)).Select(v => v[5..]));
        while (todo.Count > 0)
        {
            string id = todo.Pop();
            if (!needed.Add(id)) continue;
            AudioNode? n = st.Nodes.FirstOrDefault(x => x.Id == id);
            if (n is null) continue;
            foreach ((_, string source) in n.Inputs.Where(i => i.Source.StartsWith("node:", StringComparison.Ordinal))) todo.Push(source[5..]);
        }

        List<AudioNode> kept = [.. st.Nodes.Where(n => needed.Contains(n.Id))];
        Dictionary<string, double> latency = [];
        foreach (AudioNode n in kept)
        {
            double own = st.Catalog.Values.FirstOrDefault(p => p.Template == n.Template)?.LatencyMs ?? 0;
            latency[n.Id] = own + n.Inputs.Where(i => i.Source.StartsWith("node:", StringComparison.Ordinal)).Select(i => latency.GetValueOrDefault(i.Source[5..])).DefaultIfEmpty(0).Max();
        }

        kept = Compensate(st, kept, latency);
        // a processor with a second audio input (the key of a sidechain) has no real-time chain: the live effects read one input
        // an instrument is rendered by the server (its take is what loops, the effects after it run live): one is fine, more than one is not
        List<AudioNode> instruments = [.. kept.Where(n => n.Inputs.Any(i => i.Port == "notes"))];
        string? liveSource = instruments.Count == 1 ? instruments[0].Id : null;
        string[] noLive = [.. kept.Where(n => n.Template != "bus" && n.Id != liveSource).Select(n => (Node: n, Processor: st.Catalog.Values.FirstOrDefault(p => p.Template == n.Template), Keyed: n.Inputs.Any(i => i.Port != "in"))).Where(x => x.Processor is null || !x.Processor.LivePreview || x.Keyed).Select(x => x.Processor?.Name ?? (x.Node.Template == "midi-clip" ? "a Notes call (" + x.Node.Processor + ") is not evaluated in the browser" : x.Node.Processor)).Distinct(StringComparer.Ordinal)];
        string[] noPreview = [.. kept.Select(n => st.Catalog.Values.FirstOrDefault(p => p.Template == n.Template)).Where(p => p is not null && !p.ClientPreview).Select(p => p!.Name).Distinct(StringComparer.Ordinal)];
        return new AudioGraph(kept, outputs, st.Issues, outputs.Values.Where(v => v.StartsWith("node:", StringComparison.Ordinal)).Select(v => latency.GetValueOrDefault(v[5..])).DefaultIfEmpty(0).Max(), noPreview.Length == 0, noPreview) { NestedDependencies = [.. st.NestedDeps], LivePreview = noLive.Length == 0 && instruments.Count <= 1 && kept.Count > 0, NoLive = instruments.Count > 1 ? [.. noLive, "more than one instrument"] : noLive, LiveSource = liveSource };
    }

    /// <summary>Latency compensation: where branches with different latencies meet (a mix, a key input), the shorter branches get a <c>shift</c> block of the difference, so the signals stay aligned. The nodes are named <c>align1</c>, <c>align2</c>, ... and are part of the graph (and of the hashes).</summary>
    private static List<AudioNode> Compensate(State st, List<AudioNode> kept, Dictionary<string, double> latency)
    {
        AudioProcessor? shift = st.Catalog.Values.FirstOrDefault(p => p.Template == "shift");
        if (shift is null || latency.Values.All(v => v <= 0)) return kept;
        List<AudioNode> result = [];
        int count = 0;
        foreach (AudioNode n in kept)
        {
            double Of(string source) => source.StartsWith("node:", StringComparison.Ordinal) ? latency.GetValueOrDefault(source[5..]) : 0;
            List<(string Port, string Source)> audio = [.. n.Inputs.Where(i => i.Source.StartsWith("node:", StringComparison.Ordinal) || i.Source.StartsWith("in:", StringComparison.Ordinal))];
            double longest = audio.Select(i => Of(i.Source)).DefaultIfEmpty(0).Max();
            if (audio.Count < 2 || audio.All(i => Math.Abs(Of(i.Source) - longest) < 0.001))
            {
                result.Add(n);
                continue;
            }

            List<(string Port, string Source)> inputs = [];
            foreach ((string port, string source) in n.Inputs)
            {
                double own = audio.Any(a => a.Port == port && a.Source == source) ? Of(source) : longest;
                if (longest - own < 0.001) { inputs.Add((port, source)); continue; }
                string id = "align" + (++count).ToString(CultureInfo.InvariantCulture);
                string seconds = ((longest - own) / 1000).ToString("0.######", CultureInfo.InvariantCulture);
                result.Add(new AudioNode(id, "shift", "shift", shift.Class, shift.Version, [("seconds", seconds)], [("in", source)], "out", n.Line));
                latency[id] = longest;
                inputs.Add((port, "node:" + id));
            }

            result.Add(n with { Inputs = inputs });
        }

        return result;
    }

    private static ComponentIssue Issue(State st, string code, string message, string needle, string hint = "") =>
        new(st.Line, 0, Math.Max(1, needle.Length), code, Severity.Error, message, hint);

    private static bool LooksAudio(State st, string text)
    {
        Match m = Regex.Match(text, @"^\(*\s*([A-Za-z_][\w-]*)\s*(\()?", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!m.Success) return false;
        string id = m.Groups[1].Value;
        return st.AudioNames.ContainsKey(id) || (m.Groups[2].Success && (id == "mix" || st.Catalog.ContainsKey(id) || st.Instruments.ContainsKey(id) || NoteCatalog.Signatures.ContainsKey(id)));
    }

    private static List<string> SplitTop(string text, string separator)
    {
        List<string> parts = [];
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(text[start..i].Trim());
                i += separator.Length - 1;
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    /// <summary>Reads <c>a |&gt; f(x) |&gt; g(y)</c>: the first part is an audio name, a call without a receiver (mix) or a bracket; every other part is a processor call that takes what is before it.</summary>
    private static string? Pipe(State st, string text)
    {
        List<string> parts = SplitTop(text.Trim(), "|>");
        string? current = Primary(st, parts[0]);
        for (int i = 1; i < parts.Count && current is not null; i++) current = Call(st, parts[i], current);
        return current;
    }

    private static string? Primary(State st, string text)
    {
        string t = text.Trim();
        if (t.StartsWith('(') && t.EndsWith(')') && SplitTop(t[1..^1], "|>").Count >= 1 && Balanced(t)) return Pipe(st, t[1..^1]);
        if (Word().IsMatch(t))
        {
            if (st.AudioNames.TryGetValue(t, out string? source)) return source;
            st.Issues.Add(Issue(st, "AMP2231", st.Scope.ContainsKey(t) ? $"\"{t}\" is a value, not audio: a processor needs an Audio input or a let of audio." : $"\"{t}\" is not known here: an input of the component, or a let defined above.", t, "Names are read top to bottom, so a graph cannot loop back on itself."));
            return null;
        }

        return Call(st, t, null);
    }

    private static bool Balanced(string t)
    {
        int depth = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '(') depth++;
            else if (t[i] == ')') depth--;
            if (depth == 0 && i < t.Length - 1) return false;
        }

        return depth == 0;
    }

    private static string? Call(State st, string text, string? receiver)
    {
        Match m = Regex.Match(text.Trim(), @"^([A-Za-z_][\w.-]*)(?:@([0-9^~][\w.\-]*))?\s*\((.*)\)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        if (!m.Success)
        {
            st.Issues.Add(Issue(st, "AMP2234", $"\"{text.Trim()}\" is not a processor call: write name(arguments).", text.Trim()));
            return null;
        }

        string name = m.Groups[1].Value;
        string pin = m.Groups[2].Value;
        List<string> args = SplitTop(m.Groups[3].Value, ",").Where(a => a.Length > 0).ToList();
        if (name == "mix") return Mix(st, args, receiver);
        if (name == "repeat") return Repeat(st, args, receiver);
        bool notesReceiver = receiver is not null && st.NotesSources.Contains(receiver);
        bool instrument = st.Instruments.ContainsKey(name);
        if (instrument && receiver is not null && !notesReceiver)
        {
            st.Issues.Add(Issue(st, "AMP2232", $"{name} is an instrument: it turns Notes into audio, and the value before it is audio.", name, "Put the instrument on a Notes input: notes |> " + name + "(...)."));
            return null;
        }

        if (!instrument && notesReceiver && st.Catalog.ContainsKey(name))
        {
            st.Issues.Add(Issue(st, "AMP2232", $"{name} takes audio, and the value before it is Notes.", name, "Make sound of the notes first: notes |> piano() |> " + name + "(...)."));
            return null;
        }

        if (notesReceiver && !instrument && !st.Catalog.ContainsKey(name))
        {
            List<ComponentDefinition> notesFamily = [.. st.Visible.Where(c => !c.IsAudio && (c.Name == name || c.DeclaredName == name))];
            if (notesFamily.Count > 0) return Pinned(st, name, notesFamily, pin) is { } notesPick ? NotesCall(st, name, notesPick, args, receiver!) : null;
        }

        AudioProcessor? found = instrument ? st.Instruments[name] : null;
        if (!instrument && !st.Catalog.TryGetValue(name, out found))
        {
            List<ComponentDefinition> family = [.. st.Visible.Where(c => c.IsAudio && (c.Name == name || c.DeclaredName == name))];
            if (family.Count > 0) return Pinned(st, name, family, pin) is { } pick ? Nest(st, name, pick, args, receiver) : null;
            string hint = NoteCatalog.Signatures.ContainsKey(name) ? $"{name} is an operation on Notes. An Audio value goes into: {string.Join(", ", st.Catalog.Keys.Order(StringComparer.Ordinal))}, mix." : $"The processors are: {string.Join(", ", st.Catalog.Keys.Order(StringComparer.Ordinal))}, mix.";
            st.Issues.Add(Issue(st, "AMP2234", $"\"{name}\" is not an audio processor.", name, hint));
            return null;
        }

        AudioProcessor processor = found!;

        if (receiver is null && !processor.Source)
        {
            st.Issues.Add(Issue(st, "AMP2232", $"{name} needs audio in front of it: write audio |> {name}(...).", name));
            return null;
        }

        List<(string Name, string Value)> parameters = [];
        List<(string Port, string Source)> inputs = receiver is null ? [] : [(instrument ? "notes" : "in", receiver)];
        bool positionalUsed = false;
        foreach (string arg in args)
        {
            Match named = Regex.Match(arg, @"^([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            string argName = named.Success ? named.Groups[1].Value : processor.Positional ?? string.Empty;
            string argText = named.Success ? named.Groups[2].Value.Trim() : arg;
            if (!named.Success)
            {
                if (processor.Positional is null || positionalUsed) { st.Issues.Add(Issue(st, "AMP2235", $"{name} takes named arguments: {name}({(processor.Parameters.Count > 0 ? processor.Parameters[0].Name : "name")}=value).", arg)); continue; }
                positionalUsed = true;
            }

            if (processor.ExtraInputs.Contains(argName))
            {
                string? source = Primary(st, argText);
                if (source is not null) inputs.Add((argName, source));
                continue;
            }

            AudioProcessorParameter? parameter = processor.Parameter(argName);
            if (parameter is null)
            {
                st.Issues.Add(Issue(st, "AMP2235", $"{name} has no parameter \"{argName}\".", argName, "Its parameters: " + string.Join(", ", processor.Parameters.Select(p => p.Name + (p.Unit.Length > 0 ? " (" + p.Unit + ")" : string.Empty)))));
                continue;
            }

            string? argValue = Argument(st, name, parameter, argText);
            if (argValue is not null) parameters.Add((argName, argValue));
        }

        string id = name + (st.Counts[name] = st.Counts.GetValueOrDefault(name) + 1).ToString(CultureInfo.InvariantCulture);
        st.Nodes.Add(new AudioNode(id, name, processor.Template, processor.Class, processor.Version, parameters, inputs, instrument ? "audio" : processor.OutputPort, st.Line));
        if (processor.Notes) st.NotesSources.Add("node:" + id);   // fire: what comes out are notes, an instrument can play them
        return "node:" + id;
    }

    /// <summary>The version of a family that a call means: the default one, or the one that a pin (<c>name@1.2(...)</c>) asks for (a request that nothing matches is a problem).</summary>
    private static ComponentDefinition? Pinned(State st, string name, List<ComponentDefinition> family, string pin)
    {
        if (pin.Length == 0) return ComponentVersions.Default(family);
        if (!VersionRequest.TryParse(pin, out VersionRequest request)) { st.Issues.Add(Issue(st, "AMP2248", $"\"{pin}\" is not a version request for {name} (1.8, 1.8.0, ^1.2 or ~1.2).", name)); return null; }
        ComponentDefinition? pick = ComponentVersions.Pick(family, request);
        if (pick is null) st.Issues.Add(Issue(st, "AMP2248", $"No version of {name} matches @{request}. Versions: {string.Join(", ", family.Select(f => f.Version.ToString()).Order(StringComparer.Ordinal))}.", name));
        return pick;
    }

    /// <summary>A call of a Notes component on the notes that go to an instrument: <c>notes |&gt; koda.notes.octaveLayers(layers=2) |&gt; piano()</c>. It becomes a <c>midi-clip</c> block whose notes are the call of the component, and the instrument reads that clip.</summary>
    private static string? NotesCall(State st, string name, ComponentDefinition inner, List<string> args, string receiver)
    {
        List<string> given = ["{~src}"];
        foreach (string arg in args)
        {
            Match named = Regex.Match(arg, @"^([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!named.Success) { st.Issues.Add(Issue(st, "AMP2235", $"{name} takes named arguments: {name}(property=value); the notes come from before the pipe.", arg)); continue; }
            string argName = named.Groups[1].Value, argText = named.Groups[2].Value.Trim();
            if (inner.Properties.All(p => p.Name != argName)) { st.Issues.Add(Issue(st, "AMP2235", $"{name} has no property \"{argName}\".", argName, "Its properties: " + string.Join(", ", inner.Properties.Select(p => p.Name)))); continue; }
            string? value = ComponentMigrations.EvaluateScoped(argText, st.Scope, out string? failure);
            if (value is null && Word().IsMatch(argText) && !st.Scope.ContainsKey(argText)) value = argText;
            if (value is null) st.Issues.Add(Issue(st, "AMP2232", $"The value of {argName} for {name} cannot be read: {failure}", argText));
            else given.Add(argName + "=" + value.Trim('"'));
        }

        List<NestIssue> problems = [];
        string call = ComponentNesting.CallText(inner, given, st.Definition, problems);
        foreach (NestIssue problem in problems) st.Issues.Add(Issue(st, problem.Code, problem.Message, name));
        string id = name[(name.LastIndexOf('.') + 1)..] + (st.Counts[name] = st.Counts.GetValueOrDefault(name) + 1).ToString(CultureInfo.InvariantCulture);
        st.NotesSources.Add("node:" + id);
        st.NestedDeps.Add($"component={inner.Name}@{inner.Version}:{ComponentHashing.Semantic(inner, [.. ComponentNesting.Callees(inner, st.Visible).Select(c => $"component={c.Name}@{c.Version}:{ComponentHashing.Semantic(c, [])}")])}");
        st.Nodes.Add(new AudioNode(id, name, "midi-clip", "notes", "1.1", [("notes", call)], [("~src", receiver)], "notes", st.Line));
        return "node:" + id;
    }

    /// <summary>A call of another audio component: its graph is built with the arguments as its properties and spliced into this one (the nodes are named <c>name1_delay1</c>), so the engine still sees ordinary blocks.</summary>
    private static string? Nest(State st, string name, ComponentDefinition inner, List<string> args, string? receiver)
    {
        if (receiver is null) { st.Issues.Add(Issue(st, "AMP2232", $"{name} needs audio in front of it: write audio |> {name}(...).", name)); return null; }
        int circle = st.Chain.ToList().IndexOf(inner.Name);
        if (circle >= 0)
        {
            string way = string.Join(" -> ", st.Chain.Skip(circle).Append(inner.Name));
            st.Issues.Add(Issue(st, "AMP2247", circle == st.Chain.Count - 1 ? $"{inner.Name} calls itself ({way}): a component never calls itself." : $"These components call each other in a circle: {way}. A component never calls itself, directly or through other components.", name));
            return null;
        }

        if (st.Depth >= 4 || inner.Id == st.Definition.Id && inner.Id.Length > 0 || inner.Name == st.Definition.Name) { st.Issues.Add(Issue(st, "AMP2233", $"{name} cannot be called here: components are nested at most four deep and never call themselves.", name)); return null; }
        List<ComponentPort> audioInputs = [.. inner.Inputs.Where(i => i.Type.TrimEnd('?') == "Audio")];
        if (audioInputs.Count == 0) { st.Issues.Add(Issue(st, "AMP2234", $"{name} has no Audio input, so audio cannot go into it.", name)); return null; }
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        Dictionary<string, string> extra = new(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            Match named = Regex.Match(arg, @"^([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!named.Success) { st.Issues.Add(Issue(st, "AMP2235", $"{name} takes named arguments: {name}(property=value).", arg)); continue; }
            string argName = named.Groups[1].Value, argText = named.Groups[2].Value.Trim();
            if (audioInputs.Skip(1).Any(i => i.Name == argName))
            {
                string? source = Primary(st, argText);
                if (source is not null) extra[argName] = source;
            }
            else if (inner.Properties.Any(p => p.Name == argName))
            {
                string? value = ComponentMigrations.EvaluateScoped(argText, st.Scope, out string? failure);
                if (value is null && Word().IsMatch(argText) && !st.Scope.ContainsKey(argText)) value = argText;
                if (value is null) st.Issues.Add(Issue(st, "AMP2232", $"The value of {argName} for {name} cannot be read: {failure}", argText));
                else values[argName] = value.Trim('"');
            }
            else st.Issues.Add(Issue(st, "AMP2235", $"{name} has no property or audio input \"{argName}\".", argName, "Its properties: " + string.Join(", ", inner.Properties.Select(p => p.Name))));
        }

        ComponentPort? output = inner.Outputs.FirstOrDefault(o => o.Type.TrimEnd('?') == "Audio");
        if (output is null) { st.Issues.Add(Issue(st, "AMP2234", $"{name} has no Audio output.", name)); return null; }
        AudioGraph g = Build(inner, st.Spec, values, st.Tempo, st.Visible, st.Depth + 1, st.Chain);
        foreach (ComponentIssue issue in g.Issues) st.Issues.Add(Issue(st, issue.Code, $"In {name}: {issue.Message}", name));
        if (g.Issues.Count > 0 || !g.Outputs.TryGetValue(output.Name, out string? inside)) return null;
        string prefix = name[(name.LastIndexOf('.') + 1)..] + (st.Counts[name] = st.Counts.GetValueOrDefault(name) + 1).ToString(CultureInfo.InvariantCulture);
        string? Map(string source)
        {
            if (source.StartsWith("node:", StringComparison.Ordinal)) return "node:" + prefix + "_" + source[5..];
            string input = source[3..];
            if (input == audioInputs[0].Name) return receiver;
            if (extra.TryGetValue(input, out string? given)) return given;
            st.Issues.Add(Issue(st, "AMP2231", $"{name} needs the audio input \"{input}\": {name}({input}=...).", name));
            return null;
        }

        foreach (AudioNode n in g.Nodes)
        {
            List<(string Port, string Source)> inputs = [];
            foreach ((string port, string source) in n.Inputs)
            {
                string? mapped = Map(source);
                if (mapped is null) return null;
                inputs.Add((port, mapped));
            }

            st.Nodes.Add(n with { Id = prefix + "_" + n.Id, Inputs = inputs, Line = st.Line });
        }

        st.NestedDeps.Add($"component={inner.Name}@{inner.Version}:{ComponentHashing.Semantic(inner, g.Dependencies)}");
        return Map(inside);
    }

    private static string? Mix(State st, List<string> args, string? receiver)
    {
        List<string> sources = [];
        if (receiver is not null) sources.Add(receiver);
        foreach (string arg in args)
        {
            string? s = Pipe(st, arg);
            if (s is null) return null;
            sources.Add(s);
        }

        if (sources.Any(st.NotesSources.Contains)) { st.Issues.Add(Issue(st, "AMP2232", "mix sums audio: Notes go through an instrument first.", "mix")); return null; }
        if (sources.Count < 2) { st.Issues.Add(Issue(st, "AMP2232", "mix needs at least two audio values: mix(a, b).", "mix")); return null; }
        if (sources.Count > 16) { st.Issues.Add(Issue(st, "AMP2233", "mix takes at most 16 inputs.", "mix")); return null; }
        return MixSources(st, sources);
    }

    private static string MixSources(State st, List<string> sources)
    {
        TemplateSpec bus = st.Spec.Templates.GetValueOrDefault("bus") ?? throw new InvalidOperationException("The bus template is missing.");
        string id = "mix" + (st.Counts["mix"] = st.Counts.GetValueOrDefault("mix") + 1).ToString(CultureInfo.InvariantCulture);
        int inputs = Math.Max(4, sources.Count);
        List<(string, string)> parameters = [("inputs", inputs.ToString(CultureInfo.InvariantCulture)), ("gains", string.Join(' ', Enumerable.Repeat("0", inputs))), ("pan", string.Join(' ', Enumerable.Repeat("c", inputs)))];
        st.Nodes.Add(new AudioNode(id, "mix", "bus", bus.Class, bus.Version, parameters, [.. sources.Select((s, i) => ("in" + (i + 1).ToString(CultureInfo.InvariantCulture), s))], "mix", st.Line));
        return "node:" + id;
    }

    /// <summary>The most copies repeat makes (a mix takes 16 inputs).</summary>
    private const int MaxRepeats = 16;

    /// <summary>
    /// <c>audio |> repeat(times=cars, delay=2bar, length=24bar)</c>: the sound again and again, a number of times that can be a property (so a component can say how many cars pass), the first after <c>delay</c>,
    /// spread evenly over <c>length</c> (or <c>every=4bar</c> for a fixed distance), mixed into one sound. The graph is built for the value of the property, so each instance has its own number of copies.
    /// </summary>
    private static string? Repeat(State st, List<string> args, string? receiver)
    {
        if (receiver is null) { st.Issues.Add(Issue(st, "AMP2232", "repeat needs audio in front of it: write audio |> repeat(times=3, every=2bar).", "repeat")); return null; }
        if (st.NotesSources.Contains(receiver)) { st.Issues.Add(Issue(st, "AMP2232", "repeat repeats a sound: Notes go through an instrument first.", "repeat")); return null; }
        Dictionary<string, string> given = new(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            Match named = Regex.Match(arg, @"^([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!named.Success || named.Groups[1].Value is not ("times" or "delay" or "every" or "length")) { st.Issues.Add(Issue(st, "AMP2235", "repeat takes times=, delay=, and every= or length=.", arg)); return null; }
            given[named.Groups[1].Value] = named.Groups[2].Value.Trim();
        }

        if (!given.TryGetValue("times", out string? timesText)) { st.Issues.Add(Issue(st, "AMP2235", "repeat needs times=: how many copies (1 to 16).", "repeat")); return null; }
        string? timesValue = ComponentMigrations.EvaluateScoped(timesText, st.Scope, out string? timesFailure);
        if (timesValue is null || !int.TryParse(timesValue, NumberStyles.None, CultureInfo.InvariantCulture, out int times) || times < 1 || times > MaxRepeats)
        {
            st.Issues.Add(Issue(st, "AMP2232", $"repeat times must be a whole number from 1 to {MaxRepeats} (it is {timesValue ?? timesFailure}).", timesText));
            return null;
        }

        double? delay = given.TryGetValue("delay", out string? delayText) ? Milliseconds(st, delayText, "delay") : 0;
        if (delay is null) return null;
        double every;
        if (given.TryGetValue("every", out string? everyText))
        {
            double? fixedStep = Milliseconds(st, everyText, "every");
            if (fixedStep is null) return null;
            every = fixedStep.Value;
        }
        else if (given.TryGetValue("length", out string? lengthText))
        {
            double? length = Milliseconds(st, lengthText, "length");
            if (length is null) return null;
            every = (length.Value - delay.Value) / times;
            if (every <= 0 && times > 1) { st.Issues.Add(Issue(st, "AMP2232", "repeat length must be longer than delay: the copies are spread over what is left.", lengthText)); return null; }
        }
        else
        {
            if (times > 1) { st.Issues.Add(Issue(st, "AMP2235", "repeat needs every= (a distance) or length= (spread over this time) when it makes more than one copy.", "repeat")); return null; }
            every = 0;
        }

        AudioProcessor? shift = st.Catalog.Values.FirstOrDefault(p => p.Template == "shift");
        if (shift is null) { st.Issues.Add(Issue(st, "AMP2234", "repeat needs the shift block, which is not in this build.", "repeat")); return null; }
        List<string> sources = [];
        string previous = receiver;
        double carried = 0;
        for (int k = 0; k < times; k++)
        {
            double offset = delay.Value + (k * every);
            previous = ShiftBy(st, shift, previous, offset - carried);   // each copy starts from the one before it
            carried = offset;
            sources.Add(previous);
        }

        return sources.Count == 1 ? sources[0] : MixSources(st, sources);
    }

    /// <summary>The sound moved on by some milliseconds: one shift block for up to 30 seconds (the limit of a shift), a chain of them for more; no block when it is not moved at all.</summary>
    private static string ShiftBy(State st, AudioProcessor shift, string source, double milliseconds)
    {
        double left = milliseconds;
        while (Math.Abs(left) >= 0.5)
        {
            double step = Math.Clamp(left, -30000, 30000);
            string id = "shift" + (st.Counts["shift"] = st.Counts.GetValueOrDefault("shift") + 1).ToString(CultureInfo.InvariantCulture);
            st.Nodes.Add(new AudioNode(id, "shift", "shift", shift.Class, shift.Version, [("seconds", (step / 1000).ToString("0.######", CultureInfo.InvariantCulture))], [("in", source)], "out", st.Line));
            source = "node:" + id;
            left -= step;
        }

        return source;
    }

    /// <summary>A time in milliseconds: a value with a unit (2bar, 0.5beat, 3s, 250ms), which may be calculated from properties.</summary>
    private static double? Milliseconds(State st, string text, string what)
    {
        string? value = ComponentMigrations.EvaluateScoped(text.Trim(), st.Scope, out string? failure);
        Match m = value is null ? Match.Empty : Regex.Match(value, @"^(-?[0-9]+(?:\.[0-9]+)?)([A-Za-z]*)$", RegexOptions.None, TimeSpan.FromSeconds(1));
        double? ms = !m.Success ? null : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var n ? m.Groups[2].Value switch { "ms" => n, "s" => n * 1000, "beat" or "beats" => n * 60000 / st.Tempo, "bar" or "bars" => n * 4 * 60000 / st.Tempo, _ => null } : null;
        if (ms is null) st.Issues.Add(Issue(st, "AMP2232", $"repeat {what} needs a time with a unit (2bar, 0.5beat, 3s, 250ms), not {value ?? failure}.", text));
        return ms;
    }

    private static string? Argument(State st, string processor, AudioProcessorParameter parameter, string text)
    {
        string t = text.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') return t[1..^1];
        // a Bool property goes to enabled as on or off
        if (parameter.Name == "enabled" && st.Scope.TryGetValue(t, out string? flag) && flag is "true" or "false") return flag == "true" ? "on" : "off";
        if (parameter.Name == "enabled" && t is "true" or "false") return t == "true" ? "on" : "off";
        // a switch of a block (default on or off): a Bool property or 1 and 0 turn it on or off
        if (parameter.Default is "on" or "off" && !t.StartsWith('"'))
        {
            string? onOff = ComponentMigrations.EvaluateScoped(t, st.Scope, out _);
            if (onOff is "true" or "false" or "1" or "0") return onOff is "true" or "1" ? "on" : "off";
        }

        string? value = ComponentMigrations.EvaluateScoped(t, st.Scope, out string? failure);
        if (value is null)
        {
            // a word such as lowpass is the value itself
            if (Word().IsMatch(t) && !st.Scope.ContainsKey(t)) return t;
            st.Issues.Add(Issue(st, "AMP2232", $"The value of {parameter.Name} for {processor} cannot be read: {failure}", t));
            return null;
        }

        Match m = Regex.Match(value, @"^(-?[0-9]+(?:\.[0-9]+)?)([A-Za-z]*|%)$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!m.Success) return value;
        double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string unit = m.Groups[2].Value;
        string Wrong(string need) => $"{processor} {parameter.Name} needs {need}, not {value}.";
        switch (parameter.Type)
        {
            case "Duration":
                double? ms = unit switch { "ms" => n, "s" => n * 1000, "beat" or "beats" => n * 60000 / st.Tempo, "bar" or "bars" => n * 4 * 60000 / st.Tempo, _ => null };
                if (ms is null) { st.Issues.Add(Issue(st, "AMP2232", Wrong("a time (250ms, 0.25beat, 1bar)"), t)); return null; }
                return FormatNumber(ms.Value) + " ms";
            case "Percent":
                if (unit != "%") { st.Issues.Add(Issue(st, "AMP2232", Wrong("a Percent (35%)"), t)); return null; }
                return FormatNumber(n) + "%";
            case "Gain":
                if (!string.Equals(unit, "dB", StringComparison.OrdinalIgnoreCase)) { st.Issues.Add(Issue(st, "AMP2232", Wrong("a gain in dB (-12dB)"), t)); return null; }
                return FormatNumber(n) + " dB";
            case "Frequency":
                double? hz = unit switch { "Hz" or "hz" => n, "kHz" or "khz" => n * 1000, _ => null };
                if (hz is null) { st.Issues.Add(Issue(st, "AMP2232", Wrong("a frequency (80Hz, 2kHz)"), t)); return null; }
                return FormatNumber(hz.Value) + " Hz";
            default:
                return value;
        }
    }

    /// <summary>Gets the text of the block of a node (without the header position): used by the lowering and the inspection.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The parameter lines.</returns>
    public static string ParameterText(AudioNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        StringBuilder sb = new();
        foreach ((string name, string value) in node.Parameters) sb.Append("  ").Append(name).Append(' ').Append(value).Append('\n');
        return sb.ToString();
    }
}
