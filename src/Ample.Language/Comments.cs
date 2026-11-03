using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// What a doc comment says about the declaration under it (docs/language/18-comments-and-doc-comments.md): a summary and the tags <c>@param name text</c>, <c>@return text</c>, <c>@example code</c> and <c>@deprecated text</c>.
/// The editors show it when the name is hovered.
/// </summary>
/// <param name="Summary">The text before the first tag.</param>
/// <param name="Params">The parameters described, in the order they were written.</param>
/// <param name="Returns">What the helper gives back, or null.</param>
/// <param name="Examples">The examples.</param>
/// <param name="Deprecated">Why it should not be used any more (empty when no reason was given), or null when it is not deprecated.</param>
public sealed record DocComment(string Summary, IReadOnlyList<DocParam> Params, string? Returns, IReadOnlyList<string> Examples, string? Deprecated)
{
    /// <summary>Gets the type written for the returned value (<c>@return {Notes|Int} text</c>), or null.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Gets the 0-based line of the original text where the comment starts.</summary>
    public int FirstLine { get; init; }

    /// <summary>Gets the 0-based line of the original text where the comment ends.</summary>
    public int LastLine { get; init; }

    /// <summary>Gets the text of one parameter, or null when it is not described.</summary>
    /// <param name="name">The parameter name (compared ignoring case).</param>
    /// <returns>The text, or null.</returns>
    public string? ParamText(string name) => Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Text;

    /// <summary>Gets the type written for one parameter (<c>@param {Interval|Int} by text</c>), or null.</summary>
    /// <param name="name">The parameter name (compared ignoring case).</param>
    /// <returns>The type as written, or null.</returns>
    public string? ParamType(string name) => Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Type;

    /// <param name="declaredReturn">The return type of the declaration, to say so when the comment gives another one (or null).</param>
    /// <summary>The comment as markdown, for a hover: the summary, then the notes about a deprecated name, the returned value and the examples (the parameters are listed by the signature of the hover itself).</summary>
    /// <returns>The markdown.</returns>
    public string Markdown(string? declaredReturn = null)
    {
        List<string> parts = [];
        if (Deprecated is not null) parts.Add("**Deprecated**" + (Deprecated.Length == 0 ? "." : ": " + Deprecated));
        if (Summary.Length > 0) parts.Add(Summary);
        if (Returns is { Length: > 0 } || ReturnType is not null) parts.Add("**Returns** " + (ReturnType is null ? string.Empty : $"`{ReturnType}` ") + Returns + (ReturnType is not null && declaredReturn is not null && !string.Equals(ReturnType.Replace(" ", string.Empty, StringComparison.Ordinal), declaredReturn.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase) ? $" *(declared `{declaredReturn}`)*" : string.Empty));
        foreach (string example in Examples) parts.Add("**Example**\n```\n" + example + "\n```");
        return string.Join("\n\n", parts);
    }
}

/// <summary>One <c>@param</c> of a doc comment.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Text">What it means.</param>
/// <param name="Type">The type as written (a union is written with |, for example Interval|Int), or null.</param>
public sealed record DocParam(string Name, string Text, string? Type);

/// <summary>Block comments (<c>/* ... */</c>, next to the <c>#</c> comment of a line) and doc comments (<c>/** ... */</c> or lines that start with <c>##</c>) in a song or a module.</summary>
public static class Comments
{
    private static bool Word(char c) => char.IsLetterOrDigit(c);

    /// <summary>Replaces every block comment with spaces (new lines stay), so that lines, columns and offsets stay the same and the parser sees a blank where the comment was. A <c>/*</c> inside a string, after a <c>#</c> or right after a letter or digit (a division) starts nothing.</summary>
    /// <param name="text">The text, with LF line ends.</param>
    /// <param name="unterminated">The 0-based line of a comment that is never closed (it runs to the end of the text), or -1.</param>
    /// <returns>The text without block comments.</returns>
    public static string BlankBlocks(string text, out int unterminated) => BlankBlocks(text, out unterminated, null);

    /// <summary>Like <see cref="BlankBlocks(string, out int)"/>, and says where the comments were.</summary>
    /// <param name="text">The text, with LF line ends.</param>
    /// <param name="unterminated">The 0-based line of a comment that is never closed, or -1.</param>
    /// <param name="ranges">Receives the 0-based line, start column and length of every piece of a comment (one per line it covers); may be null.</param>
    /// <returns>The text without block comments.</returns>
    public static string BlankBlocks(string text, out int unterminated, List<(int Line, int Start, int Length)>? ranges)
    {
        unterminated = -1;
        if (!text.Contains("/*", StringComparison.Ordinal)) return text;
        StringBuilder sb = new(text.Length);
        bool inString = false, inLine = false, inBlock = false;
        int line = 0, openedAt = -1, lineStart = 0, pieceStart = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                if (inBlock && pieceStart >= 0) ranges?.Add((line, pieceStart, i - lineStart - pieceStart));
                line++; lineStart = i + 1; pieceStart = inBlock ? 0 : -1; inString = false; inLine = false; sb.Append(c);
                continue;
            }

            if (inBlock)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inBlock = false; sb.Append("  "); i++; ranges?.Add((line, pieceStart, i + 1 - lineStart - pieceStart)); pieceStart = -1; }
                else sb.Append(c == '\r' ? c : ' ');
                continue;
            }

            if (inLine) { sb.Append(c); continue; }
            if (c == '"') inString = !inString;
            else if (!inString && c == '#' && !(sb.Length > 0 && Word(sb[^1]))) inLine = true;
            else if (!inString && c == '/' && i + 1 < text.Length && text[i + 1] == '*' && !(sb.Length > 0 && Word(sb[^1])))
            {
                inBlock = true;
                openedAt = line;
                pieceStart = i - lineStart;
                sb.Append("  ");
                i++;
                continue;
            }

            sb.Append(c);
        }

        if (inBlock)
        {
            unterminated = openedAt;
            if (pieceStart >= 0) ranges?.Add((line, pieceStart, text.Length - lineStart - pieceStart));
        }

        return sb.ToString();
    }

    /// <summary>Finds the doc comments of a text: a <c>/** ... */</c> block, or lines that start with <c>##</c>, directly above a line of code (no blank line between). The result maps the 0-based line of that code to the comment.</summary>
    /// <param name="lines">The lines of the original text (comments included).</param>
    /// <returns>The doc comment by the line of the declaration it describes.</returns>
    public static Dictionary<int, DocComment> DocsOf(IReadOnlyList<string> lines)
    {
        Dictionary<int, DocComment> found = [];
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            List<string> body = [];
            int last;
            if (trimmed.StartsWith("/**", StringComparison.Ordinal) && !trimmed.StartsWith("/**/", StringComparison.Ordinal))
            {
                last = i;
                string rest = trimmed[3..];
                while (true)
                {
                    int close = rest.IndexOf("*/", StringComparison.Ordinal);
                    if (close >= 0) { body.Add(Clean(rest[..close])); break; }
                    body.Add(Clean(rest));
                    if (++last >= lines.Count) { last = -1; break; }
                    rest = lines[last].Trim();
                }

                if (last < 0) break;   // never closed: the parser reports it
            }
            else if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                last = i;
                while (last < lines.Count && lines[last].Trim().StartsWith("##", StringComparison.Ordinal)) { body.Add(lines[last].Trim().TrimStart('#').TrimStart(' ')); last++; }
                last--;
            }
            else continue;

            int declaration = last + 1;
            while (declaration < lines.Count && lines[declaration].TrimStart().StartsWith("#[", StringComparison.Ordinal)) declaration++;   // attributes (#[DisplayName("...")]) stand between the comment and what it describes
            if (declaration < lines.Count && lines[declaration].Trim().Length > 0 && !lines[declaration].TrimStart().StartsWith('#') && !lines[declaration].TrimStart().StartsWith("/*", StringComparison.Ordinal))
                found[declaration] = Parse(body) with { FirstLine = i, LastLine = last };
            i = last;
        }

        return found;
    }

    private static string Clean(string line) => line.Trim().TrimStart('*').TrimStart(' ');

    /// <summary>Reads the lines of a doc comment (without the comment marks).</summary>
    /// <param name="body">The lines.</param>
    /// <returns>The comment.</returns>
    public static DocComment Parse(IReadOnlyList<string> body)
    {
        List<string> summary = [];
        List<DocParam> parameters = [];
        List<string> examples = [];
        string? returns = null, deprecated = null, returnType = null;
        string tag = string.Empty;
        foreach (string raw in body)
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('@'))
            {
                string trimmed = line.TrimStart();
                int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
                tag = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
                string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
                switch (tag)
                {
                    case "@param":
                        string typed = TakeType(rest, out string? paramType);
                        string[] words = typed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        parameters.Add(new DocParam(words.Length > 0 ? words[0].TrimStart('$') : string.Empty, words.Length > 1 ? words[1].TrimStart('-', ' ') : string.Empty, paramType));
                        break;
                    case "@return" or "@returns": returns = TakeType(rest, out returnType); break;
                    case "@example": examples.Add(rest); break;
                    case "@deprecated": deprecated = rest; break;
                    default: tag = string.Empty; break;
                }

                continue;
            }

            switch (tag)
            {
                case "@param" when parameters.Count > 0 && line.Trim().Length > 0: parameters[^1] = parameters[^1] with { Text = (parameters[^1].Text + " " + line.Trim()).Trim() }; break;
                case "@return" or "@returns" when line.Trim().Length > 0: returns = ((returns ?? string.Empty) + " " + line.Trim()).Trim(); break;
                case "@example" when examples.Count > 0: examples[^1] = examples[^1].Length == 0 ? line : examples[^1] + "\n" + line; break;
                case "@deprecated" when line.Trim().Length > 0: deprecated = ((deprecated ?? string.Empty) + " " + line.Trim()).Trim(); break;
                case "": summary.Add(line); break;
            }
        }

        return new DocComment(string.Join('\n', summary).Trim(), parameters, returns, [.. examples.Select(e => e.Trim('\n'))], deprecated) { ReturnType = returnType };
    }

    /// <summary>Takes an optional type off the front of the text of a tag: <c>{Interval|Int}</c> in braces, or a bare word that is a type of the language, a union (with |), a generic (List&lt;Int&gt;) or an optional type (Int?).</summary>
    private static string TakeType(string rest, out string? type)
    {
        type = null;
        if (rest.StartsWith('{'))
        {
            int close = rest.IndexOf('}', StringComparison.Ordinal);
            if (close > 0) { type = rest[1..close].Trim().Replace(" ", string.Empty, StringComparison.Ordinal); return rest[(close + 1)..].TrimStart(); }
        }

        string remaining = DocTypes.TakeWord(rest, out string first);
        if (IsTypeWord(first))
        {
            type = first.Replace(" ", string.Empty, StringComparison.Ordinal);
            return remaining;
        }

        return rest;
    }

    private static bool IsTypeWord(string first) => first.Length > 0 && (first.Contains('|', StringComparison.Ordinal) || first.Contains('<', StringComparison.Ordinal) || first.EndsWith('?') || NoteCatalog.Types.ContainsKey(first) || Builtin.Contains(first));

    /// <summary>Where a tag stands in the original text: the line, the type (when one is written), the name of a parameter, the text before the tag and where the block of the tag ends.</summary>
    /// <param name="Line">The 0-based line.</param>
    /// <param name="TypeStart">The start of the type text (inside the braces), or -1.</param>
    /// <param name="TypeEnd">The end of the type text.</param>
    /// <param name="NameStart">The start of the name of a parameter, or -1.</param>
    /// <param name="NameEnd">The end of the name.</param>
    /// <param name="Prefix">The text of the line before the tag (the comment mark and the spaces).</param>
    /// <param name="Length">The length of the line.</param>
    /// <param name="EndLine">The last 0-based line of the block of the tag (the text that goes on over the next lines).</param>
    /// <param name="StandsAlone">Whether the line holds nothing but the tag, so that the whole line can be removed.</param>
    internal sealed record TagSpan(int Line, int TypeStart, int TypeEnd, int NameStart, int NameEnd, string Prefix, int Length, int EndLine, bool StandsAlone);

    /// <summary>Finds the line of a tag of a doc comment in the original text, and the places of its type and name.</summary>
    /// <param name="lines">The original lines.</param>
    /// <param name="doc">The comment.</param>
    /// <param name="tag">param or return.</param>
    /// <param name="name">The name of the parameter looked for (ignoring case), or null for a return.</param>
    /// <returns>The place, or null.</returns>
    internal static TagSpan? FindTag(IReadOnlyList<string> lines, DocComment doc, string tag, string? name)
    {
        Regex pattern = new(tag == "param" ? @"@param(?=\s|$)" : @"@returns?(?=\s|$)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        for (int k = doc.FirstLine; k <= doc.LastLine && k < lines.Count; k++)
        {
            string text = lines[k];
            Match m = pattern.Match(text);
            if (!m.Success) continue;
            int pos = m.Index + m.Length;
            while (pos < text.Length && text[pos] == ' ') pos++;
            int typeStart = -1, typeEnd = -1;
            if (pos < text.Length && text[pos] == '{')
            {
                int close = text.IndexOf('}', pos);
                if (close > pos) { typeStart = pos + 1; typeEnd = close; pos = close + 1; }
            }
            else
            {
                int end = pos, depth = 0;
                for (; end < text.Length; end++)
                {
                    if (text[end] == '<') depth++;
                    else if (text[end] == '>') depth = Math.Max(0, depth - 1);
                    else if (text[end] == ' ' && depth == 0) break;
                }

                if (end > pos && IsTypeWord(text[pos..end])) { typeStart = pos; typeEnd = end; pos = end; }
            }

            while (pos < text.Length && text[pos] == ' ') pos++;
            int nameStart = -1, nameEnd = -1;
            if (tag == "param")
            {
                int end = pos;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                nameStart = pos < end && text[pos] == '$' ? pos + 1 : pos;
                nameEnd = end;
                if (name is not null && !string.Equals(text[nameStart..nameEnd], name, StringComparison.OrdinalIgnoreCase)) continue;
            }

            int endLine = k;
            while (endLine + 1 <= doc.LastLine && endLine + 1 < lines.Count)
            {
                string next = lines[endLine + 1].Trim();
                if (next.StartsWith("*/", StringComparison.Ordinal)) break;
                string content = next.TrimStart('*', '#', ' ');
                if (content.Length == 0 || content.StartsWith('@')) break;
                endLine++;
            }

            string prefix = text[..m.Index];
            return new TagSpan(k, typeStart, typeEnd, nameStart, nameEnd, prefix, text.Length, endLine, Regex.IsMatch(prefix, @"^\s*(?:\*+|##?)?\s*$", RegexOptions.None, TimeSpan.FromSeconds(1)) && !text.Contains("*/", StringComparison.Ordinal));
        }

        return null;
    }

    /// <summary>Gets the type names of the language layer that a doc comment may use next to the catalogue and the aliases of the file.</summary>
    public static IReadOnlySet<string> BuiltinTypes => Builtin;

    private static readonly HashSet<string> Builtin = ["Int", "Float", "Bool", "String", "Text", "Number", "Notes", "Note", "Seed", "List", "Length", "Time", "Duration", "Interval", "Gain", "Percent"];
}
