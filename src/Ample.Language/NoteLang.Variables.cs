using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>What the editors say about a variable: a <c>let</c>, a <c>var</c>, a constant of the file, a parameter or a loop variable of a helper.</summary>
/// <param name="Name">The name.</param>
/// <param name="Kind">let, var, const, parameter or loop variable.</param>
/// <param name="Type">The type: the declared one, else what the value is worked out to be (a union is written A | B).</param>
/// <param name="Potential">The types the variable can have, when more than one (a var that is assigned values of other types).</param>
/// <param name="Origin">How the type was found (a notes literal, the call of transpose, the declared type ...).</param>
/// <param name="ValueText">The value as written (shortened), or empty.</param>
public sealed record VariableDetail(string Name, string Kind, string Type, IReadOnlyList<string> Potential, string Origin, string ValueText);

/// <summary>A place in a declaration where a variable is declared or used.</summary>
/// <param name="Line">The 0-based line where the declaration (helper or constant) starts.</param>
/// <param name="Start">The offset of the name, counted from the start of that declaration.</param>
/// <param name="End">The end offset.</param>
/// <param name="Keyword">Whether this is the let or var keyword of the declaration (not the name).</param>
/// <param name="Detail">What is known about the variable.</param>
public sealed record VariableSpot(int Line, int Start, int End, bool Keyword, VariableDetail Detail);

public static partial class NotePipeline
{
    public sealed partial class HelperTable
    {
        /// <summary>Gets the places where variables are declared and used in the helpers and constants, with the types that were worked out without running anything (docs/language/18-comments-and-doc-comments.md).</summary>
        public IReadOnlyList<VariableSpot> VariableSpots { get; private set; } = [];

        private sealed class VarInfo(string name, string kind)
        {
            public string Name { get; } = name;

            public string Kind { get; } = kind;

            public List<string> Types { get; } = [];

            public string? Declared { get; set; }

            public string Origin { get; set; } = string.Empty;

            public string ValueText { get; set; } = string.Empty;

            public VariableDetail Detail() => new(Name, Kind, Declared ?? (Types.Count == 0 ? "unknown" : string.Join(" | ", Types)), Declared is null && Types.Count > 1 ? Types : [], Declared is null ? Origin : "the declared type", ValueText);
        }

        private static readonly string[] Quantities = ["Length", "Time", "Interval", "Gain", "Percent"];

        private readonly Dictionary<string, List<string>> inferredReturns = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The type a helper gives back, worked out from its body (what it returns, and Notes when it emits), for a helper that does not say it: a union is written A | B. Null when the helper says it, or nothing is known.</summary>
        /// <param name="name">The helper name.</param>
        /// <returns>The type, or null.</returns>
        public string? InferredReturn(string name) => inferredReturns.TryGetValue(name, out List<string>? types) && types.Count > 0 ? string.Join(" | ", types) : null;

        private static bool IsNumber(string t) => t is "Int" or "Float";

        private static string? Combine(string op, string a, string b)
        {
            if (op == "+" && a == "String" && b == "String") return "String";
            if (IsNumber(a) && IsNumber(b)) return op == "/" ? "Float" : a == "Float" || b == "Float" ? "Float" : "Int";
            bool qa = Quantities.Contains(a), qb = Quantities.Contains(b);
            if (qa && IsNumber(b) && op is "*" or "/") return a;
            if (IsNumber(a) && qb && op == "*") return b;
            if (qa && qb)
            {
                if (op == "/" && a == b) return "Float";
                if (op is "+" or "-" or "%") return a == b ? a : a is "Length" or "Time" && b is "Length" or "Time" ? "Duration" : null;
            }

            return null;
        }

        private static string? ElementOf(string type)
        {
            Match m = Regex.Match(type, @"^(?:List|Range)<(.+)>$", RegexOptions.None, TimeSpan.FromSeconds(1));
            return m.Success ? m.Groups[1].Value : type == "Notes" ? "Note" : null;
        }

        private static string Shorten(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim() is { Length: > 90 } long90 ? long90[..90] + " …" : Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

        /// <summary>Walks every helper and constant like the checker does, resolving names through the scopes, and works out the type of every variable from its value.</summary>
        private void WorkOutVariables(List<Decl> all)
        {
            List<VariableSpot> spots = [];
            Dictionary<string, VarInfo> constantInfos = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> busy = new(StringComparer.OrdinalIgnoreCase);

            VarInfo ConstantInfo(string name)
            {
                if (constantInfos.TryGetValue(name, out VarInfo? known)) return known;
                VarInfo info = new(name, "const");
                constantInfos[name] = info;
                Decl d = constants[name];
                ConstDeclNode node = (ConstDeclNode)d.Node;
                info.ValueText = Shorten(d.Text[node.Value.Start..Math.Min(d.Text.Length, node.Value.End)]);
                if (node.Type is not null) info.Declared = node.Type;
                else if (busy.Add(name))
                {
                    (List<string> types, string origin) = Infer(node.Value, [], d);
                    info.Types.AddRange(types);
                    info.Origin = origin;
                    busy.Remove(name);
                }

                return info;
            }

            (List<string> Types, string Origin) Infer(Node? n, List<Dictionary<string, VarInfo>> scopes, Decl d)
            {
                List<string> found = [];
                string origin = string.Empty;
                void Add(string? t) { if (t is not null && !found.Contains(t)) found.Add(t); }
                VarInfo? Find(string name) { for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].TryGetValue(name, out VarInfo? v)) return v; return null; }
                List<string> TypesOf(VarInfo v) => v.Declared is not null ? [v.Declared] : v.Types;
                switch (n)
                {
                    case null: break;
                    case LiteralNode or EditableNode or RefNode: Add("Notes"); origin = n is LiteralNode ? "a list of notes" : n is RefNode ? "the notes of another block" : "editable notes"; break;
                    case QuantityNode q: Add(string.IsNullOrEmpty(q.Unit) ? (q.Value.IsInt ? "Int" : "Float") : q.Unit switch { "bar" or "bars" or "beat" or "beats" or "tick" or "ticks" => "Length", "s" or "ms" => "Time", "st" or "oct" or "cents" => "Interval", "dB" or "db" => "Gain", "%" => "Percent", _ => "Number" }); origin = string.IsNullOrEmpty(q.Unit) ? "a number" : $"a number with the unit {q.Unit}"; break;
                    case StringNode: Add("String"); origin = "a string"; break;
                    case BoolNode: Add("Bool"); origin = "true or false"; break;
                    case ListNode l:
                        {
                            List<string> items = [];
                            foreach (Node item in l.Items) foreach (string t in Infer(item, scopes, d).Types) if (!items.Contains(t)) items.Add(t);
                            Add(items.Count == 0 ? "List" : $"List<{string.Join(" | ", items)}>");
                            origin = "a list";
                            break;
                        }
                    case RangeExprNode r:
                        {
                            List<string> ends = [.. Infer(r.From, scopes, d).Types, .. Infer(r.To, scopes, d).Types];
                            Add(ends.Count == 0 ? "Range" : $"Range<{string.Join(" | ", ends.Distinct())}>");
                            origin = "a range";
                            break;
                        }
                    case RangeNode rn: Add("Range<" + (string.IsNullOrEmpty(rn.From.Unit) ? "Int" : Infer(rn.From, scopes, d).Types.FirstOrDefault() ?? "Number") + ">"); origin = "a range"; break;
                    case NamedNode nm: return Infer(nm.Value, scopes, d);
                    case TernaryNode tn:
                        foreach (string t in Infer(tn.Then, scopes, d).Types.Concat(Infer(tn.Otherwise, scopes, d).Types)) Add(t);
                        origin = "a conditional: the types of both branches";
                        break;
                    case BakeValueNode bv: return Infer(bv.Expr, scopes, d);
                    case UnaryNode u:
                        if (u.Op == "!") { Add("Bool"); origin = "a negation"; }
                        else { foreach (string t in Infer(u.Operand, scopes, d).Types) Add(t); origin = "a signed value"; }
                        break;
                    case BinaryNode b:
                        if (b.Op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "is" or "and" or "or") { Add("Bool"); origin = "a comparison"; break; }
                        foreach (string x in Infer(b.Left, scopes, d).Types) foreach (string y in Infer(b.Right, scopes, d).Types) Add(Combine(b.Op, x, y));
                        origin = "arithmetic on the types of both sides";
                        break;
                    case WordNode w:
                        {
                            if (Find(w.Text) is { } local) { foreach (string t in TypesOf(local)) Add(t); origin = $"the {local.Kind} {local.Name}"; break; }
                            if (constants.ContainsKey(w.Text)) { VarInfo c = ConstantInfo(w.Text); foreach (string t in TypesOf(c)) Add(t); origin = $"the constant {c.Name}"; break; }
                            if (w.Text is "true" or "false") { Add("Bool"); origin = "true or false"; break; }
                            if (Regex.IsMatch(w.Text, "^[A-G][#b]?-?[0-9]$", RegexOptions.None, TimeSpan.FromSeconds(1))) { Add("Pitch"); origin = "a note name"; break; }
                            if (Regex.IsMatch(w.Text, "^[A-G][#b]?$", RegexOptions.None, TimeSpan.FromSeconds(1))) { Add("PitchClass"); origin = "a pitch class"; break; }
                            foreach (NoteTypeInfo t in NoteCatalog.Types.Values.Where(t => t.Values.Contains(w.Text, StringComparer.OrdinalIgnoreCase))) Add(t.Name);
                            foreach ((string aliasName, TypeDeclNode alias) in types.Select(kv => (kv.Key, kv.Value)).Where(kv => kv.Value.Members.Contains(w.Text, StringComparer.OrdinalIgnoreCase))) Add(alias.Name);
                            origin = "a word of the language";
                            break;
                        }
                    case MethodNode m:
                        if (NoteCatalog.Builtins.TryGetValue(m.Name, out NoteSignature? method) && method.Kind == "method") { Add(method.Returns); origin = $"the method {m.Name}()"; }
                        break;
                    case FieldNode f:
                        if (Infer(f.Target, scopes, d).Types.Contains("Now")) { Add("Int"); origin = $"the field {f.Name} of the time"; }
                        break;
                    case PipelineNode p:
                        {
                            Node? last = p.Steps.Count > 0 ? p.Steps[^1] : p.Source;
                            (List<string> types, string o) = Infer(last, scopes, d);
                            foreach (string t in types) Add(t);
                            origin = o;
                            break;
                        }
                    case CallNode c:
                        {
                            if (helpers.TryGetValue(c.Name, out Decl? helper) && helper.Node is HelperDeclNode hn)
                            {
                                if (hn.Returns is not null) Add(hn.Returns);
                                else if (inferredReturns.TryGetValue(hn.Name, out List<string>? worked)) foreach (string t in worked) Add(t);
                                origin = $"the call of {hn.Name}()" + (hn.Returns is null ? " (its type is worked out from its body)" : string.Empty);
                                break;
                            }

                            if (NoteCatalog.Signatures.TryGetValue(c.Name, out NoteSignature? sig) && c.Name is not ("random" or "choose" or "chance"))
                            {
                                Add(sig.Returns);
                                origin = $"the call of {c.Name}()";
                                break;
                            }

                            if (c.Name is "min" or "max" or "abs")
                            {
                                foreach (Node a in c.Args.Take(c.Name == "abs" ? 1 : 2)) foreach (string t in Infer(a is NamedNode na ? na.Value : a, scopes, d).Types) Add(t);
                                origin = $"the call of {c.Name}() on the types of its arguments";
                                break;
                            }

                            if (c.Name == "at" && c.Args.Count > 0)
                            {
                                foreach (string t in Infer(c.Args[0], scopes, d).Types) Add(ElementOf(t));
                                origin = "an item of a list";
                                break;
                            }

                            if (c.Name is "random" or "choose" or "chance")
                            {
                                if (c.Name == "chance") Add("Int");
                                else if (c.Name == "choose" && c.Args.Count > 0 && Infer(c.Args[0], scopes, d).Types.Select(ElementOf).FirstOrDefault(e => e is not null) is { } element) Add(element);
                                else Add(NoteCatalog.Signatures.TryGetValue(c.Name, out NoteSignature? drawn) ? drawn.Returns : "Number");
                                origin = $"the call of {c.Name}()";
                                break;
                            }

                            if (NoteCatalog.Builtins.TryGetValue(c.Name, out NoteSignature? builtin) && builtin.Kind == "function") { Add(builtin.Returns); origin = $"the call of {builtin.Id}()"; }
                            break;
                        }
                }

                return (found, origin);
            }

            string Cut(Decl d, int from, int to) => d.Text.Length >= to && to > from ? Shorten(d.Text[from..to]) : string.Empty;

            void Spot(Decl d, int start, int end, VarInfo info, bool keyword = false) => spots.Add(new VariableSpot(d.Line, start, end, keyword, info.Detail()));

            // the spots are made at the end, when every var has seen all of its assignments
            List<(Decl D, int Start, int End, VarInfo Info, bool Keyword)> pending = [];
            void Mark(Decl d, int start, int end, VarInfo info, bool keyword = false) => pending.Add((d, start, end, info, keyword));

            // three rounds: what a helper gives back (worked out from its body) is known to the helpers that call it in the next round
            for (int round = 0; round < 3; round++)
            {
                constantInfos.Clear();
                pending.Clear();
                foreach (Decl d in all)
                {
                    if (d.Node is ConstDeclNode cd)
                    {
                        VarInfo info = ConstantInfo(cd.Name);
                        Mark(d, cd.NameStart, cd.NameEnd, info);
                        Mark(d, cd.Start, Math.Min(cd.NameStart, cd.Start + 3), info, true);
                        foreach (WordNode w in Descendants(cd.Value).OfType<WordNode>().Where(w => constants.ContainsKey(w.Text) && !string.Equals(w.Text, cd.Name, StringComparison.OrdinalIgnoreCase))) Mark(d, w.Start, w.End, ConstantInfo(w.Text));
                        continue;
                    }

                    if (d.Node is not HelperDeclNode h) continue;
                    List<Dictionary<string, VarInfo>> scopes = [new Dictionary<string, VarInfo>(StringComparer.Ordinal)];
                    List<string> returnTypes = [];
                    foreach (ParamNode p in h.Params)
                    {
                        VarInfo info = new(p.Name, "parameter") { Declared = p.Type };
                        scopes[0][p.Name] = info;
                        int at = d.Text.IndexOf(p.Name, Math.Max(0, p.Start), StringComparison.Ordinal);
                        if (at >= 0) Mark(d, at, at + p.Name.Length, info);
                    }

                    void References(Node? n)
                    {
                        if (n is null) return;
                        foreach (WordNode w in Descendants(n).Prepend(n).OfType<WordNode>())
                        {
                            for (int i = scopes.Count - 1; i >= 0; i--)
                            {
                                if (scopes[i].TryGetValue(w.Text, out VarInfo? v)) { Mark(d, w.Start, w.End, v); goto next; }
                            }

                            if (constants.ContainsKey(w.Text)) Mark(d, w.Start, w.End, ConstantInfo(w.Text));
                            next:;
                        }
                    }

                    void Block(StmtBlock block, string? loopVar = null, ForStmt? loop = null)
                    {
                        scopes.Add(new Dictionary<string, VarInfo>(StringComparer.Ordinal));
                        if (loopVar is not null && loop is not null)
                        {
                            VarInfo info = new(loopVar, "loop variable");
                            (List<string> fromTypes, string o) = Infer(loop.From, scopes.Take(scopes.Count - 1).ToList(), d);
                            if (loop.To is not null) { foreach (string t in fromTypes) if (!info.Types.Contains(t)) info.Types.Add(t); info.Origin = "the start of the range"; }
                            else { foreach (string? t in fromTypes.Select(ElementOf)) if (t is not null && !info.Types.Contains(t)) info.Types.Add(t); info.Origin = "the items of " + o; }
                            scopes[^1][loopVar] = info;
                            int at = d.Text.IndexOf(loopVar, loop.Start + 3, StringComparison.Ordinal);
                            if (at >= 0) Mark(d, at, at + loopVar.Length, info);
                        }

                        foreach (Node s in block.Statements)
                        {
                            switch (s)
                            {
                                case LetStmt l:
                                    {
                                        References(l.Value);
                                        VarInfo info = new(l.Name, l.Mutable ? "var" : "let") { ValueText = Cut(d, l.Value.Start, l.Value.End) };
                                        if (l.Type is not null) info.Declared = l.Type;
                                        else { (List<string> t, string o) = Infer(l.Value, scopes, d); info.Types.AddRange(t); info.Origin = o; }
                                        scopes[^1][l.Name] = info;
                                        int at = d.Text.IndexOf(l.Name, l.Start + 3, StringComparison.Ordinal);
                                        if (at >= 0) Mark(d, at, at + l.Name.Length, info);
                                        Mark(d, l.Start, l.Start + 3, info, true);
                                        break;
                                    }

                                case AssignStmt a:
                                    {
                                        References(a.Value);
                                        VarInfo? target = null;
                                        for (int i = scopes.Count - 1; i >= 0 && target is null; i--) { if (scopes[i].TryGetValue(a.Name, out VarInfo? hit)) target = hit; }
                                        if (target is not null)
                                        {
                                            if (target.Declared is null) foreach (string t in Infer(a.Value, scopes, d).Types) if (!target.Types.Contains(t)) target.Types.Add(t);
                                            Mark(d, a.Start, a.Start + a.Name.Length, target);
                                        }

                                        break;
                                    }

                                case IfStmt f:
                                    foreach ((Node cond, StmtBlock body) in f.Branches) { References(cond); Block(body); }
                                    if (f.Otherwise is not null) Block(f.Otherwise);
                                    break;
                                case ForStmt fs:
                                    References(fs.From);
                                    References(fs.To);
                                    Block(fs.Body, fs.Var, fs);
                                    break;
                                case RepeatStmt rs:
                                    References(rs.Count);
                                    Block(rs.Body);
                                    break;
                                case WhileStmt ws:
                                    References(ws.Cond);
                                    References(ws.Max);
                                    Block(ws.Body);
                                    break;
                                case ReturnStmt r:
                                    References(r.Value);
                                    if (r.Value is not null) foreach (string t in Infer(r.Value, scopes, d).Types) if (!returnTypes.Contains(t)) returnTypes.Add(t);
                                    break;
                                case EmitStmt em:
                                    References(em.Value);
                                    if (!returnTypes.Contains("Notes")) returnTypes.Add("Notes");
                                    break;
                                case ExprStmt ex:
                                    References(ex.Value);
                                    if (ReferenceEquals(block, h.Body) && ReferenceEquals(s, block.Statements[^1])) foreach (string t in Infer(ex.Value, scopes, d).Types) if (!returnTypes.Contains(t)) returnTypes.Add(t);   // the last expression is the value
                                    break;
                            }
                        }

                        scopes.RemoveAt(scopes.Count - 1);
                    }

                    Block(h.Body);
                    if (h.Returns is null) inferredReturns[h.Name] = returnTypes;
                }
            }

            foreach ((Decl d, int start, int end, VarInfo info, bool keyword) in pending) Spot(d, start, end, info, keyword);
            VariableSpots = spots;
        }
    }
}
