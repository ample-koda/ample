namespace Ample.Speech;

/// <summary>Maps Humanico frame parameter names to the isolated Klatt voice kernel.</summary>
public static class HumanicoTargetAdapter
{
    public static HumanicoKlattParameters Create(IReadOnlyDictionary<string, double> target, double f0)
        => Apply(new HumanicoKlattParameters(), target, f0);

    public static HumanicoKlattParameters Apply(
        HumanicoKlattParameters previous, IReadOnlyDictionary<string, double> target, double f0)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(f0) || f0 <= 0) throw new ArgumentOutOfRangeException(nameof(f0));
        double Value(string key, double fallback) => target.TryGetValue(key, out double value) ? value : fallback;
        return new HumanicoKlattParameters
        {
            F0 = f0,
            Voicing = Value("av", previous.Voicing),
            Aspiration = Value("ah", previous.Aspiration),
            Frication = Value("af", previous.Frication),
            Bypass = Value("ab", previous.Bypass),
            NasalPole = Value("fnp", previous.NasalPole),
            NasalPoleBandwidth = Value("bnp", previous.NasalPoleBandwidth),
            NasalZero = Value("fnz", previous.NasalZero),
            NasalZeroBandwidth = Value("bnz", previous.NasalZeroBandwidth),
            GlottalBandwidth = Value("bgl", previous.GlottalBandwidth),
            GlottalBandwidth2 = Value("bgl2", previous.GlottalBandwidth2),
            Frequencies = [Value("f1", previous.Frequencies[0]), Value("f2", previous.Frequencies[1]),
                Value("f3", previous.Frequencies[2]), Value("f4", previous.Frequencies[3]),
                Value("f5", previous.Frequencies[4]), Value("f6", previous.Frequencies[5])],
            CascadeBandwidths = [Value("b1", previous.CascadeBandwidths[0]), Value("b2", previous.CascadeBandwidths[1]),
                Value("b3", previous.CascadeBandwidths[2]), Value("b4", previous.CascadeBandwidths[3]),
                Value("b5", previous.CascadeBandwidths[4])],
            ParallelBandwidths = [Value("b2f", previous.ParallelBandwidths[0]), Value("b3f", previous.ParallelBandwidths[1]),
                Value("b4f", previous.ParallelBandwidths[2]), Value("b5f", previous.ParallelBandwidths[3]),
                Value("b6f", previous.ParallelBandwidths[4])],
            ParallelAmplitudes = [Value("a2", previous.ParallelAmplitudes[0]), Value("a3", previous.ParallelAmplitudes[1]),
                Value("a4", previous.ParallelAmplitudes[2]), Value("a5", previous.ParallelAmplitudes[3]),
                Value("a6", previous.ParallelAmplitudes[4])]
        };
    }
}
