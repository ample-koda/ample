using System.Collections.Concurrent;
using System.Globalization;
using Ample.Speech.Flite.Tests.EndToEnd;

namespace Ample.Speech.Flite.Tests.Word;

/// <summary>
/// <see cref="FliteEngine.RenderWord"/>, one word on a flat pitch (plan/speech/flite/EXTENSIONS.md, X1b). The C program is the reference: <c>reference/x1/run-x1.sh</c> speaks the 2,000 words of
/// <c>flite-x1-words.txt</c> at 12 pitches (stddev 0, stretch 1.1) and 200 of them with two other stretches, and writes the number of samples and the SHA-256 of the samples of each.
/// </summary>
public class FliteWordTests
{
    private sealed record Section(float Mean, float Stretch, int Count, List<EndToEndFixtures.Reference> Lines);

    private static readonly Lazy<string[]> Words = new(() => File.ReadAllLines(EndToEndFixtures.Path("flite-x1-words.txt")).Where(l => l.Length > 0).ToArray());

    private static readonly Lazy<List<Section>> Sections = new(ReadSections);

    private static List<Section> ReadSections()
    {
        List<Section> sections = [];
        List<string> lines = [];
        string? header = null;
        foreach (string line in EndToEndFixtures.ReadGz("flite-x1-pcm.tsv.gz"))
        {
            if (line.StartsWith('#'))
            {
                Close(sections, header, lines);
                header = line.StartsWith("# mean=", StringComparison.Ordinal) ? line : null;
            }
            else if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        Close(sections, header, lines);
        return sections;
    }

    private static void Close(List<Section> sections, string? header, List<string> lines)
    {
        if (header is null)
        {
            lines.Clear();
            return;
        }

        // "# mean=220 sd=0 stretch=1.1 words=2000"
        string[] parts = header.Split(' ');
        float mean = float.Parse(parts[1][5..], CultureInfo.InvariantCulture);
        float stretch = float.Parse(parts[3][8..], CultureInfo.InvariantCulture);
        int count = int.Parse(parts[4][6..], CultureInfo.InvariantCulture);
        List<EndToEndFixtures.Reference> list = [];
        foreach (string line in lines)
        {
            string[] f = line.Split('\t');
            list.Add(f[1] switch
            {
                "OK" => new EndToEndFixtures.Reference("OK", int.Parse(f[2], CultureInfo.InvariantCulture), f[4], 0),
                "EMPTY" => new EndToEndFixtures.Reference("EMPTY", 0, string.Empty, 0),
                _ => new EndToEndFixtures.Reference("CRASH", 0, string.Empty, int.Parse(f[2], CultureInfo.InvariantCulture)),
            });
        }

        sections.Add(new Section(mean, stretch, count, list));
        lines.Clear();
    }

    [Fact]
    public void The_reference_has_every_word_at_every_pitch()
    {
        Assert.Equal(2000, Words.Value.Length);
        Assert.Equal(14, Sections.Value.Count);
        Assert.Equal(12, Sections.Value.Count(s => s.Count == 2000 && s.Stretch == 1.1f));
        foreach (Section s in Sections.Value)
        {
            Assert.Equal(s.Count, s.Lines.Count);
        }
    }

    [Fact]
    public void Every_word_at_every_pitch_gives_the_samples_of_the_c_program()
    {
        string[] words = Words.Value;
        ConcurrentBag<string> failures = [];
        int compared = 0;
        foreach (Section section in Sections.Value)
        {
            Parallel.For(0, section.Count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
            {
                EndToEndFixtures.Reference reference = section.Lines[i];
                Interlocked.Increment(ref compared);
                try
                {
                    FliteWordResult result = FliteEngine.Default.RenderWord(words[i], section.Mean, section.Stretch);
                    string got = EndToEndFixtures.Sha256(result.Samples);
                    if (reference.Status == "CRASH")
                    {
                        failures.Add($"{section.Mean} Hz [{words[i]}]: the C program died, the port gives {result.Samples.Length} samples");
                    }
                    else if (reference.Status == "EMPTY" ? result.Samples.Length != 0 : result.Samples.Length != reference.Samples || got != reference.Sha256)
                    {
                        failures.Add($"{section.Mean} Hz x{section.Stretch} [{words[i]}]: {result.Samples.Length} samples {got[..12]}, reference {reference.Status} {reference.Samples}");
                    }
                }
                catch (FliteRenderException) when (reference.Status == "CRASH")
                {
                    // the C program dies on this word and the port refuses it
                }
                catch (Exception e)
                {
                    failures.Add($"{section.Mean} Hz [{words[i]}]: {e.GetType().Name} {e.Message}");
                }
            });
        }

        Assert.Equal(24_400, compared);
        Assert.True(failures.IsEmpty, $"{failures.Count} differences from the C program, for example: {string.Join(" | ", failures.Take(8))}");
    }

    [Fact]
    public void A_word_is_the_text_of_one_token_with_a_flat_pitch()
    {
        FliteWordResult word = FliteEngine.Default.RenderWord("music", 220f, 1.1f);
        FliteRenderResult text = FliteEngine.Default.Render("music", new FliteOptions { F0Mean = 220f, F0Stddev = 0f, DurationStretch = 1.1f });
        Assert.Equal(text.Samples, word.Samples);
        Assert.Equal(text.Segments, word.Segments);
        Assert.Equal(16_000, word.SampleRate);
    }

    [Fact]
    public void The_word_sits_between_the_pauses_of_the_utterance()
    {
        FliteWordResult r = FliteEngine.Default.RenderWord("music", 220f);
        Assert.Equal("pau", r.Segments[0].Phone);
        Assert.Equal("pau", r.Segments[^1].Phone);
        Assert.Equal(r.Segments[0].EndSample, r.WordStartSample);
        Assert.Equal(r.Segments[^2].EndSample, r.WordEndSample);
        Assert.InRange(r.WordStartSample, 1, r.Samples.Length / 2);
        Assert.InRange(r.WordEndSample, r.Samples.Length / 2, r.Samples.Length - 1);
        Assert.Equal(r.WordEndSample - r.WordStartSample, r.WordLengthSamples);

        // the stretch scales the word: a faster voice speaks it in a shorter time
        FliteWordResult fast = FliteEngine.Default.RenderWord("music", 220f, 0.55f);
        Assert.True(fast.WordLengthSamples < r.WordLengthSamples * 0.7, $"{fast.WordLengthSamples} against {r.WordLengthSamples}");
    }

    [Theory]
    [InlineData(10f, 50f)]
    [InlineData(50f, 50f)]
    [InlineData(220f, 220f)]
    [InlineData(500f, 500f)]
    [InlineData(700f, 500f)]
    public void The_sung_pitch_is_the_request_inside_the_range_of_the_pitch_model(float asked, float sung)
    {
        FliteWordResult r = FliteEngine.Default.RenderWord("one", asked);
        Assert.Equal(asked, r.RequestedF0Hz);
        Assert.Equal(sung, r.EffectiveF0Hz);
        Assert.Equal(asked != sung, r.F0Clamped);

        // a request outside the range sounds as the nearest end of the range
        Assert.Equal(FliteEngine.Default.RenderWord("one", sung).Samples, r.Samples);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData(" yes")]
    [InlineData("yes\n")]
    public void A_word_has_no_white_space(string word)
    {
        Assert.Throws<ArgumentException>(() => FliteEngine.Default.RenderWord(word, 220f));
    }

    [Theory]
    [InlineData(0f, 1.1f)]
    [InlineData(-5f, 1.1f)]
    [InlineData(float.NaN, 1.1f)]
    [InlineData(float.PositiveInfinity, 1.1f)]
    [InlineData(220f, 0f)]
    [InlineData(220f, -1f)]
    [InlineData(220f, float.NaN)]
    public void The_pitch_and_the_stretch_are_numbers_above_zero(float f0, float stretch)
    {
        Assert.Throws<ArgumentException>(() => FliteEngine.Default.RenderWord("yes", f0, stretch));
    }

    [Fact]
    public void A_waveform_above_the_limit_and_a_cancellation_are_refused()
    {
        Assert.Throws<SpeechResourceLimitException>(() => FliteEngine.Default.RenderWord("music", 220f, 1.1f, maxSamples: 100));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FliteEngine.Default.RenderWord("music", 220f, 1.1f, cancellation: cancelled.Token));
    }

    [Fact]
    public void A_number_is_one_token_that_is_spoken_as_several_words()
    {
        FliteWordResult r = FliteEngine.Default.RenderWord("123", 196f);
        Assert.True(r.Words.Count >= 3, "one hundred twenty three");
        Assert.True(r.WordLengthSamples > 0);
    }

    [Fact]
    public void A_token_without_sound_keeps_the_span_inside_the_waveform()
    {
        FliteWordResult empty = FliteEngine.Default.RenderWord("--", 220f);
        Assert.InRange(empty.WordStartSample, 0, empty.Samples.Length);
        Assert.InRange(empty.WordEndSample, empty.WordStartSample, empty.Samples.Length);
    }
}
