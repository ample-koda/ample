using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ample.Language;

public static partial class NotePipeline
{
    /// <summary>The text of a computed decimal (see Interp.NumberText), for the tests.</summary>
    internal static string NumberTextForTests(double d) => Interp.NumberText(d);

    private sealed partial class Interp
    {
        /// <summary>The longest text a helper may make (characters). Every text made by +, String or sprintf is checked against it and charged fuel by its length.</summary>
        internal const int MaxTextLength = 1_048_576;

        private const int MaxFormatWidth = 64;
        private const int MaxFormatPrecision = 20;

        /// <summary>The text of a single value, the one rule behind +, String(value) and sprintf's %s: a text as it is, any other single value as the log
        /// writes it (3, 0.5, true, 40%, -6dB, major), a length in beats (1beat, 2.5beats; the internal ticks mean nothing to a reader). Null for a value that has
        /// no single text (a list, record, notes, error, range, date or nothing).</summary>
        private string? ScalarText(Val v) => v switch
        {
            StrV s => s.V,
            QuantV { Dim: "length" } q => DecimalText(q.V, 6) + (q.V == Rat.Of(1) ? "beat" : "beats"),
            IntV or FloatV or BoolV or QuantV or WordV => ValText(v),
            _ => null,
        };

        /// <summary>A decimal number the way JavaScript writes it (the shortest text that reads back as the same number), so C# and the JavaScript twin agree:
        /// 0.1, 1e+21, 1e-7, -0 as 0, NaN, Infinity.</summary>
        internal static string NumberText(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return "0";
            // the shortest round trip: "R" gives the digits, then the exponent rules of ECMAScript Number::toString place the point
            string shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
            int ePos = shortest.IndexOfAny(['E', 'e']);
            string mantissa = ePos < 0 ? shortest : shortest[..ePos];
            int exp10 = ePos < 0 ? 0 : int.Parse(shortest[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            int point = mantissa.IndexOf('.', StringComparison.Ordinal);
            string digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
            int intLen = point < 0 ? mantissa.Length : point;
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
            digits = digits[lead..].TrimEnd('0');
            if (digits.Length == 0) return "0";
            int n = intLen - lead + exp10;   // the value is 0.DIGITS x 10^n
            int k = digits.Length;
            string body;
            if (k <= n && n <= 21) body = digits + new string('0', n - k);
            else if (0 < n && n <= 21) body = digits[..n] + "." + digits[n..];
            else if (-6 < n && n <= 0) body = "0." + new string('0', -n) + digits;
            else
            {
                int e = n - 1;
                body = digits[..1] + (k > 1 ? "." + digits[1..] : string.Empty) + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            }

            return (d < 0 ? "-" : string.Empty) + body;
        }

        /// <summary>Checks a text that was just made against the text limit and charges fuel for its length (one step per 256 characters).</summary>
        private StrV MadeText(string text, Node at)
        {
            if (text.Length > MaxTextLength) throw new LangFailure("memory", $"A text made here would be {text.Length} characters long; a text may be at most {MaxTextLength} characters", at);
            fuel += text.Length / 256;
            if (fuel > FuelLimit) throw new LangFailure("fuel", $"The helper code used more than {FuelLimit} evaluation steps (the limit of the song is limits.fuel)", at);
            return new StrV(text);
        }

        /// <summary>+ with a text on one side: a text and a text, a text and an Int or a Float (either order) make one text. Null when it does not apply.</summary>
        private StrV? JoinText(string op, Val l, Val r, Node at)
        {
            if (op != "+" || (l is not StrV && r is not StrV)) return null;
            bool joinable(Val v) => v is StrV or IntV or FloatV;
            if (joinable(l) && joinable(r)) return MadeText(ScalarText(l) + ScalarText(r), at);
            Val other = l is StrV ? r : l;
            throw new LangFailure("type-mismatch", $"+ joins a text with a text or a number, not with {Describe(other)}: write String(...) around it to turn it into text first", at);
        }

        /// <summary>String(value): the text of a single value (see <see cref="ScalarText"/>).</summary>
        private StrV ToStringValue(Val v, Node at)
        {
            string? text = ScalarText(v);
            if (text is null)
            {
                string what = v is NotesV ? "Notes" : TypeName(v);
                throw new LangFailure("conversion", $"String cannot turn {A(what)} into text: it takes a text, a number, a Bool or a quantity (log the value itself to see a list or record)", at);
            }

            return MadeText(text, at);
        }

        /// <summary>sprintf(format, values...): %s (the text of a single value, as String gives it), %d (an Int), %f and %.Nf (a number, N decimals, default 6,
        /// rounded half to even on its exact value), %% (a percent sign); an optional flag - (left align) or 0 (zero padding, numbers only) and a width up to 64.</summary>
        private StrV Sprintf(List<Arg> args, CallNode c)
        {
            if (args.Count == 0 || args[0].Value is not StrV format) throw new LangFailure("bad-argument", "sprintf needs a format text first, like sprintf(\"Bar %d\", 4)", args.Count == 0 ? c : args[0].Origin);
            if (args.FirstOrDefault(a => a.Name is not null) is { } named) throw new LangFailure("bad-argument", "sprintf takes its values in order, without names", named.Origin);
            bool secretFormat = IsSensitive(format);
            string f = format.V;
            StringBuilder sb = new();
            int next = 1;
            int i = 0;
            while (i < f.Length)
            {
                char ch = f[i];
                if (ch != '%') { sb.Append(ch); i++; continue; }
                int start = i++;
                char flag = '\0';
                if (i < f.Length && f[i] is '-' or '0') flag = f[i++];
                int widthStart = i;
                while (i < f.Length && char.IsAsciiDigit(f[i]) && i - widthStart < 9) i++;
                int width = i > widthStart ? int.Parse(f.AsSpan(widthStart, i - widthStart), CultureInfo.InvariantCulture) : 0;
                int precision = -1;
                if (i < f.Length && f[i] == '.')
                {
                    int precisionStart = ++i;
                    while (i < f.Length && char.IsAsciiDigit(f[i]) && i - precisionStart < 9) i++;
                    if (i == precisionStart) throw BadFormat("a precision needs digits after the point, like %.2f");
                    precision = int.Parse(f.AsSpan(precisionStart, i - precisionStart), CultureInfo.InvariantCulture);
                }

                if (i >= f.Length) throw BadFormat("the format ends inside a placeholder (write %% for a percent sign)");
                char conv = f[i++];
                string spec = f[start..i];
                if (conv == '%')
                {
                    if (i - start != 2) throw BadFormat($"{Show(spec)} is not a placeholder: write %% alone for a percent sign");
                    sb.Append('%');
                    continue;
                }

                if (conv is not ('s' or 'd' or 'f')) throw BadFormat($"{Show(spec)} is not supported (supported: %s, %d, %f, %.Nf and %%, with an optional - or 0 and a width)");
                if (width > MaxFormatWidth) throw BadFormat($"the width of {Show(spec)} is more than {MaxFormatWidth}");
                if (precision > MaxFormatPrecision) throw BadFormat($"the precision of {Show(spec)} is more than {MaxFormatPrecision}");
                if (precision >= 0 && conv != 'f') throw BadFormat($"{Show(spec)}: a precision is only for %f");
                if (flag == '0' && conv == 's') throw BadFormat($"{Show(spec)}: zero padding is only for numbers");
                if (next >= args.Count) throw new LangFailure("bad-argument", $"sprintf: the format has more placeholders than the {args.Count - 1} value{(args.Count == 2 ? string.Empty : "s")} given", c);
                Arg arg = args[next++];
                string piece = conv switch
                {
                    's' => ScalarText(arg.Value) ?? throw new LangFailure("bad-argument", $"sprintf: %s takes a text, a number, a Bool or a quantity, not {(arg.Value is NotesV ? "Notes" : A(TypeName(arg.Value)))}", arg.Origin),
                    'd' => arg.Value is IntV iv ? iv.V.ToString(CultureInfo.InvariantCulture) : throw new LangFailure("bad-argument", $"sprintf: %d takes an Int, not {Describe(arg.Value)}: use %f, or Round(...) first", arg.Origin),
                    _ => arg.Value switch
                    {
                        IntV iv => FixedText(Rat.Of(iv.V), precision < 0 ? 6 : precision),
                        FloatV fv when double.IsNaN(fv.V) || double.IsInfinity(fv.V) => NumberText(fv.V),
                        FloatV fv => FixedText(fv.Exact ?? RatOfDouble(fv.V), precision < 0 ? 6 : precision),
                        _ => throw new LangFailure("bad-argument", $"sprintf: %f takes a number, not {Describe(arg.Value)}: use %s for a quantity", arg.Origin),
                    },
                };
                bool numeric = conv != 's' && piece.Length > 0 && (char.IsAsciiDigit(piece[^1]));
                if (piece.Length < width)
                {
                    if (flag == '-') piece = piece.PadRight(width);
                    else if (flag == '0' && numeric)
                    {
                        bool negative = piece[0] == '-';
                        string unsigned = negative ? piece[1..] : piece;
                        piece = (negative ? "-" : string.Empty) + unsigned.PadLeft(width - (negative ? 1 : 0), '0');
                    }
                    else piece = piece.PadLeft(width);
                }

                sb.Append(piece);
                if (sb.Length > MaxTextLength) break;
            }

            if (next < args.Count) throw new LangFailure("bad-argument", $"sprintf: {args.Count - 1} values were given but the format has {next - 1} placeholder{(next == 2 ? string.Empty : "s")}", args[next].Origin);
            return MadeText(sb.ToString(), c);

            string Show(string spec) => secretFormat ? "a placeholder" : "\"" + spec + "\"";
            LangFailure BadFormat(string why) => new("bad-argument", "sprintf: " + why, args[0].Origin);
        }

        /// <summary>A number with exactly <paramref name="places"/> decimals, rounded half to even on its exact value (0.125 to 2 places is 0.12, as C writes it); never -0.</summary>
        private static string FixedText(Rat r, int places)
        {
            BigInteger scale = BigInteger.Pow(10, places);
            BigInteger q = RoundHalfEven(Rat.Of(r.N * scale, r.D));
            bool negative = q.Sign < 0;
            string digits = BigInteger.Abs(q).ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
            string whole = digits[..^places], fraction = places > 0 ? digits[^places..] : string.Empty;
            return (negative ? "-" : string.Empty) + whole + (places > 0 ? "." + fraction : string.Empty);
        }
    }
}
