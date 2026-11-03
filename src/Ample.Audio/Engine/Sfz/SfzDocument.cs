using Ample.Core.Abstractions;

namespace Ample.Audio.Engine.Sfz;

/// <summary>
/// Parses the text of an SFZ instrument into a flat list of regions, each already carrying the effective
/// opcode value of every opcode set on it or on an enclosing <c>&lt;global&gt;</c>/<c>&lt;master&gt;</c>/<c>&lt;group&gt;</c>
/// header (inheritance resolved here, once, so later readers only ever see "the opcodes of this region").
/// No opcode gets semantic meaning in this type: that is <c>SfzReader</c>'s job.
/// </summary>
public static class SfzDocument
{
    /// <summary>One resolved region: its effective opcodes, lower-cased names, last value of a name at the same scope wins.</summary>
    public sealed record SfzRegion(IReadOnlyDictionary<string, string> Opcodes);

    /// <summary>
    /// Parses <paramref name="text"/>. <paramref name="resolveInclude"/> turns an <c>#include</c> path (as written in the
    /// source, not yet resolved) into the included text; it is called once per <c>#include</c>, depth first, so nested
    /// includes (an included file including another) work without this type knowing anything about the file system.
    /// </summary>
    public static IReadOnlyList<SfzRegion> Parse(string text, Func<string, string> resolveInclude)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(resolveInclude);
        Dictionary<string, string> defines = [];
        string expanded = ExpandIncludesAndDefines(text, resolveInclude, defines, 0);
        return ParseExpanded(expanded);
    }

    private const int MaxIncludeDepth = 32;

    private static string ExpandIncludesAndDefines(string text, Func<string, string> resolveInclude, Dictionary<string, string> defines, int depth)
    {
        if (depth > MaxIncludeDepth)
        {
            throw new AmpleException("AMP5395", "The SFZ file includes itself (or nests #include too deeply).");
        }

        System.Text.StringBuilder result = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\r')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (c == '#' && Matches(text, i, "#define"))
            {
                int end = text.IndexOf('\n', i);
                string line = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end;
                string[] parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1].StartsWith('$'))
                {
                    defines[parts[1]] = parts[2].Trim();
                }

                continue;
            }

            if (c == '#' && Matches(text, i, "#include"))
            {
                int end = text.IndexOf('\n', i);
                string line = (end < 0 ? text[i..] : text[i..end]).Trim();
                i = end < 0 ? text.Length : end;
                int q1 = line.IndexOf('"');
                int q2 = q1 < 0 ? -1 : line.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0)
                {
                    throw new AmpleException("AMP5395", "A #include line must name a quoted path: #include \"path.sfz\".");
                }

                string path = line[(q1 + 1)..q2];
                string included = resolveInclude(path);
                result.Append(ExpandIncludesAndDefines(included, p => resolveInclude(ResolveIncludeRelative(path, p)), defines, depth + 1));
                result.Append('\n');
                continue;
            }

            result.Append(c);
            i++;
        }

        return ApplyDefines(result.ToString(), defines);
    }

    /// <summary>An <c>#include</c> found inside an included file is resolved relative to the including file's own folder.</summary>
    private static string ResolveIncludeRelative(string parentPath, string childPath)
    {
        if (childPath.Contains('/') || childPath.Contains('\\'))
        {
            return childPath;
        }

        int slash = parentPath.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? childPath : parentPath[..(slash + 1)] + childPath;
    }

    private static bool Matches(string text, int at, string token) => at + token.Length <= text.Length && string.CompareOrdinal(text, at, token, 0, token.Length) == 0 && (at + token.Length == text.Length || char.IsWhiteSpace(text[at + token.Length]));

    private static string ApplyDefines(string text, Dictionary<string, string> defines)
    {
        if (defines.Count == 0)
        {
            return text;
        }

        // Longest name first, so "$NOTE2" is not partially replaced by a rule for "$NOTE".
        foreach ((string name, string value) in defines.OrderByDescending(d => d.Key.Length))
        {
            text = text.Replace(name, value, StringComparison.Ordinal);
        }

        return text;
    }

    private static readonly string[] Headers = ["<global>", "<master>", "<group>", "<region>", "<control>", "<curve>", "<effect>"];

    private static List<SfzRegion> ParseExpanded(string text)
    {
        List<SfzRegion> regions = [];
        Dictionary<string, string> global = [];
        Dictionary<string, string> master = [];
        Dictionary<string, string> group = [];
        Dictionary<string, string>? current = null;
        string? currentHeader = null;
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '<')
            {
                int close = text.IndexOf('>', i);
                if (close < 0)
                {
                    throw new AmpleException("AMP5395", "An SFZ header '<' is not closed with '>'.");
                }

                string header = text[i..(close + 1)].ToLowerInvariant();
                i = close + 1;
                FlushPending(regions, currentHeader, current, global, master, group);
                switch (header)
                {
                    case "<global>":
                        global = [];
                        current = global;
                        currentHeader = header;
                        break;
                    case "<master>":
                        master = [];
                        current = master;
                        currentHeader = header;
                        break;
                    case "<group>":
                        group = [];
                        current = group;
                        currentHeader = header;
                        break;
                    case "<region>":
                        current = [];
                        currentHeader = header;
                        break;
                    default:
                        // <control>/<curve>/<effect> and anything not yet recognised: skip its opcodes, they do not
                        // become a region and are not inherited (keeping this reader to the instrument-building subset).
                        current = [];
                        currentHeader = null;
                        break;
                }

                continue;
            }

            // An opcode token: name=value, value runs up to the next recognised header or opcode token (SFZ has no
            // quoting), so scan ahead for the next "<" or the next "word=" at the start of a token.
            int start = i;
            int eq = FindOpcodeEquals(text, i);
            if (eq < 0)
            {
                break;
            }

            string name = text[start..eq].Trim().ToLowerInvariant();
            int valueStart = eq + 1;
            int valueEnd = FindValueEnd(text, valueStart);
            string value = text[valueStart..valueEnd].Trim();
            i = valueEnd;
            if (current is not null && name.Length > 0)
            {
                current[name] = value;
            }
        }

        FlushPending(regions, currentHeader, current, global, master, group);
        return regions;
    }

    private static void FlushPending(List<SfzRegion> regions, string? header, Dictionary<string, string>? current, Dictionary<string, string> global, Dictionary<string, string> master, Dictionary<string, string> group)
    {
        if (header != "<region>" || current is null)
        {
            return;
        }

        Dictionary<string, string> effective = new(global, StringComparer.Ordinal);
        foreach ((string k, string v) in master)
        {
            effective[k] = v;
        }

        foreach ((string k, string v) in group)
        {
            effective[k] = v;
        }

        foreach ((string k, string v) in current)
        {
            effective[k] = v;
        }

        regions.Add(new SfzRegion(effective));
    }

    /// <summary>Finds the '=' of the opcode token starting at or after <paramref name="from"/> (skipping whitespace first).</summary>
    private static int FindOpcodeEquals(string text, int from)
    {
        int i = from;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        int eq = text.IndexOf('=', i);
        return eq;
    }

    /// <summary>
    /// A value runs until whitespace that is immediately followed by another opcode token (next non-space run contains
    /// an unescaped <c>=</c> before the next whitespace or <c>&lt;</c>) or by a header (<c>&lt;</c>). This lets values
    /// contain spaces (<c>sample=Kick In.wav</c>) while still splitting <c>a=1 b=2</c> into two opcodes.
    /// </summary>
    private static int FindValueEnd(string text, int from)
    {
        int i = from;
        int lastNonSpace = from;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                int j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                if (j >= text.Length || text[j] == '<' || StartsNextOpcode(text, j))
                {
                    return lastNonSpace + 1;
                }

                i = j;
                continue;
            }

            lastNonSpace = i;
            i++;
        }

        // When `from` is already at (or the text ends exactly at) the opcode's '=', lastNonSpace stays at `from`
        // itself (never advanced by the loop above), which can equal text.Length for a truncated "name=" at the
        // very end of the file - without the clamp this returns text.Length + 1, an out-of-range Substring length.
        return Math.Min(lastNonSpace + 1, text.Length);
    }

    /// <summary>A word at <paramref name="at"/> is the start of the next opcode token when it is <c>name=...</c> with no spaces in <c>name</c>.</summary>
    private static bool StartsNextOpcode(string text, int at)
    {
        int i = at;
        while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i]) && text[i] != '<')
        {
            i++;
        }

        return i < text.Length && text[i] == '=' && i > at;
    }
}
