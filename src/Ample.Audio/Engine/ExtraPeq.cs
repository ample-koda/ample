namespace Ample.Audio.Engine;

/// <summary>The parametric equaliser: up to six bands (peak, low shelf, high shelf, notch, low cut, high cut), each a biquad of the RBJ cookbook in double precision.</summary>
internal sealed class PeqRenderer : ExtraEffect
{
    internal const int Bands = 6;

    public override string BlockType => "peq";

    public override string Version => "1";

    /// <summary>Gets the normalised biquad coefficients (b0, b1, b2, a1, a2) of one band.</summary>
    /// <param name="type">peak, lowshelf, highshelf, notch, lowcut (12 dB per octave high-pass) or highcut (12 dB per octave low-pass).</param>
    /// <param name="hz">The frequency.</param>
    /// <param name="gainDb">The gain in dB (ignored by a notch).</param>
    /// <param name="q">The quality factor.</param>
    /// <param name="rate">The sample rate.</param>
    internal static (double B0, double B1, double B2, double A1, double A2) Coefficients(string type, double hz, double gainDb, double q, int rate)
    {
        double w0 = 2 * Math.PI * Math.Clamp(hz, 20, rate * 0.45) / rate, cw = Math.Cos(w0), sw = Math.Sin(w0);
        double alpha = sw / (2 * Math.Max(0.1, q)), a = Math.Pow(10, gainDb / 40);
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case "notch":
                b0 = 1; b1 = -2 * cw; b2 = 1; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case "lowcut":
                b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = b0; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case "highcut":
                b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = b0; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case "lowshelf":
                {
                    double beta = 2 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) - ((a - 1) * cw) + beta); b1 = 2 * a * ((a - 1) - ((a + 1) * cw)); b2 = a * ((a + 1) - ((a - 1) * cw) - beta);
                    a0 = (a + 1) + ((a - 1) * cw) + beta; a1 = -2 * ((a - 1) + ((a + 1) * cw)); a2 = (a + 1) + ((a - 1) * cw) - beta;
                    break;
                }

            case "highshelf":
                {
                    double beta = 2 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) + ((a - 1) * cw) + beta); b1 = -2 * a * ((a - 1) + ((a + 1) * cw)); b2 = a * ((a + 1) + ((a - 1) * cw) - beta);
                    a0 = (a + 1) - ((a - 1) * cw) + beta; a1 = 2 * ((a - 1) - ((a + 1) * cw)); a2 = (a + 1) - ((a - 1) * cw) - beta;
                    break;
                }

            default:
                b0 = 1 + (alpha * a); b1 = -2 * cw; b2 = 1 - (alpha * a); a0 = 1 + (alpha / a); a1 = -2 * cw; a2 = 1 - (alpha / a);
                break;
        }

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Runs the bands over one channel.</summary>
    /// <param name="x">The samples.</param>
    /// <param name="bands">The coefficients of the active bands, in order.</param>
    internal static short[] Run(short[] x, IReadOnlyList<(double B0, double B1, double B2, double A1, double A2)> bands)
    {
        double[] y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i];
        foreach ((double b0, double b1, double b2, double a1, double a2) in bands)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double input = y[i], o = (b0 * input) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
                x2 = x1; x1 = input; y2 = y1; y1 = o;
                y[i] = o;
            }
        }

        return Dsp.ToShort(y);
    }

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        List<(double, double, double, double, double)> active = [];
        for (int n = 1; n <= Bands; n++)
        {
            if (Text(r, $"band{n}_enabled") == "off") continue;
            string type = Text(r, $"band{n}_type");
            double gain = Math.Clamp(Num(r, $"band{n}_gain", 0), -24, 24);
            if (type is not ("notch" or "lowcut" or "highcut") && gain == 0) continue;
            active.Add(Coefficients(type, Math.Clamp(Num(r, $"band{n}_hz", 1000), 20, 20000), gain, Math.Clamp(Num(r, $"band{n}_q", 1), 0.1, 18), rate));
        }

        if (active.Count == 0) return Out(Encode(left, right, rate, stereo));
        short[] l = Run(left, active);
        return Out(Encode(l, stereo ? Run(right, active) : l, rate, stereo));
    }
}
