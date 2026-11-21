namespace Ample.Speech;

/// <summary>Renders calibrated target frames with explicit excitation for reference comparisons.</summary>
public static class HumanicoTargetRenderer
{
    public const int SamplesPerFrame = 50;

    public static double[] Render(IReadOnlyList<IReadOnlyDictionary<string, double>> frames,
        IReadOnlyList<double> pitches, ReadOnlySpan<double> noise, int maxSamples, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(pitches);
        token.ThrowIfCancellationRequested();
        if (frames.Count != pitches.Count) throw new ArgumentException("Each frame needs a pitch.", nameof(pitches));
        ArgumentOutOfRangeException.ThrowIfNegative(maxSamples);
        long totalSamples = (long)frames.Count * SamplesPerFrame;
        if (totalSamples > int.MaxValue || maxSamples > 0 && totalSamples > maxSamples)
            throw new SpeechResourceLimitException("Humanico reference render exceeds the configured sample limit.");
        if (noise.Length != totalSamples) throw new ArgumentException("Each sample needs an excitation value.", nameof(noise));
        double[] output = new double[(int)totalSamples];
        HumanicoKlattVoice voice = new();
        HumanicoKlattParameters parameters = new();
        for (int i = 0; i < frames.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            parameters = HumanicoTargetAdapter.Apply(parameters, frames[i], pitches[i]);
            double[] samples = voice.RenderFrame(parameters, noise.Slice(i * SamplesPerFrame, SamplesPerFrame), token);
            samples.CopyTo(output, i * SamplesPerFrame);
        }
        token.ThrowIfCancellationRequested();
        return output;
    }
}
