using System.Text;
using Ample.Language;

namespace Ample.Lsp;

/// <summary>Conversions between the language library's positions and the protocol's (0-based line, UTF-16 character).</summary>
internal static class Positions
{
    /// <summary>Splits text into lines the way an editor counts them (the end of line is <c>\n</c>; a <c>\r</c> before it belongs to no line).</summary>
    public static string[] Lines(string text) => [.. text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l)];

    /// <summary>Offset of a protocol position in the text (clamped into the line).</summary>
    public static int OffsetOf(string text, int line, int character)
    {
        int offset = 0;
        for (int i = 0; i < line; i++)
        {
            int nl = text.IndexOf('\n', offset);
            if (nl < 0)
            {
                return text.Length;
            }

            offset = nl + 1;
        }

        int end = text.IndexOf('\n', offset);
        int lineEnd = end < 0 ? text.Length : end;
        if (lineEnd > offset && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        return offset + Math.Clamp(character, 0, lineEnd - offset);
    }

    /// <summary>Applies one incremental change (a range, or the whole text when there is no range).</summary>
    public static string Apply(string text, int? startLine, int startChar, int endLine, int endChar, string newText)
    {
        if (startLine is null)
        {
            return newText;
        }

        int a = OffsetOf(text, startLine.Value, startChar);
        int b = Math.Max(a, OffsetOf(text, endLine, endChar));
        return string.Concat(text.AsSpan(0, a), newText, text.AsSpan(b));
    }

    /// <summary>The library counts diagnostic columns in code points; the protocol counts UTF-16 units.</summary>
    public static int CodePointsToUtf16(string line, int codePoints)
    {
        int units = 0;
        int cps = 0;
        while (cps < codePoints && units < line.Length)
        {
            units += char.IsHighSurrogate(line[units]) && units + 1 < line.Length ? 2 : 1;
            cps++;
        }

        return units + (codePoints - cps);
    }

    /// <summary>A <see cref="Location"/> (1-based line, UTF-16 index + 1) as a protocol range.</summary>
    public static object Range(Location l) => RangeOf(l.Line - 1, l.Col - 1, l.Line - 1, l.EndCol - 1);

    /// <summary>A protocol range.</summary>
    public static object RangeOf(int startLine, int startChar, int endLine, int endChar) => new { start = new { line = startLine, character = startChar }, end = new { line = endLine, character = endChar } };

    /// <summary>A <see cref="TextEdit"/> of the library as a protocol edit.</summary>
    public static object Edit(TextEdit e) => new { range = RangeOf(e.StartLine - 1, e.StartCol - 1, e.EndLine - 1, e.EndCol - 1), newText = e.NewText };

    /// <summary>The range that covers a whole text.</summary>
    public static object Whole(string text)
    {
        string[] lines = Lines(text);
        return RangeOf(0, 0, lines.Length - 1, lines[^1].Length);
    }

    /// <summary>Counts the UTF-8 bytes of a text (used only to size buffers in tests).</summary>
    public static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);
}
