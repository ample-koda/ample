namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_mlsa.c (mlsa_resynthesis, synthesis_body, init_vocoder, vocoder, plus_or_minus_one,
// mlsadf, mlsadf1, mlsadf2, mlsafir, mc2b, b2en, b2mc, freqt, c2ir).
// Translated to C# and modified: exp and log are CgMath, rand() is GlibcRandom; the Gaussian generator of the original, which only runs without mixed excitation, is not part of this port.
// Original: Tomoki Toda, Nagoya Institute of Technology, and Alan W Black, Carnegie Mellon University; the MLSA filter after the HTS of Nagoya Institute of Technology.
// Copyright and licence: LICENSES/flite.txt (Nagoya Institute of Technology, Carnegie Mellon University).

/// <summary>The MLSA vocoder with mixed excitation of the clustergen voices: for every 5 ms frame the F0, the smoothed mel-cepstrum and the five strengths make 80 samples
/// (a pulse train and noise, shaped by the five filters, through the MLSA filter). All arithmetic is double precision with the types and the order of the C expressions.</summary>
internal static class CgVocoder
{
    private const int PadeOrder = 5;
    private const int ImpulseLength = 64;

    /// <summary>The Pade approximants of the MLSA filter (<c>vs->pade</c>).</summary>
    private static readonly double[] Pade =
    [
        1.0,
        1.0, 0.0,
        1.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        1.0, 0.4999273, 0.1067005, 0.01170221, 0.0005656279,
        1.0, 0.4999391, 0.1107098, 0.01369984, 0.0009564853,
        0.00003041721
    ];

    private sealed class Setup(FliteCgVoiceData voice, int framel, int m, double rate, GlibcRandom random)
    {
        public int Fprd { get; } = framel;

        public int Iprd { get; } = 1;

        public double P1 { get; set; } = -1;

        public double Pc { get; set; }

        public double Rate { get; } = rate;

        // the coefficients: c (the current), cc (of this frame), cinc (the step of each sample) and the delay lines d1, one block in the original
        public double[] C { get; } = new double[(3 * (m + 1)) + (3 * (PadeOrder + 1)) + (PadeOrder * (m + 2))];

        public int CcAt { get; } = m + 1;

        public int CincAt { get; } = 2 * (m + 1);

        public int D1At { get; } = 3 * (m + 1);

        public double[] Mc { get; } = new double[(m + 1) + (2 * ImpulseLength)];

        public double[] FreqtD { get; } = new double[(2 * (ImpulseLength - 1)) + 2];

        public double[] Cep { get; } = new double[ImpulseLength];

        public double[] Ir { get; } = new double[ImpulseLength];

        public double[] HPulse { get; } = new double[voice.MixedExcitationOrder];

        public double[] HNoise { get; } = new double[voice.MixedExcitationOrder];

        public double[] XPulseSignal { get; } = new double[voice.MixedExcitationOrder];

        public double[] XNoiseSignal { get; } = new double[voice.MixedExcitationOrder];

        public GlibcRandom Random { get; } = random;

        public int PadeAt { get; set; }
    }

    /// <summary><c>mlsa_resynthesis</c>: the waveform of a smoothed track (<c>(frames - 1) * 80</c> samples: the first frame only starts the filter).</summary>
    /// <param name="smoothed">The smoothed track: F0 and 25 mel-cepstrum values for each frame.</param>
    /// <param name="strengths">The five mixed excitation strengths of each frame.</param>
    /// <param name="times">The time of each frame in seconds (the first two give the frame shift).</param>
    /// <param name="voice">The voice.</param>
    /// <param name="random">The generator of the noise (<c>rand()</c>).</param>
    /// <param name="cancellationToken">Stops the work between two frames.</param>
    public static short[] Synthesize(float[][] smoothed, float[][] strengths, float[] times, FliteCgVoiceData voice, GlibcRandom random, CancellationToken cancellationToken)
    {
        if (!voice.MixedExcitation) throw new InvalidDataException("Only voices with mixed excitation are supported.");
        double sampleRate = voice.SampleRate;
        double shift = smoothed.Length > 1 ? 1000.0 * (times[1] - times[0]) : 5.0;
        int m = CgMlpg.OutputColumns - 1;
        int framel = (int)(0.5 + (shift * sampleRate / 1000.0));
        Setup vs = new(voice, framel, m, sampleRate, random);
        short[] samples = new short[smoothed.Length * framel];
        double[] mcep = new double[m + 1];
        int pos = 0;
        for (int t = 0; t < smoothed.Length; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double f0 = smoothed[t][0];
            for (int i = 1; i < m + 1; i++) mcep[i - 1] = smoothed[t][i];
            mcep[m] = 0;
            Vocode(f0, mcep, strengths[t], m, voice, vs, samples, ref pos);
        }

        return pos == samples.Length ? samples : samples[..pos];
    }

    private static double PlusOrMinusOne(GlibcRandom random) => random.Next() > GlibcRandom.Max / 2.0 ? 1.0 : -1.0;

    /// <summary>(short)x as the processor does it: the double is cut to a 32 bit integer (the 'integer indefinite' value when it does not fit) and then to its low 16 bits.</summary>
    private static short ToShort(double x)
    {
        int whole = x >= -2147483648.0 && x < 2147483648.0 ? (int)x : int.MinValue;
        return unchecked((short)whole);
    }

    private static void Vocode(double p, double[] mc, float[] str, int m, FliteCgVoiceData voice, Setup vs, short[] wav, ref int pos)
    {
        double alpha = voice.MlsaAlpha;
        float gain = 1.0f;
        if (voice.Gain != 0.0) gain = voice.Gain;
        double[] c = vs.C;

        // mixed excitation: the filters of the pulse and of the noise for this frame
        for (int i = 0; i < voice.MixedExcitationOrder; i++)
        {
            vs.HPulse[i] = vs.HNoise[i] = 0.0;
            for (int j = 0; j < voice.MixedExcitationFilterCount; j++)
            {
                vs.HPulse[i] += str[j] * voice.MixedExcitationFilters[j][i];
                vs.HNoise[i] += (1 - str[j]) * voice.MixedExcitationFilters[j][i];
            }
        }

        if (p != 0.0) p = vs.Rate / p;   // F0 to pitch period
        if (vs.P1 < 0)
        {
            // the first frame: only the coefficients are set up
            vs.P1 = p;
            vs.Pc = vs.P1;
            Mc2b(mc, c, 0, m, alpha);
            if (voice.MlsaBeta > 0.0 && m > 1)
            {
                double e1 = B2En(c, 0, m, alpha, vs);
                c[1] -= voice.MlsaBeta * voice.MlsaAlpha * mc[2];
                for (int k = 2; k <= m; k++) c[k] *= 1.0 + voice.MlsaBeta;
                double e2 = B2En(c, 0, m, alpha, vs);
                c[0] += CgMath.Log(e1 / e2) / 2;
            }

            return;
        }

        int cc = vs.CcAt, cinc = vs.CincAt, d1 = vs.D1At;
        Mc2b(mc, c, cc, m, alpha);
        if (voice.MlsaBeta > 0.0 && m > 1)
        {
            double e1 = B2En(c, cc, m, alpha, vs);
            c[cc + 1] -= voice.MlsaBeta * voice.MlsaAlpha * mc[2];
            for (int k = 2; k <= m; k++) c[cc + k] *= 1.0 + voice.MlsaBeta;
            double e2 = B2En(c, cc, m, alpha, vs);
            c[cc] += CgMath.Log(e1 / e2) / 2.0;
        }

        for (int k = 0; k <= m; k++) c[cinc + k] = (c[cc + k] - c[k]) * (double)vs.Iprd / vs.Fprd;
        double inc;
        if (vs.P1 != 0.0 && p != 0.0)
        {
            inc = (p - vs.P1) * (double)vs.Iprd / vs.Fprd;
        }
        else
        {
            inc = 0.0;
            vs.Pc = p;
            vs.P1 = 0.0;
        }

        vs.PadeAt = PadeOrder * (PadeOrder + 1) / 2;
        int order = voice.MixedExcitationOrder;
        int i2 = (vs.Iprd + 1) / 2;
        for (int j = vs.Fprd; j-- != 0;)
        {
            double x, xpulse, xnoise;
            if (vs.P1 == 0.0)
            {
                x = PlusOrMinusOne(vs.Random);   // the noise of the unvoiced frames (no Gaussian noise with mixed excitation)
                xnoise = x;
                xpulse = 0.0;
            }
            else
            {
                if ((vs.Pc += 1.0) >= vs.P1)
                {
                    x = Math.Sqrt(vs.P1);
                    vs.Pc -= vs.P1;
                }
                else
                {
                    x = 0.0;
                }

                xpulse = x;
                xnoise = PlusOrMinusOne(vs.Random);
            }

            // the shaping filters of the pulse and of the noise
            double fxpulse = 0.0, fxnoise = 0.0;
            for (int k = order - 1; k > 0; k--)
            {
                fxpulse += vs.HPulse[k] * vs.XPulseSignal[k];
                fxnoise += vs.HNoise[k] * vs.XNoiseSignal[k];
                vs.XPulseSignal[k] = vs.XPulseSignal[k - 1];
                vs.XNoiseSignal[k] = vs.XNoiseSignal[k - 1];
            }

            fxpulse += vs.HPulse[0] * xpulse;
            fxnoise += vs.HNoise[0] * xnoise;
            vs.XPulseSignal[0] = xpulse;
            vs.XNoiseSignal[0] = xnoise;
            x = fxpulse + fxnoise;   // the excitation is the pulse plus the noise

            if (voice.SampleRate == 8000) x *= CgMath.Exp(c[0]) * 2.0;   // 8 kHz voices are too quiet
            else x *= CgMath.Exp(c[0]) * gain;
            x = MlsaDf(x, c, m, alpha, d1, vs);
            wav[pos] = ToShort(x);
            pos++;
            if (--i2 == 0)
            {
                vs.P1 += inc;
                for (int k = 0; k <= m; k++) c[k] += c[cinc + k];
                i2 = vs.Iprd;
            }
        }

        vs.P1 = p;
        Array.Copy(c, cc, c, 0, m + 1);
    }

    private static double MlsaDf(double x, double[] b, int m, double a, int d, Setup vs)
    {
        x = MlsaDf1(x, b, a, d, vs);
        x = MlsaDf2(x, b, m, a, d + (2 * (PadeOrder + 1)), vs);
        return x;
    }

    private static double MlsaDf1(double x, double[] b, double a, int d, Setup vs)
    {
        double[] w = vs.C;
        double aa = 1 - (a * a);
        int pt = d + PadeOrder + 1;
        double result = 0.0;
        for (int i = PadeOrder; i >= 1; i--)
        {
            w[d + i] = (aa * w[pt + i - 1]) + (a * w[d + i]);
            w[pt + i] = w[d + i] * b[1];
            double v = w[pt + i] * Pade[vs.PadeAt + i];
            x += (1 & i) != 0 ? v : -v;
            result += v;
        }

        w[pt] = x;
        result += x;
        return result;
    }

    private static double MlsaDf2(double x, double[] b, int m, double a, int d, Setup vs)
    {
        double[] w = vs.C;
        double result = 0.0;
        int pt = d + (PadeOrder * (m + 2));
        for (int i = PadeOrder; i >= 1; i--)
        {
            w[pt + i] = MlsaFir(w[pt + i - 1], b, m, a, d + ((i - 1) * (m + 2)), w);
            double v = w[pt + i] * Pade[vs.PadeAt + i];
            x += (1 & i) != 0 ? v : -v;
            result += v;
        }

        w[pt] = x;
        result += x;
        return result;
    }

    private static double MlsaFir(double x, double[] b, int m, double a, int d, double[] w)
    {
        double y = 0.0;
        double aa = 1 - (a * a);
        w[d] = x;
        w[d + 1] = (aa * w[d]) + (a * w[d + 1]);
        for (int i = 2; i <= m; i++)
        {
            w[d + i] = w[d + i] + (a * (w[d + i + 1] - w[d + i - 1]));
            y += w[d + i] * b[i];
        }

        for (int i = m + 1; i > 1; i--) w[d + i] = w[d + i - 1];
        return y;
    }

    /// <summary>mc2b: mel-cepstrum to the coefficients of the MLSA filter, into <paramref name="b"/> from <paramref name="at"/>.</summary>
    private static void Mc2b(double[] mc, double[] b, int at, int m, double a)
    {
        b[at + m] = mc[m];
        for (m--; m >= 0; m--) b[at + m] = mc[m] - (a * b[at + m + 1]);
    }

    /// <summary>b2en: the energy of the impulse response of the filter.</summary>
    private static double B2En(double[] b, int at, int m, double a, Setup vs)
    {
        double[] bs = new double[m + 1];
        Array.Copy(b, at, bs, 0, m + 1);
        B2Mc(bs, vs.Mc, m, a);
        Freqt(vs.Mc, m, vs.Cep, ImpulseLength - 1, -a, vs);
        C2Ir(vs.Cep, ImpulseLength, vs.Ir, ImpulseLength);
        double en = 0.0;
        for (int k = 0; k < ImpulseLength; k++) en += vs.Ir[k] * vs.Ir[k];
        return en;
    }

    private static void B2Mc(double[] b, double[] mc, int m, double a)
    {
        double d = mc[m] = b[m];
        for (m--; m >= 0; m--)
        {
            double o = b[m] + (a * d);
            d = b[m];
            mc[m] = o;
        }
    }

    /// <summary>freqt: the frequency transformation of a cepstrum.</summary>
    private static void Freqt(double[] c1, int m1, double[] c2, int m2, double a, Setup vs)
    {
        double[] d = vs.FreqtD;
        int g = m2 + 1;   // g follows d (size + 1 values for d)
        double b = 1 - (a * a);
        for (int i = 0; i < m2 + 1; i++) d[g + i] = 0.0;
        for (int i = -m1; i <= 0; i++)
        {
            if (0 <= m2) d[g] = c1[-i] + (a * (d[0] = d[g]));
            if (1 <= m2) d[g + 1] = (b * d[0]) + (a * (d[1] = d[g + 1]));
            for (int j = 2; j <= m2; j++) d[g + j] = d[j - 1] + (a * ((d[j] = d[g + j]) - d[g + j - 1]));
        }

        Array.Copy(d, g, c2, 0, m2 + 1);
    }

    /// <summary>c2ir: the impulse response of a minimum phase filter from its cepstrum.</summary>
    private static void C2Ir(double[] c, int nc, double[] h, int leng)
    {
        h[0] = CgMath.Exp(c[0]);
        for (int n = 1; n < leng; n++)
        {
            double d = 0;
            int upl = n >= nc ? nc - 1 : n;
            for (int k = 1; k <= upl; k++) d += k * c[k] * h[n - k];
            h[n] = d / n;
        }
    }
}
