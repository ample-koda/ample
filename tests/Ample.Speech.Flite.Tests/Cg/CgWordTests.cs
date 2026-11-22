using System.Globalization;
using System.Security.Cryptography;
using Ample.Speech.Flite.Cg;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Milestone C9 of the clustergen voices: one word on one steady pitch. For slt and awb the lever is the one of the C program (<c>int_f0_target_mean</c> with a deviation of 0): the
/// samples equal the pinned program word by word and pitch by pitch. For rms, whose F0 is written by <c>cst_spamf0</c> after that scaling, the pitch is put on the frames afterwards
/// (not in C), and what is tested is the pitch that comes out. Measured ranges: plan/speech/flite/cg-words.md.</summary>
public sealed class CgWordTests
{
    private static readonly Dictionary<string, FliteCgEngine> Engines = [];

    private static FliteCgEngine Engine(string voice)
    {
        lock (Engines)
        {
            if (!Engines.TryGetValue(voice, out FliteCgEngine? engine)) Engines[voice] = engine = new FliteCgEngine(FliteCgVoiceData.Embedded(voice));
            return engine;
        }
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
    [InlineData("awb")]
    public void A_word_on_a_steady_pitch_equals_the_pinned_program(string voice)
    {
        string[] words = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flite-cg-words.txt"));
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"flite-cg-words-{voice}.tsv"));
        Assert.Equal(words.Length * 12, expected.Length);
        List<string> differences = [];
        foreach (string line in expected)
        {
            string[] parts = line.Split('\t');
            float pitch = float.Parse(parts[0], CultureInfo.InvariantCulture);
            string word = words[int.Parse(parts[1], CultureInfo.InvariantCulture) - 1];
            FliteRenderResult made = Engine(voice).RenderText(word + "\n", new FliteCgOptions { F0Mean = pitch, F0Stddev = 0f, DurationStretch = 1f });
            string mine = made.Samples.Length == 0 ? "EMPTY" : $"OK {made.Samples.Length} {Sha(made.Samples)}";
            string theirs = parts.Length == 4 ? $"OK {parts[2]} {parts[3]}" : parts[2];
            if (mine != theirs) differences.Add($"{word} at {pitch}: expected [{theirs}] got [{mine}]");
        }

        Assert.True(differences.Count == 0, $"{differences.Count} words differ; first: {(differences.Count > 0 ? differences[0] : string.Empty)}");
    }

    /// <summary>The pitch of a waveform: the median of the normalised autocorrelation pitch of frames of 40 ms (as plan/speech/flite/reference/x1/x1a/track.mjs measures), in Hz; 0 when no frame is voiced.</summary>
    private static double MedianPitch(short[] x, int rate, double target)
    {
        int len = (int)(0.04 * rate), hop = (int)(0.01 * rate);
        int loLag = Math.Max(2, (int)(rate / (target * 1.7))), hiLag = (int)Math.Ceiling(rate / (target * 0.6));
        List<double> found = [];
        for (int start = 0; start + len + hiLag < x.Length; start += hop)
        {
            double e0 = 0;
            for (int i = 0; i < len; i++) e0 += (double)x[start + i] * x[start + i];
            if (e0 < 1e6) continue;
            double[] r = new double[hiLag + 2];
            for (int lag = loLag - 1; lag <= hiLag + 1; lag++)
            {
                double c = 0, e1 = 0;
                for (int i = 0; i < len; i++)
                {
                    c += (double)x[start + i] * x[start + i + lag];
                    e1 += (double)x[start + i + lag] * x[start + i + lag];
                }

                r[lag] = c / Math.Sqrt((e0 * e1) + 1e-9);
            }

            int best = loLag;
            for (int lag = loLag; lag <= hiLag; lag++) if (r[lag] > r[best]) best = lag;
            if (r[best] < 0.6) continue;
            double a = r[best - 1], b = r[best], cc = r[best + 1], d = a - (2 * b) + cc;
            found.Add(rate / (best + (d != 0 ? 0.5 * (a - cc) / d : 0)));
        }

        if (found.Count == 0) return 0;
        found.Sort();
        return found[found.Count / 2];
    }

    [Theory]
    [InlineData("slt", 100)]
    [InlineData("slt", 220)]
    [InlineData("slt", 440)]
    [InlineData("rms", 100)]
    [InlineData("rms", 220)]
    [InlineData("rms", 400)]
    [InlineData("awb", 130)]
    [InlineData("awb", 220)]
    [InlineData("awb", 440)]
    public void A_word_is_sung_on_its_pitch_within_a_few_cents(string voice, double hz)
    {
        foreach (string word in new[] { "one", "music", "hello", "again" })
        {
            FliteCgWordResult made = Engine(voice).RenderWord(word, (float)hz);
            short[] span = made.Samples[made.WordStartSample..made.WordEndSample];
            Assert.True(span.Length > 2000, $"{word} has a body ({span.Length} samples)");
            double measured = MedianPitch(span, made.SampleRate, hz);
            double cents = 1200 * Math.Log2(measured / hz);
            Assert.True(Math.Abs(cents) < 30, $"{voice} {word} at {hz} Hz measured {measured:0.0} Hz ({cents:0} cents)");
            Assert.False(FliteSpeechEngine.HasWrapAround(span), $"{voice} {word} at {hz} Hz wraps around");
        }
    }

    [Fact]
    public void The_word_has_its_place_in_the_waveform_and_the_pitch_is_kept_in_the_range_of_the_voice()
    {
        FliteCgWordResult made = Engine("slt").RenderWord("music", 30f);
        Assert.Equal(FliteCgEngine.MinF0Hz, made.EffectiveF0Hz);
        Assert.True(made.F0Clamped);
        Assert.InRange(made.WordStartSample, 1, made.Samples.Length);
        Assert.InRange(made.WordEndSample, made.WordStartSample, made.Samples.Length);
        Assert.Equal(700f, Engine("awb").RenderWord("music", 900f).EffectiveF0Hz);
        Assert.Throws<ArgumentException>(() => Engine("slt").RenderWord("two words", 200f));
        Assert.Throws<ArgumentException>(() => Engine("slt").RenderWord("one", 0f));
        Assert.Throws<ArgumentException>(() => Engine("slt").RenderWord("one", 200f, -1f));
    }

    [Fact]
    public void The_steady_pitch_of_rms_does_nothing_unless_it_is_asked_for()
    {
        FliteCgEngine rms = Engine("rms");
        short[] plain = rms.Render("music").Samples;
        short[] steady = rms.Render("music", new FliteCgOptions { SteadyF0Hz = 220f }).Samples;
        Assert.NotEqual(plain, steady);
        Assert.Equal(steady, rms.Render("music", new FliteCgOptions { SteadyF0Hz = 220f }).Samples);   // deterministic
    }

    [Fact]
    public void The_ranges_of_the_voices_are_wide_enough_to_fold_every_note_into_them()
    {
        foreach (string voice in FliteSpeechEngine.Voices)
        {
            double min = FliteSpeechEngine.WordsMinPitchHzOf(voice), max = FliteSpeechEngine.WordsMaxPitchHzOf(voice);
            Assert.True(max >= 2 * min, $"{voice}: the range {min} to {max} Hz is less than an octave, a note could not be folded into it");
            for (double hz = 20; hz < 5000; hz *= 1.03)
            {
                (double folded, bool moved) = FliteSpeechEngine.FoldPitch(hz, voice);
                Assert.InRange(folded, min, max);
                Assert.Equal(hz != folded, moved);
                Assert.Equal(0, Math.Round(Math.Log2(folded / hz)) - Math.Log2(folded / hz), 9);   // whole octaves only
            }
        }
    }
}
