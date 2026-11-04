using Ample.Audio.Engine;

namespace Ample.Server.Tests;

public sealed class AiClipTests
{
    private static byte[] TenSeconds()
    {
        const int rate = 44100;
        short[] left = new short[rate * 10], right = new short[rate * 10];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = right[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
        }

        return StereoWav.Encode(new StereoBuffer(left, right), rate);
    }

    [Fact]
    public void A_slice_is_cut_by_seconds_or_by_bars_and_has_a_short_fade_at_both_ends()
    {
        byte[] wav = TenSeconds();
        (byte[] Wav, double From, double To, double Seconds)? bySeconds = AiService.Slice(wav, 2, 5, bars: false, barSeconds: 2.4);
        Assert.NotNull(bySeconds);
        Assert.Equal(3.0, bySeconds.Value.Seconds, 2);
        (StereoBuffer buffer, int rate) = StereoWav.Decode(bySeconds.Value.Wav);
        Assert.InRange(buffer.Length, (3 * 44100) - 5, (3 * 44100) + 5);
        Assert.Equal(0, buffer.Left[0]);                                  // starts silent (the fade), so it does not click
        Assert.True(Math.Abs((int)buffer.Left[buffer.Length / 2]) > 0 || Math.Abs((int)buffer.Left[(buffer.Length / 2) + 3]) > 0);
        Assert.Equal(44100, rate);

        // bars: bar 2 to bar 3 at 2.4 s a bar is from 2.4 s to 7.2 s (the end bar is included)
        (byte[] Wav, double From, double To, double Seconds)? byBars = AiService.Slice(wav, 2, 3, bars: true, barSeconds: 2.4);
        Assert.Equal(2.4, byBars!.Value.From, 2);
        Assert.Equal(7.2, byBars.Value.To, 2);

        // longer than the block: cut at the end; empty: nothing
        Assert.Equal(10.0, AiService.Slice(wav, 0, 99, bars: false, barSeconds: 2.4)!.Value.To, 2);
        Assert.Null(AiService.Slice(wav, 20, 30, bars: false, barSeconds: 2.4));
    }

    [Fact]
    public void A_full_length_preview_is_not_truncated_at_ninety_seconds()
    {
        const int rate = 1000;
        short[] samples = new short[rate * 100];
        byte[] wav = StereoWav.Encode(new StereoBuffer(samples, (short[])samples.Clone()), rate);

        (byte[] Wav, double From, double To, double Seconds)? full = AiService.Slice(wav, 0, 0, bars: false, barSeconds: 2.4);

        Assert.NotNull(full);
        Assert.Equal(100.0, full.Value.Seconds, 2);
        Assert.Equal(100.0, full.Value.To, 2);
        Assert.Equal(rate * 100, StereoWav.Decode(full.Value.Wav).Buffer.Length);
    }
}
