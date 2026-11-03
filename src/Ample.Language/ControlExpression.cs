using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>A control that a parameter reads: <c>block.output</c> in a control expression.</summary>
/// <param name="Block">The block the control comes from.</param>
/// <param name="Output">Its output.</param>
/// <param name="Parameter">The parameter that reads it.</param>
/// <param name="Line">0-based line of the parameter.</param>
public sealed record ControlRef(string Block, string Output, string Parameter, int Line);

/// <summary>
/// The value of a parameter written with controls, for example <c>600 + 3000 * traffic.level</c> (the cutoff is 600 Hz when no car passes and up to 3600 Hz while one does).
/// Numbers, units, <c>+ - *</c>, brackets and <c>block.output</c>. Everything is integer fixed point with three decimals, so the result is the same on every machine;
/// a general control is a fraction from 0 to 1 (so <c>3000 * level</c> is 0 to 3000), a typed control is a value in its own unit.
/// </summary>
public sealed partial class ControlExpression
{
    /// <summary>One <c>block.output</c> in the text.</summary>
    /// <param name="Block">The block.</param>
    /// <param name="Output">The output.</param>
    /// <param name="Start">Start of the text in the expression.</param>
    /// <param name="Length">Length of the text.</param>
    public sealed record Reference(string Block, string Output, int Start, int Length);

    private abstract record Node;

    private sealed record Number(long Fixed) : Node;

    private sealed record Ref(int Index) : Node;

    private sealed record Neg(Node Inner) : Node;

    private sealed record Binary(char Op, Node Left, Node Right) : Node;

    private readonly Node root;

    private ControlExpression(Node root, IReadOnlyList<Reference> references, IReadOnlyList<string> units)
    {
        this.root = root;
        References = references;
        Units = units;
    }

    /// <summary>Gets the controls the expression reads, in the order of the text.</summary>
    public IReadOnlyList<Reference> References { get; }

    /// <summary>Gets the units written after numbers (for the dimension check).</summary>
    public IReadOnlyList<string> Units { get; }

    /// <summary>Whether a parameter value is a control expression (it names a <c>block.output</c> and is not one of the automation functions).</summary>
    public static bool LooksLike(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || FunctionStart().IsMatch(t)) return false;
        return ReferenceToken().IsMatch(t);
    }

    /// <summary>Reads an expression.</summary>
    /// <param name="text">The parameter value.</param>
    /// <param name="error">What is wrong, when null is returned.</param>
    /// <returns>The expression, or null.</returns>
    public static ControlExpression? TryParse(string text, out string? error)
    {
        Parser p = new(text);
        try
        {
            Node node = p.ParseExpression();
            p.SkipSpace();
            if (!p.AtEnd) throw new FormatException($"unexpected \"{text[p.Position..]}\"");
            error = null;
            return new ControlExpression(node, p.References, p.Units);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The value in fixed point (1000 is 1), given the value of every reference at this moment (also in fixed point: a general control from 0 to 1000, a typed control in its unit times 1000).
    /// </summary>
    /// <param name="valueOf">The value of reference number <c>i</c> of <see cref="References"/>.</param>
    public long Evaluate(Func<int, long> valueOf) => Eval(root, valueOf);

    /// <summary>Rounds a fixed-point value to a whole number (half away from zero).</summary>
    public static int ToInt(long fixedPoint) => (int)((fixedPoint + (fixedPoint >= 0 ? 500 : -500)) / 1000);

    private static long Eval(Node node, Func<int, long> valueOf) => node switch
    {
        Number n => n.Fixed,
        Ref r => valueOf(r.Index),
        Neg g => -Eval(g.Inner, valueOf),
        Binary { Op: '+' } b => Eval(b.Left, valueOf) + Eval(b.Right, valueOf),
        Binary { Op: '-' } b => Eval(b.Left, valueOf) - Eval(b.Right, valueOf),
        Binary b => Multiply(Eval(b.Left, valueOf), Eval(b.Right, valueOf)),
        _ => 0,
    };

    private static long Multiply(long a, long b)
    {
        long product = a * b;
        return (product + (product >= 0 ? 500 : -500)) / 1000;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*\\s*\\(", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FunctionStart();

    [GeneratedRegex("(?<![\\w.])[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z0-9_]+)*\\.[A-Za-z_][A-Za-z0-9_]*(?![\\w(])", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ReferenceToken();

    private sealed class Parser(string text)
    {
        private readonly List<Reference> references = [];
        private readonly List<string> units = [];

        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public IReadOnlyList<Reference> References => references;

        public IReadOnlyList<string> Units => units;

        public void SkipSpace()
        {
            while (Position < text.Length && char.IsWhiteSpace(text[Position])) Position++;
        }

        public Node ParseExpression()
        {
            Node left = ParseTerm();
            while (true)
            {
                SkipSpace();
                if (AtEnd || text[Position] is not ('+' or '-')) return left;
                char op = text[Position++];
                left = new Binary(op, left, ParseTerm());
            }
        }

        private Node ParseTerm()
        {
            Node left = ParseFactor();
            while (true)
            {
                SkipSpace();
                if (AtEnd || text[Position] != '*') return left;
                Position++;
                left = new Binary('*', left, ParseFactor());
            }
        }

        private Node ParseFactor()
        {
            SkipSpace();
            if (AtEnd) throw new FormatException("the expression ends too soon");
            char c = text[Position];
            if (c == '-')
            {
                Position++;
                return new Neg(ParseFactor());
            }

            if (c == '(')
            {
                Position++;
                Node inner = ParseExpression();
                SkipSpace();
                if (AtEnd || text[Position] != ')') throw new FormatException("a bracket is not closed");
                Position++;
                return inner;
            }

            if (char.IsDigit(c)) return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseReference();
            throw new FormatException($"unexpected \"{c}\"");
        }

        private Number ParseNumber()
        {
            int start = Position;
            while (Position < text.Length && char.IsDigit(text[Position])) Position++;
            long whole = long.Parse(text.AsSpan(start, Position - start), NumberStyles.None, CultureInfo.InvariantCulture);
            long fraction = 0;
            if (Position + 1 < text.Length && text[Position] == '.' && char.IsDigit(text[Position + 1]))
            {
                Position++;
                int digits = 0;
                while (Position < text.Length && char.IsDigit(text[Position]))
                {
                    if (digits < 3) { fraction = fraction * 10 + (text[Position] - '0'); digits++; }
                    Position++;
                }

                while (digits++ < 3) fraction *= 10;
            }

            // a unit right after the number (600 Hz, -6 dB, 30%)
            int save = Position;
            SkipSpace();
            int u = Position;
            while (Position < text.Length && (char.IsLetter(text[Position]) || text[Position] == '%')) Position++;
            string unit = text[u..Position];
            if (unit.Length > 0 && ControlTypes.Units.Contains(unit) && !(Position < text.Length && (text[Position] == '.' || text[Position] == '(' || char.IsLetterOrDigit(text[Position]))))
            {
                units.Add(unit);
            }
            else
            {
                Position = save;
            }

            return new Number(whole * 1000 + fraction);
        }

        private Ref ParseReference()
        {
            int start = Position;
            string block = Identifier();
            if (Position >= text.Length || text[Position] != '.') throw new FormatException($"\"{block}\" is not a control: write the block and its output, for example {block}.level");
            Position++;
            if (Position >= text.Length || !(char.IsLetter(text[Position]) || text[Position] == '_')) throw new FormatException($"\"{block}.\" needs the name of an output");
            int o = Position;
            while (Position < text.Length && (char.IsLetterOrDigit(text[Position]) || text[Position] == '_')) Position++;
            string output = text[o..Position];
            references.Add(new Reference(block, output, start, Position - start));
            return new Ref(references.Count - 1);
        }

        private string Identifier()
        {
            int start = Position;
            while (Position < text.Length && (char.IsLetterOrDigit(text[Position]) || text[Position] == '_' || (text[Position] == '-' && Position + 1 < text.Length && char.IsLetterOrDigit(text[Position + 1]) && Position > start))) Position++;
            return text[start..Position];
        }
    }
}
