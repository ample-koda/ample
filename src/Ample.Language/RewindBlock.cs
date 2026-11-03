using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// The scratch block of a rewind block: <c>scratch { at 6s { duration 1.5s; back 0.5s } }</c> is the readable form of the events text
/// <c>at 6s scratch 1.5s back 0.5s</c>. The song keeps the block; the engine reads the events text made from it.
/// </summary>
public static partial class RewindBlock
{
    [GeneratedRegex(@"\bat\s+(?<t>[^\s{;]+)\s*\{(?<b>[^{}]*)\}")]
    private static partial Regex AtBlock();

    [GeneratedRegex(@"^-?\d+(\.\d+)?(s|ms|bars?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeWord();

    /// <summary>Turns the lines inside <c>scratch { ... }</c> into the events text. False with a plain sentence when a line does not belong.</summary>
    public static bool TryCompact(IEnumerable<string> body, out string events, out string error)
    {
        events = string.Empty;
        error = string.Empty;
        string text = string.Join('\n', body.Select(l => { int hash = l.IndexOf('#'); return hash >= 0 ? l[..hash] : l; }));
        List<string> parts = [];
        foreach (Match m in AtBlock().Matches(text))
        {
            string at = m.Groups["t"].Value;
            if (!TimeWord().IsMatch(at)) { error = $"'{at}' is not a time: write seconds (6s), milliseconds (500ms) or bars (4.5)."; return false; }
            string duration = "1s", back = "0";
            foreach (string statement in m.Groups["b"].Value.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                string[] words = statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length != 2 || (words[0] != "duration" && words[0] != "back"))
                {
                    error = $"'{statement}' is not known inside 'at {at}': use 'duration <time>' and 'back <time>'.";
                    return false;
                }

                if (!TimeWord().IsMatch(words[1])) { error = $"'{words[1]}' is not a time: write seconds (1.5s), milliseconds (500ms) or bars (4.5)."; return false; }
                if (words[0] == "duration") duration = words[1]; else back = words[1];
            }

            parts.Add($"at {at} scratch {duration} back {back}");
        }

        string rest = AtBlock().Replace(text, string.Empty).Replace(";", string.Empty);
        if (rest.Trim().Length > 0)
        {
            error = $"'{rest.Trim().Split('\n')[0].Trim()}' does not belong in the scratch block: write 'at <time> {{ duration <time>; back <time> }}'.";
            return false;
        }

        events = string.Join("; ", parts);
        return true;
    }

    /// <summary>The scratch block for an events text, indented with <paramref name="indent"/>; null when the text cannot be read (it then stays a plain events line).</summary>
    public static string[]? Expand(string events, string indent)
    {
        List<string> lines = [indent + "scratch {"];
        foreach (string chunk in events.Trim().Trim('"').Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            string[] words = chunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string? at = null, duration = "1s", back = "0";
            if (words.Length % 2 != 0) return null;
            for (int i = 0; i < words.Length; i += 2)
            {
                if (!TimeWord().IsMatch(words[i + 1])) return null;
                switch (words[i].ToLowerInvariant())
                {
                    case "at": at = words[i + 1]; break;
                    case "scratch": duration = words[i + 1]; break;
                    case "back": back = words[i + 1]; break;
                    default: return null;
                }
            }

            if (at is null) return null;
            lines.Add($"{indent}  at {at} {{ duration {duration}; back {back} }}");
        }

        lines.Add(indent + "}");
        return [.. lines];
    }
}
