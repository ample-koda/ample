using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>Where a block's audio lands in the bars of a range: the clip is shifted by its origin and cut to the range, sample for sample.</summary>
public sealed class BlockTimingPlaceTests
{
    private static readonly int[] Lengths = [0, 1, 2, 7, 50];
    private static readonly int[] BarSizes = [1, 3, 8];
    private static readonly int[] FirstBars = [1, 2, 3];
    private static readonly int[] BarCounts = [0, 1, 2, 5];

    /// <summary>The placement written the obvious way: output sample i is input sample i minus the offset, or silence.</summary>
    private static short[] Expected(short[] samples, int origin, BarRange range, int samplesPerBar)
    {
        short[] expected = new short[range.Bars * samplesPerBar];
        long offset = (long)(origin - range.FirstBar) * samplesPerBar;
        for (int i = 0; i < expected.Length; i++)
        {
            long source = i - offset;
            if (source >= 0 && source < samples.Length)
            {
                expected[i] = samples[source];
            }
        }

        return expected;
    }

    private static short[] Noise(Random random, int length) => [.. Enumerable.Range(0, length).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1))];

    private static IEnumerable<(int Origin, BarRange Range, int SamplesPerBar)> Cases() =>
        from samplesPerBar in BarSizes
        from firstBar in FirstBars
        from bars in BarCounts
        from origin in Enumerable.Range(-2, 11)   // before the range, inside it, beyond its end
        select (origin, new BarRange(firstBar, bars), samplesPerBar);

    [Fact]
    public void A_mono_clip_is_placed_sample_for_sample()
    {
        Random random = new(11);
        int checkedCases = 0;
        foreach (int length in Lengths)
        {
            short[] clip = Noise(random, length);
            byte[] audio = Wav.Encode(clip, 8000);
            foreach ((int origin, BarRange range, int samplesPerBar) in Cases())
            {
                (short[] placed, int rate) = Wav.Decode(BlockTiming.Place(audio, origin, range, samplesPerBar));
                Assert.Equal(Expected(clip, origin, range, samplesPerBar), placed);
                Assert.Equal(8000, rate);
                checkedCases++;
            }
        }

        Assert.True(checkedCases > 500);
    }

    [Fact]
    public void A_stereo_clip_is_placed_sample_for_sample_in_both_channels()
    {
        Random random = new(12);
        foreach (int length in Lengths)
        {
            short[] left = Noise(random, length), right = Noise(random, length);
            byte[] audio = StereoWav.Encode(new StereoBuffer(left, right), 44100);
            foreach ((int origin, BarRange range, int samplesPerBar) in Cases())
            {
                (StereoBuffer placed, int rate) = ChannelAudio.Decode(BlockTiming.Place(audio, origin, range, samplesPerBar));
                Assert.Equal(Expected(left, origin, range, samplesPerBar), placed.Left);
                Assert.Equal(Expected(right, origin, range, samplesPerBar), placed.Right);
                Assert.Equal(44100, rate);
            }
        }
    }

    [Fact]
    public void A_clip_that_starts_after_the_range_or_ends_before_it_gives_silence()
    {
        short[] clip = [5, 6, 7, 8];
        BarRange range = new(3, 2);   // bars 3 and 4 of 4 samples each
        Assert.All(Wav.Decode(BlockTiming.Place(Wav.Encode(clip, 8000), 9, range, 4)).Samples, sample => Assert.Equal(0, sample));    // starts at bar 9
        Assert.All(Wav.Decode(BlockTiming.Place(Wav.Encode(clip, 8000), 1, range, 4)).Samples, sample => Assert.Equal(0, sample));    // one bar long, ended at bar 2
        Assert.Equal([0, 0, 0, 0, 5, 6, 7, 8], Wav.Decode(BlockTiming.Place(Wav.Encode(clip, 8000), 4, range, 4)).Samples);           // starts at bar 4: the second bar of the range
    }
}
