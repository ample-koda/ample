using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// The view block of a song: what the canvas keeps of the owner's own arrangement (groups, block sizes, pinned blocks). The song does not depend on it.
/// Older songs keep the same in comment lines (<c># view group {json}</c>, <c># view size</c>, <c># view pin</c>); <see cref="Upgrade"/> turns those into the block.
/// </summary>
public static partial class ViewBlock
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Gets a value that says whether a trimmed line is one of the old comment lines that kept the view.</summary>
    /// <param name="trimmed">The trimmed line.</param>
    /// <returns>True for <c># view …</c> and <c># view: …</c>.</returns>
    public static bool IsLegacyLine(string trimmed) => trimmed.StartsWith("# view ", StringComparison.Ordinal) || trimmed.StartsWith("# view:", StringComparison.Ordinal);

    /// <summary>Checks one line inside a view block.</summary>
    /// <param name="trimmed">The trimmed line (not a comment, not a closing brace).</param>
    /// <param name="inGroup">True inside a <c>group … { }</c>.</param>
    /// <returns>Whether the line opens a group, and an error text when the line is not valid (empty when it is).</returns>
    internal static (bool Opens, string Error) CheckLine(string trimmed, bool inGroup)
    {
        List<string> words = [.. Token().Matches(trimmed).Select(m => m.Value)];
        string first = words.Count > 0 ? words[0] : string.Empty;
        if (!inGroup)
        {
            return first switch
            {
                "group" when words.Count == 3 && words[2] == "{" => (true, string.Empty),
                "size" when words.Count == 3 && double.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 0 => (false, string.Empty),
                "pin" when words.Count == 2 => (false, string.Empty),
                _ => (false, "Expected: group <id> {  |  size <block> <scale>  |  pin <block>  |  }"),
            };
        }

        return first switch
        {
            "title" or "member" when words.Count == 2 => (false, string.Empty),
            "x" or "y" when words.Count == 2 && double.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _) => (false, string.Empty),
            "label" when words.Count == 3 => (false, string.Empty),
            _ when words.Count >= 2 && Key().IsMatch(first) && ValidJson(trimmed[first.Length..].Trim()) => (false, string.Empty),
            _ => (false, "Expected in a group: title <text>  |  member <block>  |  x <n>  |  y <n>  |  label <key> <text>  |  <name> <json value>  |  }"),
        };
    }

    /// <summary>Turns the old comment lines of the view into a view block.</summary>
    /// <param name="legacy">The lines (trimmed or not): <c># view group {json}</c>, <c># view size &lt;block&gt; &lt;scale&gt;</c>, <c># view pin &lt;block&gt;</c> and the <c># view:</c> header.</param>
    /// <returns>The text of the block (no final newline). Lines that cannot be read stay behind it as comments.</returns>
    public static string Upgrade(IEnumerable<string> legacy)
    {
        List<string> groups = [], sizes = [], pins = [], unread = [];
        foreach (string raw in legacy)
        {
            string line = raw.Trim();
            if (line.StartsWith("# view:", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("# view group ", StringComparison.Ordinal))
            {
                string[]? group = GroupLines(line["# view group ".Length..]);
                if (group is null)
                {
                    unread.Add(line);
                }
                else
                {
                    groups.AddRange(group);
                }
            }
            else if (line.StartsWith("# view size ", StringComparison.Ordinal))
            {
                string rest = line["# view size ".Length..].Trim();
                int cut = rest.LastIndexOf(' ');
                if (cut > 0 && double.TryParse(rest[(cut + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 0)
                {
                    sizes.Add($"  size {Word(rest[..cut])} {rest[(cut + 1)..]}");
                }
                else
                {
                    unread.Add(line);
                }
            }
            else if (line.StartsWith("# view pin ", StringComparison.Ordinal) && line["# view pin ".Length..].Trim().Length > 0)
            {
                pins.Add(line["# view pin ".Length..].Trim());
            }
            else
            {
                unread.Add(line);
            }
        }

        StringBuilder text = new("view {\n");
        foreach (string line in groups.Concat(sizes).Concat(pins.Order(StringComparer.Ordinal).Select(p => $"  pin {Word(p)}")))
        {
            text.Append(line).Append('\n');
        }

        text.Append('}');
        foreach (string line in unread)
        {
            text.Append("\n# not read: ").Append(line[2..]);
        }

        return text.ToString();
    }

    /// <summary>Gets a name as it is written in the view block: bare when it is plain, otherwise in quotes.</summary>
    /// <param name="name">A block name, group id or text.</param>
    /// <returns>The word.</returns>
    internal static string Word(string name) => BareWord().IsMatch(name) ? name : JsonSerializer.Serialize(name, Json);

    private static string[]? GroupLines(string json)
    {
        JsonObject? group;
        try
        {
            group = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (group?["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.String || group["members"] is not JsonArray members)
        {
            return null;
        }

        List<string> lines = [$"  group {Word(id.GetValue<string>())} {{"];
        if (group["title"] is JsonValue title && title.GetValueKind() == JsonValueKind.String)
        {
            lines.Add($"    title {title.ToJsonString(Json)}");
        }

        lines.AddRange(members.Select(m => $"    member {Word(m?.GetValue<string>() ?? string.Empty)}"));
        foreach ((string key, JsonNode? value) in group.Where(p => p.Key is not ("id" or "title" or "members" or "labels") && Key().IsMatch(p.Key)))
        {
            lines.Add($"    {key} {value?.ToJsonString(Json) ?? "null"}");
        }

        if (group["labels"] is JsonObject labels)
        {
            lines.AddRange(labels.Select(p => $"    label {JsonSerializer.Serialize(p.Key, Json)} {p.Value?.ToJsonString(Json) ?? "null"}"));
        }

        lines.Add("  }");
        return [.. lines];
    }

    private static bool ValidJson(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|\\S+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Token();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Key();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BareWord();
}
