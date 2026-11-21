namespace Ample.Speech;

/// <summary>Candidate Humanico frame renderer using the isolated source and cascade seams.</summary>
public static class HumanicoKlattFrameRenderer
{
    private const int SamplesPerFrame = 50;
    private const double OutputScale = 1_700;
    private const double VoicedSourceMultiplier = 2;

    public static short[] Render(IReadOnlyList<SpeechFrame> frames, CancellationToken cancellationToken = default)
        => Render(frames, checked(frames.Count * SamplesPerFrame), 0, cancellationToken);

    public static short[] Render(IReadOnlyList<SpeechFrame> frames, int targetSamples, CancellationToken cancellationToken = default)
        => Render(frames, targetSamples, 0, cancellationToken);

    public static short[] Render(IReadOnlyList<SpeechFrame> frames, int targetSamples, ulong seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(targetSamples);
        if (frames.Count > int.MaxValue / SamplesPerFrame) throw new SpeechResourceLimitException("The frame plan is too large.");
        short[] output = new short[checked(targetSamples)];
        HumanicoKlattSource source = new();
        HumanicoKlattCascade cascade = new();
        SplitMix64 random = new(seed);
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            if ((frameIndex & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            SpeechFrame frame = frames[frameIndex];
            for (int sampleIndex = 0; sampleIndex < SamplesPerFrame && frameIndex * SamplesPerFrame + sampleIndex < output.Length; sampleIndex++)
            {
                double value = frame.Parameters.Voiced
                    ? cascade.Process(source.Process(frame.FundamentalHz, frame.Parameters.SourceGain * VoicedSourceMultiplier), frame.Parameters)
                    : (random.NextUnit() * 2 - 1) * frame.Parameters.NoiseGain * 0.025;
                output[frameIndex * SamplesPerFrame + sampleIndex] = (short)Math.Clamp(
                    Math.Round(value * OutputScale, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private struct SplitMix64(ulong state)
    {
        private ulong _state = state;
        public double NextUnit()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return ((z ^ (z >> 31)) >> 11) * (1.0 / (1UL << 53));
        }
    }
}
