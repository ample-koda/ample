using System.Text;
using Ample.Speech.Flite;

namespace Ample.Speech;

/// <summary>
/// The pipeline of the engine in use: <see cref="FliteEngine"/> (the translation of Flite for the voice kal16), with the spoken words placed in the waveform and in the text.
/// </summary>
internal sealed class FliteDefaultPipeline : IFlitePipeline
{
    /// <inheritdoc />
    public FliteRender Run(string text, FliteSettings settings, CancellationToken cancellationToken)
    {
        FliteOptions options = OptionsOf(settings);
        FliteMarkup? markup = settings.Markup;
        string spoken = markup is null ? text : markup.Plain;   // the tags are not spoken
        RefuseTextBeyondTheLimit(Encoding.UTF8.GetByteCount(spoken), options.MaxSamples);
        FliteRenderResult made;
        try
        {
            // The text is spoken a sentence at a time, as Flite speaks a file. Flite drops the last token of a text when that token starts a new sentence and the text ends right
            // after it (a file normally ends with a new line, which is why that is not seen there), so the text gets the new line that a file has: no word is lost, and nothing is spoken for it.
            made = FliteEngine.Default.RenderText(spoken + "\n", options, cancellationToken);
        }
        catch (FliteRenderException error)
        {
            throw new ArgumentException(error.Message, nameof(text), error);
        }

        List<FliteSpokenWord> words = WordsOf(spoken, made);
        if (markup is null) return new(made.Samples, words);
        // the words stand where they are in the text as it was written, with the tags in it; a pause of the markup comes before the word that is spoken after it
        List<FliteBreak> breaks = [];
        foreach (MarkupBreak mark in markup.Breaks)
        {
            int next = words.FindIndex(w => w.TextFrom >= mark.PlainIndex);
            breaks.Add(new FliteBreak(next < 0 ? words.Count : next, mark.Milliseconds * FliteEngine.Default.SampleRate / 1000));
        }

        List<FliteSpokenWord> inSource = [.. words.Select(w => w with { TextFrom = markup.SourceStart(w.TextFrom), TextTo = markup.SourceEnd(w.TextTo) })];
        return new(made.Samples, inSource, breaks);
    }

    /// <inheritdoc />
    public FliteRender RunPhones(string phones, int sourceLength, FliteSettings settings, CancellationToken cancellationToken)
    {
        FliteOptions options = OptionsOf(settings);
        FliteRenderResult made;
        try
        {
            made = FliteEngine.Default.RenderPhones(phones, options, cancellationToken);
        }
        catch (FliteRenderException error)
        {
            throw new ArgumentException(error.Message, nameof(phones), error);
        }

        // the phones are spoken as one utterance, like the [phonemes] word of the other engines: it sounds over the whole waveform
        return new(made.Samples, made.Samples.Length == 0 ? [] : [new("[phonemes]", 0, sourceLength, 0, made.Samples.Length)]);
    }

    /// <summary>Bytes of text that one second of speech can hold at the most: ordinary speech has about 15 characters a second, this leaves room for numbers, punctuation and white space.</summary>
    internal const int MaxBytesPerSecond = 40;

    /// <summary>
    /// Refuses a text that cannot fit in the waveform that the limit allows, before the text analysis walks through it
    /// (a text of a megabyte takes tens of seconds to analyse and would be refused at the end otherwise).
    /// </summary>
    private static void RefuseTextBeyondTheLimit(int textBytes, int maxSamples)
    {
        long allowed = (long)maxSamples / FliteEngine.Default.SampleRate * MaxBytesPerSecond + 1024;
        if (textBytes > allowed)
        {
            throw new SpeechResourceLimitException($"The text has {textBytes} bytes; the speech that is allowed holds at most {allowed} bytes of text.");
        }
    }

    private static FliteOptions OptionsOf(FliteSettings settings) => new()
    {
        DurationStretch = (float)settings.DurationStretch,
        F0Mean = (float)settings.F0Mean,
        F0Stddev = (float)settings.F0Stddev,
        MaxSamples = settings.MaxSamples > 0 ? (int)Math.Min(settings.MaxSamples, int.MaxValue) : FliteOptions.DefaultMaxSamples,
        UserLexicon = settings.User,
        Markup = settings.Markup
    };

    /// <summary>
    /// Places the words: a word sounds from the start of its first segment to the end of its last (the pauses between words belong to no word), and it stands where its token
    /// stands in the text (a number is several words that share the place of the number).
    /// </summary>
    internal static List<FliteSpokenWord> WordsOf(string text, FliteRenderResult made)
    {
        int[] first = new int[made.Words.Count], last = new int[made.Words.Count];
        Array.Fill(first, -1);
        for (int i = 0; i < made.Segments.Count; i++)
        {
            int word = made.Segments[i].WordIndex;
            if (word < 0 || word >= first.Length) continue;
            if (first[word] < 0) first[word] = i;
            last[word] = i;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        List<FliteSpokenWord> words = [];
        for (int w = 0; w < made.Words.Count; w++)
        {
            if (first[w] < 0) continue;   // a word without sound (nothing of it was spoken) has no place in the waveform
            int start = first[w] == 0 ? 0 : made.Segments[first[w] - 1].EndSample;
            int end = made.Segments[last[w]].EndSample;
            (int from, int to) = TextPlace(text, bytes, made.Words[w]);
            words.Add(new(made.Words[w].Spoken, from, to, start, Math.Max(start, end)));
        }

        return words;
    }

    private static (int From, int To) TextPlace(string text, byte[] bytes, FliteWordInfo word)
    {
        if (word.TokenStart < 0) return (0, 0);
        // the token starts at a byte of the UTF-8 text: count the characters in front of it
        if (word.TokenStart > bytes.Length) return (0, 0);
        int from = Encoding.UTF8.GetCharCount(bytes, 0, word.TokenStart);
        return (from, Math.Min(text.Length, from + word.Token.Length));
    }
}
