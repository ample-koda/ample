using Ample.Audio.Engine;

namespace Ample.Core.Tests;

/// <summary>Loop options of import-audio (plan L01): a plain first pass, crossfade units and curve, an intro and an outro around the repeating part.</summary>
public sealed class LoopOptionsTests
{
    private static short[] Ramp(int n) => [.. Enumerable.Range(0, n).Select(i => (short)i)];

    [Fact]
    public void The_first_pass_plays_the_stretch_from_its_start_and_only_later_passes_are_blended()
    {
        short[] stretch = Ramp(1000);
        short[] placed = AudioCut.Place(stretch, 0, 3000, loop: true, crossfade: 200, AudioCut.FadeCurve.Linear);
        Assert.Equal(stretch[..800], placed[..800]);   // period = 1000 - 200: the first pass is plain up to the first join
        Assert.NotEqual(stretch[0], placed[800]);   // the join starts there: the start of the stretch is blended in
    }

    [Fact]
    public void The_join_of_unrelated_noise_keeps_its_level_with_equal_power_and_dips_with_linear()
    {
        System.Random random = new(7);
        short[] noise = [.. Enumerable.Range(0, 20000).Select(_ => (short)random.Next(-8000, 8001))];
        double RmsAtJoin(AudioCut.FadeCurve curve)
        {
            short[] placed = AudioCut.Place(noise, 0, 60000, loop: true, crossfade: 4000, curve);
            int middle = 16000 + 2000;   // the middle of the first join (period 16000)
            return Math.Sqrt(Enumerable.Range(middle - 500, 1000).Sum(i => (double)placed[i] * placed[i]) / 1000);
        }

        double plain = Math.Sqrt(noise.Sum(v => (double)v * v) / noise.Length);
        Assert.InRange(RmsAtJoin(AudioCut.FadeCurve.EqualPower) / plain, 0.9, 1.1);
        Assert.True(RmsAtJoin(AudioCut.FadeCurve.Linear) / plain < 0.85, "a linear blend of unrelated material dips in the middle");
    }

    [Theory]
    [InlineData("3", 48000)]
    [InlineData("3s", 48000)]
    [InlineData("1 bar", 64000)]
    [InlineData("2 beats", 32000)]
    [InlineData("0.5 bars", 32000)]
    public void A_crossfade_length_can_be_seconds_beats_or_bars(string text, int samples)
    {
        Assert.Equal(samples, AudioCut.SamplesOf(text, 16000, 64000));
        Assert.Null(AudioCut.SamplesOf("soon", 16000, 64000));
        Assert.Null(AudioCut.SamplesOf("", 16000, 64000));
    }

    [Fact]
    public void An_intro_plays_once_before_the_repeating_part_and_an_outro_once_at_the_end()
    {
        short[] stretch = [.. Enumerable.Repeat((short)100, 300), .. Enumerable.Repeat((short)5000, 1000), .. Enumerable.Repeat((short)-200, 400)];
        short[] placed = AudioCut.Place(stretch, 0, 4000, loop: true, crossfade: 0, AudioCut.FadeCurve.Linear, loopFrom: 300, loopTo: 1300);
        Assert.All(placed[..300], v => Assert.Equal(100, v));   // intro once
        Assert.All(placed[300..3500], v => Assert.Equal(5000, v));   // the repeating part
        Assert.All(placed[3600..], v => Assert.Equal(-200, v));   // outro at the end of the block
        Assert.DoesNotContain((short)100, placed[300..]);   // the intro never comes back
    }

    [Fact]
    public void The_joins_are_marked_and_body_plus_region_is_the_whole_loop()
    {
        short[] stretch = Ramp(1000);
        bool[] seam = new bool[3000];
        short[] whole = AudioCut.Place(stretch, 0, 3000, loop: true, crossfade: 200, AudioCut.FadeCurve.EqualPower, seam: seam);
        Assert.False(seam[799]);
        Assert.True(seam[800]);   // the first join starts at the period (1000 - 200)
        Assert.True(seam[999]);
        Assert.False(seam[1000]);
        short[] body = [.. whole.Select((v, i) => seam[i] ? (short)0 : v)];
        short[] region = [.. whole.Select((v, i) => seam[i] ? v : (short)0)];
        Assert.Equal(whole, body.Zip(region, (a, b) => (short)(a + b)).ToArray());
    }
}
