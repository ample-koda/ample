namespace Ample.Audio.Engine;

/// <summary>
/// An integrated loudness estimate in LUFS (ITU-R BS.1770): K-weighting, 400 ms blocks with 75 percent overlap, an absolute gate at -70 LUFS and a relative gate 10 LU below.
/// It is a measurement for people to read (the build output, the master check), not part of the integer render, so it uses floating point. Channels are weighted equally.
/// </summary>
public static class Loudness
{
    /// <summary>Gets the integrated loudness of mono samples, or null when the sound is too short or all below the gate.</summary>
    public static double? IntegratedLufs(short[] samples, int sampleRate) => IntegratedLufs([samples], sampleRate);

    /// <summary>Gets the integrated loudness of one or more channels.</summary>
    public static double? IntegratedLufs(IReadOnlyList<short[]> channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(channels);
        int block = sampleRate * 400 / 1000, hop = sampleRate * 100 / 1000;
        int length = channels[0].Length;
        if (length < block)
        {
            return null;
        }

        double[][] weighted = [.. channels.Select(c => KWeight(c, sampleRate))];
        List<double> energies = [];
        for (int start = 0; start + block <= length; start += hop)
        {
            double sum = 0;
            foreach (double[] channel in weighted)
            {
                double energy = 0;
                for (int i = start; i < start + block; i++)
                {
                    energy += channel[i] * channel[i];
                }

                sum += energy / block;
            }

            energies.Add(sum);
        }

        double Lufs(double z) => -0.691 + (10 * Math.Log10(z));
        List<double> aboveAbsolute = [.. energies.Where(z => z > 0 && Lufs(z) > -70)];
        if (aboveAbsolute.Count == 0)
        {
            return null;
        }

        double relativeGate = Lufs(aboveAbsolute.Average()) - 10;
        List<double> gated = [.. aboveAbsolute.Where(z => Lufs(z) > relativeGate)];
        return gated.Count == 0 ? null : Lufs(gated.Average());
    }

    private static double[] KWeight(short[] x, int rate)
    {
        // stage 1: the high shelf of the head, stage 2: a high-pass at 38 Hz (coefficients derived for the sample rate, as in libebur128)
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / rate), vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + (k / q) + (k * k);
        double[] shelfB = [(vh + (vb * k / q) + (k * k)) / a0, 2 * ((k * k) - vh) / a0, (vh - (vb * k / q) + (k * k)) / a0];
        double[] shelfA = [1, 2 * ((k * k) - 1) / a0, (1 - (k / q) + (k * k)) / a0];
        double f1 = 38.13547087602444, q1 = 0.5003270373238773;
        double k1 = Math.Tan(Math.PI * f1 / rate);
        double d = 1 + (k1 / q1) + (k1 * k1);
        double[] highB = [1, -2, 1];
        double[] highA = [1, 2 * ((k1 * k1) - 1) / d, (1 - (k1 / q1) + (k1 * k1)) / d];
        return Biquad(Biquad([.. x.Select(v => v / 32768d)], shelfB, shelfA), highB, highA);
    }

    private static double[] Biquad(double[] x, double[] b, double[] a)
    {
        double[] y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = (b[0] * x[i]) + (b[1] * x1) + (b[2] * x2) - (a[1] * y1) - (a[2] * y2);
            x2 = x1;
            x1 = x[i];
            y2 = y1;
            y1 = v;
            y[i] = v;
        }

        return y;
    }
}
