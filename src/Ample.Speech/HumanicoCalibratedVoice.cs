namespace Ample.Speech;

public sealed record HumanicoPcmResult(short[] Samples, int ClippedSamples);

/// <summary>Experimental calibrated single-word synthesis at the native 10 kHz clock.</summary>
public static class HumanicoCalibratedVoice
{
    public const double PcmScale = 16384;

    public static HumanicoPcmResult ToPcm(ReadOnlySpan<double> samples, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        short[] output = new short[samples.Length];
        int clipped = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (!double.IsFinite(samples[i])) throw new InvalidOperationException("Humanico synthesis produced a non-finite sample.");
            double scaled = Math.Round(samples[i] * PcmScale, MidpointRounding.ToEven);
            if (scaled < short.MinValue || scaled > short.MaxValue) clipped++;
            output[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }
        token.ThrowIfCancellationRequested();
        return new(output, clipped);
    }

    public static double[] RenderWord(string word, double pitchHz, double rate, ulong seed,
        int maxFrames, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(word);
        token.ThrowIfCancellationRequested();
        return RenderPhones(HumanicoFrontEnd.WordToPhones(word), pitchHz, rate, seed, maxFrames, token);
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, double>> PlanPhones(
        IReadOnlyList<SpeechPhone> phones, double rate, int maxFrames, CancellationToken token)
    {
        IReadOnlyList<HumanicoTargetSegment> segments = HumanicoCalibratedSegments.Plan(phones, maxFrames, token);
        return HumanicoTargetFrames.PlanStable(segments, rate, maxFrames, token);
    }

    public static double[] RenderPhones(IReadOnlyList<SpeechPhone> phones, double pitchHz, double rate, ulong seed,
        int maxFrames, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(phones);
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(pitchHz) || pitchHz is < 45 or > 700)
            throw new ArgumentOutOfRangeException(nameof(pitchHz));
        IReadOnlyList<IReadOnlyDictionary<string, double>> frames = PlanPhones(phones, rate, maxFrames, token);
        long samples = (long)frames.Count * HumanicoTargetRenderer.SamplesPerFrame;
        if (samples > int.MaxValue) throw new SpeechResourceLimitException("Humanico word output is too large.");
        double[] noise = new double[(int)samples];
        ulong state = seed;
        for (int i = 0; i < noise.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            ulong z = state;
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            noise[i] = (((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53))) * 2 - 1;
        }
        double[] pitches = Enumerable.Repeat(pitchHz, frames.Count).ToArray();
        return HumanicoTargetRenderer.Render(frames, pitches, noise, (int)samples, token);
    }
}
