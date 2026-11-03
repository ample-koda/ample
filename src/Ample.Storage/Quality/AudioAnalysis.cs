using System.Buffers.Binary;
using System.Text;

namespace Ample.Storage.Quality;

/// <summary>What was measured on one audio take.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Seconds">Length.</param>
/// <param name="PeakDb">Sample peak in dBFS.</param>
/// <param name="TruePeakDb">Reconstructed peak (4x oversampled) in dBTP.</param>
/// <param name="ClippedRuns">How many runs of three or more full-scale samples.</param>
/// <param name="DcOffset">Mean value, -1..1.</param>
/// <param name="Lufs">Integrated loudness (ITU-R BS.1770-4, gated), or null when the take is too short or silent.</param>
/// <param name="LongestSilenceSeconds">The longest quiet stretch between sound (lead-in and tail excluded).</param>
public sealed record AudioMetrics(int SampleRate, double Seconds, double PeakDb, double TruePeakDb, int ClippedRuns, double DcOffset, double? Lufs, double LongestSilenceSeconds);

/// <summary>Loudness after EBU R128 (ITU-R BS.1770-4 with the loudness range of EBU Tech 3342).</summary>
/// <param name="IntegratedLufs">Gated programme loudness, or null when too short or silent.</param>
/// <param name="MomentaryMaxLufs">Highest 400 ms loudness.</param>
/// <param name="ShortTermMaxLufs">Highest 3 s loudness.</param>
/// <param name="LoudnessRange">Loudness range in LU (10th to 95th percentile of the gated short-term values), or null when shorter than 3 s.</param>
/// <param name="TruePeakDb">Reconstructed peak in dBTP.</param>
public sealed record LoudnessProfile(double? IntegratedLufs, double? MomentaryMaxLufs, double? ShortTermMaxLufs, double? LoudnessRange, double TruePeakDb);

/// <summary>Plain audio measurements on mono samples, written for clarity: the same numbers on every machine up to floating point rounding.</summary>
public static class AudioAnalysis
{
    /// <summary>Reads 16-bit PCM WAV (1 or 2 channels, mixed down to mono) as floats in -1..1.</summary>
    /// <exception cref="InvalidDataException">The data is not 16-bit PCM WAV.</exception>
    public static (float[] Samples, int SampleRate) ReadWav(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes[..4]) != "RIFF" || Encoding.ASCII.GetString(bytes.Slice(8, 4)) != "WAVE")
        {
            throw new InvalidDataException("This is not a WAV file.");
        }

        int pos = 12;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        int format = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes.Slice(pos, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 4)..]);
            int body = pos + 8;
            if (id == "fmt " && body + 16 <= bytes.Length)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(bytes[body..]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes[(body + 14)..]);
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16 || channels is < 1 or > 2 || rate <= 0)
                {
                    throw new InvalidDataException("Only 16-bit PCM WAV with one or two channels can be measured.");
                }

                int available = Math.Min(size < 0 ? int.MaxValue : size, bytes.Length - body);
                int frames = available / (2 * channels);
                float[] samples = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BinaryPrimitives.ReadInt16LittleEndian(bytes[(body + ((i * channels) + c) * 2)..]) / 32768f;
                    }

                    samples[i] = sum / channels;
                }

                return (samples, rate);
            }

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException("The WAV file has no audio data.");
    }

    /// <summary>Measures samples.</summary>
    public static AudioMetrics Measure(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double seconds = samples.Length / (double)sampleRate;
        double peak = 0;
        double sum = 0;
        int clipped = 0;
        int run = 0;
        foreach (float s in samples)
        {
            double a = Math.Abs(s);
            peak = Math.Max(peak, a);
            sum += s;
            if (a >= 0.9999)
            {
                run++;
                if (run == 3)
                {
                    clipped++;
                }
            }
            else
            {
                run = 0;
            }
        }

        double dc = samples.Length == 0 ? 0 : sum / samples.Length;
        return new AudioMetrics(sampleRate, seconds, Db(peak), Db(TruePeak(samples)), clipped, dc, Lufs(samples, sampleRate), LongestSilence(samples, sampleRate));
    }

    /// <summary>The full EBU R128 profile of mono samples.</summary>
    public static LoudnessProfile Profile(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double[] w = KWeight(samples, sampleRate);
        double[] momentary = BlockLoudness(w, (int)(0.4 * sampleRate), (int)(0.1 * sampleRate));
        double[] shortTerm = BlockLoudness(w, 3 * sampleRate, sampleRate);
        double? range = null;
        if (shortTerm.Length > 0)
        {
            double[] gated = [.. shortTerm.Where(l => l > -70)];
            if (gated.Length > 0)
            {
                double relative = LoudnessOfMeanEnergy(gated) - 20;
                double[] kept = [.. gated.Where(l => l > relative).Order()];
                if (kept.Length > 0)
                {
                    range = kept[(int)Math.Min(kept.Length - 1, Math.Floor(0.95 * (kept.Length - 1) + 0.5))] - kept[(int)Math.Floor(0.10 * (kept.Length - 1) + 0.5)];
                }
            }
        }

        return new LoudnessProfile(Lufs(samples, sampleRate), momentary.Length == 0 ? null : momentary.Max(), shortTerm.Length == 0 ? null : shortTerm.Max(), range, Db(TruePeak(samples)));
    }

    private static double[] BlockLoudness(double[] w, int block, int step)
    {
        if (block <= 0 || step <= 0 || w.Length < block)
        {
            return [];
        }

        List<double> values = [];
        for (int start = 0; start + block <= w.Length; start += step)
        {
            double sum = 0;
            for (int i = start; i < start + block; i++)
            {
                sum += w[i] * w[i];
            }

            values.Add(-0.691 + (10 * Math.Log10(Math.Max(sum / block, 1e-20))));
        }

        return [.. values];
    }

    private static double LoudnessOfMeanEnergy(double[] loudness) => -0.691 + (10 * Math.Log10(Math.Max(loudness.Average(l => Math.Pow(10, (l + 0.691) / 10)), 1e-20)));

    /// <summary>The decibel value of a linear amplitude (-infinity is reported as -200).</summary>
    public static double Db(double amplitude) => amplitude <= 1e-10 ? -200 : 20 * Math.Log10(amplitude);

    private static double TruePeak(float[] x)
    {
        // 4x oversampling with a Catmull-Rom cubic between neighbours: a simple, honest estimate of the reconstructed peak.
        double peak = 0;
        for (int i = 0; i < x.Length - 1; i++)
        {
            double p0 = x[Math.Max(0, i - 1)];
            double p1 = x[i];
            double p2 = x[i + 1];
            double p3 = x[Math.Min(x.Length - 1, i + 2)];
            for (int k = 0; k < 4; k++)
            {
                double t = k / 4.0;
                double v = 0.5 * ((2 * p1) + ((-p0 + p2) * t) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * t * t) + (((-p0) + (3 * p1) - (3 * p2) + p3) * t * t * t));
                peak = Math.Max(peak, Math.Abs(v));
            }
        }

        return Math.Max(peak, x.Length == 0 ? 0 : Math.Abs(x[^1]));
    }

    /// <summary>Integrated loudness after ITU-R BS.1770-4 for one channel: K-weighting, 400 ms blocks every 100 ms, absolute gate -70 LUFS, relative gate -10 LU.</summary>
    public static double? Lufs(float[] x, int rate)
    {
        int block = (int)(0.4 * rate);
        int step = (int)(0.1 * rate);
        if (x.Length < block || block <= 0 || step <= 0)
        {
            return null;
        }

        double[] w = KWeight(x, rate);
        List<double> energies = [];
        for (int start = 0; start + block <= w.Length; start += step)
        {
            double sum = 0;
            for (int i = start; i < start + block; i++)
            {
                sum += w[i] * w[i];
            }

            energies.Add(sum / block);
        }

        double Loud(double e) => -0.691 + (10 * Math.Log10(Math.Max(e, 1e-20)));
        List<double> above = [.. energies.Where(e => Loud(e) > -70)];
        if (above.Count == 0)
        {
            return null;
        }

        double relative = Loud(above.Average()) - 10;
        List<double> gated = [.. above.Where(e => Loud(e) > relative)];
        return gated.Count == 0 ? null : Loud(gated.Average());
    }

    private static double[] KWeight(float[] x, int rate)
    {
        // The two filters of BS.1770 (a high shelf and a high pass), with the coefficients derived for any sample rate.
        double[] y = Biquad(x.Select(v => (double)v).ToArray(), Shelf(rate));
        return Biquad(y, HighPass(rate));
    }

    private static (double B0, double B1, double B2, double A1, double A2) Shelf(int fs)
    {
        const double F0 = 1681.974450955533;
        const double G = 3.999843853973347;
        const double Q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * F0 / fs);
        double vh = Math.Pow(10, G / 20);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + (k / Q) + (k * k);
        return ((vh + (vb * k / Q) + (k * k)) / a0, 2 * ((k * k) - vh) / a0, (vh - (vb * k / Q) + (k * k)) / a0, 2 * ((k * k) - 1) / a0, (1 - (k / Q) + (k * k)) / a0);
    }

    private static (double B0, double B1, double B2, double A1, double A2) HighPass(int fs)
    {
        const double F0 = 38.13547087602444;
        const double Q = 0.5003270373238773;
        double k = Math.Tan(Math.PI * F0 / fs);
        double a0 = 1 + (k / Q) + (k * k);
        return (1, -2, 1, 2 * ((k * k) - 1) / a0, (1 - (k / Q) + (k * k)) / a0);
    }

    private static double[] Biquad(double[] x, (double B0, double B1, double B2, double A1, double A2) c)
    {
        double[] y = new double[x.Length];
        double z1 = 0;
        double z2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = (c.B0 * x[i]) + z1;
            z1 = (c.B1 * x[i]) - (c.A1 * v) + z2;
            z2 = (c.B2 * x[i]) - (c.A2 * v);
            y[i] = v;
        }

        return y;
    }

    private static double LongestSilence(float[] x, int rate)
    {
        // 50 ms windows below -60 dBFS RMS count as silence; the stretches before the first and after the last sound do not count.
        int window = Math.Max(1, rate / 20);
        List<bool> quiet = [];
        for (int start = 0; start + window <= x.Length; start += window)
        {
            double sum = 0;
            for (int i = start; i < start + window; i++)
            {
                sum += x[i] * x[i];
            }

            quiet.Add(Db(Math.Sqrt(sum / window)) < -60);
        }

        int first = quiet.IndexOf(false);
        int last = quiet.LastIndexOf(false);
        if (first < 0)
        {
            return 0;
        }

        int longest = 0;
        int current = 0;
        for (int i = first; i <= last; i++)
        {
            current = quiet[i] ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest * window / (double)rate;
    }
}
