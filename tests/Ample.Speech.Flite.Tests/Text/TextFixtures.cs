using System.IO.Compression;
using System.Text;
using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Tests.Text;

/// <summary>The golden files of milestone M3 (<c>plan/speech/flite/reference/m3</c>) and the helpers to read and write them.</summary>
internal static class TextFixtures
{
    /// <summary>The lines of a gzip-compressed fixture file.</summary>
    public static string[] ReadGz(string name)
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        using GZipStream zip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(zip, new UTF8Encoding(false));
        List<string> lines = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>Decodes the escapes <c>\xHH</c> and <c>\\</c> of a fixture line into a byte string; the C program stops at a NUL byte, so does this.</summary>
    public static string Unescape(string s)
    {
        StringBuilder r = new();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '\\')
            {
                r.Append('\\');
                i++;
            }
            else if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' && Uri.IsHexDigit(s[i + 2]) && Uri.IsHexDigit(s[i + 3]))
            {
                r.Append((char)Convert.ToInt32(s.Substring(i + 2, 2), 16));
                i += 3;
            }
            else
            {
                r.Append(s[i]);
            }
        }

        string text = r.ToString();
        int nul = text.IndexOf('\0', StringComparison.Ordinal);
        return nul >= 0 ? text[..nul] : text;
    }

    /// <summary>Writes a byte string as the C dumper does: bytes below 0x20 or from 0x7f and the backslash are escaped.</summary>
    public static string Escape(string byteString)
    {
        StringBuilder r = new();
        foreach (char c in byteString)
        {
            if (c < 0x20 || c >= 0x7f)
            {
                r.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
            }
            else if (c == '\\')
            {
                r.Append("\\\\");
            }
            else
            {
                r.Append(c);
            }
        }

        return r.ToString();
    }

    /// <summary>One text of the golden file: the question to the lexicon and the lines of tokens and words the C program gave, or a crash.</summary>
    internal sealed record TextRecord(string Escaped, List<string> Lex, List<string> Lines, bool Crashed);

    /// <summary>Splits the output of <c>m3dump text</c> into records.</summary>
    public static List<TextRecord> ParseText(string[] lines)
    {
        List<TextRecord> records = [];
        TextRecord? current = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("#TEXT\t", StringComparison.Ordinal))
            {
                current = new TextRecord(line[6..], [], [], false);
                records.Add(current);
            }
            else if (current is null)
            {
                continue;
            }
            else if (line.StartsWith("#LEX\t", StringComparison.Ordinal))
            {
                current.Lex.Add(line);
            }
            else if (line.StartsWith("#CRASH", StringComparison.Ordinal))
            {
                records[^1] = current = current with { Crashed = true };
            }
            else
            {
                current.Lines.Add(line);
            }
        }

        return records;
    }

    /// <summary>The lines of the Token and Word relations of an analysed utterance, in the format of the C dumper.</summary>
    public static List<string> DumpTokensAndWords(Utterance u)
    {
        List<string> lines = [];
        Relation tokens = u.GetRelation("Token");
        int k = 0;
        for (Item? i = tokens.Head; i is not null; i = Hrg.Next(i), k++)
        {
            lines.Add(string.Join('\t', "#TOKEN", k.ToString(System.Globalization.CultureInfo.InvariantCulture), Escape(Hrg.FeatString(i, "name")),
                $"[{Escape(Hrg.FeatString(i, "whitespace"))}]", $"[{Escape(Hrg.FeatString(i, "prepunctuation"))}]", $"[{Escape(Hrg.FeatString(i, "punc"))}]",
                Hrg.FeatInt(i, "file_pos").ToString(System.Globalization.CultureInfo.InvariantCulture), Hrg.FeatInt(i, "line_number").ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        k = 0;
        for (Item? i = u.GetRelation("Word").Head; i is not null; i = Hrg.Next(i), k++)
        {
            Item? parent = Hrg.Parent(Hrg.ItemAs(i, "Token"));
            int index = -1;
            int t = 0;
            for (Item? j = tokens.Head; j is not null && parent is not null; j = Hrg.Next(j), t++)
            {
                if (Hrg.Equal(j, parent))
                {
                    index = t;
                    break;
                }
            }

            lines.Add(string.Join('\t', "#WORD", k.ToString(System.Globalization.CultureInfo.InvariantCulture), Escape(Hrg.FeatString(i, "name")),
                Hrg.FeatPresent(i, "pos") ? Hrg.FeatString(i, "pos") : "-",
                Hrg.FeatPresent(i, "break") ? Hrg.FeatString(i, "break") : "-", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return lines;
    }
}

/// <summary>A lexicon that answers as the C program did for one text, and notes what it was asked.</summary>
internal sealed class RecordedLexicon(IEnumerable<string> logged) : ILexicon
{
    private readonly Dictionary<string, bool> answers = logged
        .Select(l => l.Split('\t'))
        .GroupBy(p => p[1], StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First()[2] == "1", StringComparer.Ordinal);

    /// <summary>The questions in the format of the C log.</summary>
    public List<string> Asked { get; } = [];

    /// <inheritdoc />
    public bool InLex(string word)
    {
        bool known = answers.TryGetValue(TextFixtures.Escape(word), out bool answer);
        Asked.Add($"#LEX\t{TextFixtures.Escape(word)}\t{(known && answer ? 1 : 0)}");
        return known && answer;
    }
}
