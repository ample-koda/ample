using Ample.Audio.Engine;
using Ample.Speech;
using Xunit.Abstractions;

namespace Ample.Engine.Tests;

/// <summary>
/// X3: the loudness offsets of the speech engines (<see cref="SpeechLevels"/>) are measured, not guessed. Twenty plain English sentences are spoken by every engine at its own
/// default pitch and rate at 22,050 Hz, joined with half a second of silence; the integrated loudness (ITU-R BS.1770 with gating, the measure of the master report) of that signal
/// is taken. The test repeats the method of the remarks of <see cref="SpeechLevels"/>: when an engine changes and its loudness moves, this test names the new offset.
/// </summary>
public sealed class SpeechLevelsTests(ITestOutputHelper output)
{
    private const int Rate = 22_050;

    private static readonly string[] Corpus =
    [
        "Hello from AMPLE.", "Three voices, one song.", "All of them written in code.", "Press play and listen along.", "Music is text, and text is music.",
        "The quick brown fox jumps over the lazy dog.", "She sells sea shells by the sea shore.", "Please call Stella and ask her to bring these things with her.",
        "A good sound is a story that you can hear.", "We will meet again at ten o'clock on Friday morning.", "The rain in Spain stays mainly in the plain.",
        "Can you hear the difference between the three voices?", "Every sample you hear was made by the machine.", "Slow down, and listen to the room.",
        "The first one buzzes like a toy.", "The second voice is made of formants.", "The third one learned to read plain text.",
        "No sound file ever left the disk.", "Just numbers turned into a voice.", "Press stop when you have heard enough.",
    ];

    private static SpeechRequest Request(string text, string engine) =>
        new(text, Rate, 1, engine == "flite" ? FliteSpeechEngine.DefaultPitchHz : 105) { Engine = engine };

    /// <summary>The signal of the whole corpus for one engine: the sentences with half a second of silence between them.</summary>
    private static short[] Signal(string engine)
    {
        List<short> signal = [];
        foreach (string sentence in Corpus)
        {
            SpeechRequest request = Request(sentence, engine);
            short[] samples = engine == "flite" ? FliteSpeechEngine.Render(request).Samples : HumanicoSpeechEngine.Render(request).Samples;
            signal.AddRange(samples);
            signal.AddRange(new short[Rate / 2]);
        }

        return [.. signal];
    }

    private static double Lufs(string engine) => Loudness.IntegratedLufs(Signal(engine), Rate) ?? throw new InvalidOperationException("The corpus of " + engine + " has no loudness.");

    [Fact]
    public void The_offsets_make_the_three_engines_equally_loud()
    {
        double reference = Lufs("humanico");
        foreach (string engine in new[] { "retro", "humanico", "flite" })
        {
            double raw = Lufs(engine);
            double offset = SpeechLevels.OffsetDb(engine);
            double expected = Math.Round((reference - raw) * 2, MidpointRounding.AwayFromZero) / 2;
            output.WriteLine($"{engine}: {raw:F2} LUFS raw, offset in the table {offset:+0.0;-0.0;0.0} dB, measured offset {expected:+0.0;-0.0;0.0} dB, " +
                $"matched {raw + offset:F2} LUFS (reference {reference:F2} LUFS)");
            Assert.Equal(expected, offset);
            Assert.InRange(raw + offset - reference, -0.5, 0.5);
        }
    }

    [Fact]
    public void The_sentences_of_an_engine_do_not_differ_much_from_each_other_so_one_offset_per_engine_is_enough()
    {
        foreach (string engine in new[] { "retro", "humanico", "flite" })
        {
            double[] each = [.. Corpus.Select(sentence =>
            {
                SpeechRequest request = Request(sentence, engine);
                short[] samples = engine == "flite" ? FliteSpeechEngine.Render(request).Samples : HumanicoSpeechEngine.Render(request).Samples;
                return Loudness.IntegratedLufs([.. samples, .. new short[Rate]], Rate) ?? -70.0;
            })];
            double spread = each.Max() - each.Min();
            output.WriteLine($"{engine}: per sentence {each.Min():F1} to {each.Max():F1} LUFS (spread {spread:F1} LU)");
            Assert.InRange(spread, 0, 6);
        }
    }

    [Fact]
    public void The_offsets_hold_at_another_sample_rate()
    {
        // the resampling of the voices is linear, so the loudness hardly depends on the rate of the project
        double Measure(string engine, int rate)
        {
            List<short> signal = [];
            foreach (string sentence in Corpus.Take(8))
            {
                SpeechRequest request = Request(sentence, engine) with { SampleRate = rate };
                signal.AddRange(engine == "flite" ? FliteSpeechEngine.Render(request).Samples : HumanicoSpeechEngine.Render(request).Samples);
                signal.AddRange(new short[rate / 2]);
            }

            return Loudness.IntegratedLufs([.. signal], rate) ?? -70.0;
        }

        double at44 = Measure("humanico", 44_100) - Measure("flite", 44_100);
        double at22 = Measure("humanico", 22_050) - Measure("flite", 22_050);
        output.WriteLine($"humanico minus flite: {at22:F2} LU at 22.05 kHz, {at44:F2} LU at 44.1 kHz");
        Assert.InRange(at44 - at22, -0.5, 0.5);
    }
}
