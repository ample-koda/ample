
namespace Ample.Language;

public static partial class NotePipeline
{
    private sealed partial class ExprParser
    {
        /// <summary>Gets a value indicating whether this reads the process of an audio component: <c>emit ... -&gt; output</c> names the output, and a stage may be a nested component (<c>koda.fx.echo(...)</c>, <c>koda.fx.echo@1.0.0(...)</c>). Server-only, like the audio graph builder.</summary>
        internal bool AudioProcess { get; init; }

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "let", "var", "if", "else", "for", "in", "repeat", "while", "max", "stop", "return", "emit", "true", "false", "helper", "dynamic", "bake", "as", "override", "type", "is" };

        /// <summary>Parses the whole text as one declaration: a helper, a type alias or a file-wide constant.</summary>
        public Node ParseDeclaration()
        {
            Tok first = Peek() ?? throw new SyntaxError("The declaration is empty", 0, Math.Max(1, Text.Length));
            bool dynamic = false;
            if (first.Kind == T.Ident && first.Text == "dynamic") { p++; dynamic = true; }
            Tok kw = Expect(T.Ident, "helper, type, error or let");
            Node result;
            switch (kw.Text)
            {
                case "helper": result = HelperDecl(first, dynamic); break;
                case "type" when !dynamic: result = TypeDecl(first); break;
                case "error" when !dynamic:
                    if (!SyntaxGate.Supports(SyntaxFeatures.ErrorDecl)) throw new SyntaxError("\"error\" is not supported yet in this AMPLE version", kw.Start, kw.End, "feature-off");
                    result = ErrorDecl(first);
                    break;
                case "let" when !dynamic: result = ConstDecl(first); break;
                default: throw new SyntaxError("Expected helper, dynamic helper, type or let", kw.Start, kw.End);
            }

            if (p < Tokens.Count) throw new SyntaxError("Unexpected input after the declaration", Tokens[p].Start, Tokens[p].End);
            return result;
        }

        private string TypeText()
        {
            Tok name = Expect(T.Ident, "a type");
            if (Peek()?.Kind != T.Lt) return name.Text;
            p++;
            string inner = TypeText();
            Expect(T.Gt, "\">\"");
            return name.Text + "<" + inner + ">";
        }

        private HelperDeclNode HelperDecl(Tok first, bool dynamic)
        {
            Tok name = Expect(T.Ident, "the name of the helper");
            Expect(T.LParen, "\"(\"");
            List<ParamNode> parameters = [];
            if (Peek()?.Kind != T.RParen)
            {
                while (true)
                {
                    string type = TypeText();
                    Tok pn = Expect(T.Ident, "the name of the parameter");
                    Node? def = null;
                    if (Peek()?.Kind == T.Eq) { p++; def = Arg(); }
                    parameters.Add(new ParamNode(pn.Text, type, def, pn.Start, pn.End));
                    if (Peek()?.Kind == T.Comma) { p++; continue; }
                    break;
                }
            }

            Expect(T.RParen, "\")\"");
            string? returns = null;
            if (Peek()?.Kind == T.Arrow) { p++; returns = TypeText(); }
            Node? baked = null;
            if (Peek() is { Kind: T.Ident, Text: "bake" })
            {
                p++;
                Tok asWord = Expect(T.Ident, "as");
                if (asWord.Text != "as") throw new SyntaxError("Expected: bake as <value>", asWord.Start, asWord.End);
                baked = Arg();
            }
            StmtBlock body = Block();
            return new HelperDeclNode(name.Text, dynamic, parameters, returns, baked, body, first.Start, body.End, name.Start, name.End);
        }

        private TypeDeclNode TypeDecl(Tok first)
        {
            Tok name = Expect(T.Ident, "the name of the type");
            Expect(T.Eq, "\"=\"");
            List<string> members = [TypeText()];
            while (Peek() is { Kind: T.Ident, Text: "or" } or { Kind: T.Bar }) { p++; members.Add(TypeText()); }
            return new TypeDeclNode(name.Text, members, first.Start, Tokens[p - 1].End);
        }

        private ErrorDeclNode ErrorDecl(Tok first)
        {
            Tok name = Expect(T.Ident, "the name of the error");
            string? parent = null;
            if (Peek() is { Kind: T.Ident, Text: "extends" }) { p++; parent = Expect(T.Ident, "the error it extends").Text; }
            Tok open = Expect(T.LBrace, "\"{\"");
            List<ErrorFieldDecl> fields = [];
            while (Peek()?.Kind != T.RBrace)
            {
                if (Peek() is null) throw new SyntaxError("The error declaration is not closed", open.Start, Text.Length);
                Tok start = Peek()!;
                bool dynamic = false;
                if (start is { Kind: T.Ident, Text: "dynamic" } && Peek(1)?.Kind == T.Ident && Peek(2)?.Kind is T.Ident or T.Lt) { p++; dynamic = true; }
                string type = TypeText();
                Tok field = Expect(T.Ident, "the name of the field");
                if (fields.Any(f => f.Name == field.Text)) throw new SyntaxError($"The field \"{field.Text}\" is declared twice", field.Start, field.End);
                fields.Add(new ErrorFieldDecl(dynamic, type, field.Text, start.Start, field.End));
                if (Peek()?.Kind == T.Comma) p++;
            }

            Tok close = Expect(T.RBrace, "\"}\"");
            return new ErrorDeclNode(name.Text, parent, fields, first.Start, close.End);
        }

        private ConstDeclNode ConstDecl(Tok first)
        {
            string? type = null;
            if (Peek()?.Kind == T.Ident && Peek(1)?.Kind == T.Ident && Peek(2)?.Kind == T.Eq) type = TypeText();
            Tok name = Expect(T.Ident, "the name of the constant");
            Expect(T.Eq, "\"=\"");
            Node value = Pipeline();
            return new ConstDeclNode(name.Text, type, value, first.Start, value.End, name.Start, name.End);
        }

        private TryStmt TryStatement()
        {
            Tok word = Peek()!;
            p++;
            StmtBlock body = Block();
            List<CatchClause> catches = [];
            StmtBlock? last = null;
            int end = body.End;
            while (Peek() is { Kind: T.Ident, Text: "catch" } catchWord)
            {
                p++;
                Expect(T.LParen, "\"(\" after catch");
                Tok first = Expect(T.Ident, "the name of the error");
                string? type = null;
                Tok name = first;
                if (Peek()?.Kind == T.Ident) { type = first.Text; name = Expect(T.Ident, "the name of the error"); }
                Expect(T.RParen, "\")\"");
                StmtBlock catchBody = Block();
                catches.Add(new CatchClause(type, name.Text, catchBody, catchWord.Start, catchBody.End));
                end = catchBody.End;
            }

            if (Peek() is { Kind: T.Ident, Text: "finally" })
            {
                p++;
                last = Block();
                end = last.End;
                foreach (Node inner in Descendants(last))
                {
                    if (inner is ReturnStmt or EmitStmt or ObserveStmt { Kind: "throw" })
                        throw new SyntaxError(
                            "A finally block cannot return, emit or throw: it can only observe, check and clean up, so it can never replace the outcome of the try",
                            inner.Start, inner.End, "finally-flow");
                }
            }

            if (catches.Count == 0 && last is null) throw new SyntaxError("try needs a catch or a finally", word.Start, word.End);
            return new TryStmt(body, catches, last, word.Start, end);
        }

        private StmtBlock Block()
        {
            Tok open = Expect(T.LBrace, "\"{\"");
            List<Node> statements = [];
            Node? previous = null;
            while (true)
            {
                bool separated = false;
                while (Peek()?.Kind == T.Semi) { p++; separated = true; }
                Tok? k = Peek();
                if (k is null) throw new SyntaxError("The block is not closed", open.Start, Text.Length);
                if (k.Kind == T.RBrace) { p++; return new StmtBlock(statements, open.Start, k.End); }
                // two statements on one line need a ";" between them (a statement that ends with a block, like if or for, ends there by itself): "re turn 3" is a typo, not three statements
                bool endsWithBlock = previous is IfStmt or ForStmt or RepeatStmt or WhileStmt or TryStmt;
                if (previous is not null && !endsWithBlock && !separated && previous.End <= k.Start && !Text[previous.End..k.Start].Contains('\n', StringComparison.Ordinal))
                    throw new SyntaxError($"Statements on one line are separated by \";\": write \";\" before \"{k.Text}\", or put it on a new line", k.Start, k.End);
                previous = Statement();
                statements.Add(previous);
            }
        }

        private Node Statement()
        {
            Tok k = Peek()!;
            if (KeywordStatement(k) is { } keyword) return keyword;
            if (k.Kind == T.Ident)
            {
                switch (k.Text)
                {
                    case "let" or "var":
                        {
                            p++;
                            string? type = null;
                            if (Peek()?.Kind == T.Ident && Peek(1)?.Kind == T.Ident && Peek(2)?.Kind == T.Eq) type = TypeText();
                            Tok name = Expect(T.Ident, "a name");
                            Expect(T.Eq, "\"=\"");
                            Node value = Pipeline();
                            return new LetStmt(name.Text, type, value, k.Text == "var", k.Start, value.End);
                        }

                    case "if": return IfStatement();
                    case "try" when Peek(1)?.Kind == T.LBrace: return TryStatement();
                    case "for":
                        {
                            p++;
                            Tok name = Expect(T.Ident, "the loop variable");
                            Tok inWord = Expect(T.Ident, "in");
                            if (inWord.Text != "in") throw new SyntaxError("Expected \"in\"", inWord.Start, inWord.End);
                            Node from = Binary(0);
                            Node? to = null;
                            bool inclusive = false;
                            if (Peek()?.Kind is T.Dots or T.DotsEq) { inclusive = Peek()!.Kind == T.DotsEq; p++; to = Binary(0); }
                            StmtBlock body = Block();
                            return new ForStmt(name.Text, from, to, inclusive, body, k.Start, body.End);
                        }

                    case "repeat":
                        {
                            p++;
                            Node count = Binary(0);
                            StmtBlock body = Block();
                            return new RepeatStmt(count, body, k.Start, body.End);
                        }

                    case "while":
                        {
                            p++;
                            Node cond = Binary(0);
                            Node? max = null;
                            bool stop = false;
                            if (Peek() is { Kind: T.Ident, Text: "max" }) { p++; max = Binary(0); if (Peek() is { Kind: T.Ident, Text: "stop" }) { p++; stop = true; } }
                            StmtBlock body = Block();
                            return new WhileStmt(cond, max, stop, body, k.Start, body.End);
                        }

                    case "return":
                        {
                            p++;
                            Tok? after = Peek();
                            if (after is null || after.Kind is T.RBrace or T.Semi) return new ReturnStmt(null, k.Start, k.End);
                            Node value = Pipeline();
                            return new ReturnStmt(value, k.Start, value.End);
                        }

                    case "emit":
                        {
                            p++;
                            Node value = Pipeline();
                            if (AudioProcess && Peek()?.Kind == T.Arrow && Peek(1)?.Kind == T.Ident) { Tok output = Peek(1)!; p += 2; return new EmitStmt(value, k.Start, output.End); }   // emit ... -> output
                            return new EmitStmt(value, k.Start, value.End);
                        }
                }

                if (Peek(1)?.Kind == T.Eq && !Keywords.Contains(k.Text))
                {
                    p += 2;
                    Node value = Pipeline();
                    return new AssignStmt(k.Text, value, k.Start, value.End);
                }

                if (Peek(1)?.Kind == T.Ident && Peek(2)?.Kind == T.Eq && !Keywords.Contains(k.Text) && !Keywords.Contains(Peek(1)!.Text))
                {
                    string type = TypeText();
                    Tok name = Expect(T.Ident, "a name");
                    Expect(T.Eq, "\"=\"");
                    Node value = Pipeline();
                    return new LetStmt(name.Text, type, value, false, k.Start, value.End);
                }
            }

            Node expression = Pipeline();
            return new ExprStmt(expression, expression.Start, expression.End);
        }

        private static int Prec(T kind) => kind switch { T.OrOr => 1, T.AndAnd => 2, T.EqEq or T.NotEq or T.Lt or T.Gt or T.Le or T.Ge => 3, T.Plus or T.Minus => 4, T.Star or T.Slash or T.Mod => 5, _ => 0 };

        private Node Binary(int min) => Climb(Unary(), min);

        private Node Unary()
        {
            Tok? k = Peek();
            if (k is not null && k.Kind is T.Bang or T.Minus)
            {
                p++;
                Node operand = Unary();
                return new UnaryNode(k.Text, operand, k.Start, operand.End);
            }

            return Atom();
        }

        private Node Climb(Node left, int min)
        {
            while (true)
            {
                Tok? t = Peek();
                if (t is null) break;
                bool signedNumber = t.Kind == T.Number && t.Text.Length > 1 && t.Text[0] is '+' or '-';
                bool isWord = t.Kind == T.Ident && t.Text == "is";
                int prec = signedNumber ? 4 : isWord ? 3 : Prec(t.Kind);
                if (prec == 0 || prec < min) break;
                if (signedNumber)
                {
                    p++;
                    Rat v = ParseDecimal(t.Text[1..])!.Value;
                    QuantityNode q = new(v, t.Unit, t.Text[1..] + t.Unit, t.Start + 1, t.End);
                    Node rightSigned = Climb(q, prec + 1);
                    left = new BinaryNode(t.Text[0].ToString(), left, rightSigned, left.Start, rightSigned.End);
                    continue;
                }

                p++;
                Node right = Binary(prec + 1);
                left = new BinaryNode(t.Text, left, right, left.Start, right.End);
            }

            return left;
        }

        /// <summary>What follows a simple argument (a word, a number, a string): arithmetic, a range of expressions, or the steps of a pipeline.</summary>
        private Node ArgTail(Node first)
        {
            Node e = TernaryTail(Climb(first, 0));
            if (Peek()?.Kind is T.Dots or T.DotsEq && e is not QuantityNode)
            {
                bool inclusive = Peek()!.Kind == T.DotsEq;
                p++;
                Node to = Binary(0);
                return new RangeExprNode(e, to, inclusive, e.Start, to.End);
            }

            return PipeTail(e);
        }

        /// <summary>The grouped form (a-b): the hyphen is a subtraction between its parts (names and numbers), whatever symbols exist.</summary>
        private static Node? HyphenMath(Tok token)
        {
            string[] parts = token.Text.Split('-');
            if (parts.Length < 2 || parts.Any(x => x.Length == 0)) return null;
            int at = token.Start;
            Node? result = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                Node operand = part.All(char.IsDigit) ? new QuantityNode(ParseDecimal(part)!.Value, null, part, at, at + part.Length) : new WordNode(part, at, at + part.Length);
                result = result is null ? operand : new BinaryNode("-", result, operand, result.Start, operand.End);
                at += part.Length + 1;
            }

            return result;
        }

        private Node ListLiteral()
        {
            if (Peek(1) is { Kind: T.Ident } sym && Peek(2)?.Kind == T.RBracket)
            {
                // [name] is the symbol itself (always allowed, only needed when a hyphenated name is ambiguous); a one-element list of a name is written [name,]
                Tok open1 = Peek()!;
                Tok close1 = Peek(2)!;
                p += 3;
                if (Peek()?.Kind == T.Dot && Peek(1)?.Kind == T.Ident && Peek(2)?.Kind != T.LParen)
                {
                    Tok port = Peek(1)!;
                    p += 2;
                    return new RefNode(sym.Text, port.Text, open1.Start, port.End, true);
                }

                return new WordNode(sym.Text, open1.Start, close1.End, true);
            }

            Tok open = Expect(T.LBracket, "\"[\"");
            List<Node> items = [];
            if (Peek()?.Kind != T.RBracket)
            {
                while (true)
                {
                    items.Add(Arg());
                    if (Peek()?.Kind == T.Comma) { p++; if (Peek()?.Kind == T.RBracket) break; continue; }
                    break;
                }
            }

            Tok close = Expect(T.RBracket, "\"]\"");
            return new ListNode(items, open.Start, close.End);
        }

        private BakeValueNode BakeValue()
        {
            Tok word = Expect(T.Ident, "bake");
            Node expr = Binary(0);
            Tok asWord = Expect(T.Ident, "as");
            if (asWord.Text != "as") throw new SyntaxError("Expected \"as\" and the value: bake <expression> as <value>", asWord.Start, asWord.End);
            Node value = Binary(0);
            string? hash = null;
            int end = value.End;
            if (Peek()?.Kind == T.Hash) { hash = Peek()!.Text; end = Peek()!.End; p++; }
            bool over = false;
            if (Peek() is { Kind: T.Ident, Text: "override" }) { over = true; end = Peek()!.End; p++; }
            return new BakeValueNode(expr, value, hash, over, asWord.Start, word.Start, end);
        }

        private static readonly Dictionary<string, SyntaxFeatures> StatementWords = new(StringComparer.Ordinal)
        {
            ["log"] = SyntaxFeatures.Observe,
            ["debug"] = SyntaxFeatures.Observe,
            ["trace"] = SyntaxFeatures.Observe,
            ["warn"] = SyntaxFeatures.Observe,
            ["assert"] = SyntaxFeatures.Assert,
            ["require"] = SyntaxFeatures.Require,
            ["throw"] = SyntaxFeatures.Throw,
            ["fail"] = SyntaxFeatures.Fail,
        };

        /// <summary>Reads a text that is exactly one observation statement, or gives null (anything else, a mistake, or a feature that is off).</summary>
        public ObserveStmt? TryObservation()
        {
            if (Peek() is not { Kind: T.Ident } k || KeywordStatement(k) is not { } s || p < Tokens.Count) return null;
            return s;
        }

        private static readonly HashSet<string> ObserveNames = new(StringComparer.Ordinal) { "code", "hint", "context" };

        /// <summary>The words that start the statements of the diagnostics work. A word whose feature is off is a clear error (AMP6020), never a silently ignored statement (plan D17).</summary>
        private ObserveStmt? KeywordStatement(Tok k)
        {
            if (k.Kind != T.Ident) return null;
            SyntaxFeatures needed;
            if (k.Text == "try" && Peek(1)?.Kind == T.LBrace)
            {
                if (!SyntaxGate.Supports(SyntaxFeatures.TryCatch)) throw new SyntaxError("\"try\" is not supported yet in this AMPLE version", k.Start, k.End, "feature-off");
                return null;   // Statement() reads it
            }

            if (!StatementWords.TryGetValue(k.Text, out needed) || Peek(1) is { Kind: T.Eq or T.Dot or T.Colon }) return null;
            if (!SyntaxGate.Supports(needed)) throw new SyntaxError($"\"{k.Text}\" is not supported yet in this AMPLE version", k.Start, k.End, "feature-off");
            return ObserveStatement(k);
        }

        private int MatchingParen(int open)
        {
            int depth = 0;
            for (int i = open; i < Tokens.Count; i++)
            {
                if (Tokens[i].Kind == T.LParen) depth++;
                else if (Tokens[i].Kind == T.RParen && --depth == 0) return i;
            }

            throw new SyntaxError("This \"(\" is never closed", Tokens[open].Start, Tokens[open].End);
        }

        /// <summary>
        /// <c>log "x"</c> and <c>log("x")</c> are one statement. A word followed by "(" is the call form when the matching ")" ends the statement; when anything follows it (an operator, a comma) the
        /// parenthesis only starts the first expression: <c>log (a + b) * 2</c>.
        /// </summary>
        private ObserveStmt ObserveStatement(Tok word)
        {
            p++;
            bool call = false;
            if (Peek()?.Kind == T.LParen)
            {
                int close = MatchingParen(p);
                Tok? after = close + 1 < Tokens.Count ? Tokens[close + 1] : null;
                call = after is null || after.Kind is T.RBrace or T.Semi || Text[Tokens[close].End..after.Start].Contains('\n', StringComparison.Ordinal);
            }

            List<Node> args = [];
            int end;
            if (call)
            {
                p++;
                if (Peek()?.Kind != T.RParen)
                {
                    while (true)
                    {
                        args.Add(ObserveArg());
                        if (Peek()?.Kind == T.Comma) { p++; if (Peek()?.Kind == T.RParen) break; continue; }
                        break;
                    }
                }

                end = Expect(T.RParen, "\")\"").End;
            }
            else
            {
                if (Peek() is null or { Kind: T.RBrace or T.Semi } || Text[word.End..Peek()!.Start].Contains('\n', StringComparison.Ordinal))
                    throw new SyntaxError($"{word.Text} needs a message or a condition after it", word.Start, word.End);
                while (true)
                {
                    args.Add(ObserveArg());
                    if (Peek()?.Kind == T.Comma) { p++; continue; }
                    break;
                }

                end = args[^1].End;
            }

            List<Node> positional = [.. args.Where(a => a is not NamedNode)];
            List<NamedNode> named = [.. args.OfType<NamedNode>()];
            bool assert = word.Text is "assert" or "require";
            if (positional.Count == 0 || positional.Count > (assert ? 2 : 1))
                throw new SyntaxError(assert ? $"{word.Text} takes a condition and an optional message" : $"{word.Text} takes one message or value (more go in context={{...}})", word.Start, end);
            foreach (NamedNode n in named)
            {
                if (word.Text is "fail" or "require" ? n.Name != "hint" : word.Text == "throw" || !ObserveNames.Contains(n.Name))
                {
                    string has = word.Text is "fail" or "require" ? "hint" : word.Text == "throw" ? "none: put code, hint and context in Error(...)" : "code, hint, context";
                    throw new SyntaxError($"{word.Text} has no argument \"{n.Name}\" (it has: {has})", n.Start, n.End);
                }
                if (named.Count(o => o.Name == n.Name) > 1) throw new SyntaxError($"\"{n.Name}\" is given twice", n.Start, n.End);
            }

            if (args.IndexOf(named.FirstOrDefault()!) is var firstNamed and >= 0 && args.Skip(firstNamed).Any(a => a is not NamedNode))
                throw new SyntaxError("The named arguments come after the message", args[firstNamed].Start, args[firstNamed].End);
            return new ObserveStmt(word.Text, positional, named, call, word.Start, end);
        }

        private Node ObserveArg()
        {
            if (Peek() is { Kind: T.Ident } id && Peek(1)?.Kind == T.Eq)
            {
                p += 2;
                Node v = Pipeline();
                return new NamedNode(id.Text, v, id.Start, v.End);
            }

            return Pipeline();
        }

        private bool IsRecordStart() => Peek() is { Kind: T.LBrace } && ((Peek(1) is { Kind: T.Ident or T.Str } && Peek(2)?.Kind == T.Colon) || Peek(1)?.Kind == T.RBrace);

        private RecordNode RecordLiteral()
        {
            Tok open = Expect(T.LBrace, "\"{\"");
            if (!SyntaxGate.Supports(SyntaxFeatures.Record)) throw new SyntaxError("Record literals are not supported yet in this AMPLE version", open.Start, open.End, "feature-off");
            List<RecordField> fields = [];
            while (Peek()?.Kind != T.RBrace)
            {
                Tok key = Peek() ?? throw new SyntaxError("The record is not closed", open.Start, Text.Length);
                if (key.Kind is not (T.Ident or T.Str)) throw new SyntaxError("Expected the name of a field", key.Start, key.End);
                p++;
                Expect(T.Colon, "\":\"");
                Node value = Pipeline();
                if (fields.Any(f => f.Name == key.Text)) throw new SyntaxError($"The field \"{key.Text}\" is given twice", key.Start, key.End);
                fields.Add(new RecordField(key.Text, value, key.Start, key.End));
                if (Peek()?.Kind == T.Comma) { p++; continue; }
                if (Peek()?.Kind != T.RBrace) throw new SyntaxError("Expected \",\" or \"}\" in the record", Peek()?.Start ?? Text.Length, Peek()?.End ?? Text.Length);
            }

            Tok close = Expect(T.RBrace, "\"}\"");
            return new RecordNode(fields, open.Start, close.End);
        }

        private IfStmt IfStatement()
        {
            Tok start = Peek()!;
            List<(Node Cond, StmtBlock Body)> branches = [];
            StmtBlock? otherwise = null;
            int end = start.End;
            while (true)
            {
                p++;   // if
                Node cond = Binary(0);
                StmtBlock body = Block();
                branches.Add((cond, body));
                end = body.End;
                if (Peek() is not { Kind: T.Ident, Text: "else" }) break;
                p++;
                if (Peek() is { Kind: T.Ident, Text: "if" }) continue;
                otherwise = Block();
                end = otherwise.End;
                break;
            }

            return new IfStmt(branches, otherwise, start.Start, end);
        }
    }
}
