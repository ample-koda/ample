namespace Ample.Speech;

public static class HumanicoFrameSynthesizer
{
    private const int SampleRate = 10_000;
    private const int SamplesPerFrame = 50;

    public static short[] Render(IReadOnlyList<SpeechFrame> frames, ulong seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count > int.MaxValue / SamplesPerFrame) throw new SpeechResourceLimitException("The frame plan is too large.");
        short[] samples = new short[checked(frames.Count * SamplesPerFrame)];
        var random = new SplitMix64(seed);
        double phase = 0;
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            if ((frameIndex & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            SpeechFrame frame = frames[frameIndex];
            SpeechFrameParameters parameters = frame.Parameters;
            if (frame.FrameInSegment < frame.TransitionFrames && frameIndex > 0 && frames[frameIndex - 1].Parameters.Voiced && parameters.Voiced)
                parameters = Blend(frames[frameIndex - 1].Parameters, parameters, (frame.FrameInSegment + 1d) / (frame.TransitionFrames + 1));
            for (int sampleIndex = 0; sampleIndex < SamplesPerFrame; sampleIndex++)
            {
                double t = (double)sampleIndex / SampleRate;
                double voiced = parameters.Voiced ? FormantVoice(frame with { Parameters = parameters }, t) * parameters.SourceGain : 0;
                double noise = (random.NextUnit() * 2 - 1) * parameters.NoiseGain * 0.025;
                samples[frameIndex * SamplesPerFrame + sampleIndex] = (short)Math.Clamp(
                    Math.Round((voiced + noise) * short.MaxValue, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
            }

            phase += 2 * Math.PI * frame.FundamentalHz * SamplesPerFrame / SampleRate;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return samples;
    }

    private static SpeechFrameParameters Blend(SpeechFrameParameters from, SpeechFrameParameters to, double amount) =>
        new(from.F1Hz + (to.F1Hz - from.F1Hz) * amount, from.F2Hz + (to.F2Hz - from.F2Hz) * amount,
            from.F3Hz + (to.F3Hz - from.F3Hz) * amount, from.NoiseGain + (to.NoiseGain - from.NoiseGain) * amount,
            true, from.SourceGain + (to.SourceGain - from.SourceGain) * amount);

    private static double FormantVoice(SpeechFrame frame, double time)
    {
        double value = 0;
        for (int harmonic = 1; harmonic <= 12; harmonic++)
        {
            double frequency = frame.FundamentalHz * harmonic;
            double weight = FormantWeight(frequency, frame.Parameters.F1Hz)
                + FormantWeight(frequency, frame.Parameters.F2Hz) * 0.75
                + FormantWeight(frequency, frame.Parameters.F3Hz) * 0.5;
            double carrier = Math.Sin(2 * Math.PI * frequency * time);
            double glottal = Math.Sign(carrier) * Math.Pow(Math.Abs(carrier), 0.65);
            value += (carrier * 0.35 + glottal * 0.65) * weight / harmonic;
        }

        double pulse = 0.45 + 0.55 * Math.Max(0, Math.Sin(2 * Math.PI * frame.FundamentalHz * time));
        value += pulse * (Math.Sin(2 * Math.PI * frame.Parameters.F1Hz * time) * 0.22
            + Math.Sin(2 * Math.PI * frame.Parameters.F2Hz * time) * 0.12
            + Math.Sin(2 * Math.PI * frame.Parameters.F3Hz * time) * 0.07);

        return value * 0.055;
    }

    private static double FormantWeight(double frequency, double formant)
    {
        if (formant <= 0) return 0;
        double normalized = (frequency - formant) / (formant * 0.18);
        return Math.Exp(-0.5 * normalized * normalized);
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

