using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

public static partial class NotePipeline
{
    /// <summary>The interpreter of the language layer (docs/language/14): it evaluates helpers, variables, arithmetic and control flow, and hands every operator of a pipeline to the evaluator of the note pipelines, so there is one set of operators.</summary>
    private sealed class Interp
    {
        private sealed class Env(Env? parent)
        {
            private readonly Env? up = parent;

            private readonly Dictionary<string, (Val Value, bool Mutable)> vars = new(StringComparer.Ordinal);

            public bool TryGet(string name, out Val value)
            {
                for (Env? e = this; e is not null; e = e.up)
                {
                    if (e.vars.TryGetValue(name, out (Val Value, bool Mutable) t)) { value = t.Value; return true; }
                }

                value = null!;
                return false;
            }

            public bool DefinedHere(string name) => vars.ContainsKey(name);

            public void Define(string name, Val value, bool mutable) => vars[name] = (value, mutable);

            public int Assign(string name, Val value)
            {
                for (Env? e = this; e is not null; e = e.up)
                {
                    if (!e.vars.TryGetValue(name, out (Val Value, bool Mutable) t)) continue;
                    if (!t.Mutable) return 2;
                    e.vars[name] = (value, true);
                    return 0;
                }

                return 1;
            }
        }

        private sealed class Frame(string name)
        {
            public string Name { get; } = name;

            public NotesV? Emitted { get; set; }

            public bool Returned { get; set; }

            public Val? ReturnValue { get; set; }

            public Val? Last { get; set; }
        }

        private readonly Context context;
        private readonly Rat tempo;
        private readonly List<PipeIssue> errors;
        private readonly List<PipeIssue> warnings;
        private readonly string text;
        private readonly HelperTable table;
        private readonly LangLimits limits;
        private readonly SortedSet<string> closure = new(StringComparer.Ordinal);
        private readonly List<string> dynamicInputs = [];
        private readonly Dictionary<string, Val> constants = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> constantsBusy = new(StringComparer.OrdinalIgnoreCase);
        private long fuel;
        private long noteBytes;
        private int depth;

        public Interp(Context context, Rat tempo, List<PipeIssue> errors, List<PipeIssue> warnings, string text)
        {
            this.context = context;
            this.tempo = tempo;
            this.errors = errors;
            this.warnings = warnings;
            this.text = text;
            table = context.Helpers ?? HelperTable.Empty;
            limits = context.Limits.Clamped();
            Legacy = new Evaluation(context, tempo, errors, warnings, text);
        }

        public Evaluation Legacy { get; }

        public string? EvalText(Node ast)
        {
            try
            {
                Val v = Eval(ast, new Env(null));
                return v is NotesV ? null : ValText(v);
            }
            catch (LangFailure)
            {
                return null;
            }
        }

        public bool Dynamic { get; private set; }

        public bool BakedDynamic { get; private set; }

        public IReadOnlyList<string> DynamicInputs => dynamicInputs;

        public long Fuel => fuel;

        public PipeValue? Run(Node ast, out string? recipe)
        {
            recipe = null;
            try
            {
                Val v = Eval(ast, new Env(null));
                if (v is not NotesV notes) throw new LangFailure("type-mismatch", $"The notes of a clip must be Notes, this expression is {TypeName(v)}", ast);
                string body = notes.Recipe + "\nhelpers:" + string.Join(",", closure);
                recipe = Sha("ample-lang/1\n" + body + Legacy.Extra(body));
                return notes.V;
            }
            catch (StopRun stop)
            {
                string body = stop.Value.Recipe + "\nhelpers:" + string.Join(",", closure);
                recipe = Sha("ample-lang/1\n" + body + Legacy.Extra(body));
                return stop.Value.V;
            }
            catch (LangFailure f)
            {
                if (f.Id != "reported") errors.Add(new PipeIssue(f.Id, f.Message, f.Node?.Start ?? 0, f.Node?.End ?? text.Length, f.Suggestion));
                return null;
            }
        }

        public string RecipeHash(Node expr) => ExprRecipe(expr);

        public sealed record BakeInfo(string BakedText, string CurrentText, string Hash, bool Override, bool Stale, bool Dirty);

        public BakeInfo? LastBake { get; private set; }

        /// <summary>Evaluates a property expression and gives the value in the form the block takes (a number in the unit of the template).</summary>
        public string? RunValue(Node ast, PropertyShape shape, out string? failure)
        {
            failure = null;
            try
            {
                Val v = Eval(ast, new Env(null));
                Val c = CoerceProperty(v, shape, ast);
                return PropertyText(c, shape);
            }
            catch (LangFailure f)
            {
                if (f.Id != "reported") errors.Add(new PipeIssue(f.Id, f.Message, f.Node?.Start ?? 0, f.Node?.End ?? text.Length, f.Suggestion));
                failure = f.Message;
                return null;
            }
        }

        private Val CoerceProperty(Val v, PropertyShape s, Node at)
        {
            string what = "property " + s.Name;
            switch (s.Type)
            {
                case "Gain":
                    if (v is QuantV { Dim: "gain" }) return v;
                    if (IsNumber(v)) return With(new QuantV(AsRat(v), "gain"), v.Tainted);
                    break;
                case "Time":
                    if (v is QuantV { Dim: "time" }) return v;
                    if (IsNumber(v)) return With(new QuantV(s.Unit == "s" ? AsRat(v) : AsRat(v) * Rat.Of(1, 1000), "time"), v.Tainted);
                    break;
                case "Percent":
                    if (v is QuantV { Dim: "percent" }) return v;
                    if (IsNumber(v)) return With(new QuantV(AsRat(v) * Rat.Of(1, 100), "percent"), v.Tainted);
                    break;
                case "Number" or "Frequency":
                    if (IsNumber(v)) return v;
                    break;
                default:
                    return Coerce(v, s.Type, at, what);
            }

            throw new LangFailure("type-mismatch", $"{what}: {Describe(v)} is not {A(s.Type)}", at);
        }

        private string PropertyText(Val v, PropertyShape s)
        {
            // a text or a flag is written as it is (the block takes the text); only numbers and quantities are converted to the unit of the template
            if (v is StrV text) return text.V;
            if (v is BoolV flag) return flag.V ? "true" : "false";
            Rat number = v switch
            {
                QuantV { Dim: "time" } q => s.Unit == "s" ? q.V : q.V * Rat.Of(1000),
                QuantV { Dim: "percent" } q => q.V * Rat.Of(100),
                QuantV q => q.V,
                IntV i => Rat.Of(i.V),
                FloatV f => f.Exact ?? RatOfDouble(f.V),
                _ => throw new LangFailure("type-mismatch", $"property {s.Name}: {Describe(v)} cannot be written as a number", new WordNode(string.Empty, 0, 0)),
            };
            double d = DoubleOfRat(number);
            if ((s.Min is { } lo && d < lo) || (s.Max is { } hi && d > hi)) throw new LangFailure("type-mismatch", $"property {s.Name}: {DecimalText(number, 6)} is outside {s.Min}..{s.Max}", new WordNode(string.Empty, 0, 0));
            string written = DecimalText(number, 6);
            Match unit = Regex.Match(s.Default ?? string.Empty, @"^-?[\d.]+(\s*)([A-Za-z%]+)$", RegexOptions.None, TimeSpan.FromSeconds(1));
            return unit.Success ? written + unit.Groups[1].Value + unit.Groups[2].Value : written;
        }

        // ---------- small helpers

        private void Tick(Node n)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (++fuel > limits.Fuel) throw new LangFailure("fuel", $"The helper code used more than {limits.Fuel} evaluation steps (the limit of the song is limits.fuel)", n);
        }

        private void Account(Val v, Node n)
        {
            if (v is not NotesV notes) return;
            noteBytes += (long)notes.V.Events.Count * 64;
            if (noteBytes > (long)limits.MemoryMb * 1_048_576) throw new LangFailure("memory", $"The notes made by this song take more than {limits.MemoryMb} MB (an estimate; the limit of the song is limits.memory)", n);
        }

        private void Warn(string id, string message, Node node, string? suggestion = null) => warnings.Add(new PipeIssue(id, message, node.Start, node.End, suggestion));

        private static string A(string name) => (name.Length > 0 && "AEIOU".Contains(name[0], StringComparison.Ordinal) ? "an " : "a ") + name;

        private static string TypeName(Val v) => v switch
        {
            IntV => "Int",
            FloatV => "Float",
            BoolV => "Bool",
            StrV => "String",
            QuantV { Dim: "length" } => "Length",
            QuantV { Dim: "time" } => "Time",
            QuantV { Dim: "interval" } => "Interval",
            QuantV { Dim: "percent" } => "Percent",
            QuantV => "Gain",
            NotesV => "Notes",
            NoteV => "Note",
            ListV => "List",
            RangeV => "Range",
            WordV => "a word",
            SeedV => "RandomSource",
            NowV => "a date and time",
            _ => "nothing",
        };

        private string Describe(Val v) => v is NotesV ? "Notes" : $"{ValText(v)} ({TypeName(v)})";

        private static string DecimalText(Rat r, int places = 9)
        {
            BigInteger n = BigInteger.Abs(r.N), d = r.D;
            string sign = r.N.Sign < 0 ? "-" : string.Empty;
            BigInteger whole = BigInteger.DivRem(n, d, out BigInteger rest);
            if (rest.IsZero) return sign + whole.ToString(CultureInfo.InvariantCulture);
            StringBuilder sb = new(sign + whole.ToString(CultureInfo.InvariantCulture) + ".");
            for (int i = 0; i < places && !rest.IsZero; i++)
            {
                rest *= 10;
                sb.Append(BigInteger.DivRem(rest, d, out rest).ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString().TrimEnd('0').TrimEnd('.');
        }

        private string ValText(Val v) => v switch
        {
            IntV i => i.V.ToString(CultureInfo.InvariantCulture),
            FloatV f => f.Exact is { } e ? DecimalText(e) : f.V.ToString("R", CultureInfo.InvariantCulture),
            BoolV b => b.V ? "true" : "false",
            StrV s => "\"" + s.V + "\"",
            QuantV { Dim: "length" } q => (ToMicro(q.V, "beats", tempo) ?? 0).ToString(CultureInfo.InvariantCulture) + "ticks",
            QuantV { Dim: "time" } q => DecimalText(q.V, 6) + "s",
            QuantV { Dim: "percent" } q => DecimalText(q.V * Rat.Of(100), 6) + "%",
            QuantV { Dim: "interval" } q => DecimalText(q.V, 6) + "st",
            QuantV q => DecimalText(q.V, 6) + "dB",
            WordV w => w.V,
            SeedV sd => sd.Text,
            NowV n => n.T.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ListV l => "[" + string.Join(", ", l.Items.Select(ValText)) + "]",
            RangeV r => ValText(r.From) + (r.Inclusive ? "..=" : "..") + ValText(r.To),
            NotesV => "notes",
            _ => "nothing",
        };

        private static string ExactText(Val v) => v switch
        {
            IntV i => i.V.ToString(CultureInfo.InvariantCulture),
            FloatV f => (f.Exact ?? RatOfDouble(f.V)).ToString(),
            QuantV q => q.Dim + ":" + q.V,
            StrV s => "\"" + s.V + "\"",
            BoolV b => b.V ? "true" : "false",
            WordV w => w.V,
            SeedV sd => sd.Text,
            NowV n => n.T.ToString("O", CultureInfo.InvariantCulture),
            ListV l => "[" + string.Join(",", l.Items.Select(ExactText)) + "]",
            RangeV r => ExactText(r.From) + (r.Inclusive ? "..=" : "..") + ExactText(r.To),
            NotesV n => "notes:" + n.Recipe,
            _ => "unit",
        };

        private static bool Taint(params Val[] values) => values.Any(v => v.Tainted);

        private static Val With(Val v, bool tainted) => tainted && !v.Tainted ? v with { Tainted = true } : v;

        // ---------- values from and to the syntax tree

        private static Val FromQuantity(QuantityNode q)
        {
            Rat v = q.Value;
            switch (q.Unit)
            {
                case null: return v.IsInt ? new IntV(v.N) : new FloatV(DoubleOfRat(v), v);
                case "bar" or "bars": return new QuantV(v * Rat.Of(4), "length");
                case "beat" or "beats": return new QuantV(v, "length");
                case "tick" or "ticks": return new QuantV(Rat.Of(v.N, v.D * 1_000_000), "length");
                case "s": return new QuantV(v, "time");
                case "ms": return new QuantV(v * Rat.Of(1, 1000), "time");
                case "st": return new QuantV(v, "interval");
                case "oct": return new QuantV(v * Rat.Of(12), "interval");
                case "cents": return new QuantV(v * Rat.Of(1, 100), "interval");
                case "%": return new QuantV(v * Rat.Of(1, 100), "percent");
                case "dB" or "db": return new QuantV(v, "gain");
                default: throw new LangFailure("wrong-unit", $"Unknown unit \"{q.Unit}\"", q);
            }
        }

        private static Node NodeOf(Val v, Node at) => v switch
        {
            IntV i => new QuantityNode(Rat.Of(i.V), null, i.V.ToString(CultureInfo.InvariantCulture), at.Start, at.End),
            FloatV f => new QuantityNode(f.Exact ?? RatOfDouble(f.V), null, f.V.ToString("R", CultureInfo.InvariantCulture), at.Start, at.End),
            QuantV { Dim: "length" } q => new QuantityNode(q.V, "beats", q.V + "beats", at.Start, at.End),
            QuantV { Dim: "time" } q => new QuantityNode(q.V, "s", q.V + "s", at.Start, at.End),
            QuantV { Dim: "interval" } q => new QuantityNode(q.V, null, q.V.ToString(), at.Start, at.End),
            QuantV { Dim: "percent" } q => new QuantityNode(q.V, null, q.V.ToString(), at.Start, at.End),
            QuantV q => new QuantityNode(q.V, "dB", q.V + "dB", at.Start, at.End),
            StrV s => new StringNode(s.V, at.Start, at.End),
            WordV w => new WordNode(w.V, at.Start, at.End),
            BoolV b => new WordNode(b.V ? "true" : "false", at.Start, at.End),
            SeedV sd => sd.Expr,
            NotesV n => new ValueNode(n.V, n.Recipe, at.Start, at.End),
            RangeV r when NodeOf(r.From, at) is QuantityNode from && NodeOf(r.To, at) is QuantityNode to => new RangeNode(from, to, r.Inclusive, at.Start, at.End),
            _ => throw new LangFailure("type-mismatch", "This value cannot be given to an operator here", at),
        };

        private static Val FromNode(Node n) => n switch
        {
            QuantityNode q => FromQuantity(q),
            StringNode s => new StrV(s.Value),
            WordNode w => new WordV(w.Text),
            _ => throw new LangFailure("type-mismatch", "An operator gave something this layer cannot read", n),
        };

        // ---------- evaluation

        private Val Eval(Node node, Env env)
        {
            Tick(node);
            switch (node)
            {
                case QuantityNode q: return FromQuantity(q);
                case StringNode s: return new StrV(s.Value);
                case BoolNode b: return new BoolV(b.Value);
                case WordNode w: return Word(w, env);
                case ListNode l: return new ListV([.. l.Items.Select(i => Eval(i, env))]) { Tainted = false };
                case RangeNode r: return new RangeV(FromQuantity(r.From), FromQuantity(r.To), r.Inclusive);
                case RangeExprNode re: return new RangeV(Eval(re.From, env), Eval(re.To, env), re.Inclusive);
                case RefNode r when !r.Escaped && env.TryGet(r.Block, out Val target): return Field(target, r.Port, r);
                case RefNode r when r.Port != "notes": return ReadProperty(r);
                case RefNode or LiteralNode or EditableNode or ValueNode:
                    {
                        PipeValue? v = Legacy.Eval(node, out string? recipe);
                        if (v is null) throw new LangFailure("reported", string.Empty, node);
                        return new NotesV(v, recipe ?? Sha("ref")) { Tainted = false };
                    }

                case UnaryNode u: return Unary(u, env);
                case BinaryNode b: return BinaryOp(b, env);
                case TernaryNode t:
                    {
                        Val condition = Eval(t.Cond, env);
                        Val chosen = Eval(Truthy(condition) ? t.Then : t.Otherwise, env);
                        return With(chosen, condition.Tainted);
                    }

                case FieldNode f: return Field(Eval(f.Target, env), f.Name, f);
                case MethodNode m: return Method(m, env);
                case CallNode c: return Call(c, env, null);
                case PipelineNode p: { Val made = PipelineVal(p, env, true); Account(made, p); return made; }
                case BakeValueNode bv: return BakeValue(bv, env);
                case NamedNode n: return Eval(n.Value, env);
                default: throw new LangFailure("bad-argument", "This cannot be evaluated here", node);
            }
        }

        private Val Word(WordNode w, Env env)
        {
            bool hyphenated = w.Text.Contains('-', StringComparison.Ordinal);
            bool PartKnown(string part) => env.TryGet(part, out _) || table.TryConst(part, out _, out _) || (part.Length > 0 && part.All(char.IsDigit));
            bool partsExist = hyphenated && w.Text.Split('-') is { Length: >= 2 } split && split.All(PartKnown);
            if (hyphenated && !w.Escaped && partsExist && (env.TryGet(w.Text, out _) || table.TryConst(w.Text, out _, out _)))
                throw new LangFailure("ambiguous-name", $"\"{w.Text}\" is ambiguous: it can be the symbol {w.Text} or the subtraction {string.Join(" - ", w.Text.Split('-'))}. Write ({w.Text}) or {string.Join(" - ", w.Text.Split('-'))} to subtract, or [{w.Text}] for the symbol", w, string.Join(" - ", w.Text.Split('-')));
            if (env.TryGet(w.Text, out Val value)) return value;
            if (table.TryConst(w.Text, out ConstDeclNode decl, out string hash))
            {
                if (constants.TryGetValue(w.Text, out Val? cached)) return cached;
                if (!constantsBusy.Add(w.Text)) throw new LangFailure("cycle", $"\"{w.Text}\" is defined in terms of itself", w);
                try
                {
                    Val v = Eval(decl.Value, new Env(null));
                    if (decl.Type is not null) v = Coerce(v, decl.Type, decl.Value, w.Text);
                    closure.Add(hash);
                    constants[w.Text] = v;
                    return v;
                }
                finally
                {
                    constantsBusy.Remove(w.Text);
                }
            }

            if (partsExist && !w.Escaped) throw new LangFailure("unknown-name", $"\"{w.Text}\" is one name (a hyphen belongs to a name): write {string.Join(" - ", w.Text.Split('-'))} or ({w.Text}) to subtract", w, string.Join(" - ", w.Text.Split('-')));
            if (w.Escaped) throw new LangFailure("unknown-name", $"There is no symbol [{w.Text}]" + (partsExist ? $": did you mean the subtraction {string.Join(" - ", w.Text.Split('-'))}?" : string.Empty), w, partsExist ? string.Join(" - ", w.Text.Split('-')) : null);
            return new WordV(w.Text);
        }

        private Val ReadProperty(RefNode r)
        {
            if (context.Property is null) throw new LangFailure("bad-argument", $"\"{r.Port}\" is not a notes output; use {r.Block}.notes (properties of blocks can be read in a property expression)", r);
            PropertyRead read = context.Property(r.Block, r.Port);
            if (read.Error is not null || read.Text is null) throw new LangFailure(read.Error?.StartsWith("cycle", StringComparison.Ordinal) == true ? "cycle" : "bad-argument", read.Error ?? $"\"{r.Block}.{r.Port}\" has no value", r);
            string clean = StripComments(read.Text.Trim());
            if (clean.Length == 0) return new StrV(string.Empty);
            if (clean.StartsWith('-') || clean.StartsWith('+') || char.IsDigit(clean[0]))
            {
                Match m = Regex.Match(clean, @"^([+-]?\d+(?:\.\d+)?)\s*([A-Za-z%]*)$", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (m.Success && ParseDecimal(m.Groups[1].Value) is { } number)
                {
                    string unit = m.Groups[2].Value;
                    return unit.Length == 0 ? (number.IsInt ? new IntV(number.N) : new FloatV(DoubleOfRat(number), number)) : FromQuantity(new QuantityNode(number, unit, clean, r.Start, r.End));
                }
            }

            return new WordV(clean.Trim('"'));
        }

        private Val Unary(UnaryNode u, Env env)
        {
            Val v = Eval(u.Operand, env);
            Val result = (u.Op, v) switch
            {
                ("!", _) => new BoolV(!Truthy(v)),
                ("-", IntV i) => new IntV(-i.V),
                ("-", FloatV f) => new FloatV(-f.V, f.Exact is { } e ? Rat.Of(-e.N, e.D) : null),
                ("-", QuantV q) => new QuantV(Rat.Of(-q.V.N, q.V.D), q.Dim),
                _ => throw new LangFailure("type-mismatch", $"{u.Op} cannot be applied to {Describe(v)}", u),
            };
            return With(result, v.Tainted);
        }

        private Val BinaryOp(BinaryNode b, Env env)
        {
            if (b.Op is "&&" or "||")
            {
                Val left = Eval(b.Left, env);
                bool leftTruth = Truthy(left);
                if (b.Op == "&&" ? !leftTruth : leftTruth) return With(new BoolV(leftTruth), left.Tainted);   // short circuit: the answer is always true or false
                Val right = Eval(b.Right, env);
                return With(new BoolV(Truthy(right)), left.Tainted || right.Tainted);
            }

            if (b.Op == "is")
            {
                Val subject = Eval(b.Left, env);
                string type = b.Right is WordNode w ? w.Text : string.Empty;
                return With(new BoolV(IsType(subject, type)), subject.Tainted);
            }

            Val l = Eval(b.Left, env), r = Eval(b.Right, env);
            return With(Arith(b.Op, l, r, b), l.Tainted || r.Tainted);
        }

        private static bool IsType(Val v, string type) => type switch
        {
            "Int" => v is IntV,
            "Float" => v is FloatV,
            "Number" => v is IntV or FloatV,
            "Bool" => v is BoolV,
            "String" => v is StrV,
            "Length" => v is QuantV { Dim: "length" },
            "Time" => v is QuantV { Dim: "time" },
            "Duration" => v is QuantV { Dim: "length" or "time" },
            "Interval" => v is QuantV { Dim: "interval" },
            "Notes" => v is NotesV,
            _ => false,
        };

        private static bool IsNumber(Val v) => v is IntV or FloatV;

        private static double AsDouble(Val v) => v is IntV i ? (double)i.V : ((FloatV)v).V;

        private static Rat AsRat(Val v) => v is IntV i ? Rat.Of(i.V) : ((FloatV)v).Exact ?? RatOfDouble(((FloatV)v).V);

        private Val Arith(string op, Val l, Val r, Node at)
        {
            static Val Cmp(string op, int c) => new BoolV(op switch { "==" => c == 0, "!=" => c != 0, "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 });
            bool compare = op is "==" or "!=" or "<" or "<=" or ">" or ">=";
            if (l is IntV a && r is IntV b)
            {
                if (compare) return Cmp(op, a.V.CompareTo(b.V));
                switch (op)
                {
                    case "+": return new IntV(a.V + b.V);
                    case "-": return new IntV(a.V - b.V);
                    case "*": return new IntV(a.V * b.V);
                    case "/": if (b.V.IsZero) throw new LangFailure("divide-by-zero", "Division by zero", at); return new FloatV((double)a.V / (double)b.V);
                    case "%": if (b.V.IsZero) throw new LangFailure("divide-by-zero", "Division by zero", at); return new IntV(BigInteger.Remainder(a.V, b.V));
                }
            }

            if (IsNumber(l) && IsNumber(r))
            {
                double x = AsDouble(l), y = AsDouble(r);
                if (compare) return Cmp(op, x.CompareTo(y));
                switch (op)
                {
                    case "+": return new FloatV(x + y);
                    case "-": return new FloatV(x - y);
                    case "*": return new FloatV(x * y);
                    case "/": if (y == 0) throw new LangFailure("divide-by-zero", "Division by zero", at); return new FloatV(x / y);
                    case "%": if (y == 0) throw new LangFailure("divide-by-zero", "Division by zero", at); return new FloatV(Math.IEEERemainder(x, y) is var _ ? x % y : 0);
                }
            }

            if (l is QuantV ql && r is QuantV qr && ql.Dim == qr.Dim)
            {
                if (compare) return Cmp(op, ql.V.N * qr.V.D < qr.V.N * ql.V.D ? -1 : ql.V.N * qr.V.D > qr.V.N * ql.V.D ? 1 : 0);
                switch (op)
                {
                    case "+": return new QuantV(ql.V + qr.V, ql.Dim);
                    case "-": return new QuantV(ql.V - qr.V, ql.Dim);
                    case "/": if (qr.V.N.IsZero) throw new LangFailure("divide-by-zero", "Division by zero", at); return new FloatV(DoubleOfRat(Rat.Of(ql.V.N * qr.V.D, ql.V.D * qr.V.N)));
                }
            }

            if (l is QuantV qm && IsNumber(r) && op is "*" or "/")
            {
                Rat k = AsRat(r);
                if (op == "/" && k.N.IsZero) throw new LangFailure("divide-by-zero", "Division by zero", at);
                return new QuantV(op == "*" ? qm.V * k : Rat.Of(qm.V.N * k.D, qm.V.D * k.N), qm.Dim);
            }

            if (IsNumber(l) && r is QuantV qn && op == "*") return new QuantV(AsRat(l) * qn.V, qn.Dim);
            if (l is StrV s1 && r is StrV s2)
            {
                if (op == "+") return new StrV(s1.V + s2.V);
                if (compare) return Cmp(op, string.CompareOrdinal(s1.V, s2.V));
            }

            if (op is "==" or "!=" && ((l is BoolV && r is BoolV) || (l is WordV or StrV && r is WordV or StrV)))
            {
                bool equal = l is BoolV bl ? bl.V == ((BoolV)r).V : TextOf(l) == TextOf(r);
                return new BoolV(op == "==" ? equal : !equal);
            }

            throw new LangFailure("type-mismatch", $"{op} cannot be applied to {Describe(l)} and {Describe(r)}", at);
        }

        private static string TextOf(Val v) => v is WordV w ? w.V : ((StrV)v).V;

        private Val Field(Val target, string name, Node at)
        {
            Val result = target switch
            {
                NowV n => name switch
                {
                    "year" => new IntV(n.T.Year),
                    "month" => new IntV(n.T.Month),
                    "day" => new IntV(n.T.Day),
                    "hour" => new IntV(n.T.Hour),
                    "minute" => new IntV(n.T.Minute),
                    "second" => new IntV(n.T.Second),
                    "weekday" => new IntV(n.T.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)n.T.DayOfWeek),
                    _ => throw new LangFailure("bad-argument", $"A date and time has no \"{name}\" (it has year, month, day, hour, minute, second, weekday)", at),
                },
                NoteV note => name switch
                {
                    "at" => new QuantV(Rat.Of(note.N.At, Beat), "length"),
                    "len" => new QuantV(Rat.Of(note.N.Len, Beat), "length"),
                    "pitch" => new IntV(note.N.Pitch),
                    "vel" => new IntV(note.N.Vel),
                    _ => throw new LangFailure("bad-argument", $"A note has no \"{name}\" (it has at, len, pitch, vel)", at),
                },
                _ => throw new LangFailure("bad-argument", $"{Describe(target)} has no field \"{name}\"", at),
            };
            return With(result, target.Tainted);
        }

        private static string? BuiltinFor(string method) => method.ToLowerInvariant() switch { "floor" => "Floor", "ceil" or "ceiling" => "Ceil", "round" => "Round", "truncate" or "trunc" => "Truncate", "abs" => "abs", "int" or "toint" => "Int", "float" or "tofloat" => "Float", "min" => "min", "max" => "max", "count" or "length" or "len" => "count", _ => null };

        private Val Method(MethodNode m, Env env)
        {
            if (m.Name == "fork") return Seed(m, env);
            if (m.Name == "next" && m.Target is WordNode w && context.Streams is not null && context.Streams.ContainsKey(w.Text))
            {
                QuantityNode? q = Legacy.StreamNext(w, m);
                if (q is null) throw new LangFailure("reported", string.Empty, m);
                return FromQuantity(q);
            }

            string? builtin = BuiltinFor(m.Name);
            throw new LangFailure("bad-argument", builtin is null ? $"Unknown method \"{m.Name}\" (the methods are .fork(label) on a seed and .next() on a random-stream)" : $"Numbers have no method \"{m.Name}\": the built-in is written {builtin}(value), like {builtin}({SourceOf(m.Target)})", m, builtin is null ? null : $"{builtin}({SourceOf(m.Target)})");
        }

        // ---------- seeds: they keep the form the operators know (a number, stable("name"), .fork(label))

        private SeedV Seed(Node n, Env env)
        {
            Node resolved = SeedTree(n, env);
            return new SeedV(resolved, Evaluation.SeedText(resolved));
        }

        private Node SeedTree(Node n, Env env)
        {
            switch (n)
            {
                case CallNode { Name: "stable" } c when c.Args.Count == 1:
                    {
                        Val name = Eval(c.Args[0], env);
                        return new CallNode("stable", [new StringNode(name is StrV s ? s.V : ValText(name), c.Args[0].Start, c.Args[0].End)], c.Start, c.End, c.NameEnd);
                    }

                case CallNode { Name: "random-seed" }: return n;
                case MethodNode { Name: "fork" } m:
                    {
                        Node target = SeedTree(m.Target, env);
                        Node result = target;
                        foreach (Node labelNode in m.Args)
                        {
                            Val label = Eval(labelNode, env);
                            Node ln = label switch { StrV s => new StringNode(s.V, labelNode.Start, labelNode.End), IntV i => new QuantityNode(Rat.Of(i.V), null, i.V.ToString(CultureInfo.InvariantCulture), labelNode.Start, labelNode.End), FloatV => throw new LangFailure("seed-error", "A fork label is a name in quotes or a whole number, and this is a Float (a division gives one): write Floor(...), Round(...) or Int(...) around it, or use %", labelNode, $"Floor({SourceOf(labelNode)})"), _ => throw new LangFailure("seed-error", "A fork label is a name in quotes or a whole number", labelNode) };
                            result = new MethodNode(result, "fork", [ln], m.Start, m.End);
                        }

                        return result;
                    }

                default:
                    {
                        Val v = Eval(n, env);
                        return v switch
                        {
                            SeedV sd => sd.Expr,
                            IntV i when i.V.Sign >= 0 => new QuantityNode(Rat.Of(i.V), null, i.V.ToString(CultureInfo.InvariantCulture), n.Start, n.End),
                            WordV w => new WordNode(w.V, n.Start, n.End),
                            _ => throw new LangFailure("seed-error", "A seed is a whole number, stable(\"name\"), or one of those followed by .fork(\"label\")", n),
                        };
                    }
            }
        }

        // ---------- calls

        private sealed record Arg(string? Name, Val Value, Node Origin);

        private Val Call(CallNode c, Env env, Val? piped)
        {
            string name = c.Name;
            string lower = name.ToLowerInvariant();
            if (lower is "currenttime" or "currentdate" or "osrandom" or "environment") return Volatile_(c, lower, env);
            if (table.TryHelper(name, out HelperDeclNode helper, out string hash, out string canonical, out string effect))
            {
                if (!string.Equals(canonical, name, StringComparison.Ordinal)) Warn("helper-casing", $"Helper \"{canonical}\" found, but the casing differs", new CallNode(name, [], c.Start, c.NameEnd, c.NameEnd), canonical);
                return CallHelper(c, helper, hash, canonical, effect, env, piped);
            }

            if (NoteCatalog.Signatures.TryGetValue(name, out NoteSignature? sig) && name is not ("random" or "choose" or "chance" or "stable" or "random-seed" or "bake")) return CatalogCall(c, sig, env, piped);
            Val? builtin = Builtin(c, name, env, piped);
            if (builtin is not null) return builtin;
            if (table.IsBroken(name)) throw new LangFailure("helper-broken", $"The helper \"{name}\" is not available: its declaration has an error (fix that first)", new CallNode(name, [], c.Start, c.NameEnd, c.NameEnd));
            string? near = Near(name);
            throw new LangFailure("unknown-operator", $"Unknown operator \"{name}\"", new CallNode(name, [], c.Start, c.NameEnd, c.NameEnd), near);
        }

        private string? Near(string word)
        {
            string? best = null;
            int bestDistance = 3;
            foreach (string candidate in OperatorNames.Concat(table.Names).Concat(LangFunctions))
            {
                int d = Distance(word.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (d < bestDistance) { best = candidate; bestDistance = d; }
            }

            return best;
        }

        private List<Arg> Args(CallNode c, Env env)
        {
            List<Arg> list = [];
            foreach (Node a in c.Args)
            {
                if (a is NamedNode n) list.Add(new Arg(n.Name, n.Name == "seed" ? Seed(n.Value, env) : Eval(n.Value, env), n.Value));
                else list.Add(new Arg(null, Eval(a, env), a));
            }

            return list;
        }

        private Val Volatile_(CallNode c, string lower, Env env)
        {
            string capability = lower switch { "currenttime" or "currentdate" => "clock", "osrandom" => "os-random", _ => "environment" };
            if (context.Capabilities is { } caps && !caps.Allowed(capability))
                throw new LangFailure("capability-denied", $"Capability `{capability}` is {caps.Why(capability)}", new CallNode(c.Name, [], c.Start, c.NameEnd, c.NameEnd));
            Dynamic = true;
            Val result;
            switch (lower)
            {
                case "currenttime" or "currentdate":
                    result = new NowV((context.Clock ?? (() => DateTime.Now))());
                    break;
                case "osrandom":
                    result = new IntV(context.OsRandom is { } source ? source() : RandomNumberGenerator.GetInt32(int.MaxValue));
                    break;
                default:
                    {
                        List<Arg> args = Args(c, env);
                        if (args.Count != 1 || args[0].Value is not StrV key) throw new LangFailure("bad-argument", "environment(name) takes the name of a variable in quotes", c);
                        if (context.Capabilities is { } named && !named.NameAllowed("environment", key.V))
                            throw new LangFailure("capability-denied", $"environment(\"{key.V}\") is not allowed: this scope only allows {string.Join(" and ", named.AllowLists("environment").Select(l => "[" + string.Join(", ", l) + "]"))}", c);
                        result = new StrV(context.Environment is { } read ? read(key.V) ?? string.Empty : System.Environment.GetEnvironmentVariable(key.V) ?? string.Empty);
                        // the value is not written into provenance: it can be a secret; only a short fingerprint is
                        dynamicInputs.Add($"environment(\"{key.V}\") = sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(((StrV)result).V)))[..8]}");
                        return result with { Tainted = true };
                    }
            }

            dynamicInputs.Add($"{c.Name}() = {ValText(result)}");
            return result with { Tainted = true };
        }

        private NotesV CatalogCall(CallNode c, NoteSignature sig, Env env, Val? piped)
        {
            if (sig.Receiver == "Notes" && piped is not NotesV) throw new LangFailure("bad-argument", $"{c.Name}(...) needs notes to work on: write notes |> {c.Name}(...)", c);
            List<Node> nodes = [];
            int position = 0;
            foreach (Node a in c.Args)
            {
                NoteParam? prm;
                Node value = a;
                string? named = null;
                if (a is NamedNode n) { named = n.Name; value = n.Value; prm = sig.Params.FirstOrDefault(x => x.Name == n.Name); }
                else
                {
                    prm = position < sig.Params.Count ? sig.Params[position] : null;
                    if (prm is not null && prm.Type != "Values") position++;
                }

                Node resolved = ArgNode(prm, value, env);
                nodes.Add(named is null ? resolved : new NamedNode(named, resolved, a.Start, a.End));
            }

            CallNode call = new(c.Name, nodes, c.Start, c.End, c.NameEnd);
            if (sig.Receiver is null)
            {
                PipeValue? generated = Legacy.Generate(call);
                if (generated is null) throw new LangFailure("reported", string.Empty, c);
                return Produced(generated, Sha("gen:" + Legacy.Normalize(call)), c);
            }

            NotesV input = (NotesV)piped!;
            PipeValue? stepped = Legacy.Step(input.V.Copy(), call);
            if (stepped is null) throw new LangFailure("reported", string.Empty, c);
            return Produced(stepped, Sha(input.Recipe + "|>" + Legacy.Normalize(call)), c);
        }

        private NotesV Produced(PipeValue value, string recipe, Node at)
        {
            if (value.Events.Count > context.Limit) throw new LangFailure("cap-events", $"More than {context.Limit} notes", at);
            return new NotesV(value, recipe);
        }

        private Node ArgNode(NoteParam? prm, Node value, Env env)
        {
            if (prm is { Type: "RandomSource" }) return Seed(value, env).Expr;
            if (value is CallNode { Name: "bars" or "beats" or "time" } window && window.Args.Count == 1)
                return new CallNode(window.Name, [ArgNode(prm, window.Args[0], env)], window.Start, window.End, window.NameEnd);
            Val v = Eval(value, env);
            Node plain = NodeOf(v, value);
            return prm is null ? plain : TypedValue(prm, plain);
        }

        private Val? Builtin(CallNode c, string name, Env env, Val? piped)
        {
            string lower = name.ToLowerInvariant();
            if (lower is "random" or "choose" or "chance")
            {
                List<Node> nodes = [];
                foreach (Node a in c.Args) nodes.Add(a is NamedNode n ? new NamedNode(n.Name, n.Name == "seed" ? Seed(n.Value, env).Expr : NodeOf(Eval(n.Value, env), n.Value), a.Start, a.End) : NodeOf(Eval(a, env), a));
                Node? result = Legacy.ValueCall(new CallNode(lower, nodes, c.Start, c.End, c.NameEnd));
                if (result is null) throw new LangFailure("reported", string.Empty, c);
                return FromNode(result);
            }

            if (lower is "stable" or "random-seed") return Seed(c, env);
            List<Arg> args = Args(c, env);
            if (piped is not null) args.Insert(0, new Arg(null, piped, c));
            bool tainted = args.Any(a => a.Value.Tainted);
            Val Need(int index, string what)
            {
                if (index >= args.Count) throw new LangFailure("bad-argument", $"{name} needs {what}", c);
                return args[index].Value;
            }

            Val? result2 = lower switch
            {
                "int" => ToInt(Need(0, "a value"), c, "exact"),
                "float" => Need(0, "a value") switch { IntV i => new FloatV((double)i.V, Rat.Of(i.V)), FloatV f => f, StrV s when double.TryParse(s.V, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => new FloatV(d), var o => throw new LangFailure("conversion", $"{Describe(o)} cannot be made a Float", c) },
                "round" => ToInt(Need(0, "a number"), c, "round"),
                "floor" => ToInt(Need(0, "a number"), c, "floor"),
                "ceil" => ToInt(Need(0, "a number"), c, "ceil"),
                "truncate" => ToInt(Need(0, "a number"), c, "truncate"),
                "count" => Need(0, "notes or a list") switch { NotesV n => new IntV(n.V.Events.Count), ListV l => new IntV(l.Items.Count), StrV s => new IntV(s.V.Length), var o => throw new LangFailure("type-mismatch", $"count needs notes, a list or a string, not {Describe(o)}", c) },
                "len" => Need(0, "a list or a string") switch { ListV l => new IntV(l.Items.Count), StrV s => new IntV(s.V.Length), NotesV n => new IntV(n.V.Events.Count), var o => throw new LangFailure("type-mismatch", $"len needs a list or a string, not {Describe(o)}", c) },
                "span" => Need(0, "notes") is NotesV sn ? new QuantV(Rat.Of(sn.V.Span, Beat), "length") : throw new LangFailure("type-mismatch", "span needs notes", c),
                "abs" => Need(0, "a number") switch { IntV i => new IntV(BigInteger.Abs(i.V)), FloatV f => new FloatV(Math.Abs(f.V), f.Exact is { } e ? Rat.Of(BigInteger.Abs(e.N), e.D) : null), QuantV q => new QuantV(Rat.Of(BigInteger.Abs(q.V.N), q.V.D), q.Dim), var o => throw new LangFailure("type-mismatch", $"abs needs a number, not {Describe(o)}", c) },
                "min" or "max" => MinMax(lower == "min", Need(0, "two values"), Need(1, "two values"), c),
                "at" => Need(0, "a list") is ListV al && Need(1, "an index") is IntV ai && ai.V >= 0 && ai.V < al.Items.Count ? al.Items[(int)ai.V] : throw new LangFailure("bad-argument", "at(list, index) needs a list and an index inside it", c),
                "note" => MakeNote(c, args),
                "emptynotes" => new NotesV(new PipeValue { Events = [], Span = Bar }, Sha("empty-notes")),
                _ => null,
            };
            return result2 is null ? null : With(result2, tainted);
        }

        private IntV ToInt(Val v, Node at, string mode)
        {
            switch (v)
            {
                case IntV i: return new IntV(i.V);
                case FloatV f:
                    {
                        Rat r = f.Exact ?? RatOfDouble(f.V);
                        if (mode == "exact")
                        {
                            if (!r.IsInt) throw new LangFailure("conversion", $"{ValText(v)} is not a whole number: use Round, Floor, Ceil or Truncate", at);
                            return new IntV(r.N);
                        }

                        BigInteger whole = BigInteger.DivRem(r.N, r.D, out BigInteger rest);
                        if (rest.Sign < 0) { whole -= 1; rest += r.D; }
                        return new IntV(mode switch { "round" => RoundHalfEven(r), "floor" => whole, "ceil" => rest.IsZero ? whole : whole + 1, _ => r.N.Sign < 0 && !rest.IsZero ? whole + 1 : whole });
                    }

                case StrV s when mode == "exact" && BigInteger.TryParse(s.V, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger parsed): return new IntV(parsed);
                default: throw new LangFailure("conversion", $"{Describe(v)} cannot be made a whole number", at);
            }
        }

        private static Val MinMax(bool min, Val a, Val b, Node at)
        {
            if (IsNumber(a) && IsNumber(b))
            {
                bool less = AsRat(a).N * AsRat(b).D < AsRat(b).N * AsRat(a).D;
                return min == less ? a : b;
            }

            if (a is QuantV qa && b is QuantV qb && qa.Dim == qb.Dim) return min == (qa.V.N * qb.V.D < qb.V.N * qa.V.D) ? a : b;
            throw new LangFailure("type-mismatch", $"{(min ? "min" : "max")} needs two numbers or two quantities of the same kind", at);
        }

        private NotesV MakeNote(CallNode c, List<Arg> args)
        {
            long at = 0, len = Beat;
            int pitch = 60, vel = 100;
            foreach (Arg a in args)
            {
                switch (a.Name)
                {
                    case "at": at = ToMicro((a.Value as QuantV)?.V ?? throw new LangFailure("type-mismatch", "at needs a length, like 1beat", a.Origin), "beats", tempo) ?? 0; break;
                    case "len": len = Math.Max(1, ToMicro((a.Value as QuantV)?.V ?? throw new LangFailure("type-mismatch", "len needs a length, like 1beat", a.Origin), "beats", tempo) ?? Beat); break;
                    case "pitch": pitch = (int)Math.Clamp((long)((Coerce(a.Value, "Pitch", a.Origin, "pitch") as IntV)?.V ?? 60), 0, 127); break;
                    case "vel": vel = (int)Math.Clamp((long)((Coerce(a.Value, "Velocity", a.Origin, "vel") as IntV)?.V ?? 100), 1, 127); break;
                    default: throw new LangFailure("bad-argument", "Note takes at=, len=, pitch= and vel=", a.Origin);
                }
            }

            PipeValue value = new() { Events = [new PipeNote(at, len, pitch, vel, string.Empty, string.Empty)], Span = SpanOf([new PipeNote(at, len, pitch, vel, string.Empty, string.Empty)]) };
            return new NotesV(value, Sha("note:" + at + ":" + len + ":" + pitch + ":" + vel));
        }

        // ---------- helpers

        private Val CallHelper(CallNode c, HelperDeclNode h, string hash, string canonical, string effect, Env env, Val? piped)
        {
            if (depth >= limits.StackDepth) throw new LangFailure("stack-depth", $"Helpers call each other more than {limits.StackDepth} deep (the limit of the song is limits.stack-depth)", c);
            List<Arg> args = [];
            if (piped is not null) args.Add(new Arg(null, piped, c));
            args.AddRange(Args(c, env));
            Env scope = new(null);
            HashSet<string> bound = new(StringComparer.Ordinal);
            int position = 0;
            foreach (Arg a in args)
            {
                ParamNode? prm;
                if (a.Name is not null)
                {
                    prm = h.Params.FirstOrDefault(x => string.Equals(x.Name, a.Name, StringComparison.Ordinal));
                    if (prm is null) throw new LangFailure("bad-argument", $"{canonical} has no parameter \"{a.Name}\" (it has: {string.Join(", ", h.Params.Select(x => x.Name))})", a.Origin);
                }
                else
                {
                    if (position >= h.Params.Count) throw new LangFailure("bad-argument", $"{canonical} takes at most {h.Params.Count} arguments", a.Origin);
                    prm = h.Params[position++];
                }

                if (!bound.Add(prm.Name)) throw new LangFailure("bad-argument", $"\"{prm.Name}\" is given twice", a.Origin);
                scope.Define(prm.Name, Coerce(a.Value, prm.Type, a.Origin, canonical + ": " + prm.Name), false);
            }

            foreach (ParamNode prm in h.Params.Where(x => !bound.Contains(x.Name)))
            {
                if (prm.Default is null) throw new LangFailure("bad-argument", $"{canonical} needs \"{prm.Name}\" ({prm.Type})", c);
                scope.Define(prm.Name, Coerce(Eval(prm.Default, new Env(null)), prm.Type, prm.Default, canonical + ": " + prm.Name), false);
            }

            closure.Add(hash);
            _ = effect;
            if (h.Baked is not null)
            {
                Val baked = Eval(h.Baked, new Env(null)) with { Tainted = false };
                BakedDynamic = true;
                return h.Returns is null ? baked : Coerce(baked, h.Returns, h.Baked, canonical);
            }

            depth++;
            try
            {
                Frame frame = new(canonical);
                ExecBlock(h.Body, scope, frame);
                Val? result = frame.Returned ? frame.ReturnValue ?? frame.Emitted : frame.Emitted is not null ? frame.Emitted : frame.Last;
                if (result is null || result is UnitV) throw new LangFailure("bad-argument", $"\"{canonical}\" gave no value: end it with an expression, emit or return", h.Body);
                Account(result, h.Body);
                return h.Returns is null ? result : Coerce(result, h.Returns, h.Body, canonical);
            }
            finally
            {
                depth--;
            }
        }

        private enum Flow { Next, Return }

        private Flow ExecBlock(StmtBlock block, Env env, Frame frame)
        {
            foreach (Node statement in block.Statements)
            {
                if (Exec(statement, env, frame) == Flow.Return) return Flow.Return;
            }

            return Flow.Next;
        }

        private bool Truth(Node cond, Env env)
        {
            return Truthy(Eval(cond, env));
        }

        /// <summary>
        /// Whether a value counts as true where a condition is read (<c>if</c>, <c>while</c>, <c>?:</c>, <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>): true is true; a number that is not 0, a quantity that is not 0, a string or a symbol that is not empty, a list
        /// with items, a range that is not empty and notes with at least one note are true-ish; 0, 0.0, 0ms, "", an empty list, an empty range and empty notes are false-ish. Seeds, random streams, times and single notes are always true.
        /// </summary>
        private static bool Truthy(Val v) => v switch
        {
            BoolV b => b.V,
            IntV i => !i.V.IsZero,
            FloatV f => f.V != 0,
            QuantV q => !q.V.N.IsZero,
            StrV s => s.V.Length > 0,
            WordV w => w.V.Length > 0,
            ListV l => l.Items.Count > 0,
            RangeV r => RangeHasItems(r),
            NotesV n => n.V.Events.Count > 0,
            UnitV => false,
            _ => true,
        };

        private static bool RangeHasItems(RangeV r)
        {
            static Rat? Number(Val v) => v switch { IntV i => Rat.Of(i.V), FloatV f => f.Exact ?? RatOfDouble(f.V), QuantV q => q.V, _ => null };
            if (Number(r.From) is not { } from || Number(r.To) is not { } to) return true;
            BigInteger left = from.N * to.D, right = to.N * from.D;
            return r.Inclusive ? left <= right : left < right;
        }

        private BigInteger WholeNumber(Node n, Env env, string what)
        {
            Val v = Coerce(Eval(n, env), "Int", n, what);
            return ((IntV)v).V;
        }

        private Flow Exec(Node statement, Env env, Frame frame)
        {
            Tick(statement);
            switch (statement)
            {
                case LetStmt let:
                    {
                        if (env.DefinedHere(let.Name)) throw new LangFailure("duplicate-variable", $"\"{let.Name}\" is already defined here", let);
                        Val v = Eval(let.Value, env);
                        env.Define(let.Name, let.Type is null ? v : Coerce(v, let.Type, let.Value, let.Name), let.Mutable);
                        return Flow.Next;
                    }

                case AssignStmt assign:
                    {
                        Val v = Eval(assign.Value, env);
                        int outcome = env.Assign(assign.Name, v);
                        if (outcome == 1) throw new LangFailure("undefined-variable", $"There is no variable \"{assign.Name}\"", assign);
                        if (outcome == 2) throw new LangFailure("immutable-variable", $"\"{assign.Name}\" was made with let: use var to change it", assign);
                        return Flow.Next;
                    }

                case IfStmt i:
                    foreach ((Node cond, StmtBlock body) in i.Branches)
                    {
                        if (Truth(cond, env)) return ExecBlock(body, new Env(env), frame);
                    }

                    return i.Otherwise is null ? Flow.Next : ExecBlock(i.Otherwise, new Env(env), frame);
                case ForStmt f: return For(f, env, frame);
                case RepeatStmt r:
                    {
                        BigInteger count = WholeNumber(r.Count, env, "repeat");
                        if (count > limits.LoopIterations) throw new LangFailure("loop-limit", $"This loop would run {count} times; the limit is {limits.LoopIterations} (limits.loop-iterations)", r.Count);
                        for (BigInteger k = 0; k < count; k++)
                        {
                            Tick(r);
                            if (ExecBlock(r.Body, new Env(env), frame) == Flow.Return) return Flow.Return;
                        }

                        return Flow.Next;
                    }

                case WhileStmt w:
                    {
                        BigInteger max = w.Max is null ? limits.WhileDefault : WholeNumber(w.Max, env, "max");
                        if (max > limits.LoopIterations) throw new LangFailure("loop-limit", $"max {max} is above the limit of {limits.LoopIterations} (limits.loop-iterations)", w.Max ?? w);
                        BigInteger n = 0;
                        while (Truth(w.Cond, env))
                        {
                            Tick(w);
                            if (n >= max)
                            {
                                if (w.Stop) break;
                                throw new LangFailure("while-limit", $"The while loop reached its max of {max} and its condition is still true (write max {max} stop if stopping there is what you want)", w);
                            }

                            if (ExecBlock(w.Body, new Env(env), frame) == Flow.Return) return Flow.Return;
                            n++;
                        }

                        return Flow.Next;
                    }

                case ReturnStmt rt:
                    frame.Returned = true;
                    frame.ReturnValue = rt.Value is null ? null : Eval(rt.Value, env);
                    return Flow.Return;
                case EmitStmt em:
                    {
                        Val v = Eval(em.Value, env);
                        if (v is not NotesV notes) throw new LangFailure("type-mismatch", $"emit needs Notes, not {Describe(v)}", em.Value);
                        frame.Emitted = frame.Emitted is null ? notes : MergeNotes(frame.Emitted, notes, em);
                        return Flow.Next;
                    }

                case ExprStmt ex:
                    frame.Last = Eval(ex.Value, env);
                    return Flow.Next;
                default: throw new LangFailure("bad-argument", "This is not a statement", statement);
            }
        }

        private NotesV MergeNotes(NotesV a, NotesV b, Node at)
        {
            CallNode merge = new("merge", [new ValueNode(b.V, b.Recipe, at.Start, at.End)], at.Start, at.End, at.End);
            PipeValue? merged = Legacy.Step(a.V.Copy(), merge);
            if (merged is null) throw new LangFailure("reported", string.Empty, at);
            return Produced(merged, Sha(a.Recipe + "|merge|" + b.Recipe), at);
        }

        private Flow For(ForStmt f, Env env, Frame frame)
        {
            if (f.To is not null)
            {
                BigInteger from = WholeNumber(f.From, env, "the start of the range"), to = WholeNumber(f.To, env, "the end of the range") + (f.Inclusive ? 1 : 0);
                BigInteger count = to - from;
                if (count > limits.LoopIterations) throw new LangFailure("loop-limit", $"This loop would run {count} times; the limit is {limits.LoopIterations} (limits.loop-iterations)", f.From);
                for (BigInteger i = from; i < to; i++)
                {
                    Tick(f);
                    Env scope = new(env);
                    scope.Define(f.Var, new IntV(i), false);
                    if (ExecBlock(f.Body, scope, frame) == Flow.Return) return Flow.Return;
                }

                return Flow.Next;
            }

            Val iterable = Eval(f.From, env);
            IEnumerable<Val> items = iterable switch
            {
                ListV l => l.Items,
                NotesV n => n.V.Events.Select(e => (Val)new NoteV(e)),
                RangeV { From: IntV a, To: IntV b } r => Enumerable.Range(0, (int)Math.Min(int.MaxValue, Math.Max(0, (long)(b.V - a.V) + (r.Inclusive ? 1 : 0)))).Select(k => (Val)new IntV(a.V + k)),
                _ => throw new LangFailure("type-mismatch", $"A for loop goes over a range, a list or notes, not {Describe(iterable)}", f.From),
            };
            int taken = 0;
            foreach (Val item in items)
            {
                if (++taken > limits.LoopIterations) throw new LangFailure("loop-limit", $"This loop would run more than {limits.LoopIterations} times (limits.loop-iterations)", f.From);
                Tick(f);
                Env scope = new(env);
                scope.Define(f.Var, With(item, iterable.Tainted), false);
                if (ExecBlock(f.Body, scope, frame) == Flow.Return) return Flow.Return;
            }

            return Flow.Next;
        }

        // ---------- types

        private Val Coerce(Val v, string typeText, Node at, string what)
        {
            string type = typeText.Trim();
            why = whyId = whySuggestion = null;
            if (TryCoerce(v, type, at, out Val result)) return With(result, v.Tainted);
            string unit = type is "Length" or "Time" or "Duration" && IsNumber(v) ? $" (a number needs a unit: beat, bar, tick, ms or s)" : string.Empty;
            throw new LangFailure(unit.Length > 0 ? "missing-unit" : whyId ?? "type-mismatch", $"{what}: {why ?? $"{Describe(v)} is not {A(type)}"}{unit}", at, whySuggestion);
        }

        private string? why;

        private string? whyId;

        private string? whySuggestion;

        private bool TryCoerce(Val v, string type, Node at, out Val result)
        {
            result = v;
            if (type.StartsWith("List<", StringComparison.Ordinal) && type.EndsWith('>'))
            {
                if (v is not ListV l) return false;
                List<Val> items = [];
                foreach (Val item in l.Items)
                {
                    if (!TryCoerce(item, type[5..^1], at, out Val converted)) return false;
                    items.Add(converted);
                }

                result = new ListV(items);
                return true;
            }

            if (type.StartsWith("Range<", StringComparison.Ordinal) && type.EndsWith('>'))
            {
                if (v is not RangeV r) return false;
                if (!TryCoerce(r.From, type[6..^1], at, out Val from) || !TryCoerce(r.To, type[6..^1], at, out Val to)) return false;
                result = new RangeV(from, to, r.Inclusive);
                return true;
            }

            if (table.TryType(type, out List<string> members))
            {
                foreach (string member in members)
                {
                    if (TryCoerce(v, member, at, out result)) return true;
                }

                result = v;
                return false;
            }

            switch (type)
            {
                case "Int":
                    if (v is IntV) return true;
                    if (v is FloatV f)
                    {
                        Rat r = f.Exact ?? RatOfDouble(f.V);
                        if (r.IsInt) { result = new IntV(r.N); return true; }
                        why = $"{ValText(v)} is not a whole number: write Round, Floor, Ceil or Truncate to say how it becomes one";
                        whyId = "lossy-conversion";
                        whySuggestion = "Round(" + SourceOf(at) + ")";
                        return false;
                    }

                    return false;
                case "Float":
                    if (v is FloatV) return true;
                    if (v is IntV i) { result = new FloatV((double)i.V, Rat.Of(i.V)); return true; }
                    return false;
                case "Number": return IsNumber(v);
                case "Bool": return v is BoolV;
                case "String" or "Text": return v is StrV;
                case "Notes": return v is NotesV;
                case "Length": return v is QuantV { Dim: "length" };
                case "Time": return v is QuantV { Dim: "time" };
                case "Duration": return v is QuantV { Dim: "length" or "time" };
                case "Gain": return v is QuantV { Dim: "gain" };
                case "Percent":
                    if (v is QuantV { Dim: "percent" }) return true;
                    if (v is FloatV pf) { result = new QuantV(pf.Exact ?? RatOfDouble(pf.V), "percent"); return true; }
                    return false;
                case "Probability" when v is QuantV { Dim: "percent" } pq: result = new FloatV(DoubleOfRat(pq.V), pq.V); return true;
                case "Interval":
                    if (v is QuantV { Dim: "interval" }) return true;
                    if (IsNumber(v)) { result = new QuantV(AsRat(v), "interval"); return true; }
                    return false;
                case "RandomSource" or "Seed" or "StableSeed" or "RandomStream":
                    if (v is SeedV) return true;
                    if (v is IntV sn && sn.V.Sign >= 0) { result = new SeedV(new QuantityNode(Rat.Of(sn.V), null, sn.V.ToString(CultureInfo.InvariantCulture), at.Start, at.End), sn.V.ToString(CultureInfo.InvariantCulture)); return true; }
                    return false;
                case "Values": return v is ListV;
            }

            if (!NoteCatalog.Types.TryGetValue(type, out NoteTypeInfo? info)) return false;
            switch (info.Kind)
            {
                case "vocabulary":
                    {
                        string? word = v is WordV w ? w.V : v is StrV s ? s.V : null;
                        if (word is null) return false;
                        bool valid = info.Name == "PitchClass" ? NoteTheory.TryRoot(word, out _) : info.Values.Contains(word);
                        if (!valid) return false;
                        if (v is StrV) Warn("quoted-word", $"\"{word}\" is a quoted word where {A(info.Name)} is expected: write it without quotes", at, word);
                        result = new WordV(word);
                        return true;
                    }

                case "vocabulary-list":
                    {
                        if (v is not StrV chords) return false;
                        return chords.V.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => NoteTheory.TryChord(t, out _, out _));
                    }

                case "refined":
                    {
                        if (v is WordV nameWord && info.NoteNames && TryNoteName(nameWord.V, out int pitch)) { result = new IntV(pitch); return true; }
                        if (info.Base == "Int")
                        {
                            if (v is FloatV ff && (ff.Exact ?? RatOfDouble(ff.V)) is { IsInt: true } exactInt) v = new IntV(exactInt.N);
                            if (v is not IntV iv) return false;
                            if (iv.Brand is { } made && made != info.Name)
                            {
                                why = $"{A(made)} is not {A(info.Name)}: both are whole numbers, but they mean different things (Int(...) or a calculation makes a plain number again)";
                                return false;
                            }

                            result = iv with { Brand = info.Name };
                            return (info.Min is null || iv.V >= info.Min) && (info.Max is null || iv.V <= info.Max);
                        }

                        if (!IsNumber(v)) return false;
                        Rat q = AsRat(v);
                        bool inside = (info.Min is null || q.N >= info.Min * q.D) && (info.Max is null || q.N <= info.Max * q.D);
                        result = v is IntV ? new FloatV(AsDouble(v), q) : v;
                        return inside;
                    }

                default: return false;
            }
        }

        private string SourceOf(Node n) => n.Start >= 0 && n.End <= text.Length && n.End >= n.Start ? text[n.Start..n.End] : "...";

        // ---------- pipelines (also with note bake stages) and value bakes

        private NotesV PipelineVal(PipelineNode p, Env env, bool top)
        {
            Val first = Eval(p.Source, env);
            if (first is not NotesV start) throw new LangFailure("type-mismatch", $"A pipeline starts with notes, not with {Describe(first)}", p.Source);
            NotesV current = start;
            int lastStored = -1;
            for (int i = 0; i < p.Steps.Count; i++)
            {
                if (p.Steps[i] is BakeNode { Stage: { HasBody: true } s } && (s.NotesText is not null || s.File is not null)) lastStored = i;
            }

            bool prefixVolatile = lastStored >= 0 && (IsVolatileNode(p.Source, table) || p.Steps.Take(lastStored).Any(s => IsVolatileNode(s, table)));
            for (int i = 0; i < p.Steps.Count; i++)
            {
                Node step = p.Steps[i];
                if (step is BakeNode b)
                {
                    if (!top) throw new LangFailure("bad-argument", "A note bake stage belongs to the pipeline of a clip, not to a helper", b);
                    BakeStage stage = b.Stage;
                    string accNow = current.Recipe;
                    if (context.StopAt is not null && stage.Name == context.StopAt)
                    {
                        context.StoppedInputHash = Sha("ample-note-bake/1\n" + accNow + Legacy.Extra(accNow));
                        context.StoppedValue = current.V;
                        throw new StopRun(current);
                    }

                    if (!stage.HasBody || (stage.NotesText is null && stage.File is null))
                    {
                        warnings.Add(new PipeIssue("unbaked", $"The bake stage \"{stage.Name}\" has no notes yet", b.Start, b.End));
                        continue;
                    }

                    PipeValue? stored = Legacy.StoredNotes(b);
                    if (stored is null) throw new LangFailure("reported", string.Empty, b);
                    string notesHash = NotesHash(stored);
                    string expected = Sha("ample-note-bake/1\n" + accNow + Legacy.Extra(accNow));
                    if (stage.NotesHash is not null && stage.NotesHash != notesHash)
                        context.StageIssues.Add(new PipeIssue("dirty-bake", $"The notes of the bake stage \"{stage.Name}\" were changed by hand ({(stage.Strict ? "strict" : "warning")}): re-bake, or accept the change", b.Start, b.End) { Strict = stage.Strict });
                    if (stage.InputHash is not null && stage.InputHash != expected && !prefixVolatile)
                        context.StageIssues.Add(new PipeIssue("stale-bake", $"The bake stage \"{stage.Name}\" is stale: what it is made of has changed since it was baked", b.Start, b.End));
                    current = new NotesV(stored, "baked:" + notesHash);
                    continue;
                }

                if (step is not CallNode call) throw new LangFailure("bad-argument", "A pipeline step is an operator or helper call", step);
                if (prefixVolatile && i < lastStored) continue;   // the stored notes of a later stage are authoritative: a volatile step before it is not run
                Val next = Call(call, env, current);
                if (next is not NotesV nv) throw new LangFailure("type-mismatch", $"{call.Name} gave {TypeName(next)}, not Notes", call);
                current = nv with { Tainted = current.Tainted || next.Tainted };
            }

            return current;
        }

        private sealed class StopRun(NotesV value) : Exception
        {
            public NotesV Value { get; } = value;
        }

        private string ExprRecipe(Node expr)
        {
            List<string> helperHashes = [];
            List<string> refRecipes = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            void Visit(Node n)
            {
                foreach (Node d in Descendants(n).Prepend(n))
                {
                    if (d is CallNode c && seen.Add(c.Name) && table.TryHelper(c.Name, out HelperDeclNode hd, out string hh, out _, out _)) { helperHashes.Add(hh); Visit(hd); }
                    if (d is RefNode r && context.Source?.Invoke(r.Block) is { Recipe: { } recipe }) refRecipes.Add(r.Block + ":" + recipe);
                }
            }

            Visit(expr);
            helperHashes.Sort(StringComparer.Ordinal);
            refRecipes.Sort(StringComparer.Ordinal);
            string body = Collapse(text[expr.Start..Math.Min(text.Length, expr.End)]) + "\n" + string.Join(",", helperHashes) + "\n" + string.Join(",", refRecipes);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ample-value-bake/1\n" + body + Legacy.Extra(body))))[..8];
        }

        private static bool SameValue(Val a, Val b) => ExactText(a) == ExactText(b);

        private Val BakeValue(BakeValueNode b, Env env)
        {
            Val baked = Eval(b.Value, env) with { Tainted = false };
            LastBake = null;
            if (baked is NotesV) throw new LangFailure("type-mismatch", "A value bake keeps a value (a number, a length, a word, a string); notes are baked with |> bake name { ... }", b.Value);
            if (IsVolatileNode(b.Expr, table)) { BakedDynamic = true; LastBake = null; return baked; }   // a volatile expression is not run: the baked value is the value
            Val current = Eval(b.Expr, env);
            string hash = ExprRecipe(b.Expr);
            string resign = $"as {ValText(current)} @{hash}";
            LastBake = new BakeInfo(ValText(baked), ValText(current), hash, b.Override, b.Hash is not null && b.Hash != hash, b.Hash is not null && b.Hash == hash && !b.Override && !SameValue(baked, current));
            PipeIssue Issue(string id, string message, string? suggestion) => new(id, message, b.AsStart, b.End, suggestion);
            if (b.Hash is not null && b.Hash != hash) warnings.Add(Issue("stale-value-bake", $"This baked value is stale: what {SourceOf(b.Expr)} is made of has changed since it was baked (it now resolves to {ValText(current)})", resign));
            else if (b.Override) warnings.Add(Issue("value-bake-override", $"The baked value {ValText(baked)} is kept on purpose; the expression now resolves to {ValText(current)}", null));
            else if (b.Hash is not null && !SameValue(baked, current)) warnings.Add(Issue("dirty-value-bake", $"The baked value {ValText(baked)} was changed by hand (the expression resolves to {ValText(current)}): write override to keep it, or bake it again", resign));
            return baked;
        }
    }
}
