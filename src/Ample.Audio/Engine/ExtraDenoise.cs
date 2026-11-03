using System.Numerics;

namespace Ample.Audio.Engine;

/// <summary>Spectral noise reduction: the noise floor of every frequency band is estimated (from the quietest frames, or from the start of the sound) and taken away.</summary>
internal sealed class DenoiseRenderer : ExtraEffect
{
    private const int N = 2048, Hop = 512;

    public override string BlockType => "denoise";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        double reduction = Math.Clamp(Num(r, "reduction", 12), 0, 40);
        double alpha = 1 + (Math.Clamp(Num(r, "sensitivity", 50), 0, 100) / 50);
        bool fromStart = Text(r, "noise") == "start";
        string optimize = Text(r, "optimize") is "memory" or "speed" ? Text(r, "optimize") : "auto";   // only changes which pass redoes the FFT, never the sound (see Clean)
        int learnFrames = Math.Max(1, (int)(Num(r, "learn", 0.5) * rate / Hop));
        int learnFrom = Math.Max(0, (int)(Num(r, "learn_from", 0) * rate / Hop));   // where the stretch that holds only noise starts
        short[] l = Clean(left, reduction, alpha, fromStart, learnFrom, learnFrames, optimize, cancellationToken);
        short[] rr = stereo ? Clean(right, reduction, alpha, fromStart, learnFrom, learnFrames, optimize, cancellationToken) : l;
        return Out(Encode(l, rr, rate, stereo));
    }

    // Above this many cached spectrum bytes, "auto" falls back to rescanning instead of caching: a typical short preview or voice take stays well
    // under it and gets the speed win for free, while a long song does not pay for a spectrum cache sized to its whole length.
    private const long AutoSpectrumCacheBytes = 64L * 1024 * 1024;

    private static short[] Clean(short[] x, double reductionDb, double alpha, bool fromStart, int learnFrom, int learnFrames, string optimize, CancellationToken token)
    {
        int length = x.Length;
        if (length < N)
        {
            return x;
        }

        double[] window = new double[N];
        for (int i = 0; i < N; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / N));
        }

        int frames = ((length + N - 1) / Hop) + 1;
        int bins = (N / 2) + 1;
        Complex[] buffer = new Complex[N];
        double[] noise = new double[bins];
        int first = Math.Min(learnFrom, frames - 1);
        int last = Math.Min(frames, first + learnFrames);
        // Finding the noise floor over the whole sound (noise auto, below) already does one full-song FFT pass. The cleaning pass further down
        // needs the complex spectrum again (magnitude alone cannot be inverse-transformed), not just its magnitude. "memory" recomputes that
        // spectrum with a second full-song FFT pass there (the lower-memory default); "speed" keeps every frame's spectrum from this pass
        // instead (bins*frames Complex values, four times the size of the float magnitudes below) so the cleaning pass only copies it back in;
        // "auto" picks speed when that cache would stay under AutoSpectrumCacheBytes, memory otherwise.
        // All three produce byte-identical output: the same deterministic FFT run on the same input either way.
        float[]? mags = null;
        Complex[]? spectra = null;
        if (fromStart)
        {
            for (int f = first; f < last; f++)
            {
                token.ThrowIfCancellationRequested();
                FillFrame(x, window, buffer, f);
                Dsp.Fft(buffer, false);
                for (int k = 0; k < bins; k++) noise[k] += (float)buffer[k].Magnitude;
            }

            for (int k = 0; k < bins; k++) noise[k] /= Math.Max(1, last - first);
        }
        else
        {
            bool cacheSpectrum = optimize switch
            {
                "speed" => true,
                "memory" => false,
                _ => (long)frames * bins * 16 <= AutoSpectrumCacheBytes,
            };
            if (cacheSpectrum) spectra = new Complex[frames * bins]; else mags = new float[frames * bins];
            for (int f = 0; f < frames; f++)
            {
                token.ThrowIfCancellationRequested();
                FillFrame(x, window, buffer, f);
                Dsp.Fft(buffer, false);
                int offset = f * bins;
                for (int k = 0; k < bins; k++)
                {
                    if (spectra != null) spectra[offset + k] = buffer[k];
                    else mags![offset + k] = (float)buffer[k].Magnitude;
                }
            }

            float[] column = new float[frames];
            int rank = (int)(frames * 0.15);
            for (int k = 0; k < bins; k++)
            {
                token.ThrowIfCancellationRequested();
                for (int f = 0; f < frames; f++) column[f] = spectra != null ? (float)spectra[(f * bins) + k].Magnitude : mags![(f * bins) + k];
                noise[k] = SelectOrderStatistic(column, frames, rank, token);
            }
        }

        double floor = Dsp.DbToLinear(-reductionDb);
        double[] output = new double[length + (2 * N)];
        double[] previous = new double[bins];
        Array.Fill(previous, 1.0);
        for (int f = 0; f < frames; f++)
        {
            token.ThrowIfCancellationRequested();
            int offset = f * bins;
            if (spectra != null)
            {
                // the mirror assignment below fills buffer[bins..N) from these same bins, so copying only [0, bins) is enough
                Array.Copy(spectra, offset, buffer, 0, bins);
            }
            else
            {
                FillFrame(x, window, buffer, f);
                Dsp.Fft(buffer, false);
            }

            for (int k = 0; k < bins; k++)
            {
                double m = mags != null ? mags[offset + k] : buffer[k].Magnitude;
                double g = m <= 1e-9 ? floor : Math.Max(floor, 1 - (alpha * noise[k] / m));
                g = Math.Max(g, previous[k] * 0.85);   // let the gain fall slowly, so the cleaned noise does not flutter
                previous[k] = g;
                buffer[k] *= g;
                if (k > 0 && k < N / 2)
                {
                    buffer[N - k] = Complex.Conjugate(buffer[k]);
                }
            }

            Dsp.Fft(buffer, true);
            int at = (f * Hop) - N + Hop;
            for (int i = 0; i < N; i++)
            {
                int p = at + i;
                if (p >= 0 && p < length)
                {
                    output[p] += buffer[i].Real * window[i];
                }
            }
        }

        short[] y = new short[length];
        for (int i = 0; i < length; i++)
        {
            y[i] = FixedAudio.Clip((long)Math.Round(output[i] / 1.5));   // Hann analysis and synthesis at 75 % overlap sum to 1.5
        }

        return y;
    }

    private static void FillFrame(short[] samples, double[] window, Complex[] buffer, int frame)
    {
        int at = (frame * Hop) - N + Hop;
        for (int i = 0; i < N; i++)
        {
            int p = at + i;
            buffer[i] = p >= 0 && p < samples.Length ? samples[p] * window[i] : 0;
        }
    }

    private static float SelectOrderStatistic(float[] values, int count, int rank, CancellationToken token)
    {
        int left = 0, right = count - 1, depth = 2 * BitOperations.Log2((uint)count);
        while (left < right)
        {
            token.ThrowIfCancellationRequested();
            if (depth-- == 0)
            {
                Array.Sort(values, left, right - left + 1);
                return values[rank];
            }

            int middle = left + ((right - left) / 2);
            float pivot = Median(values[left], values[middle], values[right]);
            int lower = left, current = left, upper = right;
            while (current <= upper)
            {
                if (values[current] < pivot)
                {
                    (values[lower], values[current]) = (values[current], values[lower]);
                    lower++;
                    current++;
                }
                else if (values[current] > pivot)
                {
                    (values[current], values[upper]) = (values[upper], values[current]);
                    upper--;
                }
                else current++;
            }

            if (rank < lower) right = lower - 1;
            else if (rank > upper) left = upper + 1;
            else return values[rank];
        }

        return values[rank];
    }

    private static float Median(float a, float b, float c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }
}
