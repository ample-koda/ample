using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Ample.VisualStudio;

/// <summary>
/// Colours .amplet files with the AMPLE palette. It follows the rules of the TextMate grammar of the VS Code extension (comments, strings,
/// numbers, the block types, names, parameters, from-lines, directives and the view) line by line, with the state of being inside a block or
/// the view. Switched off (<see cref="AmplePalette.Enabled"/>), it gives nothing and Visual Studio's own colours show.
/// </summary>
[Export(typeof(IClassifierProvider))]
[ContentType(AmpleContentType.Name)]
internal sealed class AmpleClassifierProvider : IClassifierProvider
{
    [Import] internal IClassificationTypeRegistryService Registry { get; set; } = null!;

    public IClassifier GetClassifier(ITextBuffer buffer) =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new AmpleClassifier(buffer, Registry));
}

internal sealed class AmpleClassifier : IClassifier
{
    private const string Id = "(?:\"(?:[^\"\\\\]|\\\\.)*\"|[A-Za-z0-9_-]+)";
    private const string BlockTypes = "ace-music|ai-voice|ambience|arpeggiator|autotune|bass|bitcrusher|bus|choir|chop|chord|chorus|click|compressor|convolution|"
        + "crossfade|curve|cut|deesser|delay|delay-llama|denoise|distortion|drums|duck|eq|fade|filter|fire|flanger|follow|gate|gm|granular|group|guitar|"
        + "import-audio|join|limiter|lyrics|map|master|meter|midi|midi-clip|midi-humanize|midi-render|mixer|mood-scene|normalize|pan|peq|phaser|piano|"
        + "pitch|quantize|region-merge|reverb|reverse|rewind|sampler|scorer|scratch|send|sequencer|shift|silence|speech|splitter|stretch|synth|"
        + "transpose|tremolo|trim|variants|vocoder|voice-clone|volume|width";

    private static readonly Regex Comment = new("#.*$", RegexOptions.Compiled);
    private static readonly Regex Directive = new(@"^(composition|project|features|lint|assist)\b(.*)$", RegexOptions.Compiled);
    private static readonly Regex Layout = new("^(layout)\\s+(" + Id + ")\\s+(-?[0-9]+)\\s+(-?[0-9]+)", RegexOptions.Compiled);
    private static readonly Regex ViewStart = new(@"^(view)\s*(\{)", RegexOptions.Compiled);
    private static readonly Regex BlockStart = new("^(" + BlockTypes + ")\\s+(" + Id + ")([^{#\\n]*)(\\{)", RegexOptions.Compiled);
    private static readonly Regex End = new(@"^\s*(\})", RegexOptions.Compiled);
    private static readonly Regex From = new("^\\s*(from)\\s+(" + Id + ")(\\.)(" + Id + ")\\s*(->)\\s*(" + Id + ")", RegexOptions.Compiled);
    private static readonly Regex Parameter = new(@"^\s*([A-Za-z0-9_-]+)(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex ViewWord = new(@"^\s*(section|line|order|group|size|pin|member|title|label|x|y)\b", RegexOptions.Compiled);
    private static readonly Regex String = new("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"-?\b[0-9]+(?:\.[0-9]+)?\b", RegexOptions.Compiled);
    private static readonly Regex ClassVersion = new(@"\b(stable|pinned|live)(@)([0-9]+(?:\.[0-9]+)*)", RegexOptions.Compiled);
    private static readonly Regex HeaderWord = new(@"\b(at|for|as|bake|lock|refresh|freeze|seeds|plan)\b", RegexOptions.Compiled);
    private static readonly Regex Import = new(@"^(import)\s+""", RegexOptions.Compiled);
    private static readonly Regex CodeUnit = new(@"^(helper|component)\s+([A-Za-z0-9_.-]+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex ComponentVersion = new(@"\bversion\s+([0-9][0-9A-Za-z.+-]*)", RegexOptions.Compiled);
    private static readonly Regex Instance = new("^([A-Za-z_][A-Za-z0-9_-]*(?:\\.[A-Za-z0-9_-]+)+)\\s+(" + Id + ")([^{#\\n]*)(\\{)", RegexOptions.Compiled);
    private static readonly Regex CodeKeyword = new(@"\b(input|output|property|process|emit|for|in|let|return|if|else|id|version|current|stable|pinned|live)\b", RegexOptions.Compiled);
    private static readonly Regex TypeWord = new(@"\b(Notes|Int|Float|Number|Duration|Text|Bool|Audio|Beats|List|Record)\b", RegexOptions.Compiled);
    private static readonly Regex CodeOperator = new(@"\|>|->|\.\.", RegexOptions.Compiled);
    private static readonly Regex InlineWord = new(@"\b(tempo|master-seed|on|off|diff|button|auto)\b", RegexOptions.Compiled);

    private readonly ITextBuffer buffer;
    private readonly IClassificationTypeRegistryService registry;
    private bool logged;
    private ITextSnapshot? cachedSnapshot;
    private List<(int Start, int Length, string Kind)> cached = [];

    public AmpleClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
    {
        this.buffer = buffer;
        this.registry = registry;
        buffer.Changed += (_, _) => Raise();
        AmplePalette.Changed += (_, _) => Raise();
    }

    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;

    private void Raise()
    {
        ITextSnapshot snapshot = buffer.CurrentSnapshot;
        ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        List<ClassificationSpan> result = [];
        if (!AmplePalette.Enabled) return result;
        if (!ReferenceEquals(cachedSnapshot, span.Snapshot))
        {
            cached = Tokenize(span.Snapshot);
            cachedSnapshot = span.Snapshot;
        }

        if (!logged)
        {
            logged = true;
            AmpleLog.Write($"palette: classifier used by Visual Studio, {cached.Count} tokens, dark theme {AmplePalette.IsDarkTheme()}, first {(cached.Count > 0 ? cached[0].Kind : "none")}");
        }

        foreach ((int start, int length, string kind) in cached)
        {
            if (start + length < span.Start || start > span.End) continue;
            result.Add(new ClassificationSpan(new SnapshotSpan(span.Snapshot, start, length), registry.GetClassificationType(kind)));
        }

        return result;
    }

    internal static List<(int Start, int Length, string Kind)> Tokenize(ITextSnapshot snapshot)
    {
        List<(int, int, string)> tokens = [];
        bool inBlock = false, inView = false, inDoc = false;
        int codeDepth = 0;
        foreach (ITextSnapshotLine line in snapshot.Lines)
        {
            Tokenize(line.GetText(), line.Start.Position, tokens, ref inBlock, ref inView, ref codeDepth, ref inDoc);
        }

        return tokens;
    }

    /// <summary>Tokenizes one line (offset = where the line starts in the buffer); the two flags carry whether a block or the view is open.</summary>
    internal static void Tokenize(string text, int offset, List<(int Start, int Length, string Kind)> tokens, ref bool inBlock, ref bool inView, ref int codeDepth, ref bool inDoc)
    {
        // a doc comment (/** ... */) and an attribute line (#[...]) come before the "# comment" rule
        if (inDoc)
        {
            tokens.Add((offset, text.Length, AmplePalette.Comment));
            if (text.Contains("*/")) inDoc = false;
            return;
        }

        string trimmed = text.TrimStart();
        int lead = text.Length - trimmed.Length;
        if (trimmed.StartsWith("/*", StringComparison.Ordinal))
        {
            tokens.Add((offset + lead, trimmed.Length, AmplePalette.Comment));
            inDoc = !trimmed.Contains("*/");
            return;
        }

        if (trimmed.StartsWith("#[", StringComparison.Ordinal))
        {
            tokens.Add((offset + lead, trimmed.Length, AmplePalette.Property));
            return;
        }

        // a comment takes the rest of the line (the grammar's comment rule comes first), but not inside a string: find the first # outside strings
        int hash = FirstHashOutsideStrings(text);
        string code = hash < 0 ? text : text.Substring(0, hash);
        if (hash >= 0) tokens.Add((offset + hash, text.Length - hash, AmplePalette.Comment));
        if (code.Length == 0) return;

        void Add(Group g, string kind, int shift = 0)
        {
            if (g.Success && g.Length > 0) tokens.Add((offset + shift + g.Index, g.Length, kind));
        }

        void Words(string part, int at, bool header)
        {
            foreach (Match m in String.Matches(part)) tokens.Add((offset + at + m.Index, m.Length, AmplePalette.Str));
            foreach (Match m in Number.Matches(part)) if (!InString(part, m.Index)) tokens.Add((offset + at + m.Index, m.Length, AmplePalette.Number));
            if (!header) return;
            foreach (Match m in ClassVersion.Matches(part))
            {
                tokens.Add((offset + at + m.Groups[1].Index, m.Groups[1].Length, AmplePalette.BlockType));
                tokens.Add((offset + at + m.Groups[2].Index, m.Groups[2].Length, AmplePalette.Punctuation));
                tokens.Add((offset + at + m.Groups[3].Index, m.Groups[3].Length, AmplePalette.Version));
            }

            foreach (Match m in HeaderWord.Matches(part)) if (!InString(part, m.Index)) tokens.Add((offset + at + m.Index, m.Length, AmplePalette.Keyword));
        }

        if (codeDepth > 0)
        {
            CodeLine(code, offset, tokens);
            codeDepth += BraceDelta(code);
            return;
        }

        if (inBlock || inView)
        {
            Match close = End.Match(code);
            if (close.Success)
            {
                Add(close.Groups[1], AmplePalette.Punctuation);
                inBlock = false;
                inView = false;
                return;
            }
        }

        if (inView)
        {
            Match word = ViewWord.Match(code);
            if (word.Success) Add(word.Groups[1], AmplePalette.Directive);
            foreach (Match m in Regex.Matches(code, @"\b(at|for)\b")) if (!InString(code, m.Index)) tokens.Add((offset + m.Index, m.Length, AmplePalette.Keyword));
            Words(code, 0, false);
            return;
        }

        if (inBlock)
        {
            Match from = From.Match(code);
            if (from.Success)
            {
                Add(from.Groups[1], AmplePalette.Directive);
                Add(from.Groups[2], AmplePalette.Reference);
                Add(from.Groups[3], AmplePalette.Punctuation);
                Add(from.Groups[4], AmplePalette.Property);
                Add(from.Groups[5], AmplePalette.Operator);
                Add(from.Groups[6], AmplePalette.Declaration);
                return;
            }

            Match parameter = Parameter.Match(code);
            if (parameter.Success) Add(parameter.Groups[1], AmplePalette.Property);
            Words(code, 0, false);
            return;
        }

        Match view = ViewStart.Match(code);
        if (view.Success)
        {
            Add(view.Groups[1], AmplePalette.Directive);
            Add(view.Groups[2], AmplePalette.Punctuation);
            inView = true;
            return;
        }

        Match import = Import.Match(code);
        if (import.Success)
        {
            Add(import.Groups[1], AmplePalette.Directive);
            Words(code, 0, false);
            return;
        }

        Match unit = CodeUnit.Match(code);
        if (unit.Success)
        {
            Add(unit.Groups[1], AmplePalette.Directive);
            Add(unit.Groups[2], AmplePalette.Declaration);
            string rest = unit.Groups[3].Value;
            Match version = ComponentVersion.Match(rest);
            if (version.Success)
            {
                // the version number is one token (not also numbers): blank it for the keyword/number pass
                Add(version.Groups[1], AmplePalette.Version, unit.Groups[3].Index);
                rest = rest.Remove(version.Groups[1].Index, version.Groups[1].Length).Insert(version.Groups[1].Index, new string(' ', version.Groups[1].Length));
            }

            CodeLine(rest, offset + unit.Groups[3].Index, tokens);
            codeDepth = BraceDelta(code);
            return;
        }

        Match instance = Instance.Match(code);
        if (instance.Success)
        {
            Add(instance.Groups[1], AmplePalette.BlockType);
            Add(instance.Groups[2], AmplePalette.Declaration);
            Words(instance.Groups[3].Value, instance.Groups[3].Index, true);
            Add(instance.Groups[4], AmplePalette.Punctuation);
            inBlock = true;
            return;
        }

        Match directive = Directive.Match(code);
        if (directive.Success)
        {
            Add(directive.Groups[1], AmplePalette.Directive);
            string rest = directive.Groups[2].Value;
            int restAt = directive.Groups[2].Index;
            Words(rest, restAt, true);
            foreach (Match m in InlineWord.Matches(rest)) if (!InString(rest, m.Index)) tokens.Add((offset + restAt + m.Index, m.Length, AmplePalette.Keyword));
            return;
        }

        Match layout = Layout.Match(code);
        if (layout.Success)
        {
            Add(layout.Groups[1], AmplePalette.Directive);
            Add(layout.Groups[2], AmplePalette.Declaration);
            Add(layout.Groups[3], AmplePalette.Number);
            Add(layout.Groups[4], AmplePalette.Number);
            return;
        }

        Match block = BlockStart.Match(code);
        if (block.Success)
        {
            Add(block.Groups[1], AmplePalette.BlockType);
            Add(block.Groups[2], AmplePalette.Declaration);
            Words(block.Groups[3].Value, block.Groups[3].Index, true);
            Add(block.Groups[4], AmplePalette.Punctuation);
            inBlock = true;
        }
    }

    /// <summary>One line of a helper or component body: keywords, types, strings, numbers and the pipe and range operators.</summary>
    private static void CodeLine(string code, int offset, List<(int Start, int Length, string Kind)> tokens)
    {
        foreach (Match m in String.Matches(code)) tokens.Add((offset + m.Index, m.Length, AmplePalette.Str));
        foreach (Match m in Number.Matches(code)) if (!InString(code, m.Index)) tokens.Add((offset + m.Index, m.Length, AmplePalette.Number));
        foreach (Match m in CodeKeyword.Matches(code)) if (!InString(code, m.Index)) tokens.Add((offset + m.Index, m.Length, AmplePalette.Keyword));
        foreach (Match m in TypeWord.Matches(code)) if (!InString(code, m.Index)) tokens.Add((offset + m.Index, m.Length, AmplePalette.BlockType));
        foreach (Match m in CodeOperator.Matches(code)) if (!InString(code, m.Index)) tokens.Add((offset + m.Index, m.Length, AmplePalette.Operator));
    }

    /// <summary>Opening braces minus closing braces outside strings.</summary>
    private static int BraceDelta(string code)
    {
        int delta = 0;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '"') { i = SkipString(code, i); continue; }
            if (code[i] == '{') delta++;
            else if (code[i] == '}') delta--;
        }

        return delta;
    }

    private static int SkipString(string text, int start)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == (char)92) i++;
            else if (text[i] == '"') return i;
        }

        return text.Length;
    }

    private static bool InString(string text, int index)
    {
        bool inString = false;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\\' && inString) { i++; continue; }
            if (text[i] == '"') inString = !inString;
        }

        return inString;
    }

    private static int FirstHashOutsideStrings(string text)
    {
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && inString) { i++; continue; }
            if (text[i] == '"') inString = !inString;
            else if (text[i] == '#' && !inString) return i;
        }

        return -1;
    }
}
