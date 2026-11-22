using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ample.Speech.Flite.Cg;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Milestone C7 of the clustergen voices: texts of several sentences equal the pinned program (<c>flite_file_to_speech</c>, one fresh process for each text), and the engine keeps its limits,
/// stops when it is cancelled and does not crash on odd input.</summary>
public sealed class CgEngineTests
{
    private static readonly Dictionary<string, FliteCgEngine> Engines = [];

    private static FliteCgEngine Engine(string voice)
    {
        lock (Engines)
        {
            if (!Engines.TryGetValue(voice, out FliteCgEngine? engine))
            {
                engine = new FliteCgEngine(FliteCgVoiceData.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cg", $"flite-cg-{voice}.bin")));
                Engines[voice] = engine;
            }

            return engine;
        }
    }

    private static byte[][] Texts()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flite-m8-sentences.txt.gz"));
        using GZipStream zip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(zip, Encoding.UTF8);
        List<byte[]> lines = [];
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine()) lines.Add(Unescape(line));
        return [.. lines];
    }

    /// <summary>The texts of the corpus escape a byte as \xHH and a backslash as two (the same as the dumper reads).</summary>
    private static byte[] Unescape(string line)
    {
        List<byte> all = [];
        for (int i = 0; i < line.Length;)
        {
            if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '\\')
            {
                all.Add((byte)'\\');
                i += 2;
            }
            else if (line[i] == '\\' && i + 3 < line.Length && line[i + 1] == 'x' && Uri.IsHexDigit(line[i + 2]) && Uri.IsHexDigit(line[i + 3]))
            {
                all.Add(byte.Parse(line.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 4;
            }
            else
            {
                all.AddRange(Encoding.UTF8.GetBytes(line[i].ToString()));
                i++;
            }
        }

        int stop = all.IndexOf(0);   // a zero byte ends a C string
        if (stop >= 0) all.RemoveRange(stop, all.Count - stop);
        return [.. all];
    }

    private static string Sha(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)samples[i];
            bytes[(2 * i) + 1] = (byte)(samples[i] >> 8);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    [Theory]
    [InlineData("slt")]
    [InlineData("rms")]
    [InlineData("awb")]
    public void Texts_of_several_sentences_equal_the_pinned_program(string voice)
    {
        // every fifth text of the corpus of 501 (the whole corpus: set FLITE_CG_ALL_TEXTS=1; it takes about ten minutes for each voice)
        int step = Environment.GetEnvironmentVariable("FLITE_CG_ALL_TEXTS") == "1" ? 1 : 5;
        byte[][] texts = Texts();
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"flite-cg-file-{voice}.tsv"));
        Assert.Equal(texts.Length, expected.Length);
        List<string> differences = [];
        for (int k = 0; k < texts.Length; k += step)
        {
            string[] parts = expected[k].Split('\t');
            Assert.Equal(k + 1, int.Parse(parts[0], CultureInfo.InvariantCulture));
            string mine;
            try
            {
                FliteRenderResult result = Engine(voice).RenderText(texts[k]);
                mine = result.Samples.Length == 0 ? "EMPTY" : $"OK {result.Samples.Length} {result.SampleRate} {Sha(result.Samples)}";
            }
            catch (FliteRenderException)
            {
                mine = "CRASH";
            }

            string theirs = parts[1] == "OK" ? $"OK {parts[2]} {parts[3]} {parts[4]}" : parts[1];
            if (parts[1] == "CRASH" ? mine != "CRASH" : mine != theirs) differences.Add($"text {k + 1}: expected [{theirs}] got [{mine}]");
        }

        Assert.True(differences.Count == 0, $"{differences.Count} texts differ; first: {(differences.Count > 0 ? differences[0] : string.Empty)}");
    }

    [Fact]
    public void A_single_utterance_render_equals_the_plan_and_has_places_for_its_words()
    {
        FliteCgEngine engine = Engine("slt");
        FliteRenderResult result = engine.Render("Hello, world.");
        FliteCgPlan plan = engine.Plan(Encoding.UTF8.GetBytes("Hello, world."), null, CancellationToken.None);
        Assert.Equal(plan.Samples, result.Samples);
        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(["hello", "world"], result.Words.Select(w => w.Spoken));
        int previous = 0;
        foreach (FliteSegmentInfo segment in result.Segments)
        {
            Assert.InRange(segment.EndSample, previous, result.Samples.Length);
            previous = segment.EndSample;
        }

        Assert.Equal(result.Samples.Length, result.Segments[^1].EndSample);   // the last segment is the pause after the last word
    }

    [Fact]
    public void The_options_change_the_speed_and_the_pitch()
    {
        FliteCgEngine engine = Engine("slt");
        short[] normal = engine.Render("The quick brown fox.").Samples;
        short[] slow = engine.Render("The quick brown fox.", new FliteCgOptions { DurationStretch = 2f }).Samples;
        Assert.InRange(slow.Length / (double)normal.Length, 1.8, 2.2);
        short[] high = engine.Render("The quick brown fox.", new FliteCgOptions { F0Mean = 250f }).Samples;
        Assert.NotEqual(normal, high);
    }

    [Fact]
    public void The_limits_of_text_and_waveform_are_kept()
    {
        FliteCgEngine engine = Engine("awb");
        Assert.Throws<SpeechResourceLimitException>(() => engine.Render("hello", new FliteCgOptions { MaxTextBytes = 3 }));
        Assert.Throws<SpeechResourceLimitException>(() => engine.Render("This sentence needs more than one second of speech.", new FliteCgOptions { MaxSamples = 16000 }));
        Assert.Throws<SpeechResourceLimitException>(() => engine.RenderText("One sentence. Another sentence. And a third one.", new FliteCgOptions { MaxSamples = 24000 }));
    }

    [Fact]
    public void A_cancelled_render_stops()
    {
        FliteCgEngine engine = Engine("rms");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.Render("hello", null, cancelled.Token));
        using CancellationTokenSource later = new();
        later.CancelAfter(50);
        string longText = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 200));
        Stopwatch watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.RenderText(longText, null, later.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the render took {watch.Elapsed.TotalSeconds:0.0} s to stop");
    }

    [Fact]
    public void Odd_input_never_crashes_the_engine()
    {
        FliteCgEngine engine = Engine("slt");
        Random random = new(20261121);
        string[] odd =
        [
            "", " ", "\n\n", "\0", "a", "...", "1,,000kg", "\u00fcber", new string('x', 3000), "a-" + string.Concat(Enumerable.Repeat("a-", 600)),
            "\ud83d\ude00 emoji", "@@@ ### $$$", "12:30 3/4 1st 2nd 3rd 10th", "Dr. Smith vs. Mrs. Jones, Inc."
        ];
        List<string> inputs = [.. odd];
        for (int i = 0; i < 40; i++)
        {
            StringBuilder text = new();
            int length = random.Next(1, 300);
            for (int j = 0; j < length; j++) text.Append((char)random.Next(1, i % 2 == 0 ? 128 : 600));
            inputs.Add(text.ToString());
        }

        foreach (string input in inputs)
        {
            try
            {
                FliteRenderResult result = engine.RenderText(input, new FliteCgOptions { MaxSamples = 16000 * 120 });
                Assert.True(result.Samples.Length >= 0);
            }
            catch (Exception e) when (e is FliteRenderException or SpeechResourceLimitException)
            {
                // refused with a clear error: allowed
            }
        }
    }
}
