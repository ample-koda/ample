using Ample.Speech.Flite.Lexicon;
namespace Ample.Speech.Flite;

/// <summary>
/// One word spoken on one pitch (<see cref="FliteEngine.RenderWord"/>): the waveform as the C program makes it for a one-word text with a flat pitch,
/// and where the word itself sounds inside it.
/// </summary>
public sealed class FliteWordResult
{
    /// <summary>The waveform, 16-bit samples at <see cref="SampleRate"/>, with the pause of the utterance in front of and behind the word, exactly as <c>flite_synth_text</c> makes it.</summary>
    public required short[] Samples { get; init; }

    /// <summary>The sample rate of the voice, in Hz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>The first sample of the word: the end of the pause in front of it (0 when the utterance has none).</summary>
    public required int WordStartSample { get; init; }

    /// <summary>The sample after the last sound of the word: the start of the pause behind it (the length of the waveform when the utterance has none).</summary>
    public required int WordEndSample { get; init; }

    /// <summary>The pitch that was asked for, in Hz.</summary>
    public required float RequestedF0Hz { get; init; }

    /// <summary>The pitch that is sung: the request inside <see cref="FliteEngine.MinF0Hz"/> and <see cref="FliteEngine.MaxF0Hz"/>, the range of the pitch model of the voice.</summary>
    public required float EffectiveF0Hz { get; init; }

    /// <summary>The segments (phones) with the sample where each ends, as in <see cref="FliteRenderResult.Segments"/>.</summary>
    public required IReadOnlyList<FliteSegmentInfo> Segments { get; init; }

    /// <summary>The words that were spoken for the token (a number gives several), as in <see cref="FliteRenderResult.Words"/>.</summary>
    public required IReadOnlyList<FliteWordInfo> Words { get; init; }

    /// <summary>The diphones the voice does not have; they are skipped, as in C.</summary>
    public required IReadOnlyList<string> MissingDiphones { get; init; }

    /// <summary>Whether the pitch model had to move the request: the sung pitch is not the asked one.</summary>
    public bool F0Clamped => EffectiveF0Hz != RequestedF0Hz;

    /// <summary>The length of the word without the pauses, in samples.</summary>
    public int WordLengthSamples => WordEndSample - WordStartSample;
}

public sealed partial class FliteEngine
{
    /// <summary>The lowest pitch the pitch model sings: <c>add_target_point</c> raises a lower F0 target to 50 Hz.</summary>
    public const float MinF0Hz = 50.0f;

    /// <summary>The highest pitch the pitch model sings: <c>add_target_point</c> lowers a higher F0 target to 500 Hz (a period of 32 samples at 16 kHz).</summary>
    public const float MaxF0Hz = 500.0f;

    /// <summary>
    /// The highest pitch at which the measured words are clean at the stretch of the voice (1.1) and faster. From about 487 Hz the fixed-point filter of the C program
    /// wraps around in a few words (<c>yes</c>, <c>yellow</c>, <c>four</c>, <c>code</c> in the measurement of plan/speech/flite/x1a-pitch-range.md): one sample jumps
    /// over the whole scale. A slower voice wraps earlier (from 391 Hz at stretch 2.2). The port does the same as C; a caller that sings notes keeps below this value.
    /// </summary>
    public const float SafeMaxF0Hz = 480.0f;

    /// <summary>
    /// Speaks one word on one flat pitch: the voice with <c>int_f0_target_mean</c> at <paramref name="f0Hz"/> and <c>int_f0_target_stddev</c> at 0, which is a text of one token
    /// through the whole front end (lexicon or letter rules, syllables, durations), with the pauses of a one-word utterance. The samples equal the output of the C program for that setting.
    /// </summary>
    /// <param name="word">One token without white space (a number or an abbreviation is spoken as the several words it stands for).</param>
    /// <param name="f0Hz">The pitch in Hz; outside <see cref="MinF0Hz"/> and <see cref="MaxF0Hz"/> the pitch model moves it into that range (<see cref="FliteWordResult.EffectiveF0Hz"/>).</param>
    /// <param name="durationStretch">The duration stretch of the voice (<c>duration_stretch</c>): 1.1 is the voice, higher is slower, lower is faster.</param>
    /// <param name="maxSamples">The largest waveform that is made; a longer one gives a <see cref="SpeechResourceLimitException"/>.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <returns>The waveform, the span of the word in it and the segments.</returns>
    /// <exception cref="ArgumentException"><paramref name="word"/> is empty or has white space, <paramref name="f0Hz"/> or <paramref name="durationStretch"/> is not a number above 0.</exception>
    /// <exception cref="FliteRenderException">The word cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The waveform would be larger than <paramref name="maxSamples"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteWordResult RenderWord(string word, float f0Hz, float durationStretch = 1.1f, int maxSamples = FliteOptions.DefaultMaxSamples, FliteUserLexicon? userLexicon = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (word.Length == 0 || word.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A word is one token without white space.", nameof(word));
        }

        if (!(f0Hz > 0.0f) || !float.IsFinite(f0Hz))
        {
            throw new ArgumentException("The pitch is a number above 0 Hz.", nameof(f0Hz));
        }

        if (!(durationStretch > 0.0f) || !float.IsFinite(durationStretch))
        {
            throw new ArgumentException("The duration stretch is a number above 0.", nameof(durationStretch));
        }

        float effective = Math.Clamp(f0Hz, MinF0Hz, MaxF0Hz);
        FliteRenderResult made = Render(word, new FliteOptions { F0Mean = f0Hz, F0Stddev = 0.0f, DurationStretch = durationStretch, MaxSamples = maxSamples, UserLexicon = userLexicon }, cancellation);

        // the span of the word: behind the pause that opens the utterance and in front of the pause that closes it (the segments named pau)
        int start = 0;
        int end = made.Samples.Length;
        IReadOnlyList<FliteSegmentInfo> segments = made.Segments;
        if (segments.Count > 0 && segments[0].Phone == "pau")
        {
            start = segments[0].EndSample;
        }

        if (segments.Count > 1 && segments[^1].Phone == "pau")
        {
            end = segments[^2].EndSample;
        }

        if (end < start)
        {
            end = start;
        }

        return new FliteWordResult
        {
            Samples = made.Samples,
            SampleRate = made.SampleRate,
            WordStartSample = start,
            WordEndSample = end,
            RequestedF0Hz = f0Hz,
            EffectiveF0Hz = effective,
            Segments = segments,
            Words = made.Words,
            MissingDiphones = made.MissingDiphones,
        };
    }
}
