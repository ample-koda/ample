using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Xunit.Abstractions;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>The public behaviour of <see cref="FliteEngine"/> around the parity: options, the description of the result, limits, cancellation, thread safety, robustness and speed.</summary>
public class FliteEngineTests(ITestOutputHelper output)
{
    private static FliteEngine Engine => FliteEngine.Default;

    [Fact]
    public void The_options_change_the_sound_like_they_do_in_the_c_program()
    {
        // four sets of (duration_stretch, F0 mean, F0 deviation) on 78 texts of the corpus, 312 renderings, compared with the C program set to the same values
        List<byte[]> texts = [];
        using (GZipStream gz = new(File.OpenRead(EndToEndFixtures.Path("flite-m7-options-texts.txt.gz")), CompressionMode.Decompress))
        using (StreamReader reader = new(gz, Encoding.Latin1))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                texts.Add(EndToEndFixtures.Unescape(line));
            }
        }

        Assert.Equal(78, texts.Count);
        List<string> failures = [];
        int compared = 0;
        using GZipStream rows = new(File.OpenRead(EndToEndFixtures.Path("flite-m7-options.tsv.gz")), CompressionMode.Decompress);
        using StreamReader refs = new(rows, Encoding.Latin1);
        string? row;
        while ((row = refs.ReadLine()) is not null)
        {
            string[] f = row.Split('\t');
            string[] set = f[0].Split(' ');
            FliteOptions options = new()
            {
                DurationStretch = float.Parse(set[0], CultureInfo.InvariantCulture),
                F0Mean = float.Parse(set[1], CultureInfo.InvariantCulture),
                F0Stddev = float.Parse(set[2], CultureInfo.InvariantCulture),
            };
            int index = int.Parse(f[1], CultureInfo.InvariantCulture) - 1;
            compared++;
            if (f[2] == "CRASH")
            {
                Assert.Throws<FliteRenderException>(() => Engine.Render(texts[index], options));
                continue;
            }

            FliteRenderResult result = Engine.Render(texts[index], options);
            if (f[2] == "EMPTY")
            {
                if (result.Samples.Length != 0) failures.Add($"{f[0]} #{index + 1}: {result.Samples.Length} samples, reference empty");
            }
            else if (result.Samples.Length != int.Parse(f[3], CultureInfo.InvariantCulture) || EndToEndFixtures.Sha256(result.Samples) != f[5])
            {
                failures.Add($"{f[0]} #{index + 1} [{EndToEndFixtures.Printable(texts[index], 50)}]: {result.Samples.Length} samples, reference {f[3]}");
            }
        }

        Assert.Equal(312, compared);
        Assert.True(failures.Count == 0, $"{failures.Count} differences, first ones:\n{string.Join('\n', failures.Take(10))}");
    }

    [Fact]
    public void Slower_speech_is_longer_and_a_higher_pitch_changes_the_waveform()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        int normal = Engine.Render(text).Samples.Length;
        int slow = Engine.Render(text, new FliteOptions { DurationStretch = 1.8f }).Samples.Length;
        int fast = Engine.Render(text, new FliteOptions { DurationStretch = 0.6f }).Samples.Length;
        Assert.True(fast < normal && normal < slow, $"{fast} < {normal} < {slow}");
        Assert.NotEqual(EndToEndFixtures.Sha256(Engine.Render(text).Samples), EndToEndFixtures.Sha256(Engine.Render(text, new FliteOptions { F0Mean = 180 }).Samples));
        Assert.Equal(EndToEndFixtures.Sha256(Engine.Render(text).Samples), EndToEndFixtures.Sha256(Engine.Render(text, new FliteOptions()).Samples));
    }

    [Fact]
    public void The_result_says_which_word_every_segment_belongs_to_and_where_it_ends()
    {
        FliteRenderResult r = Engine.Render("Hello world.");
        Assert.Equal(16000, r.SampleRate);
        Assert.Equal(["Hello", "world"], r.Words.Select(w => w.Token.TrimEnd('.')).ToArray());
        Assert.Equal(["hello", "world"], r.Words.Select(w => w.Spoken).ToArray());
        Assert.Equal("pau", r.Segments[0].Phone);
        Assert.Equal(-1, r.Segments[0].WordIndex);
        Assert.Equal("pau", r.Segments[^1].Phone);
        Assert.Equal(r.Samples.Length, r.Segments[^1].EndSample);
        Assert.True(r.Segments.Zip(r.Segments.Skip(1)).All(p => p.First.EndSample <= p.Second.EndSample), "segment ends do not go back");
        Assert.Equal([0, 1], r.Segments.Select(s => s.WordIndex).Where(i => i >= 0).Distinct().ToArray());
        Assert.Equal([0, 0], [r.Words[0].TokenStart, r.Words[0].TokenIndex]);
        Assert.Equal([6, 1], [r.Words[1].TokenStart, r.Words[1].TokenIndex]);
    }

    [Fact]
    public void A_number_gives_several_words_of_one_token()
    {
        FliteRenderResult r = Engine.Render("I have 125 apples");
        Assert.Equal(["I", "have", "125", "125", "125", "125", "apples"], r.Words.Select(w => w.Token).ToArray());
        Assert.Equal(["i", "have", "one", "hundred", "twenty", "five", "apples"], r.Words.Select(w => w.Spoken).ToArray());
        Assert.Equal([0, 1, 2, 2, 2, 2, 3], r.Words.Select(w => w.TokenIndex).ToArray());
    }

    [Fact]
    public void An_empty_text_gives_no_samples_as_in_the_c_program()
    {
        foreach (string text in new[] { string.Empty, " ", "\t\n", "..." })
        {
            FliteRenderResult r = Engine.Render(text);
            Assert.Empty(r.Samples);
        }
    }

    [Fact]
    public void The_limits_refuse_before_the_work_is_done()
    {
        Assert.Throws<SpeechResourceLimitException>(() => Engine.Render("hello", new FliteOptions { MaxTextBytes = 4 }));
        Assert.Throws<SpeechResourceLimitException>(() => Engine.Render("hello world, how are you today", new FliteOptions { MaxSamples = 1000 }));
        FliteRenderResult small = Engine.Render("hi", new FliteOptions { MaxSamples = 16000 * 5 });
        Assert.NotEmpty(small.Samples);
    }

    [Fact]
    public void A_text_the_c_program_cannot_handle_fails_with_a_clear_error()
    {
        FliteRenderException e = Assert.Throws<FliteRenderException>(() => Engine.Render("1,,000kg"));
        Assert.Contains("cannot be spoken", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_stops_the_work()
    {
        using CancellationTokenSource already = new();
        already.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Engine.Render("hello", null, already.Token));

        byte[] longText = Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Repeat("The old miller carried twelve loaves of bread across the field.", 150)));
        using CancellationTokenSource soon = new(TimeSpan.FromMilliseconds(1));
        Assert.ThrowsAny<OperationCanceledException>(() => Engine.Render(longText, null, soon.Token));
    }

    [Fact]
    public void Renderings_on_many_threads_give_the_same_samples()
    {
        const string text = "Twenty-five thousand people walked across the bridge in 1887.";
        string expected = EndToEndFixtures.Sha256(Engine.Render(text).Samples);
        string[] hashes = new string[16];
        Parallel.For(0, hashes.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => hashes[i] = EndToEndFixtures.Sha256(Engine.Render(text).Samples));
        Assert.All(hashes, h => Assert.Equal(expected, h));
    }

    [Fact]
    public void Random_input_never_crashes_hangs_or_exceeds_the_limits()
    {
        Random random = new(20261120);
        string[] vocabulary =
        [
            "the", "a", "of", "1", "12", "1,000", "$5.50", "3rd", "Dr.", "Mr", "e.g.", "U.S.A.", "10:30", "1999", "x", "ab", "qwerty", "zzyzx",
            "--", "...", "\"", "(", ")", ",", ";", "!", "?",
        ];
        FliteOptions options = new() { MaxSamples = 16000 * 120 };
        Stopwatch total = Stopwatch.StartNew();
        int ok = 0, refused = 0;
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("FLITE_FUZZ_ITERATIONS"), out int configured) ? configured : 100;
        for (int i = 0; i < iterations; i++)
        {
            byte[] input = (i % 4) switch
            {
                0 => RandomBytes(random, random.Next(0, 65537)),
                1 => RandomAscii(random, random.Next(0, 4000)),
                2 => Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Range(0, random.Next(0, 600)).Select(_ => vocabulary[random.Next(vocabulary.Length)]))),
                _ => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, random.Next(0, 300)).Select(_ => char.ConvertFromUtf32(random.Next(0x20, 0x2fff))))),
            };
            using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(60));
            try
            {
                FliteRenderResult r = Engine.Render(input, options, watchdog.Token);
                Assert.True(r.Segments.Count == 0 || r.Segments[^1].EndSample == r.Samples.Length);
                ok++;
            }
            catch (FliteRenderException)
            {
                refused++;
            }
            catch (SpeechResourceLimitException)
            {
                refused++;
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"input {i} ({input.Length} bytes) took longer than 60 seconds");
            }
        }

        output.WriteLine($"{iterations} random inputs: {ok} spoken, {refused} refused cleanly, {total.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(iterations, ok + refused);
    }

    [Fact]
    public void Tokens_that_split_into_thousands_of_parts_fail_cleanly_and_do_not_overflow_the_stack()
    {
        // every one of these is split into smaller tokens that are expanded the same way; the C program recurses as deep as its stack allows, the port stops at a fixed nesting
        // and runs here on a thread with the smallest stack that is common (1 MiB), where a stack overflow would end the whole test run
        string[] pieces = ["a-", "a/", "1a", "a1", "a'", "A.", "a_", "1,", "$1", "a%", "a's", "a-1"];
        List<string> outcomes = [];
        Thread thread = new(() =>
        {
            foreach (string piece in pieces)
            {
                string text = string.Concat(Enumerable.Repeat(piece, 4000));
                try
                {
                    FliteRenderResult r = Engine.Render(text, new FliteOptions { MaxSamples = 16000 * 3600 });
                    outcomes.Add($"{piece}: spoken {r.Samples.Length}");
                }
                catch (FliteRenderException)
                {
                    outcomes.Add($"{piece}: refused");
                }
                catch (SpeechResourceLimitException)
                {
                    outcomes.Add($"{piece}: limit");
                }
            }
        }, 1024 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "finished");
        Assert.Equal(pieces.Length, outcomes.Count);
        output.WriteLine(string.Join(", ", outcomes));
    }

    [Fact]
    public void A_long_text_is_fast_enough()
    {
        // text 6879 of the corpus has 500 tokens; text 6892 has 1600
        List<byte[]> texts = EndToEndFixtures.Corpus();
        foreach (int index in new[] { 6878, 6891 })
        {
            byte[] text = texts[index];
            Engine.Render(text);   // warm up: data, lexicon, JIT
            long before = GC.GetTotalAllocatedBytes(true);
            Stopwatch watch = Stopwatch.StartNew();
            FliteRenderResult r = Engine.Render(text);
            watch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            double seconds = r.Samples.Length / 16000.0;
            double took = watch.Elapsed.TotalSeconds;
            double mebibytes = allocated / (1024.0 * 1024.0);
            output.WriteLine($"text {index + 1}: {text.Length} bytes, {r.Words.Count} words, {seconds:F1} s of speech in {took:F2} s");
            output.WriteLine($"  real-time factor {took / seconds:F3}, allocated {mebibytes:F1} MiB");
            Assert.True(watch.Elapsed.TotalSeconds / seconds < 1.0, "faster than real time");
        }
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        int nul = Array.IndexOf(bytes, (byte)0);
        return nul >= 0 ? bytes[..nul] : bytes;
    }

    private static byte[] RandomAscii(Random random, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(random.Next(0, 8) == 0 ? random.Next(32, 127) : random.Next(97, 123));
        }

        return bytes;
    }
}
