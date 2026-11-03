using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>Where a sample can loop: sample frames at the build rate, a crossfade in frames, and how well the two ends match (1 is perfect).</summary>
/// <param name="Start">First frame of the loop.</param>
/// <param name="End">Frame after the last frame of the loop.</param>
/// <param name="Crossfade">Crossfade length in frames.</param>
/// <param name="Quality">Normalised match of the waveform before the loop end and before the loop start, 0 to 1.</param>
public sealed record LoopPoints(int Start, int End, int Crossfade, double Quality);

/// <summary>Helpers the assistant's sample-set tool uses: decode any audio file to WAV bytes, find loop points for a sample, and merge them into a set's <c>set.json</c>.</summary>
public static class SampleSetTools
{
    private const int BuildRate = 44100;

    /// <summary>The audio file as WAV bytes (a WAV is returned as it is, other formats are decoded with ffmpeg).</summary>
    public static byte[] ReadAsWav(string path) => AudioFile.ReadWav(path);

    /// <summary>Finds loop points for a WAV (mono mix of its first channel at the build rate), or null when the sample is too short or no loop matches at all.</summary>
    public static LoopPoints? FindLoop(byte[] wav) => FindLoop(SampleSet.ReadWav(wav, BuildRate), BuildRate);

    /// <summary>
    /// Deterministic loop search: the loop starts after the attack (20 to 50 percent of the sample) and ends in the tail (55 to 95 percent). A candidate pair scores the normalised
    /// correlation of the ~10 ms of audio before the end with the ~10 ms before the start, times how alike their energy is; the best pair wins and both points move to the nearest
    /// rising zero crossing. A sample under a quarter of a second, or a loop under a tenth of a second, is not looped.
    /// </summary>
    public static LoopPoints? FindLoop(short[] samples, int rate)
    {
        int n = samples.Length;
        if (n < rate / 4) return null;
        int window = Math.Clamp(rate / 100, 128, 1024);
        int startLow = Math.Max(window, (int)(n * 0.2)), startHigh = (int)(n * 0.5), endLow = (int)(n * 0.55), endHigh = (int)(n * 0.95);
        int stepStart = Math.Max(window / 4, (startHigh - startLow) / 300), stepEnd = Math.Max(window / 4, (endHigh - endLow) / 400);
        int minLoop = rate / 10;
        double best = double.NegativeInfinity;
        int bestStart = -1, bestEnd = -1;
        for (int s = startLow; s <= startHigh; s += stepStart)
        {
            for (int e = endLow; e <= endHigh && e <= n; e += stepEnd)
            {
                if (e - s < minLoop) continue;
                double score = Match(samples, s - window, e - window, window);
                if (score > best) { best = score; bestStart = s; bestEnd = e; }
            }
        }

        if (bestStart < 0 || best <= 0) return null;
        int start = RisingZero(samples, bestStart, window / 2), end = RisingZero(samples, bestEnd, window / 2);
        if (end - start < minLoop) return null;
        int crossfade = Math.Clamp(rate / 200, 1, (end - start) / 4);
        return new LoopPoints(start, end, crossfade, Math.Round(Math.Min(1, best), 3));
    }

    // normalised correlation of two windows, scaled by how similar their energies are
    private static double Match(short[] x, int a, int b, int length)
    {
        double sumAB = 0, sumAA = 0, sumBB = 0;
        for (int i = 0; i < length; i++)
        {
            double va = x[a + i], vb = x[b + i];
            sumAB += va * vb; sumAA += va * va; sumBB += vb * vb;
        }

        if (sumAA <= 0 || sumBB <= 0) return 0;
        double correlation = sumAB / Math.Sqrt(sumAA * sumBB), energy = Math.Min(sumAA, sumBB) / Math.Max(sumAA, sumBB);
        return correlation * Math.Sqrt(energy);
    }

    // the nearest frame (within reach) where the wave crosses zero going up; the start itself when there is none
    private static int RisingZero(short[] x, int at, int reach)
    {
        for (int d = 0; d <= reach; d++)
        {
            foreach (int i in new[] { at + d, at - d })
            {
                if (i > 0 && i < x.Length && x[i - 1] <= 0 && x[i] > 0) return i;
            }
        }

        return at;
    }

    /// <summary>Merges a loop and/or an envelope into the text of a set's <c>set.json</c> (empty text starts a new one); other keys stay as they are.</summary>
    /// <param name="existing">The current text of <c>set.json</c>, or empty.</param>
    /// <param name="baseName">The sample's base name (lower case, without a layer or round-robin suffix).</param>
    /// <param name="loop">Loop points to write for that name, or null.</param>
    /// <param name="envelope">An ADSR object <c>{ attack, hold, decay, sustain, release }</c> to write for the whole set, or null.</param>
    public static string MergeSettings(string existing, string baseName, LoopPoints? loop, JsonElement? envelope)
    {
        JsonObject root = string.IsNullOrWhiteSpace(existing) ? [] : JsonNode.Parse(existing) as JsonObject ?? [];
        if (loop is not null)
        {
            JsonObject loops = root["loops"] as JsonObject ?? [];
            loops[baseName.ToLowerInvariant()] = new JsonObject { ["start"] = loop.Start, ["end"] = loop.End, ["crossfade"] = loop.Crossfade };
            root["loops"] = loops;
        }

        if (envelope is { ValueKind: JsonValueKind.Object } env)
        {
            JsonObject shaped = [];
            foreach (string key in new[] { "attack", "hold", "decay", "sustain", "release" })
            {
                if (env.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                {
                    shaped[key] = key == "sustain" ? Math.Clamp(number, 0, 100) : Math.Clamp(number, 0, 20000);
                }
            }

            if (shaped.Count > 0) root["envelope"] = shaped;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
