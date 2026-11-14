using Ample.Audio.Engine;

namespace Ample.Server.Tests;

public sealed class EffectPreviewRangeTests
{
    private static short[] Ramp(int n) => [.. Enumerable.Range(0, n).Select(i => (short)i)];

    [Fact]
    public void A_mono_key_is_cut_to_the_range_of_the_input()
    {
        byte[] key = Wav.Encode(Ramp(8000), 8000);   // one second

        byte[] cut = EffectPreviewRange.Cut(key, 250, 500);

        (short[] samples, int rate) = Wav.Decode(cut);
        Assert.Equal(8000, rate);
        Assert.Equal(2000, samples.Length);
        Assert.Equal(2000, samples[0]);
    }

    [Fact]
    public void A_stereo_key_keeps_both_channels_in_the_range()
    {
        short[] left = Ramp(8000), right = [.. Ramp(8000).Select(v => (short)(-v))];
        byte[] key = StereoWav.Encode(new StereoBuffer(left, right), 8000);

        byte[] cut = EffectPreviewRange.Cut(key, 500, 750);

        (StereoBuffer channels, int rate) = StereoWav.Decode(cut);
        Assert.Equal(8000, rate);
        Assert.Equal(2000, channels.Left.Length);
        Assert.Equal(4000, channels.Left[0]);
        Assert.Equal(-4000, channels.Right[0]);
    }

    [Fact]
    public void A_range_past_the_end_gives_what_there_is()
    {
        byte[] key = Wav.Encode(Ramp(1000), 8000);   // 125 ms

        byte[] cut = EffectPreviewRange.Cut(key, 100, 30_000);

        Assert.Equal(200, Wav.Decode(cut).Samples.Length);
    }

    [Fact]
    public void A_range_that_starts_after_the_end_is_empty_and_a_backwards_range_too()
    {
        byte[] key = Wav.Encode(Ramp(1000), 8000);

        Assert.Empty(Wav.Decode(EffectPreviewRange.Cut(key, 5000, 6000)).Samples);
        Assert.Empty(Wav.Decode(EffectPreviewRange.Cut(key, 100, 50)).Samples);
    }

    [Fact]
    public void A_missing_wav_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => EffectPreviewRange.Cut(null!, 0, 100));
    }
}
