namespace Ample.Speech;

public sealed record SpeechProgress(string Stage, int Completed, int Total);

public sealed record SpeechRequest(
    string Text,
    int SampleRate = 10_000,
    double Rate = 1,
    double PitchHz = 105,
    ulong Seed = 0,
    int MaxOutputSamples = 0,
    int MaxWorkingSamples = 0,
    Action<SpeechProgress>? Progress = null)
{
    public string Engine { get; init; } = "humanico";
    public string? Phonemes { get; init; }

    /// <summary>Gets the voice of the engine; only the <c>flite</c> engine has a choice (<c>kal16</c>).</summary>
    public string Voice { get; init; } = "kal16";

    /// <summary>Gets the pitch range in Hz (the <c>flite</c> engine only: how far the pitch moves around <see cref="PitchHz"/>).</summary>
    public double PitchRangeHz { get; init; } = 11;

    /// <summary>Gets a value indicating whether the leading silence of the <c>flite</c> voice is dropped, so that a line starts on the beat (the word timings follow).</summary>
    public bool LeadTrim { get; init; }

    /// <summary>Gets the factor of the pauses between the words of the <c>flite</c> voice: 1 leaves them as the voice made them, 0 removes them, 2 doubles them.</summary>
    public double PauseScale { get; init; } = 1;

    /// <summary>Gets the gain in dB of the high end (above 3.5 kHz) that the <c>flite</c> voice gets after the resampling; 0 leaves the sound as it is.</summary>
    public double BrightnessDb { get; init; }

    /// <summary>Gets the entries of the lexicon of the block (<c>word = PHONEMES</c>, separated by <c>;</c>) that give words their own phones (the <c>flite</c> engine only); null for none.</summary>
    public string? Lexicon { get; init; }

    /// <summary>Gets the words that are read letter by letter, separated by spaces, or <c>all</c> (the <c>flite</c> engine only); null for none.</summary>
    public string? Spell { get; init; }
}

public sealed record SpeechWordTiming(int WordIndex, int StartSample, int EndSample, string SourceWord);

public sealed record SpeechResult(
    short[] Samples,
    int SampleRate,
    IReadOnlyList<SpeechWordTiming> Words,
    string EngineIdentity)
{
    /// <summary>Gets where each word of <see cref="Words"/> stands in the text, one for one; null when the engine leaves that to <see cref="SpeechText.WordSpans"/>.</summary>
    public IReadOnlyList<SpeechWordSpan>? Spans { get; init; }
}

public sealed class SpeechResourceLimitException(string message) : Exception(message);

public sealed class HumanicoSpeechEngine
{
    private const int InternalRate = 10_000;
    private const string RetroIdentity = "retro/formant-2";
    public const string HumanicoIdentity = "humanico/klatt-candidate-3";

    public static SpeechResult Render(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        SpeechPlan plan = SpeechPlanner.Plan(request, cancellationToken);
        if (plan.Words.Count == 0) return new([], request.SampleRate, [], request.Engine == "humanico" ? HumanicoIdentity : RetroIdentity);
        string[] words = [.. plan.Words];
        int frameSamples = plan.InternalSamplesPerFrame;

        short[] internalSamples = new short[plan.InternalSamples];
        for (int word = 0; word < words.Length; word++)
        {
            Check(request, "synthesis", word, words.Length, cancellationToken);
            IReadOnlyList<SpeechPhone> phones = plan.PhoneWords.Count > 0 ? plan.PhoneWords[word] : HumanicoFrontEnd.WordToPhones(words[word]);
            ulong wordSeed = SpeechSeed.ForWord(request.Seed, word);
            int targetSamples = checked(plan.WordFrameCounts[word] * frameSamples);
            short[] wordSamples;
            if (request.Engine == "humanico")
            {
                double[] rendered = HumanicoCalibratedVoice.RenderPhones(phones, request.PitchHz, request.Rate,
                    wordSeed, plan.WordFrameCounts[word], cancellationToken);
                HumanicoPcmResult pcm = HumanicoCalibratedVoice.ToPcm(rendered, cancellationToken);
                if (pcm.ClippedSamples > 0) throw new InvalidOperationException("Humanico voice exceeded its PCM headroom.");
                wordSamples = pcm.Samples;
            }
            else
            {
                IReadOnlyList<SpeechFrame> frames = SpeechPlanner.PlanPhones(phones, request.PitchHz, request.Rate, cancellationToken);
                wordSamples = HumanicoFrameSynthesizer.Render(frames, wordSeed, cancellationToken);
            }
            Array.Copy(wordSamples, 0, internalSamples, plan.WordSampleOffsets[word], Math.Min(wordSamples.Length, targetSamples));
        }

        short[] output = new short[plan.OutputSamples];
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 255) == 0) Check(request, "resampling", i, output.Length, cancellationToken);
            double source = (double)i * InternalRate / request.SampleRate;
            int left = Math.Min((int)source, internalSamples.Length - 1);
            int right = Math.Min(left + 1, internalSamples.Length - 1);
            output[i] = (short)Math.Round(internalSamples[left] + (internalSamples[right] - internalSamples[left]) * (source - left), MidpointRounding.ToEven);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(output, request.SampleRate, plan.Timings, request.Engine == "humanico" ? HumanicoIdentity : RetroIdentity);
    }

    private static void Check(SpeechRequest request, string stage, int completed, int total, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        request.Progress?.Invoke(new(stage, completed, total));
        token.ThrowIfCancellationRequested();
    }

    private static void Validate(SpeechRequest request)
    {
        if (request.Phonemes is { Length: > 0 } && request.Text.Length > 0 || request.Engine is not ("retro" or "humanico") || request.SampleRate is < 1_000 or > 96_000 || !double.IsFinite(request.Rate) || request.Rate is < 0.1 or > 2 || !double.IsFinite(request.PitchHz) || request.PitchHz is < 45 or > 700 || request.MaxOutputSamples < 0 || request.MaxWorkingSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(request));
    }

}

