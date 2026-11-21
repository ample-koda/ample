using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FliteData;

/// <summary>Reads the small subset of C that the generated Flite data files use: comments, brace bodies, comma separated items, string and number literals, and #define constants.
/// Files are read as Latin-1 so that every character is one byte of the source.</summary>
internal static class CSource
{
    public static HashSet<string> Used { get; } = new(StringComparer.Ordinal);

    public static string Read(string path)
    {
        Used.Add(Path.GetFullPath(path));
        return File.ReadAllText(path, Encoding.Latin1);
    }

    /// <summary>Removes <c>/* */</c> and <c>//</c> comments and keeps everything else, string and character literals included.</summary>
    public static string Strip(string text)
    {
        StringBuilder result = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                int end = SkipLiteral(text, i);
                result.Append(text, i, end - i);
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("An unterminated comment.");
                result.Append(' ');
                i = end + 2;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static int SkipLiteral(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\') i++;
            i++;
        }

        if (i >= text.Length) throw new InvalidDataException("An unterminated literal.");
        return i + 1;
    }

    /// <summary>The text between the first <c>{</c> after <paramref name="marker"/> and its matching <c>}</c>.</summary>
    public static string Body(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new InvalidDataException($"'{marker}' was not found.");
        int open = text.IndexOf('{', at + marker.Length);
        if (open < 0) throw new InvalidDataException($"No body after '{marker}'.");
        int depth = 0;
        int i = open;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(open + 1, i - open - 1);
            i++;
        }

        throw new InvalidDataException($"The body after '{marker}' is not closed.");
    }

    /// <summary>Splits at the commas that are not inside a literal, parenthesis or brace; empty items at the end are dropped.</summary>
    public static List<string> Items(string body)
    {
        List<string> items = [];
        int depth = 0;
        int start = 0;
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(body, i);
                continue;
            }

            if (c is '(' or '{') depth++;
            else if (c is ')' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                items.Add(body[start..i].Trim());
                start = i + 1;
            }

            i++;
        }

        string last = body[start..].Trim();
        if (last.Length > 0) items.Add(last);
        while (items.Count > 0 && items[^1].Length == 0) items.RemoveAt(items.Count - 1);
        return items;
    }

    public static bool IsNull(string item) => item is "NULL" or "0";

    /// <summary>The bytes of one or more adjacent C string literals.</summary>
    public static byte[] DecodeStrings(string item)
    {
        List<byte> bytes = [];
        int i = 0;
        bool any = false;
        while (i < item.Length)
        {
            char c = item[i];
            if (c != '"')
            {
                if (!char.IsWhiteSpace(c)) throw new InvalidDataException($"Not a string literal: {item}");
                i++;
                continue;
            }

            any = true;
            i++;
            while (item[i] != '"')
            {
                if (item[i] != '\\')
                {
                    bytes.Add((byte)item[i++]);
                    continue;
                }

                i++;
                char e = item[i++];
                switch (e)
                {
                    case 'n': bytes.Add(10); break;
                    case 't': bytes.Add(9); break;
                    case 'r': bytes.Add(13); break;
                    case 'a': bytes.Add(7); break;
                    case 'b': bytes.Add(8); break;
                    case 'f': bytes.Add(12); break;
                    case 'v': bytes.Add(11); break;
                    case 'x':
                        {
                            int value = 0;
                            while (i < item.Length && Uri.IsHexDigit(item[i])) value = (value * 16) + Convert.ToInt32(item[i++].ToString(), 16);
                            bytes.Add((byte)value);
                            break;
                        }

                    default:
                        if (e is >= '0' and <= '7')
                        {
                            int value = e - '0';
                            for (int digits = 1; digits < 3 && i < item.Length && item[i] is >= '0' and <= '7'; digits++) value = (value * 8) + (item[i++] - '0');
                            bytes.Add((byte)value);
                        }
                        else
                        {
                            bytes.Add((byte)e);   // \\ \" \' \?
                        }

                        break;
                }
            }

            i++;
        }

        if (!any) throw new InvalidDataException($"Not a string literal: {item}");
        return [.. bytes];
    }

    public static string DecodeText(string item) => Encoding.Latin1.GetString(DecodeStrings(item));

    /// <summary>A byte value from a number or a character literal.</summary>
    public static int Number(string item)
    {
        item = item.Trim();
        if (item.Length >= 3 && item[0] == '\'')
        {
            byte[] bytes = DecodeStrings("\"" + item[1..^1] + "\"");
            if (bytes.Length != 1) throw new InvalidDataException($"Not one character: {item}");
            return bytes[0];
        }

        if (item.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return int.Parse(item[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return int.Parse(item, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>A C decimal literal as a 32-bit float: the literal is a double first and is then converted, as the C compiler does.</summary>
    public static float Float(string item) => (float)double.Parse(item.Trim().TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>A C decimal literal as a 64-bit double (the nearest double, as the C compiler reads it).</summary>
    public static double Double(string item) => double.Parse(item.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static List<int> Ints(string body) => [.. Items(body).Select(Number)];

    private static readonly Regex DefineRegex = new(@"^[ \t]*#define[ \t]+(\w+)[ \t]+(.+?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static Dictionary<string, string> Defines(string strippedText)
    {
        Dictionary<string, string> defines = new(StringComparer.Ordinal);
        foreach (Match match in DefineRegex.Matches(strippedText)) defines[match.Groups[1].Value] = match.Groups[2].Value;
        return defines;
    }

    /// <summary>Evaluates an integer expression made of numbers, + - *, parentheses and names of other defines.</summary>
    public static int Eval(string expression, IReadOnlyDictionary<string, string> defines)
    {
        int at = 0;
        int Primary()
        {
            while (at < expression.Length && expression[at] == ' ') at++;
            if (expression[at] == '(')
            {
                at++;
                int inner = Sum();
                while (expression[at] == ' ') at++;
                at++;
                return inner;
            }

            int start = at;
            while (at < expression.Length && (char.IsLetterOrDigit(expression[at]) || expression[at] == '_')) at++;
            string word = expression[start..at];
            return char.IsDigit(word[0]) ? Number(word) : Eval(defines[word], defines);
        }

        int Product()
        {
            int value = Primary();
            while (true)
            {
                while (at < expression.Length && expression[at] == ' ') at++;
                if (at < expression.Length && expression[at] == '*')
                {
                    at++;
                    value *= Primary();
                }
                else
                {
                    return value;
                }
            }
        }

        int Sum()
        {
            int value = Product();
            while (true)
            {
                while (at < expression.Length && expression[at] == ' ') at++;
                if (at < expression.Length && expression[at] is '+' or '-')
                {
                    char op = expression[at++];
                    int right = Product();
                    value = op == '+' ? value + right : value - right;
                }
                else
                {
                    return value;
                }
            }
        }

        return Sum();
    }
}
