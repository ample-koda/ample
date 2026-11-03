using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>Options for parsing and analysing a document.</summary>
public sealed class ParseOptions
{
    /// <summary>Gets or sets whether a project declaration is required (module bodies do not have one).</summary>
    public bool RequireProjectLine { get; set; } = true;

    /// <summary>Gets or sets the plugins that are installed; a block whose template needs another plugin gets AMP5001.</summary>
    public IReadOnlySet<string> InstalledPlugins { get; set; } = new HashSet<string>(StringComparer.Ordinal) { "acestep", "voice-clone", "fprint" };

    /// <summary>Gets or sets the settings every song must state for a plugin (plugin id, then setting name and the value the quick fix writes). Empty for plain parsing; the editor uses <see cref="Editor"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SongRequirements { get; set; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>Gets or sets whether values that the chosen model cannot use are reported (AMP2014). The editors turn it on; plain parsing leaves the build to refuse them.</summary>
    public bool ModelRules { get; set; }

    /// <summary>Gets or sets the largest number of notes a note pipeline may hold while the parser checks it (the build uses a larger limit).</summary>
    public int NoteLimit { get; set; } = 100_000;

    /// <summary>Gets or sets how a project file is read (a note bake that keeps its notes in a file), or null.</summary>
    public Func<string, string?>? ReadFile { get; set; }

    /// <summary>Gets or sets how the notes of a <c>midi</c> block (a MIDI file) are read for a note pipeline, or null (the editors then use a placeholder).</summary>
    public Func<BlockNode, PipeValue?>? MidiSource { get; set; }

    /// <summary>Gets or sets the components that come from elsewhere (imported files, the library): the song may use them as block types. The host resolves the <c>import</c> lines of the song; the parser only reads them.</summary>
    public IReadOnlyList<ComponentDefinition>? Components { get; set; }

    /// <summary>Gets or sets diagnostics that the host found outside the text (the problems of putting imported components together); they are added to the ones of the parse.</summary>
    public IReadOnlyList<Diagnostic> ExtraDiagnostics { get; set; } = [];

    /// <summary>Gets or sets what the host decides about capabilities above every project and song (null: the policy of a local server).</summary>
    public ServerCapabilities? ServerCapabilities { get; set; }

    /// <summary>Gets or sets what the project says about capabilities (capability to on or off), or null.</summary>
    public IReadOnlyDictionary<string, string>? ProjectCapabilities { get; set; }

    /// <summary>Gets or sets how environment(name) reads a value (tests give a fake one), or null for the process environment. Only reachable when the capability environment is on.</summary>
    public Func<string, string?>? Environment { get; set; }

    /// <summary>Gets or sets checks that need more than the text, by the host (what a drums block asks of its kit): they get the blocks and the lines and return diagnostics that are added to the ones of the parser (and can be silenced like them), or null.</summary>
    public Func<IReadOnlyList<BlockNode>, IReadOnlyList<string>, IReadOnlyList<Diagnostic>>? Preflight { get; set; }

    /// <summary>Gets or sets the clock that currentTime() and currentDate() read, or null for the system clock (tests give a fixed one).</summary>
    public Func<DateTime>? Clock { get; set; }

    /// <summary>Gets or sets whether the notes of a note pipeline replace the expression in the parsed blocks (what the build needs; the editors keep the text).</summary>
    public bool ApplyNoteFlow { get; set; }

    /// <summary>Gets the options of the editors (source editor, node editor): a song that uses the ACE-Step plugin must say how the sound is made (acestep.backend), with a quick fix that writes the project default (server).</summary>
    public static ParseOptions Editor => new() { ModelRules = true, SongRequirements = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["acestep"] = new Dictionary<string, string> { ["backend"] = "server" } } };
}

/// <summary>Error-tolerant parser and analyser for the AMPLE source language (spec 0.1). It never throws on any input.</summary>
public static partial class Parser
{
    private static readonly string[] AssistLevels = ["off", "diff", "button", "auto"];
    private static readonly string[] LintLevels = ["off", "info", "warning", "error"];
    private static readonly string[] HeaderFlags = ["bake", "lock", "refresh", "disabled"];

    /// <summary>The colours a block can be tinted with (the tint parameter, any block type, cosmetic only).</summary>
    public static readonly string[] TintNames = ["red", "orange", "yellow", "green", "teal", "blue", "purple", "pink"];

    /// <summary>Parses and analyses a song text.</summary>
    public static AmpleDocument Parse(string text, SpecData spec, ParseOptions? options = null)
    {
        options ??= new ParseOptions();
        string[] original = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // a block comment is blanked before the parser reads the lines (same lines, same columns); the document keeps the original lines, so the editors and the edits see the comment
        string[] lines = original;
        int openComment = -1;
        if (original.Any(l => l.Contains("/*", StringComparison.Ordinal))) lines = Comments.BlankBlocks(string.Join('\n', original), out openComment).Split('\n');
        Dictionary<int, DocComment> docs = Comments.DocsOf(original);
        // components (declarations of reusable typed blocks) are read first: their lines are not song lines, and the components become block types of this song
        ComponentFile components = ComponentReader.Read(lines, docs, "song");
        List<ComponentDefinition> available = [.. components.Definitions, .. options.Components ?? []];
        string[] songLines = lines;
        if (components.Blank.Count > 0)
        {
            songLines = [.. lines];
            foreach (int blankLine in components.Blank.Where(b => b < songLines.Length)) songLines[blankLine] = string.Empty;
        }

        SpecData effective = available.Count == 0 ? spec : spec.WithTemplates(available.GroupBy(c => c.Name, StringComparer.Ordinal).Select(g => ComponentChecker.TemplateOf(ComponentVersions.Default(g)!)));
        Run run = new(songLines, effective, options) { Docs = docs, Original = original, Components = components };
        run.Execute();
        if (options.ExtraDiagnostics.Count > 0) run.AddExternal(options.ExtraDiagnostics);
        if (openComment >= 0) run.AddExternal([new Diagnostic("AMP1116", Severity.Error, openComment + 1, 1, original[openComment].Length + 1, "This block comment is never closed: it runs to the end of the file.", "Close it with */, or use # for a comment that ends with the line.", null, null)]);
        if (options.Preflight is not null) run.AddExternal(options.Preflight(run.BlockList, lines));
        return new AmpleDocument
        {
            Text = text,
            Lines = original,
            Docs = docs,
            Components = components.Definitions,
            ComponentImports = components.Imports,
            ComponentReexports = components.Reexports,
            EmbeddedModules = components.Modules,
            ImportedComponents = options.Components ?? [],
            Spec = ReferenceEquals(effective, spec) ? null : effective,
            Project = run.Project,
            Features = run.Features,
            Assist = run.Assist,
            Lint = run.LintOverrides,
            Blocks = run.Blocks,
            Uses = run.Uses,
            Layout = run.Layout,
            Settings = run.Settings,
            Helpers = run.HelperInfos,
            Capabilities = run.CapabilityStates,
            HelperTable = run.HelperTableValue,
            CapabilityPolicy = run.PolicyValue,
            ParsedAt = run.StartedValue,
            CapabilitySettings = run.CapabilitySettings,
            Properties = run.PropertyBindings,
            Limits = LangLimits.Describe(run.Settings),
            Stability = run.StabilityState,
            StabilityNotes = run.StabilityNotes,
            SettingsLine = run.SettingsLine,
            Diagnostics = run.Sorted(),
        };
    }

    /// <summary>Splits a header, features or lint line into words, quoted strings and commas.</summary>
    public static IReadOnlyList<string> Tokenize(string s) => [.. WordPattern().Matches(s).Select(m => m.Value)];

    /// <summary>Removes the quotes of a quoted name and unescapes it (a backslash before a quote, a backslash, n or t; any other escaped character stands for itself).</summary>
    public static string Unquote(string token)
    {
        if (token.Length == 0 || token[0] != '"')
        {
            return token;
        }

        System.Text.StringBuilder sb = new();
        int end = token.Length > 1 && token[^1] == '"' ? token.Length - 1 : token.Length;
        for (int i = 1; i < end; i++)
        {
            char c = token[i];
            if (c == '\\' && i + 1 < end)
            {
                char next = token[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Gets the closest word of the list within edit distance 3, or null.</summary>
    public static string? Nearest(string word, IEnumerable<string> list)
    {
        string? best = null;
        int bestDistance = 99;
        string lower = word.ToLowerInvariant();
        foreach (string candidate in list)
        {
            int d = Levenshtein(lower, candidate.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        return bestDistance <= 3 ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
        }

        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@"^(?:dynamic\s+helper|helper)\s+\S|^type\s+[A-Za-z][\w-]*\s*=|^let\s+\S", RegexOptions.None, 1000)]
    private static partial Regex HelperStart();

    [GeneratedRegex(@"^\s*random-stream\s+([A-Za-z][A-Za-z0-9_-]*)\s+(\S.*?)\s*$", RegexOptions.None, 1000)]
    private static partial Regex StreamLine();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|,|[^\\s{},\"]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex WordPattern();

    [GeneratedRegex("^#\\s*ample-ignore\\s+(.+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IgnorePattern();

    [GeneratedRegex("^(?:project|composition)\\s+(\"(?:[^\"\\\\]|\\\\.)*\")\\s*\\{\\s*(?:ample\\s+(?<ample>\\d+)\\s+)?tempo\\s+(\\d+(?:\\.\\d+)?)\\s+(?:meter\\s+(?<mn>\\d+)/(?<md>\\d+)\\s+)?master-seed\\s+(\\d+)\\s*\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProjectPattern();

    [GeneratedRegex("^features\\s*\\{(.*)\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FeaturesPattern();

    [GeneratedRegex("^lint\\s*\\{(.*)\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LintPattern();

    [GeneratedRegex("^settings\\s*\\{(.*)\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SettingsPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*\\.[A-Za-z][A-Za-z0-9-]*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SettingKey();

    [GeneratedRegex("^layout\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|\\S+)\\s+(-?\\d+)\\s+(-?\\d+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LayoutPattern();

    [GeneratedRegex("^view\\s*\\{$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ViewOpen();

    [GeneratedRegex("^from\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s.\"]+)\\.(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s\"]+)\\s*->\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|\\S+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FromPattern();

    [GeneratedRegex("^([\\w-]+)\\s+(.+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ParameterPattern();

    [GeneratedRegex("^(stable|pinned|live)@(\\d+(?:\\.\\d+)*)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ClassVersionPattern();

    // the version of an instance of a component is a request: 1.8, 1.8.0, ^1.2, ~1.2 or 4.0.0-beta.1
    [GeneratedRegex("^(stable|pinned|live)(?:@([~^]?\\d+(?:\\.\\d+){0,2}(?:-[0-9A-Za-z.-]+)?))?$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ComponentClassVersionPattern();

    [GeneratedRegex("^use\\s", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UseWord();

    [GeneratedRegex("^(\"(?:[^\"\\\\]|\\\\.)*\"|[\\w-]+(?:/[\\w-]+)*)(?:@(\\d+(?:\\.\\d+)*))?$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UseModulePattern();

    [GeneratedRegex("^\\s*(?:project|composition)\\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProjectWord();

    private sealed class Run(string[] lines, SpecData spec, ParseOptions options)
    {
        private readonly List<Diagnostic> diagnostics = [];
        private readonly Dictionary<int, List<string>> ignoreFor = [];
        private readonly Dictionary<string, bool> features = [];
        private readonly Dictionary<string, string> lintOverrides = [];

        /// <summary>Block headers written in the pre-release form (<c>stable@1.1</c>, <c>live@0.4</c>): line and the word, for lint L011.</summary>
        private readonly Dictionary<int, string> preReleaseHeaders = [];
        private readonly Dictionary<string, (int, int)> layout = [];
        private readonly List<BlockNode> blocks = [];
        private readonly List<UseNode> uses = [];
        private readonly Dictionary<string, string> settings = new(StringComparer.Ordinal);

        public ProjectInfo? Project { get; private set; }

        public IReadOnlyDictionary<string, string> Settings => settings;

        public IReadOnlyList<HelperInfo> HelperInfos => helperTable.Infos;

        public IReadOnlyList<PropertyBinding> PropertyBindings => propertyBindings;

        public string StabilityState { get; private set; } = "stable";

        public IReadOnlyList<string> StabilityNotes => stabilityNotes;

        private readonly List<PropertyBinding> propertyBindings = [];

        private readonly List<string> stabilityNotes = [];

        private IReadOnlyDictionary<string, FlowEntry>? flowResults;

        private readonly DateTime started = DateTime.Now;   // currentTime() is read once per parse: one instant for the whole song

        private readonly List<(string Text, int Line)> helperSources = [];

        private readonly List<(string Name, string Value, int Line)> capabilityEntries = [];

        private CapabilityPolicy capabilityPolicy = CapabilityPolicy.AllowAll;

        public IReadOnlyList<CapabilityState> CapabilityStates => capabilityPolicy.States;

        public NotePipeline.HelperTable HelperTableValue => helperTable;

        public CapabilityPolicy PolicyValue => capabilityPolicy;

        public DateTime StartedValue => options.Clock?.Invoke() ?? started;

        public IReadOnlyList<(string Name, string Value, int Line)> CapabilitySettings => capabilityEntries;

        private NotePipeline.HelperTable helperTable = NotePipeline.HelperTable.Empty;

        public int SettingsLine { get; private set; } = -1;

        public string? Assist { get; private set; }

        public IReadOnlyDictionary<string, bool> Features => features;

        public IReadOnlyDictionary<string, string> LintOverrides => lintOverrides;

        public IReadOnlyDictionary<string, (int, int)> Layout => layout;

        public IReadOnlyList<BlockNode> Blocks => blocks;

        public IReadOnlyList<UseNode> Uses => uses;

        public IReadOnlyList<BlockNode> BlockList => blocks;

        public void AddExternal(IEnumerable<Diagnostic> items) => diagnostics.AddRange(items);

        public IReadOnlyList<Diagnostic> Sorted() => [.. diagnostics.Where(d => !Silenced(d)).Select((d, i) => (d, i)).OrderBy(x => x.d.Line).ThenBy(x => x.d.Col).ThenBy(x => x.i).Select(x => x.d)];

        /// <summary>Whether the line of a warning or a note ends with a comment "# ample-ignore CODE" that names its code: the person said they know. An error is never silenced.</summary>
        private bool Silenced(Diagnostic d)
        {
            if (d.Severity == Severity.Error || d.Line < 1 || d.Line > lines.Length) return false;
            if (ignoreFor.TryGetValue(d.Line - 1, out List<string>? above) && above.Contains(d.Code, StringComparer.OrdinalIgnoreCase)) return true;   // the comment line above a block header
            string text = lines[d.Line - 1];
            bool inString = false;
            for (int k = 0; k < text.Length; k++)
            {
                if (text[k] == '"') inString = !inString;
                else if (!inString && text[k] == '#' && !(k > 0 && char.IsLetterOrDigit(text[k - 1])))
                {
                    Match m = IgnorePattern().Match(text[k..]);
                    return m.Success && m.Groups[1].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Contains(d.Code, StringComparer.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        public void Execute()
        {
            BlockNode? cur = null;
            UseNode? curUse = null;
            List<string>? pending = null;
            List<int> legacyView = [];
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '#')
                {
                    if (cur is null && curUse is null && ViewBlock.IsLegacyLine(line))
                    {
                        legacyView.Add(i);
                    }

                    Match im = IgnorePattern().Match(line);
                    if (im.Success)
                    {
                        pending = [.. im.Groups[1].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];
                    }

                    continue;
                }

                if (pending is not null)
                {
                    ignoreFor[i] = pending;
                    pending = null;
                }

                if (curUse is not null)
                {
                    if (line == "}")
                    {
                        curUse.CloseLine = i;
                        curUse = null;
                        continue;
                    }

                    BodyLine(curUse.Parameters, curUse.Froms, line, i);
                    continue;
                }

                if (cur is not null)
                {
                    if (cur.Type == "midi-clip" && StreamLine().Match(lines[i]) is { Success: true } streamMatch)
                    {
                        string streamName = streamMatch.Groups[1].Value;
                        if (cur.Parameters.Any(p => p.Name == "random-stream:" + streamName)) Add(i, "AMP2040", Severity.Error, $"The random-stream \"{streamName}\" is declared twice in this clip.", new Opts { Needle = streamName });
                        else cur.Parameters.Add(new Parameter("random-stream:" + streamName, streamMatch.Groups[2].Value.Trim(), i));
                        continue;
                    }

                    if (cur.Type == "midi-clip" && NotesExpressionStart(lines[i], out int exprColumn))
                    {
                        int exprStart = i, exprLast = i, exprDepth = Depth(lines[i][exprColumn..], 0);
                        string exprText = lines[i][exprColumn..];
                        while (true)
                        {
                            int next = exprLast + 1;
                            while (next < lines.Length && (lines[next].Trim().Length == 0 || lines[next].Trim()[0] == '#')) next++;
                            if (next >= lines.Length) break;
                            bool goes = exprDepth > 0 ? lines[next] != "}" : lines[next].TrimStart().StartsWith("|>", StringComparison.Ordinal);
                            if (!goes) break;
                            for (int k = exprLast + 1; k <= next; k++)
                            {
                                exprText += "\n" + lines[k];
                                exprDepth = Depth(lines[k], exprDepth);
                            }

                            exprLast = next;
                        }

                        Parameter? firstNotes = cur.Parameters.FirstOrDefault(p => p.Name == "notes");
                        if (firstNotes is not null) Add(exprStart, "AMP1109", Severity.Error, "Parameter \"notes\" is given twice.", new Opts { Needle = "notes", Related = new Related(firstNotes.Line + 1, "first given here") });
                        cur.Parameters.RemoveAll(p => p.Name == "notes");
                        cur.Parameters.Add(new Parameter("notes", exprText, exprStart) { EndLine = exprLast, ValueColumn = exprColumn });
                        i = exprLast;
                        continue;
                    }

                    if (cur.Type == "midi-clip" && line == "notes {")
                    {
                        int start = i;
                        int depth = 1;
                        for (i++; i < lines.Length; i++)
                        {
                            string part = lines[i].Trim();
                            if (part == "note {") depth++;
                            else if (part == "}")
                            {
                                if (depth == 1 && lines[i].Length == part.Length) break;
                                depth--;
                                if (depth == 0) break;
                            }
                        }
                        int end = Math.Min(i, lines.Length - 1);
                        string source = string.Join('\n', lines[start..(end + (depth == 0 ? 1 : 0))]);
                        int ppq = cur.Parameters.FirstOrDefault(p => p.Name == "ppq") is { } scale && int.TryParse(scale.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedScale) ? parsedScale : StructuredMidiNotes.MicrobeatsPerBeat;
                        string compact = string.Empty;
                        bool valid = depth == 0 && ExpandedMidiNotes.TryCompact(source, ppq, out compact, Project?.BeatsPerBar ?? 4.0);
                        Parameter? first = cur.Parameters.FirstOrDefault(p => p.Name == "notes");
                        if (first is not null) Add(start, "AMP1109", Severity.Error, "Parameter \"notes\" is given twice.", new Opts { Needle = "notes", Related = new Related(first.Line + 1, "first given here") });
                        cur.Parameters.RemoveAll(p => p.Name == "notes");
                        cur.Parameters.Add(new Parameter("notes", valid ? compact : "__invalid_expanded_notes__", start) { EndLine = depth == 0 ? end : Math.Max(start, end - 1) });
                        if (depth != 0) i = Math.Max(start, end - 1);
                        continue;
                    }
                    // a sequence kept in the song itself: the lines up to the closing brace at the same indent are the text of a sequence file, taken as they are
                    if (cur.Type == "sequencer" && line == "sequence {")
                    {
                        int opener = i, indent = lines[i].Length - lines[i].TrimStart().Length, closer = -1;
                        for (int k = i + 1; k < lines.Length; k++)
                        {
                            if (lines[k].Trim() == "}" && lines[k].Length - lines[k].TrimStart().Length == indent) { closer = k; break; }
                        }

                        int last = closer < 0 ? lines.Length - 1 : closer;
                        List<string> body = [];
                        for (int k = opener + 1; k < (closer < 0 ? lines.Length : closer); k++)
                        {
                            int strip = Math.Min(indent + 2, lines[k].Length - lines[k].TrimStart().Length);
                            body.Add(lines[k][strip..]);
                        }

                        cur.Parameters.RemoveAll(p => p.Name == "sequence");
                        cur.Parameters.Add(new Parameter("sequence", string.Join('\n', body), opener) { EndLine = last });
                        i = last;
                        continue;
                    }

                    // the scratch block of a rewind: the readable form of its events text
                    if (cur.Type == "rewind" && line == "scratch {")
                    {
                        int opener = i, indent = lines[i].Length - lines[i].TrimStart().Length, closer = -1;
                        for (int k = i + 1; k < lines.Length; k++)
                        {
                            if (lines[k].Trim() == "}" && lines[k].Length - lines[k].TrimStart().Length == indent) { closer = k; break; }
                        }

                        int last = closer < 0 ? lines.Length - 1 : closer;
                        if (!RewindBlock.TryCompact(lines.Skip(opener + 1).Take(Math.Max(0, (closer < 0 ? lines.Length : closer) - opener - 1)), out string compactEvents, out string why))
                        {
                            Add(opener, "AMP1115", Severity.Error, "The scratch block of this rewind cannot be read.", new Opts { Hint = why });
                            compactEvents = string.Empty;
                        }

                        cur.Parameters.RemoveAll(p => p.Name == "events");
                        cur.Parameters.Add(new Parameter("events", compactEvents, opener) { EndLine = last });
                        i = last;
                        continue;
                    }

                    if (line == "}")
                    {
                        cur.CloseLine = i;
                        cur = null;
                        continue;
                    }

                    BodyLine(cur.Parameters, cur.Froms, line, i);
                    continue;
                }

                if (HelperStart().IsMatch(line) && lines[i].Length == lines[i].TrimStart().Length)
                {
                    // a helper, a type alias or a constant of the file: collected here, resolved after the whole file is read (so the order does not matter)
                    int declStart = i, declDepth = Depth(lines[i], 0);
                    string declText = lines[i];
                    while (i + 1 < lines.Length && (declDepth > 0 || lines[i + 1].TrimStart().StartsWith("|>", StringComparison.Ordinal)))
                    {
                        i++;
                        declText += "\n" + lines[i];
                        declDepth = Depth(lines[i], declDepth);
                    }

                    helperSources.Add((declText, declStart));
                    continue;
                }

                Match mm;
                if ((mm = ProjectPattern().Match(line)).Success)
                {
                    string rawTempo = mm.Groups[2].Value;
                    double tempo = double.Parse(rawTempo, CultureInfo.InvariantCulture);
                    if (tempo < spec.MinTempo || tempo > spec.MaxTempo)
                    {
                        string use = (tempo < spec.MinTempo ? spec.MinTempo : spec.MaxTempo).ToString(CultureInfo.InvariantCulture);
                        Add(i, "AMP1002", Severity.Error, $"Tempo must be between {Fmt(spec.MinTempo)} and {Fmt(spec.MaxTempo)} (found {rawTempo}).", new Opts { Needle = rawTempo, Hint = "Typical songs sit between 60 and 180.", FixLabel = "Use " + use, FixText = use });
                    }

                    string rawSeed = mm.Groups[3].Value;
                    if (!ulong.TryParse(rawSeed, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed) || seed > spec.MaxSeed)
                    {
                        Add(i, "AMP1009", Severity.Error, $"The master seed must be a whole number from 0 to {spec.MaxSeed.ToString(CultureInfo.InvariantCulture)} (found {rawSeed}).", new Opts { Needle = rawSeed, Hint = "Use a whole number in the supported unsigned 64-bit range." });
                        seed = 0;
                    }

                    // meter 3/4: how many beats a bar has (numerator 1 to 16 over 2, 4, 8 or 16); the default is 4/4
                    int meterNumerator = 4, meterDenominator = 4;
                    if (mm.Groups["mn"].Success)
                    {
                        int n = int.Parse(mm.Groups["mn"].Value, CultureInfo.InvariantCulture), d = int.Parse(mm.Groups["md"].Value, CultureInfo.InvariantCulture);
                        if (n is < 1 or > 16 || d is not (2 or 4 or 8 or 16))
                        {
                            Add(i, "AMP1120", Severity.Error, $"The meter {n}/{d} is not allowed: the top number is 1 to 16 and the bottom number is 2, 4, 8 or 16.", new Opts { Needle = mm.Groups["mn"].Value + "/" + mm.Groups["md"].Value, Hint = "Write for example meter 3/4, 6/8 or 5/4. Without a meter a song is in 4/4." });
                        }
                        else
                        {
                            (meterNumerator, meterDenominator) = (n, d);
                        }
                    }

                    int? ample = null;
                    if (mm.Groups["ample"].Success)
                    {
                        Group g = mm.Groups["ample"];
                        if (!int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int stated) || !AmpleVersions.IsKnown(stated))
                        {
                            Add(i, "AMP1119", Severity.Error, AmpleVersionMessage(g.Value), new Opts { ColStart = g.Index, ColEnd = g.Index + g.Length, Hint = AmpleVersionHint, FixLabel = "Use " + AmpleVersions.Current, FixText = AmpleVersions.Current.ToString(CultureInfo.InvariantCulture), FixFrom = g.Index, FixNeedle = g.Value });
                        }
                        else
                        {
                            ample = stated;
                        }
                    }
                    else
                    {
                        int brace = line.IndexOf('{', StringComparison.Ordinal);
                        Add(i, "AMP1118", Severity.Info, $"The song does not say which AMPLE version it was written for: it is read as the current one ({AmpleVersions.Current}).", new Opts { Needle = line.StartsWith("project", StringComparison.Ordinal) ? "project" : "composition", Hint = "Stating it keeps the song reading the same when AMPLE changes later.", FixLabel = $"Add ample {AmpleVersions.Current}", FixNeedle = "tempo", FixFrom = brace, FixText = $"ample {AmpleVersions.Current}  tempo" });
                    }

                    Project = new ProjectInfo(Unquote(mm.Groups[1].Value), tempo, seed, i, ample, meterNumerator, meterDenominator);
                    if (line.StartsWith("project", StringComparison.Ordinal))
                    {
                        // the old word still works (the song builds as it is); the warning offers the new one
                        Add(i, "AMP1117", Severity.Error, "\"project\" is the old word for the first line of a song: write \"composition\".", new Opts { Needle = "project", FixLabel = "Use composition", FixNeedle = "project", FixText = "composition", Hint = "Both words are read the same way. \"project\" also names the folder of songs, so the song line is called composition now." });
                    }

                    continue;
                }

                if (ProjectWord().IsMatch(line) && (line.StartsWith("project", StringComparison.Ordinal) || line.StartsWith("composition", StringComparison.Ordinal)))
                {
                    Add(i, "AMP1003", Severity.Error, "Expected: composition \"name\" { tempo N  master-seed N }", new Opts());
                    continue;
                }

                if ((mm = FeaturesPattern().Match(line)).Success)
                {
                    FeaturesLine(i, mm.Groups[1].Value);
                    continue;
                }

                if ((mm = LintPattern().Match(line)).Success)
                {
                    LintLine(i, mm.Groups[1].Value);
                    continue;
                }

                if ((mm = SettingsPattern().Match(line)).Success)
                {
                    SettingsLine = i;
                    SettingsBody(i, mm.Groups[1].Value);
                    continue;
                }

                if (ViewOpen().IsMatch(line))
                {
                    i = ViewBody(i);
                    continue;
                }

                if ((mm = LayoutPattern().Match(line)).Success)
                {
                    layout[Unquote(mm.Groups[1].Value)] = (int.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(mm.Groups[3].Value, CultureInfo.InvariantCulture));
                    continue;
                }

                if (UseWord().IsMatch(line))
                {
                    curUse = UseHeader(i, line);
                    continue;
                }

                if (line.EndsWith('{'))
                {
                    cur = Header(i, line);
                    continue;
                }

                Add(i, "AMP1100", Severity.Error, "Unexpected text.", new Opts { Hint = "Expected a composition, features, lint or layout line, a comment, or a block header ending with \"{\"." });
            }

            if (curUse is not null)
            {
                string raw = lines[curUse.Line];
                diagnostics.Add(new Diagnostic("AMP1101", Severity.Error, curUse.Line + 1, 1, CodePointColumn(raw, raw.Length) + 1, $"Block \"{curUse.Instance}\" is not closed (missing \"}}\").", "Every block opened with { needs a } on its own line.", new Fix("Add the missing \"}\" at the end of the file", "append", "}", 0, 0, 0, false), null));
            }

            if (cur is not null)
            {
                string raw = lines[cur.Line];
                diagnostics.Add(new Diagnostic("AMP1101", Severity.Error, cur.Line + 1, 1, CodePointColumn(raw, raw.Length) + 1, $"Block \"{cur.Name}\" is not closed (missing \"}}\").", "Every block opened with { needs a } on its own line.", new Fix("Add the missing \"}\" at the end of the file", "append", "}", 0, 0, 0, false), null));
            }

            if (Project is null && options.RequireProjectLine)
            {
                diagnostics.Add(new Diagnostic("AMP1001", Severity.Error, 1, 1, 2, "Missing the composition line.", "Start the file with: composition \"name\" { tempo 92  master-seed 424242 }", new Fix("Insert a composition line", "insert-top", "composition \"song\" { ample 1  tempo 92  master-seed 424242 }", 0, 0, 0, false), null));
            }

            ParameterAliases();
            LegacyView(legacyView);
            Semantic();
            Lint();
            RequiredSongSettings();
            AutotuneMethod();
            if (options.ModelRules)
            {
                TurboGuidance();
            }
        }

        // The view block (groups, sizes and pins of the canvas): every line is checked, and the song does not depend on it. Returns the index of its closing line.
        // a parameter that reads controls: "cutoff 600 + 3000 * traffic.level"
        /// <summary>Rewrites a parameter written under an accepted extra name (a consistent name such as <c>rate</c>) to the real name, so every later step sees one spelling.</summary>
        private void ParameterAliases()
        {
            foreach (BlockNode b in blocks)
            {
                if (!spec.Templates.TryGetValue(b.Type, out TemplateSpec? t)) continue;
                for (int i = 0; i < b.Parameters.Count; i++)
                {
                    Parameter p = b.Parameters[i];
                    if (t.Parameters.ContainsKey(p.Name)) continue;
                    foreach (TemplateParameterSpec ps in t.ParameterSpecs.Values)
                    {
                        ParameterAlias? a = ps.Aliases?.FirstOrDefault(x => x.Name == p.Name);
                        if (a is null) continue;
                        string value = p.Value;
                        if (a.Convert == "hz-to-period-ms")
                        {
                            string raw = value.Trim().Trim('"');
                            if (raw.EndsWith("Hz", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2].Trim();
                            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) && hz > 0) value = (1000.0 / hz).ToString("0.###", CultureInfo.InvariantCulture) + " ms";
                        }

                        b.Parameters[i] = p with { Name = ps.Name, Value = value };
                        break;
                    }
                }
            }
        }

        private void CheckControlExpression(BlockNode b, TemplateSpec t, TemplateParameterSpec spec1, Parameter p, List<BlockNode> blocks, HashSet<string> useNames, string[] titles)
        {
            ControlExpression? expression = ControlExpression.TryParse(p.Value, out string? error);
            if (expression is null)
            {
                Add(p.Line, "AMP2251", Severity.Error, $"The value of {p.Name} is not a valid control expression: {error}.", new Opts { Needle = p.Name, Hint = "Numbers with a unit, + - *, brackets and block.output, for example: 600 + 3000 * traffic.level" });
                return;
            }

            if (b.Froms.Any(f => !f.Disabled && f.Input == p.Name))
            {
                Add(p.Line, "AMP2254", Severity.Error, $"{p.Name} has a connection and an expression: a parameter has one source.", new Opts { Needle = p.Name, Hint = "Remove the from line or write the value without a control." });
            }

            foreach (string unit in expression.Units.Distinct())
            {
                if (spec1.Unit is not null && unit != spec1.Unit)
                {
                    Add(p.Line, "AMP2253", Severity.Error, $"Wrong unit: {p.Name} is in {spec1.Unit} but the expression has {unit}.", new Opts { Needle = unit, Hint = "Use numbers in the unit of the parameter." });
                }
            }

            foreach (ControlExpression.Reference r in expression.References)
            {
                if (useNames.Contains(r.Block)) continue;
                BlockNode? src = blocks.FirstOrDefault(x => x.Name == r.Block && !x.Skipped);
                if (src is null)
                {
                    string? near = Nearest(r.Block, titles);
                    Add(p.Line, "AMP2004", Severity.Error, $"There is no block named \"{r.Block}\".", new Opts { Needle = r.Block, Hint = near is null ? string.Empty : DidYou(near) });
                    continue;
                }

                if (ReferenceEquals(src, b))
                {
                    Add(p.Line, "AMP2009", Severity.Error, "A block cannot read from itself.", new Opts { Needle = r.Block });
                    continue;
                }

                IReadOnlyList<PortSpec> outs = Ports(src).Outputs;
                int oi = IndexOf(outs, r.Output);
                if (oi < 0)
                {
                    string? near = Nearest(r.Output, outs.Where(o => ControlTypes.Is(o.Type)).Select(o => o.Name));
                    Add(p.Line, "AMP2005", Severity.Error, $"Block \"{r.Block}\" has no output \"{r.Output}\".", new Opts { Needle = r.Output, Hint = near is null ? string.Empty : DidYou(near) });
                    continue;
                }

                string type = ControlTypes.ResolveOutput(src, outs[oi].Type);
                if (!ControlTypes.Is(type))
                {
                    Add(p.Line, "AMP2252", Severity.Error, $"{r.Block}.{r.Output} is {type}, not a control: only a control can steer a parameter.", new Opts { Needle = r.Output, Hint = "Use a follow, curve or map block and read its level." });
                    continue;
                }

                string? unitOfType = ControlTypes.UnitOf(type);
                if (unitOfType is not null && spec1.Unit is not null && unitOfType != spec1.Unit)
                {
                    Add(p.Line, "AMP2253", Severity.Error, $"Wrong unit: {r.Block}.{r.Output} is in {unitOfType} but {p.Name} is in {spec1.Unit}.", new Opts { Needle = r.Output, Hint = "Convert it with a map block." });
                    continue;
                }

                b.ControlRefs.Add(new ControlRef(r.Block, r.Output, p.Name, p.Line));
            }
        }

        private static string AmpleVersionMessage(string stated) => int.TryParse(stated, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > AmpleVersions.Current
            ? $"This song asks for AMPLE {stated}, but this AMPLE only knows versions up to {AmpleVersions.Current}: open it with a newer AMPLE."
            : $"AMPLE {stated} is not a version this AMPLE knows (it reads {AmpleVersions.Oldest} to {AmpleVersions.Current}).";

        private const string AmpleVersionHint = "The version is a whole number; leave it out to read the song as the current one.";

        private int ViewBody(int open)
        {
            bool inGroup = false;
            for (int i = open + 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line == "}")
                {
                    if (!inGroup)
                    {
                        return i;
                    }

                    inGroup = false;
                    continue;
                }

                (bool opens, string error) = ViewBlock.CheckLine(line, inGroup);
                if (error.Length > 0)
                {
                    Add(i, "AMP1114", Severity.Error, "This line does not belong in the view block.", new Opts { Hint = error });
                }

                inGroup |= opens;
            }

            string raw = lines[open];
            diagnostics.Add(new Diagnostic("AMP1101", Severity.Error, open + 1, 1, CodePointColumn(raw, raw.Length) + 1, "The view block is not closed (missing \"}\").", "Every block opened with { needs a } on its own line.", new Fix("Add the missing \"}\" at the end of the file", "append", "}", 0, 0, 0, false), null));
            return lines.Length - 1;
        }

        // The old comment lines that kept the view: one hint per run of them, with a fix that turns the run into a view block.
        private void LegacyView(List<int> legacy)
        {
            for (int k = 0; k < legacy.Count;)
            {
                int first = legacy[k], last = first;
                for (k++; k < legacy.Count && lines.Skip(last + 1).Take(legacy[k] - last - 1).All(l => l.Trim().Length == 0); k++)
                {
                    last = legacy[k];
                }

                string firstRaw = lines[first], lastRaw = lines[last];
                Fix fix = new("Upgrade the view lines to a view { } block", "replace", ViewBlock.Upgrade(lines.Skip(first).Take(last - first + 1).Where(l => l.Trim().Length > 0)), first + 1, 1, CodePointColumn(lastRaw, lastRaw.Length) + 1, false, last + 1);
                diagnostics.Add(new Diagnostic("AMP1113", Severity.Info, first + 1, 1, CodePointColumn(firstRaw, firstRaw.Length) + 1, "The view of the canvas is kept in comment lines.", "Upgrade them to a view { } block, which is checked like the rest of the song.", fix, null));
            }
        }

        // A turbo model is distilled for CFG 1; the ACE-Step plugin refuses another value at build time, so the editor says so at the line and offers to set it.
        // Same rule as web/lib/ace-generation.mjs aceFamily: the file name wins (family followed by - . _ or the end); without a file the synthModel counts (xl- ignored); anything else is "unknown", and unknown is free.
        internal static string AceFamily(IReadOnlyDictionary<string, string> settings)
        {
            string file = settings.GetValueOrDefault("acestep.synthModelFile", string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
            if (file.Length > 0)
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(file, "(?:^|[-._])(sft|base|turbo)(?:[-._]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "unknown";
            }

            string kind = settings.GetValueOrDefault("acestep.synthModel", string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
            if (kind.StartsWith("xl-", StringComparison.Ordinal))
            {
                kind = kind[3..];
            }

            return kind is "sft" or "base" or "turbo" ? kind : "unknown";
        }

        // The model a block names in its engine parameter (route:model) wins over the settings of the song, like at build time: a family replaces a model file of the song.
        private static IReadOnlyDictionary<string, string> WithEngineModel(IReadOnlyDictionary<string, string> settings, BlockNode block)
        {
            string engine = block.Parameters.FirstOrDefault(p => p.Name == "engine")?.Value.Trim().Trim('"').Trim() ?? string.Empty;
            int colon = engine.IndexOf(':', StringComparison.Ordinal);
            string model = colon < 0 ? string.Empty : engine[(colon + 1)..].Trim();
            if (model.Length == 0)
            {
                return settings;
            }

            Dictionary<string, string> merged = new(settings, StringComparer.Ordinal);
            if (model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                merged["acestep.synthModelFile"] = model;
            }
            else
            {
                merged["acestep.synthModel"] = model;
                merged.Remove("acestep.synthModelFile");
            }

            return merged;
        }

        private void TurboGuidance()
        {
            foreach (BlockNode block in blocks.Where(b => !b.Skipped && spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Plugin == "acestep"))
            {
                Parameter? guidance = block.Parameters.FirstOrDefault(p => p.Name == "guidance");
                if (AceFamily(WithEngineModel(settings, block)) != "turbo")
                {
                    continue;
                }

                if (guidance is null || !double.TryParse(guidance.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value == 1)
                {
                    continue;
                }

                string text = lines[guidance.Line], token = guidance.Value.Trim();
                int indent = text.Length - text.TrimStart().Length;
                int start = CodePointColumn(text, indent) + 1, end = CodePointColumn(text, text.Length) + 1;
                int nameAt = text.IndexOf("guidance", indent, StringComparison.Ordinal);
                int valueAt = nameAt >= 0 && token.Length > 0 ? text.IndexOf(token, nameAt + "guidance".Length, StringComparison.Ordinal) : -1;
                Fix? fix = valueAt < 0
                    ? null
                    : new Fix("Set guidance to 1 (what a turbo model uses)", "replace", "1", guidance.Line + 1, CodePointColumn(text, valueAt) + 1, CodePointColumn(text, valueAt + token.Length) + 1, false);
                diagnostics.Add(new Diagnostic("AMP2014", Severity.Error, guidance.Line + 1, start, end, $"A turbo model works with CFG 1 only, and this block asks for {token}: the build would refuse it.", "Use the quick fix, or choose a Base or SFT model in the settings of this song.", fix, null));
            }
        }

        // an autotune block says which method it uses (the sound depends on it a lot): a block without it gets an error with a quick fix that writes the spectral method
        private void AutotuneMethod()
        {
            foreach (BlockNode b in blocks.Where(b => !b.Skipped && b.Type == "autotune" && b.Parameters.All(p => p.Name != "method")))
            {
                string header = lines[b.Line];
                Fix? fix = header.TrimEnd().EndsWith('{') ? new Fix("Add method spectral", "replace", "  method spectral\n", b.Line + 2, 1, 1, false) : null;
                diagnostics.Add(new Diagnostic("AMP2016", Severity.Error, b.Line + 1, 1, CodePointColumn(header, header.Length) + 1, $"The autotune block \"{b.Name}\" does not say its method: write method spectral (keeps the colour of the voice, the default) or method psola.", "Use the quick fix: it writes method spectral.", fix, null));
            }
        }

        private void RequiredSongSettings()
        {
            foreach ((string plugin, IReadOnlyDictionary<string, string> needs) in options.SongRequirements)
            {
                BlockNode? first = blocks.FirstOrDefault(b => !b.Skipped && spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Plugin == plugin);
                List<KeyValuePair<string, string>> missing = [.. needs.Where(n => !settings.ContainsKey(plugin + "." + n.Key))];
                if (first is null || missing.Count == 0)
                {
                    return;
                }

                Dictionary<string, string> all = new(settings, StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> m in missing)
                {
                    all[plugin + "." + m.Key] = m.Value;
                }

                string text = "settings { " + string.Join("  ", all.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " " + Edits.Quote(p.Value))) + " }";
                string names = string.Join(", ", missing.Select(m => plugin + "." + m.Key));
                Fix fix;
                if (SettingsLine >= 0)
                {
                    fix = new Fix($"Add {names} (the project default)", "replace", text, SettingsLine + 1, 1, CodePointColumn(lines[SettingsLine], lines[SettingsLine].Length) + 1, false);
                }
                else
                {
                    fix = new Fix($"Add {names} (the project default)", "replace", text + "\n", (Project?.Line ?? -1) + 2, 1, 1, false);
                }

                string header = lines[first.Line];
                diagnostics.Add(new Diagnostic("AMP2013", Severity.Warning, first.Line + 1, 1, CodePointColumn(header, header.Length) + 1, $"This song uses the {plugin} plugin but does not state {names}: every song says it itself (the project value is only the default for new songs).", "Use the quick fix, or choose it in the plugin settings of this song. A song without it fails to build with AMP5370.", fix, null));
                return;
            }
        }

        private static string Fmt(double d) => d.ToString("0.##########", CultureInfo.InvariantCulture);

        private void FeaturesLine(int i, string inner)
        {
            IReadOnlyList<string> tk = Tokenize(inner);
            for (int k = 0; k < tk.Count; k += 2)
            {
                string key = tk[k];
                string? val = k + 1 < tk.Count ? tk[k + 1] : null;
                if (key == "assist")
                {
                    if (val is null || !AssistLevels.Contains(val))
                    {
                        string? near = val is null ? null : Nearest(val, AssistLevels);
                        Add(i, "AMP1006", Severity.Error, $"assist must be off, diff, button or auto (found \"{val}\").", new Opts { Needle = val, FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                    }
                    else
                    {
                        Assist = val;
                    }
                }
                else if (!spec.Modules.Contains(key))
                {
                    string? near = Nearest(key, spec.Modules);
                    Add(i, "AMP1004", Severity.Error, $"Unknown module \"{key}\".", new Opts { Needle = key, Hint = DidYou(near) + "  Modules: " + string.Join(", ", spec.Modules), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                }
                else if (val is not ("on" or "off"))
                {
                    Add(i, "AMP1005", Severity.Error, $"Module \"{key}\" must be on or off.", new Opts { Needle = val ?? key });
                }
                else
                {
                    features[key] = val == "on";
                }
            }
        }

        private void SettingsBody(int i, string inner)
        {
            IReadOnlyList<string> tk = Tokenize(inner);
            for (int k = 0; k < tk.Count; k += 2)
            {
                if (!SettingKey().IsMatch(tk[k]) || k + 1 >= tk.Count)
                {
                    Add(i, "AMP1112", Severity.Error, "Expected pairs like: settings { acestep.synthModel sft }", new Opts { Needle = tk[k], Hint = "A setting is <plugin>.<name> followed by its value; text with spaces goes in quotes." });
                    continue;
                }

                settings[tk[k]] = Unquote(tk[k + 1]);
                if (tk[k].StartsWith("capabilities.", StringComparison.Ordinal)) capabilityEntries.Add((tk[k]["capabilities.".Length..], Unquote(tk[k + 1]), i));
            }
        }

        private void LintLine(int i, string inner)
        {
            IReadOnlyList<string> tk = Tokenize(inner);
            for (int k = 0; k < tk.Count; k += 2)
            {
                string code = tk[k];
                string? level = k + 1 < tk.Count ? tk[k + 1] : null;
                if (!spec.LintRules.ContainsKey(code))
                {
                    string? near = Nearest(code, spec.LintRules.Keys);
                    Add(i, "AMP1007", Severity.Error, $"Unknown lint rule \"{code}\".", new Opts { Needle = code, Hint = DidYou(near), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                }
                else if (level is null || !LintLevels.Contains(level))
                {
                    string? near = level is null ? null : Nearest(level, LintLevels);
                    Add(i, "AMP1008", Severity.Error, "Level must be off, info, warning or error.", new Opts { Needle = level ?? code, FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                }
                else
                {
                    lintOverrides[code] = level;
                }
            }
        }

        private BlockNode Header(int i, string line)
        {
            IReadOnlyList<string> tk = Tokenize(line[..^1].Trim());
            if (tk.Count < 2)
            {
                Add(i, "AMP1102", Severity.Error, "Expected a block header: <type> <name> [pinned@<version>] [at ...] {", new Opts { Hint = "For example: drums beats at 1 for 16 {" });
                return new BlockNode { Name = "?", RawName = "?", Line = i, Skipped = true };
            }

            BlockNode b = new() { Type = tk[0], Name = Unquote(tk[1]), RawName = tk[1], Line = i };
            if (!spec.Templates.ContainsKey(b.Type))
            {
                string? near = Nearest(b.Type, spec.Templates.Keys);
                Add(i, "AMP2001", Severity.Error, $"Unknown block type \"{b.Type}\".", new Opts { Needle = b.Type, Hint = DidYou(near) + "  Types: " + string.Join(", ", spec.Templates.Keys), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
            }

            // v1: <type> <name> [pinned@<version>] [at ...] [flags] [freeze ...] {; the class comes from the definition of the block.
            // The pre-release form <type> <name> <class>@<version> is still read (lint L011 names it, ample migrate converts it).
            bool component = spec.Templates.TryGetValue(b.Type, out TemplateSpec? headerTemplate) && headerTemplate.Component;
            int k = 2;
            Match cv = tk.Count > 2 ? (component ? ComponentClassVersionPattern() : ClassVersionPattern()).Match(tk[2]) : Match.Empty;
            if (cv.Success)
            {
                k = 3;
                b.Class = cv.Groups[1].Value;
                b.Version = cv.Groups[2].Value;
                if (b.Class != "pinned")
                {
                    preReleaseHeaders[i] = tk[2];
                }
            }
            else if (tk.Count > 2 && tk[2].Contains('@', StringComparison.Ordinal))
            {
                k = 3;
                Add(i, "AMP1103", Severity.Error, "Expected pinned@<version>, for example pinned@1.2.", new Opts { Needle = tk[2], Hint = "Only a pinned block states a version (major.minor); any other block follows its definition." });
            }
            else
            {
                b.Class = headerTemplate?.Class == "live" ? "live" : "stable";
                b.Version = component ? string.Empty : headerTemplate?.Version ?? string.Empty;
            }

            if (k < tk.Count && tk[k] == "at")
            {
                k++;
                while (true)
                {
                    string atText = k < tk.Count ? tk[k] : string.Empty;
                    string kw = k + 1 < tk.Count ? tk[k + 1] : string.Empty;
                    string lenText = k + 2 < tk.Count ? tk[k + 2] : string.Empty;
                    int len = 1;
                    bool autoLength = lenText == "auto";
                    if (!int.TryParse(atText, NumberStyles.None, CultureInfo.InvariantCulture, out int at) || kw != "for" || (!autoLength && !int.TryParse(lenText, NumberStyles.None, CultureInfo.InvariantCulture, out len)))
                    {
                        Add(i, "AMP1104", Severity.Error, "Expected: at <bar> for <bars> [as <name>] (several, separated by commas).", new Opts { Needle = atText.Length > 0 ? atText : "at", Hint = "For example: at 1 for 8 as verse, 9 for 8 as chorus" + (atText.Contains('.', StringComparison.Ordinal) || lenText.Contains('.', StringComparison.Ordinal) ? ". A placement is in whole bars: to start a sound seconds or beats later use a shift block, to leave pauses between clips a join block (or [pause 800 ms] in voice-clone text)." : string.Empty) });
                        break;
                    }

                    if (at < 1)
                    {
                        Add(i, "AMP1106", Severity.Error, "A bar number starts at 1.", new Opts { Needle = atText });
                    }

                    if (len < 1)
                    {
                        Add(i, "AMP1106", Severity.Error, "A length is at least 1 bar.", new Opts { Needle = lenText });
                    }

                    if (at + len > spec.MaxBar + 1)
                    {
                        string shorter = (spec.MaxBar + 1 - at).ToString(CultureInfo.InvariantCulture);
                        bool canShorten = at >= 1 && at <= spec.MaxBar;
                        Add(i, "AMP5002", Severity.Warning, $"This part reaches past bar {spec.MaxBar}.", canShorten ? new Opts { Needle = lenText, FixLabel = $"Shorten to {shorter} bar(s)", FixNeedle = lenText, FixText = shorter } : new Opts { Needle = lenText });
                    }

                    k += 3;
                    string name = string.Empty;
                    if (k < tk.Count && tk[k] == "as")
                    {
                        name = k + 1 < tk.Count ? Unquote(tk[k + 1]) : string.Empty;
                        k += 2;
                    }

                    b.Parts.Add(new Part(at, len, name) { Auto = autoLength });
                    if (k < tk.Count && tk[k] == ",")
                    {
                        k++;
                        continue;
                    }

                    break;
                }
            }

            while (k < tk.Count)
            {
                if (tk[k] == "freeze")
                {
                    string? level = k + 1 < tk.Count ? tk[k + 1] : null;
                    if (level is not ("seeds" or "plan"))
                    {
                        string? near = level is null ? null : Nearest(level, ["seeds", "plan"]);
                        Add(i, "AMP1105", Severity.Error, "freeze must be followed by seeds or plan.", new Opts { Needle = "freeze", Hint = "seeds fixes both seeds; plan stores the melody codes too. The rendered take is frozen with the word bake.", FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                    }
                    else
                    {
                        b.Freeze = level;
                    }

                    k += 2;
                    continue;
                }

                if (HeaderFlags.Contains(tk[k]))
                {
                    b.Flags.Add(tk[k]);
                }
                else
                {
                    string? near = Nearest(tk[k], HeaderFlags);
                    Add(i, "AMP1105", Severity.Error, $"Unknown word \"{tk[k]}\" in the block header.", new Opts { Needle = tk[k], Hint = "After the placement only bake, lock, refresh and disabled are allowed." + (near is null ? string.Empty : $"  Did you mean \"{near}\"?"), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                }

                k++;
            }

            blocks.Add(b);
            return b;
        }

        private UseNode? UseHeader(int i, string line)
        {
            IReadOnlyList<string> tk = Tokenize(line.EndsWith('{') ? line[..^1].Trim() : line);
            Match module = tk.Count == 4 ? UseModulePattern().Match(tk[1]) : Match.Empty;
            if (!line.EndsWith('{') || tk.Count != 4 || tk[2] != "as" || !module.Success)
            {
                Add(i, "AMP1110", Severity.Error, "Expected: use <module>[@version] as <name> {", new Opts { Hint = "For example: use drum-groove@1.0 as intro {" });
                return line.EndsWith('{') ? new UseNode { Module = tk.Count > 1 ? Unquote(tk[1]) : string.Empty, Instance = "?", RawInstance = "?", Line = i } : null;
            }

            UseNode u = new() { Module = Unquote(module.Groups[1].Value), Version = module.Groups[2].Value, Instance = Unquote(tk[3]), RawInstance = tk[3], Line = i };
            uses.Add(u);
            return u;
        }

        private void BodyLine(List<Parameter> parameters, List<FromLine> froms, string line, int i)
        {
            if (line.StartsWith("from", StringComparison.Ordinal) && (line.Length == 4 || char.IsWhiteSpace(line[4])))
            {
                bool disabled = line.EndsWith(" disabled", StringComparison.Ordinal);
                Match fm = FromPattern().Match(disabled ? line[..^9].TrimEnd() : line);
                if (!fm.Success)
                {
                    Add(i, "AMP1107", Severity.Error, "Expected: from <block>.<output> -> <input>", new Opts { Hint = "For example: from chords.notes -> notes" });
                }
                else
                {
                    froms.Add(new FromLine(Unquote(fm.Groups[1].Value), Unquote(fm.Groups[2].Value), Unquote(fm.Groups[3].Value), i, fm.Groups[1].Value, fm.Groups[2].Value, fm.Groups[3].Value) { Disabled = disabled });
                }

                return;
            }

            Match pm = ParameterPattern().Match(line);
            if (!pm.Success)
            {
                Add(i, "AMP1108", Severity.Error, "Expected \"<parameter> <value>\", \"from ...\" or \"}\".", new Opts { Hint = "A parameter is a name followed by its value, for example: kit kit/tape" });
                return;
            }

            string name = pm.Groups[1].Value;
            Parameter? first = parameters.FirstOrDefault(p => p.Name == name);
            if (first is not null)
            {
                Add(i, "AMP1109", Severity.Error, $"Parameter \"{name}\" is given twice.", new Opts { Needle = name, Related = new Related(first.Line + 1, "first given here") });
            }

            parameters.RemoveAll(p => p.Name == name);
            parameters.Add(new Parameter(name, pm.Groups[2].Value.Trim(), i));
        }

        private static bool NotesExpressionStart(string raw, out int column)
        {
            column = 0;
            string t = raw.TrimStart();
            int indent = raw.Length - t.Length;
            if (!t.StartsWith("notes", StringComparison.Ordinal) || t.Length < 7 || !char.IsWhiteSpace(t[5])) return false;
            int k = 5;
            while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
            column = indent + k;
            return NotePipeline.IsExpression(t[k..]);
        }

        private static int Depth(string text, int depth)
        {
            bool inString = false;
            for (int k = 0; k < text.Length; k++)
            {
                char c = text[k];
                if (c == '"') inString = !inString;
                else if (!inString && c == '#' && !(k > 0 && char.IsLetterOrDigit(text[k - 1]))) break;
                else if (!inString && c is '{' or '(') depth++;
                else if (!inString && c is '}' or ')') depth = Math.Max(0, depth - 1);
            }

            return depth;
        }

        private static (string Code, Severity Severity) FlowCode(string id) => id switch
        {
            "syntax" => ("AMP1111", Severity.Error),
            "unknown-operator" => ("AMP2023", Severity.Error),
            "wrong-unit" or "bad-argument" => ("AMP2024", Severity.Error),
            "type-mismatch" => ("AMP2044", Severity.Error),
            "lossy-conversion" => ("AMP2052", Severity.Error),
            "conversion" => ("AMP2053", Severity.Error),
            "emit-return" => ("AMP2054", Severity.Error),
            "shared-stable" => ("AMP2055", Severity.Info),
            "while-limit" => ("AMP2056", Severity.Error),
            "helper-effect" => ("AMP2060", Severity.Error),
            "helper-naming" => ("AMP2078", Severity.Warning),
            "deprecated-name" => ("AMP2079", Severity.Warning),
            "capability-denied" => ("AMP2077", Severity.Error),
            "unknown-helper" => ("AMP2061", Severity.Error),
            "helper-casing" => ("AMP2062", Severity.Warning),
            "duplicate-helper" => ("AMP2064", Severity.Error),
            "recursion-needs-type" => ("AMP2065", Severity.Error),
            "unknown-type" => ("AMP2066", Severity.Error),
            "undefined-variable" or "immutable-variable" or "duplicate-variable" or "cycle" or "divide-by-zero" => ("AMP2067", Severity.Error),
            "stale-value-bake" => ("AMP2069", Severity.Warning),
            "value-bake-override" => ("AMP2070", Severity.Info),
            "dirty-value-bake" => ("AMP2072", Severity.Warning),
            "unreachable-code" => ("AMP2058", Severity.Info),
            "undefined-name" => ("AMP2082", Severity.Warning),
            "no-value" => ("AMP2083", Severity.Warning),
            "unused-variable" => ("AMP2084", Severity.Info),
            "no-effect" => ("AMP2085", Severity.Info),
            "unused-helper" => ("AMP2086", Severity.Info),
            "helper-broken" => ("AMP2087", Severity.Info),
            "unknown-name" or "ambiguous-name" => ("AMP2074", Severity.Error),
            "unsupported" => ("AMP2075", Severity.Error),
            "dynamic-property" => ("AMP2073", Severity.Info),
            "loop-limit" => ("AMP5710", Severity.Error),
            "fuel" => ("AMP5711", Severity.Error),
            "stack-depth" => ("AMP5712", Severity.Error),
            "memory" => ("AMP5713", Severity.Error),
            "quoted-word" => ("AMP2045", Severity.Warning),
            "missing-unit" => ("AMP2046", Severity.Error),
            "fractional-interval" => ("AMP2048", Severity.Error),
            "span-mismatch" => ("AMP2049", Severity.Info),
            "not-whole" => ("AMP2050", Severity.Error),
            "wrong-dimension" => ("AMP2051", Severity.Error),
            "range-reversed" => ("AMP2025", Severity.Error),
            "fractional-bar" => ("AMP2026", Severity.Warning),
            "before-origin" => ("AMP2027", Severity.Warning),
            "unknown-reference" => ("AMP2028", Severity.Error),
            "reference-cycle" => ("AMP2029", Severity.Error),
            "stale-bake" => ("AMP2030", Severity.Warning),
            "dirty-bake" => ("AMP2031", Severity.Warning),
            "bad-notes" => ("AMP2012", Severity.Error),
            "missing-file" => ("AMP2033", Severity.Error),
            "unbaked" => ("AMP2034", Severity.Info),
            "cap-events" or "cap-time" => ("AMP5701", Severity.Error),
            "missing-seed" => ("AMP2024", Severity.Error),
            "cost-estimate" => ("AMP2037", Severity.Warning),
            "seed-error" => ("AMP2038", Severity.Error),
            "unresolved-random-seed" => ("AMP2039", Severity.Error),
            "stream-error" => ("AMP2040", Severity.Error),
            "seed-typo" => ("AMP2041", Severity.Warning),
            "same-seed" => ("AMP2042", Severity.Info),
            "unused-stream" => ("AMP2043", Severity.Info),
            _ => ("AMP2024", Severity.Error),
        };

        private void ReportFlow(Parameter p, PipeIssue issue, string? strictStage = null)
        {
            (string code, Severity severity) = FlowCode(issue.Id);
            if (issue.Strict) severity = Severity.Error;
            string value = p.Value;
            int s = Math.Clamp(issue.Start, 0, value.Length);
            int nl = 0, lineStart = 0;
            for (int k = 0; k < s; k++)
            {
                if (value[k] == '\n') { nl++; lineStart = k + 1; }
            }

            int lineEnd = value.IndexOf('\n', s);
            int e = Math.Clamp(issue.End, s, lineEnd < 0 ? value.Length : lineEnd);
            int baseCol = nl == 0 ? p.ValueColumn : 0;
            string hint = issue.Id switch
            {
                "unknown-operator" => issue.Suggestion is { Length: > 0 } ? $"Did you mean {issue.Suggestion}?" : "Operators: bars, beats, time, mask, remove, cut, replace, transpose, shift, concat, merge, bake.",
                "fractional-bar" => "A position inside a bar is allowed, but check that you meant it (1 is the start of bar 1, 1.5 the middle of it).",
                "stale-bake" => "Bake the stage again, or detach it into plain notes.",
                "dirty-bake" => "Bake the stage again to go back, or accept the change by baking it as editable.",
                "unbaked" => "Bake it to store the notes of this pipeline up to here.",
                "seed-typo" => "A stable name is only a label: use the same name to stay in the same random family.",
                "unresolved-random-seed" => "Pick a seed (the button of the tooltip, or ample seeds --pick): it is written into the song as a number, so the song stays reproducible.",
                "same-seed" => "Give the second one its own fork, for example seed=stable(\"verse\").fork(\"second\"), if they should differ.",
                "helper-effect" => "A helper that reads the clock, OS randomness or the environment is dynamic: its result can differ between builds. Mark it dynamic, or bake the dependency (bake currentTime() as ...).",
                "helper-casing" => "Names are compared ignoring case; the declaration spells it the other way.",
                "shared-stable" => "Pass the seed in (RandomSource seed) and fork it per call, for example seed.fork(\"voice\", i).",
                "while-limit" => "A while loop always has a max. Raise it, or write max N stop when stopping there is intended.",
                "stale-value-bake" => "Bake the value again, or make it dynamic again by removing bake ... as ....",
                "dirty-value-bake" => "Write override to keep the number on purpose, or bake it again.",
                "type-mismatch" => "The catalogue says what each argument is (hover the operator, or see docs/language/15-musical-types.md).",
                "undefined-name" => "A word that is not a parameter, a variable, a constant or a word of the language is a symbol, and that is rarely meant. Declare it with let, or fix the spelling.",
                "no-value" => "Every path of a helper that says what it returns must end with return VALUE (or an expression). Add an else.",
                "unused-variable" => "Remove it, or use it.",
                "no-effect" => "A statement is run for what it does: a call, a return, an emit, a let or an assignment.",
                "unused-helper" => "Call it from a clip or a property, remove it, or silence this with # ample-ignore AMP2086 on the line above.",
                "helper-broken" => "The error is in the declaration of the helper; this call follows once it is fixed.",
                "quoted-word" => "Pitch classes, scales and modes are bare words: walk(C, minor, ...).",
                "missing-unit" => "A length needs a unit: beat, bar, tick, ms or s (the choice changes the music, so it is not made for you).",
                "wrong-dimension" => "Pitch distances use st, oct or cents; lengths use bar, beat, tick; times use ms or s.",
                "not-whole" => "Use Round, Floor or Ceil of the value in your head and write the whole number.",
                "fractional-interval" => "Use a whole number of semitones for now.",
                "span-mismatch" => "Write for auto to follow the notes that feed this block.",
                "cost-estimate" => "Make the pipeline smaller, or raise the limit of the song: settings { notes.maxEvents 500000 } (never more than the limit of the server, 2000000).",
                _ => string.Empty,
            };
            string? typoName = issue.Id == "seed-typo" && issue.Suggestion is not null ? System.Text.RegularExpressions.Regex.Match(value.Substring(s, e - s), "\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value : null;
            Opts opts = typoName is { Length: > 0 }
                ? new Opts { ColStart = baseCol + (s - lineStart), ColEnd = baseCol + (e - lineStart), Hint = hint, FixLabel = $"Use \"{issue.Suggestion}\"", FixNeedle = "\"" + typoName + "\"", FixFrom = baseCol + (s - lineStart), FixText = "\"" + issue.Suggestion + "\"" }
                : issue.Id is "type-mismatch" or "quoted-word" or "not-whole" or "bad-argument" or "helper-casing" or "unknown-helper" or "helper-effect" or "lossy-conversion" or "unknown-name" or "undefined-name" or "ambiguous-name" or "deprecated-name" or "stale-value-bake" or "dirty-value-bake" or "unknown-type" && issue.Suggestion is { Length: > 0 }
                ? new Opts { ColStart = baseCol + (s - lineStart), ColEnd = baseCol + (e - lineStart), Hint = hint, FixLabel = issue.Id switch { "helper-effect" => "Mark the helper dynamic", "stale-value-bake" or "dirty-value-bake" => "Bake this value again", "lossy-conversion" => "Say how: Round(...)", _ => $"Change to {issue.Suggestion}" }, FixNeedle = value.Substring(s, e - s), FixFrom = baseCol + (s - lineStart), FixText = issue.Suggestion }
                : issue.Id == "unknown-operator" && issue.Suggestion is { Length: > 0 }
                ? new Opts { ColStart = baseCol + (s - lineStart), ColEnd = baseCol + (e - lineStart), Hint = hint, FixLabel = $"Replace with {issue.Suggestion}", FixNeedle = value.Substring(s, e - s), FixFrom = baseCol + (s - lineStart), FixText = issue.Suggestion }
                : new Opts { ColStart = baseCol + (s - lineStart), ColEnd = baseCol + (e - lineStart), Hint = hint };
            Add(p.Line + nl, code, severity, issue.Message + ".", opts);
            _ = strictStage;
        }

        /// <summary>A block that is fed by notes (an instrument, an effect): "for auto" takes the length of those notes (the longest, in whole bars), and a fixed length that differs from the length of its only, statically known source gets an info with the fix "for auto".</summary>
        private void DownstreamSpans(IReadOnlyDictionary<string, FlowEntry> flow)
        {
            (int Bars, bool Static, bool Single)? Upstream(BlockNode b, HashSet<string> seen)
            {
                if (!seen.Add(b.Name)) return null;
                if (IsComponentBlock(b)) return null;   // a component makes its own length (a loop of 4 bars repeated 4 times is 16): what feeds it says nothing about what it gives
                if (b.Type == "midi-clip")
                {
                    if (!flow.TryGetValue(b.Name, out FlowEntry? e) || e.Result.Value is null) return null;
                    string text = b.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty;
                    using IDisposable barsScope = NotePipeline.UseMeter((decimal)(Project?.BeatsPerBar ?? 4.0));
                    int clipBars = NoteFlow.BarsOf(e.Result.Value.Span);
                    if (b.Parameters.FirstOrDefault(p => p.Name == "bars") is { Number: double ownBars and >= 1 }) clipBars = Math.Max(clipBars, (int)ownBars);   // a clip that says bars 44 is 44 bars long, whatever its last note is
                    return (clipBars, !NotePipeline.IsExpression(text) || NotePipeline.SpanClass(text) != "unresolved", true);
                }

                if (b.Type == "midi" && b.Parameters.FirstOrDefault(p => p.Name == "bars") is { Number: double fileBars and >= 1 }) return ((int)fileBars, true, true);
                List<(int Bars, bool Static, bool Single)> fed = [];
                foreach (FromLine f in b.Froms.Where(x => !x.Disabled && x.Output is "notes" or "sequence"))
                {
                    BlockNode? source = blocks.FirstOrDefault(x => !x.Skipped && x.Name == f.Block);
                    if (source is null) continue;
                    (int, bool, bool)? up = Upstream(source, new HashSet<string>(seen, StringComparer.Ordinal));
                    if (up is not null) fed.Add(up.Value);
                }

                return fed.Count == 0 ? null : (fed.Max(x => x.Bars), fed.All(x => x.Static), fed.Count == 1 && fed[0].Single);
            }

            foreach (BlockNode b in blocks.Where(x => !x.Skipped && x.Type is not ("midi-clip" or "midi") && !IsComponentBlock(x) && x.Froms.Any(f => !f.Disabled && f.Output is "notes" or "sequence") && (x.Parts.Count > 0 || x.Froms.Any(f => f.Output == "sequence") || !x.Parameters.Any(p => p.Name == "bars"))))
            {
                (int Bars, bool Static, bool Single)? up = Upstream(b, new HashSet<string>(StringComparer.Ordinal));
                if (b.Parts.Count == 0)
                {
                    // no placement and no bars of its own: the block covers the default 8 bars, and the music that feeds it may be longer (it is cut off there)
                    if (up is { Static: true, Single: true } longer && longer.Bars > 8 && !b.Parameters.Any(p => p.Name == "bars"))
                    {
                        string header = b.Line >= 0 && b.Line < lines.Length ? lines[b.Line] : string.Empty;
                        string word = b.Class + "@" + b.Version;
                        Add(b.Line, "AMP2089", Severity.Warning, $"This block has no length (at ... for ...), so it covers 8 bars, but the music that feeds it is {longer.Bars} bars long: the rest is cut off.", new Opts { Needle = header.Contains(word, StringComparison.Ordinal) ? word : b.Name, Explicit = AutoLengthFix(header, b) });
                    }

                    continue;
                }

                for (int k = 0; k < b.Parts.Count; k++)
                {
                    if (b.Parts[k].Auto)
                    {
                        if (up is null) Add(b.Line, "AMP2036", Severity.Warning, "For auto needs notes that feed this block (a midi-clip connected with from ... -> notes): the length is taken as 1 bar until there are.", new Opts { Needle = "auto" });
                        else b.Parts[k] = b.Parts[k] with { Length = up.Value.Bars };
                    }
                    else if (up is { Static: true, Single: true } known && b.Parts.Count == 1 && known.Bars != b.Parts[k].Length)
                    {
                        string raw = b.Line >= 0 && b.Line < lines.Length ? lines[b.Line] : string.Empty;
                        int forAt = raw.IndexOf(" for ", StringComparison.Ordinal);
                        string len = b.Parts[k].Length.ToString(CultureInfo.InvariantCulture);
                        Add(b.Line, "AMP2049", Severity.Info, $"The notes that feed this block are {known.Bars} bar(s) long, this part says {len}.", new Opts { Needle = len, From = forAt < 0 ? 0 : forAt + 5, FixLabel = "Use auto span", FixNeedle = len, FixText = "auto" });
                    }
                }
            }
        }

        /// <summary>The <c>block.notes</c> that note pipelines read, collected before the note flow replaces a pipeline by its value (the unused-output lint needs them).</summary>
        private readonly HashSet<string> notePipelineSources = [];

        private void NoteFlowDiagnostics()
        {
            foreach (BlockNode clipBlock in blocks.Where(x => !x.Skipped && x.Type == "midi-clip"))
                foreach (Parameter notesParameter in clipBlock.Parameters.Where(p => p.Name == "notes"))
                    foreach (System.Text.RegularExpressions.Match source in System.Text.RegularExpressions.Regex.Matches(notesParameter.Value, @"(?<![\w.-])([A-Za-z_][\w-]*)\.(notes)\b"))
                        notePipelineSources.Add(source.Groups[1].Value + "." + source.Groups[2].Value);
            bool any = blocks.Any(b => !b.Skipped && b.Type == "midi-clip" && (b.Parameters.Any(p => p.Name == "notes" && NotePipeline.IsExpression(p.Value)) || b.Parts.Any(x => x.Auto)))
                || blocks.Any(b => !b.Skipped && b.Type != "midi-clip" && b.Parts.Count > 0 && b.Froms.Any(f => f.Output is "notes" or "sequence"))
                || blocks.Any(b => !b.Skipped && b.Type != "midi" && b.Parts.Count == 0 && b.Froms.Any(f => !f.Disabled && f.Output == "sequence"));
            if (!any) return;
            string tempo = (Project?.Tempo ?? 120).ToString(CultureInfo.InvariantCulture);
            // the limit of the song (settings { notes.maxEvents N }) replaces the default, but never goes past the limit of the server
            int limit = options.NoteLimit;
            if (settings.TryGetValue("notes.maxEvents", out string? raw) && int.TryParse(raw.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out int fromSong) && fromSong > 0) limit = fromSong;
            limit = Math.Min(limit, NotePipeline.HardCap);
            IReadOnlyDictionary<string, FlowEntry> flow = flowResults = NoteFlow.Analyze(blocks, tempo, limit, options.ReadFile, midiSource: options.MidiSource, masterSeed: Project?.MasterSeed ?? 0, helpers: helperTable, limits: LangLimits.From(settings), clock: options.Clock ?? (() => started), capabilities: capabilityPolicy, environment: options.Environment, isComponent: IsComponentBlock, beatsPerBar: Project?.BeatsPerBar ?? 4.0);
            foreach ((FlowEntry owner, PipeIssue typo) in NoteFlow.SeedLint([.. flow.Values])) ReportFlow(owner.Parameter, typo);
            foreach (FlowEntry entry in flow.Values.Where(x => x.IsExpression))
            {
                IReadOnlyList<PipeIssue> typed = NotePipeline.CheckTypes(entry.Parameter.Value);
                foreach (PipeIssue issue in typed) ReportFlow(entry.Parameter, issue);
                bool Explained(PipeIssue i) => typed.Any(t => i.Start < t.End && t.Start < i.End && t.Id != "quoted-word");
                if (entry.Estimate > limit) ReportFlow(entry.Parameter, new PipeIssue("cost-estimate", $"This pipeline may make up to {entry.Estimate} notes; the limit is {limit}", 0, entry.Parameter.Value.Length));
                foreach (PipeIssue issue in entry.Result.Errors.Where(i => !Explained(i))) ReportFlow(entry.Parameter, issue);
                foreach (PipeIssue issue in entry.Result.Warnings) ReportFlow(entry.Parameter, issue);
                foreach (PipeIssue issue in entry.StageIssues) ReportFlow(entry.Parameter, issue);
            }

            foreach (BlockNode b in blocks.Where(x => !x.Skipped && x.Type == "midi-clip" && x.Parts.Any(p => p.Auto)))
            {
                flow.TryGetValue(b.Name, out FlowEntry? mine);
                for (int k = 0; k < b.Parts.Count; k++)
                {
                    if (!b.Parts[k].Auto) continue;
                    int bars;
                    using (NotePipeline.UseMeter((decimal)(Project?.BeatsPerBar ?? 4.0))) bars = mine?.Result.Value is { } v ? NoteFlow.BarsOf(v.Span) : 1;
                    if (mine?.Result.Value is null) Add(b.Line, "AMP2036", Severity.Warning, "For auto needs the notes of this clip to be valid: the length is taken as 1 bar until they are.", new Opts { Needle = "auto" });
                    b.Parts[k] = b.Parts[k] with { Length = bars };
                }
            }

            DownstreamSpans(flow);
            if (!options.ApplyNoteFlow) return;
            foreach (FlowEntry entry in flow.Values.Where(x => x.IsExpression && x.Result.Notes is not null))
            {
                int at = entry.Block.Parameters.FindIndex(p => p.Name == "notes");
                if (at >= 0) entry.Block.Parameters[at] = entry.Block.Parameters[at] with { Value = entry.Result.Notes! };
            }
        }

        /// <summary>The numeric properties of blocks that are expressions: each is evaluated with the helpers and the other blocks, reported, and (for a build) replaced by the value the block takes.</summary>
        private void PropertyFlow()
        {
            List<(BlockNode Block, Parameter Param, TemplateParameterSpec Spec)> targets = [];
            foreach (BlockNode b in blocks.Where(x => !x.Skipped && spec.Templates.ContainsKey(x.Type)))
            {
                foreach (Parameter p in b.Parameters)
                {
                    if (spec.Templates[b.Type].ParameterSpecs.TryGetValue(p.Name, out TemplateParameterSpec? ps) && ps.Kind == "number" && (NotePipeline.IsPropertyExpression(p.Value) || helperTable.IsConstant(p.Value.Trim()))) targets.Add((b, p, ps));
                }
            }

            if (targets.Count == 0) return;
            string tempo = (Project?.Tempo ?? 120).ToString(CultureInfo.InvariantCulture);
            int limit = Math.Min(options.NoteLimit, NotePipeline.HardCap);
            LangLimits limits = LangLimits.From(settings);
            IReadOnlyDictionary<string, FlowEntry> flow = flowResults ?? NoteFlow.Analyze(blocks, tempo, limit, options.ReadFile, midiSource: options.MidiSource, masterSeed: Project?.MasterSeed ?? 0, helpers: helperTable, limits: limits, clock: options.Clock ?? (() => started), capabilities: capabilityPolicy, environment: options.Environment, isComponent: IsComponentBlock, beatsPerBar: Project?.BeatsPerBar ?? 4.0);
            Dictionary<string, PropertyResult> resolved = new(StringComparer.Ordinal);
            HashSet<string> chain = new(StringComparer.Ordinal);

            PropertyRead Read(string block, string prop)
            {
                BlockNode? b = blocks.FirstOrDefault(x => !x.Skipped && x.Name == block);
                if (b is null) return new PropertyRead(null, $"There is no block \"{block}\"");
                spec.Templates.TryGetValue(b.Type, out TemplateSpec? template);
                TemplateParameterSpec? ps = null;
                template?.ParameterSpecs.TryGetValue(prop, out ps);
                Parameter? p = b.Parameters.FirstOrDefault(x => x.Name == prop);
                if (p is null) return ps is null ? new PropertyRead(null, $"\"{block}\" has no property \"{prop}\"") : new PropertyRead(ps.Default, null);
                if (ps is not { Kind: "number" } || !(NotePipeline.IsPropertyExpression(p.Value) || helperTable.IsConstant(p.Value.Trim()))) return new PropertyRead(p.Value.Trim().Trim('"'), null);
                PropertyResult r = Resolve(b, p, ps);
                return r.Value is null ? new PropertyRead(null, (r.Errors.Count > 0 ? r.Errors[0].Message : null) ?? $"\"{block}.{prop}\" has no value") : new PropertyRead(r.Value, null);
            }

            NotePipeline.Context Ctx() => new()
            {
                Tempo = tempo,
                Limit = limit,
                MasterSeed = Project?.MasterSeed ?? 0,
                Helpers = helperTable,
                Limits = limits,
                Clock = options.Clock ?? (() => started),
                Capabilities = capabilityPolicy,
                Environment = options.Environment,
                Property = Read,
                Source = name => flow.TryGetValue(name, out FlowEntry? e) && e.Result.Value is { } v ? new NotePipeline.SourceInfo(v, e.Result.Recipe, null) : new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\" with notes"),
            };

            PropertyResult Resolve(BlockNode b, Parameter p, TemplateParameterSpec ps)
            {
                string key = b.Name + "." + p.Name;
                if (resolved.TryGetValue(key, out PropertyResult? known)) return known;
                if (!chain.Add(key))
                {
                    string cycle = "cycle: " + string.Join(" -> ", chain.Append(key));
                    return new PropertyResult(null, ps.Type, false, [], [new PipeIssue("cycle", cycle, 0, p.Value.Length)], [], "expression", null, null);
                }

                try
                {
                    string type = p.Name == "program" ? "Program" : ps.Unit switch { "dB" => "Gain", "ms" or "s" => "Time", "%" => "Percent", "Hz" => "Frequency", _ => "Number" };
                    PropertyShape shape = new(p.Name, type, ps.Unit, ps.Default, ps.Min, ps.Max);
                    PropertyResult result = NotePipeline.EvaluateProperty(p.Value, Ctx(), shape);
                    resolved[key] = result;
                    return result;
                }
                finally
                {
                    chain.Remove(key);
                }
            }

            foreach ((BlockNode b, Parameter p, TemplateParameterSpec ps) in targets)
            {
                PropertyResult r = Resolve(b, p, ps);
                int at = lines[p.Line].IndexOf(p.Value.Split('\n')[0], StringComparison.Ordinal);
                Parameter shown = p with { ValueColumn = Math.Max(0, at) };
                foreach (PipeIssue issue in r.Errors) ReportFlow(shown, issue);
                foreach (PipeIssue issue in r.Warnings) ReportFlow(shown, issue);
                if (r.Dynamic) ReportFlow(shown, new PipeIssue("dynamic-property", $"{b.Name}.{p.Name} depends on a volatile value ({string.Join(", ", r.DynamicInputs)}): it can differ between builds until it is baked", 0, p.Value.Length));
                propertyBindings.Add(new PropertyBinding(b.Name, p.Name, p.Value.Trim(), r.Value, r.State, r.Dynamic, r.BakedText, r.SourceText, p.Line) { Hash = r.Hash, ExpressionText = r.ExpressionText });
                if (r.Dynamic) stabilityNotes.AddRange(r.DynamicInputs.Select(i => $"{b.Name}.{p.Name}: {i}"));
                if (r.BakedDynamic) bakedDynamicSeen = true;
                if (r.State is "stale" or "dirty") staleSeen = true;
                if (options.ApplyNoteFlow && r.Value is not null)
                {
                    int index = b.Parameters.FindIndex(x => x.Name == p.Name);
                    if (index >= 0) b.Parameters[index] = b.Parameters[index] with { Value = r.Value };
                }
            }
        }

        private bool bakedDynamicSeen;

        private bool staleSeen;

        private void Stability()
        {
            foreach (FlowEntry entry in (flowResults ?? new Dictionary<string, FlowEntry>()).Values)
            {
                if (entry.Result.Dynamic) stabilityNotes.AddRange(entry.Result.DynamicInputs.Select(i => $"{entry.Block.Name}: {i}"));
                if (entry.Result.BakedDynamic) bakedDynamicSeen = true;
                if (entry.StageIssues.Any(i => i.Id is "stale-bake" or "dirty-bake") || entry.Result.Warnings.Any(i => i.Id is "stale-value-bake" or "dirty-value-bake")) staleSeen = true;
            }

            bool dynamic = stabilityNotes.Count > 0;
            StabilityState = staleSeen ? "stale" : dynamic ? "dynamic" : bakedDynamicSeen ? "baked-dynamic" : "stable";
        }

        private void HelperDiagnostics()
        {
            if (SettingsLine >= 0)
            {
                foreach ((string key, long requested, long cap) in LangLimits.Overruns(settings))
                    Add(SettingsLine, "AMP2071", Severity.Warning, $"{key} {requested} is above the cap of the server ({cap}): {cap} is used.", new Opts { Needle = key, Hint = "A song may lower a limit or raise it up to the cap of the server, never above it." });
            }

            List<HelperIssue> issues = [];
            List<CapabilityIssue> capabilityIssues = [];
            capabilityPolicy = CapabilityPolicy.Resolve(options.ServerCapabilities ?? ServerCapabilities.Current, options.ProjectCapabilities ?? ServerCapabilities.Project, capabilityEntries, capabilityIssues);
            foreach (CapabilityIssue ci in capabilityIssues)
                Add(Math.Max(0, ci.Line), "AMP2076", Severity.Error, ci.Message, new Opts { Needle = ci.Needle, FixLabel = ci.Suggestion is null ? null : $"Use {ci.Suggestion}", FixNeedle = ci.Suggestion is null ? null : ci.Needle, FixText = ci.Suggestion });
            // the process of every component of this text is a helper like any other (its lines stand where the process lines stand), so that it is checked, typed and evaluated by the helper system
            List<ComponentDefinition> visibleComponents = [.. Components.Definitions, .. (options.Components ?? []).Concat(ImportedByFile()).Where(x => !Components.Definitions.Contains(x))];
            List<NestIssue> nestIssues = [];
            foreach (ComponentDefinition component in Components.Definitions.Where(c => c.ProcessLine >= 0 && !c.IsAudio)) helperSources.Add((ComponentHelper.Text(component, visibleComponents, nestIssues), component.ProcessLine));
            // the components that these call and that this text does not declare (the ones it imports): their helpers are needed for the check, without a line of their own
            HashSet<ComponentDefinition> calledComponents = [];
            Queue<ComponentDefinition> toCheck = new(Components.Definitions.Where(c => c.ProcessLine >= 0 && !c.IsAudio));
            while (toCheck.Count > 0)
                foreach (ComponentDefinition callee in ComponentNesting.Callees(toCheck.Dequeue(), visibleComponents))
                    if (!Components.Definitions.Contains(callee) && calledComponents.Add(callee)) toCheck.Enqueue(callee);
            foreach (ComponentDefinition callee in calledComponents.Where(c => c.ProcessLine >= 0)) helperSources.Add((ComponentHelper.Text(callee, visibleComponents), -1));
            foreach (NestIssue nest in nestIssues.Concat(ComponentNesting.Analyze(visibleComponents).Where(i => Components.Definitions.Contains(i.Component))))
                Add(Math.Max(0, nest.Component.ProcessLine), nest.Code, nest.Severity, nest.Message, new Opts());
            helperTable = NotePipeline.HelperTable.Build(helperSources, issues);
            foreach ((int line, int start, int end, string call, string capability) in helperTable.VolatileUses())
            {
                if (capabilityPolicy.Allowed(capability)) continue;
                ReportFlow(new Parameter("helper", helperSources.First(x => x.Line == line).Text, line) { ValueColumn = 0 }, new PipeIssue("capability-denied", $"{call}() needs the capability `{capability}`, which is {capabilityPolicy.Why(capability)}", start, end));
            }

            foreach (HelperIssue h in issues)
            {
                string declText = helperSources.First(x => x.Line == h.Line).Text;
                ReportFlow(new Parameter("helper", declText, h.Line) { ValueColumn = 0 }, h.Issue);
            }

            // a helper that nothing calls (only in a song: a library of helpers has no blocks to call them)
            if (blocks.Count > 0)
            {
                foreach (HelperInfo info in helperTable.Infos.Where(i => i.Kind == "helper" && !i.Name.StartsWith("__c_", StringComparison.Ordinal)))
                {
                    (string text, int first) = helperSources.FirstOrDefault(x => x.Line == info.Line);
                    int last = first + (text?.Count(ch => ch == '\n') ?? 0);
                    System.Text.RegularExpressions.Regex use = new(@"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(info.Name) + @"(?![\w-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    bool called = false;
                    for (int k = 0; k < lines.Length && !called; k++)
                    {
                        if (k >= first && k <= last) continue;
                        string code = lines[k].Contains('#', StringComparison.Ordinal) ? lines[k][..lines[k].IndexOf('#', StringComparison.Ordinal)] : lines[k];
                        called = use.IsMatch(code);
                    }

                    if (!called) ReportFlow(new Parameter("helper", text ?? string.Empty, info.Line) { ValueColumn = 0 }, new PipeIssue("unused-helper", $"\"{info.Name}\" is never called in this song", (text ?? string.Empty).IndexOf(info.Name, StringComparison.Ordinal), (text ?? string.Empty).IndexOf(info.Name, StringComparison.Ordinal) + info.Name.Length));
                }
            }
        }

        /// <summary>The doc comments of the file by the line of the declaration under them (read from the original text, comments included).</summary>
        public Dictionary<int, DocComment> Docs { get; init; } = [];

        /// <summary>The components read from this text.</summary>
        public ComponentFile Components { get; init; } = ComponentFile.Empty;

        /// <summary>The lines of the original text, comments included (the lines the parser reads have the block comments blanked).</summary>
        public string[] Original { get; init; } = [];

        /// <summary>Whether a documented type says the same as a declared one: unions are compared as sets of members, aliases (type X = A | B) are written out, an optional type (Int?) is the type itself, the arguments of a generic are compared as types of their own, and case is ignored.</summary>
        private bool SameType(string documented, string declared) => DocTypes.Canonical(documented, AliasMembers).SetEquals(DocTypes.Canonical(declared, AliasMembers));

        private IReadOnlyList<string>? AliasMembers(string name) => helperTable.TryType(name, out List<string> members) ? members : null;

        private bool KnownType(string member)
        {
            string name = member.TrimEnd('?');
            return name.EndsWith("[]", StringComparison.Ordinal) || NoteCatalog.Types.ContainsKey(name) || Comments.BuiltinTypes.Contains(name) || helperTable.TryType(name, out _) || name is "Value" or "None" or "Null" or "Any";
        }

        /// <summary>What a doc comment says about a helper against what the declaration says (AMP2090 to AMP2094), and the calls of a helper that is deprecated (AMP2093). Only a helper that has a doc comment is checked, so a song without doc comments has nothing new to say.</summary>
        private void DocDiagnostics()
        {
            Fix? Replace(string label, int line, int start, int end, string text) => start < 0 || line >= Original.Length ? null : new Fix(label, "replace", text, line + 1, CodePointColumn(Original[line], start) + 1, CodePointColumn(Original[line], end) + 1, false);
            string? NearestType(string unknown)
            {
                IEnumerable<string> known = NoteCatalog.Types.Keys.Concat(Comments.BuiltinTypes).Concat(helperTable.TypeNames);
                return known.Select(k => (Name: k, Distance: NotePipeline.Distance(unknown.ToLowerInvariant(), k.ToLowerInvariant()))).Where(x => x.Distance <= 2).OrderBy(x => x.Distance).Select(x => x.Name).FirstOrDefault();
            }

            foreach (HelperInfo info in helperTable.Infos.Where(i => i.Kind == "helper"))
            {
                if (!Docs.TryGetValue(info.Line, out DocComment? doc)) continue;
                string names = string.Join(", ", info.Params.Select(p => p.Name));
                foreach (DocParam documented in doc.Params)
                {
                    HelperParamInfo? declared = info.Params.FirstOrDefault(p => string.Equals(p.Name, documented.Name, StringComparison.OrdinalIgnoreCase));
                    Comments.TagSpan? tag = Comments.FindTag(Original, doc, "param", documented.Name);
                    if (declared is null)
                    {
                        string? suggestion = info.Params.Select(p => p.Name).Where(n => doc.ParamText(n) is null).Select(n => (Name: n, Distance: NotePipeline.Distance(n.ToLowerInvariant(), documented.Name.ToLowerInvariant()))).Where(x => x.Distance <= 2).OrderBy(x => x.Distance).Select(x => x.Name).FirstOrDefault();
                        Fix? fix = null;
                        if (tag is not null && suggestion is not null) fix = Replace($"Rename @param {documented.Name} to {suggestion}", tag.Line, tag.NameStart, tag.NameEnd, suggestion);
                        else if (tag is { StandsAlone: true }) fix = new Fix("Remove this @param line", "replace", string.Empty, tag.Line + 1, 1, 1, true, tag.EndLine + 2);   // risky: it removes text, so "Fix all" leaves it alone
                        Add(info.Line, "AMP2090", Severity.Warning, $"The doc comment of \"{info.Name}\" describes a parameter \"{documented.Name}\" that it does not have ({(names.Length == 0 ? "it has none" : "it has " + names)}).", new Opts { Needle = info.Name, Explicit = fix, Hint = "Fix the name, or remove the @param line (a parameter that was renamed or removed leaves its note behind)." });
                        continue;
                    }

                    if (documented.Type is { } type && !SameType(type, declared.Type))
                        Add(info.Line, "AMP2091", Severity.Warning, $"The doc comment says \"{documented.Name}\" is {type}, the declaration says {declared.Type}.", new Opts { Needle = info.Name, Explicit = tag is null ? null : Replace($"Use {declared.Type} in the comment", tag.Line, tag.TypeStart, tag.TypeEnd, declared.Type), Hint = $"Use {declared.Type} in the comment, or change the declaration to {type}." });
                    if (documented.Type is { } written && tag is { TypeStart: >= 0 })
                    {
                        foreach (string member in DocTypes.Names(written).Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !KnownType(m)))
                        {
                            string? near = NearestType(member);
                            int at = Original[tag.Line].IndexOf(member, tag.TypeStart, StringComparison.Ordinal);
                            Add(info.Line, "AMP2092", Severity.Warning, $"The doc comment of \"{info.Name}\" uses a type that does not exist: {member}.", new Opts { Needle = info.Name, Explicit = near is null || at < 0 ? null : Replace($"Change {member} to {near}", tag.Line, at, at + member.Length, near), Hint = "The types are Int, Float, Notes, Interval, Duration ... (docs/language/15-musical-types.md) and the type aliases of the file." });
                        }
                    }
                }

                if (doc.ReturnType is { } returned)
                {
                    Comments.TagSpan? returnTag = Comments.FindTag(Original, doc, "return", null);
                    if (info.Returns is { } declaredReturn && !SameType(returned, declaredReturn))
                        Add(info.Line, "AMP2091", Severity.Warning, $"The doc comment says \"{info.Name}\" returns {returned}, the declaration says {declaredReturn}.", new Opts { Needle = info.Name, Explicit = returnTag is null ? null : Replace($"Use {declaredReturn} in the comment", returnTag.Line, returnTag.TypeStart, returnTag.TypeEnd, declaredReturn), Hint = $"Use {declaredReturn} in the comment, or change the declaration to {returned}." });
                    foreach (string member in DocTypes.Names(returned).Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !KnownType(m)))
                    {
                        string? near = NearestType(member);
                        int at = returnTag is { TypeStart: >= 0 } ? Original[returnTag.Line].IndexOf(member, returnTag.TypeStart, StringComparison.Ordinal) : -1;
                        Add(info.Line, "AMP2092", Severity.Warning, $"The doc comment of \"{info.Name}\" uses a type that does not exist: {member}.", new Opts { Needle = info.Name, Explicit = near is null || at < 0 || returnTag is null ? null : Replace($"Change {member} to {near}", returnTag.Line, at, at + member.Length, near), Hint = "The types are Int, Float, Notes, Interval, Duration ... (docs/language/15-musical-types.md) and the type aliases of the file." });
                    }
                }

                if (doc.Params.Count > 0)
                {
                    string[] missing = [.. info.Params.Where(p => doc.ParamText(p.Name) is null).Select(p => p.Name)];
                    if (missing.Length > 0)
                    {
                        // add the missing lines after the last @param (and the text that goes on under it), in the same style
                        Comments.TagSpan? last = doc.Params.Select(p => Comments.FindTag(Original, doc, "param", p.Name)).Where(t => t is not null).OrderBy(t => t!.Line).LastOrDefault();
                        Fix? add = last is null || Original[last.EndLine].Contains("*/", StringComparison.Ordinal) ? null : new Fix($"Add @param for {string.Join(", ", missing)}", "replace", string.Concat(missing.Select(m => "\n" + last.Prefix + "@param " + m + " ")), last.EndLine + 1, CodePointColumn(Original[last.EndLine], Original[last.EndLine].Length) + 1, CodePointColumn(Original[last.EndLine], Original[last.EndLine].Length) + 1, false);
                        Add(info.Line, "AMP2094", Severity.Info, $"The doc comment of \"{info.Name}\" describes some parameters but not {string.Join(", ", missing)}.", new Opts { Needle = info.Name, Explicit = add, Hint = "Add a @param line for it, or silence this with # ample-ignore AMP2094 on the line above." });
                    }
                }

                if (doc.Deprecated is { } why)
                {
                    int declaredLines = helperSources.FirstOrDefault(x => x.Line == info.Line).Text?.Count(ch => ch == '\n') ?? 0;
                    System.Text.RegularExpressions.Regex call = new(@"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(info.Name) + @"\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    for (int k = 0; k < lines.Length; k++)
                    {
                        if (k >= info.Line && k <= info.Line + declaredLines) continue;
                        string code = lines[k].Contains('#', StringComparison.Ordinal) ? lines[k][..lines[k].IndexOf('#', StringComparison.Ordinal)] : lines[k];
                        if (call.IsMatch(code)) Add(k, "AMP2093", Severity.Warning, $"\"{info.Name}\" is deprecated{(why.Length > 0 ? ": " + why : ".")}", new Opts { Needle = info.Name, Hint = "Use what the doc comment points to, or silence this with # ample-ignore AMP2093 on the line above." });
                    }
                }
            }
        }

        /// <summary>The components a component file imports, when the file is read on its own (the editor, <c>ample components check</c>) and the host gave no resolved ones: the nested calls in its audio processes need them.</summary>
        private IReadOnlyList<ComponentDefinition> ImportedByFile() => Components.Imports.Count > 0 && options.ReadFile is not null && (options.Components ?? []).Count == 0 ? ComponentRegistry.Resolve(string.Join('\n', Original), "file", t => options.ReadFile(t), spec).Visible : [];

        private bool IsComponentBlock(BlockNode b) => spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Component;

        private void AddComponentIssue(ComponentIssue i) => diagnostics.Add(new Diagnostic(i.Code, i.Severity, i.Line + 1, i.Col + 1, Math.Max(i.EndCol, i.Col + 1) + 1, i.Message, i.Hint, null, null));

        /// <summary>The problems of the component declarations of this text and of the instances of components in it.</summary>
        private void ComponentDiagnostics()
        {
            string tempo = (Project?.Tempo ?? 120).ToString("0.###", CultureInfo.InvariantCulture);
            foreach (ComponentIssue issue in Components.Issues) AddComponentIssue(issue);
            foreach (ComponentDefinition d in Components.Definitions)
            {
                foreach (ComponentIssue issue in ComponentChecker.Check(d, spec)) AddComponentIssue(issue);
                foreach (ComponentIssue issue in ComponentChecker.CheckDefaults(d, tempo)) AddComponentIssue(issue);
                if (d.IsAudio && !d.Outputs.Any(p => p.Type.TrimEnd('?') == "Notes"))
                {
                    // the process of an audio component is a graph of processors: read it once with the defaults, so that its problems are shown where they are written
                    AudioGraph graph = AudioGraphBuilder.Build(d, spec, new Dictionary<string, string>(StringComparer.Ordinal), Project?.Tempo ?? 120, [.. Components.Definitions, .. options.Components ?? [], .. ImportedByFile()]);
                    foreach (ComponentIssue issue in graph.Issues) AddComponentIssue(issue);
                    if (!graph.ClientPreview && graph.Issues.Count == 0)
                        AddComponentIssue(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2236", Severity.Info, $"The browser cannot preview {string.Join(", ", graph.NoPreview)}: the component is rendered by the server only.", string.Empty));
                    if (ComponentUiReader.Effective(d).Preview == "live" && graph.Issues.Count == 0 && !graph.LivePreview)
                        AddComponentIssue(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2245", Severity.Info, $"The live preview needs a real-time chain for every processor; {string.Join(", ", graph.NoLive)} {(graph.NoLive.Count == 1 ? "has" : "have")} none, so the Live switch stays off for this component.", "Use processors the browser can run live (eq, filter, compressor, distortion, delay, reverb, chorus, gain), or take preview live out."));
                    if (graph.LatencyMs > 0)
                        AddComponentIssue(new ComponentIssue(d.ProcessLine, 0, 1, "AMP2236", Severity.Info, $"The component adds {graph.LatencyMs.ToString("0.##", CultureInfo.InvariantCulture)} ms of latency (its branches are lined up inside it); to line it up with other tracks too, write settings {{ audio.latencyCompensation on }}.", string.Empty));
                }
            }

            foreach (BlockNode block in blocks.Where(b => !b.Skipped && spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Component)) ComponentInstance(block, spec.Templates[block.Type], tempo);
        }

        private void ComponentInstance(BlockNode block, TemplateSpec template, string tempo)
        {
            foreach (Parameter p in block.Parameters)
            {
                if (!template.ParameterSpecs.TryGetValue(p.Name, out TemplateParameterSpec? ps)) continue;
                string value = p.Value.Trim();
                if (ps.Type == "Notes")
                {
                    if (NotePipeline.IsExpression(value)) continue;
                    List<PipeIssue> bad = [];
                    NotePipeline.ReadNotes(value.Trim('"'), bad);
                    if (bad.Count > 0) Add(p.Line, "AMP2218", Severity.Error, $"The notes of \"{p.Name}\" cannot be read: {bad[0].Message}.", new Opts { Needle = p.Name });
                    continue;
                }

                if (NotePipeline.IsPropertyExpression(value)) continue;
                // a component property of another kind than a number: a switch is true or false, a text is any text; the number evaluator below is not for them
                if (ps.Type == "Bool")
                {
                    if (value is not ("true" or "false")) Add(p.Line, "AMP2218", Severity.Error, $"\"{value}\" is not a Bool for \"{p.Name}\" of {block.Type}: write true or false.", new Opts { Needle = value.Length > 0 ? value : p.Name });
                    continue;
                }

                if (ps.Type is "String" or "Text") continue;
                PropertyResult result = NotePipeline.EvaluateProperty(value, new NotePipeline.Context { Tempo = tempo, BeatsPerBar = (decimal)(Project?.BeatsPerBar ?? 4.0) }, new PropertyShape(p.Name, ps.Type, ps.Unit, ps.Default, ps.Min, ps.Max));
                if (result.Errors.Count > 0)
                {
                    Add(p.Line, "AMP2218", Severity.Error, $"\"{value}\" is not a {ps.Type} for \"{p.Name}\" of {block.Type}: {result.Errors[0].Message}.", new Opts { Needle = value.Length > 0 ? value : p.Name });
                    continue;
                }

                Match number = Regex.Match(value, @"^([+-]?\d+(?:\.\d+)?)", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (number.Success && double.TryParse(number.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    if (ps.Min is { } lo && n < lo) Add(p.Line, "AMP2218", Severity.Error, $"{p.Name} {value} is below the lowest value of this property ({lo.ToString(CultureInfo.InvariantCulture)}).", new Opts { Needle = value });
                    else if (ps.Max is { } hi && n > hi) Add(p.Line, "AMP2218", Severity.Error, $"{p.Name} {value} is above the highest value of this property ({hi.ToString(CultureInfo.InvariantCulture)}).", new Opts { Needle = value });
                }
            }
        }

        private void Semantic()
        {
            HelperDiagnostics();
            ComponentDiagnostics();
            DocDiagnostics();
            NoteFlowDiagnostics();
            PropertyFlow();
            Stability();
            Dictionary<string, int> seen = [];
            foreach (BlockNode b in blocks.Where(x => !x.Skipped))
            {
                if (seen.TryGetValue(b.Name, out int firstLine))
                {
                    Add(b.Line, "AMP2002", Severity.Error, $"Two blocks are called \"{b.Name}\".", new Opts { Needle = b.RawName, Related = new Related(firstLine + 1, "first used here"), Hint = "Block names are unique. Rename one of them." });
                }
                else
                {
                    seen[b.Name] = b.Line;
                }
            }

            HashSet<string> useNames = [.. uses.Select(u => u.Instance)];
            foreach (UseNode u in uses)
            {
                if (seen.TryGetValue(u.Instance, out int firstLine))
                {
                    Add(u.Line, "AMP2002", Severity.Error, $"Two blocks are called \"{u.Instance}\".", new Opts { Needle = u.RawInstance, Related = new Related(firstLine + 1, "first used here"), Hint = "Block names are unique. Rename one of them." });
                }
                else
                {
                    seen[u.Instance] = u.Line;
                }
            }

            string[] titles = [.. blocks.Where(x => !x.Skipped).Select(x => x.Name).Concat(useNames)];
            foreach (BlockNode b in blocks)
            {
                if (!spec.Templates.TryGetValue(b.Type, out TemplateSpec? t))
                {
                    continue;
                }

                foreach (Parameter p in b.Parameters)
                {
                    if (t.ParameterSpecs.TryGetValue(p.Name, out TemplateParameterSpec? automatable) && automatable.Automatable && ControlExpression.LooksLike(p.Value))
                    {
                        CheckControlExpression(b, t, automatable, p, blocks, useNames, titles);
                        continue;
                    }

                    if (p.Name == "tint")
                    {
                        // every block can have a tint; it only colours the card and never changes the sound
                        if (!TintNames.Contains(p.Value.Trim().Trim('"')))
                        {
                            Add(p.Line, "AMP2022", Severity.Error, $"Unknown tint \"{p.Value.Trim().Trim('"')}\" on {b.Name}.", new Opts { Needle = p.Name, Hint = "Use one of: " + string.Join(", ", TintNames) + "." });
                        }

                        continue;
                    }

                    if (!t.Parameters.ContainsKey(p.Name) && !p.Name.StartsWith("random-stream:", StringComparison.Ordinal))
                    {
                        string? near = Nearest(p.Name, t.Parameters.Keys);
                        Add(p.Line, "AMP2003", Severity.Error, $"Unknown parameter \"{p.Name}\" for {b.Type}.", new Opts { Needle = p.Name, Hint = (near is null ? string.Empty : DidYou(near) + "  ") + (t.Parameters.Count > 0 ? "Known parameters: " + string.Join(", ", t.Parameters.Keys) : "This block has no parameters."), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                    }
                }

                foreach (Parameter p in b.Parameters)
                {
                    if (!t.ParameterSpecs.TryGetValue(p.Name, out TemplateParameterSpec? numberSpec) || numberSpec.Kind != "number" || (numberSpec.Min is null && numberSpec.Max is null)) continue;
                    string raw = p.Value.Trim().Trim('"');
                    IEnumerable<double> values;
                    if (raw.StartsWith("auto(", StringComparison.Ordinal))
                    {
                        values = System.Text.RegularExpressions.Regex.Matches(raw, @":\s*([+-]?\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    }
                    else if (System.Text.RegularExpressions.Regex.Match(raw, @"^[+-]?\d+(?:\.\d+)?(?=\s*[A-Za-z%]*$)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)) is { Success: true } plain)
                    {
                        values = [double.Parse(plain.Value, CultureInfo.InvariantCulture)];
                    }
                    else
                    {
                        continue;
                    }

                    foreach (double v in values)
                    {
                        if ((numberSpec.Max is double max && v > max) || (numberSpec.Min is double min && v < min))
                        {
                            System.Text.RegularExpressions.Match number = System.Text.RegularExpressions.Regex.Match(raw, @"^[+-]?\d+(?:\.\d+)?", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
                            string unitText = number.Success ? raw[number.Length..].Trim() : string.Empty;
                            // a value in another unit than the limit (1 min against seconds) is not converted: no fix then
                            bool simple = !raw.StartsWith("auto(", StringComparison.Ordinal) && number.Success && (unitText.Length == 0 || string.Equals(unitText, numberSpec.Unit ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                            string lineText = lines[p.Line];
                            int nameAt = System.Text.RegularExpressions.Regex.Match(lineText, @"(?<![w-])" + System.Text.RegularExpressions.Regex.Escape(p.Name) + @"(?![w-])", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)) is { Success: true } nm ? nm.Index + nm.Length : 0;
                            string cut = Fmt(Math.Min(numberSpec.Max ?? double.MaxValue, Math.Max(numberSpec.Min ?? double.MinValue, v)));
                            string range = $"{numberSpec.Min?.ToString(CultureInfo.InvariantCulture) ?? "..."} to {numberSpec.Max?.ToString(CultureInfo.InvariantCulture) ?? "..."}{(numberSpec.Unit is { Length: > 0 } unit ? " " + unit : string.Empty)}";
                            Add(p.Line, "AMP2021", Severity.Warning, $"{p.Name} {raw}: {v.ToString(CultureInfo.InvariantCulture)} is outside the range of {b.Type} {p.Name} ({range}); it is cut to the nearest limit when the block is built.", new Opts { Needle = p.Name, FixNeedle = simple ? number.Value : null, FixFrom = nameAt, Hint = "Use a value inside the range, or chain two blocks when you need more.", FixLabel = simple ? "Use " + cut : null, FixText = simple ? cut : null });
                            break;
                        }
                    }
                }

                Parameter? structuredNotes = b.Type == "midi-clip" ? b.Parameters.FirstOrDefault(p => p.Name == "notes") : null;
                Parameter? midiPpq = b.Type == "midi-clip" ? b.Parameters.FirstOrDefault(p => p.Name == "ppq") : null;
                int ppq = midiPpq is null || !int.TryParse(midiPpq.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPpq) ? StructuredMidiNotes.MicrobeatsPerBeat : parsedPpq;
                if (structuredNotes is not null && t.Parameters.ContainsKey("notes") && !NotePipeline.IsExpression(structuredNotes.Value) && !StructuredMidiNotes.TryParse(structuredNotes.Value, ppq, out _, Project?.BeatsPerBar ?? 4.0))
                {
                    Add(structuredNotes.Line, "AMP2012", Severity.Error, "Structured MIDI notes must be space-separated bar:beat:length:pitch:velocity entries (optionally followed by ;pan=..;fine=..;rel=..;cut=..;slide=..;port=.. within their own ranges) and each note must fit within one bar (4 beats, or the beats of the meter of the song).", new Opts { Needle = structuredNotes.Value, Hint = ppq == StructuredMidiNotes.MicrobeatsPerBeat ? "Use decimal beats (up to six places), for example 1:0.5:1:60:96;pan=-40." : $"Use integer ticks at {ppq} ticks per quarter note, for example 1:0:720:60:96." });
                }

                Parameter? midiTrack = b.Type == "midi" ? b.Parameters.FirstOrDefault(p => p.Name == "track") : null;
                if (midiTrack is not null && midiTrack.Value.Trim('"') != "all" && (!int.TryParse(midiTrack.Value.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedTrack) || parsedTrack < 0))
                {
                    Add(midiTrack!.Line, "AMP2015", Severity.Error, "A MIDI track selector must be 'all' or a nonnegative zero-based index.", new Opts { Needle = midiTrack.Value, Hint = "Use track all or a zero-based track index such as track 0." });
                }

                foreach (string seedName in new[] { "lm_seed", "seed" })
                {
                    Parameter? p = b.Parameters.FirstOrDefault(x => x.Name == seedName);
                    if (p is not null && t.Parameters.ContainsKey(seedName) && p.Value != "derived" && !(ulong.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) && v <= spec.MaxSeed))
                    {
                        Add(p.Line, "AMP2011", Severity.Error, $"A seed must be the word derived or a whole number from 0 to {spec.MaxSeed.ToString(CultureInfo.InvariantCulture)} (found {p.Value}).", new Opts { Needle = p.Value, Hint = "Use derived to follow the master seed." });
                    }
                }

                if (t.Plugin is not null && !options.InstalledPlugins.Contains(t.Plugin))
                {
                    Add(b.Line, "AMP5001", Severity.Warning, $"Block \"{b.Name}\" needs the plugin \"{t.Plugin}\", which is not installed.", new Opts { Needle = b.Type, Hint = "Plugins are opt-in. Install it from the plugin list, or remove the block." });
                }

                for (int fi = 0; fi < b.Froms.Count; fi++)
                {
                    // a song may write the stream names (audio, notes, render, audio1, ...); the model carries the internal port names
                    FromLine written = b.Froms[fi];
                    BlockNode? origin = blocks.FirstOrDefault(x => x.Name == written.Block);
                    if (origin is null || ReferenceEquals(origin, b)) continue;
                    string outName = PortNames.Resolve(Ports(origin).Outputs, written.Output, true);
                    string inName = PortNames.Resolve(Ports(b).Inputs, written.Input, false);
                    if (outName != written.Output || inName != written.Input) b.Froms[fi] = written with { Output = outName, Input = inName };
                }

                HashSet<int> used = [];
                if (t.Inputs.Any(p => p.Name == "render_in" && p.Type == "render") && b.Froms.Any(f => !f.Disabled && f.Input == "in") && b.Froms.Any(f => !f.Disabled && f.Input is "render_in" or "region_in"))
                {
                    Add(b.Line, "AMP2020", Severity.Error, "An effect cannot use audio and a render recipe at the same time.", new Opts { Hint = "Disable one connection, or connect only in for existing audio or render_in for an isolated source variant." });
                }
                foreach (FromLine f in b.Froms)
                {
                    if (useNames.Contains(f.Block))
                    {
                        continue;
                    }

                    BlockNode? src = blocks.FirstOrDefault(x => x.Name == f.Block);
                    if (src is null)
                    {
                        string? near = Nearest(f.Block, titles);
                        Add(f.Line, "AMP2004", Severity.Error, $"There is no block named \"{f.Block}\".", new Opts { Needle = f.RawBlock, Hint = (near is null ? string.Empty : DidYou(near) + "  ") + "Blocks in this song: " + string.Join(", ", titles), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                        continue;
                    }

                    if (ReferenceEquals(src, b))
                    {
                        Add(f.Line, "AMP2009", Severity.Error, "A block cannot read from itself.", new Opts { Needle = f.RawBlock });
                        continue;
                    }

                    IReadOnlyList<PortSpec> outs = Ports(src).Outputs;
                    IReadOnlyList<PortSpec> ins = Ports(b).Inputs;
                    int oi = IndexOf(outs, f.Output);
                    int ii = IndexOf(ins, f.Input);
                    if (oi < 0)
                    {
                        string? near = Nearest(f.Output, outs.Select(p => PortNames.Public(p, outs, true)));
                        Add(f.Line, "AMP2005", Severity.Error, $"Block \"{f.Block}\" has no output \"{f.Output}\".", new Opts { Needle = f.RawOutput, From = lines[f.Line].IndexOf('.', StringComparison.Ordinal), Hint = (near is null ? string.Empty : DidYou(near) + "  ") + $"Outputs of {f.Block}: " + (outs.Count == 0 ? "none" : string.Join(", ", outs.Select(p => p.Name))), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                        continue;
                    }

                    if (ii < 0)
                    {
                        string? near = Nearest(f.Input, ins.Select(p => PortNames.Public(p, ins, false)));
                        Add(f.Line, "AMP2006", Severity.Error, $"Block \"{b.Name}\" has no input \"{f.Input}\".", new Opts { Needle = f.RawInput, From = lines[f.Line].IndexOf("->", StringComparison.Ordinal), Hint = (near is null ? string.Empty : DidYou(near) + "  ") + $"Inputs of {b.Name}: " + (ins.Count == 0 ? "none" : string.Join(", ", ins.Select(p => p.Name))), FixLabel = near is null ? null : $"Replace with \"{near}\"", FixText = near });
                        continue;
                    }

                    string ta = ControlTypes.ResolveOutput(src, outs[oi].Type);
                    string tb = ins[ii].Type;
                    if (ControlTypes.Is(ta) && ControlTypes.Is(tb))
                    {
                        if (!ControlTypes.Compatible(ta, tb)) Add(f.Line, "AMP2253", Severity.Error, $"Wrong unit: {f.Block}.{f.Output} is {ta} but {b.Name}.{f.Input} wants {tb}.", new Opts { Hint = "A control with a unit only steers a parameter in that unit. A control without a unit fits any parameter (it is scaled to the range of the parameter); convert with a map block." });
                    }
                    else if (ta != tb && (ta == "render" || tb == "render" || ta == "region" || tb == "region" || ta != "any" && tb != "any"))
                    {
                        Add(f.Line, "AMP2007", Severity.Error, $"Type mismatch: {f.Block}.{f.Output} is {ta} but {b.Name}.{f.Input} wants {tb}.", new Opts { Hint = "Only equal types connect (any matches everything)." });
                    }

                    if (!used.Add(ii))
                    {
                        Add(f.Line, "AMP2008", Severity.Error, $"Input \"{f.Input}\" of \"{b.Name}\" already has a source.", new Opts { Needle = f.RawInput, Hint = "An input takes one source. Use a mixer to combine several." });
                    }
                }
            }

            Dictionary<string, int> state = [];
            foreach (BlockNode b in blocks.Where(x => !x.Skipped))
            {
                Dfs(b, []);
            }

            void Dfs(BlockNode b, List<string> path)
            {
                state.TryGetValue(b.Name, out int s);
                if (s == 2)
                {
                    return;
                }

                if (s == 1)
                {
                    int start = path.IndexOf(b.Name);
                    Add(b.Line, "AMP2010", Severity.Error, "Cycle: " + string.Join(" → ", path.Skip(start).Append(b.Name)), new Opts { Hint = "Blocks cannot depend on each other in a loop." });
                    return;
                }

                state[b.Name] = 1;
                foreach (FromLine f in b.Froms.Where(f => !f.Disabled))
                {
                    BlockNode? src = blocks.FirstOrDefault(x => x.Name == f.Block);
                    if (src is not null && !ReferenceEquals(src, b))
                    {
                        Dfs(src, [.. path, b.Name]);
                    }
                }

                foreach (ControlRef c in b.ControlRefs)
                {
                    BlockNode? src = blocks.FirstOrDefault(x => x.Name == c.Block);
                    if (src is not null && !ReferenceEquals(src, b))
                    {
                        Dfs(src, [.. path, b.Name]);
                    }
                }

                state[b.Name] = 2;
            }
        }

        private void Lint()
        {
            Dictionary<string, string> eff = spec.LintRules.ToDictionary(r => r.Key, r => r.Value.DefaultLevel);
            foreach ((string code, string level) in lintOverrides)
            {
                eff[code] = level;
            }

            void L(string code, BlockNode b, string message, Opts o)
            {
                string level = eff[code];
                if (level == "off" || (ignoreFor.TryGetValue(b.Line, out List<string>? ignored) && ignored.Contains(code)))
                {
                    return;
                }

                Severity sev = level == "error" ? Severity.Error : level == "warning" ? Severity.Warning : Severity.Info;
                Add(b.Line, code, sev, message + "  [" + spec.LintRules[code].Name + "]", o);
            }

            List<BlockNode> ok = [.. blocks.Where(b => !b.Skipped && spec.Templates.ContainsKey(b.Type) && b.Class.Length > 0)];
            foreach (BlockNode b in ok)
            {
                if (preReleaseHeaders.TryGetValue(b.Line, out string? word))
                {
                    L("L011", b, $"\"{word}\" is the pre-release form of a block header: v1 leaves the class and the number out.", new Opts { Needle = word, Hint = "Run ample migrate on the song: it shows the changes first. Only pinned@<version> stays in a header." });
                }
            }


            // L002: offers the nearest earlier block that has an output of the right type, written as a from-line right after the opening brace
            Opts ConnectOpts(BlockNode target, string inputName, string inputType, string hint)
            {
                Opts plain = new() { Needle = target.RawName, Hint = hint };
                BlockNode? source = ok.LastOrDefault(c => c.Line < target.Line && c.Name != target.Name && Ports(c).Outputs.Any(p => p.Type == inputType));
                string header = target.Line >= 0 && target.Line < lines.Length ? lines[target.Line] : string.Empty;
                IReadOnlyList<PortSpec> targetIns = Ports(target).Inputs;
                if (source is null || !header.TrimEnd().EndsWith('{') || !targetIns.Any(p => p.Name == inputName))
                {
                    return plain;
                }

                IReadOnlyList<PortSpec> sourceOuts = Ports(source).Outputs;
                PortSpec outPort = sourceOuts.First(p => p.Type == inputType), inPort = targetIns.First(p => p.Name == inputName);
                string line = $"from {source.RawName}.{PortNames.Public(outPort, sourceOuts, true)} -> {PortNames.Public(inPort, targetIns, false)}";
                return new Opts { Needle = target.RawName, Hint = hint, FixLabel = $"Connect {source.Name}", FixNeedle = "{", FixFrom = header.LastIndexOf('{'), FixText = "{\n  " + line, Risky = true };
            }
            HashSet<string> consumed = [.. ok.SelectMany(b => b.Froms.Select(f => f.Block + "." + f.Output)), .. ok.SelectMany(b => b.ControlRefs.Select(c => c.Block + "." + c.Output)), .. uses.SelectMany(u => u.Froms.Select(f => f.Block + "." + f.Output)),
                .. notePipelineSources];   // a clip that another clip reads in its note pipeline (notes a.notes |> merge(b.notes)) is used
            // AMP2095: the steps of a drum grid fill whole bars (a bar is 16 steps in 4/4): a shorter grid must divide the bar, a longer one must be whole bars
            int usualBar = Math.Max(1, (int)Math.Round(4 * (Project?.BeatsPerBar ?? 4.0)));
            foreach (BlockNode drums in ok.Where(x => x.Type == "drums"))
            {
                int gridBar = drums.Parameters.FirstOrDefault(p => p.Name == "steps") is { } stepsParameter && int.TryParse(stepsParameter.Value.Trim().Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int own) && own >= 1 ? own : usualBar;
                foreach (Parameter grids in drums.Parameters.Where(p => p.Name is "pattern" or "patterns"))
                {
                    foreach (System.Text.RegularExpressions.Match lane in System.Text.RegularExpressions.Regex.Matches(grids.Value, @"([A-Za-z][\w-]*):([xXo1-9.]+)"))
                    {
                        int steps = lane.Groups[2].Length;
                        if (steps <= gridBar ? gridBar % steps == 0 : steps % gridBar == 0)
                        {
                            continue;
                        }

                        Add(grids.Line, "AMP2095", Severity.Warning, $"The grid of \"{lane.Groups[1].Value}\" has {steps} steps, which does not fill whole bars ({gridBar} steps make a bar{(gridBar == usualBar ? string.Empty : " here")}): it starts again in the middle of a bar.", new Opts { Needle = lane.Groups[1].Value });
                    }
                }
            }

            foreach (BlockNode b in ok)
            {
                TemplateSpec t = spec.Templates[b.Type];
                IReadOnlyList<PortSpec> outs = t.Outputs;
                if (b.Type is not ("master" or "variants") && outs.Count > 0 && !outs.Any(o => consumed.Contains(b.Name + "." + o.Name)))
                {
                    L("L001", b, $"The output of \"{b.Name}\" is not used by any block.", new Opts { Needle = b.RawName });
                }

                foreach (PortSpec required in t.Inputs.Where(p => p.Required))
                {
                    if (!b.Froms.Any(f => f.Input == required.Name))
                    {
                        L("L002", b, $"Required input \"{required.Name}\" of \"{b.Name}\" has no source.", ConnectOpts(b, required.Name, required.Type, "Add: from <block>.<output> -> " + required.Name));
                    }
                }

                if (t.Inputs.Any(p => p.Name == "render_in" && p.Type == "render") && !b.Froms.Any(f => f.Input is "in" or "render_in" or "region_in"))
                {
                    L("L002", b, $"Effect \"{b.Name}\" needs an audio or render input.", ConnectOpts(b, "in", "audio", "Connect in or render_in."));
                }

                if (b.Flags.Contains("lock") && b.Flags.Contains("refresh"))
                {
                    L("L003", b, $"\"{b.Name}\" is locked and also always-refetch.", new Opts { Needle = "refresh", Hint = "A locked block keeps its value; refresh asks for a new one. Keep one.", FixLabel = "Remove \"refresh\"", FixText = string.Empty });
                }

                List<Part> sorted = [.. b.Parts.OrderBy(p => p.At)];
                for (int q = 1; q < sorted.Count; q++)
                {
                    if (sorted[q].At < sorted[q - 1].At + sorted[q - 1].Length)
                    {
                        L("L004", b, $"Parts of \"{b.Name}\" overlap (bar {sorted[q].At.ToString(CultureInfo.InvariantCulture)}).", new Opts { Needle = " at ", Hint = "Move or shorten one of the parts." });
                        break;
                    }
                }

                if (t.Dynamic && !b.Flags.Contains("bake") && !b.Flags.Contains("lock") && b.Freeze is null)
                {
                    L("L005", b, $"\"{b.Name}\" is dynamic and neither baked nor locked.", new Opts { Needle = b.RawName, Hint = "Bake it (add the word bake) before a release." });
                }

                if (t.Dynamic && b.Parameters.Any(p => (p.Name is "lm_seed" or "seed") && p.Value != "derived"))
                {
                    L("L009", b, $"\"{b.Name}\" uses custom seeds.", new Opts { Needle = b.RawName, Hint = "Fine, and recorded. Changing a seed later means takes made with the old one cannot be reproduced from the new one." });
                }

                foreach (FromLine f in b.Froms)
                {
                    // the stream names are the ones to write; the internal names (in, out, mix, in3, render_in) still work
                    BlockNode? fromBlock = ok.FirstOrDefault(x => x.Name == f.Block);
                    if (fromBlock is null) continue;
                    IReadOnlyList<PortSpec> fromOuts = Ports(fromBlock).Outputs, toIns = Ports(b).Inputs;
                    PortSpec? po = fromOuts.FirstOrDefault(p => p.Name == f.Output), pi = toIns.FirstOrDefault(p => p.Name == f.Input);
                    string writtenOut = f.RawOutput.Trim('"'), writtenIn = f.RawInput.Trim('"');
                    string wantOut = po is null ? writtenOut : PortNames.Public(po, fromOuts, true), wantIn = pi is null ? writtenIn : PortNames.Public(pi, toIns, false);
                    if (writtenOut != wantOut || writtenIn != wantIn)
                    {
                        string fromRaw = lines[f.Line];
                        bool fixOut = writtenOut != wantOut;
                        int blockEnd = Math.Max(0, fromRaw.IndexOf(f.RawBlock, StringComparison.Ordinal)) + f.RawBlock.Length;
                        string wasRaw = fixOut ? f.RawOutput : f.RawInput, want = fixOut ? wantOut : wantIn;
                        L("L010", b, $"\"{b.Name}\" connects {f.Block}.{writtenOut} -> {writtenIn}; the stream names are {f.Block}.{wantOut} -> {wantIn}.", new Opts { Needle = writtenIn, Hint = "A port is named by the stream it carries: audio, notes, render, audio1, audio2... Both spellings work.", FixLabel = $"Write {want}", FixLine = f.Line, FixNeedle = wasRaw, FixFrom = fixOut ? blockEnd : Math.Max(0, fromRaw.LastIndexOf(wasRaw, StringComparison.Ordinal)), FixText = wasRaw.StartsWith('"') ? "\"" + want + "\"" : want });
                        break;
                    }
                }

                if (b.Class == "pinned" && TemplateNewer(b.Version, t.Version))
                {
                    L("L006", b, $"\"{b.Name}\" is pinned at {b.Version} but the template is at {t.Version}.", new Opts { Needle = "pinned@" + b.Version, Hint = "Pinned blocks are updated on purpose; updating changes the sound.", FixLabel = $"Update to {t.Version} (changes the sound)", FixText = "pinned@" + t.Version, Risky = true });
                }
            }

            if (ok.Any(b => b.Type == "master"))
            {
                Dictionary<string, List<string>> down = [];
                foreach (BlockNode b in ok)
                {
                    foreach (FromLine f in b.Froms)
                    {
                        if (!down.TryGetValue(f.Block, out List<string>? list))
                        {
                            down[f.Block] = list = [];
                        }

                        list.Add(b.Name);
                    }
                }

                bool Reach(string title, HashSet<string> seenReach)
                {
                    if (!seenReach.Add(title))
                    {
                        return false;
                    }

                    BlockNode? bb = ok.FirstOrDefault(x => x.Name == title);
                    return (bb is not null && bb.Type == "master") || (down.TryGetValue(title, out List<string>? next) && next.Any(n => Reach(n, seenReach)));
                }

                foreach (BlockNode b in ok)
                {
                    if (b.Type != "master" && spec.Templates[b.Type].Outputs.Any(o => o.Type == "audio") && !Reach(b.Name, []))
                    {
                        L("L007", b, $"\"{b.Name}\" makes audio but never reaches the master.", new Opts { Needle = b.RawName, Hint = "Connect it to the mixer or another block that leads to the master." });
                    }
                }
            }

            Dictionary<string, BlockNode> lower = [];
            foreach (BlockNode b in ok)
            {
                string key = b.Name.ToLowerInvariant();
                if (lower.TryGetValue(key, out BlockNode? other) && other.Name != b.Name)
                {
                    L("L008", b, $"Block names \"{other.Name}\" and \"{b.Name}\" differ only by case.", new Opts { Needle = b.RawName, Related = new Related(other.Line + 1, "the other one") });
                }
                else
                {
                    lower[key] = b;
                }
            }

            // L013: a controller of a clip that is not a value it can play (the build would stop with AMP5506); L012: a clip with controllers that only the gm block
            // plays (modwheel, sustain, the sends, cc) whose notes reach another instrument, directly or through note blocks (it plays only volume, expression and pan)
            bool NotesFrom(BlockNode b, string clip, HashSet<string> seen) => b.Froms.Where(f => !f.Disabled && f.Output == "notes")
                .Any(f => f.Block == clip || (seen.Add(f.Block) && blocks.FirstOrDefault(x => !x.Skipped && x.Name == f.Block) is { } up && NotesFrom(up, clip, seen)));
            foreach (BlockNode clip in ok.Where(x => x.Type == "midi-clip"))
            {
                List<string> gmOnly = [];
                foreach ((string name, _, string unit) in ClipControlSyntax.Named)
                {
                    string? value = clip.Parameters.FirstOrDefault(p => p.Name == name)?.Value;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (!ClipControlSyntax.Valid(value, unit)) L("L013", clip, $"The {name} of \"{clip.Name}\" is not a value it can play: {value.Trim()}.", new Opts { Needle = name, Hint = $"{name} takes {ClipControlSyntax.UnitText(unit)}, or a curve such as auto(1:0, 2:100)." });
                    else if (!ClipControlSyntax.EveryInstrument.Contains(name)) gmOnly.Add(name);
                }

                string? cc = clip.Parameters.FirstOrDefault(p => p.Name == "cc")?.Value;
                if (!string.IsNullOrWhiteSpace(cc))
                {
                    if (ClipControlSyntax.ParseCc(cc) is null) L("L013", clip, $"The cc of \"{clip.Name}\" is not a list of controller=value entries: {cc.Trim()}.", new Opts { Needle = "cc", Hint = "Write it like \"74=auto(1:20, 4:100) 67=127\": controllers and values from 0 to 127." });
                    else gmOnly.Add("cc");
                }

                if (gmOnly.Count == 0) continue;
                foreach (BlockNode player in ok.Where(x => !ClipControlSyntax.PlaysAll.Contains(x.Type) && spec.Templates[x.Type].Outputs.Any(o => o.Type == "audio") && NotesFrom(x, clip.Name, [])))
                {
                    L("L012", player, $"\"{player.Name}\" plays only volume, expression and pan of the clip \"{clip.Name}\"; {string.Join(", ", gmOnly)} need a gm block.", new Opts { Needle = player.RawName, Hint = "Play the clip with a gm block (or midi-render), or leave these controllers out." });
                }
            }
        }

        private static bool TemplateNewer(string have, string template)
        {
            int[] a = [.. have.Split('.').Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)];
            int[] c = [.. template.Split('.').Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)];
            for (int ix = 0; ix < c.Length; ix++)
            {
                int ai = ix < a.Length ? a[ix] : 0;
                if (c[ix] > ai && Enumerable.Range(0, ix).All(jx => c[jx] == (jx < a.Length ? a[jx] : 0)))
                {
                    return true;
                }
            }

            return false;
        }

        private (IReadOnlyList<PortSpec> Inputs, IReadOnlyList<PortSpec> Outputs) PortsOf(string type) =>
            spec.Templates.TryGetValue(type, out TemplateSpec? t) ? (t.ControlInputs.Count == 0 ? t.Inputs : [.. t.Inputs, .. t.ControlInputs.Where(c => !t.Inputs.Any(i => i.Name == c.Name))], t.Outputs) : ([], []);

        private (IReadOnlyList<PortSpec> Inputs, IReadOnlyList<PortSpec> Outputs) Ports(BlockNode b) => PortsOf(b.Type);

        private static int IndexOf(IReadOnlyList<PortSpec> ports, string name)
        {
            for (int i = 0; i < ports.Count; i++)
            {
                if (ports[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string DidYou(string? near) => near is null ? string.Empty : $"Did you mean \"{near}\"?";

        /// <summary>The quick fix "Use for auto" of AMP2089: " at 1 for auto" right after the name of the block (after a pre-release class word when there is one).</summary>
        private static Fix? AutoLengthFix(string header, BlockNode b)
        {
            int typeAt = header.IndexOf(b.Type, StringComparison.Ordinal);
            int nameAt = typeAt < 0 ? -1 : header.IndexOf(b.RawName, typeAt + b.Type.Length, StringComparison.Ordinal);
            if (nameAt < 0) return null;
            int after = nameAt + b.RawName.Length;
            Match cls = Regex.Match(header[after..], @"^\s+(?:stable|pinned|live)(?:@\S+)?", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (cls.Success) after += cls.Length;
            int col = CodePointColumn(header, after) + 1;
            return new Fix("Use for auto", "replace", " at 1 for auto", b.Line + 1, col, col, false);
        }

        private static int CodePointColumn(string raw, int utf16Index)
        {
            int cp = 0;
            for (int i = 0; i < utf16Index && i < raw.Length; i++)
            {
                if (!char.IsLowSurrogate(raw[i]))
                {
                    cp++;
                }
            }

            return cp;
        }

        private void Add(int i, string code, Severity severity, string message, Opts o)
        {
            string raw = i >= 0 && i < lines.Length ? lines[i] : string.Empty;
            int colIndex = raw.Length - raw.TrimStart().Length;
            int endIndex = raw.TrimEnd().Length;
            if (o.Needle is not null)
            {
                int ix = raw.IndexOf(o.Needle, Math.Clamp(o.From, 0, raw.Length), StringComparison.Ordinal);
                if (ix >= 0)
                {
                    colIndex = ix;
                    endIndex = ix + o.Needle.Length;
                }
            }

            if (o.ColStart is int explicitStart && explicitStart >= 0)
            {
                colIndex = Math.Min(explicitStart, raw.Length);
                endIndex = Math.Clamp(o.ColEnd ?? raw.TrimEnd().Length, colIndex, raw.Length);
            }

            int col = CodePointColumn(raw, colIndex) + 1;
            int endCol = CodePointColumn(raw, endIndex) + 1;
            if (endCol <= col)
            {
                endCol = col + 1;
            }

            // a fix may live on another line than the diagnostic (L010 points at the block, the fix edits its from-line)
            int fixLine = o.FixLine is int other && other >= 0 && other < lines.Length ? other : i;
            string fixRaw = fixLine == i ? raw : lines[fixLine];
            int fixCol = col, fixEnd = endCol;
            if (o.FixNeedle is not null && fixRaw.IndexOf(o.FixNeedle, Math.Clamp(o.FixFrom, 0, fixRaw.Length), StringComparison.Ordinal) is int at and >= 0)
            {
                fixCol = CodePointColumn(fixRaw, at) + 1;
                fixEnd = CodePointColumn(fixRaw, at + o.FixNeedle.Length) + 1;
            }

            Fix? fix = o.Explicit ?? (o.FixText is null && o.FixLabel is null ? null : new Fix(o.FixLabel ?? string.Empty, "replace", o.FixText ?? string.Empty, fixLine + 1, fixCol, fixEnd, o.Risky));
            diagnostics.Add(new Diagnostic(code, severity, i + 1, col, endCol, message, o.Hint ?? string.Empty, fix, o.Related));
        }

        private sealed class Opts
        {
            public string? Needle { get; init; }

            public int? ColStart { get; init; }

            public int? ColEnd { get; init; }

            public int From { get; init; }

            public string? Hint { get; init; }

            public Fix? Explicit { get; init; }

            public string? FixLabel { get; init; }

            public string? FixNeedle { get; init; }

            public int? FixLine { get; init; }

            public int FixFrom { get; init; }

            public string? FixText { get; init; }

            public bool Risky { get; init; }

            public Related? Related { get; init; }
        }
    }
}
