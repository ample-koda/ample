using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class VocoderTests
{
    private const int Rate = 22050;

    private static readonly double[] Notes = [110.0, 138.6, 164.8];

    // a sawtooth chord (rich in harmonics) as the carrier, and noise that is only there from 0.3 s to 0.7 s as the voice
    private static double[] Chord(double seconds) => [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => 4000 * Notes.Sum(f => ((((f * i / Rate) % 1) * 2) - 1)))];

    private static double[] Voice(double seconds)
    {
        System.Random random = new(7);
        return [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => i is >= (int)(0.3 * Rate) and < (int)(0.7 * Rate) ? (random.NextDouble() - 0.5) * 12000 : 0)];
    }

    private static double Rms(double[] x, double from, double to) => Math.Sqrt(x.Skip((int)(from * Rate)).Take((int)((to - from) * Rate)).Average(v => v * v));

    [Fact]
    public void The_carrier_only_speaks_where_the_voice_is()
    {
        double[][] result = Vocoder.Process(Voice(1), [Chord(1)], Rate, new Vocoder.Settings(), 1);
        Assert.Equal(Rate, result[0].Length);
        Assert.True(Rms(result[0], 0.4, 0.65) > 20 * Rms(result[0], 0.0, 0.2), "loud where the voice is, almost silent where it is not");
        Assert.True(Rms(result[0], 0.4, 0.65) > 500, "and at a healthy level, like the carrier");
    }

    [Fact]
    public void Mix_zero_is_the_carrier_and_the_same_input_gives_the_same_samples()
    {
        double[] carrier = Chord(0.5), voice = Voice(0.5);
        double[][] dry = Vocoder.Process(voice, [carrier], Rate, new Vocoder.Settings(Mix: 0), 1);
        Assert.Equal(carrier.Take(2000), dry[0].Take(2000));
        double[][] a = Vocoder.Process(voice, [carrier], Rate, new Vocoder.Settings(), 3), b = Vocoder.Process(voice, [carrier], Rate, new Vocoder.Settings(), 3);
        Assert.Equal(a[0], b[0]);
    }
}
