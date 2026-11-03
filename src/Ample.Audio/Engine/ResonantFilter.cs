namespace Ample.Audio.Engine;

/// <summary>The filter with resonance: a two-pole state-variable filter (topology-preserving transform) with a cutoff per sample.</summary>
internal static class ResonantFilter
{
    /// <summary>Gets the linear Q of a resonance in percent: 0 is 1, 100 is a peak of about 18 dB at the cutoff.</summary>
    /// <param name="percent">Resonance, 0 to 100.</param>
    internal static double QOf(double percent) => Math.Pow(10, Math.Clamp(percent, 0, 100) * 0.18 / 20);

    /// <summary>Filters one channel.</summary>
    /// <param name="x">The samples.</param>
    /// <param name="cutoffs">The cutoff in Hz for every sample.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="highpass">True for a high-pass, false for a low-pass.</param>
    /// <param name="resonance">Resonance in percent.</param>
    internal static int[] Process(int[] x, int[] cutoffs, int rate, bool highpass, double resonance) => Process(x, cutoffs, rate, highpass ? HighPass : LowPass, resonance, null);

    /// <summary>The filter shape: low-pass.</summary>
    internal const int LowPass = 0;

    /// <summary>The filter shape: high-pass.</summary>
    internal const int HighPass = 1;

    /// <summary>The filter shape: band-pass (unity gain at the cutoff).</summary>
    internal const int BandPass = 2;

    /// <summary>The filter shape: notch.</summary>
    internal const int Notch = 3;

    /// <summary>Gets the shape of a filter type word (lowpass is the default).</summary>
    /// <param name="type">The type parameter.</param>
    internal static int ShapeOf(string type) => type switch { "highpass" => HighPass, "bandpass" => BandPass, "notch" => Notch, _ => LowPass };

    /// <summary>Gets how many passes a slope word means: 12 is one, 24 two, 36 three, 48 four (anything else is one, the old behaviour).</summary>
    /// <param name="slope">The slope parameter, in dB per octave.</param>
    internal static int PassesOf(string slope) => slope.Trim() switch { "24" => 2, "36" => 3, "48" => 4, _ => 1 };

    /// <summary>Filters one channel in a shape, in one or more passes (two passes make 24 dB per octave, three 36, four 48).</summary>
    /// <param name="x">The samples.</param>
    /// <param name="cutoffs">The cutoff in Hz for every sample.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="shape">The shape (see <see cref="ShapeOf"/>).</param>
    /// <param name="resonance">Resonance in percent, used when <paramref name="resonances"/> is null.</param>
    /// <param name="resonances">The resonance in percent for every sample, or null.</param>
    /// <param name="passes">How many times the filter runs in a row.</param>
    internal static int[] Process(int[] x, int[] cutoffs, int rate, int shape, double resonance, int[]? resonances, int passes)
    {
        int[] y = Process(x, cutoffs, rate, shape, resonance, resonances);
        for (int p = 1; p < passes; p++) y = Process(y, cutoffs, rate, shape, resonance, resonances);
        return y;
    }

    /// <summary>Filters one channel with a resonance that may change over time.</summary>
    /// <param name="x">The samples.</param>
    /// <param name="cutoffs">The cutoff in Hz for every sample.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="highpass">True for a high-pass, false for a low-pass.</param>
    /// <param name="resonance">Resonance in percent, used when <paramref name="resonances"/> is null.</param>
    /// <param name="resonances">The resonance in percent for every sample, or null for the constant <paramref name="resonance"/>.</param>
    internal static int[] Process(int[] x, int[] cutoffs, int rate, bool highpass, double resonance, int[]? resonances) => Process(x, cutoffs, rate, highpass ? HighPass : LowPass, resonance, resonances);

    /// <summary>Filters one channel in one shape, one pass.</summary>
    /// <param name="x">The samples.</param>
    /// <param name="cutoffs">The cutoff in Hz for every sample.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="resonance">Resonance in percent, used when <paramref name="resonances"/> is null.</param>
    /// <param name="resonances">The resonance in percent for every sample, or null for the constant <paramref name="resonance"/>.</param>
    internal static int[] Process(int[] x, int[] cutoffs, int rate, int shape, double resonance, int[]? resonances)
    {
        int[] y = new int[x.Length];
        double k = 1 / QOf(resonance), ic1 = 0, ic2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (resonances is not null) k = 1 / QOf(resonances[i]);
            double g = Math.Tan(Math.PI * Math.Min(cutoffs[i], rate * 0.45) / rate);
            double a1 = 1 / (1 + (g * (g + k))), a2 = g * a1, a3 = g * a2;
            double v3 = x[i] - ic2, v1 = (a1 * ic1) + (a2 * v3), v2 = ic2 + (a2 * ic1) + (a3 * v3);
            ic1 = (2 * v1) - ic1;
            ic2 = (2 * v2) - ic2;
            double o = shape switch { HighPass => x[i] - (k * v1) - v2, BandPass => k * v1, Notch => x[i] - (k * v1), _ => v2 };
            y[i] = (int)Math.Clamp(Math.Round(o), int.MinValue / 2, int.MaxValue / 2);
        }

        return y;
    }
}
