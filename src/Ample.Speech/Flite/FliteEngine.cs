using System.Text;
using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Lexicon;
using Ample.Speech.Flite.Prosody;
using Ample.Speech.Flite.Synth;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (utt_synth: the order of the synth modules) and flite.c (flite_text_to_speech) for the voice cmu_us_kal16.
// Translated to C# and modified: the modules are the ones of this port, the voice parameters are options of the call, and the result carries the end of every segment in the
// waveform and the word of every segment so that a caller can derive word timings. See FLITE-NOTICE.md in the data folder.

/// <summary>The parameters of one rendering. The defaults are the values of the voice cmu_us_kal16.</summary>
public sealed record FliteOptions
{
    /// <summary>The largest text, in bytes of UTF-8, that is accepted.</summary>
    public const int DefaultMaxTextBytes = 1 << 20;

    /// <summary>The default limit of the waveform: ten minutes at 16 kHz.</summary>
    public const int DefaultMaxSamples = 16_000 * 600;

    /// <summary><c>duration_stretch</c>: 1.0 is the speed of the data, larger is slower. The voice has 1.1.</summary>
    public float DurationStretch { get; init; } = 1.1f;

    /// <summary><c>int_f0_target_mean</c>, the mean pitch in Hz. The voice has 95.</summary>
    public float F0Mean { get; init; } = 95.0f;

    /// <summary><c>int_f0_target_stddev</c>, the spread of the pitch in Hz. The voice has 11.</summary>
    public float F0Stddev { get; init; } = 11.0f;

    /// <summary>The longest text in bytes of UTF-8; a longer text is refused before any work is done.</summary>
    public int MaxTextBytes { get; init; } = DefaultMaxTextBytes;

    /// <summary>The longest waveform in samples (16 kHz); a text whose duration model gives more is refused before the synthesis allocates.</summary>
    public int MaxSamples { get; init; } = DefaultMaxSamples;

    /// <summary>The words that have their own phones and the words that are read letter by letter; null leaves the voice as it is (the sound is then the sound of the C program).</summary>
    public FliteUserLexicon? UserLexicon { get; init; }

    /// <summary>The inline markup of the text (pitch, rate, range and emphasis of words): it is read from the text that is spoken, <see cref="FliteMarkup.Plain"/>;
    /// null leaves the voice as it is.</summary>
    public FliteMarkup? Markup { get; init; }
}

/// <summary>One segment (a phone or a silence) of a rendering.</summary>
/// <param name="Phone">The phone name (<c>hh</c>, <c>ae</c>, <c>pau</c>).</param>
/// <param name="EndSample">The end of the segment in the waveform, in samples of <see cref="FliteRenderResult.SampleRate"/>; the last segment ends with the waveform.</param>
/// <param name="WordIndex">The index in <see cref="FliteRenderResult.Words"/> of the word the segment belongs to, or -1 for a silence that belongs to no word.</param>
public readonly record struct FliteSegmentInfo(string Phone, int EndSample, int WordIndex);

/// <summary>One spoken word of a rendering. A token of the text can give several words (a number is spoken as many).</summary>
/// <param name="Spoken">The word as it is spoken (<c>one</c>, <c>hundred</c>).</param>
/// <param name="TokenIndex">The index of the token of the text the word comes from.</param>
/// <param name="Token">The token as it stands in the text.</param>
/// <param name="TokenStart">The position of the token in the text, in bytes of UTF-8 (the first character of the token, after any opening punctuation); -1 when it cannot be found.</param>
public readonly record struct FliteWordInfo(string Spoken, int TokenIndex, string Token, int TokenStart);

/// <summary>What a rendering made: the waveform, and where the segments and words are in it.</summary>
public sealed class FliteRenderResult
{
    /// <summary>The waveform, 16-bit mono samples.</summary>
    public required short[] Samples { get; init; }

    /// <summary>The sample rate of the voice (16000).</summary>
    public required int SampleRate { get; init; }

    /// <summary>The segments in order, with their end in the waveform.</summary>
    public required IReadOnlyList<FliteSegmentInfo> Segments { get; init; }

    /// <summary>The spoken words in order.</summary>
    public required IReadOnlyList<FliteWordInfo> Words { get; init; }

    /// <summary>The diphones that are not in the voice, in order of use. Flite prints these and takes the first diphone of the voice instead; the sound is still the sound of the C program.</summary>
    public required IReadOnlyList<string> MissingDiphones { get; init; }
}

/// <summary>The text cannot be spoken: the C program would crash or read outside its tables on it, or the text is too long.</summary>
public sealed class FliteRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Text to speech with the voice cmu_us_kal16 of Flite, in C#. Thread-safe: the data and the models are immutable and every call has its own utterance.
/// </summary>
public sealed partial class FliteEngine
{
    private static readonly Lazy<FliteEngine> Shared = new(() => new FliteEngine());

    private readonly FliteVoiceData voice = FliteVoiceData.Instance;
    private readonly FliteLexicon lexicon = FliteLexicon.Cmu;
    private readonly FliteProsodyVoice prosody = FliteProsodyVoice.Create(FliteEnglishData.Instance);
    private readonly UsEnglishFrontEnd frontEnd;

    private FliteEngine()
    {
        frontEnd = new UsEnglishFrontEnd(lexicon);
    }

    /// <summary>The engine with the embedded data of the voice (made on first use).</summary>
    public static FliteEngine Default => Shared.Value;

    /// <summary>The sample rate of the waveforms (16000).</summary>
    public int SampleRate => voice.SampleRate;

    /// <summary>The identity of the data of the engine, for the hash of a build: the voice, the commit of Flite and the hash of the converted data.</summary>
    public static string Identity => FliteDataIdentity.Value;

    /// <summary>Speaks a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult Render(string text, FliteOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new FliteOptions();
        if (text.Length > options.MaxTextBytes)
        {
            throw new SpeechResourceLimitException($"The text has more than {options.MaxTextBytes} bytes.");
        }

        return Render(Encoding.UTF8.GetBytes(text), options, cancellation);
    }

    /// <summary>Speaks a text given as UTF-8 bytes. Bytes that are not valid UTF-8 are passed to the text analysis as they are, as the C program does.</summary>
    /// <param name="utf8">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult Render(ReadOnlySpan<byte> utf8, FliteOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new FliteOptions();
        if (utf8.Length > options.MaxTextBytes)
        {
            throw new SpeechResourceLimitException($"The text has more than {options.MaxTextBytes} bytes.");
        }

        try
        {
            return RenderCore(ByteText.FromUtf8(utf8), options, cancellation);
        }
        catch (FliteException e)
        {
            throw new FliteRenderException("The text cannot be spoken: " + e.Message, e);
        }
    }

    /// <summary>
    /// Speaks a text that may hold several sentences, as <c>flite -f</c> (<c>flite_file_to_speech</c>) does: the text is cut into utterances at the ends of sentences
    /// (<c>default_utt_break</c>: a double new line, <c>: ? !</c>, a full stop in front of a capital that does not follow an abbreviation, and after 500 tokens), every utterance is
    /// spoken on its own, with its own phrasing and intonation, and the waveforms follow each other without extra silence. <see cref="Render(string, FliteOptions?, CancellationToken)"/>
    /// speaks the whole text as one utterance, as <c>flite -t</c> does.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice. <see cref="FliteOptions.MaxSamples"/> counts all utterances together.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <returns>The waveform and the segments and words of all utterances, placed in the waveform and in the text.</returns>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult RenderText(string text, FliteOptions? options = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new FliteOptions();
        if (text.Length > options.MaxTextBytes)
        {
            throw new SpeechResourceLimitException($"The text has more than {options.MaxTextBytes} bytes.");
        }

        return RenderText(Encoding.UTF8.GetBytes(text), options, cancellation);
    }

    /// <summary>Speaks a text that may hold several sentences, given as UTF-8 bytes; see <see cref="RenderText(string, FliteOptions?, CancellationToken)"/>.</summary>
    /// <param name="utf8">The text.</param>
    /// <param name="options">The parameters, or null for the values of the voice.</param>
    /// <param name="cancellation">Stops the work between the steps and inside the synthesis.</param>
    /// <returns>The waveform and the segments and words of all utterances.</returns>
    /// <exception cref="FliteRenderException">The text cannot be spoken.</exception>
    /// <exception cref="SpeechResourceLimitException">The text or the waveform is larger than the limits of <paramref name="options"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    public FliteRenderResult RenderText(ReadOnlySpan<byte> utf8, FliteOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new FliteOptions();
        if (utf8.Length > options.MaxTextBytes)
        {
            throw new SpeechResourceLimitException($"The text has more than {options.MaxTextBytes} bytes.");
        }

        try
        {
            string byteText = ByteText.FromUtf8(utf8);
            List<List<TokenRead>> utterances = UtteranceBreaks.Split(byteText, cancellation);
            List<short[]> pieces = [];
            List<FliteSegmentInfo> segments = [];
            List<FliteWordInfo> words = [];
            List<string> missing = [];
            long total = 0;
            int cursor = 0, tokenCount = 0;
            foreach (List<TokenRead> tokens in utterances)
            {
                cancellation.ThrowIfCancellationRequested();
                if (tokens.Count == 0)
                {
                    continue;   // an utterance without tokens makes no sound
                }

                Utterance u = NewUtterance(options);
                UsEnglishFrontEnd.TokenizeTokens(u, tokens);
                FliteOptions rest = options with { MaxSamples = (int)Math.Max(0, Math.Min(options.MaxSamples, options.MaxSamples - total)) };
                FliteRenderResult part = SynthesizeUtterance(u, byteText, cursor, out cursor, rest, cancellation);
                int wordBase = words.Count;
                int segmentBase = (int)total;
                foreach (FliteSegmentInfo segment in part.Segments)
                {
                    segments.Add(segment with { EndSample = segment.EndSample + segmentBase, WordIndex = segment.WordIndex < 0 ? -1 : segment.WordIndex + wordBase });
                }

                foreach (FliteWordInfo word in part.Words)
                {
                    words.Add(word with { TokenIndex = word.TokenIndex < 0 ? -1 : word.TokenIndex + tokenCount });
                }

                tokenCount += tokens.Count;
                missing.AddRange(part.MissingDiphones);
                pieces.Add(part.Samples);
                total += part.Samples.Length;
            }

            short[] samples = new short[total];
            int at = 0;
            foreach (short[] piece in pieces)
            {
                piece.CopyTo(samples, at);
                at += piece.Length;
            }

            return new FliteRenderResult { Samples = samples, SampleRate = voice.SampleRate, Segments = segments, Words = words, MissingDiphones = missing };
        }
        catch (FliteException e)
        {
            throw new FliteRenderException("The text cannot be spoken: " + e.Message, e);
        }
    }

    private Utterance NewUtterance(FliteOptions options)
    {
        // utt_init: the features of the voice are linked into the utterance, then the options of the call are set on the utterance, where they are found first
        Utterance u = prosody.NewUtterance();
        u.Features.SetFloat("duration_stretch", options.DurationStretch);
        u.Features.SetFloat("int_f0_target_mean", options.F0Mean);
        u.Features.SetFloat("int_f0_target_stddev", options.F0Stddev);
        return u;
    }

    private FliteRenderResult RenderCore(string byteText, FliteOptions options, CancellationToken cancellation)
    {
        Utterance u = NewUtterance(options);

        // the modules in the order of synth_method_text (cst_synth.c) for this voice
        UsEnglishFrontEnd.Tokenize(u, byteText);
        return SynthesizeUtterance(u, byteText, 0, out _, options, cancellation);
    }

    /// <summary>The modules after the tokenisation, for an utterance whose Token relation is made: text analysis to the waveform.</summary>
    private FliteRenderResult SynthesizeUtterance(Utterance u, string byteText, int cursorStart, out int cursorEnd, FliteOptions options, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (options.Markup is { HasMarkup: true } markup) ApplyMarkup(u, byteText, cursorStart, markup);
        frontEnd.TextAnalysis(u, options.UserLexicon, cancellation);
        frontEnd.PosTag(u, cancellation);
        cancellation.ThrowIfCancellationRequested();
        FliteProsody.Phrasing(u);
        LexicalInsertion.Run(u, lexicon, options.UserLexicon);
        FliteProsody.PauseInsertion(u);
        FliteProsody.Intonation(u);
        CmuPostlex.Run(u);
        FliteProsody.Duration(u);
        cancellation.ThrowIfCancellationRequested();
        LimitCheck(u, options);
        FliteProsody.F0Model(u, FliteEnglishData.Instance.F0Terms);

        // diphone_synth: the segments and the targets of the utterance
        List<Item> segmentItems = [];
        List<FliteSegmentInput> segments = [];
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
        {
            segmentItems.Add(s);
            segments.Add(new FliteSegmentInput(
                Hrg.Name(s), Hrg.FeatFloat(s, "end"), FeaturePath.FfeatureString(s, "ph_vc"), FeaturePath.FfeatureString(s, "R:SylStructure.n.ph_vc")));
        }

        List<FliteTarget> targets = [];
        for (Item? t = u.GetRelation("Target").Head; t is not null; t = Hrg.Next(t))
        {
            targets.Add(new FliteTarget(Hrg.FeatFloat(t, "pos"), Hrg.FeatFloat(t, "f0")));
        }

        FliteSynthResult made = FliteSynthesizer.Synthesize(voice, segments, targets, cancellation);
        if (made.Samples.Length > options.MaxSamples)
        {
            throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
        }

        return Describe(u, byteText, cursorStart, out cursorEnd, segmentItems, made);
    }

    /// <summary>Refuses a text whose durations make a waveform above the limit, before the synthesis allocates for it: the last segment end is the length in seconds.</summary>
    private void LimitCheck(Utterance u, FliteOptions options)
    {
        Item? last = u.GetRelation("Segment").Tail;
        if (last is null || !Hrg.FeatPresent(last, "end"))
        {
            return;
        }

        double samples = Hrg.FeatFloat(last, "end") * (double)voice.SampleRate;
        if (!(samples <= options.MaxSamples))
        {
            throw new SpeechResourceLimitException($"The speech is longer than {options.MaxSamples} samples.");
        }
    }

    private static FliteRenderResult Describe(Utterance u, string byteText, int cursorStart, out int cursorEnd, List<Item> segmentItems, FliteSynthResult made)
    {
        // the words, with the token of every word
        List<Item> wordItems = [];
        Dictionary<Item, int> wordIndex = new(ReferenceEqualityComparer.Instance);
        Dictionary<Item, int> tokenIndex = new(ReferenceEqualityComparer.Instance);
        int k = 0;
        for (Item? t = u.GetRelation("Token").Head; t is not null; t = Hrg.Next(t)) tokenIndex[t] = k++;
        // the position of every token in the text
        Dictionary<Item, int> tokenStart = TokenStarts(u, byteText, cursorStart, out cursorEnd);
        List<FliteWordInfo> words = [];
        for (Item? w = u.GetRelation("Word").Head; w is not null; w = Hrg.Next(w))
        {
            wordIndex[w] = wordItems.Count;
            wordItems.Add(w);
            Item? token = Hrg.Parent(Hrg.ItemAs(w, "Token"));
            int index = token is not null && tokenIndex.TryGetValue(token, out int found) ? found : -1;
            string tokenName = token is null ? string.Empty : ByteText.ToText(Hrg.Name(token));
            int start = token is not null && tokenStart.TryGetValue(token, out int startAt) ? startAt : -1;
            words.Add(new FliteWordInfo(ByteText.ToText(Hrg.Name(w)), index, tokenName, start));
        }

        // the segments, with the word of every segment: segment -> syllable -> word in the SylStructure relation; a silence has no place there
        List<FliteSegmentInfo> segments = new(segmentItems.Count);
        for (int i = 0; i < segmentItems.Count; i++)
        {
            Item? syllable = Hrg.Parent(Hrg.ItemAs(segmentItems[i], "SylStructure"));
            Item? word = syllable is null ? null : Hrg.Parent(Hrg.ItemAs(syllable, "SylStructure"));
            int w = word is not null && wordIndex.TryGetValue(Hrg.ItemAs(word, "Word") ?? word, out int found) ? found : -1;
            segments.Add(new FliteSegmentInfo(Hrg.Name(segmentItems[i]), made.SegmentEnds[i], w));
        }

        return new FliteRenderResult { Samples = made.Samples, SampleRate = made.SampleRate, Segments = segments, Words = words, MissingDiphones = made.MissingDiphones };
    }
}
