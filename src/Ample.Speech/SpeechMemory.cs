namespace Ample.Speech;

/// <summary>
/// What a speech render asks of the managed heap, as an upper bound that a host can reserve before rendering. Measured, not exact: a render of
/// output samples allocates about 12 times the bytes of its 16-bit output in text mode (buffers of the frames, the working samples, the resampling and the output);
/// the factor keeps a margin above that. It is the bytes that are allocated over the whole render, which is more than the peak that is alive at once.
/// </summary>
public static class SpeechMemory
{
    /// <summary>Gets the allocated bytes of a render per byte of 16-bit output that the estimate allows.</summary>
    public const int AllocationFactor = 13;

    /// <summary>
    /// Gets the upper bound in bytes for one speech block: what the speech of its text costs (the whole text is spoken before it is cut to the placement,
    /// so a long text in a short placement costs the text) or what its output span costs, whichever is more; a words-mode block also keeps the audio of every
    /// word and its mix buffer next to the output, which the same amount again covers (measured: 15 MiB for 4 s of output with 24 words of 0.5 s).
    /// </summary>
    /// <param name="spanSamples">The length of the block output in mono samples at <paramref name="sampleRate"/>.</param>
    /// <param name="text">The text, or null when there is none.</param>
    /// <param name="phonemes">The phonemes, or null when there are none.</param>
    /// <param name="rate">The authored speaking rate.</param>
    /// <param name="engine">The engine, retro or humanico.</param>
    /// <param name="wordsMode">Whether the block speaks one word per note.</param>
    /// <param name="sampleRate">The sample rate of the build.</param>
    public static long EstimateBlockBytes(long spanSamples, string? text, string? phonemes, double rate, string engine, bool wordsMode, int sampleRate)
    {
        long spoken = 0;
        try
        {
            SpeechRequest request = new(text ?? string.Empty, sampleRate, rate) { Engine = engine, Phonemes = string.IsNullOrWhiteSpace(phonemes) ? null : phonemes };
            spoken = SpeechPlanner.Plan(request).OutputSamples;
        }
        catch (ArgumentException)
        {
            // an input that cannot be planned is refused by the render itself; the span is what is left to count
        }

        long bytes = EstimateBytes(Math.Max(spanSamples, spoken));
        return wordsMode ? checked(bytes + EstimateBytes(spoken)) : bytes;
    }

    /// <summary>Gets the upper bound in bytes for a render that produces <paramref name="outputSamples"/> mono 16-bit samples.</summary>
    public static long EstimateBytes(long outputSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputSamples);
        return checked(outputSamples * sizeof(short) * AllocationFactor);
    }
}
