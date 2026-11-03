namespace Ample.Audio.Engine;

/// <summary>Granular texture: short windowed grains of the input, scattered along it with a little pitch variation, make a longer sound that never repeats. The block seed drives the choices.</summary>
internal sealed class GranularRenderer : ExtraEffect
{
    public override string BlockType => "granular";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        int n = left.Length;
        int grain = Math.Max(64, (int)(Math.Clamp(Num(r, "grain", 120), 10, 1000) * rate / 1000));
        double density = Math.Clamp(Num(r, "density", 40), 1, 400);
        double scatter = Math.Clamp(Num(r, "scatter", 60), 0, 100) / 100;
        double pitch = Math.Clamp(Num(r, "pitch", 0), 0, 12);
        double[] l = new double[n];
        double[] rr = stereo ? new double[n] : l;
        ulong state = r.Seed == 0 ? 0x9E3779B97F4A7C15UL : r.Seed;
        double Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (state >> 11) / (double)(1UL << 53);
        }

        if (n > grain * 2)
        {
            int spacing = Math.Max(1, (int)(rate / density));
            double overlap = Math.Max(1, grain / (double)spacing);
            double scale = 1 / Math.Sqrt(overlap);
            for (int at = 0; at < n; at += spacing)
            {
                if ((at & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                double centre = at + ((Next() - 0.5) * 2 * scatter * n);
                double speed = pitch == 0 ? 1 : Math.Pow(2, ((Next() * 2) - 1) * pitch / 12);
                double from = Math.Clamp(centre - (grain * speed / 2), 0, n - 1 - (grain * speed));
                for (int i = 0; i < grain && at + i < n; i++)
                {
                    double w = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / grain));
                    double p = from + (i * speed);
                    int p0 = (int)p;
                    if (p0 < 0 || p0 + 1 >= n)
                    {
                        continue;
                    }

                    double frac = p - p0;
                    l[at + i] += (left[p0] + ((left[p0 + 1] - left[p0]) * frac)) * w * scale;
                    if (stereo)
                    {
                        rr[at + i] += (right[p0] + ((right[p0 + 1] - right[p0]) * frac)) * w * scale;
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                l[i] = left[i];
                if (stereo)
                {
                    rr[i] = right[i];
                }
            }
        }

        short[] ls = Dsp.ToShort(l);
        return Out(Encode(ls, stereo ? Dsp.ToShort(rr) : ls, rate, stereo));
    }
}
