namespace Ample.Language;

/// <summary>
/// The types written in a doc comment (docs/language/18-comments-and-doc-comments.md): a union (<c>Interval|Int</c>), an optional type (<c>Int?</c>), a list (<c>Int[]</c>) and a generic (<c>List&lt;Int|Float&gt;</c>, <c>Range&lt;Pitch&gt;</c>),
/// split only where a <c>|</c> or a <c>,</c> stands outside the angle brackets, so that the arguments of a generic are types of their own.
/// </summary>
internal static class DocTypes
{
    /// <summary>Splits a text at a separator that is not inside angle brackets.</summary>
    /// <param name="text">The text.</param>
    /// <param name="separator">The separator.</param>
    /// <returns>The parts, trimmed, without empty ones.</returns>
    public static List<string> SplitTop(string text, char separator)
    {
        List<string> parts = [];
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth = Math.Max(0, depth - 1);
            else if (text[i] == separator && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return [.. parts.Where(p => p.Length > 0)];
    }

    /// <summary>Gets the type as a set of canonical members: unions as members, aliases written out, <c>?</c> dropped, case ignored, the arguments of a generic canonical too (so <c>List&lt;Float|Int&gt;</c> and <c>List&lt;Int|Float&gt;</c> are the same).</summary>
    /// <param name="type">The type as written.</param>
    /// <param name="alias">The members of a type alias of the file, or null when the name is not an alias.</param>
    /// <param name="depth">The depth of the aliases written out so far.</param>
    /// <returns>The canonical members.</returns>
    public static HashSet<string> Canonical(string type, Func<string, IReadOnlyList<string>?> alias, int depth = 0)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        foreach (string raw in SplitTop(type, '|'))
        {
            string member = raw.TrimEnd('?').Trim();
            int open = member.IndexOf('<', StringComparison.Ordinal);
            if (open > 0 && member.EndsWith('>'))
            {
                string name = member[..open].Trim().ToLowerInvariant();
                IEnumerable<string> arguments = SplitTop(member[(open + 1)..^1], ',').Select(a => "{" + string.Join('|', Canonical(a, alias, depth).Order(StringComparer.Ordinal)) + "}");
                set.Add(name + "<" + string.Join(',', arguments) + ">");
            }
            else if (depth < 4 && alias(member) is { } members) set.UnionWith(members.SelectMany(m => Canonical(m, alias, depth + 1)));
            else set.Add(member.ToLowerInvariant());
        }

        return set;
    }

    /// <summary>Gets every type name used in a type, the arguments of generics included (<c>List&lt;Int|Float&gt;</c> uses List, Int and Float).</summary>
    /// <param name="type">The type as written.</param>
    /// <returns>The names.</returns>
    public static IEnumerable<string> Names(string type)
    {
        foreach (string raw in SplitTop(type, '|'))
        {
            string member = raw.TrimEnd('?').Trim();
            int open = member.IndexOf('<', StringComparison.Ordinal);
            if (open > 0 && member.EndsWith('>'))
            {
                yield return member[..open].Trim();
                foreach (string argument in SplitTop(member[(open + 1)..^1], ','))
                    foreach (string inner in Names(argument)) yield return inner;
            }
            else yield return member;
        }
    }

    /// <summary>Takes a type off the front of the text of a tag: the first word, which can contain spaces inside angle brackets.</summary>
    /// <param name="rest">The text after the tag.</param>
    /// <param name="type">The first word.</param>
    /// <returns>The text after it.</returns>
    public static string TakeWord(string rest, out string type)
    {
        int depth = 0, i = 0;
        for (; i < rest.Length; i++)
        {
            if (rest[i] == '<') depth++;
            else if (rest[i] == '>') depth = Math.Max(0, depth - 1);
            else if (rest[i] == ' ' && depth == 0) break;
        }

        type = rest[..i];
        return rest[i..].TrimStart();
    }
}
