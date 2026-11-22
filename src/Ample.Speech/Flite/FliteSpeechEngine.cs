using Ample.Speech.Flite;
using Ample.Speech.Flite.Data;

namespace Ample.Speech;

/// <summary>One word as the Flite pipeline spoke it: its name, where it stands in the text, and where it sounds in the render.</summary>
/// <param name="Name">The word as it was spoken (a number is several words, each with the place of the number).</param>
/// <param name="TextFrom">Where the word starts in the text, a UTF-16 offset.</param>
/// <param name="TextTo">Where the word ends in the text (exclusive), a UTF-16 offset.</param>
/// <param name="StartSample">The first sample of the word at 16 kHz.</param>
/// <param name="EndSample">The sample after the last of the word at 16 kHz.</param>
internal sealed record FliteSpokenWord(string Name, int TextFrom, int TextTo, int StartSample, int EndSample);

/// <summary>What the Flite pipeline made of one text: the samples at 16 kHz and the words with their places.</summary>
/// <param name="Samples">The 16-bit samples at 16 kHz.</param>
/// <param name="Words">The spoken words in order.</param>
/// <param name="Breaks">The pauses that the markup of the text asks for, to be put into the finished waveform (<see cref="FliteShaping.InsertBreaks"/>); null for none.</param>
internal sealed record FliteRender(short[] Samples, IReadOnlyList<FliteSpokenWord> Words, IReadOnlyList<FliteBreak>? Breaks = null);

/// <summary>A pause of the markup, kept as a place among the words so that it follows the words when the render is shaped.</summary>
/// <param name="BeforeWord">The index of the word that is spoken after the pause; the number of words for a pause at the end.</param>
/// <param name="Samples">The length of the pause in samples at 16 kHz.</param>
internal readonly record struct FliteBreak(int BeforeWord, int Samples);

/// <summary>The settings of one Flite render, in the units of Flite itself.</summary>
/// <param name="DurationStretch">Flite <c>duration_stretch</c>: 1.1 is the speed of the voice; larger is slower.</param>
/// <param name="F0Mean">Flite <c>int_f0_target_mean</c> in Hz.</param>
/// <param name="F0Stddev">Flite <c>int_f0_target_stddev</c> in Hz.</param>
/// <param name="User">The words of the block that have their own phones or are spelled; null for none.</param>
/// <param name="MaxSamples">The most samples at 16 kHz that the render may make; 0 is no limit. A pipeline throws <see cref="SpeechResourceLimitException"/> before it allocates more.</param>
/// <param name="Markup">The inline markup of the text, read; null when the text has none.</param>
internal sealed record FliteSettings(double DurationStretch, double F0Mean, double F0Stddev, long MaxSamples, Flite.Lexicon.FliteUserLexicon? User = null, FliteMarkup? Markup = null);

/// <summary>The text-to-waveform pipeline of the Flite voice (the part that is translated from Flite, see plan/speech/flite).</summary>
internal interface IFlitePipeline
{
    /// <summary>Speaks <paramref name="text"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples and the words.</returns>
    FliteRender Run(string text, FliteSettings settings, CancellationToken cancellationToken);

    /// <summary>Speaks explicit phones (<c>flite -p</c>): one word that spans all of the sound.</summary>
    /// <param name="phones">The phones of the voice, separated by a space (see <see cref="FlitePhoneTable"/>).</param>
    /// <param name="sourceLength">The length of the phonemes as the block wrote them, for the place of the word.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples and the word.</returns>
    FliteRender RunPhones(string phones, int sourceLength, FliteSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This pipeline does not speak phones.");
}

/// <summary>
/// The Flite voice <c>kal16</c> as a speech engine of the <c>speech</c> block: the same request and result as <see cref="HumanicoSpeechEngine"/>. Flite works at
/// 16 kHz and is deterministic, so the seed does nothing; the result is resampled to the rate of the request the way Humanico resamples.
/// </summary>
public static class FliteSpeechEngine
{
    /// <summary>Gets the internal sample rate of the voice.</summary>
    public const int InternalRate = 16_000;

    /// <summary>Gets the pitch (Flite <c>int_f0_target_mean</c>) of the voice in Hz when a block does not say.</summary>
    public const double DefaultPitchHz = 95;

    /// <summary>Gets the pitch range (Flite <c>int_f0_target_stddev</c>) of the voice in Hz when a block does not say.</summary>
    public const double DefaultPitchRangeHz = 11;

    /// <summary>Gets the duration stretch at which the rate of a block is 1 (Flite <c>duration_stretch</c> of the voice).</summary>
    public const double ReferenceStretch = 1.1;

    /// <summary>Gets the voice that a block has when it does not name one (a diphone voice).</summary>
    public const string VoiceName = "kal16";

    /// <summary>Gets the voices of the engine: <c>kal16</c> (diphone) and the clustergen voices <c>slt</c> (US female), <c>rms</c> (US male) and <c>awb</c> (Scottish male).</summary>
    public static IReadOnlyList<string> Voices { get; } = ["kal16", "slt", "rms", "awb"];

    /// <summary>Gets a value indicating whether the voice is one of the clustergen voices (speaks text and the markup, but no phonemes and no words mode).</summary>
    /// <param name="voice">The name of the voice.</param>
    public static bool IsClustergen(string voice) => voice is "slt" or "rms" or "awb";

    /// <summary>Gets the identity of the takes that a voice makes: the Flite source, the converted data and the version of the translation.</summary>
    /// <param name="voice">The name of the voice.</param>
    public static string IdentityOf(string voice) => IsClustergen(voice) ? Flite.Cg.FliteCgVoiceData.IdentityOf(voice) + "/cs1" : Identity;

    /// <summary>Gets the pitch (the mean of the F0 in Hz) that a voice has when a block does not say: 95 Hz for kal16, the mean of the recording for a clustergen voice.</summary>
    /// <param name="voice">The name of the voice.</param>
    public static double DefaultPitchHzOf(string voice) => IsClustergen(voice) ? Flite.Cg.FliteCgVoiceData.Embedded(voice).F0Mean : DefaultPitchHz;

    /// <summary>Gets the pitch range (the standard deviation of the F0 in Hz) that a voice has when a block does not say: 11 Hz for kal16, the value of the recording for a clustergen voice.</summary>
    /// <param name="voice">The name of the voice.</param>
    public static double DefaultPitchRangeHzOf(string voice) => IsClustergen(voice) ? Flite.Cg.FliteCgVoiceData.Embedded(voice).F0Stddev : DefaultPitchRangeHz;

    /// <summary>Gets the identity of takes made by this engine: the Flite source, the converted data and the version of the translation. A change invalidates cached takes.</summary>
    public const string Identity = FliteDataIdentity.Value + "/cs4";

    /// <summary>Gets the lowest pitch in Hz that words mode sings: the lowest that the pitch model of the voice holds (<see cref="FliteEngine.MinF0Hz"/>).</summary>
    public const double WordsMinPitchHz = FliteEngine.MinF0Hz;

    /// <summary>Gets the highest pitch in Hz that words mode sings: below it every measured word is clean (<see cref="FliteEngine.SafeMaxF0Hz"/>, plan/speech/flite/x1a-pitch-range.md).</summary>
    public const double WordsMaxPitchHz = FliteEngine.SafeMaxF0Hz;

    /// <summary>Gets the lowest pitch in Hz that words mode sings with a voice: kal16 from 50 Hz, the clustergen voices from the pitch below which the vocoder of the voice
    /// wraps around in measured words (slt 74, awb 114) or from the lowest measured pitch (rms 55); see plan/speech/flite/x1a-pitch-range.md and cg-words.md.</summary>
    /// <param name="voice">The voice.</param>
    public static double WordsMinPitchHzOf(string voice) => voice switch { "slt" => 74, "rms" => 55, "awb" => 114, _ => WordsMinPitchHz };

    /// <summary>Gets the highest pitch in Hz that words mode sings with a voice: kal16 up to 480 Hz, the clustergen voices up to the pitch at which every measured word is still followed
    /// within 50 cents (slt 650, rms 450, awb 486).</summary>
    /// <param name="voice">The voice.</param>
    public static double WordsMaxPitchHzOf(string voice) => voice switch { "slt" => 650, "rms" => 450, "awb" => 486, _ => WordsMaxPitchHz };

    internal static Flite.Cg.FliteCgEngine CgEngineOf(string voice) => ((FliteCgPipeline)PipelineOf(voice)).Engine;

    private const int WordFadeInSamples = 32;
    private const int WordFadeOutSamples = 64;

    private static readonly Lazy<IFlitePipeline> Shared = new(() => new FliteDefaultPipeline(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IFlitePipeline>> CgShared = new(StringComparer.Ordinal);
    private static readonly FliteRenderCache Cache = new(capacity: 4, maxSamples: 2_000_000);

    private static IFlitePipeline PipelineOf(string voice)
    {
        if (voice == VoiceName) return Shared.Value;
        if (!IsClustergen(voice)) throw new ArgumentOutOfRangeException(nameof(voice), "The voices of the flite engine are kal16, slt, rms and awb.");
        return CgShared.GetOrAdd(voice, name => new Lazy<IFlitePipeline>(() => new FliteCgPipeline(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Speaks the text of the request.</summary>
    /// <param name="request">The request; <see cref="SpeechRequest.Engine"/> must be <c>flite</c>.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at the rate of the request, the word timings (with their places in the text) and the identity.</returns>
    /// <exception cref="ArgumentException">The request is not valid for this engine.</exception>
    /// <exception cref="SpeechResourceLimitException">The speech needs more than the request allows.</exception>
    public static SpeechResult Render(SpeechRequest request, CancellationToken cancellationToken = default) =>
        Render(request, PipelineOf(request.Voice), useCache: true, cancellationToken);

    /// <summary>Plans a request for its length: the render itself, or the memo of a render that was made a moment ago.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The plan: the words with their timings and the number of output samples.</returns>
    public static SpeechPlan Plan(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        SpeechResult result = Render(request, cancellationToken);
        int internalSamples = (int)Math.Min(int.MaxValue, ((long)result.Samples.Length * InternalRate + request.SampleRate / 2) / request.SampleRate);
        return new([.. result.Words.Select(word => word.SourceWord)], [], 0, [], [], InternalRate, 1, internalSamples, result.Samples.Length, result.Words);
    }

    /// <summary>
    /// The pitch of a note for the words mode, as <see cref="HumanicoNotePitchMapper.Map"/> makes it (the reference pitch times 2 to the power of the distance in octaves, kept from 45 to
    /// 700 Hz), but with <see cref="DeterministicMath.Exp2"/>: <see cref="Math.Pow(double, double)"/> is a call to the C library of the platform and may differ in the last bit.
    /// </summary>
    /// <param name="midiPitch">The MIDI pitch of the note.</param>
    /// <param name="referenceMidi">The MIDI pitch of the reference note.</param>
    /// <param name="referenceHz">The pitch of the reference note in Hz.</param>
    /// <returns>The pitch in Hz, from 45 to 700.</returns>
    public static double NotePitchHz(int midiPitch, int referenceMidi, double referenceHz)
    {
        double requested = referenceHz * DeterministicMath.Exp2((midiPitch - referenceMidi) / 12d);
        return Math.Clamp(requested, 45, 700);
    }

    /// <summary>
    /// Folds a pitch by whole octaves into the range that the voice sings (<see cref="WordsMinPitchHz"/> to <see cref="WordsMaxPitchHz"/>), so that a note that is too high or too low
    /// is still sung as the same note name; clamping would make every high note the same. The range is wider than an octave, so the fold always lands inside it.
    /// </summary>
    /// <param name="pitchHz">The pitch the note asks for, in Hz (above 0).</param>
    /// <returns>The pitch to sing, and whether it was moved.</returns>
    /// <param name="voice">The voice; each has its own range (<see cref="WordsMinPitchHzOf"/>, <see cref="WordsMaxPitchHzOf"/>).</param>
    public static (double Hz, bool Folded) FoldPitch(double pitchHz, string voice = VoiceName)
    {
        if (!double.IsFinite(pitchHz) || pitchHz <= 0) throw new ArgumentOutOfRangeException(nameof(pitchHz), "The pitch must be a number above 0 Hz.");
        double min = WordsMinPitchHzOf(voice), max = WordsMaxPitchHzOf(voice), hz = pitchHz;
        while (hz > max) hz /= 2;
        while (hz < min) hz *= 2;
        return (hz, hz != pitchHz);
    }

    /// <summary>
    /// Speaks one word at a flat pitch, for the words mode of the <c>speech</c> block: <see cref="FliteEngine.RenderWord"/> (the whole front end, one steady pitch), cut to the word itself so that it
    /// sounds where its note starts, with a short fade at both ends so that the cut does not click.
    /// </summary>
    /// <param name="word">The word, as written in the text of the block (a number is spoken as several words, all of them are kept).</param>
    /// <param name="pitchHz">The pitch in Hz, from <see cref="WordsMinPitchHz"/> to <see cref="WordsMaxPitchHz"/> (<see cref="FoldPitch"/> moves a note into it).</param>
    /// <param name="rate">The speaking rate, 1 is the speed of the voice (<see cref="ReferenceStretch"/>); from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz; empty when the word has no sound.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pitch or the rate is outside its range.</exception>
    /// <exception cref="SpeechResourceLimitException">The word needs more than <paramref name="maxSamples"/>.</exception>
    public static short[] RenderWord(string word, double pitchHz, double rate, long maxSamples, CancellationToken cancellationToken = default) =>
        RenderWord(word, pitchHz, rate, maxSamples, null, cancellationToken);

    /// <summary>Speaks one word on one pitch, with the words of the block that have their own phones or are spelled (<see cref="Flite.Lexicon.FliteUserLexicon"/>).</summary>
    /// <param name="word">The word, as written in the text of the block.</param>
    /// <param name="pitchHz">The pitch in Hz.</param>
    /// <param name="rate">The speaking rate, from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz; empty when the word has no sound.</returns>
    public static short[] RenderWord(string word, double pitchHz, double rate, long maxSamples, Flite.Lexicon.FliteUserLexicon? userLexicon, CancellationToken cancellationToken = default) =>
        RenderWord(word, pitchHz, rate, maxSamples, userLexicon, VoiceName, cancellationToken);

    /// <summary>Speaks one word on one pitch with a voice (kal16 or a clustergen voice), with the words of the block that have their own phones or are spelled.</summary>
    /// <param name="word">The word, as written in the text of the block.</param>
    /// <param name="pitchHz">The pitch in Hz, from <see cref="WordsMinPitchHzOf"/> to <see cref="WordsMaxPitchHzOf"/> of the voice.</param>
    /// <param name="rate">The speaking rate, from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="voice">The voice: kal16 or a clustergen voice (slt, rms, awb).</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz; empty when the word has no sound.</returns>
    public static short[] RenderWord(string word, double pitchHz, double rate, long maxSamples, Flite.Lexicon.FliteUserLexicon? userLexicon, string voice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(word);
        double minHz = WordsMinPitchHzOf(voice), maxHz = WordsMaxPitchHzOf(voice);
        if (!double.IsFinite(pitchHz) || pitchHz < minHz || pitchHz > maxHz)
            throw new ArgumentOutOfRangeException(nameof(pitchHz), $"The pitch of a word must be from {minHz} to {maxHz} Hz.");
        if (!double.IsFinite(rate) || rate is < 0.1 or > 2) throw new ArgumentOutOfRangeException(nameof(rate), "The rate must be from 0.1 to 2.");
        cancellationToken.ThrowIfCancellationRequested();
        string token = string.Concat(word.Where(c => !char.IsWhiteSpace(c)));
        if (token.Length == 0) return [];
        int limit = maxSamples is <= 0 or > int.MaxValue ? FliteOptions.DefaultMaxSamples : (int)maxSamples;
        short[] all;
        int wordStart, wordEnd;
        try
        {
            if (IsClustergen(voice))
            {
                Flite.Cg.FliteCgWordResult cg = CgEngineOf(voice).RenderWord(token, (float)pitchHz, (float)(1.0 / rate), limit, userLexicon, cancellationToken);
                (all, wordStart, wordEnd) = (cg.Samples, cg.WordStartSample, cg.WordEndSample);
            }
            else
            {
                FliteWordResult made = FliteEngine.Default.RenderWord(token, (float)pitchHz, (float)(ReferenceStretch / rate), limit, userLexicon, cancellationToken);
                (all, wordStart, wordEnd) = (made.Samples, made.WordStartSample, made.WordEndSample);
            }
        }
        catch (FliteRenderException)
        {
            return [];   // a token without anything to say (only punctuation)
        }

        if (all.Length == 0) return [];
        int from = Math.Clamp(wordStart, 0, all.Length);
        int to = Math.Clamp(wordEnd, from, all.Length);
        short[] sound = all[from..to];
        FadeEnds(sound);
        return sound;
    }

    /// <summary>
    /// Sings a word on a note: <see cref="FoldPitch"/> moves the pitch into the range, <see cref="RenderWord(string, double, double, long, CancellationToken)"/> speaks it,
    /// and a word whose waveform wraps around is sung an octave lower, so that
    /// it never clicks (the fixed-point filter of the C program wraps for some words at the top of the range, at a pitch that depends on the word and the rate: measured from 370 Hz on 400 words).
    /// </summary>
    /// <param name="word">The word, as written in the text of the block.</param>
    /// <param name="requestedHz">The pitch of the note in Hz (above 0).</param>
    /// <param name="rate">The speaking rate, from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="sungHz">The pitch that was sung: the note, or the note moved by whole octaves.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz.</returns>
    public static short[] RenderSungWord(string word, double requestedHz, double rate, long maxSamples, out double sungHz, CancellationToken cancellationToken = default) =>
        RenderSungWord(word, requestedHz, rate, maxSamples, null, out sungHz, cancellationToken);

    /// <summary>Sings a word on a note, with the words of the block that have their own phones or are spelled (<see cref="Flite.Lexicon.FliteUserLexicon"/>).</summary>
    /// <param name="word">The word, as written in the text of the block.</param>
    /// <param name="requestedHz">The pitch of the note in Hz (above 0).</param>
    /// <param name="rate">The speaking rate, from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="sungHz">The pitch that was sung: the note, or the note moved by whole octaves.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz.</returns>
    public static short[] RenderSungWord(string word, double requestedHz, double rate, long maxSamples, Flite.Lexicon.FliteUserLexicon? userLexicon, out double sungHz,
        CancellationToken cancellationToken = default) =>
        RenderSungWord(word, requestedHz, rate, maxSamples, userLexicon, VoiceName, out sungHz, cancellationToken);

    /// <summary>Sings a word on a note with a voice (kal16 or a clustergen voice): the pitch is folded into the range of the voice, and a word that wraps around is sung an octave away.</summary>
    /// <param name="word">The word, as written in the text of the block.</param>
    /// <param name="requestedHz">The pitch of the note in Hz (above 0).</param>
    /// <param name="rate">The speaking rate, from 0.1 to 2.</param>
    /// <param name="maxSamples">The most samples at 16 kHz the word may take; 0 is no limit.</param>
    /// <param name="userLexicon">The words of the block that have their own phones or are spelled, or null.</param>
    /// <param name="voice">The voice: kal16 or a clustergen voice (slt, rms, awb).</param>
    /// <param name="sungHz">The pitch that was sung: the note, or the note moved by whole octaves.</param>
    /// <param name="cancellationToken">Cancels the render between steps.</param>
    /// <returns>The samples at 16 kHz.</returns>
    public static short[] RenderSungWord(string word, double requestedHz, double rate, long maxSamples, Flite.Lexicon.FliteUserLexicon? userLexicon, string voice, out double sungHz,
        CancellationToken cancellationToken = default)
    {
        (double hz, _) = FoldPitch(requestedHz, voice);
        short[] sound = RenderWord(word, hz, rate, maxSamples, userLexicon, voice, cancellationToken);
        // a word that wraps around is sung an octave away, towards the safe side first: kal16 wraps at the top of its range (down), the clustergen voices at the bottom (up)
        double min = WordsMinPitchHzOf(voice), max = WordsMaxPitchHzOf(voice);
        foreach (double step in IsClustergen(voice) ? new[] { 2.0, 0.5 } : new[] { 0.5, 2.0 })
        {
            while (HasWrapAround(sound) && hz * step >= min && hz * step <= max)
            {
                hz *= step;
                sound = RenderWord(word, hz, rate, maxSamples, userLexicon, voice, cancellationToken);
            }
        }

        sungHz = hz;
        return sound;
    }

    /// <summary>Applies the brightness of the voice (a high shelf, as <see cref="SpeechRequest.BrightnessDb"/> does in text mode) to the samples of a word, in place.</summary>
    /// <param name="samples">The samples at 16 kHz; changed in place.</param>
    /// <param name="gainDb">The gain of the high end in dB; 0 leaves the samples as they are.</param>
    public static void Brighten(short[] samples, double gainDb) => FliteShaping.Brighten(samples, InternalRate, gainDb);

    /// <summary>Whether a waveform jumps over (almost) the whole scale between two samples: the wrap-around of a filter, a click. The steps of a clean word stay below a third of the scale.</summary>
    internal static bool HasWrapAround(short[] sound)
    {
        for (int i = 1; i < sound.Length; i++)
        {
            if (Math.Abs(sound[i] - sound[i - 1]) > 40_000) return true;
        }

        return false;
    }

    private static void FadeEnds(short[] sound)
    {
        int fadeIn = Math.Min(WordFadeInSamples, sound.Length / 2), fadeOut = Math.Min(WordFadeOutSamples, sound.Length / 2);
        for (int i = 0; i < fadeIn; i++) sound[i] = (short)Math.Round(sound[i] * ((i + 1d) / (fadeIn + 1d)), MidpointRounding.ToEven);
        for (int i = 0; i < fadeOut; i++) sound[sound.Length - 1 - i] = (short)Math.Round(sound[sound.Length - 1 - i] * ((i + 1d) / (fadeOut + 1d)), MidpointRounding.ToEven);
    }

    internal static SpeechResult Render(SpeechRequest request, IFlitePipeline pipeline, bool useCache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        FliteSettings settings = SettingsOf(request);
        FliteRender render;
        string phones = request.Phonemes is { Length: > 0 } ? FlitePhoneTable.Translate(request.Phonemes) : string.Empty;
        string key = string.Join('\u0001', request.Voice, phones.Length > 0 ? "phones:" + phones : request.Text, settings.User?.Canonical ?? string.Empty,
            settings.DurationStretch.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            settings.F0Mean.ToString("R", System.Globalization.CultureInfo.InvariantCulture), settings.F0Stddev.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (!useCache || !Cache.TryGet(key, settings.MaxSamples, out render))
        {
            render = phones.Length > 0 ? pipeline.RunPhones(phones, request.Phonemes!.Length, settings, cancellationToken) : pipeline.Run(request.Text, settings, cancellationToken);
            if (settings.MaxSamples > 0 && render.Samples.Length > settings.MaxSamples)
                throw new SpeechResourceLimitException($"Speech work needs {render.Samples.Length} internal samples at 16 kHz, exceeding the configured limit.");
            if (useCache) Cache.Add(key, render);
        }

        cancellationToken.ThrowIfCancellationRequested();
        render = FliteShaping.Shape(render, request.LeadTrim, request.PauseScale);   // after the cache and after the exact render: never inside the pipeline
        render = FliteShaping.InsertBreaks(render);   // the pauses of the markup are exactly as long as written: after the lead and the pause scale
        if (render.Samples.Length == 0) return new([], request.SampleRate, [], IdentityOf(request.Voice)) { Spans = [] };
        long outputSamples = ((long)render.Samples.Length * request.SampleRate + InternalRate / 2) / InternalRate;
        if (outputSamples > int.MaxValue || request.MaxOutputSamples > 0 && outputSamples > request.MaxOutputSamples)
            throw new SpeechResourceLimitException($"Speech output needs {outputSamples} samples, exceeding the configured limit.");
        short[] output = Resample(render.Samples, request, (int)outputSamples, cancellationToken);
        FliteShaping.Brighten(output, request.SampleRate, request.BrightnessDb);
        List<SpeechWordTiming> timings = [];
        List<SpeechWordSpan> spans = [];
        for (int i = 0; i < render.Words.Count; i++)
        {
            FliteSpokenWord word = render.Words[i];
            timings.Add(new(i, Scale(word.StartSample, request.SampleRate), Scale(word.EndSample, request.SampleRate), word.Name));
            spans.Add(new(word.Name, word.TextFrom, word.TextTo));
        }

        return new(output, request.SampleRate, timings, IdentityOf(request.Voice)) { Spans = spans };
    }

    internal static FliteSettings SettingsOf(SpeechRequest request)
    {
        double stretch = (IsClustergen(request.Voice) ? 1.0 : ReferenceStretch) / request.Rate;   // 1.1 is the speed of kal16, 1 the speed of a clustergen voice
        long max = 0;
        // the working budget of a request counts samples at 10 kHz (Humanico); the same number of seconds is allowed here
        if (request.MaxWorkingSamples > 0) max = (long)request.MaxWorkingSamples * InternalRate / 10_000;
        if (request.MaxOutputSamples > 0)
        {
            long fromOutput = ((long)request.MaxOutputSamples * InternalRate + request.SampleRate - 1) / request.SampleRate + 1;
            max = max == 0 ? fromOutput : Math.Min(max, fromOutput);
        }

        Flite.Lexicon.FliteUserLexicon user = Flite.Lexicon.FliteUserLexicon.Parse(request.Lexicon, request.Spell);
        FliteMarkup markup = request.Phonemes is { Length: > 0 } ? FliteMarkup.Parse(string.Empty, request.PitchHz) : FliteMarkup.Parse(request.Text, request.PitchHz);
        return new(stretch, request.PitchHz, request.PitchRangeHz, max, user.IsEmpty ? null : user, markup.HasMarkup ? markup : null);
    }

    private static void Validate(SpeechRequest request)
    {
        if (request.Engine != "flite") throw new ArgumentException("The engine of the request is not flite.", nameof(request));
        if (!Voices.Contains(request.Voice)) throw new ArgumentOutOfRangeException(nameof(request), "The voices of the flite engine are kal16, slt, rms and awb.");
        if (IsClustergen(request.Voice) && request.Phonemes is { Length: > 0 })
            throw new ArgumentException($"The voice {request.Voice} speaks text: phonemes belong to the voice kal16.", nameof(request));
        if (request.Phonemes is { Length: > 0 } && request.Text.Length > 0) throw new ArgumentException("Give text or phonemes, never both.", nameof(request));
        if (request.Phonemes is { Length: > 0 } && (!string.IsNullOrWhiteSpace(request.Lexicon) || !string.IsNullOrWhiteSpace(request.Spell)))
            throw new ArgumentException("The lexicon and spell work on a text, not on phonemes.", nameof(request));
        if (request.SampleRate is < 1_000 or > 96_000) throw new ArgumentOutOfRangeException(nameof(request), "The sample rate must be from 1000 to 96000 Hz.");
        if (!double.IsFinite(request.Rate) || request.Rate is < 0.1 or > 2) throw new ArgumentOutOfRangeException(nameof(request), "The rate must be from 0.1 to 2.");
        if (!double.IsFinite(request.PitchHz) || request.PitchHz is < 45 or > 700) throw new ArgumentOutOfRangeException(nameof(request), "The pitch must be from 45 to 700 Hz.");
        if (!double.IsFinite(request.PitchRangeHz) || request.PitchRangeHz is < 0 or > 200) throw new ArgumentOutOfRangeException(nameof(request), "The pitch range must be from 0 to 200 Hz.");
        if (!double.IsFinite(request.PauseScale) || request.PauseScale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(request), "The pause scale must be from 0 to 4.");
        if (!double.IsFinite(request.BrightnessDb) || request.BrightnessDb is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(request), "The brightness must be from 0 to 12 dB.");
    }

    private static short[] Resample(short[] source, SpeechRequest request, int outputSamples, CancellationToken token)
    {
        short[] output = new short[outputSamples];
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
                request.Progress?.Invoke(new("resampling", i, output.Length));
            }

            double position = (double)i * InternalRate / request.SampleRate;
            int left = Math.Min((int)position, source.Length - 1);
            int right = Math.Min(left + 1, source.Length - 1);
            output[i] = (short)Math.Round(source[left] + (source[right] - source[left]) * (position - left), MidpointRounding.ToEven);
        }

        return output;
    }

    private static int Scale(int sample, int outputRate) => checked((int)(((long)sample * outputRate + InternalRate / 2) / InternalRate));
}

/// <summary>A small memo of the last renders, so that a block that is planned for its length and then rendered is spoken once.</summary>
internal sealed class FliteRenderCache(int capacity, int maxSamples)
{
    private readonly object gate = new();
    private readonly List<(string Key, FliteRender Render)> entries = [];

    public bool TryGet(string key, long limit, out FliteRender render)
    {
        lock (gate)
        {
            int index = entries.FindIndex(entry => entry.Key == key);
            if (index >= 0 && (limit <= 0 || entries[index].Render.Samples.Length <= limit))
            {
                render = entries[index].Render;
                return true;
            }
        }

        render = null!;
        return false;
    }

    public void Add(string key, FliteRender render)
    {
        if (render.Samples.Length > maxSamples) return;
        lock (gate)
        {
            entries.RemoveAll(entry => entry.Key == key);
            entries.Add((key, render));
            if (entries.Count > capacity) entries.RemoveAt(0);
        }
    }
}
