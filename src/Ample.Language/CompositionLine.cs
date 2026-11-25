using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// Reads the first line of a song: <c>composition "name" { ample 1  tempo 120  meter 3/4  master-seed 7 }</c>. The settings in the braces may come in any order
/// (owner, 2026-11-24: "meter 3/4" written after the master-seed made the whole line unreadable, and the song fell back to the default tempo); each may appear once,
/// tempo and master-seed are required, ample and meter are optional. The parser and the formatter both read the line here (the formatter writes the usual order).
/// </summary>
public static partial class CompositionLine
{
    /// <summary>What a composition line says. The raw texts are as written (the parser checks their ranges); the indexes are columns in the line (0-based).</summary>
    /// <param name="Word">project or composition.</param>
    /// <param name="Name">The quoted name, with its quotes.</param>
    /// <param name="Tempo">The tempo as written.</param>
    /// <param name="Seed">The master seed as written.</param>
    /// <param name="Ample">The AMPLE version as written, or null.</param>
    /// <param name="AmpleIndex">The column of the AMPLE version, or -1.</param>
    /// <param name="MeterTop">The top number of the meter as written, or null.</param>
    /// <param name="MeterBottom">The bottom number of the meter as written, or null.</param>
    public sealed record Parts(string Word, string Name, string Tempo, string Seed, string? Ample, int AmpleIndex, string? MeterTop, string? MeterBottom);

    [GeneratedRegex("^(project|composition)\\s*(\"(?:[^\"\\\\]|\\\\.)*\")\\s*\\{(?<body>.*)\\}\\s*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Shape();

    [GeneratedRegex(
        "\\G\\s*(?:(?<key>ample)\\s+(?<value>\\d+)|(?<key>tempo)\\s+(?<value>\\d+(?:\\.\\d+)?)|(?<key>meter)\\s+(?<value>\\d+/\\d+)|(?<key>master-seed)\\s+(?<value>\\d+))"
            + "(?=\\s|$)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex Setting();

    /// <summary>Reads a composition line (already trimmed); null when it is not one: a setting that is not known, given twice, or no tempo or master-seed.</summary>
    /// <param name="line">The line.</param>
    /// <param name="requireSpace">True for the parser (a space after the word, as before); the formatter also reads <c>composition"x"</c>.</param>
    public static Parts? Read(string line, bool requireSpace = true)
    {
        Match shape = Shape().Match(line);
        if (!shape.Success) return null;
        if (requireSpace && shape.Groups[2].Index == shape.Groups[1].Length) return null;
        Group body = shape.Groups["body"];
        Dictionary<string, (string Value, int Index)> found = new(StringComparer.Ordinal);
        int at = 0;
        string text = body.Value;
        while (true)
        {
            Match setting = Setting().Match(text, at);
            if (!setting.Success) break;
            string key = setting.Groups["key"].Value;
            if (!found.TryAdd(key, (setting.Groups["value"].Value, body.Index + setting.Groups["value"].Index))) return null;   // given twice
            at = setting.Index + setting.Length;
        }

        if (text[at..].Trim().Length > 0 || !found.TryGetValue("tempo", out (string Value, int Index) tempo) || !found.TryGetValue("master-seed", out (string Value, int Index) seed)) return null;
        string? meter = found.TryGetValue("meter", out (string Value, int Index) m) ? m.Value : null;
        (string Value, int Index)? ample = found.TryGetValue("ample", out (string Value, int Index) a) ? a : null;
        return new Parts(
            shape.Groups[1].Value,
            shape.Groups[2].Value,
            tempo.Value,
            seed.Value,
            ample?.Value,
            ample?.Index ?? -1,
            meter?.Split('/')[0],
            meter?.Split('/')[1]);
    }

    /// <summary>Writes the line in the usual order: ample, tempo, meter, master-seed.</summary>
    /// <param name="parts">What the line says.</param>
    public static string Format(Parts parts)
    {
        string ample = parts.Ample is null ? string.Empty : "ample " + parts.Ample + "  ";
        string meter = parts.MeterTop is null ? string.Empty : "meter " + parts.MeterTop + "/" + parts.MeterBottom + "  ";
        return $"{parts.Word} {parts.Name} {{ {ample}tempo {parts.Tempo}  {meter}master-seed {parts.Seed} }}";
    }
}
