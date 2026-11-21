using Xunit.Abstractions;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// Flite X2 and X3 on the real voice: dropping the leading silence and scaling the pauses only removes or adds silence, the words stay sample for sample what the exact render made,
/// and with no option written the output is the exact render. Measured, not judged by ear.
/// </summary>
public class FliteShapingEndToEndTests(ITestOutputHelper output)
{
    private const int Rate = 16_000;   // at the internal rate the resampling is the identity, so the output can be compared with the exact render

    private static readonly string[] Texts =
    [
        "Hello from AMPLE.",
        "Three voices, one song.",
        "All of them written in code.",
        "Press play and listen along.",
        "Music is text, and text is music.",
        "The quick brown fox jumps over the lazy dog, twice. Then it sleeps.",
        "Can you hear it? Yes, I can; it sounds like a small machine.",
        "On 12/25/2024 at 10:45 the price was $1,234.56, a fair price.",
        "One. Two. Three. Four.",
        "Wait... what? Oh, I see!",
    ];

    private static SpeechRequest Request(string text) => new(text, Rate, 1, FliteSpeechEngine.DefaultPitchHz) { Engine = "flite" };

    private static FliteRender Exact(string text) => new FliteDefaultPipeline().Run(text, FliteSpeechEngine.SettingsOf(Request(text)), CancellationToken.None);

    private static int Peak(short[] samples, int from, int to) => from >= to ? 0 : Enumerable.Range(from, to - from).Max(i => Math.Abs((int)samples[i]));

    [Fact]
    public void Without_options_the_output_is_the_exact_render()
    {
        foreach (string text in Texts)
        {
            FliteRender exact = Exact(text);
            SpeechResult plain = FliteSpeechEngine.Render(Request(text));
            SpeechResult explicitDefaults = FliteSpeechEngine.Render(Request(text) with { LeadTrim = false, PauseScale = 1, BrightnessDb = 0 });

            Assert.Equal(exact.Samples, plain.Samples);
            Assert.Equal(plain.Samples, explicitDefaults.Samples);
            Assert.Equal(plain.Words, explicitDefaults.Words);
        }
    }

    [Fact]
    public void Lead_trim_removes_only_quiet_room_tone_and_starts_the_first_word_after_one_millisecond()
    {
        int largestRemoved = 0, removedTotal = 0, latestOnset = 0;
        foreach (string text in Texts)
        {
            FliteRender exact = Exact(text);
            SpeechResult trimmed = FliteSpeechEngine.Render(Request(text) with { LeadTrim = true });
            int removed = exact.Samples.Length - trimmed.Samples.Length;

            Assert.InRange(removed, 1, exact.Words[0].StartSample);
            Assert.Equal(FliteShaping.LeadKeepSamples, trimmed.Words[0].StartSample);
            Assert.Equal(exact.Words.Count, trimmed.Words.Count);
            for (int i = 0; i < exact.Words.Count; i++)
            {
                Assert.Equal(exact.Words[i].StartSample - removed, trimmed.Words[i].StartSample);
                Assert.Equal(exact.Words[i].EndSample - removed, trimmed.Words[i].EndSample);
            }

            largestRemoved = Math.Max(largestRemoved, Peak(exact.Samples, 0, removed));
            removedTotal += removed;
            // everything from the first word on is the exact render, sample for sample
            Assert.Equal(exact.Samples.Skip(exact.Words[0].StartSample), trimmed.Samples.Skip(trimmed.Words[0].StartSample));
            // nothing audible (above -50 dBFS) comes before the first word; the sound itself can start later (the closure of a p or a t is silent)
            int onset = Array.FindIndex(trimmed.Samples, sample => Math.Abs((int)sample) > 100);
            Assert.True(onset >= trimmed.Words[0].StartSample, $"sound before the first word: {text}");
            latestOnset = Math.Max(latestOnset, onset - trimmed.Words[0].StartSample);
        }

        output.WriteLine($"lead_trim: {removedTotal / Texts.Length} samples removed per text on average ({removedTotal / Texts.Length * 1000.0 / Rate:F0} ms), " +
            $"loudest removed sample {largestRemoved} of 32768, the sound starts at most {latestOnset * 1000.0 / Rate:F0} ms after the first word");
        Assert.True(largestRemoved <= 1_000, $"the removed part is not quiet: its loudest sample is {largestRemoved}");   // room tone of the voice, about -38 dBFS, not digital silence
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void Pause_scale_changes_only_the_pauses_between_the_words(double scale)
    {
        int largestGap = 0, largestEdge = 0, gapTotal = 0;
        foreach (string text in Texts)
        {
            FliteRender exact = Exact(text);
            SpeechResult shaped = FliteSpeechEngine.Render(Request(text) with { PauseScale = scale });
            int gaps = 0, expected = exact.Samples.Length;
            for (int i = 1; i < exact.Words.Count; i++)
            {
                int gap = exact.Words[i].StartSample - exact.Words[i - 1].EndSample;
                gaps += gap;
                expected += (int)Math.Round(gap * scale, MidpointRounding.ToEven) - gap;
                int from = exact.Words[i - 1].EndSample, to = exact.Words[i].StartSample, quarter = (to - from) / 4;
                largestGap = Math.Max(largestGap, Peak(exact.Samples, from + quarter, to - quarter));   // the middle half is what a shorter pause drops
                largestEdge = Math.Max(largestEdge, Math.Max(Peak(exact.Samples, from, from + quarter), Peak(exact.Samples, to - quarter, to)));
            }

            gapTotal += gaps;
            Assert.Equal(expected, shaped.Samples.Length);
            for (int i = 0; i < exact.Words.Count; i++)
            {
                int length = exact.Words[i].EndSample - exact.Words[i].StartSample;
                Assert.Equal(length, shaped.Words[i].EndSample - shaped.Words[i].StartSample);
                // the body of a word is untouched, except for the fade that sits in the pause next to it
                Assert.Equal(exact.Samples.Skip(exact.Words[i].StartSample + FliteShaping.FadeSamples).Take(length - 2 * FliteShaping.FadeSamples),
                    shaped.Samples.Skip(shaped.Words[i].StartSample + FliteShaping.FadeSamples).Take(length - 2 * FliteShaping.FadeSamples));
            }
        }

        output.WriteLine($"pause_scale {scale}: {gapTotal / Texts.Length} samples of pause per text ({gapTotal / Texts.Length * 1000.0 / Rate:F0} ms), " +
            $"loudest sample in the middle half of a pause {largestGap}, at its edges {largestEdge} of 32768");
        Assert.True(largestGap <= 1_000, $"the middle of a pause is not quiet: its loudest sample is {largestGap}");   // the pause of the voice is room tone, about -38 dBFS, not digital silence
    }

    [Fact]
    public void Brightness_lifts_the_high_end_of_the_real_voice_and_not_the_low_end()
    {
        const string text = "Three voices, one song. All of them written in code.";
        SpeechResult dull = FliteSpeechEngine.Render(Request(text) with { SampleRate = 22_050 });
        SpeechResult bright = FliteSpeechEngine.Render(Request(text) with { SampleRate = 22_050, BrightnessDb = 6 });

        double high = Band(bright.Samples, 5_000, 8_000) - Band(dull.Samples, 5_000, 8_000);
        double low = Band(bright.Samples, 100, 800) - Band(dull.Samples, 100, 800);
        output.WriteLine($"brightness 6 dB: 5 to 8 kHz {high:+0.0;-0.0} dB, 100 to 800 Hz {low:+0.0;-0.0} dB");
        Assert.InRange(high, 4.5, 6.5);
        Assert.InRange(low, -0.5, 0.5);
        Assert.Equal(dull.Samples.Length, bright.Samples.Length);
    }

    /// <summary>The energy in dB of a band of the signal, from a Goertzel sum on a few frequencies (enough to compare two versions of the same speech).</summary>
    private static double Band(short[] samples, double fromHz, double toHz)
    {
        double energy = 0;
        for (double hz = fromHz; hz <= toHz; hz += (toHz - fromHz) / 12)
        {
            double w = 2 * Math.PI * hz / 22_050, coefficient = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (short sample in samples)
            {
                double s0 = sample + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            energy += s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        }

        return 10 * Math.Log10(energy + 1e-9);
    }
}
