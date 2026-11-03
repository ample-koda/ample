
namespace Ample.Language;

public static partial class NotePipeline
{
    private sealed partial class ExprParser
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "let", "var", "if", "else", "for", "in", "repeat", "while", "max", "stop", "return", "emit", "true", "false", "helper", "dynamic", "bake", "as", "override", "type", "is" };

        /// <summary>Parses the whole text as one declaration: a helper, a type alias or a file-wide constant.</summary>
        public Node ParseDeclaration()
        {
            Tok first = Peek() ?? throw new SyntaxError("The declaration is empty", 0, Math.Max(1, Text.Length));
            bool dynamic = false;
            if (first.Kind == T.Ident && first.Text == "dynamic") { p++; dynamic = true; }
            Tok kw = Expect(T.Ident, "helper, type or let");
            Node result;
            switch (kw.Text)
            {
                case "helper": result = HelperDecl(first, dynamic); break;
                case "type" when !dynamic: result = TypeDecl(first); break;
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

        private ConstDeclNode ConstDecl(Tok first)
        {
            string? type = null;
            if (Peek()?.Kind == T.Ident && Peek(1)?.Kind == T.Ident && Peek(2)?.Kind == T.Eq) type = TypeText();
            Tok name = Expect(T.Ident, "the name of the constant");
            Expect(T.Eq, "\"=\"");
            Node value = Pipeline();
            return new ConstDeclNode(name.Text, type, value, first.Start, value.End, name.Start, name.End);
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
                if (previous is not null and not (IfStmt or ForStmt or RepeatStmt or WhileStmt) && !separated && previous.End <= k.Start && !Text[previous.End..k.Start].Contains('\n', StringComparison.Ordinal))
                    throw new SyntaxError($"Statements on one line are separated by \";\": write \";\" before \"{k.Text}\", or put it on a new line", k.Start, k.End);
                previous = Statement();
                statements.Add(previous);
            }
        }

        private Node Statement()
        {
            Tok k = Peek()!;
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
