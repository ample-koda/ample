using System.Collections.Frozen;
using System.Text.Json;

namespace Ample.Speech;

/// <summary>Maps phones to bounded, pre-calibrated Humanico targets and segment timings.</summary>
public static class HumanicoCalibratedSegments
{
    public const string CalibrationSha256 = "C49A1E0B3A8E18668678074CC2AFDD74C50C4C003CC98447359F011A1616D71F";
    private static readonly FrozenDictionary<(string Phone, string Following), Target[]> Targets = Load();

    public static IReadOnlyList<HumanicoTargetSegment> Plan(
        IReadOnlyList<SpeechPhone> phones, int maxSegments, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(phones);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSegments);
        token.ThrowIfCancellationRequested();
        List<HumanicoTargetSegment> result = [];
        for (int i = 0; i < phones.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            SpeechPhone phone = phones[i];
            string following = i + 1 < phones.Count && HumanicoFrontEnd.IsVowel(phones[i + 1].Symbol)
                ? phones[i + 1].Symbol : string.Empty;
            if (!Targets.TryGetValue((phone.Symbol, following), out Target[]? targets)
                && !Targets.TryGetValue((phone.Symbol, string.Empty), out targets))
                throw new ArgumentException($"Unsupported Humanico phone '{phone.Symbol}'.", nameof(phones));
            IReadOnlyList<SpeechSegment> timing = HumanicoReferenceSegments.Plan([phone], token);
            if (timing.Count != targets.Length) throw new InvalidOperationException("Humanico calibration does not match segment timing.");
            if (maxSegments > 0 && (long)result.Count + timing.Count > maxSegments)
                throw new SpeechResourceLimitException("Humanico segment plan exceeds the configured segment limit.");
            for (int j = 0; j < timing.Count; j++)
            {
                if (timing[j].Kind != targets[j].Kind) throw new InvalidOperationException("Humanico calibration kind mismatch.");
                result.Add(new(timing[j], targets[j].Values));
            }
        }
        return result;
    }

    private static FrozenDictionary<(string Phone, string Following), Target[]> Load()
    {
        using Stream stream = typeof(HumanicoCalibratedSegments).Assembly.GetManifestResourceStream(
            "Ample.Speech.Data.humanico-calibration.json") ?? throw new InvalidOperationException("Humanico calibration is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        Dictionary<(string, string), Target[]> result = [];
        foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string phone = item.GetProperty("phone").GetString()!;
            string following = item.TryGetProperty("following", out JsonElement next) ? next.GetString()! : string.Empty;
            Target[] segments = [.. item.GetProperty("segments").EnumerateArray().Select(segment => new Target(
                segment.GetProperty("kind").GetString()!, segment.GetProperty("target").EnumerateObject()
                    .ToFrozenDictionary(property => property.Name, property => property.Value.GetDouble(), StringComparer.Ordinal)))];
            result.Add((phone, following), segments);
        }
        return result.ToFrozenDictionary();
    }

    private sealed record Target(string Kind, FrozenDictionary<string, double> Values);
}
