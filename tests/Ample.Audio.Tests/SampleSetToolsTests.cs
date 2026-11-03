using System.Text.Json;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class SampleSetToolsTests
{
    private static short[] Sine(int frames, double hz, int rate = 44100, double amplitude = 9000)
    {
        short[] wave = new short[frames];
        for (int i = 0; i < frames; i++) wave[i] = (short)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return wave;
    }

    [Fact]
    public void A_steady_tone_loops_cleanly_with_zero_crossing_points_and_a_short_crossfade()
    {
        short[] tone = Sine(88200, 220);
        LoopPoints? loop = SampleSetTools.FindLoop(tone, 44100);
        Assert.NotNull(loop);
        Assert.True(loop.Quality > 0.9, $"quality {loop.Quality}");
        Assert.True(loop.End - loop.Start >= 4410, "a loop is at least a tenth of a second");
        Assert.InRange(loop.Start, 17000, 45000);
        Assert.InRange(loop.End, 48000, 84000);
        Assert.True(tone[loop.Start - 1] <= 0 && tone[loop.Start] > 0, "the loop starts where the wave crosses zero going up");
        Assert.True(loop.Crossfade > 0 && loop.Crossfade <= (loop.End - loop.Start) / 4);
    }

    [Fact]
    public void The_search_is_deterministic_and_a_sample_under_a_quarter_second_is_not_looped()
    {
        short[] tone = Sine(60000, 330);
        Assert.Equal(SampleSetTools.FindLoop(tone, 44100), SampleSetTools.FindLoop(tone, 44100));
        Assert.Null(SampleSetTools.FindLoop(Sine(8000, 330), 44100));
    }

    [Fact]
    public void Loop_points_and_an_envelope_merge_into_set_json_and_leave_the_other_keys()
    {
        using JsonDocument envelope = JsonDocument.Parse("""{ "attack": 200, "release": 800, "sustain": 150, "bogus": 3 }""");
        string text = SampleSetTools.MergeSettings("""{ "gain": 3, "loops": { "c3": { "start": 1, "end": 9, "crossfade": 1 } } }""", "C4", new LoopPoints(1000, 5000, 220, 0.97), envelope.RootElement.Clone());
        using JsonDocument merged = JsonDocument.Parse(text);
        JsonElement root = merged.RootElement;
        Assert.Equal(3, root.GetProperty("gain").GetInt32());
        Assert.Equal(9, root.GetProperty("loops").GetProperty("c3").GetProperty("end").GetInt32());
        Assert.Equal(5000, root.GetProperty("loops").GetProperty("c4").GetProperty("end").GetInt32());
        Assert.Equal(200, root.GetProperty("envelope").GetProperty("attack").GetInt32());
        Assert.Equal(100, root.GetProperty("envelope").GetProperty("sustain").GetInt32());   // a sustain is a percentage: 150 is capped
        Assert.False(root.GetProperty("envelope").TryGetProperty("bogus", out _));
        Assert.Equal("""{ "x": 1 }""".Replace(" ", string.Empty, StringComparison.Ordinal), SampleSetTools.MergeSettings("""{ "x": 1 }""", "c4", null, null).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal));
    }
}
