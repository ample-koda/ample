using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Lexicon;
using Ample.Speech.Flite.Prosody;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), lang/cmu_us_slt/cmu_us_slt.c (register_cmu_us_slt) and src/cg/cst_cg.c (cg_synth). Translated to C# and modified: see Cg/FliteCgVoiceData.cs.

/// <summary>The parameters of a clustergen render that differ from the voice: a value that is null is not set on the utterance, so the voice's own value counts (as in <c>flite</c>).</summary>
internal sealed record FliteCgOptions
{
    /// <summary>Gets the default limit of the waveform: ten minutes at 16 kHz.</summary>
    public const int DefaultMaxSamples = 16_000 * 600;

    /// <summary>Gets the default limit of the text in bytes.</summary>
    public const int DefaultMaxTextBytes = 1 << 20;

    /// <summary>Gets the <c>duration_stretch</c>: 1 is the speed of the voice.</summary>
    public float? DurationStretch { get; init; }

    /// <summary>Gets the <c>int_f0_target_mean</c> in Hz.</summary>
    public float? F0Mean { get; init; }

    /// <summary>Gets the <c>int_f0_target_stddev</c> in Hz.</summary>
    public float? F0Stddev { get; init; }

    /// <summary>Gets the most samples that all utterances of a render may have together.</summary>
    public int MaxSamples { get; init; } = DefaultMaxSamples;

    /// <summary>Gets the most bytes of text.</summary>
    public int MaxTextBytes { get; init; } = DefaultMaxTextBytes;

    /// <summary>Gets the most frames of 5 ms that one utterance may have (a plan made alone; a render takes it from <see cref="MaxSamples"/>).</summary>
    public int MaxFrames { get; init; } = 200 * 600;

    /// <summary>Gets the words of the block that have their own phones or are spelled (the same hooks of the text analysis and the lexicon as for kal16); null for none.</summary>
    public Lexicon.FliteUserLexicon? UserLexicon { get; init; }

    /// <summary>Gets a steady pitch in Hz that every voiced frame is put on after the voice has made its F0, or null for the F0 of the voice. This is the way to a steady pitch for a voice whose
    /// F0 comes from the accent and phrase model (<c>spamf0</c>, the voice rms): the C program scales the F0 before that model writes it, so <c>int_f0_target_mean</c> does not reach it.
    /// The voices without <c>spamf0</c> are put on a steady pitch the way C does it (a deviation of 0 with <c>int_f0_target_mean</c>), which is also tested against the C program.</summary>
    public float? SteadyF0Hz { get; init; }

    /// <summary>Gets the inline markup of the text (pitch, rate, range and emphasis of words), read from the text that is spoken; null when the text has none.</summary>
    public FliteMarkup? Markup { get; init; }
}

/// <summary>What the voice path has made of an utterance: the HMM states and frames in the utterance, and the parameter tracks.</summary>
internal sealed class FliteCgPlan
{
    /// <summary>Gets the utterance with the relations Segment, HMMstate, segstate, mcep and mcep_link.</summary>
    public required Utterance Utterance { get; init; }

    /// <summary>Gets the number of frames.</summary>
    public required int Frames { get; init; }

    /// <summary>Gets the parameter tracks before smoothing.</summary>
    public required CgParamTrack Track { get; init; }

    /// <summary>Gets the track after MLPG: F0 and the 25 smoothed static mel-cepstrum values of each frame.</summary>
    public required float[][] Smoothed { get; init; }

    /// <summary>Gets the waveform at the sample rate of the voice.</summary>
    public required short[] Samples { get; init; }
}

/// <summary>A clustergen voice: the front end of US English as kal16 uses it, then states, frames and parameters
/// (<c>cg_synth</c>: the waveform of one utterance).</summary>
internal sealed partial class FliteCgEngine
{
    private readonly FliteCgVoiceData voice;
    private readonly CgTrees trees;
    private readonly FliteLexicon lexicon = FliteLexicon.Cmu;
    private readonly FliteProsodyVoice english = FliteProsodyVoice.Create(FliteEnglishData.Instance);
    private readonly UsEnglishFrontEnd frontEnd;

    public FliteCgEngine(FliteCgVoiceData voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        this.voice = voice;
        trees = new CgTrees(voice);
        frontEnd = new UsEnglishFrontEnd(lexicon);

        // the voice of a clustergen voice does not set the duration stretch or the F0 of kal16: they come from the options or from the voice data
        english.Features.Remove("int_f0_target_mean");
        english.Features.Remove("int_f0_target_stddev");
        english.Features.Remove("duration_stretch");
    }

    /// <summary>Gets the data of the voice.</summary>
    public FliteCgVoiceData Voice => voice;

    /// <summary>Gets the sample rate of the waveforms (16000).</summary>
    public int SampleRate => voice.SampleRate;

    private Utterance NewUtterance(FliteCgOptions options)
    {
        Utterance u = english.NewUtterance();
        if (options.DurationStretch is { } stretch) u.Features.SetFloat("duration_stretch", stretch);
        if (options.F0Mean is { } mean) u.Features.SetFloat("int_f0_target_mean", mean);
        if (options.F0Stddev is { } stddev) u.Features.SetFloat("int_f0_target_stddev", stddev);
        return u;
    }

    /// <summary>Runs the text through the modules of <c>flite_synth_text</c> for this voice (one utterance, as <c>flite -t</c> does), up to the waveform.</summary>
    /// <param name="utf8">The text.</param>
    /// <param name="options">The parameters of the call.</param>
    /// <param name="cancellationToken">Stops the work between the steps.</param>
    /// <param name="random">The generator of the noise (<c>rand()</c>); null starts one as a fresh process has it.</param>
    public FliteCgPlan Plan(ReadOnlySpan<byte> utf8, FliteCgOptions? options, CancellationToken cancellationToken, GlibcRandom? random = null)
    {
        options ??= new FliteCgOptions();
        Utterance u = NewUtterance(options);
        string byteText = ByteText.FromUtf8(utf8);
        UsEnglishFrontEnd.Tokenize(u, byteText);
        return Synthesize(u, byteText, 0, options, options.MaxFrames, random ?? new GlibcRandom(), cancellationToken);
    }

    /// <summary>The modules after the tokenisation: text analysis to the waveform.</summary>
    private FliteCgPlan Synthesize(Utterance u, string byteText, int cursorStart, FliteCgOptions options, int maxFrames, GlibcRandom random, CancellationToken cancellationToken)
    {
        // the modules in the order of synth_method_text for this voice (the voice sets no_segment_duration_model and no_f0_target_model: no duration or F0 target modules)
        if (options.Markup is { HasMarkup: true } markup) ApplyMarkup(u, byteText, cursorStart, markup, options);
        frontEnd.TextAnalysis(u, options.UserLexicon, cancellationToken);
        frontEnd.PosTag(u, cancellationToken);
        FliteProsody.Phrasing(u);
        LexicalInsertion.Run(u, lexicon, options.UserLexicon);
        FliteProsody.PauseInsertion(u);
        FliteProsody.Intonation(u);
        CmuPostlex.Run(u);

        // cg_synth: states, frames, parameters
        CgFeatureFunctions.Register(u.FFunctions);
        CgStates.Make(u, voice, cancellationToken);
        float durationStretch = Values.Float(FeatureSet.Param(u.Features, "duration_stretch", new FloatValue(1.0f)));
        int frames = CgFrames.Make(u, voice, durationStretch, maxFrames, cancellationToken);
        CgParamTrack track = CgParams.Predict(u, voice, trees, cancellationToken);
        if (voice.Spamf0) CgSpamf0.Apply(u, voice, track, cancellationToken);
        if (options.SteadyF0Hz is { } steady) CgParams.PutOnSteadyPitch(track, steady);
        float[][] smoothed = CgMlpg.Generate(track, voice.DynamicWindow.Span, cancellationToken);
        short[] samples = CgVocoder.Synthesize(smoothed, track.Strengths, track.Times, voice, random, cancellationToken);
        return new FliteCgPlan { Utterance = u, Frames = frames, Track = track, Smoothed = smoothed, Samples = samples };
    }

    /// <summary>Puts the markup on the tokens as the features that the clustergen code reads: the factor of the duration (<c>local_duration_stretch</c>), the pitch as a mean in Hz
    /// (<c>local_f0_mean</c>; kal16 takes the factor, a clustergen voice the mean itself) and the range as the standard deviation plus one (0 means: not set).</summary>
    private void ApplyMarkup(Utterance u, string byteText, int cursorStart, FliteMarkup markup, FliteCgOptions options)
    {
        float baseMean = options.F0Mean ?? voice.F0Mean;
        foreach ((Item token, int at) in FliteEngine.TokenStarts(u, byteText, cursorStart, out _))
        {
            if (!markup.TokenFeatures(at, out float stretch, out float shift, out float range)) continue;
            if (stretch != 0) Hrg.SetFloat(token, "local_duration_stretch", stretch);
            if (shift != 0) Hrg.SetFloat(token, "local_f0_mean", shift * baseMean);
            if (range != 0) Hrg.SetFloat(token, "local_f0_range", range + 1);
        }
    }

    /// <summary>Speaks a text as one utterance, as <c>flite -t</c> does.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult Render(string text, FliteCgOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Render(System.Text.Encoding.UTF8.GetBytes(text), options, cancellation);
    }

    /// <summary>Speaks a text given as UTF-8 bytes as one utterance. Bytes that are not valid UTF-8 are passed to the text analysis as they are, as the C program does.</summary>
    /// <param name="utf8">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    public FliteRenderResult Render(ReadOnlySpan<byte> utf8, FliteCgOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new FliteCgOptions();
        CheckText(utf8.Length, options);
        try
        {
            FliteCgPlan plan = Plan(utf8, options with { MaxFrames = FramesFor(options.MaxSamples) }, cancellation);
            CheckSamples(plan.Samples.Length, options);
            return Describe(plan.Utterance, ByteText.FromUtf8(utf8), 0, out _, [.. SegmentItems(plan.Utterance)], plan.Samples, 0);
        }
        catch (FliteException e)
        {
            throw new FliteRenderException("The text cannot be spoken: " + e.Message, e);
        }
        catch (FliteCgBudgetException)
        {
            throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
        }
    }

    /// <summary>Speaks a text that may hold several sentences, as <c>flite -f</c> does: the text is cut into utterances at the ends of sentences, every utterance is spoken
    /// on its own and the waveforms follow each other. The noise of the vocoder is one sequence for the whole text (the C program never seeds <c>rand</c> again).</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice. <see cref="FliteCgOptions.MaxSamples"/> counts all utterances together.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult RenderText(string text, FliteCgOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RenderText(System.Text.Encoding.UTF8.GetBytes(text), options, cancellation);
    }

    /// <summary>Speaks a text given as UTF-8 bytes, as <see cref="RenderText(string, FliteCgOptions?, CancellationToken)"/> does.
    /// Bytes that are not valid UTF-8 are passed to the text analysis as they are.</summary>
    /// <param name="utf8">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    public FliteRenderResult RenderText(ReadOnlySpan<byte> utf8, FliteCgOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new FliteCgOptions();
        CheckText(utf8.Length, options);
        try
        {
            string byteText = ByteText.FromUtf8(utf8);
            List<List<TokenRead>> utterances = UtteranceBreaks.Split(byteText, cancellation);
            GlibcRandom random = new();
            List<short[]> pieces = [];
            List<FliteSegmentInfo> segments = [];
            List<FliteWordInfo> words = [];
            long total = 0;
            int cursor = 0, tokenCount = 0;
            foreach (List<TokenRead> tokens in utterances)
            {
                cancellation.ThrowIfCancellationRequested();
                if (tokens.Count == 0) continue;   // an utterance without tokens makes no sound
                Utterance u = NewUtterance(options);
                UsEnglishFrontEnd.TokenizeTokens(u, tokens);
                int rest = (int)Math.Max(0, Math.Min(options.MaxSamples, options.MaxSamples - total));
                FliteCgPlan plan = Synthesize(u, byteText, cursor, options, FramesFor(rest), random, cancellation);
                CheckSamples(total + plan.Samples.Length, options);
                int wordBase = words.Count;
                FliteRenderResult part = Describe(plan.Utterance, byteText, cursor, out cursor, [.. SegmentItems(plan.Utterance)], plan.Samples, (int)total);
                foreach (FliteSegmentInfo segment in part.Segments) segments.Add(segment with { WordIndex = segment.WordIndex < 0 ? -1 : segment.WordIndex + wordBase });
                foreach (FliteWordInfo word in part.Words) words.Add(word with { TokenIndex = word.TokenIndex < 0 ? -1 : word.TokenIndex + tokenCount });
                tokenCount += tokens.Count;
                pieces.Add(plan.Samples);
                total += plan.Samples.Length;
            }

            short[] samples = new short[total];
            int at = 0;
            foreach (short[] piece in pieces)
            {
                piece.CopyTo(samples, at);
                at += piece.Length;
            }

            return new FliteRenderResult { Samples = samples, SampleRate = voice.SampleRate, Segments = segments, Words = words, MissingDiphones = [] };
        }
        catch (FliteException e)
        {
            throw new FliteRenderException("The text cannot be spoken: " + e.Message, e);
        }
        catch (FliteCgBudgetException)
        {
            throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
        }
    }

    private int FramesFor(int samples) => (int)(((long)samples / Math.Max(1, (int)Math.Round(voice.FrameAdvance * voice.SampleRate))) + 2);

    private static void CheckText(int bytes, FliteCgOptions options)
    {
        if (bytes > options.MaxTextBytes) throw new SpeechResourceLimitException($"The text has more than {options.MaxTextBytes} bytes.");
    }

    private static void CheckSamples(long samples, FliteCgOptions options)
    {
        if (samples > options.MaxSamples) throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
    }

    private static IEnumerable<Item> SegmentItems(Utterance u)
    {
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s)) yield return s;
    }

    /// <summary>The words and the segments of an utterance with their places in the waveform and in the text. The end of a segment in samples is its end in time, less the first frame,
    /// which the vocoder only uses to start its filter (there is no reference for these places in the C program: they are for the marks in the editor).</summary>
    private FliteRenderResult Describe(Utterance u, string byteText, int cursorStart, out int cursorEnd, List<Item> segmentItems, short[] samples, int offset)
    {
        List<FliteWordInfo> words = [];
        Dictionary<Item, int> wordIndex = new(ReferenceEqualityComparer.Instance), tokenIndex = new(ReferenceEqualityComparer.Instance);
        int k = 0;
        for (Item? t = u.GetRelation("Token").Head; t is not null; t = Hrg.Next(t)) tokenIndex[t] = k++;
        Dictionary<Item, int> tokenStart = FliteEngine.TokenStarts(u, byteText, cursorStart, out cursorEnd);
        int w0 = 0;
        for (Item? w = u.GetRelation("Word").Head; w is not null; w = Hrg.Next(w))
        {
            wordIndex[w] = w0++;
            Item? token = Hrg.Parent(Hrg.ItemAs(w, "Token"));
            int index = token is not null && tokenIndex.TryGetValue(token, out int found) ? found : -1;
            string tokenName = token is null ? string.Empty : ByteText.ToText(Hrg.Name(token));
            int start = token is not null && tokenStart.TryGetValue(token, out int startAt) ? startAt : -1;
            words.Add(new FliteWordInfo(ByteText.ToText(Hrg.Name(w)), index, tokenName, start));
        }

        int frame = (int)Math.Round(voice.FrameAdvance * voice.SampleRate);
        List<FliteSegmentInfo> segments = new(segmentItems.Count);
        int previous = 0;
        foreach (Item segment in segmentItems)
        {
            Item? syllable = Hrg.Parent(Hrg.ItemAs(segment, "SylStructure"));
            Item? word = syllable is null ? null : Hrg.Parent(Hrg.ItemAs(syllable, "SylStructure"));
            int w = word is not null && wordIndex.TryGetValue(Hrg.ItemAs(word, "Word") ?? word, out int found) ? found : -1;
            // frame n makes the samples from (n - 1) * 80 to n * 80; the frames of a segment are those up to its end (CgFrames)
            int end = Math.Clamp((int)Math.Floor(Hrg.FeatFloat(segment, "end") / voice.FrameAdvance) * frame, previous, samples.Length);
            previous = end;
            segments.Add(new FliteSegmentInfo(Hrg.Name(segment), end + offset, w));
        }

        // the last segment ends with the waveform
        if (segments.Count > 0) segments[^1] = segments[^1] with { EndSample = samples.Length + offset };

        return new FliteRenderResult { Samples = samples, SampleRate = voice.SampleRate, Segments = segments, Words = words, MissingDiphones = [] };
    }
}
