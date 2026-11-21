using System.Collections.Concurrent;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// The splitting of a text into utterances (<c>flite_ts_to_speech</c> with <c>default_utt_break</c>, the path of <c>flite -f</c>):
/// <see cref="FliteEngine.RenderText(string, FliteOptions?, CancellationToken)"/>
/// gives the same number of samples and the same SHA-256 as the pinned C program (<c>flite_file_to_speech</c>, <c>reference/m8</c>) on 501 multi-sentence texts, and
/// <see cref="FliteEngine.Render(string, FliteOptions?, CancellationToken)"/> still gives the one utterance of <c>flite -t</c> (<c>flite_synth_text</c>) on the same texts.
/// </summary>
public class FliteSentenceSplitTests
{
    private static readonly Lazy<List<byte[]>> Texts = new(EndToEndFixtures.SentenceCorpus);
    private static readonly Lazy<List<EndToEndFixtures.Reference>> FileReferences = new(() => EndToEndFixtures.References("flite-m8-file-pcm.tsv.gz"));
    private static readonly Lazy<List<EndToEndFixtures.Reference>> TextReferences = new(() => EndToEndFixtures.References("flite-m8-text-pcm.tsv.gz"));

    [Fact]
    public void The_corpus_and_the_references_have_the_same_number_of_lines()
    {
        Assert.Equal(501, Texts.Value.Count);
        Assert.Equal(Texts.Value.Count, FileReferences.Value.Count);
        Assert.Equal(Texts.Value.Count, TextReferences.Value.Count);
        Assert.True(FileReferences.Value.Count(r => r.Status == "OK") > 480);
        // the splitting matters: most multi-sentence texts sound different in file mode than as one utterance
        Assert.True(FileReferences.Value.Zip(TextReferences.Value).Count(p => p.First.Sha256 != p.Second.Sha256) > 400);
    }

    [Fact]
    public void Every_text_gives_the_samples_of_flite_file_to_speech()
    {
        Compare(Texts.Value, FileReferences.Value, (text) => FliteEngine.Default.RenderText(text));
    }

    [Fact]
    public void Render_stays_the_one_utterance_of_flite_synth_text()
    {
        Compare(Texts.Value, TextReferences.Value, (text) => FliteEngine.Default.Render(text));
    }

    [Fact]
    public void The_segments_and_words_of_the_utterances_are_placed_in_the_waveform_and_in_the_text()
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes("Alice walked the dog. Then Bob left! Is it over? Yes.");
        FliteRenderResult result = FliteEngine.Default.RenderText(text);
        Assert.Equal(result.Samples.Length, result.Segments[^1].EndSample);
        Assert.True(result.Segments.Zip(result.Segments.Skip(1)).All(p => p.First.EndSample <= p.Second.EndSample), "the ends of the segments never go back");
        // the last token of the text is not spoken: it starts a new utterance at the end of the text, and flite_ts_to_speech stops there (as the C program does)
        string[] spoken = [.. result.Words.Select(w => w.Spoken)];
        Assert.Equal(["alice", "walked", "the", "dog", "then", "bob", "left", "is", "it", "over"], spoken);
        // every word is at its token in the text: the offsets keep running over the utterances
        foreach (FliteWordInfo word in result.Words)
        {
            Assert.True(word.TokenStart >= 0, word.Token);
            Assert.Equal(word.Token, System.Text.Encoding.UTF8.GetString(text, word.TokenStart, word.Token.Length));
        }

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], result.Words.Select(w => w.TokenIndex).ToArray());
        Assert.True(result.Segments.All(s => s.WordIndex < result.Words.Count));
        Assert.Equal(Enumerable.Range(0, result.Words.Count), result.Segments.Select(s => s.WordIndex).Where(i => i >= 0).Distinct().Order());
    }

    [Fact]
    public void The_sample_limit_counts_all_utterances_together()
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes("Alice walked the dog. Then Bob left! Is it over? Yes.");
        int whole = FliteEngine.Default.RenderText(text).Samples.Length;
        // the limit is checked against the duration model before the synthesis, so a small margin is needed above the real length
        Assert.Throws<SpeechResourceLimitException>(() => FliteEngine.Default.RenderText(text, new FliteOptions { MaxSamples = whole / 2 }));
        Assert.Equal(whole, FliteEngine.Default.RenderText(text, new FliteOptions { MaxSamples = whole * 2 }).Samples.Length);
    }

    [Fact]
    public void Cancelling_stops_the_work_between_utterances()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => FliteEngine.Default.RenderText("One. Two. Three.", null, source.Token));
    }

    [Theory]
    [InlineData("Hello. World. Again now", "Hello.|World.|Again now")]
    [InlineData("Hello. world. again", "Hello. world. again")]
    [InlineData("Mr. Smith left. Then he came back", "Mr. Smith left.|Then he came back")]
    [InlineData("It is U.S.A. Then it is not", "It is U.S.A. Then it is not")]
    [InlineData("Stop: go on", "Stop:|go on")]
    [InlineData("First line\n\nSecond line", "First line|Second line")]
    [InlineData("First line\nSecond line", "First line Second line")]
    [InlineData("Yahoo! and so on. Now", "Yahoo! and so on.")]
    [InlineData("Really? yes", "Really?")]
    [InlineData("Hello. World.", "Hello.")]
    [InlineData("", "")]
    public void The_text_is_cut_where_default_utt_break_says(string text, string expected)
    {
        List<List<TokenRead>> parts = UtteranceBreaks.Split(ByteText.FromUtf8(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, string.Join('|', parts.Select(p => string.Join(' ', p.Select(t => t.Name + t.Postpunctuation)))));
    }

    [Fact]
    public void An_utterance_has_at_most_501_tokens()
    {
        string text = string.Join(' ', Enumerable.Repeat("and", 1200)) + ".";
        List<List<TokenRead>> parts = UtteranceBreaks.Split(text);
        Assert.Equal([501, 501, 198], parts.Select(p => p.Count));
    }

    private static void Compare(List<byte[]> texts, List<EndToEndFixtures.Reference> references, Func<byte[], FliteRenderResult> render)
    {
        ConcurrentBag<string> failures = [];
        int equal = 0;
        Parallel.For(0, texts.Count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
        {
            EndToEndFixtures.Reference reference = references[i];
            try
            {
                FliteRenderResult result = render(texts[i]);
                if (reference.Status == "CRASH")
                {
                    failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: the C program died (signal {reference.Signal}), the port gives {result.Samples.Length} samples");
                }
                else if (reference.Status == "EMPTY" ? result.Samples.Length != 0 : result.Samples.Length != reference.Samples || EndToEndFixtures.Sha256(result.Samples) != reference.Sha256)
                {
                    string got = EndToEndFixtures.Sha256(result.Samples)[..12];
                    failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: {result.Samples.Length} samples {got}, reference {reference.Status} {reference.Samples}");
                }
                else
                {
                    Interlocked.Increment(ref equal);
                }
            }
            catch (FliteRenderException) when (reference.Status == "CRASH")
            {
                Interlocked.Increment(ref equal);
            }
            catch (Exception e)
            {
                failures.Add($"{i + 1} [{EndToEndFixtures.Printable(texts[i])}]: {e.GetType().Name}: {e.Message} (reference {reference.Status})");
            }
        });

        string[] first = [.. failures.OrderBy(f => int.Parse(f.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).Take(15)];
        Assert.True(failures.IsEmpty, $"{failures.Count} differences ({equal} equal), first ones:\n{string.Join('\n', first)}");
    }
}
