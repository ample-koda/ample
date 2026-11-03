using Ample.Audio.Engine;

namespace Ample.Core.Tests;

[Trait("Category", "Effects")]
public sealed class LoudnessTests
{
    [Fact]
    public void A_one_kilohertz_sine_at_minus_twenty_dBFS_peak_reads_about_minus_twenty_three_LUFS()
    {
        const int rate = 48000;
        short[] sine = [.. Enumerable.Range(0, rate * 3).Select(i => (short)Math.Round(32768 * 0.1 * Math.Sin(2 * Math.PI * 1000 * i / rate)))];
        double? lufs = Loudness.IntegratedLufs(sine, rate);
        Assert.NotNull(lufs);
        Assert.InRange(lufs!.Value, -23.5, -22.5);   // a sine of 0.1 peak has an rms of -23 dBFS, and the K-weighting is about flat at 1 kHz
    }

    [Fact]
    public void Silence_and_very_short_sounds_have_no_loudness()
    {
        Assert.Null(Loudness.IntegratedLufs(new short[48000 * 2], 48000));
        Assert.Null(Loudness.IntegratedLufs(new short[1000], 48000));
    }
}
