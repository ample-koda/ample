using Ample.Speech.Flite.Lexicon;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_cg.c (cg_smooth_F0: the scaling of the F0 by int_f0_target_mean and int_f0_target_stddev). Translated to C# and modified.
// Copyright and licence: LICENSES/flite.txt, including the original authors.

/// <summary>One word spoken by a clustergen voice on one pitch (<see cref="FliteCgEngine.RenderWord"/>): the waveform as the C program makes it for a one-word text with a flat pitch,
/// and where the word itself sounds inside it.</summary>
internal sealed class FliteCgWordResult
{
    /// <summary>Gets the waveform, 16-bit samples at <see cref="SampleRate"/>, with the pause of the utterance in front of and behind the word.</summary>
    public required short[] Samples { get; init; }

    /// <summary>Gets the sample rate of the voice, in Hz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Gets the first sample of the word: the end of the pause in front of it (0 when the utterance has none).</summary>
    public required int WordStartSample { get; init; }

    /// <summary>Gets the sample after the last sound of the word: the start of the pause behind it.</summary>
    public required int WordEndSample { get; init; }

    /// <summary>Gets the pitch that was asked for, in Hz.</summary>
    public required float RequestedF0Hz { get; init; }

    /// <summary>Gets the pitch that is sung: the request inside <see cref="FliteCgEngine.MinF0Hz"/> and <see cref="FliteCgEngine.MaxF0Hz"/>, the clamp of the F0 in <c>cg_smooth_F0</c>.</summary>
    public required float EffectiveF0Hz { get; init; }

    /// <summary>Gets the segments with the sample where each ends.</summary>
    public required IReadOnlyList<FliteSegmentInfo> Segments { get; init; }

    /// <summary>Gets the words that were spoken for the token (a number gives several).</summary>
    public required IReadOnlyList<FliteWordInfo> Words { get; init; }

    /// <summary>Gets a value indicating whether the pitch had to be moved: the sung pitch is not the asked one.</summary>
    public bool F0Clamped => EffectiveF0Hz != RequestedF0Hz;
}

internal sealed partial class FliteCgEngine
{
    /// <summary>Gets the lowest pitch a clustergen voice sings: <c>cg_smooth_F0</c> raises a lower F0 to 50 Hz.</summary>
    public const float MinF0Hz = 50.0f;

    /// <summary>Gets the highest pitch a clustergen voice sings: <c>cg_smooth_F0</c> lowers a higher F0 to 700 Hz.</summary>
    public const float MaxF0Hz = 700.0f;

    /// <summary>Speaks one word on one flat pitch: the voice with <c>int_f0_target_mean</c> at <paramref name="f0Hz"/> and <c>int_f0_target_stddev</c> at 0, so that the scaling of the F0
    /// (<c>((f0 - mean of the voice) / deviation of the voice) * 0 + mean</c>) puts every voiced frame on the same pitch; the whole front end, the pauses of a one-word utterance.</summary>
    /// <param name="word">One token without white space.</param>
    /// <param name="f0Hz">The pitch in Hz; outside <see cref="MinF0Hz"/> and <see cref="MaxF0Hz"/> the voice moves it into that range.</param>
    /// <param name="durationStretch">The duration stretch (<c>duration_stretch</c>): 1 is the speed of the voice, higher is slower.</param>
    /// <param name="maxSamples">The largest waveform that is made; a longer one gives a <see cref="SpeechResourceLimitException"/>.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="ArgumentException">The word is empty or has white space, or the pitch or the stretch is not a number above 0.</exception>
    /// <exception cref="FliteRenderException">The word cannot be spoken.</exception>
    public FliteCgWordResult RenderWord(string word, float f0Hz, float durationStretch = 1.0f, int maxSamples = FliteCgOptions.DefaultMaxSamples, FliteUserLexicon? userLexicon = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length == 0 || word.Any(char.IsWhiteSpace)) throw new ArgumentException("A word is one token without white space.", nameof(word));
        if (!(f0Hz > 0.0f) || !float.IsFinite(f0Hz)) throw new ArgumentException("The pitch is a number above 0 Hz.", nameof(f0Hz));
        if (!(durationStretch > 0.0f) || !float.IsFinite(durationStretch)) throw new ArgumentException("The duration stretch is a number above 0.", nameof(durationStretch));
        float effective = Math.Clamp(f0Hz, MinF0Hz, MaxF0Hz);
        // a voice without spamf0 is put on the pitch the way C does it (the scaling of cg_smooth_F0 with a deviation of 0); a voice with spamf0 writes its F0 after that scaling and is put on it afterwards
        FliteCgOptions options = Voice.Spamf0
            ? new FliteCgOptions { SteadyF0Hz = effective, DurationStretch = durationStretch, MaxSamples = maxSamples, UserLexicon = userLexicon }
            : new FliteCgOptions { F0Mean = f0Hz, F0Stddev = 0.0f, DurationStretch = durationStretch, MaxSamples = maxSamples, UserLexicon = userLexicon };
        FliteRenderResult made = Render(word, options, cancellation);

        // the span of the word: behind the pause that opens the utterance and in front of the pause that closes it (the segments named pau)
        int start = 0;
        int end = made.Samples.Length;
        IReadOnlyList<FliteSegmentInfo> segments = made.Segments;
        if (segments.Count > 0 && segments[0].Phone == "pau") start = segments[0].EndSample;
        if (segments.Count > 1 && segments[^1].Phone == "pau") end = segments[^2].EndSample;
        if (end < start) end = start;
        return new FliteCgWordResult
        {
            Samples = made.Samples,
            SampleRate = made.SampleRate,
            WordStartSample = start,
            WordEndSample = end,
            RequestedF0Hz = f0Hz,
            EffectiveF0Hz = effective,
            Segments = segments,
            Words = made.Words,
        };
    }
}
