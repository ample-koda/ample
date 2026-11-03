using System.Globalization;
using System.Text.Json;

namespace Ample.Language;

/// <summary>One parameter of a note operator, from the typed catalogue (spec/note-ops.json).</summary>
/// <param name="Name">The parameter name (also its named-argument spelling).</param>
/// <param name="Type">The type name from the types section (PitchClass, Duration, Octave ...).</param>
/// <param name="Required">Whether the argument must be given.</param>
/// <param name="Default">The default as written, or null.</param>
/// <param name="Of">The element type of a Range, or null.</param>
/// <param name="Min">A lower bound that replaces the one of the type, or null.</param>
/// <param name="Max">An upper bound that replaces the one of the type, or null.</param>
/// <param name="Options">The words of a Choice, or null.</param>
/// <param name="Doc">What the parameter means, shown by the hover of a call, or null.</param>
public sealed record NoteParam(string Name, string Type, bool Required, string? Default, string? Of, long? Min, long? Max, IReadOnlyList<string>? Options, string? Doc = null);

/// <summary>A note operator, generator or value function with its typed parameters.</summary>
/// <param name="Id">The name.</param>
/// <param name="Kind">The kind (source, select, transform, combine, value, seed ...).</param>
/// <param name="Receiver">The type the operator works on (Notes), or null for a generator or a value function.</param>
/// <param name="Returns">The type it makes.</param>
/// <param name="Params">The parameters in positional order.</param>
/// <param name="Doc">A one-line description.</param>
public sealed record NoteSignature(string Id, string Kind, string? Receiver, string Returns, IReadOnlyList<NoteParam> Params, string? Doc);

/// <summary>A type of the musical type model (docs/language/15-musical-types.md).</summary>
/// <param name="Name">The type name.</param>
/// <param name="Kind">vocabulary, refined, number, quantity, union, seed, window, range, notes, choice, list, string, identifier or vocabulary-list.</param>
/// <param name="Doc">A one-line description.</param>
/// <param name="Base">The base of a refinement.</param>
/// <param name="Min">The lowest value of a refinement.</param>
/// <param name="Max">The highest value of a refinement.</param>
/// <param name="Values">The words of a vocabulary.</param>
/// <param name="Units">The units of a quantity.</param>
/// <param name="Of">The members of a union.</param>
/// <param name="Percent">Whether a percent literal converts to the value.</param>
/// <param name="NoteNames">Whether a note name (C2) is accepted.</param>
/// <param name="Bare">The unit a bare number means (Interval: st), or null.</param>
public sealed record NoteTypeInfo(string Name, string Kind, string? Doc, string? Base, long? Min, long? Max, IReadOnlyList<string> Values, IReadOnlyList<string> Units, IReadOnlyList<string> Of, bool Percent, bool NoteNames, string? Bare);

/// <summary>The typed catalogue of the note operators, loaded once from the embedded spec/note-ops.json: the checker, the completion and the docs all read this one file.</summary>
public static class NoteCatalog
{
    private static readonly Lazy<(IReadOnlyDictionary<string, NoteSignature>, IReadOnlyDictionary<string, NoteTypeInfo>, IReadOnlyDictionary<string, NoteSignature>)> Loaded = new(Load);

    /// <summary>Gets the operators by name.</summary>
    public static IReadOnlyDictionary<string, NoteSignature> Signatures => Loaded.Value.Item1;

    /// <summary>Gets the built-in functions, methods, units and keywords of the language layer by name (Int, Round, min, fork, bars, helper ...): what the hover and the completion say about them. The evaluator does not read this list.</summary>
    public static IReadOnlyDictionary<string, NoteSignature> Builtins => Loaded.Value.Item3;

    /// <summary>Gets the types by name.</summary>
    public static IReadOnlyDictionary<string, NoteTypeInfo> Types => Loaded.Value.Item2;

    /// <summary>Finds the dimension of a unit word: pitch (st, oct, cents), length (bars, beats, ticks) or time (s, ms); null for an unknown unit.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The dimension.</returns>
    public static string? DimensionOf(string unit)
    {
        foreach (string dimension in new[] { "Interval", "Length", "Time" })
        {
            if (Types.TryGetValue(dimension, out NoteTypeInfo? t) && t.Units.Contains(unit)) return dimension;
        }

        return unit == "%" ? "Percent" : null;
    }

    private static (IReadOnlyDictionary<string, NoteSignature>, IReadOnlyDictionary<string, NoteTypeInfo>, IReadOnlyDictionary<string, NoteSignature>) Load()
    {
        using JsonDocument doc = JsonDocument.Parse(NoteCatalogData.Json);
        JsonElement root = doc.RootElement;
        static IReadOnlyList<string> Strings(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(x => x.ToString())] : [];
        static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        Dictionary<string, NoteTypeInfo> types = new(StringComparer.Ordinal);
        foreach (JsonProperty p in root.GetProperty("types").EnumerateObject())
        {
            JsonElement t = p.Value;
            types[p.Name] = new NoteTypeInfo(p.Name, Str(t, "kind") ?? "string", Str(t, "doc"), Str(t, "base"), Long(t, "min"), Long(t, "max"), Strings(t, "values"), Strings(t, "units"), Strings(t, "of"), Flag(t, "percent"), Flag(t, "noteNames"), Str(t, "bare"));
        }

        Dictionary<string, NoteSignature> signatures = new(StringComparer.Ordinal);
        foreach (JsonElement op in root.GetProperty("operators").EnumerateArray())
        {
            if (!op.TryGetProperty("returns", out JsonElement returns)) continue;
            List<NoteParam> list = [];
            foreach (JsonElement prm in op.GetProperty("params").EnumerateArray())
            {
                IReadOnlyList<string> options = Strings(prm, "options");
                list.Add(new NoteParam(prm.GetProperty("name").GetString()!, prm.GetProperty("type").GetString()!, Flag(prm, "required"), Str(prm, "default"), Str(prm, "of"), Long(prm, "min"), Long(prm, "max"), options.Count == 0 ? null : options, Str(prm, "doc")));
            }

            string id = op.GetProperty("id").GetString()!;
            signatures[id] = new NoteSignature(id, Str(op, "kind") ?? string.Empty, Str(op, "receiver"), returns.GetString() ?? "Notes", list, Str(op, "doc"));
        }

        Dictionary<string, NoteSignature> builtins = new(StringComparer.Ordinal);
        if (root.TryGetProperty("builtins", out JsonElement builtinList))
        {
            foreach (JsonElement item in builtinList.EnumerateArray())
            {
                List<NoteParam> parameters = [];
                if (item.TryGetProperty("params", out JsonElement written))
                {
                    foreach (JsonElement prm in written.EnumerateArray()) parameters.Add(new NoteParam(prm.GetProperty("name").GetString()!, prm.GetProperty("type").GetString()!, Flag(prm, "required"), Str(prm, "default"), null, null, null, null, Str(prm, "doc")));
                }

                string id = item.GetProperty("id").GetString()!;
                builtins[id] = new NoteSignature(id, Str(item, "kind") ?? string.Empty, Str(item, "receiver"), Str(item, "returns") ?? string.Empty, parameters, Str(item, "doc"));
            }
        }

        return (signatures, types, builtins);
    }
}

public static partial class NotePipeline
{
    private static readonly System.Text.RegularExpressions.Regex NoteNamePattern = new(@"^([A-Ga-g])([#b]?)(-?\d)$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Reads a note name (C4, F#3, Bb2) as a MIDI pitch (C4 is 60).</summary>
    /// <param name="text">The name.</param>
    /// <param name="pitch">The pitch.</param>
    /// <returns>True when it is a note name in the range 0 to 127.</returns>
    public static bool TryNoteName(string text, out int pitch)
    {
        pitch = 0;
        System.Text.RegularExpressions.Match m = NoteNamePattern.Match(text);
        if (!m.Success || !NoteTheory.TryRoot(m.Groups[1].Value + m.Groups[2].Value, out int pc)) return false;
        int value = (12 * (int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + 1)) + pc;
        pitch = value;
        return value is >= 0 and <= 127;
    }

    /// <summary>How well the span of an expression is known without running it: "known" (plain notes, a stored bake), "derived" (generators and operators over static quantities: the span follows by arithmetic) or "unresolved" (it reads another block, a file or something dynamic, so only evaluation can tell).</summary>
    /// <param name="text">The expression text.</param>
    /// <returns>known, derived or unresolved.</returns>
    public static string SpanClass(string text)
    {
        string clean = StripComments(text);
        try
        {
            return SpanClassOf(new ExprParser(clean, Tokenize(clean)).Parse()) switch { 0 => "known", 1 => "derived", _ => "unresolved" };
        }
        catch (SyntaxError)
        {
            return "unresolved";
        }
    }

    private static int SpanClassOf(Node node) => node switch
    {
        LiteralNode or EditableNode => 0,
        BakeNode { Stage: { NotesText: not null } } => 0,
        CallNode { Name: "walk" or "euclid" or "chords" or "scale" } c => c.Args.Any(HasReference) ? 2 : 1,
        PipelineNode p => Math.Max(SpanClassOf(p.Source), p.Steps.Select(s => s is BakeNode { Stage: { NotesText: not null } } ? 0 : s is CallNode c ? (c.Args.Any(HasReference) ? 2 : 1) : 2).DefaultIfEmpty(0).Max()),
        _ => 2,
    };

    private static bool HasReference(Node node) => node switch
    {
        RefNode => true,
        PipelineNode p => HasReference(p.Source) || p.Steps.Any(HasReference),
        CallNode c => c.Args.Any(HasReference),
        NamedNode n => HasReference(n.Value),
        MethodNode m => HasReference(m.Target) || m.Args.Any(HasReference),
        BakeNode b => b.Stage.NotesText is null,
        _ => false,
    };

    /// <summary>Checks the arguments of every operator in an expression against the typed catalogue, without running it: vocabulary (pitch classes, scales, chords), refinements (octave, velocity, counts), units and dimensions, seeds and windows.</summary>
    /// <param name="text">The expression text (comments allowed; offsets of the issues refer to this text).</param>
    /// <returns>The problems; syntax problems are not reported here (the evaluation reports them).</returns>
    public static IReadOnlyList<PipeIssue> CheckTypes(string text)
    {
        List<PipeIssue> issues = [];
        string clean = StripComments(text);
        Node ast;
        try
        {
            ast = new ExprParser(clean, Tokenize(clean)).Parse();
        }
        catch (SyntaxError)
        {
            return issues;
        }

        new TypeCheck(issues).Walk(ast);
        return issues;
    }

    /// <summary>Rewrites the typed spellings to the plain ones the evaluator knows, so that +7 and +7st (and 25% and 0.25, C2 and 36) are the same expression with the same recipe.</summary>
    private static Node Typed(Node node) => node switch
    {
        PipelineNode p => p with { Source = Typed(p.Source), Steps = [.. p.Steps.Select(Typed)] },
        CallNode c => TypedCall(c),
        NamedNode n => n with { Value = Typed(n.Value) },
        MethodNode m => m with { Target = Typed(m.Target), Args = [.. m.Args.Select(Typed)] },
        _ => node,
    };

    private static CallNode TypedCall(CallNode c)
    {
        if (c.Name == "randomSeed") c = c with { Name = "random-seed" };
        NoteCatalog.Signatures.TryGetValue(c.Name, out NoteSignature? sig);
        List<Node> args = [];
        for (int i = 0; i < c.Args.Count; i++)
        {
            Node a = c.Args[i];
            NoteParam? prm = null;
            if (sig is not null)
            {
                prm = a is NamedNode named ? sig.Params.FirstOrDefault(x => x.Name == named.Name) : i < sig.Params.Count ? sig.Params[i] : null;
            }

            Node inner = a is NamedNode nn ? nn.Value : a;
            Node converted = prm is null ? Typed(inner) : TypedValue(prm, Typed(inner));
            args.Add(a is NamedNode nn2 ? nn2 with { Value = converted } : converted);
        }

        return c with { Args = args };
    }

    private static Node TypedValue(NoteParam prm, Node node)
    {
        NoteCatalog.Types.TryGetValue(prm.Type, out NoteTypeInfo? t);
        switch (node)
        {
            case QuantityNode { Unit: "st" } q when prm.Type == "Interval": return q with { Unit = null };
            case QuantityNode { Unit: "oct" } q when prm.Type == "Interval": return q with { Unit = null, Value = q.Value * Rat.Of(12) };
            case QuantityNode { Unit: "cents" } q when prm.Type == "Interval": return q with { Unit = null, Value = q.Value * Rat.Of(1, 100) };
            case QuantityNode { Unit: "%" } q when t is { Percent: true }: return q with { Unit = null, Value = q.Value * Rat.Of(1, 100) };
            case WordNode w when t is { NoteNames: true } && TryNoteName(w.Text, out int pitch): return new QuantityNode(Rat.Of(pitch), null, pitch.ToString(CultureInfo.InvariantCulture), w.Start, w.End);
            case RangeNode r when prm.Type == "Range" && prm.Of is not null:
                {
                    NoteParam element = prm with { Type = prm.Of, Of = null };
                    return r with { From = (QuantityNode)TypedValue(element, r.From), To = (QuantityNode)TypedValue(element, r.To) };
                }

            default: return node;
        }
    }

    private sealed class TypeCheck(List<PipeIssue> issues)
    {
        private static string A(string name) => ("AEIOU".Contains(name[0], StringComparison.Ordinal) ? "an " : "a ") + name;

        private void Issue(string id, string message, Node node, string? suggestion = null) => issues.Add(new PipeIssue(id, message, node.Start, node.End, suggestion));

        private static string Nearest(string word, IEnumerable<string> candidates)
        {
            string best = string.Empty;
            int bestDistance = 3;
            foreach (string candidate in candidates)
            {
                int d = NotePipeline.Distance(word.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (d < bestDistance) { best = candidate; bestDistance = d; }
            }

            return best;
        }

        public void Walk(Node node)
        {
            switch (node)
            {
                case PipelineNode p:
                    Walk(p.Source);
                    foreach (Node s in p.Steps) Walk(s);
                    break;
                case CallNode c:
                    Call(c);
                    break;
                case NamedNode n:
                    Walk(n.Value);
                    break;
                case MethodNode m:
                    Walk(m.Target);
                    foreach (Node a in m.Args) Walk(a);
                    break;
            }
        }

        private void Call(CallNode c)
        {
            if (c.Name == "random-seed") issues.Add(new PipeIssue("deprecated-name", "random-seed() is spelled randomSeed()", c.Start, c.NameEnd, "randomSeed"));
            if (!NoteCatalog.Signatures.TryGetValue(c.Name, out NoteSignature? sig)) { foreach (Node a in c.Args) Walk(a); return; }
            HashSet<string> given = [];
            int position = 0;
            foreach (Node a in c.Args)
            {
                NoteParam? prm;
                Node value = a;
                if (a is NamedNode n)
                {
                    prm = sig.Params.FirstOrDefault(x => x.Name == n.Name);
                    value = n.Value;
                    if (prm is null)
                    {
                        string near = Nearest(n.Name, sig.Params.Select(x => x.Name));
                        Issue("bad-argument", $"{c.Name} has no argument \"{n.Name}\" (it has: {string.Join(", ", sig.Params.Select(x => x.Name))})", n, near.Length > 0 ? near + "=" : null);
                        Walk(value);
                        continue;
                    }
                }
                else if (position < sig.Params.Count)
                {
                    prm = sig.Params[position];
                    if (prm.Type != "Values") position++;
                    else if (!given.Add(prm.Name + "#" + given.Count)) continue;
                }
                else
                {
                    Issue("bad-argument", $"{c.Name} takes at most {sig.Params.Count} arguments", a);
                    Walk(a);
                    continue;
                }

                if (prm.Type != "Values" && !given.Add(prm.Name)) { Issue("bad-argument", $"\"{prm.Name}\" is given twice", a); continue; }
                if (prm.Type == "Values") given.Add(prm.Name);
                Arg(c, prm, value);
            }

            foreach (NoteParam prm in sig.Params.Where(x => x.Required && !given.Contains(x.Name) && x.Type != "Notes" && x.Name != "range" && x.Name != "name"))
                Issue("bad-argument", $"{c.Name} needs \"{prm.Name}\" ({prm.Type})", c);
        }

        private void Arg(CallNode call, NoteParam prm, Node value)
        {
            NoteCatalog.Types.TryGetValue(prm.Type, out NoteTypeInfo? t);
            if (t is null) { Walk(value); return; }
            switch (t.Kind)
            {
                case "notes":
                case "list":
                case "identifier":
                    Walk(value);
                    break;
                case "string":
                    if (value is not StringNode) Issue("type-mismatch", $"{prm.Name} needs a name in quotes", value);
                    break;
                case "seed":
                    Seed(value);
                    break;
                case "window":
                    Window(value, call.Name is "bars" or "beats" or "time" ? call.Name : "bars");
                    break;
                case "range":
                    Range(prm, value);
                    break;
                case "choice":
                    if (value is WordNode w && prm.Options!.Contains(w.Text)) break;
                    Issue("type-mismatch", $"{prm.Name} is one of: {string.Join(", ", prm.Options!)}", value, value is WordNode w2 ? Nearest(w2.Text, prm.Options!) is { Length: > 0 } s ? s : null : null);
                    break;
                case "vocabulary-list":
                    Chords(value);
                    break;
                default:
                    Value(prm, t, value);
                    break;
            }
        }

        private void Chords(Node value)
        {
            if (value is not StringNode s) { Issue("type-mismatch", "Chords are written as a quoted list, for example \"Am F C G7\"", value); return; }
            int offset = 0;
            foreach (string token in s.Value.Split(' ', StringSplitOptions.None))
            {
                if (token.Length > 0 && !NoteTheory.TryChord(token, out _, out _))
                {
                    int start = value.Start + 1 + offset;
                    bool rootOk = token.Length > 0 && NoteTheory.TryRoot(token.Length > 1 && token[1] is '#' or 'b' ? token[..2] : token[..1], out _);
                    issues.Add(new PipeIssue("type-mismatch", rootOk ? $"\"{token}\" is not a chord symbol (qualities: m, 7, maj7, m7, m7b5, dim, dim7, aug, sus2, sus4, 6, m6, 9, add9)" : $"\"{token}\" is not a chord symbol: it must start with a root (C, C#, Db ... B)", start, start + token.Length));
                }

                offset += token.Length + 1;
            }
        }

        private void Seed(Node value)
        {
            switch (value)
            {
                case QuantityNode { Unit: null } q when q.Value.IsInt && q.Value.N.Sign >= 0: return;
                case CallNode { Name: "stable" or "random-seed" } c: Call(c); return;
                case MethodNode { Name: "fork" } m: Seed(m.Target); return;
                case WordNode: return;   // a name: the evaluation knows whether it is a random-stream (and says so)
                default:
                    return;   // seed errors (AMP2038) are reported by the evaluation
            }
        }

        private void Window(Node value, string how)
        {
            if (value is CallNode { Name: "bars" or "beats" or "time", Args.Count: 1 } c) { Window(c.Args[0], c.Name); return; }
            if (value is not RangeNode r) { Issue("type-mismatch", "A range like 1..5 or bars(1..5) is expected", value); return; }
            foreach (QuantityNode end in new[] { r.From, r.To })
            {
                if (how == "time")
                {
                    if (end.Unit is null) Issue("missing-unit", "A time position needs a unit (s or ms)", end);
                    else if (NoteCatalog.DimensionOf(end.Unit) != "Time") Issue("wrong-dimension", $"{end.Text} is not a time: use s or ms", end);
                }
                else if (end.Unit is not null && NoteCatalog.DimensionOf(end.Unit) != "Length")
                {
                    Issue("wrong-dimension", $"{end.Text}: {(how == "beats" ? "beat" : "bar")} positions are plain numbers", end);
                }
            }
        }

        private void Range(NoteParam prm, Node value)
        {
            if (value is not RangeNode r) { Issue("type-mismatch", "A range like 1..5 or 1..=4 is expected", value); return; }
            if (prm.Of is null)
            {
                string? u1 = r.From.Unit, u2 = r.To.Unit;
                if (u1 != u2) Issue("wrong-dimension", "Both ends of the range need the same unit", r);
                return;
            }

            NoteParam element = prm with { Type = prm.Of, Of = null };
            NoteCatalog.Types.TryGetValue(element.Type, out NoteTypeInfo? et);
            if (et is null) return;
            Value(element, et, r.From);
            Value(element, et, r.To);
        }

        private void Value(NoteParam prm, NoteTypeInfo t, Node value)
        {
            switch (t.Kind)
            {
                case "vocabulary":
                    Vocabulary(prm, t, value);
                    return;
                case "number" or "refined":
                    Number(prm, t, value);
                    return;
                case "quantity" or "union":
                    Quantity(prm, t, value);
                    return;
                default:
                    return;
            }
        }

        private void Vocabulary(NoteParam prm, NoteTypeInfo t, Node value)
        {
            bool Valid(string word) => t.Name == "PitchClass" ? NoteTheory.TryRoot(word, out _) : t.Values.Contains(word);
            switch (value)
            {
                case WordNode w when Valid(w.Text): return;
                case WordNode w:
                    {
                        string near = Nearest(w.Text, t.Values);
                        Issue("type-mismatch", $"\"{w.Text}\" is not {A(t.Name)}" + (t.Name == "PitchClass" ? " (C, C#, Db ... B)" : near.Length > 0 ? string.Empty : $" (one of: {string.Join(", ", t.Values)})"), value, near.Length > 0 ? near : null);
                        return;
                    }

                case StringNode s when Valid(s.Value):
                    issues.Add(new PipeIssue("quoted-word", $"\"{s.Value}\" is a quoted word where {A(t.Name)} is expected: write it without quotes", value.Start, value.End, s.Value));
                    return;
                case StringNode s:
                    Issue("type-mismatch", $"\"{s.Value}\" is not {A(t.Name)}" + (t.Name == "PitchClass" ? " (C, C#, Db ... B)" : string.Empty), value);
                    return;
                case QuantityNode q:
                    Issue("type-mismatch", $"{q.Text} is not {A(t.Name)}" + (t.Name == "PitchClass" ? " (C, C#, Db ... B)" : $" (for example {t.Values[0]})"), value);
                    return;
                default:
                    if (value is RangeNode or ListNode or BoolNode) Issue("type-mismatch", $"{A(t.Name)} is expected", value);
                    return;
            }
        }

        private void Number(NoteParam prm, NoteTypeInfo t, Node value)
        {
            bool integer = t.Name == "Int" || t.Base == "Int";
            long? min = prm.Min ?? t.Min, max = prm.Max ?? t.Max;
            string Describe() => (min, max) switch
            {
                ({ } lo, { } hi) => $"{t.Name} ({lo} to {hi})",
                ({ } lo, null) => $"{t.Name} ({lo} or more)",
                (null, { } hi) => $"{t.Name} ({hi} or less)",
                _ => t.Name,
            };
            switch (value)
            {
                case QuantityNode q:
                    {
                        if (q.Unit is not null && !(t.Percent && q.Unit == "%"))
                        {
                            Issue("wrong-dimension", $"{q.Text} has a unit, but {A(t.Name)} is a plain number", value);
                            return;
                        }

                        Rat v = q.Unit == "%" ? q.Value * Rat.Of(1, 100) : q.Value;
                        if (integer && !v.IsInt)
                        {
                            string rounded = RoundHalfEven(v).ToString(CultureInfo.InvariantCulture);
                            Issue("not-whole", $"{q.Text} is not a whole number: {A(Describe())} is a whole number", value, rounded);
                            return;
                        }

                        decimal d = (decimal)v.N / (decimal)v.D;
                        if ((min is { } lo && d < lo) || (max is { } hi && d > hi)) Issue("type-mismatch", $"{q.Text} is not {A(Describe())}", value);
                        return;
                    }

                case WordNode w when t.NoteNames:
                    if (!TryNoteName(w.Text, out _)) Issue("type-mismatch", $"\"{w.Text}\" is not a Pitch: a number from 0 to 127 or a note name such as C2 or F#3", value);
                    return;
                case StringNode s:
                    Issue("type-mismatch", $"\"{s.Value}\" is text where {A(Describe())} is expected: write a number", value);
                    return;
                case CallNode { Name: "random" or "choose" or "chance" } c:
                    Call(c);
                    return;
                case MethodNode or WordNode:
                    return;   // a stream value (name.next()) or a name: the evaluation resolves it
                default:
                    if (value is RangeNode or ListNode or BoolNode) Issue("type-mismatch", $"A {Describe()} is expected", value);
                    return;
            }
        }

        private void Quantity(NoteParam prm, NoteTypeInfo t, Node value)
        {
            List<string> dimensions = t.Kind == "union" ? [.. t.Of] : [t.Name];
            switch (value)
            {
                case QuantityNode { Unit: null } q:
                    if (t.Name == "Interval") return;   // a bare number is semitones
                    issues.Add(new PipeIssue("missing-unit", $"{q.Text} needs a unit: {A(t.Name)} is written like {ExampleOf(t)}", value.Start, value.End));
                    return;
                case QuantityNode q:
                    {
                        string? dimension = NoteCatalog.DimensionOf(q.Unit!);
                        if (dimension is null || !dimensions.Contains(dimension))
                            Issue("wrong-dimension", $"{q.Text} is {(dimension is null ? "not a known unit" : DescribeDimension(dimension))}, {A(t.Name)} is expected ({ExampleOf(t)})", value);
                        else if (dimension == "Interval" && !(q.Unit == "st" ? q.Value : q.Unit == "oct" ? q.Value * Rat.Of(12) : q.Value * Rat.Of(1, 100)).IsInt)
                            Issue("fractional-interval", $"{q.Text} is not a whole number of semitones: note events cannot carry microtonal pitches yet (pitch bend is not supported by this operator)", value);
                        return;
                    }

                case StringNode s:
                    Issue("type-mismatch", $"\"{s.Value}\" is text where {A(t.Name)} is expected ({ExampleOf(t)})", value);
                    return;
                case CallNode { Name: "random" or "choose" or "chance" } c:
                    Call(c);
                    return;
                case MethodNode or WordNode:
                    return;
                default:
                    if (value is RangeNode or ListNode or BoolNode) Issue("type-mismatch", $"{A(t.Name)} is expected ({ExampleOf(t)})", value);
                    return;
            }
        }

        private static string DescribeDimension(string dimension) => dimension switch
        {
            "Interval" => "a pitch distance",
            "Length" => "a musical length",
            "Time" => "a time",
            "Percent" => "a percentage",
            _ => dimension,
        };

        private static string ExampleOf(NoteTypeInfo t) => t.Name switch
        {
            "Interval" => "+7st, -1oct, 50cents",
            "Length" => "1bar, 0.5beat",
            "Time" => "250ms, 1.5s",
            "Duration" => "0.5beat, 2bars, 250ms, 1.5s",
            _ => t.Name,
        };
    }
}
