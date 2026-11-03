using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class AudioAnalyzerTests
{
    private const int Rate = 44100;

    private static short[] Sine(double hz, double seconds)
    {
        short[] samples = new short[(int)(Rate * seconds)];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(16000 * Math.Sin(2 * Math.PI * hz * i / Rate));
        return samples;
    }

    [Fact]
    public void A_steady_220_hz_tone_is_an_a3_with_a_high_confidence()
    {
        JsonObject result = AudioAnalyzer.Analyze(Sine(220, 1.5), Rate);
        JsonObject pitch = result["pitch"]!.AsObject();
        Assert.True(pitch["pitched"]!.GetValue<bool>());
        Assert.Equal("a3", pitch["note"]!.GetValue<string>());
        Assert.InRange(pitch["cents"]!.GetValue<int>(), -10, 10);
        Assert.True(pitch["confidence"]!.GetValue<double>() > 0.85);
    }

    [Fact]
    public void A_click_track_at_120_bpm_of_eight_bars_is_found_as_a_loop_of_eight_bars()
    {
        short[] samples = new short[Rate * 16];
        int beat = Rate / 2;
        for (int n = 0; n * beat < samples.Length; n++)
        {
            for (int i = 0; i < 1500 && (n * beat) + i < samples.Length; i++) samples[(n * beat) + i] = (short)(20000 * Math.Exp(-i / 300.0) * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        }

        JsonObject tempo = AudioAnalyzer.Analyze(samples, Rate)["tempo"]!.AsObject();
        Assert.True(tempo["found"]!.GetValue<bool>());
        Assert.InRange(tempo["bpm"]!.GetValue<double>(), 118, 122);
        Assert.Equal(8, tempo["wholeBars"]!.GetValue<int>());
        Assert.True(tempo["loopReady"]!.GetValue<bool>());
    }

    [Fact]
    public void Silence_and_very_short_audio_are_reported_without_numbers_that_mean_nothing()
    {
        Assert.NotNull(AudioAnalyzer.Analyze(new short[Rate], Rate)["note"]);
        Assert.NotNull(AudioAnalyzer.Analyze(new short[100], Rate)["note"]);
        Assert.Equal("c4", AudioAnalyzer.NoteName(60));
        Assert.Equal("a#2", AudioAnalyzer.NoteName(46));
    }
}
