using System.Text.Json;
using Ample.Plugins.Analysis;

namespace Ample.Plugins.Tests;

internal static class Synth
{
    public const int Rate = 44100;

    public static float[] Clicks(double bpm, double seconds, double offset = 0.1)
    {
        float[] x = new float[(int)(seconds * Rate)];
        double period = 60 / bpm;
        for (double t = offset; t < seconds - 0.05; t += period)
        {
            int start = (int)(t * Rate);
            for (int i = 0; i < 441 && start + i < x.Length; i++)
            {
                x[start + i] += (float)(0.8 * Math.Exp(-i / 90.0) * Math.Sin(2 * Math.PI * 1000 * i / Rate));
            }
        }

        return x;
    }

    public static float[] Tone(double hz, double seconds, double amplitude = 0.5, double vibratoCents = 0)
    {
        float[] x = new float[(int)(seconds * Rate)];
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double f = hz * Math.Pow(2, vibratoCents / 1200 * Math.Sin(2 * Math.PI * 5 * i / Rate));
            phase += 2 * Math.PI * f / Rate;
            x[i] = (float)(amplitude * (Math.Sin(phase) + (0.4 * Math.Sin(2 * phase)) + (0.2 * Math.Sin(3 * phase))) / 1.6);
        }

        return x;
    }

    public static float[] Sine(double hz, double seconds, double amplitude) => [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate)))];

    public static float[] Chord(int[] midi, double seconds, double amplitude = 0.3)
    {
        float[] x = new float[(int)(seconds * Rate)];
        foreach (int m in midi)
        {
            double hz = 440 * Math.Pow(2, (m - 69) / 12.0);
            for (int i = 0; i < x.Length; i++)
            {
                x[i] += (float)(amplitude / midi.Length * (Math.Sin(2 * Math.PI * hz * i / Rate) + (0.4 * Math.Sin(4 * Math.PI * hz * i / Rate)) + (0.2 * Math.Sin(6 * Math.PI * hz * i / Rate))));
            }
        }

        return x;
    }

    public static float[] Noise(double seconds, double amplitude = 0.3, int seed = 1)
    {
        Random r = new(seed);
        return [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)(amplitude * ((r.NextDouble() * 2) - 1)))];
    }

    public static float[] Join(params float[][] parts) => [.. parts.SelectMany(p => p)];
}

[Trait("Category", "Analysis")]
public sealed class AnalyzerTests
{
    private static JsonElement Run(IAnalyzer a, float[] x, string options = "{}") => JsonDocument.Parse(a.Analyze(new AnalysisInput(x, Synth.Rate, options))).RootElement.Clone();

    [Theory]
    [InlineData(70)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(170)]
    public void Tempo_of_a_click_track_is_found_within_one_bpm(double bpm)
    {
        JsonElement r = Run(new TempoAnalyzer(), Synth.Clicks(bpm, 16));
        Assert.InRange(r.GetProperty("bpm").GetDouble(), bpm - 1, bpm + 1);
        Assert.InRange(r.GetProperty("firstBeatSeconds").GetDouble(), 0.05, 0.16);
        double[] beats = [.. r.GetProperty("beatSeconds").EnumerateArray().Select(b => b.GetDouble())];
        Assert.True(beats.Length >= 16 * bpm / 60 - 2);
        Assert.InRange(beats[1] - beats[0], (60 / bpm) - 0.02, (60 / bpm) + 0.02);
    }

    [Fact]
    public void A_signal_without_beats_or_too_short_gives_no_tempo()
    {
        Assert.Equal(JsonValueKind.Null, Run(new TempoAnalyzer(), new float[500]).GetProperty("bpm").ValueKind);
        Assert.True(Run(new TempoAnalyzer(), Synth.Noise(8)).GetProperty("confidence").GetDouble() < 0.6);
    }

    [Fact]
    public void Key_of_chord_sequences_is_the_tonic_and_mode_or_the_relative_key_with_lower_confidence()
    {
        float[] cMajor = Synth.Join(Synth.Chord([48, 52, 55], 1.5), Synth.Chord([53, 57, 60], 1.5), Synth.Chord([55, 59, 62], 1.5), Synth.Chord([48, 52, 55], 1.5));
        JsonElement c = Run(new KeyAnalyzer(), cMajor);
        Assert.Equal("C major", c.GetProperty("key").GetString());
        Assert.Equal(12, c.GetProperty("chroma").GetArrayLength());

        float[] aMinor = Synth.Join(Synth.Chord([45, 48, 52], 1.5), Synth.Chord([50, 53, 57], 1.5), Synth.Chord([52, 56, 59], 1.5), Synth.Chord([45, 48, 52], 1.5));
        Assert.Equal("A minor", Run(new KeyAnalyzer(), aMinor).GetProperty("key").GetString());

        float[] eMajor = Synth.Join(Synth.Chord([52, 56, 59], 1.5), Synth.Chord([57, 61, 64], 1.5), Synth.Chord([59, 63, 66], 1.5), Synth.Chord([52, 56, 59], 1.5));
        Assert.Equal("E major", Run(new KeyAnalyzer(), eMajor).GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, Run(new KeyAnalyzer(), new float[20000]).GetProperty("key").ValueKind);
    }

    [Fact]
    public void Loudness_of_tones_follows_the_standard_and_the_range_sees_two_levels()
    {
        JsonElement steady = Run(new LoudnessAnalyzer(), Synth.Sine(1000, 8, 0.1));
        Assert.True(steady.GetProperty("integratedLufs").GetDouble() is > -23.3 and < -22.7, steady.GetRawText());
        Assert.True(steady.GetProperty("loudnessRange").GetDouble() < 0.5);

        float[] quiet = Synth.Sine(1000, 12, 0.0316);
        float[] loud = Synth.Sine(1000, 12, 0.18);
        JsonElement ranged = Run(new LoudnessAnalyzer(), Synth.Join(quiet, loud));
        Assert.InRange(ranged.GetProperty("loudnessRange").GetDouble(), 13.0, 17.0);
        Assert.True(ranged.GetProperty("shortTermMaxLufs").GetDouble() > ranged.GetProperty("integratedLufs").GetDouble());

        JsonElement tooShort = Run(new LoudnessAnalyzer(), Synth.Sine(1000, 0.2, 0.1));
        Assert.Equal(JsonValueKind.Null, tooShort.GetProperty("integratedLufs").ValueKind);
    }

    [Fact]
    public void The_spectrum_of_a_sine_and_of_noise_differ_as_they_should()
    {
        JsonElement sine = Run(new SpectrumAnalyzer(), Synth.Tone(440, 3, 0.5));
        Assert.InRange(sine.GetProperty("centroidHz").GetDouble(), 400, 900);
        Assert.True(sine.GetProperty("flatness").GetDouble() < 0.1);
        Assert.True(sine.GetProperty("bands").GetProperty("mid").GetDouble() > 0.9);

        JsonElement noise = Run(new SpectrumAnalyzer(), Synth.Noise(3));
        Assert.InRange(noise.GetProperty("centroidHz").GetDouble(), 9000, 13000);
        Assert.True(noise.GetProperty("flatness").GetDouble() > 0.5);
        Assert.True(noise.GetProperty("bands").GetProperty("air").GetDouble() > 0.3);
    }

    [Theory]
    [InlineData(110)]
    [InlineData(220)]
    [InlineData(261.63)]
    [InlineData(440)]
    [InlineData(880)]
    public void Pitch_of_a_tone_is_found_within_ten_cents_even_with_vibrato_around_it(double hz)
    {
        IReadOnlyList<PitchFrame> frames = PitchAnalyzer.Contour(Synth.Tone(hz, 1.5, 0.5, vibratoCents: 20), Synth.Rate);
        Assert.True(frames.Count > 100, frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        double median = frames.Select(f => f.Hz).Order().ElementAt(frames.Count / 2);
        Assert.InRange(1200 * Math.Log2(median / hz), -10, 10);
        Assert.True(frames.Average(f => f.Confidence) > 0.8);
    }

    [Fact]
    public void Silence_and_noise_have_no_pitch()
    {
        Assert.Empty(PitchAnalyzer.Contour(new float[20000], Synth.Rate));
        Assert.True(PitchAnalyzer.Contour(Synth.Noise(1), Synth.Rate).Count < 20);
    }

    [Fact]
    public void A_synthesised_melody_comes_back_as_the_same_notes_on_the_beat_grid()
    {
        // C4 E4 G4 E4 C5, one beat each at 120 BPM (half a second), with a rest after the third note.
        double[] hz = [261.63, 329.63, 392.00, 329.63, 523.25];
        List<float[]> parts = [];
        for (int i = 0; i < hz.Length; i++)
        {
            parts.Add(Synth.Tone(hz[i], 0.5, 0.5, vibratoCents: 15));
            if (i == 2)
            {
                parts.Add(new float[Synth.Rate / 2]);
            }
        }

        float[] melody = Synth.Join([.. parts]);
        JsonElement plan = Run(new MelodyAnalyzer(), melody, "{\"tempo\":120}");
        Assert.Equal("ample-melody/1", plan.GetProperty("schema").GetString());
        int[] pitches = [.. plan.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("pitch").GetInt32())];
        Assert.Equal([60, 64, 67, 64, 72], pitches);
        double[] starts = [.. plan.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("start").GetDouble())];
        Assert.Equal([0, 1, 2, 4, 5], starts);
        Assert.All(plan.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("length").GetDouble()), l => Assert.InRange(l, 0.75, 1.25));
        Assert.Equal(2, plan.GetProperty("notes")[4].GetProperty("bar").GetInt32());

        // Without a tempo option the tempo analyser is asked: a melody has no clicks, so the plan still has notes.
        Assert.True(Run(new MelodyAnalyzer(), melody).GetProperty("notes").GetArrayLength() >= 4);
    }

    [Fact]
    public void Every_analyser_has_an_id_a_version_and_a_description_and_is_found_by_id()
    {
        Assert.Equal(["tempo", "key", "loudness", "spectrum", "pitch", "melody"], BuiltInAnalyzers.All.Select(a => a.Id));
        Assert.All(BuiltInAnalyzers.All, a =>
        {
            Assert.NotEmpty(a.Version);
            Assert.NotEmpty(a.Description);
            Assert.Same(a, BuiltInAnalyzers.Find(a.Id.ToUpperInvariant()));
        });
        Assert.Null(BuiltInAnalyzers.Find("nothing"));
    }

    [Fact]
    public void The_same_input_gives_the_same_text()
    {
        float[] x = Synth.Clicks(120, 8);
        Assert.All(BuiltInAnalyzers.All, a => Assert.Equal(a.Analyze(new AnalysisInput(x, Synth.Rate)), a.Analyze(new AnalysisInput(x, Synth.Rate))));
    }
}
