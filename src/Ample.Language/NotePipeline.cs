using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>One problem found in a note pipeline: a symbolic id, a message and the span of the text it is about (offsets into the expression text).</summary>
/// <param name="Id">Symbolic id (syntax, unknown-operator, wrong-unit, missing-unit, bad-argument, range-reversed, fractional-bar, before-origin, unknown-reference, reference-cycle, stale-bake, dirty-bake, bad-notes, missing-file, cap-events, unbaked).</param>
/// <param name="Message">Human text.</param>
/// <param name="Start">Offset of the first character.</param>
/// <param name="End">Offset after the last character.</param>
/// <param name="Suggestion">A "did you mean" replacement, or null.</param>
public sealed record PipeIssue(string Id, string Message, int Start, int End, string? Suggestion = null)
{
    /// <summary>Gets whether the issue is an error because its stage is <c>strict</c>.</summary>
    public bool Strict { get; init; }
}

/// <summary>One note of a pipeline value, in exact micro-beats (1 beat = 1,000,000; 1 bar = 4 beats) from the origin of the value; the routing fields and the per-note extensions ride along as text.</summary>
public sealed record PipeNote(long At, long Len, int Pitch, int Vel, string Tail, string Ext);

/// <summary>A value of the note pipelines: notes plus a span (the length of the value, in micro-beats).</summary>
public sealed class PipeValue
{
    /// <summary>Gets the notes.</summary>
    public List<PipeNote> Events { get; init; } = [];

    /// <summary>Gets or sets the span in micro-beats.</summary>
    public long Span { get; set; }

    internal PipeValue Copy() => new() { Events = [.. Events], Span = Span };
}

/// <summary>One use of a seed by an operator or a value function (provenance): which seed, the key it derives to, and how many values were drawn.</summary>
/// <param name="Operator">The operator, value function or stream.</param>
/// <param name="Seed">The seed expression as written, normalized.</param>
/// <param name="Key">The derived 64-bit key (16 hexadecimal digits).</param>
/// <param name="Draws">How many values were drawn.</param>
public sealed record SeedUseInfo(string Operator, string Seed, string Key, long Draws);

/// <summary>A random-stream of a clip after the evaluation (provenance): its seed, its key and how many values were consumed.</summary>
/// <param name="Name">The name.</param>
/// <param name="Seed">The seed expression.</param>
/// <param name="Key">The derived key, empty when the stream was never read.</param>
/// <param name="Consumed">How many values next() gave.</param>
public sealed record StreamUseInfo(string Name, string Seed, string Key, long Consumed);

/// <summary>The outcome of evaluating a pipeline.</summary>
public sealed class PipeResult
{
    /// <summary>Gets the compact notes text, or null when there were errors.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets the span in micro-beats (0 when there were errors).</summary>
    public long Span { get; init; }

    /// <summary>Gets the warnings.</summary>
    public List<PipeIssue> Warnings { get; init; } = [];

    /// <summary>Gets the errors.</summary>
    public List<PipeIssue> Errors { get; init; } = [];

    /// <summary>Gets the recipe hash of the whole expression (what its baked stages and references say), or null when it could not be computed.</summary>
    public string? Recipe { get; init; }

    /// <summary>Gets the notes in structured form (null when there were errors).</summary>
    public PipeValue? Value { get; init; }

    /// <summary>Gets where the randomness came from: every use of a seed.</summary>
    public IReadOnlyList<SeedUseInfo> Seeds { get; init; } = [];

    /// <summary>Gets the random-streams of the clip and how much of each was consumed.</summary>
    public IReadOnlyList<StreamUseInfo> Streams { get; init; } = [];

    /// <summary>Gets whether a volatile value (the clock, OS randomness, the environment, a dynamic helper) took part and was not baked: the result can differ the next time.</summary>
    public bool Dynamic { get; init; }

    /// <summary>Gets the volatile values that were read, as <c>currentTime() = 14:20:03</c> (provenance of a take of a dynamic song).</summary>
    public IReadOnlyList<string> DynamicInputs { get; init; } = [];

    /// <summary>Gets whether a volatile value was baked on purpose (a value bake or a baked helper) and so did not run.</summary>
    public bool BakedDynamic { get; init; }

    /// <summary>Gets the number of evaluation steps (fuel) the helper code used.</summary>
    public long Fuel { get; init; }
}

/// <summary>One parsed <c>bake</c> stage.</summary>
/// <param name="Name">The name of the stage.</param>
/// <param name="Start">Offset of the word <c>bake</c>.</param>
/// <param name="End">Offset after the stage (after the closing brace when it has a body).</param>
/// <param name="HasBody">Whether the stage has a body.</param>
/// <param name="Strict">Whether a manual change is an error.</param>
/// <param name="InputHash">The stored recipe hash, or null.</param>
/// <param name="NotesHash">The stored signature of the notes, or null.</param>
/// <param name="Span">The stored span text (for example 8bars), or null.</param>
/// <param name="NotesText">The stored notes, or null.</param>
/// <param name="File">The file the notes are kept in, or null.</param>
public sealed record BakeStage(string Name, int Start, int End, bool HasBody, bool Strict, string? InputHash, string? NotesHash, string? Span, string? NotesText, string? File);

/// <summary>
/// The note pipelines (docs/language/12-note-pipelines.md): <c>take.notes |&gt; bars(1..=4) |&gt; transpose(+12)</c> as an exact, pure function from sources and a tempo to compact notes text.
/// The JavaScript twin is web/lib/note-pipeline.mjs; both run spec-tests/note-pipelines. Exact maths only: decimals are rationals, time is an integer number of micro-beats, seconds convert with the
/// song tempo and round half to even.
/// </summary>
public static partial class NotePipeline
{
    /// <summary>Micro-beats in one beat.</summary>
    public const long Beat = 1_000_000;

    [ThreadStatic]
    private static long meterBar;

    /// <summary>Micro-beats in one bar: 4 beats (4/4), or the beats of the song's meter inside a <see cref="UseMeter"/> scope (3/4: 3 beats, 7/8: 3.5 beats).</summary>
    public static long Bar => meterBar == 0 ? 4 * Beat : meterBar;

    /// <summary>Makes a bar this many quarter-note beats long on the current thread until the scope is disposed (the song's meter); 4 is the default of 4/4.</summary>
    /// <param name="beatsPerBar">The beats of a bar in quarter notes.</param>
    /// <returns>The scope to dispose.</returns>
    public static IDisposable UseMeter(decimal beatsPerBar)
    {
        long previous = meterBar;
        long micro = (long)(beatsPerBar * Beat);
        meterBar = micro == 4 * Beat || micro <= 0 ? 0 : micro;
        return new MeterScope(previous);
    }

    private sealed class MeterScope(long previous) : IDisposable
    {
        public void Dispose() => meterBar = previous;
    }

    /// <summary>The largest number of notes a pipeline value may hold while it is evaluated.</summary>
    public const int MaxEvents = 1_000_000;

    /// <summary>The most notes any pipeline may hold, whatever a song asks for: the limit of the server (a song may raise its own limit up to this, never past it).</summary>
    public const int HardCap = 2_000_000;

    /// <summary>The default limit of a build: lower than the hard cap, so a pipeline that runs away is stopped early; a song raises it with settings { notes.maxEvents ... }.</summary>
    public const int DefaultBuildLimit = 200_000;

    private static readonly string[] OperatorNames = ["bars", "beats", "time", "mask", "remove", "cut", "replace", "transpose", "shift", "concat", "merge", "reverse", "stretch", "legato", "quantize", "humanize", "repeat", "arp", "where", "euclid", "randomize", "thin", "scramble", "clamp", "fold", "snap", "keep", "avoid", "walk", "chords", "scale", "set"];

    private static readonly Dictionary<string, string> OperatorVersions = OperatorNames.Concat(["random", "choose", "chance", "stable", "random-seed"]).ToDictionary(n => n, _ => "1", StringComparer.Ordinal);

    /// <summary>Whether the value of a <c>notes</c> parameter is an expression (it starts with a word or a parenthesis) and not a plain note literal.</summary>
    /// <param name="value">The raw value text.</param>
    /// <returns>True for an expression.</returns>
    public static bool IsExpression(string value)
    {
        string v = StripComments(value).Trim().Trim('"');
        return v.Length > 0 && (char.IsLetter(v[0]) || v.StartsWith("__c_", StringComparison.Ordinal) || v[0] == '(');
    }

    // ---------- exact numbers

    private readonly record struct Rat(BigInteger N, BigInteger D)
    {
        public static Rat Of(BigInteger n, BigInteger? d = null)
        {
            BigInteger den = d ?? BigInteger.One;
            if (den.Sign < 0) { n = -n; den = -den; }
            BigInteger g = BigInteger.GreatestCommonDivisor(n, den);
            if (g.IsZero) g = BigInteger.One;
            return new Rat(n / g, den / g);
        }

        public bool IsInt => D.IsOne;

        public static Rat operator *(Rat a, Rat b) => Of(a.N * b.N, a.D * b.D);

        public static Rat operator -(Rat a, Rat b) => Of((a.N * b.D) - (b.N * a.D), a.D * b.D);

        public static Rat operator +(Rat a, Rat b) => Of((a.N * b.D) + (b.N * a.D), a.D * b.D);

        public override string ToString() => D.IsOne ? N.ToString(CultureInfo.InvariantCulture) : N.ToString(CultureInfo.InvariantCulture) + "/" + D.ToString(CultureInfo.InvariantCulture);
    }

    private static BigInteger RoundHalfEven(Rat q)
    {
        BigInteger whole = BigInteger.DivRem(q.N, q.D, out BigInteger rest);
        if (rest.Sign < 0) { whole -= 1; rest += q.D; }
        BigInteger twice = rest * 2;
        int c = twice.CompareTo(q.D);
        if (c > 0 || (c == 0 && !whole.IsEven)) whole += 1;
        return whole;
    }

    private static Rat? ParseDecimal(string text)
    {
        Match m = DecimalPattern().Match(text);
        if (!m.Success) return null;
        string frac = m.Groups[3].Value;
        BigInteger n = BigInteger.Parse(m.Groups[2].Value + frac, CultureInfo.InvariantCulture);
        if (m.Groups[1].Value == "-") n = -n;
        return Rat.Of(n, BigInteger.Pow(10, frac.Length));
    }

    private static long? ToMicro(Rat q, string? unit, Rat tempo)
    {
        Rat micro;
        switch (unit)
        {
            case "bar" or "bars": micro = q * Rat.Of(Bar); break;
            case "beat" or "beats": micro = q * Rat.Of(Beat); break;
            case "tick" or "ticks": micro = q; break;
            case "s": micro = q * tempo * Rat.Of(Beat, 60); break;
            case "ms": micro = q * tempo * Rat.Of(Beat, 60_000); break;
            default: return null;
        }

        BigInteger r = RoundHalfEven(micro);
        return r > long.MaxValue / 4 || r < long.MinValue / 4 ? null : (long)r;
    }

    // ---------- tokens and the syntax tree

    private enum T { Pipe, Dots, DotsEq, LParen, RParen, Comma, Dot, LBrace, RBrace, Eq, Entry, Number, Ident, Str, Plus, Minus, Star, Slash, Mod, Lt, Gt, Le, Ge, EqEq, NotEq, AndAnd, OrOr, Bang, LBracket, RBracket, Arrow, Semi, Bar, Hash, Question, Colon }

    private sealed record Tok(T Kind, string Text, int Start, int End, string? Unit = null);

    internal abstract record Node(int Start, int End);

    private sealed record LiteralNode(string Entries, int Start, int End) : Node(Start, End);

    private sealed record EditableNode(string Entries, int Start, int End) : Node(Start, End);

    private sealed record RefNode(string Block, string Port, int Start, int End, bool Escaped = false) : Node(Start, End);

    private sealed record QuantityNode(Rat Value, string? Unit, string Text, int Start, int End) : Node(Start, End);

    private sealed record RangeNode(QuantityNode From, QuantityNode To, bool Inclusive, int Start, int End) : Node(Start, End);

    private sealed record CallNode(string Name, List<Node> Args, int Start, int End, int NameEnd) : Node(Start, End);

    private sealed record BakeNode(BakeStage Stage, int Start, int End) : Node(Start, End);

    private sealed record NamedNode(string Name, Node Value, int Start, int End) : Node(Start, End);

    private sealed record StringNode(string Value, int Start, int End) : Node(Start, End);

    /// <summary>A conditional expression: <c>cond ? then : otherwise</c>. Only the branch that is taken is worked out; the condition is read as true or false by the truthiness rule of docs/language/14.</summary>
    private sealed record TernaryNode(Node Cond, Node Then, Node Otherwise, int Start, int End) : Node(Start, End);

    private sealed record WordNode(string Text, int Start, int End, bool Escaped = false) : Node(Start, End);

    private sealed record MethodNode(Node Target, string Name, List<Node> Args, int Start, int End) : Node(Start, End);

    private sealed record PipelineNode(Node Source, List<Node> Steps, int Start, int End) : Node(Start, End);

    private sealed class SyntaxError(string message, int start, int end) : Exception(message)
    {
        public int Start { get; } = start;

        public int End { get; } = end;
    }

    /// <summary>Replaces a comment (from a # to the end of the line) with spaces so that offsets stay the same.</summary>
    /// <param name="text">The expression text.</param>
    /// <returns>The text without comments.</returns>
    public static string StripComments(string text)
    {
        if (!text.Contains('#', StringComparison.Ordinal)) return text;
        StringBuilder sb = new(text.Length);
        bool inString = false, inComment = false;
        foreach (char c in text)
        {
            if (inComment) { if (c == '\n') { inComment = false; sb.Append(c); } else sb.Append(' '); continue; }
            if (c == '"') inString = !inString;
            if (c == '#' && !inString && !(sb.Length > 0 && char.IsLetterOrDigit(sb[^1]))) { inComment = true; sb.Append(' '); continue; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static List<Tok> Tokenize(string text)
    {
        List<Tok> tokens = [];
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (string.CompareOrdinal(text, i, "|>", 0, 2) == 0) { tokens.Add(new Tok(T.Pipe, "|>", i, i + 2)); i += 2; continue; }
            if (string.CompareOrdinal(text, i, "..=", 0, 3) == 0) { tokens.Add(new Tok(T.DotsEq, "..=", i, i + 3)); i += 3; continue; }
            if (string.CompareOrdinal(text, i, "..", 0, 2) == 0) { tokens.Add(new Tok(T.Dots, "..", i, i + 2)); i += 2; continue; }
            string two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            T? twoKind = two switch { "->" => T.Arrow, "==" => T.EqEq, "!=" => T.NotEq, "<=" => T.Le, ">=" => T.Ge, "&&" => T.AndAnd, "||" => T.OrOr, _ => null };
            if (twoKind is { } tk2) { tokens.Add(new Tok(tk2, two, i, i + 2)); i += 2; continue; }
            bool signedNumber = c is '+' or '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]);
            T? oneKind = c switch { '<' => T.Lt, '>' => T.Gt, '!' => T.Bang, '*' => T.Star, '/' => T.Slash, '%' => T.Mod, '[' => T.LBracket, ']' => T.RBracket, ';' => T.Semi, '|' => T.Bar, '?' => T.Question, ':' => T.Colon, '+' when !signedNumber => T.Plus, '-' when !signedNumber => T.Minus, _ => null };
            if (oneKind is { } tk1) { tokens.Add(new Tok(tk1, c.ToString(), i, i + 1)); i++; continue; }
            if (c == '@')
            {
                int h = i + 1;
                while (h < text.Length && Uri.IsHexDigit(text[h])) h++;
                if (h > i + 1) { tokens.Add(new Tok(T.Hash, text[(i + 1)..h], i, h)); i = h; continue; }
            }

            if (c is '(' or ')' or ',' or '.' or '{' or '}' or '=')
            {
                T kind = c switch { '(' => T.LParen, ')' => T.RParen, ',' => T.Comma, '.' => T.Dot, '{' => T.LBrace, '=' => T.Eq, _ => T.RBrace };
                tokens.Add(new Tok(kind, c.ToString(), i, i + 1));
                i++;
                continue;
            }

            if (c == '"')
            {
                int close = text.IndexOf('"', i + 1);
                if (close < 0) throw new SyntaxError("A string is not closed", i, text.Length);
                tokens.Add(new Tok(T.Str, text[(i + 1)..close], i, close + 1));
                i = close + 1;
                continue;
            }

            if (char.IsDigit(c))
            {
                Match entry = EntryPattern().Match(text, i);
                if (entry.Success && entry.Index == i) { tokens.Add(new Tok(T.Entry, entry.Value, i, i + entry.Length)); i += entry.Length; continue; }
            }

            Match number = NumberPattern().Match(text, i);
            if (number.Success && number.Index == i)
            {
                int end = i + number.Length;
                Match unit = UnitPattern().Match(text, end);
                string? u = null;
                if (unit.Success && unit.Index == end) { u = unit.Value; end += unit.Length; }
                else if (end < text.Length && text[end] == '%' && !(end + 1 < text.Length && (char.IsLetterOrDigit(text[end + 1]) || text[end + 1] == '('))) { u = "%"; end++; }
                else if (end < text.Length && text[end] == '%') { u = "%"; end++; }
                tokens.Add(new Tok(T.Number, number.Value, i, end, u));
                i = end;
                continue;
            }

            Match ident = IdentPattern().Match(text, i);
            if (ident.Success && ident.Index == i) { tokens.Add(new Tok(T.Ident, ident.Value, i, i + ident.Length)); i += ident.Length; continue; }
            throw new SyntaxError($"Unexpected \"{c}\"", i, i + 1);
        }

        return tokens;
    }

    private sealed partial class ExprParser(string text, List<Tok> tokens)
    {
        private int p;

        private string Text => text;

        private List<Tok> Tokens => tokens;

        public Node Parse()
        {
            if (tokens.Count == 0) throw new SyntaxError("The expression is empty", 0, Math.Max(1, text.Length));
            Node n = Pipeline();
            if (p < tokens.Count) throw new SyntaxError("Unexpected input after the expression", tokens[p].Start, tokens[p].End);
            return n;
        }

        private Tok? Peek(int k = 0) => p + k < tokens.Count ? tokens[p + k] : null;

        private Tok Expect(T kind, string what)
        {
            Tok? k = Peek();
            if (k is null || k.Kind != kind) throw new SyntaxError($"Expected {what}", k?.Start ?? text.Length, k?.End ?? text.Length);
            p++;
            return k;
        }

        private QuantityNode Quantity()
        {
            Tok k = Expect(T.Number, "a number");
            Rat v = ParseDecimal(k.Text)!.Value;
            return new QuantityNode(v, k.Unit, k.Text + k.Unit, k.Start, k.End);
        }

        private Node Range()
        {
            QuantityNode from = Quantity();
            Tok? op = Peek();
            if (op is null || (op.Kind != T.Dots && op.Kind != T.DotsEq)) throw new SyntaxError("Expected \"..\" in a range", op?.Start ?? text.Length, op?.End ?? text.Length);
            p++;
            if (Peek() is null) Expect(T.Number, "a number");
            Node to = Binary(0);
            if (to is QuantityNode quantity) return new RangeNode(from, quantity, op.Kind == T.DotsEq, from.Start, quantity.End);
            return new RangeExprNode(from, to, op.Kind == T.DotsEq, from.Start, to.End);
        }

        private Node Arg()
        {
            Tok? k = Peek();
            if (k?.Kind == T.Ident && Peek(1)?.Kind == T.Eq)
            {
                p += 2;
                Node value = Arg();
                return new NamedNode(k.Text, value, k.Start, value.End);
            }

            if (k?.Kind == T.Str) { p++; return ArgTail(new StringNode(k.Text, k.Start, k.End)); }
            if (k?.Kind == T.Ident && k.Text is not ("true" or "false" or "bake") && Peek(1)?.Kind is not (T.Dot or T.LParen or T.LBrace)) { p++; return ArgTail(new WordNode(k.Text, k.Start, k.End)); }
            if (k?.Kind == T.Number)
            {
                Tok? after = Peek(1);
                return after is not null && after.Kind is T.Dots or T.DotsEq ? Range() : ArgTail(Quantity());
            }

            return Pipeline();
        }

        private CallNode Call()
        {
            Tok name = Expect(T.Ident, "an operator name");
            Expect(T.LParen, "\"(\"");
            List<Node> args = [];
            if (Peek()?.Kind != T.RParen)
            {
                while (true)
                {
                    args.Add(Arg());
                    if (Peek()?.Kind == T.Comma) { p++; continue; }
                    break;
                }
            }

            Tok close = Expect(T.RParen, "\")\"");
            return new CallNode(name.Text, args, name.Start, close.End, name.End);
        }

        private BakeNode Bake()
        {
            Tok word = Expect(T.Ident, "bake");
            Tok name = Expect(T.Ident, "a name for the stage");
            Tok? open = Peek();
            if (open?.Kind != T.LBrace) return new BakeNode(new BakeStage(name.Text, word.Start, name.End, false, false, null, null, null, null, null), word.Start, name.End);
            int bodyStart = open.End, depth = 1, q = p + 1;
            while (q < tokens.Count && depth > 0) { if (tokens[q].Kind == T.LBrace) depth++; else if (tokens[q].Kind == T.RBrace) depth--; q++; }
            if (depth != 0) throw new SyntaxError("The bake body is not closed", open.Start, text.Length);
            Tok close = tokens[q - 1];
            p = q;
            string body = text[bodyStart..close.Start];
            return new BakeNode(ParseBakeBody(name.Text, word.Start, close.End, body), word.Start, close.End);
        }

        private static BakeStage ParseBakeBody(string name, int start, int end, string body)
        {
            bool strict = false;
            string? inputHash = null, notesHash = null, span = null, file = null;
            StringBuilder notes = new();
            bool inNotes = false;
            foreach (string raw in body.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                Match m = BodyLinePattern().Match(line);
                string key = m.Success ? m.Groups[1].Value : string.Empty, rest = m.Success ? m.Groups[2].Value.Trim() : string.Empty;
                if (key == "strict") { strict = true; inNotes = false; }
                else if (key == "input-hash") { inputHash = rest.Trim('"'); inNotes = false; }
                else if (key == "notes-hash") { notesHash = rest.Trim('"'); inNotes = false; }
                else if (key == "span") { span = rest; inNotes = false; }
                else if (key == "file") { file = rest.Trim('"'); inNotes = false; }
                else if (key == "notes") { notes.Append(rest).Append(' '); inNotes = true; }
                else if (inNotes) notes.Append(line).Append(' ');
            }

            return new BakeStage(name, start, end, true, strict, inputHash, notesHash, span, notes.Length > 0 ? notes.ToString().Trim() : (file is null ? string.Empty : null), file);
        }

        private List<Node> ParseArgs(out Tok close)
        {
            Expect(T.LParen, "\"(\"");
            List<Node> args = [];
            if (Peek()?.Kind != T.RParen)
            {
                while (true)
                {
                    args.Add(Arg());
                    if (Peek()?.Kind == T.Comma) { p++; continue; }
                    break;
                }
            }

            close = Expect(T.RParen, "\")\"");
            return args;
        }

        private Node Postfix(Node node)
        {
            while (Peek()?.Kind == T.Dot && Peek(1)?.Kind == T.Ident)
            {
                p++;
                Tok name = Expect(T.Ident, "a method name");
                if (Peek()?.Kind == T.LParen)
                {
                    List<Node> args = ParseArgs(out Tok close);
                    node = new MethodNode(node, name.Text, args, node.Start, close.End);
                }
                else
                {
                    node = new FieldNode(node, name.Text, node.Start, name.End);
                }
            }

            return node;
        }

        private Node Atom()
        {
            Tok? k = Peek();
            if (k is null) throw new SyntaxError("The expression is empty", text.Length, text.Length);
            if (k.Kind == T.Entry)
            {
                Tok first = k, last = k;
                List<string> entries = [];
                while (Peek()?.Kind == T.Entry) { last = Peek()!; entries.Add(last.Text); p++; }
                return new LiteralNode(string.Join(' ', entries), first.Start, last.End);
            }

            if (k.Kind == T.LParen && Peek(1) is { Kind: T.Ident } hy && hy.Text.Contains('-', StringComparison.Ordinal) && Peek(2)?.Kind == T.RParen && HyphenMath(hy) is { } math) { p += 3; return math; }
            if (k.Kind == T.LParen) { p++; Node inner = Pipeline(); Expect(T.RParen, "\")\""); return Postfix(inner); }
            if (k.Kind == T.Number) return Quantity();
            if (k.Kind == T.Str) { p++; return new StringNode(k.Text, k.Start, k.End); }
            if (k.Kind == T.LBracket) return ListLiteral();
            if (k.Kind == T.Ident && k.Text is "true" or "false") { p++; return Postfix(new BoolNode(k.Text == "true", k.Start, k.End)); }
            if (k.Kind == T.Ident && k.Text == "bake") return BakeValue();
            if (k.Kind == T.Ident)
            {
                if (k.Text == "editable" && Peek(1)?.Kind == T.LBrace)
                {
                    int open = Peek(1)!.End, depth = 1, q = p + 2;
                    while (q < tokens.Count && depth > 0) { if (tokens[q].Kind == T.LBrace) depth++; else if (tokens[q].Kind == T.RBrace) depth--; q++; }
                    if (depth != 0) throw new SyntaxError("The editable body is not closed", k.Start, text.Length);
                    Tok close = tokens[q - 1];
                    p = q;
                    string body = Regex.Replace(text[open..close.Start], @"^\s*notes\b", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1)).Replace('\n', ' ');
                    return new EditableNode(string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), k.Start, close.End);
                }

                if (Peek(1)?.Kind == T.Dot)
                {
                    p += 2;
                    Tok port = Expect(T.Ident, "an output name");
                    if (Peek()?.Kind == T.LParen)
                    {
                        List<Node> args = ParseArgs(out Tok close);
                        return Postfix(new MethodNode(new WordNode(k.Text, k.Start, k.End), port.Text, args, k.Start, close.End));
                    }

                    return new RefNode(k.Text, port.Text, k.Start, port.End);
                }

                if (Peek(1)?.Kind == T.LParen) return Postfix(Call());
                p++;
                return Postfix(new WordNode(k.Text, k.Start, k.End));
            }

            throw new SyntaxError("Expected notes (block.port, a literal or a call)", k.Start, k.End);
        }

        private Node Pipeline() => PipeTail(TernaryTail(Binary(0)));

        /// <summary>The rest of a conditional expression after its condition: <c>? then : otherwise</c> (right associative; both branches are whole pipelines, so <c>a ? b : c |&gt; d</c> pipes the otherwise branch).</summary>
        private Node TernaryTail(Node cond)
        {
            if (Peek()?.Kind != T.Question) return cond;
            p++;
            Node then = Pipeline();
            Expect(T.Colon, "\":\" (a conditional is: condition ? value : other value)");
            Node otherwise = Pipeline();
            return new TernaryNode(cond, then, otherwise, cond.Start, otherwise.End);
        }

        private Node PipeTail(Node source)
        {
            List<Node> steps = [];
            while (Peek()?.Kind == T.Pipe)
            {
                p++;
                Tok? k = Peek();
                steps.Add(k?.Kind == T.Ident && k.Text == "bake" ? Bake() : Call());
            }

            return steps.Count == 0 ? source : new PipelineNode(source, steps, source.Start, steps[^1].End);
        }
    }

    // ---------- reading and writing notes

    private static readonly Regex Entry = EntryOf();

    /// <summary>Reads compact notes text into events (the routing fields and suffixes ride along). Entries that are not notes are reported.</summary>
    /// <param name="text">Space-separated note entries.</param>
    /// <param name="issues">Receives the problems.</param>
    /// <param name="start">Offset used for the problems.</param>
    /// <param name="end">End offset used for the problems.</param>
    /// <returns>The events.</returns>
    public static List<PipeNote> ReadNotes(string text, List<PipeIssue> issues, int start = 0, int end = 0)
    {
        List<PipeNote> events = [];
        foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = Entry.Match(word);
            if (!m.Success) { issues.Add(new PipeIssue("bad-notes", $"Not a note: {word}", start, end)); continue; }
            int bar = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (bar < 1) { issues.Add(new PipeIssue("bad-notes", $"Bar must be 1 or more: {word}", start, end)); continue; }
            Rat beat = ParseDecimal(m.Groups[2].Value)!.Value, len = ParseDecimal(m.Groups[3].Value)!.Value;
            long at = ((bar - 1) * Bar) + (long)RoundHalfEven(beat * Rat.Of(Beat));
            long length = Math.Max(1, (long)RoundHalfEven(len * Rat.Of(Beat)));
            events.Add(new PipeNote(at, length, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture), m.Groups[6].Value, m.Groups[7].Value));
        }

        return events;
    }

    private static long SpanOf(List<PipeNote> events) => Math.Max(1, (events.Count == 0 ? 0 : (events.Max(e => e.At + e.Len) + Bar - 1) / Bar)) * Bar;

    /// <summary>A value is never shorter than its notes: the span grows (in whole bars) to cover the furthest note end.</summary>
    private static PipeValue Grown(PipeValue v)
    {
        long end = v.Events.Count == 0 ? 0 : v.Events.Max(e => e.At + e.Len);
        v.Span = Math.Max(v.Span, (end + Bar - 1) / Bar * Bar);
        return v;
    }

    private static string FormatMicro(long micro)
    {
        long whole = micro / Beat, frac = micro % Beat;
        return frac == 0 ? whole.ToString(CultureInfo.InvariantCulture) : whole.ToString(CultureInfo.InvariantCulture) + "." + frac.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// <summary>Writes events as compact notes text: canonical order; a note that crosses a bar line becomes consecutive pieces.</summary>
    /// <param name="events">The events.</param>
    /// <returns>The compact text.</returns>
    public static string WriteNotes(IEnumerable<PipeNote> events)
    {
        List<string> output = [];
        int tie = 0;
        foreach (PipeNote e in events.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.e.Pitch).ThenBy(x => x.i).Select(x => x.e))
        {
            long start = e.At, left = e.Len;
            // a note over a bar line gets a tie id of its own (unless it has one), so that its pieces play as one note
            string tied = left > Bar - (start % Bar) && !HasTie(e) && TieBase + tie <= MaxTie ? ";tie=" + (TieBase + tie++).ToString(CultureInfo.InvariantCulture) : string.Empty;
            while (left > 0)
            {
                long take = Math.Min(left, Bar - (start % Bar));
                output.Add($"{(start / Bar) + 1}:{FormatMicro(start % Bar)}:{FormatMicro(take)}:{e.Pitch.ToString(CultureInfo.InvariantCulture)}:{e.Vel.ToString(CultureInfo.InvariantCulture)}{e.Tail}{e.Ext}{tied}");
                start += take;
                left -= take;
            }
        }

        return string.Join(' ', output);
    }

    private const int TieBase = 1_000_000, MaxTie = 2_000_000;

    private static bool HasTie(PipeNote e) => e.Ext.Contains(";tie=", StringComparison.Ordinal) || e.Tail.Count(c => c == ':') >= 4;

    private static readonly string[] ExtOrder = ["pan", "fine", "rel", "cut", "slide", "port", "bend", "tie"];

    private static readonly Dictionary<string, long> ExtDefault = new(StringComparer.Ordinal) { ["pan"] = 0, ["fine"] = 0, ["rel"] = -1, ["cut"] = 100, ["slide"] = 0, ["port"] = 0, ["bend"] = 0 };

    private static readonly Dictionary<string, (long Min, long Max)> SetRange = new(StringComparer.Ordinal)
    {
        ["pitch"] = (0, 127), ["velocity"] = (1, 127), ["pan"] = (-100, 100), ["fine"] = (-100, 100), ["rel"] = (-1, 5000), ["cut"] = (0, 100), ["slide"] = (0, 1), ["port"] = (0, 2000), ["bend"] = (-2400, 2400), ["channel"] = (-1, 15), ["bank"] = (0, 16383), ["program"] = (0, 127),
    };

    private static string SetTail(string tail, string key, long value)
    {
        // channel -1 takes the routing away (the note plays with the channel, bank and program of the block)
        if (key == "channel" && value < 0) return string.Empty;
        List<string> parts = [.. tail.Split(':', StringSplitOptions.RemoveEmptyEntries)];
        while (parts.Count < 3) parts.Add("0");
        parts[key switch { "channel" => 0, "bank" => 1, _ => 2 }] = value.ToString(CultureInfo.InvariantCulture);
        return ":" + string.Join(':', parts);
    }

    private static string SetExt(string ext, string key, long value)
    {
        List<(string Key, string Value)> pairs = [.. ext.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => { int eq = x.IndexOf('=', StringComparison.Ordinal); return (x[..eq], x[(eq + 1)..]); })];
        pairs.RemoveAll(x => x.Key == key);
        if (!ExtDefault.TryGetValue(key, out long normal) || normal != value) pairs.Add((key, value.ToString(CultureInfo.InvariantCulture)));
        // known keys in a fixed order, so that the same settings always give the same text
        return string.Concat(pairs.OrderBy(x => Array.IndexOf(ExtOrder, x.Key) is var at && at < 0 ? int.MaxValue : at).Select(x => ";" + x.Key + "=" + x.Value));
    }

    private static string Sha(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The signature of a set of notes: the hash of their canonical text and span.</summary>
    /// <param name="value">The notes.</param>
    /// <returns>The hash text.</returns>
    public static string NotesHash(PipeValue value) => Sha(WriteNotes(value.Events) + "\nspan=" + value.Span.ToString(CultureInfo.InvariantCulture));

    // ---------- the evaluator

    /// <summary>How a pipeline asks for the notes of another block.</summary>
    /// <param name="Value">The notes of the block, or null.</param>
    /// <param name="Recipe">The recipe hash of the block (what it is made of), or null.</param>
    /// <param name="Error">A message when the block cannot be used, or null.</param>
    public sealed record SourceInfo(PipeValue? Value, string? Recipe, string? Error);

    /// <summary>Everything an evaluation needs from the outside.</summary>
    public sealed class Context
    {
        /// <summary>Gets or sets the song tempo (beats per minute) as a decimal text.</summary>
        public string Tempo { get; set; } = "120";

        /// <summary>Gets or sets the length of a bar in quarter notes (4 in 4/4, 3 in 3/4 or 6/8).</summary>
        public decimal BeatsPerBar { get; set; } = 4m;

        /// <summary>Gets or sets the limit on the number of notes.</summary>
        public int Limit { get; set; } = MaxEvents;

        /// <summary>Gets or sets how the notes of another block are found (by block name), or null when there are none.</summary>
        public Func<string, SourceInfo>? Source { get; set; }

        /// <summary>Gets or sets how a file of the project is read (for a bake that keeps its notes in a file), or null.</summary>
        public Func<string, string?>? ReadFile { get; set; }

        /// <summary>Gets or sets the cancellation token.</summary>
        public CancellationToken Cancellation { get; set; }

        /// <summary>Gets or sets the wall-clock budget of one evaluation.</summary>
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Gets or sets the master-seed of the song (the seed of every stable namespace).</summary>
        public ulong MasterSeed { get; set; }

        /// <summary>Gets or sets the random-streams declared in the clip: name to seed expression text.</summary>
        public Dictionary<string, string>? Streams { get; set; }

        /// <summary>Gets or sets the name of a bake stage at which the evaluation stops (to bake it): its data is ignored and the value up to it is kept in <see cref="StoppedValue"/>.</summary>
        public string? StopAt { get; set; }

        /// <summary>Gets or sets the value up to the stage named in <see cref="StopAt"/>.</summary>
        public PipeValue? StoppedValue { get; set; }

        /// <summary>Gets or sets the recipe hash of that stage (what it is made from).</summary>
        public string? StoppedInputHash { get; set; }

        /// <summary>Gets or sets the helpers, type aliases and constants of the file, or null.</summary>
        public HelperTable? Helpers { get; set; }

        /// <summary>Gets or sets the capability policy of the song (docs/language/16); null allows everything.</summary>
        public CapabilityPolicy? Capabilities { get; set; }

        /// <summary>Gets or sets how environment(name) reads a value, or null for the process environment. Only reachable when the capability environment is on.</summary>
        public Func<string, string?>? Environment { get; set; }

        /// <summary>Gets or sets how the resolved value of another block's property is read (block, property), or null.</summary>
        public Func<string, string, PropertyRead>? Property { get; set; }

        /// <summary>Gets or sets the limits of helper code (loops, while, stack depth, fuel).</summary>
        public LangLimits Limits { get; set; } = new();

        /// <summary>Gets or sets the clock the volatile built-ins read (currentTime, currentDate), or null for the system clock.</summary>
        public Func<DateTime>? Clock { get; set; }

        /// <summary>Gets or sets the source of osRandom() (a non-negative whole number), or null for the operating system.</summary>
        public Func<long>? OsRandom { get; set; }

        /// <summary>Gets the issues about bake stages that were found while evaluating (stale, dirty, not baked), by stage.</summary>
        public List<PipeIssue> StageIssues { get; } = [];
    }

    private static string Nearest(string word)
    {
        string? best = null;
        int bestDistance = 3;
        foreach (string name in OperatorNames)
        {
            int d = Distance(word, name);
            if (d < bestDistance) { best = name; bestDistance = d; }
        }

        return best ?? string.Empty;
    }

    internal static int Distance(string a, string b)
    {
        int[] row = [.. Enumerable.Range(0, b.Length + 1)];
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int keep = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = keep;
            }
        }

        return row[b.Length];
    }

    /// <summary>An upper bound of the number of notes an expression can make, worked out without running it (a repeat multiplies, a concat adds): the editors warn before it runs when it is over the limit.</summary>
    /// <param name="text">The expression text.</param>
    /// <param name="context">What the estimate can ask for (the sources).</param>
    /// <returns>The estimate, or zero when the text does not parse.</returns>
    public static BigInteger Estimate(string text, Context context)
    {
        try
        {
            string clean = StripComments(text);
            return EstimateNode(new ExprParser(clean, Tokenize(clean)).Parse(), context);
        }
        catch (SyntaxError)
        {
            return BigInteger.Zero;
        }
    }

    private static BigInteger EstimateNode(Node node, Context context)
    {
        switch (node)
        {
            case LiteralNode l: return l.Entries.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            case EditableNode e: return e.Entries.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            case RefNode r: return context.Source?.Invoke(r.Block).Value?.Events.Count ?? 0;
            case CallNode { Name: "euclid" or "walk" or "chords" or "scale" }: return 256;
            case PipelineNode p:
                {
                    BigInteger total = EstimateNode(p.Source, context);
                    foreach (Node step in p.Steps)
                    {
                        if (step is BakeNode { Stage: { NotesText: { } stored } }) { total = stored.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; continue; }
                        if (step is not CallNode c) continue;
                        switch (c.Name)
                        {
                            case "repeat" when c.Args.Count == 1 && c.Args[0] is QuantityNode { Unit: null } q && q.Value.IsInt && q.Value.N > 0: total *= q.Value.N; break;
                            case "arp": total *= 64; break;
                            case "concat" or "merge" when c.Args.Count == 1: total += EstimateNode(c.Args[0], context); break;
                            case "replace" when c.Args.Count == 2: total += EstimateNode(c.Args[1], context); break;
                        }
                    }

                    return total;
                }

            default: return BigInteger.Zero;
        }
    }

    /// <summary>The hand-written <c>editable { }</c> pieces of an expression, in the order they stand: where each one starts and ends in the text and its note entries.</summary>
    /// <param name="text">The expression text.</param>
    /// <returns>The pieces.</returns>
    public static IReadOnlyList<(int Start, int End, string Entries)> Editables(string text)
    {
        List<(int, int, string)> found = [];
        try
        {
            string clean = StripComments(text);
            Collect(new ExprParser(clean, Tokenize(clean)).Parse(), found);
        }
        catch (SyntaxError)
        {
            return [];
        }

        return [.. found.OrderBy(x => x.Item1)];
    }

    private static void Collect(Node node, List<(int, int, string)> found)
    {
        switch (node)
        {
            case EditableNode e: found.Add((e.Start, e.End, e.Entries)); break;
            case PipelineNode p: Collect(p.Source, found); foreach (Node s in p.Steps) Collect(s, found); break;
            case CallNode c: foreach (Node a in c.Args) Collect(a, found); break;
            case NamedNode n: Collect(n.Value, found); break;
        }
    }

    /// <summary>Where a seed is written in an expression: the whole seed expression (stable("a").fork("b"), a number...) and the stable name at its root, if it has one.</summary>
    /// <param name="Start">Offset of the first character.</param>
    /// <param name="End">Offset after the last character.</param>
    /// <param name="Text">The seed expression as written.</param>
    /// <param name="Stable">The stable namespace name at the root, or null (explicit seeds, random-seed()).</param>
    public sealed record SeedSite(int Start, int End, string Text, string? Stable);

    /// <summary>The seeds of an expression, in the order they stand (every <c>seed=...</c> argument).</summary>
    /// <param name="text">The expression text.</param>
    /// <returns>The sites.</returns>
    public static IReadOnlyList<SeedSite> SeedSites(string text)
    {
        List<SeedSite> found = [];
        string clean = StripComments(text);
        try
        {
            VisitSeeds(new ExprParser(clean, Tokenize(clean)).Parse(), clean, found);
        }
        catch (SyntaxError)
        {
            return [];
        }

        return [.. found.OrderBy(s => s.Start)];
    }

    /// <summary>The stable name at the root of a seed expression written as text (a random-stream line), or null.</summary>
    /// <param name="seedText">The seed expression text.</param>
    /// <returns>The name, or null.</returns>
    public static string? StableNameOf(string seedText)
    {
        string clean = StripComments(seedText);
        try
        {
            return StableRoot(new ExprParser(clean, Tokenize(clean)).Parse());
        }
        catch (SyntaxError)
        {
            return null;
        }
    }

    private static string? StableRoot(Node node) => node switch
    {
        CallNode { Name: "stable", Args: [StringNode s] } => s.Value,
        MethodNode m => StableRoot(m.Target),
        _ => null,
    };

    private static void VisitSeeds(Node node, string clean, List<SeedSite> found)
    {
        switch (node)
        {
            case NamedNode { Name: "seed" } n: found.Add(new SeedSite(n.Value.Start, n.Value.End, clean[n.Value.Start..n.Value.End], StableRoot(n.Value))); break;
            case NamedNode n: VisitSeeds(n.Value, clean, found); break;
            case PipelineNode p: VisitSeeds(p.Source, clean, found); foreach (Node s in p.Steps) VisitSeeds(s, clean, found); break;
            case CallNode c: foreach (Node a in c.Args) VisitSeeds(a, clean, found); break;
            case MethodNode m: VisitSeeds(m.Target, clean, found); foreach (Node a in m.Args) VisitSeeds(a, clean, found); break;
        }
    }

    /// <summary>Parses an expression and reports the syntax problems only (no evaluation).</summary>
    /// <param name="text">The expression text (comments allowed).</param>
    /// <param name="issues">Receives the problems.</param>
    /// <returns>The bake stages found (in order), empty on a syntax error.</returns>
    public static IReadOnlyList<BakeStage> Stages(string text, List<PipeIssue> issues)
    {
        try
        {
            Node ast = new ExprParser(StripComments(text), Tokenize(StripComments(text))).Parse();
            return ast is PipelineNode pipe ? [.. pipe.Steps.OfType<BakeNode>().Select(b => b.Stage)] : [];
        }
        catch (SyntaxError e)
        {
            issues.Add(new PipeIssue("syntax", e.Message, e.Start, e.End));
            return [];
        }
    }

    /// <summary>Evaluates an expression.</summary>
    /// <param name="text">The expression text (comments allowed; offsets of the issues refer to this text).</param>
    /// <param name="context">What the evaluation can ask for.</param>
    /// <returns>The notes, or the problems.</returns>
    public static PipeResult Evaluate(string text, Context context)
    {
        using IDisposable meterScope = UseMeter(context.BeatsPerBar);
        List<PipeIssue> errors = [], warnings = [];
        string clean = StripComments(text);
        Node ast;
        try
        {
            ast = Typed(new ExprParser(clean, Tokenize(clean)).Parse());
        }
        catch (SyntaxError e)
        {
            errors.Add(new PipeIssue("syntax", e.Message, e.Start, e.End));
            return new PipeResult { Errors = errors, Warnings = warnings };
        }

        Rat tempo = ParseDecimal(context.Tempo) ?? Rat.Of(120);
        if (NeedsLang(ast, context))
        {
            Interp lang = new(context, tempo, errors, warnings, clean);
            PipeValue? langValue = lang.Run(ast, out string? langRecipe);
            lang.Legacy.Finish();
            bool good = errors.Count == 0 && langValue is not null;
            return new PipeResult
            {
                Notes = good ? WriteNotes(langValue!.Events) : null,
                Span = good ? langValue!.Span : 0,
                Warnings = warnings,
                Errors = errors,
                Recipe = langRecipe,
                Value = good ? langValue : null,
                Seeds = lang.Legacy.Uses,
                Streams = lang.Legacy.StreamUses,
                Dynamic = lang.Dynamic,
                BakedDynamic = lang.BakedDynamic,
                DynamicInputs = lang.DynamicInputs,
                Fuel = lang.Fuel,
            };
        }

        Evaluation run = new(context, tempo, errors, warnings, clean);
        PipeValue? value = run.Eval(ast, out string? recipe);
        run.Finish();
        if (errors.Count > 0 || value is null) return new PipeResult { Errors = errors, Warnings = warnings, Recipe = recipe, Seeds = run.Uses, Streams = run.StreamUses };
        return new PipeResult { Notes = WriteNotes(value.Events), Span = value.Span, Warnings = warnings, Recipe = recipe, Value = value, Seeds = run.Uses, Streams = run.StreamUses };
    }

    private sealed class Evaluation(Context context, Rat tempo, List<PipeIssue> errors, List<PipeIssue> warnings, string text)
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private static string TempoOf(string? acc, Rat tempo) => acc is not null && SecondsPattern().IsMatch(acc) ? "|tempo=" + tempo : string.Empty;

        // what a recipe depends on besides its text: the tempo (when seconds are used) and the master-seed (when a stable seed is used, directly or through a stream)
        internal string Extra(string? acc) => TempoOf(acc, tempo) + (acc is not null && StablePattern().IsMatch(acc) ? "|master=" + context.MasterSeed.ToString(CultureInfo.InvariantCulture) : string.Empty);

        internal void Problem(List<PipeIssue> list, string id, string message, Node? node, string? suggestion = null) =>
            list.Add(new PipeIssue(id, message, node?.Start ?? 0, node?.End ?? text.Length, suggestion));

        public PipeValue? Eval(Node node, out string? recipe)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            recipe = null;
            PipeValue? value = null;
            switch (node)
            {
                case RefNode r:
                    {
                        SourceInfo? info = context.Source?.Invoke(r.Block);
                        if (info is null) { Problem(errors, "unknown-reference", $"There is no block \"{r.Block}\"", node); return null; }
                        if (info.Error is not null) { Problem(errors, info.Error.StartsWith("cycle", StringComparison.Ordinal) ? "reference-cycle" : "unknown-reference", info.Error.StartsWith("cycle", StringComparison.Ordinal) ? $"\"{r.Block}\" reads itself through its notes" : info.Error, node); return null; }
                        if (r.Port != "notes") { Problem(errors, "bad-argument", $"\"{r.Port}\" is not a notes output; use {r.Block}.notes", node); return null; }
                        recipe = $"ref:{r.Block}.{r.Port}@{info.Recipe}";
                        value = info.Value?.Copy();
                        break;
                    }

                case LiteralNode l:
                    {
                        List<PipeNote> events = ReadNotes(l.Entries, errors, l.Start, l.End);
                        recipe = "lit:" + WriteNotes(events);
                        value = new PipeValue { Events = events, Span = SpanOf(events) };
                        break;
                    }

                case EditableNode e:
                    {
                        List<PipeNote> events = ReadNotes(e.Entries, errors, e.Start, e.End);
                        recipe = "lit:" + WriteNotes(events);
                        value = new PipeValue { Events = events, Span = SpanOf(events) };
                        break;
                    }

                case ValueNode injected:
                    value = injected.Value.Copy();
                    recipe = injected.Recipe;
                    break;
                case PipelineNode p:
                    value = Pipeline(p, out recipe);
                    break;
                case CallNode { Name: "euclid" or "walk" or "chords" or "scale" } g:
                    value = Generate(g);
                    recipe = value is null ? null : "gen:" + Normalize(g);
                    break;
                case CallNode c:
                    Problem(errors, "bad-argument", $"{c.Name}(...) needs notes to work on: write notes |> {c.Name}(...)", node);
                    break;
                default:
                    Problem(errors, "bad-argument", "Notes are expected here", node);
                    break;
            }

            if (value is not null && value.Events.Count > context.Limit) { Problem(errors, "cap-events", $"More than {context.Limit} notes", node); return null; }
            return value;
        }

        private PipeValue? Pipeline(PipelineNode p, out string? recipe)
        {
            PipeValue? value = Eval(p.Source, out string? acc);
            recipe = null;
            if (value is null) return null;
            // one pass over the steps keeps the recipe (what the notes are made of) and checks every stage that has notes: a stage is stale when its input-hash is not the hash of the recipe before it,
            // and edited by hand when its notes do not match its notes-hash. The last stage with notes is authoritative: the value starts there and the steps before it are not run.
            string accNow = acc ?? string.Empty;
            PipeValue? baseValue = null;
            int baseIndex = 0, stopIndex = p.Steps.Count;
            for (int i = 0; i < p.Steps.Count; i++)
            {
                if (p.Steps[i] is BakeNode b)
                {
                    BakeStage stage = b.Stage;
                    if (context.StopAt is not null && stage.Name == context.StopAt)
                    {
                        context.StoppedInputHash = Sha("ample-note-bake/1\n" + accNow + Extra(accNow));
                        stopIndex = i;
                        break;
                    }

                    if (!stage.HasBody || (stage.NotesText is null && stage.File is null))
                    {
                        Problem(warnings, "unbaked", $"The bake stage \"{stage.Name}\" has no notes yet", b);
                        continue;
                    }

                    PipeValue? stored = StoredNotes(b);
                    if (stored is null) continue;
                    string notesHash = NotesHash(stored);
                    string expected = Sha("ample-note-bake/1\n" + accNow + Extra(accNow));
                    if (stage.NotesHash is not null && stage.NotesHash != notesHash)
                        context.StageIssues.Add(new PipeIssue("dirty-bake", $"The notes of the bake stage \"{stage.Name}\" were changed by hand ({(stage.Strict ? "strict" : "warning")}): re-bake, or accept the change", b.Start, b.End) { Strict = stage.Strict });
                    if (stage.InputHash is not null && stage.InputHash != expected)
                        context.StageIssues.Add(new PipeIssue("stale-bake", $"The bake stage \"{stage.Name}\" is stale: what it is made of has changed since it was baked", b.Start, b.End));
                    baseValue = stored;
                    baseIndex = i + 1;
                    accNow = "baked:" + notesHash;
                }
                else if (p.Steps[i] is CallNode c)
                {
                    accNow += "|>" + Normalize(c);
                }
            }

            if (baseValue is not null) value = baseValue;
            for (int i = baseIndex; i < stopIndex; i++)
            {
                if (p.Steps[i] is BakeNode) continue;
                value = Step(value!, (CallNode)p.Steps[i]);
                if (value is null) return null;
            }

            if (context.StopAt is not null && stopIndex < p.Steps.Count) context.StoppedValue = value;
            recipe = Sha("ample-note-bake/1\n" + accNow + Extra(accNow));
            return value;
        }

        internal PipeValue? StoredNotes(BakeNode b)
        {
            BakeStage s = b.Stage;
            string? notes = s.NotesText;
            if (notes is null && s.File is not null)
            {
                notes = context.ReadFile?.Invoke(s.File);
                if (notes is null) { Problem(errors, "missing-file", $"The file \"{s.File}\" of the bake stage \"{s.Name}\" cannot be read", b); return null; }
            }

            List<PipeIssue> bad = [];
            List<PipeNote> events = ReadNotes(notes ?? string.Empty, bad, b.Start, b.End);
            if (bad.Count > 0) { errors.AddRange(bad); return null; }
            long span = SpanOf(events);
            if (s.Span is not null)
            {
                Match m = SpanPattern().Match(s.Span);
                Rat? q = m.Success ? ParseDecimal(m.Groups[1].Value) : null;
                long? micro = q is null ? null : ToMicro(q.Value, m.Groups[2].Value.Length == 0 ? "bars" : m.Groups[2].Value, tempo);
                if (micro is not null) span = micro.Value;
            }

            return new PipeValue { Events = events, Span = span };
        }

        internal string Normalize(Node node) => node switch
        {
            QuantityNode q => q.Value + (q.Unit ?? string.Empty),
            RangeNode r => Normalize(r.From) + (r.Inclusive ? "..=" : "..") + Normalize(r.To),
            CallNode c => c.Name + (OperatorNames.Contains(c.Name) ? "@" + (OperatorVersions.GetValueOrDefault(c.Name) ?? "?") : string.Empty) + "(" + string.Join(',', c.Args.Select(Normalize)) + ")",
            LiteralNode l => "lit:" + WriteNotes(ReadNotes(l.Entries, [])),
            EditableNode e => "lit:" + WriteNotes(ReadNotes(e.Entries, [])),
            RefNode rf => "ref:" + rf.Block + "." + rf.Port + "@" + (context.Source?.Invoke(rf.Block)?.Recipe ?? "?"),
            PipelineNode p => "(" + Normalize(p.Source) + string.Concat(p.Steps.Select(s => "|>" + Normalize(s))) + ")",
            StringNode sn => "\"" + sn.Value + "\"",
            ValueNode vn => "val:" + vn.Recipe,
            WordNode w => w.Text,
            NamedNode nn => nn.Name + "=" + Normalize(nn.Value),
            MethodNode m => (m.Target is WordNode sw && context.Streams is not null && context.Streams.TryGetValue(sw.Text, out string? definition) ? "stream:" + sw.Text + "=" + definition.Trim() : Normalize(m.Target)) + "." + m.Name + "(" + string.Join(',', m.Args.Select(Normalize)) + ")",
            _ => string.Empty,
        };

        private long? Micro(Rat q, string? unit, Node node)
        {
            long? value = ToMicro(q, unit, tempo);
            if (value is null) { Problem(errors, "wrong-unit", $"Unknown unit \"{unit}\"", node); return null; }
            return value;
        }

        private (long From, long To)? Window(Node node, string how)
        {
            if (node is not RangeNode r) { Problem(errors, "bad-argument", "A range like 1..5 is expected", node); return null; }
            long? Coord(QuantityNode q, bool end)
            {
                Rat value = q.Value;
                if (how == "bars")
                {
                    if (q.Unit is not null and not ("bars" or "bar")) { Problem(errors, "wrong-unit", "Bar positions have no unit", q); return null; }
                    if (!value.IsInt) Problem(warnings, "fractional-bar", "A fractional bar position", q);
                    if (end && r.Inclusive && value.IsInt) value = Rat.Of(value.N + 1);
                    return Micro(value - Rat.Of(1), "bars", q);
                }

                if (how == "beats")
                {
                    if (q.Unit is not null and not ("beats" or "beat")) { Problem(errors, "wrong-unit", "Beat positions have no unit", q); return null; }
                    if (end && r.Inclusive && value.IsInt) value = Rat.Of(value.N + 1);
                    return Micro(value - Rat.Of(1), "beats", q);
                }

                if (q.Unit is null) { Problem(errors, "missing-unit", "Seconds or milliseconds need a unit (s or ms)", q); return null; }
                return Micro(value, q.Unit, q);
            }

            long? from = Coord(r.From, false), to = Coord(r.To, true);
            if (from is null || to is null) return null;
            if (to < from) { Problem(errors, "range-reversed", "The end of the range is before its start", r); return null; }
            return (from.Value, to.Value);
        }

        private (long From, long To)? RangeArg(Node arg)
        {
            if (arg is RangeNode) return Window(arg, "bars");
            if (arg is CallNode { Name: "bars" or "beats" or "time", Args.Count: 1 } c) return Window(c.Args[0], c.Name);
            Problem(errors, "bad-argument", "A range like bars(1..5) is expected", arg);
            return null;
        }

        private Dictionary<string, Node>? ArgMap(CallNode node, string[] names, int required)
        {
            Dictionary<string, Node> map = [];
            int position = 0;
            foreach (Node a in node.Args)
            {
                string? key;
                Node value;
                if (a is NamedNode n)
                {
                    if (!names.Contains(n.Name)) { Problem(errors, "bad-argument", $"{node.Name} has no argument \"{n.Name}\" (it has: {string.Join(", ", names)})", n); return null; }
                    key = n.Name;
                    value = n.Value;
                }
                else
                {
                    if (position >= names.Length) { Problem(errors, "bad-argument", $"{node.Name} takes at most {names.Length} arguments", a); return null; }
                    key = names[position++];
                    value = a;
                }

                if (!map.TryAdd(key, value)) { Problem(errors, "bad-argument", $"\"{key}\" is given twice", a); return null; }
            }

            for (int i = 0; i < required; i++)
            {
                if (!map.ContainsKey(names[i])) { Problem(errors, "bad-argument", $"{node.Name} needs \"{names[i]}\"", node); return null; }
            }

            return map;
        }

        private bool Int(Node n, string what, long min, long max, out long value)
        {
            value = 0;
            if (n is not QuantityNode { Unit: null } q || !q.Value.IsInt || q.Value.N < min || q.Value.N > max) { Problem(errors, "bad-argument", $"{what} must be a whole number from {min} to {max}", n); return false; }
            value = (long)q.Value.N;
            return true;
        }

        private bool Length(Node n, out long micro)
        {
            micro = 0;
            if (n is not QuantityNode q) { Problem(errors, "bad-argument", "A length like 0.5beat or 20ms is expected", n); return false; }
            if (q.Unit is null) { Problem(errors, "missing-unit", "A length needs a unit (bars, beats, s, ms or ticks)", q); return false; }
            long? m = Micro(q.Value, q.Unit, q);
            if (m is null) return false;
            micro = m.Value;
            return true;
        }

        private PipeValue? Euclid(CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["hits", "steps", "pitch", "step", "velocity", "rotate"], 2);
            if (m is null) return null;
            if (!Int(m["hits"], "hits", 0, 256, out long hits) || !Int(m["steps"], "steps", 1, 256, out long steps)) return null;
            if (hits > steps) { Problem(errors, "bad-argument", "hits cannot be more than steps", node); return null; }
            long pitch = 36, velocity = 100, rotate = 0, step = NotePipeline.Beat;
            if (m.TryGetValue("pitch", out Node? pn) && !Int(pn, "pitch", 0, 127, out pitch)) return null;
            if (m.TryGetValue("velocity", out Node? vn) && !Int(vn, "velocity", 1, 127, out velocity)) return null;
            if (m.TryGetValue("rotate", out Node? rn) && !Int(rn, "rotate", -256, 256, out rotate)) return null;
            if (m.TryGetValue("step", out Node? sn) && (!Length(sn, out step) || step <= 0)) { if (step <= 0) Problem(errors, "bad-argument", "step must be longer than zero", sn); return null; }
            List<PipeNote> events = [];
            for (long i = 0; i < steps; i++)
            {
                long j = (((i + rotate) % steps) + steps) % steps;
                if ((j * hits) % steps < hits) events.Add(new PipeNote(i * step, step, (int)pitch, (int)velocity, string.Empty, string.Empty));
            }

            return new PipeValue { Events = events, Span = Math.Max(1, ((steps * step) + NotePipeline.Bar - 1) / NotePipeline.Bar) * NotePipeline.Bar };
        }

        private sealed class SeedUse(string op, string seed, ulong key)
        {
            public string Operator { get; } = op;

            public string Seed { get; } = seed;

            public ulong Key { get; } = key;

            public long Draws { get; set; }

            public int Start { get; set; }

            public int End { get; set; }
        }

        private sealed class StreamState(string name, string seedText, Node seedNode)
        {
            public string Name { get; } = name;

            public string SeedText { get; } = seedText;

            public Node SeedNode { get; } = seedNode;

            public SeedUse? Use { get; set; }

            public long Cursor { get; set; }
        }

        private readonly List<SeedUse> uses = [];
        private Dictionary<string, StreamState>? streamStates;

        public List<SeedUseInfo> Uses => [.. uses.Select(u => new SeedUseInfo(u.Operator, u.Seed, NoteRandom.Hex(u.Key), u.Draws))];

        public List<StreamUseInfo> StreamUses => [.. (streamStates ?? new Dictionary<string, StreamState>()).Values.Select(s => new StreamUseInfo(s.Name, s.SeedText, s.Use is null ? string.Empty : NoteRandom.Hex(s.Use.Key), s.Cursor))];

        /// <summary>Notes that are not wrong but worth a look: the same seed used twice by the same operator (identical, correlated results), a declared stream nobody reads.</summary>
        public void Finish()
        {
            _ = Streams();   // streams that were declared but never read are noted too
            foreach (IGrouping<(string, ulong), SeedUse> group in uses.Where(u => u.Operator is "humanize" or "randomize" or "thin" or "scramble").GroupBy(u => (u.Operator, u.Key)).Where(g => g.Count() > 1))
            {
                SeedUse second = group.Skip(1).First();
                warnings.Add(new PipeIssue("same-seed", $"{second.Operator} uses the same seed as an earlier {second.Operator} in this clip, so their random results are identical", second.Start, second.End));
            }

            foreach (StreamState s in (streamStates ?? new Dictionary<string, StreamState>()).Values.Where(s => errors.Count == 0 && s.Cursor == 0 && s.Use is null))
                warnings.Add(new PipeIssue("unused-stream", $"The random-stream \"{s.Name}\" is declared but never used", 0, 1));
        }

        private Dictionary<string, StreamState> Streams()
        {
            if (streamStates is not null) return streamStates;
            streamStates = new Dictionary<string, StreamState>(StringComparer.Ordinal);
            foreach ((string name, string seedText) in context.Streams ?? new Dictionary<string, string>())
            {
                try
                {
                    string clean = StripComments(seedText);
                    streamStates[name] = new StreamState(name, seedText.Trim(), new ExprParser(clean, Tokenize(clean)).Parse());
                }
                catch (SyntaxError e)
                {
                    errors.Add(new PipeIssue("stream-error", $"random-stream {name}: {e.Message}", 0, 1));
                }
            }

            return streamStates;
        }

        internal static string SeedText(Node n) => n switch
        {
            QuantityNode q => q.Value.ToString(),
            CallNode { Name: "stable", Args: [StringNode s] } => "stable(\"" + s.Value + "\")",
            CallNode c => c.Name + "()",
            MethodNode m => SeedText(m.Target) + "." + m.Name + "(" + string.Join(", ", m.Args.Select(a => a is StringNode sa ? "\"" + sa.Value + "\"" : a is QuantityNode qa ? qa.Value.ToString() : "?")) + ")",
            WordNode w => w.Text,
            _ => "?",
        };

        internal bool TryKey(Node node, out ulong key, out string seedText)
        {
            key = 0;
            seedText = SeedText(node);
            switch (node)
            {
                case QuantityNode { Unit: null } q when q.Value.IsInt && q.Value.N.Sign >= 0 && q.Value.N <= ulong.MaxValue:
                    key = NoteRandom.Explicit(q.Value.N);
                    return true;
                case CallNode { Name: "stable" } c:
                    if (c.Args.Count == 1 && c.Args[0] is StringNode { Value.Length: > 0 } s) { key = NoteRandom.Stable(context.MasterSeed, s.Value); return true; }
                    Problem(errors, "seed-error", "stable(...) takes one name in quotes, like stable(\"verse\")", node);
                    return false;
                case CallNode { Name: "random-seed" }:
                    Problem(errors, "unresolved-random-seed", "randomSeed() is not a number yet: pick one (the fix writes it into the song) so that the result can be reproduced", node);
                    return false;
                case MethodNode { Name: "fork" } m:
                    {
                        if (m.Args.Count != 1) { Problem(errors, "seed-error", "fork(...) takes one label, like .fork(\"chords\") or .fork(3)", node); return false; }
                        string? label = m.Args[0] switch
                        {
                            StringNode { Value.Length: > 0 } ls => ls.Value,
                            QuantityNode { Unit: null } ln when ln.Value.IsInt && ln.Value.N.Sign >= 0 => "#" + ln.Value.N.ToString(CultureInfo.InvariantCulture),
                            _ => null,
                        };
                        if (label is null) { Problem(errors, "seed-error", "A fork label is a name in quotes or a whole number", m.Args[0]); return false; }
                        if (!TryKey(m.Target, out ulong parent, out _)) return false;
                        key = NoteRandom.Fork(parent, label);
                        return true;
                    }

                case WordNode w when Streams().ContainsKey(w.Text):
                    Problem(errors, "stream-error", $"\"{w.Text}\" is a random-stream, not a seed: read it with {w.Text}.next()", node);
                    return false;
                default:
                    Problem(errors, "seed-error", "A seed is a whole number, stable(\"name\"), or one of those followed by .fork(\"label\")", node);
                    return false;
            }
        }

        private bool SeedArg(Dictionary<string, Node> m, CallNode node, string op, out SeedUse use)
        {
            use = null!;
            if (!m.TryGetValue("seed", out Node? seedNode)) { Problem(errors, "missing-seed", $"{op} needs a seed, for example seed=stable(\"verse\") or seed=7: the result must be the same every time", node); return false; }
            if (!TryKey(seedNode, out ulong key, out string seedText)) return false;
            use = new SeedUse(op, seedText, key) { Start = seedNode.Start, End = seedNode.End };
            uses.Add(use);
            return true;
        }

        private static ulong DrawBits(SeedUse use, string purpose, string address)
        {
            use.Draws++;
            return NoteRandom.Draw(use.Key, purpose, address);
        }

        private static string[] Addresses(List<PipeNote> events)
        {
            string[] result = new string[events.Count];
            Dictionary<(long, int), int> seen = [];
            foreach (int i in Enumerable.Range(0, events.Count).OrderBy(i => events[i].At).ThenBy(i => events[i].Pitch).ThenBy(i => i))
            {
                (long, int) k = (events[i].At, events[i].Pitch);
                seen.TryGetValue(k, out int dup);
                seen[k] = dup + 1;
                result[i] = $"n:{events[i].At}:{events[i].Pitch}:{dup}";
            }

            return result;
        }

        private CallNode? ResolveArgs(CallNode node)
        {
            if (node.Args.Count == 0) return node;
            List<Node> args = [];
            foreach (Node a in node.Args)
            {
                Node? r = Scalar(a);
                if (r is null) return null;
                args.Add(r);
            }

            return node with { Args = args };
        }

        private Node? Scalar(Node a)
        {
            switch (a)
            {
                case NamedNode { Name: "seed" }:
                    return a;
                case NamedNode n:
                    {
                        Node? v = Scalar(n.Value);
                        return v is null ? null : n with { Value = v };
                    }

                case CallNode { Name: "random" or "choose" or "chance" } c:
                    return ValueCall(c);
                case MethodNode { Target: WordNode w, Name: "next" } m:
                    return StreamNext(w, m);
                case MethodNode { Name: "next" }:
                    Problem(errors, "stream-error", "next() belongs to a random-stream: declare one in the clip (random-stream name <seed>) and call name.next(); a plain seed has no cursor", a);
                    return null;
                default:
                    return a;
            }
        }

        internal Node? ValueCall(CallNode c)
        {
            List<Node> positional = [];
            Dictionary<string, Node> named = [];
            foreach (Node a in c.Args)
            {
                if (a is NamedNode n)
                {
                    if (n.Name is not ("seed" or "round") || !named.TryAdd(n.Name, n.Value)) { Problem(errors, "bad-argument", $"{c.Name} takes seed= and round= as named arguments, each once", n); return null; }
                }
                else positional.Add(a);
            }

            if (!SeedArg(named, c, c.Name, out SeedUse use)) return null;
            long round = 0;
            if (named.TryGetValue("round", out Node? rn) && !Int(rn, "round", 0, 9_007_199_254_740_991, out round)) return null;
            string address = round.ToString(CultureInfo.InvariantCulture);
            switch (c.Name)
            {
                case "random":
                    if (positional.Count != 1) { Problem(errors, "bad-argument", "random takes one range, like random(0..100, seed=...)", c); return null; }
                    return InRange(positional[0], DrawBits(use, "random", address), c);
                case "choose":
                    if (positional.Count < 1) { Problem(errors, "bad-argument", "choose needs the values to choose from", c); return null; }
                    return positional[(int)NoteRandom.Below(DrawBits(use, "choose", address), positional.Count)];
                default:
                    {
                        if (positional.Count != 1 || positional[0] is not QuantityNode { Unit: null } p || p.Value.N.Sign < 0 || p.Value.N > p.Value.D) { Problem(errors, "bad-argument", "chance takes a probability from 0 to 1, like chance(0.25, seed=...)", c); return null; }
                        bool hit = new BigInteger(DrawBits(use, "chance", address)) * p.Value.D < p.Value.N * (BigInteger.One << 64);
                        return new QuantityNode(Rat.Of(hit ? 1 : 0), null, hit ? "1" : "0", c.Start, c.End);
                    }
            }
        }

        private QuantityNode? InRange(Node? range, ulong bits, Node at)
        {
            if (range is null)
            {
                Rat unit = Rat.Of(NoteRandom.UnitNumerator(bits), NoteRandom.UnitDenominator);
                return new QuantityNode(unit, null, unit.ToString(), at.Start, at.End);
            }

            if (range is not RangeNode r) { Problem(errors, "bad-argument", "A range like 0..100 is expected", range); return null; }
            if ((r.From.Unit is null) != (r.To.Unit is null)) { Problem(errors, "missing-unit", "Both ends of the range need the same kind of unit", r); return null; }
            if (r.From.Unit is not null)
            {
                long? lo = Micro(r.From.Value, r.From.Unit, r.From), hi = Micro(r.To.Value, r.To.Unit, r.To);
                if (lo is null || hi is null) return null;
                long top = hi.Value + (r.Inclusive ? 1 : 0);
                if (top <= lo) { Problem(errors, "range-reversed", "The end of the range is not after its start", r); return null; }
                long v = lo.Value + (long)NoteRandom.Below(bits, top - lo.Value);
                return new QuantityNode(Rat.Of(v), "ticks", v.ToString(CultureInfo.InvariantCulture), at.Start, at.End);
            }

            if (r.From.Value.IsInt && r.To.Value.IsInt)
            {
                BigInteger lo = r.From.Value.N, top = r.To.Value.N + (r.Inclusive ? 1 : 0);
                if (top <= lo) { Problem(errors, "range-reversed", "The end of the range is not after its start", r); return null; }
                BigInteger v = lo + NoteRandom.Below(bits, top - lo);
                return new QuantityNode(Rat.Of(v), null, v.ToString(CultureInfo.InvariantCulture), at.Start, at.End);
            }

            Rat lowRat = r.From.Value, highRat = r.To.Value;
            if (highRat.N * lowRat.D <= lowRat.N * highRat.D) { Problem(errors, "range-reversed", "The end of the range is not after its start", r); return null; }
            Rat value = lowRat + ((highRat - lowRat) * Rat.Of(NoteRandom.UnitNumerator(bits), NoteRandom.UnitDenominator));
            return new QuantityNode(value, null, value.ToString(), at.Start, at.End);
        }

        internal QuantityNode? StreamNext(WordNode w, MethodNode m)
        {
            if (!Streams().TryGetValue(w.Text, out StreamState? state)) { Problem(errors, "stream-error", $"There is no random-stream \"{w.Text}\": declare it in the clip with  random-stream {w.Text} stable(\"name\")", m); return null; }
            if (m.Args.Count > 1) { Problem(errors, "bad-argument", "next() takes no argument, or one range", m); return null; }
            if (state.Use is null)
            {
                int before = errors.Count;
                bool ok = TryKey(state.SeedNode, out ulong key, out string seedText);
                for (int i = before; i < errors.Count; i++) errors[i] = errors[i] with { Start = m.Start, End = m.End, Message = $"random-stream {w.Text}: {errors[i].Message}" };
                if (!ok) return null;
                state.Use = new SeedUse("stream:" + w.Text, seedText, key) { Start = m.Start, End = m.End };
                uses.Add(state.Use);
            }

            ulong bits = DrawBits(state.Use, "random", state.Cursor.ToString(CultureInfo.InvariantCulture));
            state.Cursor++;
            return InRange(m.Args.Count == 1 ? m.Args[0] : null, bits, m);
        }

        private bool IntRange(Node n, string what, long min, long max, out long lo, out long top)
        {
            lo = 0;
            top = 0;
            if (n is not RangeNode { From.Unit: null, To.Unit: null } r || !r.From.Value.IsInt || !r.To.Value.IsInt) { Problem(errors, "bad-argument", $"{what} takes a range of whole numbers, like {what}=48..72 (or 48..=72 to include 72)", n); return false; }
            lo = (long)r.From.Value.N;
            top = (long)r.To.Value.N + (r.Inclusive ? 1 : 0);
            if (top <= lo) { Problem(errors, "range-reversed", "The end of the range is not after its start", n); return false; }
            if (lo < min || top - 1 > max) { Problem(errors, "bad-argument", $"{what} must stay between {min} and {max}", n); return false; }
            return true;
        }

        private PipeValue? Randomize(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["velocity", "pitch", "seed"], 0);
            if (m is null) return null;
            if (!m.ContainsKey("velocity") && !m.ContainsKey("pitch")) { Problem(errors, "bad-argument", "randomize needs velocity=lo..hi and/or pitch=lo..hi (pitch in semitones around the note)", node); return null; }
            long vLo = 0, vTop = 0, pLo = 0, pTop = 0;
            if (m.TryGetValue("velocity", out Node? vn) && !IntRange(vn, "velocity", 1, 127, out vLo, out vTop)) return null;
            if (m.TryGetValue("pitch", out Node? pn) && !IntRange(pn, "pitch", -48, 48, out pLo, out pTop)) return null;
            if (!SeedArg(m, node, "randomize", out SeedUse use)) return null;
            string[] addresses = Addresses(value.Events);
            List<PipeNote> events = [];
            for (int i = 0; i < value.Events.Count; i++)
            {
                PipeNote e = value.Events[i];
                if (vTop > vLo) e = e with { Vel = (int)(vLo + (long)NoteRandom.Below(DrawBits(use, "randomize.velocity", addresses[i]), vTop - vLo)) };
                if (pTop > pLo) e = e with { Pitch = (int)Math.Clamp(e.Pitch + pLo + (long)NoteRandom.Below(DrawBits(use, "randomize.pitch", addresses[i]), pTop - pLo), 0, 127) };
                events.Add(e);
            }

            return new PipeValue { Events = events, Span = value.Span };
        }

        private PipeValue? Thin(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["p", "seed", "per"], 1);
            if (m is null) return null;
            if (m["p"] is not QuantityNode { Unit: null } p || p.Value.N.Sign < 0 || p.Value.N > p.Value.D) { Problem(errors, "bad-argument", "thin takes the chance to drop a note, from 0 to 1, like thin(0.25, seed=...)", m["p"]); return null; }
            string per = "note";
            if (m.TryGetValue("per", out Node? pn)) { if (pn is WordNode { Text: "note" or "bar" } w) per = w.Text; else { Problem(errors, "bad-argument", "per is note or bar", pn); return null; } }
            if (!SeedArg(m, node, "thin", out SeedUse use)) return null;
            string[] addresses = Addresses(value.Events);
            List<PipeNote> kept = [];
            for (int i = 0; i < value.Events.Count; i++)
            {
                string address = per == "bar" ? "bar:" + (value.Events[i].At / NotePipeline.Bar).ToString(CultureInfo.InvariantCulture) : addresses[i];
                bool drop = new BigInteger(DrawBits(use, "thin", address)) * p.Value.D < p.Value.N * (BigInteger.One << 64);
                if (!drop) kept.Add(value.Events[i]);
            }

            return new PipeValue { Events = kept, Span = value.Span };
        }

        private PipeValue? Scramble(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["seed"], 0);
            if (m is null || !SeedArg(m, node, "scramble", out SeedUse use)) return null;
            string[] addresses = Addresses(value.Events);
            int[] canonical = [.. Enumerable.Range(0, value.Events.Count).OrderBy(i => value.Events[i].At).ThenBy(i => value.Events[i].Pitch).ThenBy(i => i)];
            ulong[] keys = [.. canonical.Select(i => DrawBits(use, "scramble", addresses[i]))];
            int[] order = [.. Enumerable.Range(0, canonical.Length).OrderBy(j => keys[j]).ThenBy(j => j)];
            PipeNote[] result = [.. value.Events];
            for (int j = 0; j < canonical.Length; j++) result[canonical[j]] = value.Events[canonical[j]] with { Pitch = value.Events[canonical[order[j]]].Pitch };
            return new PipeValue { Events = [.. result], Span = value.Span };
        }

        private static string? WordOf(Node n) => n switch { WordNode w => w.Text, StringNode s => s.Value, _ => null };

        private bool ScaleArgs(Dictionary<string, Node> m, CallNode node, out int root, out int[] degrees)
        {
            root = 0;
            degrees = [];
            string? rootText = WordOf(m["root"]), scaleText = WordOf(m["scale"]);
            if (rootText is null || !NoteTheory.TryRoot(rootText, out root)) { Problem(errors, "bad-argument", "A root is a note name like C, Eb or \"F#\"", m["root"]); return false; }
            if (scaleText is null || !NoteTheory.Scales.TryGetValue(scaleText.ToLowerInvariant(), out int[]? found)) { Problem(errors, "bad-argument", $"Unknown scale. Scales: {string.Join(", ", NoteTheory.Scales.Keys)}", m["scale"]); return false; }
            degrees = found;
            _ = node;
            return true;
        }

        private static int DegreePitch(int root, int[] degrees, long octave, long index)
        {
            long len = degrees.Length, shift = (long)Math.Floor(index / (double)len), within = (index % len + len) % len;
            return (int)((12 * (octave + 1)) + root + degrees[within] + (12 * shift));
        }

        private PipeValue? SnapKeepAvoid(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["root", "scale"], 2);
            if (m is null || !ScaleArgs(m, node, out int root, out int[] degrees)) return null;
            bool InScale(int pitch) => degrees.Contains((((pitch - root) % 12) + 12) % 12);
            switch (node.Name)
            {
                case "keep": return new PipeValue { Events = [.. value.Events.Where(e => InScale(e.Pitch))], Span = value.Span };
                case "avoid": return new PipeValue { Events = [.. value.Events.Where(e => !InScale(e.Pitch))], Span = value.Span };
                default:
                    return new PipeValue
                    {
                        Events = [.. value.Events.Select(e =>
                        {
                            int rel = (((e.Pitch - root) % 12) + 12) % 12, best = 0, bestDistance = int.MaxValue;
                            foreach (int d in degrees)
                            {
                                int delta = d - rel;
                                if (delta > 6) delta -= 12;
                                if (delta <= -6) delta += 12;
                                if (Math.Abs(delta) < bestDistance || (Math.Abs(delta) == bestDistance && delta < best)) { best = delta; bestDistance = Math.Abs(delta); }
                            }

                            return e with { Pitch = Math.Clamp(e.Pitch + best, 0, 127) };
                        })],
                        Span = value.Span,
                    };
            }
        }

        private PipeValue? ClampFold(PipeValue value, CallNode node)
        {
            if (!Arity(node, 1) || !IntRange(node.Args[0], "range", 0, 127, out long lo, out long top)) return null;
            long hi = top - 1;
            if (node.Name == "clamp") return new PipeValue { Events = [.. value.Events.Select(e => e with { Pitch = (int)Math.Clamp(e.Pitch, lo, hi) })], Span = value.Span };
            if (hi - lo < 11) { Problem(errors, "bad-argument", "fold needs a range of at least one octave (12 semitones), like fold(48..=71)", node.Args[0]); return null; }
            return new PipeValue
            {
                Events = [.. value.Events.Select(e =>
                {
                    int p = e.Pitch;
                    while (p > hi) p -= 12;
                    while (p < lo) p += 12;
                    return e with { Pitch = p };
                })],
                Span = value.Span,
            };
        }

        internal PipeValue? Generate(CallNode node)
        {
            CallNode? g = ResolveArgs(node);
            if (g is null) return null;
            switch (g.Name)
            {
                case "euclid": return Euclid(g);
                case "chords": return Chords(g);
                case "scale": return ScaleRun(g);
                default: return Walk(g);
            }
        }

        private static long SpanFor(long count, long step) => Math.Max(1, ((count * step) + NotePipeline.Bar - 1) / NotePipeline.Bar) * NotePipeline.Bar;

        private PipeValue? Chords(CallNode g)
        {
            List<string> symbols = [];
            Dictionary<string, Node> named = [];
            foreach (Node a in g.Args)
            {
                if (a is NamedNode n)
                {
                    if (n.Name is not ("length" or "octave" or "velocity") || !named.TryAdd(n.Name, n.Value)) { Problem(errors, "bad-argument", "chords takes length=, octave= and velocity=, each once", n); return null; }
                }
                else
                {
                    string? w = WordOf(a);
                    if (w is null)
                    {
                        // after the chord symbols, the next values are length, octave and velocity in that order (the ones not given by name)
                        string[] slots = ["length", "octave", "velocity"];
                        string? slot = slots.FirstOrDefault(n => !named.ContainsKey(n));
                        if (symbols.Count == 0 || slot is null) { Problem(errors, "bad-argument", "chords takes the chord symbols first, like chords(\"Am F C G\"), then length, octave and velocity (by name or in that order)", a); return null; }
                        named[slot] = a;
                        continue;
                    }

                    symbols.AddRange(w.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                }
            }

            if (symbols.Count is 0 or > 256) { Problem(errors, "bad-argument", "chords needs between 1 and 256 chord symbols", g); return null; }
            long length = NotePipeline.Bar, octave = 4, velocity = 90;
            if (named.TryGetValue("length", out Node? ln) && (!Length(ln, out length) || length <= 0)) { if (length <= 0) Problem(errors, "bad-argument", "length must be longer than zero", ln); return null; }
            if (named.TryGetValue("octave", out Node? on) && !Int(on, "octave", 0, 8, out octave)) return null;
            if (named.TryGetValue("velocity", out Node? vn) && !Int(vn, "velocity", 1, 127, out velocity)) return null;
            List<PipeNote> events = [];
            for (int i = 0; i < symbols.Count; i++)
            {
                if (!NoteTheory.TryChord(symbols[i], out int root, out int[] intervals)) { Problem(errors, "bad-argument", $"\"{symbols[i]}\" is not a chord symbol (examples: C, Am, F#m7, Bbmaj7, Gsus4, E7)", g); return null; }
                foreach (int interval in intervals) events.Add(new PipeNote(i * length, length, (int)Math.Clamp((12 * (octave + 1)) + root + interval, 0, 127), (int)velocity, string.Empty, string.Empty));
            }

            return new PipeValue { Events = events, Span = SpanFor(symbols.Count, length) };
        }

        private PipeValue? ScaleRun(CallNode g)
        {
            Dictionary<string, Node>? m = ArgMap(g, ["root", "scale", "steps", "step", "octave", "velocity"], 2);
            if (m is null || !ScaleArgs(m, g, out int root, out int[] degrees)) return null;
            long steps = 8, step = NotePipeline.Beat / 2, octave = 4, velocity = 100;
            if (m.TryGetValue("steps", out Node? sn) && !Int(sn, "steps", 1, 256, out steps)) return null;
            if (m.TryGetValue("step", out Node? tn) && (!Length(tn, out step) || step <= 0)) { if (step <= 0) Problem(errors, "bad-argument", "step must be longer than zero", tn); return null; }
            if (m.TryGetValue("octave", out Node? on) && !Int(on, "octave", 0, 8, out octave)) return null;
            if (m.TryGetValue("velocity", out Node? vn) && !Int(vn, "velocity", 1, 127, out velocity)) return null;
            List<PipeNote> events = [];
            for (long i = 0; i < steps; i++) events.Add(new PipeNote(i * step, step, Math.Clamp(DegreePitch(root, degrees, octave, i), 0, 127), (int)velocity, string.Empty, string.Empty));
            return new PipeValue { Events = events, Span = SpanFor(steps, step) };
        }

        private PipeValue? Walk(CallNode g)
        {
            Dictionary<string, Node>? m = ArgMap(g, ["root", "scale", "steps", "step", "octave", "range", "velocity", "seed"], 2);
            if (m is null || !ScaleArgs(m, g, out int root, out int[] degrees)) return null;
            long steps = 16, step = NotePipeline.Beat / 2, octave = 4, range = 2, velocity = 100;
            if (m.TryGetValue("steps", out Node? sn) && !Int(sn, "steps", 1, 256, out steps)) return null;
            if (m.TryGetValue("step", out Node? tn) && (!Length(tn, out step) || step <= 0)) { if (step <= 0) Problem(errors, "bad-argument", "step must be longer than zero", tn); return null; }
            if (m.TryGetValue("octave", out Node? on) && !Int(on, "octave", 0, 8, out octave)) return null;
            if (m.TryGetValue("range", out Node? rn) && !Int(rn, "range", 1, 7, out range)) return null;
            if (m.TryGetValue("velocity", out Node? vn) && !Int(vn, "velocity", 1, 127, out velocity)) return null;
            if (!SeedArg(m, g, "walk", out SeedUse use)) return null;
            List<PipeNote> events = [];
            long index = 0, len = degrees.Length;
            for (long i = 0; i < steps; i++)
            {
                if (i > 0) index = Math.Clamp(index + (long)NoteRandom.Below(DrawBits(use, "walk", i.ToString(CultureInfo.InvariantCulture)), (2 * range) + 1) - range, -len, 2 * len);
                events.Add(new PipeNote(i * step, step, Math.Clamp(DegreePitch(root, degrees, octave, index), 0, 127), (int)velocity, string.Empty, string.Empty));
            }

            return new PipeValue { Events = events, Span = SpanFor(steps, step) };
        }

        private PipeValue? Humanize(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["timing", "velocity", "seed"], 0);
            if (m is null) return null;
            long timing = 0, velocity = 0;
            if (m.TryGetValue("timing", out Node? tn) && (!Length(tn, out timing) || timing < 0)) return null;
            if (m.TryGetValue("velocity", out Node? vn) && !Int(vn, "velocity", 0, 127, out velocity)) return null;
            if (!SeedArg(m, node, "humanize", out SeedUse use)) return null;
            string[] addresses = Addresses(value.Events);
            List<PipeNote> events = [];
            for (int i = 0; i < value.Events.Count; i++)
            {
                PipeNote e = value.Events[i];
                long dt = timing > 0 ? (long)NoteRandom.Below(DrawBits(use, "humanize.timing", addresses[i]), (2 * timing) + 1) - timing : 0;
                long dv = velocity > 0 ? (long)NoteRandom.Below(DrawBits(use, "humanize.velocity", addresses[i]), (2 * velocity) + 1) - velocity : 0;
                events.Add(e with { At = Math.Max(0, e.At + dt), Vel = (int)Math.Clamp(e.Vel + dv, 1, 127) });
            }

            return Grown(new PipeValue { Events = events, Span = value.Span });
        }

        private PipeValue? Arpeggio(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["rate"], 1);
            if (m is null || !Length(m["rate"], out long rate)) return null;
            if (rate <= 0) { Problem(errors, "bad-argument", "The rate must be longer than zero", node); return null; }
            List<PipeNote> events = [];
            foreach (IGrouping<long, PipeNote> group in value.Events.GroupBy(e => e.At).OrderBy(g => g.Key))
            {
                List<PipeNote> chord = [.. group.OrderBy(e => e.Pitch)];
                if (chord.Count < 2) { events.AddRange(chord); continue; }
                long length = chord.Max(e => e.Len), slots = Math.Max(1, length / rate);
                if (events.Count + slots > context.Limit) { Problem(errors, "cap-events", $"arp would make more than {context.Limit} notes", node); return null; }
                for (long s = 0; s < slots; s++) events.Add(chord[(int)(s % chord.Count)] with { At = group.Key + (s * rate), Len = Math.Min(rate, length - (s * rate)) });
            }

            return Grown(new PipeValue { Events = events, Span = value.Span });
        }

        private PipeValue? Where(PipeValue value, CallNode node)
        {
            Dictionary<string, Node>? m = ArgMap(node, ["pitch", "velocity"], 0);
            if (m is null) return null;
            if (m.Count == 0) { Problem(errors, "bad-argument", "where needs pitch=lo..hi and/or velocity=lo..hi", node); return null; }
            bool Bounds(string key, int max, out long lo, out long hi)
            {
                lo = 0;
                hi = max;
                if (!m.TryGetValue(key, out Node? n)) return true;
                if (n is not RangeNode { From.Unit: null, To.Unit: null } r || !r.From.Value.IsInt || !r.To.Value.IsInt) { Problem(errors, "bad-argument", $"{key} takes a range of whole numbers, like {key}=48..72 (or 48..=72 to include 72)", n); return false; }
                lo = (long)r.From.Value.N;
                hi = (long)r.To.Value.N - (r.Inclusive ? 0 : 1);
                return true;
            }

            if (!Bounds("pitch", 127, out long pLo, out long pHi) || !Bounds("velocity", 127, out long vLo, out long vHi)) return null;
            return new PipeValue { Events = [.. value.Events.Where(e => e.Pitch >= pLo && e.Pitch <= pHi && e.Vel >= vLo && e.Vel <= vHi)], Span = value.Span };
        }

        /// <summary>set(pitch="38 50 45", velocity=100, pan=...): puts a value, or a cycle of values, on every note in the order of the notes (start, then pitch).</summary>
        private PipeValue? SetFields(PipeValue value, CallNode node)
        {
            string[] names = ["pitch", "velocity", "pan", "fine", "rel", "cut", "slide", "port", "bend", "channel", "bank", "program"];
            Dictionary<string, Node>? m = ArgMap(node, names, 0);
            if (m is null) return null;
            if (m.Count == 0) { Problem(errors, "bad-argument", "set needs at least one of: " + string.Join(", ", names), node); return null; }
            Dictionary<string, long[]> lists = [];
            foreach (string key in names.Where(m.ContainsKey))
            {
                if (!SetValues(key, m[key], out long[] values)) return null;
                lists[key] = values;
            }

            int[] order = [.. Enumerable.Range(0, value.Events.Count).OrderBy(i => value.Events[i].At).ThenBy(i => value.Events[i].Pitch).ThenBy(i => i)];
            PipeNote[] result = [.. value.Events];
            for (int rank = 0; rank < order.Length; rank++)
            {
                PipeNote e = value.Events[order[rank]];
                foreach ((string key, long[] values) in lists)
                {
                    long v = values[rank % values.Length];
                    e = key switch { "pitch" => e with { Pitch = (int)v }, "velocity" => e with { Vel = (int)v }, "channel" or "bank" or "program" => e with { Tail = SetTail(e.Tail, key, v) }, _ => e with { Ext = SetExt(e.Ext, key, v) } };
                }

                result[order[rank]] = e;
            }

            return new PipeValue { Events = [.. result], Span = value.Span };
        }

        private bool SetValues(string key, Node n, out long[] values)
        {
            values = [];
            (long min, long max) = SetRange[key];
            string[] tokens;
            if (n is QuantityNode { Unit: null } q && q.Value.IsInt) tokens = [q.Value.N.ToString(CultureInfo.InvariantCulture)];
            else if (WordOf(n) is { } text) tokens = text.Split([' ', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            else { Problem(errors, "bad-argument", $"{key} takes a whole number or a quoted list of them, like {key}=\"1 2 3\"", n); return false; }
            if (tokens.Length is 0 or > 4096) { Problem(errors, "bad-argument", $"{key} needs between 1 and 4096 values", n); return false; }
            List<long> found = [];
            foreach (string token in tokens)
            {
                long v;
                Match name = key == "pitch" ? PitchNamePattern().Match(token) : Match.Empty;
                if (name.Success && NoteTheory.TryRoot(char.ToUpperInvariant(name.Groups[1].Value[0]) + name.Groups[1].Value[1..], out int root)) v = (12 * (long.Parse(name.Groups[2].Value, CultureInfo.InvariantCulture) + 1)) + root;
                else if (!long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) { Problem(errors, "bad-argument", $"\"{token}\" is not a value of {key}{(key == "pitch" ? " (a number or a note name like C4 or F#3)" : string.Empty)}", n); return false; }
                if (v < min || v > max) { Problem(errors, "bad-argument", $"{key} must be from {min} to {max} (found {v})", n); return false; }
                found.Add(v);
            }

            values = [.. found];
            return true;
        }

        private bool Arity(CallNode node, int count)
        {
            if (node.Args.Count == count) return true;
            Problem(errors, "bad-argument", $"{node.Name} takes {count} argument{(count == 1 ? string.Empty : "s")}", node);
            return false;
        }

        private static PipeValue SelectWindow(PipeValue value, (long From, long To) w, bool rebase)
        {
            List<PipeNote> events = [];
            foreach (PipeNote e in value.Events)
            {
                if (e.At < w.From || e.At >= w.To) continue;
                events.Add(e with { At = rebase ? e.At - w.From : e.At, Len = Math.Max(1, Math.Min(e.Len, w.To - e.At)) });
            }

            return new PipeValue { Events = events, Span = rebase ? w.To - w.From : value.Span };
        }

        internal PipeValue? Step(PipeValue value, CallNode node)
        {
            string name = node.Name;
            if (clock.Elapsed > context.Budget) { Problem(errors, "cap-time", $"The pipeline took longer than {context.Budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds", node); return null; }
            if (!OperatorNames.Contains(name) && context.Helpers?.IsBroken(name) == true) { Problem(errors, "helper-broken", $"The helper \"{name}\" is not available: its declaration has an error (fix that first)", node); return null; }
            if (!OperatorNames.Contains(name)) { Problem(errors, "unknown-operator", $"Unknown operator \"{name}\"", new CallNode(name, [], node.Start, node.NameEnd, node.NameEnd), Nearest(name)); return null; }
            CallNode? resolved = ResolveArgs(node);
            if (resolved is null) return null;
            node = resolved;
            switch (name)
            {
                case "bars" or "beats" or "time":
                    {
                        if (!Arity(node, 1)) return null;
                        (long, long)? w = Window(node.Args[0], name);
                        return w is null ? null : SelectWindow(value, w.Value, true);
                    }

                case "mask":
                    {
                        if (!Arity(node, 1)) return null;
                        (long, long)? w = RangeArg(node.Args[0]);
                        return w is null ? null : SelectWindow(value, w.Value, false);
                    }

                case "remove" or "cut":
                    {
                        if (!Arity(node, 1)) return null;
                        (long From, long To)? w = RangeArg(node.Args[0]);
                        if (w is null) return null;
                        long gap = w.Value.To - w.Value.From;
                        List<PipeNote> events = [.. value.Events.Where(e => e.At < w.Value.From || e.At >= w.Value.To).Select(e => name == "cut" && e.At >= w.Value.To ? e with { At = e.At - gap } : e)];
                        return new PipeValue { Events = events, Span = name == "cut" ? Math.Max(0, value.Span - gap) : value.Span };
                    }

                case "replace":
                    {
                        if (!Arity(node, 2)) return null;
                        (long From, long To)? w = RangeArg(node.Args[0]);
                        PipeValue? other = Eval(node.Args[1], out _);
                        if (w is null || other is null) return null;
                        List<PipeNote> kept = [.. value.Events.Where(e => e.At < w.Value.From || e.At >= w.Value.To)];
                        kept.AddRange(other.Events.Select(e => e with { At = e.At + w.Value.From }));
                        return Grown(new PipeValue { Events = kept, Span = value.Span });
                    }

                case "transpose":
                    {
                        if (!Arity(node, 1)) return null;
                        if (node.Args[0] is not QuantityNode { Unit: null } a || !a.Value.IsInt) { Problem(errors, "bad-argument", "transpose takes whole semitones, like +12", node.Args[0]); return null; }
                        int semitones = (int)BigInteger.Clamp(a.Value.N, -48, 48);
                        return new PipeValue { Events = [.. value.Events.Select(e => e with { Pitch = Math.Clamp(e.Pitch + semitones, 0, 127) })], Span = value.Span };
                    }

                case "shift":
                    {
                        if (!Arity(node, 1)) return null;
                        if (node.Args[0] is not QuantityNode a) { Problem(errors, "bad-argument", "shift takes a length like +250ms", node.Args[0]); return null; }
                        if (a.Unit is null) { Problem(errors, "missing-unit", "A length needs a unit (bars, beats, s, ms or ticks)", a); return null; }
                        long? by = Micro(a.Value, a.Unit, a);
                        if (by is null) return null;
                        List<PipeNote> moved = [.. value.Events.Select(e => e with { At = e.At + by.Value })];
                        int before = moved.Count(e => e.At < 0);
                        if (before > 0) Problem(warnings, "before-origin", $"{before} note{(before == 1 ? string.Empty : "s")} start before the beginning and were dropped", node);
                        return Grown(new PipeValue { Events = [.. moved.Where(e => e.At >= 0)], Span = value.Span });
                    }

                case "randomize": return Randomize(value, node);
                case "thin": return Thin(value, node);
                case "scramble": return Scramble(value, node);
                case "clamp" or "fold": return ClampFold(value, node);
                case "snap" or "keep" or "avoid": return SnapKeepAvoid(value, node);
                case "reverse":
                    {
                        if (!Arity(node, 0)) return null;
                        return new PipeValue { Events = [.. value.Events.Select(e => e with { At = Math.Max(0, value.Span - (e.At + e.Len)) })], Span = value.Span };
                    }

                case "stretch":
                    {
                        if (!Arity(node, 1)) return null;
                        if (node.Args[0] is not QuantityNode { Unit: null } q || q.Value.N.Sign <= 0 || q.Value.N * 64 < q.Value.D || q.Value.N > 64 * q.Value.D) { Problem(errors, "bad-argument", "stretch takes a factor between 1/64 and 64, like 2 (twice as slow) or 0.5", node.Args[0]); return null; }
                        long Scale(long x) => (long)RoundHalfEven(Rat.Of(x) * q.Value);
                        return Grown(new PipeValue { Events = [.. value.Events.Select(e => e with { At = Scale(e.At), Len = Math.Max(1, Scale(e.Len)) })], Span = Scale(value.Span) });
                    }

                case "legato":
                    {
                        if (!Arity(node, 0)) return null;
                        List<PipeNote> ordered = [.. value.Events.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.e.Pitch).ThenBy(x => x.i).Select(x => x.e)];
                        long[] starts = [.. ordered.Select(e => e.At).Distinct()];
                        List<PipeNote> result = [];
                        foreach (PipeNote e in ordered)
                        {
                            int at = Array.BinarySearch(starts, e.At);
                            result.Add(at + 1 < starts.Length ? e with { Len = starts[at + 1] - e.At } : e);
                        }

                        return new PipeValue { Events = result, Span = value.Span };
                    }

                case "quantize":
                    {
                        if (!Arity(node, 1)) return null;
                        if (node.Args[0] is not QuantityNode { Unit: not null } g) { Problem(errors, "missing-unit", "quantize takes a grid length with a unit, like 0.25beat", node.Args[0]); return null; }
                        long? grid = Micro(g.Value, g.Unit, g);
                        if (grid is null) return null;
                        if (grid <= 0) { Problem(errors, "bad-argument", "The grid must be longer than zero", g); return null; }
                        return Grown(new PipeValue { Events = [.. value.Events.Select(e => e with { At = (long)RoundHalfEven(Rat.Of(e.At, grid.Value)) * grid.Value })], Span = value.Span });
                    }

                case "humanize":
                    return Humanize(value, node);
                case "set":
                    return SetFields(value, node);
                case "repeat":
                    {
                        if (!Arity(node, 1)) return null;
                        if (!Int(node.Args[0], "times", 1, 1024, out long times)) return null;
                        if (value.Events.Count * times > context.Limit) { Problem(errors, "cap-events", $"repeat({times}) would make {value.Events.Count * times} notes; the limit is {context.Limit}", node); return null; }
                        List<PipeNote> copies = [];
                        for (long k = 0; k < times; k++) copies.AddRange(value.Events.Select(e => e with { At = e.At + (k * value.Span) }));
                        return new PipeValue { Events = copies, Span = value.Span * times };
                    }

                case "arp":
                    return Arpeggio(value, node);
                case "where":
                    return Where(value, node);
                case "euclid" or "walk" or "chords" or "scale":
                    Problem(errors, "bad-argument", $"{name}(...) makes notes, it does not work on notes: start a pipeline with it", node);
                    return null;
                case "concat" or "merge":
                    {
                        if (!Arity(node, 1)) return null;
                        PipeValue? other = Eval(node.Args[0], out _);
                        if (other is null) return null;
                        if (name == "concat") return new PipeValue { Events = [.. value.Events, .. other.Events.Select(e => e with { At = e.At + value.Span })], Span = value.Span + other.Span };
                        return Grown(new PipeValue { Events = [.. value.Events, .. other.Events], Span = Math.Max(value.Span, other.Span) });
                    }
            }

            return null;
        }
    }

    [GeneratedRegex(@"^([A-Ga-g][#b]?)(-?\d)$", RegexOptions.None, 1000)]
    private static partial Regex PitchNamePattern();

    [GeneratedRegex(@"stable\(", RegexOptions.None, 1000)]
    private static partial Regex StablePattern();

    [GeneratedRegex(@"\d(?:ms|s)\b", RegexOptions.None, 1000)]
    private static partial Regex SecondsPattern();

    [GeneratedRegex(@"^([+-]?)(\d+)(?:\.(\d+))?$", RegexOptions.None, 1000)]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"\d+:\d+(?:\.\d+)?:\d+(?:\.\d+)?:\d+:\d+(?::\d+){0,4}(?:;[a-z]+=-?\d+)*", RegexOptions.None, 1000)]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"[+-]?\d+(?:\.\d+)?", RegexOptions.None, 1000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("[A-Za-z]+", RegexOptions.None, 1000)]
    private static partial Regex UnitPattern();

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_-]*(?:#[A-Za-z0-9_-]*)?", RegexOptions.None, 1000)]
    private static partial Regex IdentPattern();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z-]*)\s*(.*)$", RegexOptions.None, 1000)]
    private static partial Regex BodyLinePattern();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([a-z]*)$", RegexOptions.None, 1000)]
    private static partial Regex SpanPattern();

    [GeneratedRegex(@"^(\d+):(\d+(?:\.\d+)?):(\d+(?:\.\d+)?):(\d+):(\d+)((?::\d+){0,4})((?:;[a-z]+=-?\d+)*)$", RegexOptions.None, 1000)]
    private static partial Regex EntryOf();
}
