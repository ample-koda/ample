using System.Text.RegularExpressions;
using Ample.Language;

namespace Ample.Lsp;

/// <summary>Semantic tokens of a song: block types, names, parameters, flags, numbers, strings and comments.</summary>
internal static partial class SemanticTokens
{
    /// <summary>The token types, in the order of the legend.</summary>
    public static readonly string[] Types = ["keyword", "type", "variable", "property", "number", "string", "comment", "enumMember", "operator", "function"];

    /// <summary>The token modifiers, in the order of the legend.</summary>
    public static readonly string[] Modifiers = ["declaration", "readonly", "dynamic", "baked", "seeded"];

    private const int Keyword = 0, TypeT = 1, Variable = 2, Property = 3, Number = 4, StringT = 5, Comment = 6, EnumMember = 7, Operator = 8, Function = 9;

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|-?[0-9]+(?:\\.[0-9]+)?|[A-Za-z_][A-Za-z0-9_-]*|->|[{},.@]", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Lexeme();

    /// <summary>Computes the tokens as (line, start, length, type, modifiers), sorted by position.</summary>
    public static List<(int Line, int Start, int Length, int Type, int Mods)> Compute(AmpleDocument doc, SpecData spec)
    {
        List<(int, int, int, int, int)> tokens = [];
        HashSet<string> keywords = [.. spec.Keywords];
        string[] lines = Positions.Lines(doc.Text);
        if (doc.Text.Contains("/*", StringComparison.Ordinal))
        {
            // a block comment is a comment token on every line it covers, and the lines are read without it
            List<(int Line, int Start, int Length)> pieces = [];
            lines = Comments.BlankBlocks(string.Join('\n', lines), out _, pieces).Split('\n');
            foreach ((int pieceLine, int start, int length) in pieces.Where(p => p.Length > 0)) tokens.Add((pieceLine, start, length, Comment, 0));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int hash = CommentStart(line);
            string code = hash < 0 ? line : line[..hash];
            if (hash >= 0)
            {
                tokens.Add((i, hash, line.Length - hash, Comment, 0));
            }

            BlockNode? header = doc.Blocks.FirstOrDefault(b => b.Line == i);
            BlockNode? owner = header is null ? doc.Blocks.FirstOrDefault(b => b.Line < i && (b.CloseLine < 0 || b.CloseLine >= i)) : null;
            bool fromLine = owner is not null && code.TrimStart().StartsWith("from ", StringComparison.Ordinal);
            int index = 0;
            string? previous = null;
            foreach (Match m in Lexeme().Matches(code))
            {
                int kind = -1;
                int mods = 0;
                string v = m.Value;
                if (v[0] == '"')
                {
                    kind = StringT;
                }
                else if (char.IsDigit(v[0]) || (v[0] == '-' && v.Length > 1 && char.IsDigit(v[1])))
                {
                    kind = Number;
                }
                else if (v is "->" or "{" or "}" or "," or "." or "@")
                {
                    kind = Operator;
                }
                else if (header is not null)
                {
                    if (index == 0 && spec.Templates.ContainsKey(v))
                    {
                        kind = TypeT;
                    }
                    else if (index == 1)
                    {
                        kind = Variable;
                        mods = 1;
                    }
                    else if (v is "stable" or "pinned" or "live")
                    {
                        kind = EnumMember;
                    }
                    else if (keywords.Contains(v) || v is "bake" or "lock" or "refresh")
                    {
                        kind = Keyword;
                    }
                }
                else if (fromLine)
                {
                    kind = v == "from" ? Keyword : previous is "from" ? Variable : previous is "." or "->" ? Property : -1;
                }
                else if (owner is not null)
                {
                    kind = index == 0 ? Property : -1;
                }
                else if (index == 0 && keywords.Contains(v))
                {
                    kind = Keyword;
                }
                else if (keywords.Contains(v))
                {
                    kind = Keyword;
                }

                if (kind >= 0)
                {
                    tokens.Add((i, m.Index, m.Length, kind, mods));
                }

                previous = v;
                index++;
            }
        }

        AddLanguageTokens(doc, lines, tokens);
        tokens.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return tokens;
    }

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)(?=\s*\()", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex CallName();

    [GeneratedRegex(@"^(?:dynamic\s+)?helper\s+([A-Za-z_][A-Za-z0-9_-]*)", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex HelperDeclaration();

    private static readonly string[] Volatile = ["currentTime", "currentDate", "osRandom", "environment"];

    /// <summary>Helper names, volatile built-ins and the operators of the note pipelines, with modifiers: dynamic (the result can differ between builds), baked, seeded (draws random numbers) and declaration.</summary>
    private static void AddLanguageTokens(AmpleDocument doc, string[] lines, List<(int Line, int Start, int Length, int Type, int Mods)> tokens)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            int hash = CommentStart(lines[i]);
            string code = hash < 0 ? lines[i] : lines[i][..hash];
            Match declaration = HelperDeclaration().Match(code);
            if (declaration.Success && doc.Helpers.FirstOrDefault(h => string.Equals(h.Name, declaration.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is { } declared)
            {
                Put(tokens, i, declaration.Groups[1].Index, declaration.Groups[1].Length, Function, 1 | (declared.Effect == "dynamic" ? 4 : 0) | (declared.Baked ? 8 : 0));
            }

            foreach (Match m in CallName().Matches(code))
            {
                string name = m.Groups[1].Value;
                if (doc.Blocks.Any(b => b.Line == i) || (declaration.Success && m.Index == declaration.Groups[1].Index)) continue;
                int mods;
                if (doc.Helpers.FirstOrDefault(h => h.Kind == "helper" && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) is { } helper)
                {
                    mods = (helper.Effect == "dynamic" ? 4 : 0) | (helper.Baked ? 8 : 0) | (helper.Dependencies.Contains("random") ? 16 : 0);
                }
                else if (Volatile.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    mods = 4;
                }
                else if (NoteCatalog.Signatures.TryGetValue(name, out NoteSignature? sig))
                {
                    mods = sig.Params.Any(p => p.Type == "RandomSource") || name is "stable" or "randomSeed" or "random-seed" ? 16 : 0;
                }
                else
                {
                    continue;
                }

                Put(tokens, i, m.Index, m.Length, Function, mods);
            }
        }
    }

    private static void Put(List<(int Line, int Start, int Length, int Type, int Mods)> tokens, int line, int start, int length, int type, int mods)
    {
        tokens.RemoveAll(t => t.Line == line && t.Start == start);
        tokens.Add((line, start, length, type, mods));
    }

    /// <summary>Encodes tokens as the protocol's relative integers.</summary>
    public static int[] Encode(IEnumerable<(int Line, int Start, int Length, int Type, int Mods)> tokens)
    {
        List<int> data = [];
        int pl = 0, ps = 0;
        foreach ((int line, int start, int length, int type, int mods) in tokens)
        {
            data.Add(line - pl);
            data.Add(line == pl ? start - ps : start);
            data.Add(length);
            data.Add(type);
            data.Add(mods);
            pl = line;
            ps = start;
        }

        return [.. data];
    }

    private static int CommentStart(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (line[i] == '#' && !inString && !(i > 0 && char.IsLetterOrDigit(line[i - 1])))
            {
                return i;
            }
        }

        return -1;
    }
}
