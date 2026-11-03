using System.Globalization;

namespace Ample.Audio.Engine;

/// <summary>What the analysis of a recording suggests for a <c>follow</c> block.</summary>
/// <param name="Band">The band where the passes stand out most against the steady background (low, mid or high).</param>
/// <param name="BaselineSeconds">How long the average of the background is.</param>
/// <param name="AttackMilliseconds">How fast the control rises.</param>
/// <param name="ReleaseMilliseconds">How fast the control falls.</param>
/// <param name="Prominence">How much the chosen band stands out (per mille: the part of the loudest moments that is above the background); 0 for a flat recording.</param>
public sealed record FollowSuggestion(string Band, int BaselineSeconds, int AttackMilliseconds, int ReleaseMilliseconds, int Prominence)
{
    /// <summary>The block lines that write the suggestion down (so the song no longer depends on the analysis).</summary>
    public string ToBlockLines() => string.Create(CultureInfo.InvariantCulture, $"  band {Band}\n  baseline on\n  baseline_window {BaselineSeconds} s\n  attack {AttackMilliseconds} ms\n  release {ReleaseMilliseconds} ms\n");
}

/// <summary>
/// Chooses the settings of a <c>follow</c> block from a recording: it looks at the low, mid and high band, takes the one whose loud moments stand out most from its own background
/// (a car in the low band against the hiss of rain in the high band), and sets the times from how long such a moment lasts. Integer arithmetic only: the same recording gives the same answer everywhere.
/// </summary>
public static class FollowAnalysis
{
    private static readonly string[] Bands = ["low", "mid", "high"];

    /// <summary>Suggests settings for a recording in a .wav file (mono or stereo).</summary>
    /// <param name="wav">The bytes of the file.</param>
    public static FollowSuggestion SuggestFromWav(byte[] wav)
    {
        (StereoBuffer both, int rate) = ChannelAudio.Decode(wav);
        int[] mono = new int[both.Left.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = (both.Left[i] + both.Right[i]) / 2;
        return Suggest(mono, rate);
    }

    /// <summary>Suggests settings for a recording.</summary>
    /// <param name="mono">The recording as one channel.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    public static FollowSuggestion Suggest(int[] mono, int rate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mono);
        int count = (int)(((long)mono.Length * ControlCurve.Rate / rate) + 1);
        string best = "low";
        int bestScore = -1;
        long[]? bestSeries = null;
        foreach (string band in Bands)
        {
            long[] series = ControlBlocks.Envelope(ControlBlocks.BandOf(mono, rate, band), rate, 30, 250, count, cancellationToken);
            int score = Prominence(series);
            if (score > bestScore)
            {
                (best, bestScore, bestSeries) = (band, score, series);
            }
        }

        // how long does a moment last that stands out? (the median length of the stretches above the middle of the background and the loud end)
        int duration = bestSeries is null ? 1000 : MedianExcursion(bestSeries);
        int baseline = Math.Clamp((duration * 5 / 1000) + 1, 3, 30);
        int attack = Math.Clamp(duration / 8, 20, 300);
        int release = Math.Clamp(duration / 3, 150, 2000);
        return new FollowSuggestion(best, baseline, attack, release, Math.Max(0, bestScore));
    }

    /// <summary>How much the loud end of a series stands out from its middle, per mille of the loud end ((p95 - median) / p95).</summary>
    private static int Prominence(long[] series)
    {
        if (series.Length == 0) return 0;
        long[] sorted = [.. series];
        Array.Sort(sorted);
        long median = sorted[sorted.Length / 2];
        long top = sorted[Math.Min(sorted.Length - 1, (int)((long)sorted.Length * 95 / 100))];
        return top <= 0 ? 0 : (int)Math.Clamp((top - median) * 1000 / top, 0, 1000);
    }

    private static int MedianExcursion(long[] series)
    {
        long[] sorted = [.. series];
        Array.Sort(sorted);
        long median = sorted[sorted.Length / 2];
        long top = sorted[Math.Min(sorted.Length - 1, (int)((long)sorted.Length * 95 / 100))];
        long level = median + ((top - median) / 2);
        List<int> lengths = [];
        int run = 0;
        foreach (long v in series)
        {
            if (v > level && top > median) run++;
            else if (run > 0)
            {
                lengths.Add(run);
                run = 0;
            }
        }

        if (run > 0) lengths.Add(run);
        if (lengths.Count == 0) return 1000;
        lengths.Sort();
        return lengths[lengths.Count / 2];   // one value per millisecond: the count is milliseconds
    }
}
