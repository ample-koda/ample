namespace Ample.Audio.Engine;

/// <summary>Scales the sound to a target peak or a target integrated loudness (never past -0.1 dBFS), so a quiet recording needs no hand-found gain.</summary>
internal sealed class NormalizeRenderer : ExtraEffect
{
    public override string BlockType => "normalize";

    public override string Version => "1";

    public override string Determinism => "exact";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        double peak = 0;
        foreach (short v in left)
        {
            peak = Math.Max(peak, Math.Abs((double)v));
        }

        if (stereo)
        {
            foreach (short v in right)
            {
                peak = Math.Max(peak, Math.Abs((double)v));
            }
        }

        double gainDb = 0;
        if (peak > 0)
        {
            double peakDb = 20 * Math.Log10(peak / 32768);
            if (Text(r, "mode") == "loudness")
            {
                double? lufs = Loudness.IntegratedLufs(stereo ? [left, right] : [left], rate);
                gainDb = lufs is null ? 0 : Num(r, "loudness", -18) - lufs.Value;
                gainDb = Math.Min(gainDb, -0.1 - peakDb);
            }
            else
            {
                gainDb = Num(r, "peak", -1) - peakDb;
            }
        }

        gainDb = Math.Clamp(gainDb, -60, Num(r, "max_gain", 48));
        double g = Dsp.DbToLinear(gainDb);
        short[] l = new short[left.Length];
        short[] rr = stereo ? new short[right.Length] : l;
        for (int i = 0; i < left.Length; i++)
        {
            l[i] = FixedAudio.Clip((long)Math.Round(left[i] * g));
        }

        if (stereo)
        {
            for (int i = 0; i < right.Length; i++)
            {
                rr[i] = FixedAudio.Clip((long)Math.Round(right[i] * g));
            }
        }

        return Out(Encode(l, rr, rate, stereo));
    }
}
