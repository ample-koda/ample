using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>One parameter of a helper.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The declared type as written.</param>
/// <param name="Default">The default as written, or null.</param>
public sealed record HelperParamInfo(string Name, string Type, string? Default);

/// <summary>What the editors need to know about a declaration of the file: a helper, a type alias or a constant.</summary>
/// <param name="Name">The name as declared.</param>
/// <param name="Kind">helper, type or const.</param>
/// <param name="Dynamic">Whether it is declared dynamic.</param>
/// <param name="Effect">pure or dynamic (what the body is inferred to be).</param>
/// <param name="Params">The parameters.</param>
/// <param name="Returns">The declared return type, or null.</param>
/// <param name="Line">The 0-based line of the declaration.</param>
public sealed record HelperInfo(string Name, string Kind, bool Dynamic, string Effect, IReadOnlyList<HelperParamInfo> Params, string? Returns, int Line)
{
    /// <summary>Gets whether the helper has a baked value (dynamic helper f() -> Int bake as 5 { ... }).</summary>
    public bool Baked { get; init; }

    /// <summary>Gets what the helper depends on, with its callees: master-seed (stable seeds), tempo (times in seconds), random (anything that draws random numbers), clock, os-random. Dependencies decide what a bake must record; only the dynamic effect decides the stability of a song.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>A problem found in a declaration, with the line of the declaration (offsets of the issue are inside its text).</summary>
/// <param name="Line">The 0-based line of the declaration.</param>
/// <param name="Issue">The issue.</param>
public sealed record HelperIssue(int Line, PipeIssue Issue);

/// <summary>What a block property expects: its name, the musical type (Program, Gain, Time, Percent, Number ...), the unit and default of the template, and the range.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Type">The type the value must have.</param>
/// <param name="Unit">The unit of the template (dB, ms, %, Hz ...), or null.</param>
/// <param name="Default">The default as written in the template, which also says how a value is written back.</param>
/// <param name="Min">The lowest value in the unit of the template, or null.</param>
/// <param name="Max">The highest value in the unit of the template, or null.</param>
public sealed record PropertyShape(string Name, string Type, string? Unit, string? Default, double? Min, double? Max);

/// <summary>The resolved value of another block's property, or why it cannot be read.</summary>
/// <param name="Text">The value as written, or null.</param>
/// <param name="Error">The problem, or null.</param>
public sealed record PropertyRead(string? Text, string? Error);

/// <summary>The outcome of evaluating a property expression.</summary>
/// <param name="Value">The value as the block understands it (what is written back), or null on errors.</param>
/// <param name="Type">The type of the property.</param>
/// <param name="Dynamic">Whether a volatile value took part.</param>
/// <param name="DynamicInputs">The volatile values that were read.</param>
/// <param name="Errors">The errors.</param>
/// <param name="Warnings">The warnings and infos.</param>
/// <param name="State">expression, baked, override, stale or dirty.</param>
/// <param name="BakedText">The baked value as written (for a baked expression), or null.</param>
/// <param name="SourceText">What the expression resolves to now (for a baked expression), or null.</param>
public sealed record PropertyResult(string? Value, string Type, bool Dynamic, IReadOnlyList<string> DynamicInputs, IReadOnlyList<PipeIssue> Errors, IReadOnlyList<PipeIssue> Warnings, string State, string? BakedText, string? SourceText)
{
    /// <summary>Gets whether a volatile value was baked on purpose and so did not run.</summary>
    public bool BakedDynamic { get; init; }

    /// <summary>Gets the recipe hash of the expression (8 hex digits) a value bake is signed with.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Gets the expression itself, without the bake around it.</summary>
    public string ExpressionText { get; init; } = string.Empty;
}

/// <summary>The limits of one evaluation of helper code (docs/language/14, section 6): the song may set them in <c>settings { limits.fuel N }</c>, never above the caps of the server.</summary>
/// <param name="LoopIterations">The most iterations of one loop.</param>
/// <param name="WhileDefault">The max of a while that does not say one.</param>
/// <param name="StackDepth">The deepest call of helpers.</param>
/// <param name="Fuel">The total budget of evaluation steps.</param>
/// <param name="MemoryMb">The memory the notes made by one evaluation may take together, in megabytes (an estimate of 64 bytes per note).</param>
public sealed record LangLimits(int LoopIterations = 10_000, int WhileDefault = 256, int StackDepth = 64, long Fuel = 2_000_000, int MemoryMb = 64)
{
    /// <summary>Gets the caps of the server: no song can raise a limit above these.</summary>
    public static LangLimits Caps { get; } = new(1_000_000, 10_000, 256, 100_000_000, 1024);

    /// <summary>Returns these limits with every value brought to at least 1 and at most the cap.</summary>
    /// <returns>The limits.</returns>
    public LangLimits Clamped() => new(Math.Clamp(LoopIterations, 1, Caps.LoopIterations), Math.Clamp(WhileDefault, 1, Caps.WhileDefault), Math.Clamp(StackDepth, 1, Caps.StackDepth), Math.Clamp(Fuel, 1, Caps.Fuel), Math.Clamp(MemoryMb, 1, Caps.MemoryMb));

    /// <summary>The effective value of every limit and where it comes from: the default, the song (settings { limits.fuel N }) or the cap of the server.</summary>
    /// <param name="settings">The settings as written.</param>
    /// <returns>Key, effective value, source and cap.</returns>
    public static IReadOnlyList<(string Key, long Effective, string Source, long Cap)> Describe(IReadOnlyDictionary<string, string> settings)
    {
        LangLimits defaults = new(), caps = Caps, effective = From(settings);
        (string, long, long, long)[] rows = [("loop-iterations", effective.LoopIterations, defaults.LoopIterations, caps.LoopIterations), ("while-default", effective.WhileDefault, defaults.WhileDefault, caps.WhileDefault), ("stack-depth", effective.StackDepth, defaults.StackDepth, caps.StackDepth), ("fuel", effective.Fuel, defaults.Fuel, caps.Fuel), ("memory", effective.MemoryMb, defaults.MemoryMb, caps.MemoryMb)];
        return [.. rows.Select(r => (r.Item1, r.Item2, settings.ContainsKey("limits." + r.Item1) ? (r.Item2 == r.Item4 ? "server-cap" : "song") : "default", r.Item4))];
    }

    /// <summary>The limits a song asks for that are above the caps of the server (the cap is used): setting, requested value and cap.</summary>
    /// <param name="settings">The settings as written.</param>
    /// <returns>The overruns.</returns>
    public static IReadOnlyList<(string Key, long Requested, long Cap)> Overruns(IReadOnlyDictionary<string, string> settings)
    {
        List<(string, long, long)> found = [];
        void Check(string key, long cap)
        {
            if (settings.TryGetValue("limits." + key, out string? raw) && long.TryParse(raw.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out long v) && v > cap) found.Add(("limits." + key, v, cap));
        }

        Check("loop-iterations", Caps.LoopIterations);
        Check("while-default", Caps.WhileDefault);
        Check("stack-depth", Caps.StackDepth);
        Check("fuel", Caps.Fuel);
        Check("memory", Caps.MemoryMb);
        return found;
    }

    /// <summary>Reads the limits of a song from its settings (limits.loop-iterations, limits.while-default, limits.stack-depth, limits.fuel, limits.memory).</summary>
    /// <param name="settings">The settings as written.</param>
    /// <returns>The limits, clamped to the caps.</returns>
    public static LangLimits From(IReadOnlyDictionary<string, string> settings)
    {
        static long Read(IReadOnlyDictionary<string, string> s, string key, long fallback) => s.TryGetValue("limits." + key, out string? raw) && long.TryParse(raw.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : fallback;
        LangLimits d = new();
        return new LangLimits((int)Math.Min(int.MaxValue, Read(settings, "loop-iterations", d.LoopIterations)), (int)Math.Min(int.MaxValue, Read(settings, "while-default", d.WhileDefault)), (int)Math.Min(int.MaxValue, Read(settings, "stack-depth", d.StackDepth)), Read(settings, "fuel", d.Fuel), (int)Math.Min(int.MaxValue, Read(settings, "memory", d.MemoryMb))).Clamped();
    }
}

public static partial class NotePipeline
{
    // ---------- the syntax tree of the language layer

    private sealed record BinaryNode(string Op, Node Left, Node Right, int Start, int End) : Node(Start, End);

    private sealed record UnaryNode(string Op, Node Operand, int Start, int End) : Node(Start, End);

    private sealed record ListNode(List<Node> Items, int Start, int End) : Node(Start, End);

    private sealed record BoolNode(bool Value, int Start, int End) : Node(Start, End);

    private sealed record FieldNode(Node Target, string Name, int Start, int End) : Node(Start, End);

    private sealed record RangeExprNode(Node From, Node To, bool Inclusive, int Start, int End) : Node(Start, End);

    private sealed record BakeValueNode(Node Expr, Node Value, string? Hash, bool Override, int AsStart, int Start, int End) : Node(Start, End);

    private sealed record ValueNode(PipeValue Value, string Recipe, int Start, int End) : Node(Start, End);

    internal sealed record StmtBlock(List<Node> Statements, int Start, int End) : Node(Start, End);

    private sealed record LetStmt(string Name, string? Type, Node Value, bool Mutable, int Start, int End) : Node(Start, End);

    private sealed record AssignStmt(string Name, Node Value, int Start, int End) : Node(Start, End);

    private sealed record IfStmt(List<(Node Cond, StmtBlock Body)> Branches, StmtBlock? Otherwise, int Start, int End) : Node(Start, End);

    private sealed record ForStmt(string Var, Node From, Node? To, bool Inclusive, StmtBlock Body, int Start, int End) : Node(Start, End);

    private sealed record RepeatStmt(Node Count, StmtBlock Body, int Start, int End) : Node(Start, End);

    private sealed record WhileStmt(Node Cond, Node? Max, bool Stop, StmtBlock Body, int Start, int End) : Node(Start, End);

    private sealed record ReturnStmt(Node? Value, int Start, int End) : Node(Start, End);

    private sealed record EmitStmt(Node Value, int Start, int End) : Node(Start, End);

    private sealed record ExprStmt(Node Value, int Start, int End) : Node(Start, End);

    /// <summary>log, debug, trace, warn and assert, in both spellings (<c>log "x"</c> and <c>log("x")</c> are the same node).</summary>
    private sealed record ObserveStmt(string Kind, List<Node> Positional, List<NamedNode> Named, bool CallForm, int Start, int End) : Node(Start, End)
    {
        public IEnumerable<Node> Args => Positional.Concat(Named);

        /// <summary>Whether the statement decides the outcome (fail, require, throw): it is part of the meaning, unlike an observation.</summary>
        public bool Semantic => Kind is "fail" or "require" or "throw";
    }

    private sealed record CatchClause(string? Type, string Name, StmtBlock Body, int Start, int End) : Node(Start, End);

    /// <summary><c>try { } catch (Type name) { } finally { }</c>: at least one catch or a finally.</summary>
    private sealed record TryStmt(StmtBlock Body, List<CatchClause> Catches, StmtBlock? Finally, int Start, int End) : Node(Start, End);

    private sealed record RecordField(string Name, Node Value, int Start, int End);

    private sealed record RecordNode(List<RecordField> Fields, int Start, int End) : Node(Start, End);

    internal sealed record ParamNode(string Name, string Type, Node? Default, int Start, int End);

    internal sealed record HelperDeclNode(string Name, bool Dynamic, List<ParamNode> Params, string? Returns, Node? Baked, StmtBlock Body, int Start, int End, int NameStart, int NameEnd) : Node(Start, End);

    internal sealed record TypeDeclNode(string Name, List<string> Members, int Start, int End) : Node(Start, End);

    internal sealed record ErrorFieldDecl(bool Dynamic, string Type, string Name, int Start, int End);

    /// <summary><c>error Name extends Parent { dynamic Int field, String other }</c>: a kind of error with typed fields (file-scoped, no methods).</summary>
    internal sealed record ErrorDeclNode(string Name, string? Parent, List<ErrorFieldDecl> Fields, int Start, int End) : Node(Start, End);

    internal sealed record ConstDeclNode(string Name, string? Type, Node Value, int Start, int End, int NameStart, int NameEnd) : Node(Start, End);

    [GeneratedRegex(@"^(?:\(|[A-Za-z_][\w-]*\s*\(|[A-Za-z_][\w-]*\.[A-Za-z_][\w-]*(?:$|\s*[*/+%)-])|[A-Za-z_][\w-]*\s+[*/+%-]\s+)", RegexOptions.None, 1000)]
    private static partial Regex PropertyExpressionStart();

    [GeneratedRegex(@"^(?:auto|lfo|env|fadein|fadeout)\s*\(", RegexOptions.None, 1000)]
    private static partial Regex AutomationStart();

    /// <summary>Whether the value of a numeric block property is an expression (a call, a bake, a property of another block, parentheses or arithmetic on a name) and not a plain value like -9, 30% or 10 ms.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>True for an expression.</returns>
    public static bool IsPropertyExpression(string value)
    {
        string v = StripComments(value).Trim();
        if (AutomationStart().IsMatch(v)) return false;   // automation curves and fades are written by their own editors, they are not formulas
        return v.StartsWith("bake ", StringComparison.Ordinal) || PropertyExpressionStart().IsMatch(v);
    }

    /// <summary>Evaluates the expression of a block property (gain helperGain(theme.notes), program choose([0, 11], seed=...), gain bake f() as -4dB) to the value the block takes.</summary>
    /// <param name="text">The expression.</param>
    /// <param name="context">What the evaluation can ask for (helpers, sources, other properties, the clock).</param>
    /// <param name="shape">What the property expects.</param>
    /// <returns>The result.</returns>
    public static PropertyResult EvaluateProperty(string text, Context context, PropertyShape shape)
    {
        List<PipeIssue> errors = [], warnings = [];
        string clean = StripComments(text);
        Node ast;
        try
        {
            ast = Typed(new ExprParser(clean, Tokenize(clean)).Parse());
        }
        catch (SyntaxError e)
        {
            errors.Add(new PipeIssue(e.Id, e.Message, e.Start, e.End));
            return new PropertyResult(null, shape.Type, false, [], errors, warnings, "expression", null, null);
        }

        Rat tempo = ParseDecimal(context.Tempo) ?? Rat.Of(120);
        Interp lang = new(context, tempo, errors, warnings, clean);
        string? value = lang.RunValue(ast, shape, out _);
        lang.Legacy.Finish();
        string state = "expression";
        string? baked = null, source = null;
        if (lang.LastBake is { } info)
        {
            baked = info.BakedText;
            source = info.CurrentText;
            state = info.Dirty ? "dirty" : info.Stale ? "stale" : info.Override ? "override" : "baked";
        }
        else if (ast is BakeValueNode)
        {
            state = "baked";
        }

        Node inner = ast is BakeValueNode bake ? bake.Expr : ast;
        string hash = string.Empty, expressionText = string.Empty;
        try
        {
            hash = lang.RecipeHash(inner);
            expressionText = clean[inner.Start..Math.Min(clean.Length, inner.End)].Trim();
        }
        catch (ArgumentException)
        {
            // an expression that did not parse well enough has no recipe
        }

        return new PropertyResult(errors.Count == 0 ? value : null, shape.Type, lang.Dynamic, lang.DynamicInputs, errors, warnings, state, baked, source) { BakedDynamic = lang.BakedDynamic, Hash = hash, ExpressionText = expressionText };
    }

    /// <summary>Evaluates helpers that take no parameters (name()) and gives each value as it is written after "bake as". A helper that cannot be evaluated is left out.</summary>
    /// <param name="context">What the evaluation can ask for (helpers, the clock, capabilities).</param>
    /// <param name="names">The helpers.</param>
    /// <returns>The values.</returns>
    public static IReadOnlyList<(string Name, string Value)> DynamicHelperValues(Context context, IEnumerable<string> names) => ValuesOf(context, names.Select(n => n + "()"), names);

    /// <summary>Evaluates expressions that give a value (not notes) and gives each as it is written after "bake as". An expression that cannot be evaluated here (it reads another block, or it gives notes) is left out.</summary>
    /// <param name="context">What the evaluation can ask for (helpers, the clock, capabilities).</param>
    /// <param name="expressions">The expressions as written, like hourShift(3).</param>
    /// <param name="labels">What to call each in the result (default: the expression).</param>
    /// <returns>The values.</returns>
    public static IReadOnlyList<(string Name, string Value)> ValuesOf(Context context, IEnumerable<string> expressions, IEnumerable<string>? labels = null)
    {
        List<(string Name, string Value)> values = [];
        Rat tempo = ParseDecimal(context.Tempo) ?? Rat.Of(120);
        string[] names = [.. labels ?? expressions];
        int i = 0;
        foreach (string text in expressions)
        {
            string label = names[i++];
            List<PipeIssue> errors = [], warnings = [];
            Interp lang = new(context, tempo, errors, warnings, text);
            try
            {
                string? value = lang.EvalText(Typed(new ExprParser(text, Tokenize(text)).Parse()));
                if (value is not null && errors.Count == 0) values.Add((label, value));
            }
            catch (SyntaxError)
            {
                // an expression that does not read as an expression is left out
            }
        }

        return values;
    }

    /// <summary>A value inside the expression of a block that depends on the clock and can be frozen where it stands: the range in the text and the value it had.</summary>
    /// <param name="Start">The offset where the value starts.</param>
    /// <param name="End">The offset where it ends.</param>
    /// <param name="Value">The value as it is written after "bake as".</param>
    public sealed record BakeSite(int Start, int End, string Value);

    /// <summary>Finds the values in the expression of a block (the notes of a clip, a property) that read the clock directly (currentTime().second % 4) or call a dynamic helper with parameters (hourShift(3)), evaluates them at the clock of the context, and says which ones can be written as "bake VALUE-EXPRESSION as VALUE". Values that give notes, or that read other blocks, are reported as left.</summary>
    /// <param name="text">The expression as written.</param>
    /// <param name="context">What the evaluation can ask for (helpers, the clock).</param>
    /// <param name="includeNoParameterHelpers">Whether calls of dynamic helpers without parameters are sites too (for one block: the helper itself stays dynamic for the other blocks).</param>
    /// <returns>The sites and the calls that stay dynamic.</returns>
    public static (IReadOnlyList<BakeSite> Sites, IReadOnlyList<string> Left) BakeSitesIn(string text, Context context, bool includeNoParameterHelpers)
    {
        List<BakeSite> sites = [];
        List<string> left = [];
        string clean = StripComments(text);
        Node ast;
        try
        {
            ast = Typed(new ExprParser(clean, Tokenize(clean)).Parse());
        }
        catch (SyntaxError)
        {
            return (sites, left);
        }

        HelperTable table = context.Helpers ?? HelperTable.Empty;
        Rat tempo = ParseDecimal(context.Tempo) ?? Rat.Of(120);
        List<PipeIssue> errors = [], warnings = [];
        Interp lang = new(context, tempo, errors, warnings, clean);
        static bool IsClock(CallNode c) => c.Name.Equals("currentTime", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("currentDate", StringComparison.OrdinalIgnoreCase);
        static bool IsRandom(CallNode c) => c.Name.Equals("osRandom", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("environment", StringComparison.OrdinalIgnoreCase);
        static bool Balanced(string s)
        {
            int depth = 0;
            bool inString = false;
            foreach (char ch in s)
            {
                if (ch == '"') inString = !inString;
                else if (!inString && ch is '(' or '[' or '{') depth++;
                else if (!inString && ch is ')' or ']' or '}' && --depth < 0) return false;
            }

            return depth == 0 && !inString;
        }

        bool Wanted(Node n) => Descendants(n, includeObservation: false).Prepend(n).OfType<CallNode>()
            .Any(c => IsClock(c) || (table.IsDynamicHelper(c.Name) && (includeNoParameterHelpers || table.ParamCount(c.Name) > 0)));
        void Walk(Node n)
        {
            if (n is BakeValueNode || !Wanted(n)) return;
            bool container = n is PipelineNode or ListNode or NamedNode or StmtBlock;
            if (!container && !Descendants(n).Prepend(n).OfType<CallNode>().Any(IsRandom))
            {
                string? value = lang.EvalText(n);
                int end = Math.Min(clean.Length, n.End);
                if (value is not null && n.Start >= 0 && n.Start < end && Balanced(clean[n.Start..end]))
                {
                    sites.Add(new BakeSite(n.Start, end, value));
                    return;
                }

                if (value is null && n is CallNode c && table.IsDynamicHelper(c.Name)) left.Add(c.Name + "(...)");
            }

            foreach (Node child in Children(n)) Walk(child);
        }

        Walk(ast);
        return (sites, left);
    }

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "debug", "trace", "warn", "assert", "require", "throw", "fail", "try", "catch", "finally", "error",
    };

    /// <summary>The name the quick fix of AMP6016 gives the declaration: the word with what it is (logHelper, warnValue, failError), made unique in the file.</summary>
    private static string ReservedRename(Node node, string name, HashSet<string> declared)
    {
        string stem = name + (node switch { ConstDeclNode => "Value", ErrorDeclNode => "Error", _ => "Helper" });
        string candidate = stem;
        for (int i = 2; declared.Contains(candidate); i++) candidate = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return candidate;
    }

    private static string ReservedMessage(string name) =>
        $"\"{name}\" is a word that AMPLE reserves for statements (log, debug, trace, warn, assert, require, throw, fail, try, catch, finally, error): "
        + "a statement written with it will not call this helper or constant, so give it another name";

    private static readonly string[] Volatile = ["currentTime", "currentDate", "osRandom", "environment"];

    private static readonly string[] LangFunctions = ["Error", "RequirementError", "Int", "Float", "Round", "Floor", "Ceil", "Truncate", "count",
        "span", "min", "max", "abs", "at", "len", "Note", "emptyNotes", "String", "sprintf", "currentTime", "currentDate", "osRandom", "environment", "random", "choose", "chance", "stable", "random-seed"];

    private static readonly string[] BuiltinTypes = ["Error", "RequirementError", "Record", "Int", "Float", "Number", "Bool", "String", "Length", "Time", "Duration", "Interval",
        "Gain", "Percent", "Notes", "Note", "RandomSource", "Seed", "StableSeed", "RandomStream", "Text"];

    private static IEnumerable<Node> Children(Node node)
    {
        switch (node)
        {
            case PipelineNode p: yield return p.Source; foreach (Node s in p.Steps) yield return s; break;
            case CallNode c: foreach (Node a in c.Args) yield return a; break;
            case NamedNode n: yield return n.Value; break;
            case MethodNode m: yield return m.Target; foreach (Node a in m.Args) yield return a; break;
            case BinaryNode b: yield return b.Left; yield return b.Right; break;
            case TernaryNode t: yield return t.Cond; yield return t.Then; yield return t.Otherwise; break;
            case UnaryNode u: yield return u.Operand; break;
            case ListNode l: foreach (Node i in l.Items) yield return i; break;
            case FieldNode f: yield return f.Target; break;
            case RangeExprNode r: yield return r.From; yield return r.To; break;
            case BakeValueNode bv: yield return bv.Expr; yield return bv.Value; break;
            case StmtBlock sb: foreach (Node s in sb.Statements) yield return s; break;
            case LetStmt ls: yield return ls.Value; break;
            case AssignStmt a: yield return a.Value; break;
            case IfStmt i: foreach ((Node cond, StmtBlock body) in i.Branches) { yield return cond; yield return body; } if (i.Otherwise is not null) yield return i.Otherwise; break;
            case ForStmt fs: yield return fs.From; if (fs.To is not null) yield return fs.To; yield return fs.Body; break;
            case RepeatStmt rs: yield return rs.Count; yield return rs.Body; break;
            case WhileStmt ws: yield return ws.Cond; if (ws.Max is not null) yield return ws.Max; yield return ws.Body; break;
            case ReturnStmt rt: if (rt.Value is not null) yield return rt.Value; break;
            case TryStmt ts: yield return ts.Body; foreach (CatchClause cc in ts.Catches) yield return cc.Body; if (ts.Finally is not null) yield return ts.Finally; break;
            case EmitStmt em: yield return em.Value; break;
            case ExprStmt ex: yield return ex.Value; break;
            case ObserveStmt os: foreach (Node a in os.Args) yield return a; break;
            case RecordNode rn: foreach (RecordField rf in rn.Fields) yield return rf.Value; break;
            case HelperDeclNode h: foreach (ParamNode pn in h.Params) if (pn.Default is not null) yield return pn.Default; if (h.Baked is not null) yield return h.Baked; yield return h.Body; break;
            case ConstDeclNode cd: yield return cd.Value; break;
        }
    }

    /// <summary>The text of a declaration with its observation and assert statements blanked (same offsets), or null when it has none.</summary>
    private static string? BlankObservation(Node root, string clean)
    {
        List<ObserveStmt> found = [.. Descendants(root).OfType<ObserveStmt>().Where(s => !s.Semantic)];
        if (found.Count == 0) return null;
        char[] chars = clean.ToCharArray();
        foreach (ObserveStmt s in found)
        {
            for (int i = Math.Max(0, s.Start); i < Math.Min(chars.Length, s.End); i++)
            {
                if (chars[i] != '\n') chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>Reads one observation statement written on one line (the audio process uses the same parser as the Notes language): its kind and the text of its arguments.</summary>
    /// <param name="statement">The statement.</param>
    /// <param name="problem">What is wrong with it (also a feature that is off), or null.</param>
    /// <returns>The kind, the positional arguments and the named ones as written, or null.</returns>
    internal static (string Kind, string[] Positional, (string Name, string Text)[] Named)? ReadObservation(string statement, out string? problem)
    {
        problem = null;
        try
        {
            string clean = StripComments(statement).Trim();
            ObserveStmt? parsed = new ExprParser(clean, Tokenize(clean)).TryObservation();
            if (parsed is null) { problem = "This is not an observation statement."; return null; }
            return (parsed.Kind, [.. parsed.Positional.Select(n => clean[n.Start..n.End])], [.. parsed.Named.Select(n => (n.Name, clean[n.Value.Start..n.Value.End]))]);
        }
        catch (SyntaxError e)
        {
            problem = e.Message;
            return null;
        }
    }

    /// <summary>One statement of an audio process as the shared parser reads it: the line in the body (0 is the first), the kind (<c>let</c>, <c>emit</c>, <c>observe</c>, <c>if</c>, <c>elseif</c>, <c>else</c>, <c>end</c>, or <c>other</c> for a statement an audio process does not have) and its text (the lines of a statement joined by one space, without the keyword for <c>let</c> and <c>emit</c>, the condition for <c>if</c> and <c>elseif</c>).</summary>
    internal sealed record AudioStatement(int BodyLine, string Kind, string Text);

    /// <summary>Reads the statements of the process of an audio component with the shared parser.</summary>
    /// <param name="body">The lines of the process, joined by newlines.</param>
    /// <returns>The statements in order, or null when the text does not parse (the caller reports it line by line).</returns>
    internal static List<AudioStatement>? ReadAudioProcess(string body)
    {
        const string head = "helper __p() {\n";
        string wrapped = head + StripComments(body) + "\n}";
        HelperDeclNode helper;
        try
        {
            if (new ExprParser(wrapped, Tokenize(wrapped)) { AudioProcess = true }.ParseDeclaration() is not HelperDeclNode h) return null;
            helper = h;
        }
        catch (SyntaxError)
        {
            return null;
        }

        List<AudioStatement> result = [];
        int LineOf(int offset) => wrapped.AsSpan(0, Math.Min(offset, wrapped.Length)).Count('\n') - 1;
        string Slice(int start, int end) => string.Join(' ', wrapped[start..end].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        void Walk(StmtBlock block)
        {
            foreach (Node s in block.Statements)
            {
                int line = LineOf(s.Start);
                switch (s)
                {
                    case LetStmt { Mutable: false } when wrapped.AsSpan(s.Start).StartsWith("let", StringComparison.Ordinal):
                        result.Add(new AudioStatement(line, "let", Slice(s.Start + 3, s.End)));
                        break;
                    case EmitStmt:
                        result.Add(new AudioStatement(line, "emit", Slice(s.Start + 4, s.End)));
                        break;
                    case ObserveStmt { Semantic: false }:
                        result.Add(new AudioStatement(line, "observe", Slice(s.Start, s.End)));
                        break;
                    case ObserveStmt { Kind: "fail" or "require" } check:
                        result.Add(new AudioStatement(line, check.Kind, Slice(s.Start, s.End)));
                        break;
                    case TryStmt t:
                        result.Add(new AudioStatement(line, "try", string.Empty));
                        Walk(t.Body);
                        foreach (CatchClause c in t.Catches)
                        {
                            result.Add(new AudioStatement(LineOf(c.Start), "catch", c.Type is null ? c.Name : c.Type + " " + c.Name));
                            Walk(c.Body);
                        }

                        if (t.Finally is { } always)
                        {
                            result.Add(new AudioStatement(LineOf(always.Start), "finally", string.Empty));
                            Walk(always);
                        }

                        result.Add(new AudioStatement(LineOf(t.End - 1), "end", string.Empty));
                        break;
                    case IfStmt i:
                        for (int b = 0; b < i.Branches.Count; b++)
                        {
                            (Node cond, StmtBlock branch) = i.Branches[b];
                            int keyword = wrapped.LastIndexOf("if", cond.Start - 1, StringComparison.Ordinal) + 2;
                            result.Add(new AudioStatement(LineOf(cond.Start), b == 0 ? "if" : "elseif", Slice(keyword, branch.Start)));
                            Walk(branch);
                        }

                        if (i.Otherwise is { } otherwise)
                        {
                            result.Add(new AudioStatement(LineOf(otherwise.Start), "else", string.Empty));
                            Walk(otherwise);
                        }

                        result.Add(new AudioStatement(LineOf(i.End - 1), "end", string.Empty));
                        break;
                    default:
                        result.Add(new AudioStatement(line, "other", Slice(s.Start, s.End)));
                        break;
                }
            }
        }

        Walk(helper.Body);
        return result;
    }

    /// <summary>The statements of a block (the process of a component) with their observation and assert statements blanked, or null when there are none or the text does not parse.</summary>
    /// <param name="body">The statements.</param>
    /// <returns>The blanked text (same length and lines), or null.</returns>
    internal static string? BlankObservationStatements(string body)
    {
        const string head = "helper __p() {\n";
        string clean = StripComments(body);
        string wrapped = head + clean + "\n}";
        using IDisposable scope = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Assert | SyntaxFeatures.Record);   // a hash reads what is written, whatever is switched on
        Node node;
        try
        {
            node = new ExprParser(wrapped, Tokenize(wrapped)).ParseDeclaration();
        }
        catch (SyntaxError)
        {
            return null;
        }

        List<ObserveStmt> found = [.. Descendants(node).OfType<ObserveStmt>().Where(s => !s.Semantic)];
        if (found.Count == 0) return null;
        char[] chars = clean.ToCharArray();
        foreach (ObserveStmt s in found)
        {
            for (int i = Math.Max(0, s.Start - head.Length); i < Math.Min(chars.Length, s.End - head.Length); i++)
            {
                if (chars[i] != '\n') chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    private static IEnumerable<Node> Descendants(Node node) => Descendants(node, includeObservation: true);

    private static IEnumerable<Node> Descendants(Node node, bool includeObservation)
    {
        if (!includeObservation && node is ObserveStmt { Semantic: false }) yield break;
        foreach (Node child in Children(node))
        {
            yield return child;
            foreach (Node deeper in Descendants(child, includeObservation)) yield return deeper;
        }
    }

    private static string Collapse(string text) => Regex.Replace(StripComments(text), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

    // ---------- the helpers of a file

    /// <summary>The declarations of a file (helpers, type aliases, constants): collected first and resolved afterwards, so the order in the file does not matter. The effect of every helper (pure or dynamic) is worked out from its body and the helpers it calls.</summary>
    public sealed partial class HelperTable
    {
        private readonly Dictionary<string, Decl> helpers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TypeDeclNode> types = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ErrorDeclNode> errors = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Decl> constants = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);

        private sealed record Decl(Node Node, string Name, string Hash, string Text, int Line, bool IsDynamic)
        {
            /// <summary>The hash of the whole text, observation and assert statements included (the source tier; <see cref="Hash"/> is the semantic tier that the recipe uses).</summary>
            public string SourceHash { get; init; } = Hash;

            /// <summary>The text without observation and assert statements (blanked, same offsets).</summary>
            public string SemanticText { get; init; } = Text;

            public string Effect { get; set; } = "pure";
        }

        /// <summary>Gets an empty table.</summary>
        public static HelperTable Empty { get; } = new();

        /// <summary>Gets what the editors need to know about every declaration.</summary>
        public IReadOnlyList<HelperInfo> Infos { get; private set; } = [];

        /// <summary>The places where a helper or constant reads a volatile value: the 0-based line of the declaration, the range of the call inside its text, the call name and the capability it needs.</summary>
        /// <returns>The uses.</returns>
        public IReadOnlyList<(int Line, int Start, int End, string Call, string Capability)> VolatileUses()
        {
            List<(int, int, int, string, string)> found = [];
            foreach (Decl d in helpers.Values.Concat(constants.Values))
            {
                foreach (CallNode c in Descendants(d.Node, includeObservation: false).OfType<CallNode>())
                {
                    string lower = c.Name.ToLowerInvariant();
                    if (lower is "currenttime" or "currentdate") found.Add((d.Line, c.Start, c.NameEnd, c.Name, "clock"));
                    else if (lower == "osrandom") found.Add((d.Line, c.Start, c.NameEnd, c.Name, "os-random"));
                    else if (lower == "environment") found.Add((d.Line, c.Start, c.NameEnd, c.Name, "environment"));
                }
            }

            return found;
        }

        /// <summary>Whether a name is a constant of the file.</summary>
        /// <param name="name">The name.</param>
        /// <returns>True for a constant.</returns>
        public bool IsConstant(string name) => constants.ContainsKey(name);

        /// <summary>Gets the names of the type aliases of the file.</summary>
        public IEnumerable<string> TypeNames => types.Values.Select(t => t.Name);

        /// <summary>Gets whether there is no declaration.</summary>
        public bool IsEmpty => helpers.Count == 0 && constants.Count == 0 && types.Count == 0;

        /// <summary>Gets the names of the helpers and constants as declared.</summary>
        public IEnumerable<string> Names => helpers.Values.Select(d => d.Name).Concat(constants.Values.Select(d => d.Name));

        /// <summary>Builds the table from the declarations of a file and reports what is wrong with them.</summary>
        /// <param name="sources">The text and the 0-based line of every declaration.</param>
        /// <param name="issues">Receives the problems.</param>
        /// <returns>The table.</returns>
        public static HelperTable Build(IEnumerable<(string Text, int Line)> sources, List<HelperIssue> issues)
            => Build(sources, issues, []);

        internal static HelperTable Build(
            IEnumerable<(string Text, int Line)> sources, List<HelperIssue> issues, IReadOnlyList<ComponentDefinition> components)
        {
            HelperTable table = new();
            foreach (ComponentDefinition component in components.Where(c => !c.IsAudio && c.PrivateProperties.Count > 0))
            {
                HashSet<string> used = ComponentHelper.UsedPrivate(component).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                table.observerProperties.TryAdd(component.HelperName,
                    component.PrivateProperties.Where(property => !used.Contains(property.Name)).ToDictionary(
                        property => property.Name, StringComparer.Ordinal));
            }
            List<Decl> all = [];
            // every name declared in the file (compared ignoring case), so the rename of a reserved name does not take one of them
            HashSet<string> declared = new(sources.Select(s => Regex.Match(StripComments(s.Text), @"^\s*(?:dynamic\s+)?(?:helper|let|error|type)\s+([A-Za-z_][\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(1)))
                .Where(m => m.Success).Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
            foreach ((string text, int line) in sources)
            {
                string clean = StripComments(text);
                Node node;
                try
                {
                    node = new ExprParser(clean, Tokenize(clean)).ParseDeclaration();
                }
                catch (SyntaxError e)
                {
                    issues.Add(new HelperIssue(line, new PipeIssue(e.Id, e.Message, e.Start, e.End)));
                    if (Regex.Match(clean, @"^\s*(?:dynamic\s+)?helper\s+([A-Za-z_][\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } broken) table.failed.Add(broken.Groups[1].Value);
                    continue;
                }

                string name = node switch { HelperDeclNode h => h.Name, TypeDeclNode t => t.Name, ErrorDeclNode e => e.Name, ConstDeclNode c => c.Name, _ => string.Empty };
                int nameStart = node switch { HelperDeclNode h => h.NameStart, ConstDeclNode c => c.NameStart, _ => node.Start };
                int nameEnd = node switch { HelperDeclNode h => h.NameEnd, ConstDeclNode c => c.NameEnd, _ => node.End };
                bool dup = node switch
                {
                    ErrorDeclNode => table.errors.ContainsKey(name) || table.types.ContainsKey(name) || name is "Error" or "RequirementError",
                    TypeDeclNode => table.types.ContainsKey(name) || table.errors.ContainsKey(name),
                    ConstDeclNode => table.constants.ContainsKey(name) || table.helpers.ContainsKey(name),
                    _ => table.helpers.ContainsKey(name) || table.constants.ContainsKey(name),
                };
                if (node is HelperDeclNode or ConstDeclNode or ErrorDeclNode && ReservedWords.Contains(name))
                    issues.Add(new HelperIssue(line, new PipeIssue("reserved-name", ReservedMessage(name), nameStart, nameEnd, ReservedRename(node, name, declared))));
                if (dup) { issues.Add(new HelperIssue(line, new PipeIssue("duplicate-helper", $"\"{name}\" is declared twice (names are compared ignoring case)", nameStart, nameEnd))); continue; }
                string full = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ample-helper/1\n" + Collapse(text))));
                string? blanked = BlankObservation(node, clean);
                string semantic = blanked is null ? full : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ample-helper/1\n" + Collapse(blanked))));
                Decl decl = new(node, name, semantic, text, line, node is HelperDeclNode { Dynamic: true }) { SourceHash = full, SemanticText = blanked ?? text };
                if (node is TypeDeclNode td) table.types[name] = td;
                else if (node is ErrorDeclNode ed) table.errors[name] = ed;
                else if (node is ConstDeclNode) table.constants[name] = decl;
                else table.helpers[name] = decl;
                all.Add(decl);
            }

            table.Analyze(all, issues);
            table.WorkOutVariables(all);
            return table;
        }

        internal bool TryHelper(string name, out HelperDeclNode node, out string hash, out string canonical, out string effect)
        {
            node = null!;
            hash = canonical = effect = string.Empty;
            if (!helpers.TryGetValue(name, out Decl? d)) return false;
            node = (HelperDeclNode)d.Node;
            hash = d.Hash;
            canonical = d.Name;
            effect = d.Effect;
            return true;
        }

        /// <summary>The text of a helper declaration (the offsets of its nodes belong to it), or null.</summary>
        internal string? TextOf(string name) => helpers.TryGetValue(name, out Decl? d) ? d.Text : null;

        internal int LineOf(string name) => helpers.TryGetValue(name, out Decl? d) ? Math.Max(0, d.Line) : 0;

        private readonly Dictionary<string, Dictionary<string, ComponentProperty>> observerProperties = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyDictionary<string, ComponentProperty>? ObservationProperties(string helper)
            => observerProperties.GetValueOrDefault(helper);

        internal bool TryConst(string name, out ConstDeclNode node, out string hash)
        {
            node = null!;
            hash = string.Empty;
            if (!constants.TryGetValue(name, out Decl? d)) return false;
            node = (ConstDeclNode)d.Node;
            hash = d.Hash;
            return true;
        }

        /// <summary>The error type with its chain (the type first, then its parents, ending in Error) and all fields, parents first; false when the name is not an error type.</summary>
        internal bool TryErrorSpec(string name, out List<string> chain, out List<ErrorFieldDecl> fields)
        {
            chain = [];
            fields = [];
            if (name.Equals("Error", StringComparison.Ordinal)) { chain = ["Error"]; return true; }
            if (name.Equals("RequirementError", StringComparison.Ordinal)) { chain = ["RequirementError", "Error"]; return true; }
            string? at = name;
            while (at is not null && errors.TryGetValue(at, out ErrorDeclNode? node))
            {
                if (chain.Contains(node.Name, StringComparer.OrdinalIgnoreCase)) return false;   // a cycle: reported by the declaration check
                chain.Add(node.Name);
                fields.InsertRange(0, node.Fields);
                at = node.Parent;
            }

            if (chain.Count == 0) return false;
            if (at is not null && at.Equals("RequirementError", StringComparison.Ordinal)) chain.Add("RequirementError");
            chain.Add("Error");
            return true;
        }

        internal bool IsErrorType(string name) => name is "Error" or "RequirementError" || errors.ContainsKey(name);

        /// <summary>Whether a type of an error (itself or a type that extends it) declares this field as dynamic: a value of that type may carry it.</summary>
        private bool MayHaveDynamicField(string type, string field)
        {
            if (!IsErrorType(type)) return false;   // a record, a number, notes ...: not an error, so no declared field
            foreach (ErrorDeclNode e in errors.Values)
            {
                if (TryErrorSpec(e.Name, out List<string> chain, out List<ErrorFieldDecl> fields) && chain.Contains(type, StringComparer.OrdinalIgnoreCase)
                    && fields.Any(f => f.Dynamic && f.Name == field)) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a helper reads a field that an error declaration marked dynamic (plan D32). The type of the receiver decides when it is known
        /// (a parameter, a typed or constructed variable, a catch variable, an alias of one of them): a record or an unrelated error does not count,
        /// a type that a dynamic field can belong to does. When the receiver's type cannot be established the field name decides: it can only ask for dynamic once too often, never once too little.
        /// </summary>
        private bool ReadsDynamicField(Decl d, HashSet<string> dynamicFields)
        {
            Dictionary<string, string?> types = new(StringComparer.Ordinal);
            void Note(string name, string? type)
            {
                if (!types.TryGetValue(name, out string? known)) types[name] = type;
                else if (!string.Equals(known, type, StringComparison.OrdinalIgnoreCase)) types[name] = null;   // two meanings: unknown
            }

            string? Infer(Node? value) => value switch
            {
                CallNode c when IsErrorType(c.Name) => c.Name,
                RecordNode => "Record",
                WordNode w when types.TryGetValue(w.Text, out string? known) => known,
                StringNode or QuantityNode or BoolNode or ListNode => "Value",
                _ => null,
            };

            if (d.Node is HelperDeclNode helper)
            {
                foreach (ParamNode p in helper.Params) Note(p.Name, p.Type);
            }

            List<Node> nodes = [.. Descendants(d.Node, includeObservation: false)];
            foreach (Node n in nodes)
            {
                switch (n)
                {
                    case LetStmt l: Note(l.Name, l.Type ?? Infer(l.Value)); break;
                    case AssignStmt a: Note(a.Name, Infer(a.Value)); break;
                    case TryStmt t: foreach (CatchClause clause in t.Catches) Note(clause.Name, clause.Type ?? "Error"); break;
                    case ForStmt f: Note(f.Var, null); break;
                }
            }

            bool Reads(string? variable, string field)
            {
                if (!dynamicFields.Contains(field)) return false;
                string? type = variable is not null && types.TryGetValue(variable, out string? known) ? known : null;
                return type is null || MayHaveDynamicField(type, field);
            }

            return nodes.Any(n => n switch
            {
                FieldNode f => Reads((f.Target as WordNode)?.Text, f.Name),
                RefNode r => Reads(r.Block, r.Port),
                _ => false,
            });
        }

        private static readonly HashSet<string> ErrorMembers = new(StringComparer.Ordinal) { "type", "message", "code", "hint", "origin", "context",
            "cause", "suppressed", "suppressedCount" };

        private void CheckErrorDeclarations(List<Decl> all, List<HelperIssue> issues)
        {
            foreach (Decl d in all.Where(x => x.Node is ErrorDeclNode))
            {
                ErrorDeclNode e = (ErrorDeclNode)d.Node;
                void Problem(string message) => issues.Add(new HelperIssue(d.Line, new PipeIssue("error-decl", message, e.Start, e.End)));
                if (e.Parent is not null && !IsErrorType(e.Parent))
                    Problem($"\"{e.Name}\" extends \"{e.Parent}\", which is not an error type (Error, RequirementError or an error of this file)");
                else if (!TryErrorSpec(e.Name, out _, out List<ErrorFieldDecl> all2)) Problem($"\"{e.Name}\" extends itself through its parents");
                else
                {
                    foreach (IGrouping<string, ErrorFieldDecl> group in all2.GroupBy(f => f.Name,
                        StringComparer.Ordinal).Where(g => g.Count() > 1)) Problem($"The field \"{group.Key}\" is declared twice (also by a parent)");
                }

                foreach (ErrorFieldDecl field in e.Fields)
                {
                    if (ErrorMembers.Contains(field.Name)) Problem($"\"{field.Name}\" is a member every error has: choose another field name");
                    CheckType(d, field.Type, field.Start, field.End, issues);
                }
            }
        }

        internal bool TryType(string name, out List<string> members)
        {
            members = [];
            if (!types.TryGetValue(name, out TypeDeclNode? t)) return false;
            members = t.Members;
            return true;
        }

        internal int ParamCount(string name) => helpers.TryGetValue(name, out Decl? d) && d.Node is HelperDeclNode h ? h.Params.Count : -1;

        /// <summary>Whether a helper of that name is declared in the file but its declaration has a syntax error: calls of it are not "unknown", they wait for the declaration to be fixed.</summary>
        internal bool IsBroken(string name) => failed.Contains(name);

        /// <summary>The hashes of the helpers and constants that a text uses, and of what those use in turn (comments are skipped): what a component depends on besides its own text. The helpers that are the process of a component are not part of it.</summary>
        /// <param name="text">The text of the process.</param>
        /// <returns>The hashes, sorted.</returns>
        public IReadOnlyList<string> ClosureHashes(string text)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<string> hashes = [];
            void Visit(string t)
            {
                foreach (Match m in Regex.Matches(StripComments(t), @"[A-Za-z_][\w-]*", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    if (!seen.Add(m.Value)) continue;
                    if (helpers.TryGetValue(m.Value, out Decl? h) && !h.Name.StartsWith("__c_", StringComparison.Ordinal)) { hashes.Add(h.Hash); Visit(h.SemanticText); }
                    else if (constants.TryGetValue(m.Value, out Decl? c)) { hashes.Add(c.Hash); Visit(c.SemanticText); }
                }
            }

            Visit(BlankObservationStatements(text) ?? text);
            hashes.Sort(StringComparer.Ordinal);

            // the source tier (plan R1.1): what only the observation or assert statements reach, and helpers whose full text differs
            // from the semantic one, are listed with the mark "source:"; only the payload hash reads them
            HashSet<string> seenSource = new(StringComparer.OrdinalIgnoreCase);
            List<string> extras = [];
            void VisitSource(string t)
            {
                foreach (Match m in Regex.Matches(StripComments(t), @"[A-Za-z_][\w-]*", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    if (!seenSource.Add(m.Value)) continue;
                    if (helpers.TryGetValue(m.Value, out Decl? h) && !h.Name.StartsWith("__c_", StringComparison.Ordinal))
                    {
                        if (h.SourceHash != h.Hash || !seen.Contains(m.Value)) extras.Add("source:" + h.SourceHash);
                        VisitSource(h.Text);
                    }
                    else if (constants.TryGetValue(m.Value, out Decl? c))
                    {
                        if (c.SourceHash != c.Hash || !seen.Contains(m.Value)) extras.Add("source:" + c.SourceHash);
                        VisitSource(c.Text);
                    }
                }
            }

            VisitSource(text);
            extras.Sort(StringComparer.Ordinal);
            hashes.AddRange(extras);
            return hashes;
        }

        internal bool IsDynamicHelper(string name) => helpers.TryGetValue(name, out Decl? d) && d.Effect == "dynamic";

        private static bool IsBaked(Decl d) => d.Node is HelperDeclNode { Baked: not null };

        private static string? Nearest(string word, IEnumerable<string> candidates)
        {
            string? best = null;
            int bestDistance = 3;
            foreach (string candidate in candidates)
            {
                int d = NotePipeline.Distance(word.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (d < bestDistance) { best = candidate; bestDistance = d; }
            }

            return best;
        }

        private void Analyze(List<Decl> all, List<HelperIssue> issues)
        {
            IEnumerable<string> known = LangFunctions.Concat(OperatorNames).Concat(helpers.Values.Select(h => h.Name)).Concat(errors.Keys).Concat(["bars", "beats", "time"]);
            Dictionary<string, HashSet<string>> calls = new(StringComparer.OrdinalIgnoreCase);
            foreach (Decl d in all)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (Node n in Descendants(d.Node, includeObservation: false))
                {
                    if (n is CallNode c) names.Add(c.Name);
                    if (n is WordNode or RefNode && n is WordNode w && constants.ContainsKey(w.Text)) names.Add(w.Text);
                }

                calls[d.Name] = names;
            }

            HashSet<string> dynamicFields = new(errors.Values.SelectMany(e => e.Fields.Where(f => f.Dynamic).Select(f => f.Name)), StringComparer.OrdinalIgnoreCase);
            HashSet<string> readsDynamicField = new(StringComparer.OrdinalIgnoreCase);
            if (dynamicFields.Count > 0)
            {
                foreach (Decl d in all)
                {
                    if (ReadsDynamicField(d, dynamicFields)) readsDynamicField.Add(d.Name);
                }
            }

            // the effect: a helper is dynamic when it calls a volatile built-in or a dynamic helper (a baked helper is pure for its callers)
            foreach (Decl d in all) d.Effect = d.IsDynamic && !IsBaked(d) ? "dynamic" : "pure";
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (Decl d in all.Where(x => x.Effect == "pure" && !IsBaked(x)))
                {
                    if (readsDynamicField.Contains(d.Name) || calls[d.Name].Any(n => Volatile.Contains(n,
                        StringComparer.OrdinalIgnoreCase) || (helpers.TryGetValue(n,
                        out Decl? callee) && callee.Effect == "dynamic") || (constants.TryGetValue(n, out Decl? k) && k.Effect == "dynamic"))) { d.Effect = "dynamic"; changed = true; }
                }
            }

            foreach (Decl d in all)
            {
                if (d.Node is HelperDeclNode h)
                {
                    Check(d, h, known, calls[d.Name], issues);
                }
                else if (d.Node is ConstDeclNode cd)
                {
                    foreach (CallNode c in Descendants(cd).OfType<CallNode>()) CheckCall(d, c, known, issues);
                }
            }

            CheckErrorDeclarations(all, issues);
            foreach (Decl d in all.Where(x => x.Node is TypeDeclNode))
            {
                foreach (string member in ((TypeDeclNode)d.Node).Members) CheckType(d, member, d.Node.Start, d.Node.End, issues);
            }

            Dictionary<string, HashSet<string>> deps = new(StringComparer.OrdinalIgnoreCase);
            foreach (Decl d in all)
            {
                HashSet<string> direct = new(StringComparer.Ordinal);
                foreach (Node n in Descendants(d.Node, includeObservation: false))
                {
                    if (n is CallNode c)
                    {
                        string lower = c.Name.ToLowerInvariant();
                        if (lower == "stable") direct.Add("master-seed");
                        if (lower is "random" or "choose" or "chance" or "humanize" or "randomize" or "thin" or "scramble" or "walk" or "stable" or "random-seed") direct.Add("random");
                        if (lower is "currenttime" or "currentdate") direct.Add("clock");
                        if (lower == "osrandom") direct.Add("os-random");
                        if (lower == "environment") direct.Add("environment");
                    }

                    if (n is QuantityNode { Unit: "s" or "ms" }) direct.Add("tempo");
                }

                deps[d.Name] = direct;
            }

            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (Decl d in all)
                {
                    foreach (string callee in calls[d.Name])
                    {
                        if (deps.TryGetValue(callee, out HashSet<string>? inherited) && !IsBaked(helpers.GetValueOrDefault(callee) ?? constants.GetValueOrDefault(callee) ?? d))
                        {
                            foreach (string dep in inherited) changed |= deps[d.Name].Add(dep);
                        }
                    }
                }
            }

            foreach (Decl declaration in all)
            {
                foreach (ObserveStmt statement in Descendants(declaration.Node).OfType<ObserveStmt>().Where(s => !s.Semantic))
                {
                    bool assertion = statement.Kind == "assert";
                    foreach (Node expression in statement.Args.SelectMany(arg => Descendants(arg).Prepend(arg)))
                    {
                        string? name = expression switch { CallNode call => call.Name, WordNode word => word.Text, _ => null };
                        if (name is null) continue;
                        string lower = name.ToLowerInvariant();
                        bool forbidden = expression is CallNode && (lower is "osrandom" or "environment"
                            || (assertion && lower is "currenttime" or "currentdate"));
                        if (deps.TryGetValue(name, out HashSet<string>? sources))
                        {
                            forbidden |= sources.Contains("os-random") || sources.Contains("environment");
                            forbidden |= assertion && (sources.Contains("clock")
                                || helpers.GetValueOrDefault(name)?.Effect == "dynamic" || constants.GetValueOrDefault(name)?.Effect == "dynamic");
                        }
                        if (!forbidden) continue;
                        const string message = "This volatile operation is not available in this diagnostic expression";
                        issues.Add(new HelperIssue(declaration.Line, new PipeIssue("diagnostic-volatile", message, expression.Start, expression.End)));
                    }
                }
            }

            Infos = [.. all.Select(d => d.Node switch
            {
                HelperDeclNode h => new HelperInfo(d.Name, "helper", h.Dynamic, d.Effect, [.. h.Params.Select(p => new HelperParamInfo(p.Name, p.Type, p.Default is null ? null : Collapse(d.Text[p.Default.Start..Math.Min(p.Default.End, d.Text.Length)])))], h.Returns, d.Line),
                TypeDeclNode t => new HelperInfo(d.Name, "type", false, "pure", [], string.Join(" | ", t.Members), d.Line),
                ErrorDeclNode e => new HelperInfo(d.Name, "error", false, "pure", [], e.Parent ?? "Error", d.Line),
                ConstDeclNode c => new HelperInfo(d.Name, "const", false, d.Effect, [], c.Type, d.Line),
                _ => new HelperInfo(d.Name, "?", false, "pure", [], null, d.Line),
            } with { Dependencies = [.. deps[d.Name].Order(StringComparer.Ordinal)], Baked = d.Node is HelperDeclNode { Baked: not null } })];
        }

        private void CheckType(Decl d, string type, int start, int end, List<HelperIssue> issues)
        {
            string name = type.StartsWith("List<", StringComparison.Ordinal) || type.StartsWith("Range<", StringComparison.Ordinal) ? type[(type.IndexOf('<', StringComparison.Ordinal) + 1)..^1] : type;
            if (BuiltinTypes.Contains(name) || NoteCatalog.Types.ContainsKey(name) || types.ContainsKey(name) || errors.ContainsKey(name) || name is "List" or "Range") return;
            string? near = Nearest(name, BuiltinTypes.Concat(NoteCatalog.Types.Keys).Concat(types.Values.Select(t => t.Name)));
            issues.Add(new HelperIssue(d.Line, new PipeIssue("unknown-type", $"\"{name}\" is not a type", start, end, near)));
        }

        private void CheckCall(Decl d, CallNode c, IEnumerable<string> known, List<HelperIssue> issues)
        {
            if (known.Contains(c.Name, StringComparer.Ordinal)) return;
            string? same = known.FirstOrDefault(k => string.Equals(k, c.Name, StringComparison.OrdinalIgnoreCase));
            if (same is not null && helpers.ContainsKey(c.Name)) { issues.Add(new HelperIssue(d.Line, new PipeIssue("helper-casing", $"Helper \"{same}\" found, but the casing differs", c.Start, c.NameEnd, same))); return; }
            if (same is not null) { issues.Add(new HelperIssue(d.Line, new PipeIssue("helper-casing", $"\"{same}\" is written with other casing", c.Start, c.NameEnd, same))); return; }
            issues.Add(new HelperIssue(d.Line, new PipeIssue("unknown-helper", $"There is no helper or operator \"{c.Name}\"", c.Start, c.NameEnd, Nearest(c.Name, known))));
        }

        /// <summary>The lowerCamelCase form of a helper name (my-helper becomes myHelper); empty parts of the name (a-, a--b) are skipped.</summary>
        private static string LowerCamel(string name)
        {
            string[] parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return name;
            return char.ToLowerInvariant(parts[0][0]) + parts[0][1..] + string.Concat(parts.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
        }

        private void Check(Decl d, HelperDeclNode h, IEnumerable<string> known, HashSet<string> calledNames, List<HelperIssue> issues)
        {
            foreach (ParamNode prm in h.Params) CheckType(d, prm.Type, prm.Start, prm.End, issues);
            if (h.Returns is not null) CheckType(d, h.Returns, h.NameStart, h.NameEnd, issues);
            foreach (CallNode c in Descendants(h).OfType<CallNode>()) CheckCall(d, c, known, issues);
            if (!h.Dynamic && d.Effect == "dynamic")
                issues.Add(new HelperIssue(d.Line, new PipeIssue("helper-effect", $"\"{h.Name}\" uses a dynamic value (the clock, OS randomness, the environment or a dynamic helper) but is not marked dynamic", h.Start, h.Start + "helper".Length, "dynamic helper")));
            foreach (StmtBlock block in Descendants(h.Body).OfType<StmtBlock>().Prepend(h.Body))
            {
                int at = block.Statements.FindIndex(s => s is ReturnStmt);
                if (at >= 0 && at < block.Statements.Count - 1) issues.Add(new HelperIssue(d.Line, new PipeIssue("unreachable-code", "This is never run: the block returned before it", block.Statements[at + 1].Start, block.Statements[^1].End)));
            }

            CheckBody(d, h, issues);
            if (h.Name.Length > 0 && (h.Name.Contains('-', StringComparison.Ordinal) || char.IsUpper(h.Name[0])))
                issues.Add(new HelperIssue(d.Line, new PipeIssue("helper-naming", $"Helpers are written in lowerCamelCase ({LowerCamel(h.Name)}); blocks and resources keep their kebab-case. Rename it with F2 so the calls follow", h.NameStart, h.NameEnd)));
            bool emits = Descendants(h.Body).OfType<EmitStmt>().Any();
            ReturnStmt? valued = Descendants(h.Body).OfType<ReturnStmt>().FirstOrDefault(r => r.Value is not null);
            if (emits && h.Returns is not null and not "Notes") issues.Add(new HelperIssue(d.Line, new PipeIssue("emit-return", "emit builds Notes: the helper must return Notes", h.NameStart, h.NameEnd)));
            if (emits && valued is not null) issues.Add(new HelperIssue(d.Line, new PipeIssue("emit-return", "A helper uses emit or return with a value, not both", valued.Start, valued.End)));
            if (calledNames.Contains(h.Name) && h.Returns is null) issues.Add(new HelperIssue(d.Line, new PipeIssue("recursion-needs-type", $"\"{h.Name}\" calls itself: write its return type (-> Type)", h.NameStart, h.NameEnd)));
            foreach (CallNode c in Descendants(h.Body).OfType<CallNode>().Where(c => c.Name == "stable" && c.Args is [StringNode]))
                issues.Add(new HelperIssue(d.Line, new PipeIssue("shared-stable", h.Name.StartsWith("__c_", StringComparison.Ordinal) ? $"This stable name is shared by every instance of the component \"{ComponentLabel(h.Name)}\": two instances draw the same random numbers. Pass the seed in as a RandomSource property, or fork it per instance" : $"This stable name is shared by every call of \"{h.Name}\": two calls draw the same random numbers. Pass the seed in as a RandomSource parameter, or fork it per call", c.Start, c.End)));
        }

        /// <summary>The name a person knows a component by, from the name of the helper its process became (<c>__c_local_local_notes_octaveLift_1_0_0</c>: octaveLift).</summary>
        private static string ComponentLabel(string helper)
        {
            string trimmed = System.Text.RegularExpressions.Regex.Replace(helper, @"_\d+_\d+_\d+$", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            return trimmed[(trimmed.LastIndexOf('_') + 1)..];
        }

        // ---------- what a helper body says even in the branches that never run: names, return types, paths without a value, variables and statements that do nothing

        private static readonly Lazy<HashSet<string>> Vocabulary = new(() => new HashSet<string>(NoteCatalog.Types.Values.SelectMany(t => t.Values), StringComparer.OrdinalIgnoreCase));

        private sealed class Local(bool declared)
        {
            public bool Used { get; set; }

            public bool Declared { get; } = declared;
        }

        private void CheckBody(Decl d, HelperDeclNode h, List<HelperIssue> issues)
        {
            List<Dictionary<string, Local>> scopes = [new Dictionary<string, Local>(StringComparer.Ordinal)];
            foreach (ParamNode p in h.Params) scopes[0][p.Name] = new Local(false) { Used = true };
            List<(string Name, Node At, Local Local)> variables = [];
            bool observing = false;

            bool KnownWord(string w) => Vocabulary.Value.Contains(w) || Regex.IsMatch(w, "^[A-G][#b]?(-?[0-9])?$", RegexOptions.None, TimeSpan.FromSeconds(1)) || constants.ContainsKey(w) || helpers.ContainsKey(w) || types.ContainsKey(w) || types.Values.Any(t => t.Members.Contains(w, StringComparer.OrdinalIgnoreCase)) || BuiltinTypes.Contains(w) || NoteCatalog.Types.ContainsKey(w) || w is "true" or "false";
            Local? Find(string name) { for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].TryGetValue(name, out Local? l)) return l; return null; }
            IEnumerable<string> VariableNames() => scopes.SelectMany(s => s.Keys).Concat(constants.Keys);

            void Undefined(WordNode w, bool arithmetic)
            {
                string? near = Nearest(w.Text, VariableNames().Concat(Vocabulary.Value));
                string hint = near is null ? string.Empty : $" Did you mean \"{near}\"?";
                issues.Add(new HelperIssue(d.Line, new PipeIssue("undefined-name", $"\"{w.Text}\" is not defined here: it is not a parameter or a variable of \"{h.Name}\", a constant, or a word the language knows (a plain word would be a symbol).{hint}", w.Start, w.End, near) { Strict = arithmetic }));
            }

            void Expr(Node? n, bool arithmetic = false)
            {
                switch (n)
                {
                    case null: return;
                    case WordNode w:
                        if (Find(w.Text) is { } local) local.Used = true;
                        else if (observing && ObservationProperties(h.Name)?.ContainsKey(w.Text) == true) return;
                        else if ((arithmetic || !KnownWord(w.Text)) && !w.Escaped && !w.Text.Contains('-', StringComparison.Ordinal)) Undefined(w, arithmetic);   // a hyphenated word is reported by the hyphen rule (AMP2074)   // in a calculation a plain word (even a pitch class like b) is never a number
                        return;
                    case RefNode r when Find(r.Block) is { } holder:   // r.steps: a field of a variable
                        holder.Used = true;
                        return;
                    case CallNode c:
                        foreach (Node a in c.Args) Expr(a is NamedNode named ? named.Value : a);
                        return;
                    case BinaryNode { Op: "is" } isNode:
                        Expr(isNode.Left);
                        return;
                    case BinaryNode b:
                        bool arith = b.Op is "+" or "-" or "*" or "/" or "%" or "<" or ">" or "<=" or ">=";
                        Expr(b.Left, arith);
                        Expr(b.Right, arith);
                        return;
                    case NamedNode nm:
                        Expr(nm.Value);
                        return;
                    default:
                        foreach (Node child in Children(n)) Expr(child);
                        return;
                }
            }

            void Declare(string name, Node at, bool track)
            {
                Local local = new(true) { Used = !track };
                scopes[^1][name] = local;
                if (track) variables.Add((name, at, local));
            }

            void Block(StmtBlock block, string? loopVar = null)
            {
                scopes.Add(new Dictionary<string, Local>(StringComparer.Ordinal));
                if (loopVar is not null) scopes[^1][loopVar] = new Local(true) { Used = true };
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    Node s = block.Statements[i];
                    switch (s)
                    {
                        case LetStmt l:
                            Expr(l.Value);
                            Declare(l.Name, l, true);
                            break;
                        case AssignStmt a:
                            Expr(a.Value);
                            if (Find(a.Name) is { } target) target.Used = true;
                            else issues.Add(new HelperIssue(d.Line, new PipeIssue("undefined-name", $"\"{a.Name}\" is not a variable here: declare it with var {a.Name} = ... first", a.Start, a.Start + a.Name.Length) { Strict = true }));
                            break;
                        case IfStmt f:
                            foreach ((Node cond, StmtBlock body) in f.Branches) { Expr(cond); Block(body); }
                            if (f.Otherwise is not null) Block(f.Otherwise);
                            break;
                        case ForStmt fs:
                            Expr(fs.From);
                            Expr(fs.To);
                            Block(fs.Body, fs.Var);
                            break;
                        case RepeatStmt rs:
                            Expr(rs.Count);
                            Block(rs.Body);
                            break;
                        case WhileStmt ws:
                            Expr(ws.Cond);
                            Expr(ws.Max);
                            Block(ws.Body);
                            break;
                        case ReturnStmt r:
                            Expr(r.Value);
                            break;
                        case TryStmt ts:
                            Block(ts.Body);
                            foreach (CatchClause cc in ts.Catches)
                            {
                                if (cc.Type is not null) CheckType(d, cc.Type, cc.Start, cc.End, issues);
                                Block(cc.Body, cc.Name);
                            }

                            if (ts.Finally is not null) Block(ts.Finally);
                            break;
                        case EmitStmt em:
                            Expr(em.Value);
                            break;
                        case ObserveStmt os:
                            observing = os.Kind != "assert" && !os.Semantic;
                            foreach (Node a in os.Args) Expr(a);
                            observing = false;
                            break;
                        case ExprStmt ex:
                            Expr(ex.Value);
                            bool last = block.Statements.Skip(i + 1).All(x => x is ObserveStmt { Semantic: false });
                            if (!last && !Descendants(ex.Value).Prepend(ex.Value).Any(x => x is CallNode or PipelineNode or MethodNode))
                                issues.Add(new HelperIssue(d.Line, new PipeIssue("no-effect", "This statement does nothing: its value is not used. Write return, emit or let in front of it, or remove it", ex.Start, ex.End)));
                            break;
                    }
                }

                scopes.RemoveAt(scopes.Count - 1);
            }

            Block(h.Body);
            foreach ((string name, Node at, Local local) in variables.Where(v => !v.Local.Used))
                issues.Add(new HelperIssue(d.Line, new PipeIssue("unused-variable", $"\"{name}\" is never used", at.Start, at.Start + 3 + 1 + name.Length)));

            // a literal that cannot be what the helper says it returns, also in a branch that never runs
            if (h.Returns is not null)
            {
                foreach (ReturnStmt r in Descendants(h.Body).OfType<ReturnStmt>().Where(x => x.Value is not null))
                {
                    string? what = LiteralMismatch(h.Returns, r.Value!);
                    if (what is not null) issues.Add(new HelperIssue(d.Line, new PipeIssue("type-mismatch", $"{what} cannot be returned by \"{h.Name}\": it says it returns {A(h.Returns)}", r.Value!.Start, r.Value.End)));
                }

                // a path that ends without a value (only when nothing is emitted: emit builds the value)
                if (!Descendants(h.Body).OfType<EmitStmt>().Any() && !Yields(h.Body))
                    issues.Add(new HelperIssue(d.Line, new PipeIssue("no-value", $"\"{h.Name}\" can end without a value: add an else with a return, or end the helper with an expression", h.NameStart, h.NameEnd)));
            }
        }

        private static string A(string name) => (name.Length > 0 && "AEIOU".Contains(name[0], StringComparison.Ordinal) ? "an " : "a ") + name;

        /// <summary>Whether every path of the block ends with a value: a return with a value, an expression, or an if with an else where every branch does.</summary>
        private static bool Yields(StmtBlock block)
        {
            Node? last = block.Statements.LastOrDefault(x => x is not ObserveStmt { Semantic: false });
            return last switch
            {
                ReturnStmt r => r.Value is not null,
                ExprStmt => true,
                EmitStmt => true,
                ObserveStmt { Kind: "fail" or "throw" } => true,   // the path ends the evaluation (a thrown error goes to a catch or ends it): it never needs a value
                IfStmt { Otherwise: not null } f => f.Branches.All(b => Yields(b.Body)) && Yields(f.Otherwise),
                TryStmt t => Yields(t.Body) && t.Catches.All(c => Yields(c.Body)),
                _ => false,
            };
        }

        /// <summary>A short description of a literal when it cannot be a value of the declared type (a string for an Int, a number for a ScaleMode, ...), or null when it can or when nothing is known.</summary>
        private static string? LiteralMismatch(string declared, Node value)
        {
            string type = declared.Trim();
            bool numeric = type is "Int" or "Float" or "Number" or "Count" or "Octave" or "Velocity" or "Pitch" or "Program" or "Probability" or "Interval" or "Length" or "Time" or "Duration" or "Gain" or "Percent";
            bool word = type is "ScaleMode" or "PitchClass";
            string? literal = value switch { StringNode s => $"\"{s.Value}\" (a String)", QuantityNode q => $"{q.Text} (a number)", BoolNode b => $"{(b.Value ? "true" : "false")} (a Bool)", _ => null };
            if (literal is null) return null;
            bool isString = value is StringNode, isBool = value is BoolNode, isNumber = value is QuantityNode;
            if (numeric && (isString || isBool)) return literal;
            if (word && (isString || isBool || isNumber)) return literal;
            if (type is "Notes" && (isString || isBool || isNumber)) return literal;
            if (type is "Bool" && (isString || isNumber)) return literal;
            if (type is "String" or "Text" && (isBool || isNumber)) return literal;
            return null;
        }
    }

    // ---------- values

    [Flags]
    private enum ValueMarks { None = 0, Dynamic = 1, Sensitive = 2 }

    private abstract record Val
    {
        public ValueMarks Marks { get; init; }

        public bool Tainted
        {
            get => (Marks & ValueMarks.Dynamic) != 0;
            init => Marks = value ? Marks | ValueMarks.Dynamic : Marks & ~ValueMarks.Dynamic;
        }
    }

    private sealed record IntV(BigInteger V, string? Brand = null) : Val;

    private sealed record FloatV(double V, Rat? Exact = null) : Val;

    private sealed record BoolV(bool V) : Val;

    private sealed record StrV(string V) : Val;

    private sealed record QuantV(Rat V, string Dim) : Val;

    private sealed record NotesV(PipeValue V, string Recipe) : Val;

    private sealed record ListV(List<Val> Items) : Val;

    /// <summary>A record: fields in the order they were written; immutable; never part of a recipe (an operator cannot take one).</summary>
    private sealed record RecordV(List<(string Key, Val Value)> Fields) : Val;

    private sealed record RangeV(Val From, Val To, bool Inclusive) : Val;

    private sealed record WordV(string V) : Val;

    private sealed record SeedV(Node Expr, string Text) : Val;

    private sealed record NowV(DateTime T) : Val;

    private sealed record NoteV(PipeNote N) : Val;

    private sealed record UnitV : Val;

    private sealed class LangFailure(string id, string message, Node? node, string? suggestion = null) : Exception(message)
    {
        public string? Suggestion { get; } = suggestion;

        public string Id { get; } = id;

        public Node? Node { get; } = node;

        /// <summary>Gets the error value of a throw, when there is one.</summary>
        internal ErrorV? Error { get; init; }

        /// <summary>Gets or sets the marks of the decisions that led here: a catch that handles it runs under them.</summary>
        internal ValueMarks Marks { get; set; }

        internal List<FailureSummary> Suppressed { get; } = [];

        internal int SuppressedTotal { get; set; }

        internal List<LangFailure> Displaced { get; } = [];

        /// <summary>Gets or sets the steps of the unwind reserve that the finally blocks of this failure have used so far (one reserve for the whole unwinding).</summary>
        internal long ReserveUsed { get; set; }

        /// <summary>Gets or sets the call stack at the place where the failure was raised (innermost first, bounded).</summary>
        internal IReadOnlyList<StackFrameInfo>? Frames { get; set; }
    }

    private static Rat RatOfDouble(double d)
    {
        if (d == 0 || double.IsNaN(d) || double.IsInfinity(d)) return Rat.Of(0);
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent++;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        BigInteger n = mantissa, den = BigInteger.One;
        if (exponent > 0) n <<= exponent;
        else den <<= -exponent;
        return Rat.Of(negative ? -n : n, den);
    }

    private static double DoubleOfRat(Rat r) => (double)r.N / (double)r.D;

    private static bool IsVolatileNode(Node root, HelperTable table) => Descendants(root, includeObservation: false).Prepend(root)
        .Any(n => n is CallNode c && (Volatile.Contains(c.Name, StringComparer.OrdinalIgnoreCase) || table.IsDynamicHelper(c.Name)));

    /// <summary>Whether an expression needs the language layer (helpers, variables, arithmetic, control flow) and not only the operators of a pipeline.</summary>
    private static bool NeedsLang(Node root, Context context)
    {
        HelperTable table = context.Helpers ?? HelperTable.Empty;
        foreach (Node n in Descendants(root).Prepend(root))
        {
            switch (n)
            {
                case BinaryNode or UnaryNode or ListNode or BoolNode or FieldNode or RangeExprNode or BakeValueNode: return true;
                case CallNode c when !OperatorNames.Contains(c.Name) && c.Name is not ("random" or "choose" or "chance" or "stable" or "random-seed"): return true;
                case WordNode w when table.TryConst(w.Text, out _, out _): return true;
            }
        }

        return false;
    }
}
