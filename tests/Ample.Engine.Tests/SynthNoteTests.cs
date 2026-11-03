using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

public sealed class SynthNoteTests
{
    private const int Rate = 44100;

    private static async Task<byte[]> Render(object[] notes, string wave = "square")
    {
        BlockNode block = new() { Type = "synth", Name = "voice" };
        block.Parameters.Add(new Parameter("wave", wave, 0));
        block.Parameters.Add(new Parameter("attack", "1", 0));
        block.Parameters.Add(new Parameter("decay", "1", 0));
        block.Parameters.Add(new Parameter("sustain", "100", 0));
        block.Parameters.Add(new Parameter("release", "1", 0));
        block.Parameters.Add(new Parameter("cutoff", "20000", 0));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = JsonSerializer.SerializeToUtf8Bytes(new { notes }) };
        RenderRequest request = new(block, EngineRig.Spec.Templates["synth"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, "fixture");
        return (await new SynthRenderer().RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data;
    }

    [Fact]
    public async Task Synth_note_properties_change_only_the_intended_voice()
    {
        object note = new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96 };
        byte[] plain = await Render([note]);
        byte[] fine = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96, fine = 50 }]);
        byte[] dark = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96, cut = 0 }]);
        byte[] released = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96, rel = 1000 }]);
        byte[] panned = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96, pan = -100 }]);
        Assert.NotEqual(plain, fine);
        Assert.True(Wav.Decode(dark).Samples.Sum(x => Math.Abs((long)x)) < Wav.Decode(plain).Samples.Sum(x => Math.Abs((long)x)));
        Assert.Contains(Wav.Decode(released).Samples.Skip(Rate / 2).Take(Rate / 10), sample => sample != 0);
        Assert.True(ChannelAudio.IsStereo(panned));
        (StereoBuffer stereo, _) = ChannelAudio.Decode(panned);
        Assert.True(stereo.Left.Sum(x => Math.Abs((long)x)) > 4 * stereo.Right.Sum(x => Math.Abs((long)x)));
    }

    [Fact]
    public async Task Synth_noise_wave_is_repeatable_and_follows_the_pitch()
    {
        static int Flips(byte[] wav) => Wav.Decode(wav).Samples.Take(Rate / 4).Zip(Wav.Decode(wav).Samples.Skip(1).Take(Rate / 4)).Count(pair => (pair.First < 0) != (pair.Second < 0));
        byte[] low = await Render([new { bar = 1, beat = 0, beats = 1, pitch = 36, velocity = 100 }], "noise");
        byte[] high = await Render([new { bar = 1, beat = 0, beats = 1, pitch = 96, velocity = 100 }], "noise");
        Assert.Equal(low, await Render([new { bar = 1, beat = 0, beats = 1, pitch = 36, velocity = 100 }], "noise"));
        Assert.NotEqual(low, await Render([new { bar = 1, beat = 0, beats = 1, pitch = 36, velocity = 100 }]));
        Assert.Contains(Wav.Decode(low).Samples.Take(Rate / 4), sample => sample != 0);
        Assert.True(Flips(high) > 4 * Flips(low));
    }

    [Fact]
    public async Task Synth_slide_continues_the_voice_without_another_attack()
    {
        object first = new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96 };
        byte[] held = await Render([new { bar = 1, beat = 0, beats = 0.5, pitch = 60, velocity = 96 }]);
        byte[] samePitchSlide = await Render([first, new { bar = 1, beat = 0.25, beats = 0.25, pitch = 60, velocity = 96, slide = true }]);
        byte[] upwardSlide = await Render([first, new { bar = 1, beat = 0.25, beats = 0.25, pitch = 72, velocity = 96, slide = true }]);
        Assert.Equal(held, samePitchSlide);
        Assert.NotEqual(held, upwardSlide);
    }

    [Fact]
    public async Task Synth_portamento_glides_previous_voice_and_orphan_note_still_plays()
    {
        byte[] plain = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96 }]);
        byte[] orphan = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96, port = 100 }]);
        byte[] connected = await Render([new { bar = 1, beat = 0, beats = 0.25, pitch = 60, velocity = 96 }, new { bar = 1, beat = 0.25, beats = 0.25, pitch = 72, velocity = 96, port = 100 }]);
        byte[] held = await Render([new { bar = 1, beat = 0, beats = 0.35, pitch = 60, velocity = 96 }]);
        Assert.Equal(plain, orphan);
        Assert.NotEqual(held, connected);
    }
}
