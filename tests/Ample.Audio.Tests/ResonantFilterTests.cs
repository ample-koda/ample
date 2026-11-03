using Ample.Audio.Engine;

namespace Ample.Core.Tests;

/// <summary>The resonant two-pole filter (the resonance of the filter block).</summary>
public sealed class ResonantFilterTests
{
    private static double Rms(int[] x) => Math.Sqrt(x.Skip(1000).Select(v => (double)v * v).Average());

    [Fact]
    public void Resonance_lifts_the_level_at_the_cutoff()
    {
        const int rate = 44100;
        int[] tone = [.. Enumerable.Range(0, 8000).Select(i => (int)(3000 * Math.Sin(2 * Math.PI * 1000 * i / rate)))];
        int[] cutoffs = [.. Enumerable.Repeat(1000, tone.Length)];
        double plain = Rms(ResonantFilter.Process(tone, cutoffs, rate, false, 1));
        double ringing = Rms(ResonantFilter.Process(tone, cutoffs, rate, false, 80));
        Assert.True(ringing > plain * 2, $"plain {plain} ringing {ringing}");
    }

    [Fact]
    public void A_constant_resonance_curve_is_byte_identical_to_the_plain_value_and_a_ramp_differs()
    {
        const int rate = 44100;
        int[] tone = [.. Enumerable.Range(0, 8000).Select(i => (int)(3000 * Math.Sin(2 * Math.PI * 1000 * i / rate)))];
        int[] cutoffs = [.. Enumerable.Repeat(1000, tone.Length)];
        int[] plain = ResonantFilter.Process(tone, cutoffs, rate, false, 60);
        Assert.Equal(plain, ResonantFilter.Process(tone, cutoffs, rate, false, 0, [.. Enumerable.Repeat(60, tone.Length)]));
        int[] ramp = ResonantFilter.Process(tone, cutoffs, rate, false, 0, [.. Enumerable.Range(0, tone.Length).Select(i => i * 100 / tone.Length)]);
        Assert.NotEqual(plain, ramp);
        Assert.True(Rms(ramp[4000..]) > Rms(ramp[..4000]));
    }

    [Fact]
    public void Bandpass_keeps_the_cutoff_tone_notch_removes_it_and_two_passes_cut_harder()
    {
        const int rate = 44100;
        static int[] Tone(int hz) => [.. Enumerable.Range(0, 8000).Select(i => (int)(3000 * Math.Sin(2 * Math.PI * hz * i / rate)))];
        int[] cutoffs = [.. Enumerable.Repeat(1000, 8000)];
        double Run(int hz, int shape, int passes) => Rms(ResonantFilter.Process(Tone(hz), cutoffs, rate, shape, 50, null, passes));
        Assert.True(Run(1000, ResonantFilter.BandPass, 1) > Run(100, ResonantFilter.BandPass, 1) * 3);
        Assert.True(Run(1000, ResonantFilter.Notch, 1) < Run(100, ResonantFilter.Notch, 1) * 0.3);
        Assert.True(Run(5000, ResonantFilter.LowPass, 2) < Run(5000, ResonantFilter.LowPass, 1) * 0.6);
        Assert.Equal(ResonantFilter.Process(Tone(300), cutoffs, rate, false, 40), ResonantFilter.Process(Tone(300), cutoffs, rate, ResonantFilter.LowPass, 40, null, 1));
    }

    [Fact]
    public void A_highpass_removes_a_low_tone_and_stays_finite()
    {
        const int rate = 44100;
        int[] tone = [.. Enumerable.Range(0, 8000).Select(i => (int)(3000 * Math.Sin(2 * Math.PI * 50 * i / rate)))];
        double high = Rms(ResonantFilter.Process(tone, [.. Enumerable.Repeat(2000, tone.Length)], rate, true, 100));
        Assert.True(high < Rms(tone) * 0.1, $"high {high}");
    }
}
