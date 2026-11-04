using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Server;

internal sealed record AudioPreviewRequest(string Url, string Label);

internal static class AiAudioPreviewSafety
{
    internal static List<AudioPreviewRequest> ParseBatch(JsonElement body, IReadOnlyCollection<string> knownUrls, int limit, IReadOnlyCollection<KeyValuePair<string, string>>? chosen = null)
    {
        bool listed = body.TryGetProperty("samples", out JsonElement given) && given.ValueKind == JsonValueKind.Array && given.GetArrayLength() > 0;
        if (!listed && chosen is { Count: > 0 })
            return chosen.DistinctBy(c => c.Key).TakeLast(limit).Select(c => new AudioPreviewRequest(c.Key, c.Value.Length is > 0 and <= 120 ? c.Value : "AudioHero preview")).ToList();   // no list given: the previews this task started, so long signed URLs need not be copied
        if (!body.TryGetProperty("samples", out JsonElement samples) || samples.ValueKind != JsonValueKind.Array || samples.GetArrayLength() is < 1 || samples.GetArrayLength() > limit) throw new AmpleException("AMP5458", $"Choose between one and {limit} AudioHero preview URLs from this task's browser session.");
        List<AudioPreviewRequest> previews = [];
        foreach (JsonElement item in samples.EnumerateArray())
        {
            string url = item.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
            string label = item.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? string.Empty : string.Empty;
            if (!BrowserSafety.IsWebUrl(url) || !knownUrls.Contains(url, StringComparer.Ordinal)) throw new AmpleException("AMP5458", "Each preview URL must be an audio response observed by this task's visible browser.");
            previews.Add(new(url, label.Trim().Length is > 0 and <= 120 ? label.Trim() : "AudioHero preview"));
        }

        return previews;
    }

    internal static int RequestedLimit(IEnumerable<string> userMessages)
    {
        int requested = 10;
        foreach (string message in userMessages)
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(message, @"(?i)\b(\d{1,2})\s+(?:(?:audiohero|guitar|mp3|preview)\s+){0,3}(?:samples?|previews?|tracks?)\b"))
        {
            if (int.TryParse(match.Groups[1].Value, out int count) && count > requested) requested = Math.Min(count, 50);
        }
        return requested;
    }

    internal static bool IsMp3(string mediaType, string path, ReadOnlySpan<byte> bytes) => mediaType is "audio/mpeg" or "audio/mp3" || Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) && (bytes.StartsWith("ID3"u8) || bytes.Length > 1 && bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0);
}
