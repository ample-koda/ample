using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Audio.Engine;
using Ample.Audio.Instruments;
using Ample.Audio.IO;
using Ample.Audio.Midi;
using Ample.Core.Abstractions;
using Ample.Core.Tests.Instruments;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Sampler")]
public sealed class SamplerTests : IDisposable
{
    private const int Rate = 44100;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-sampler-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private sealed record Rig(string Root, Takebank Bank, BuildEngine Engine);

    private async Task<Rig> RigAsync(string name = "proj")
    {
        string root = Path.Combine(folder, name);
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, name);
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        return new Rig(root, bank, new BuildEngine(bank, EngineRig.Spec, p.Manifest.Id, root));
    }

    private static void Wave(string root, string rel, short[] samples)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Wav.Encode(samples, Rate));
    }

    private static short[] Impulse(short value, int length = 200)
    {
        short[] s = new short[length];
        s[0] = value;
        return s;
    }

    private static short[] Flat(short value, int length) => [.. Enumerable.Repeat(value, length)];

    [Fact]
    public async Task SoundFont_GM_renderer_plays_a_generated_program_preset()
    {
        string soundFont = Path.Combine(folder, "tiny.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Tiny());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "tiny.sf2", 0));
        block.Parameters.Add(new Parameter("bank", "0", 1));
        block.Parameters.Add(new Parameter("program", "5", 2));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":96}]}")
        };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        byte[] wave = (await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data;

        Assert.Contains(SampleSet.ReadWav(wave, Rate), sample => sample != 0);
    }

    [Fact]
    public async Task SoundFont_GM_with_a_missing_project_file_names_the_file_and_the_pack_alternative()
    {
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "samples/not-here.sf2", 0));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":96}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");

        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await renderer.RenderAsync(request, CancellationToken.None));

        Assert.Equal("AMP5311", error.DiagnosticCode);
        Assert.Contains("samples/not-here.sf2", error.Message, StringComparison.Ordinal);
        Assert.Contains("pack:musescore-general", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoundFont_GM_with_the_template_default_file_missing_from_the_project_uses_the_installed_pack_when_there_is_one()
    {
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", SoundFontAsset.TemplateDefault, 0));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":96}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        bool packInstalled = AmpleHome.LookupPackFile(AmpleHome.DefaultPath(), null, folder, "licensed/musescore-general/MuseScore_General.sf2").Path is not null;

        if (!packInstalled)
        {
            AmpleException missing = await Assert.ThrowsAsync<AmpleException>(async () => await renderer.RenderAsync(request, CancellationToken.None));
            Assert.Equal("AMP5311", missing.DiagnosticCode);   // no pack on this machine: the error names what to install
            return;
        }

        byte[] wave = (await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data;
        Assert.Contains(SampleSet.ReadWav(wave, Rate), sample => sample != 0);
    }

    [Fact]
    public async Task SoundFont_GM_applies_integer_volume_and_pitch_bend_events()
    {
        string soundFont = Path.Combine(folder, "midi-controls.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Tiny());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "midi-controls.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 2));   // no evening out of the level: the darker note must come out quieter
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":96,\"channel\":0}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        short[] plain = Wav.Decode((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        short[] muted = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 7, 0)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        short[] bent = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.PitchBend, 0, 127, 127)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;

        Assert.Contains(plain, sample => sample != 0);
        Assert.All(muted, sample => Assert.Equal((short)0, sample));
        Assert.False(plain.SequenceEqual(bent));
        short[] dark = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 74, 0)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        byte[] panned = (await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 10, 0)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data;
        (StereoBuffer sides, _) = ChannelAudio.Decode(panned);
        Assert.True(ChannelAudio.IsStereo(panned));
        Assert.True(sides.Left.Sum(x => Math.Abs((long)x)) > 4 * sides.Right.Sum(x => Math.Abs((long)x)));   // controller 10 at 0 is hard left
        Assert.False(plain.SequenceEqual(dark));
        Assert.True(dark.Sum(x => Math.Abs((long)x)) < plain.Sum(x => Math.Abs((long)x)), $"dark {dark.Sum(x => Math.Abs((long)x))} plain {plain.Sum(x => Math.Abs((long)x))}");   // the closed filter takes energy away
    }

    [Fact]
    public async Task SoundFont_GM_volume_follows_the_soundfont_curve_and_moves_a_sounding_note()
    {
        string soundFont = Path.Combine(folder, "midi-volume.sf2");
        File.WriteAllBytes(soundFont, LoopedFont());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "midi-volume.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 2));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":2,\"velocity\":127,\"channel\":0}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        short[] plain = Wav.Decode((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        short[] half = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 7, 64)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        long Energy(short[] audio, int from, int to) => audio[from..to].Sum(x => Math.Abs((long)x));
        int end = Rate / 2;
        double ratio = (double)Energy(half, 0, end) / Energy(plain, 0, end);
        Assert.InRange(ratio, 0.22, 0.29);   // volume 64: -400 log10(64/127) = -11.9 dB (a linear volume would be -6 dB)

        // the volume drops while the note sounds: the second half follows it, the first half does not
        int middle = Rate / 4;
        short[] drop = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(middle, SmfEventKind.ControlChange, 0, 7, 64)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        Assert.Equal(Energy(plain, 0, middle - 16), Energy(drop, 0, middle - 16));
        Assert.InRange((double)Energy(drop, middle + 32, end) / Energy(plain, middle + 32, end), 0.22, 0.29);

        // the modulation wheel deepens the vibrato LFO of the zone (50 cents): the pitch moves once the LFO has risen
        short[] wheel = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 1, 127)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        Assert.False(plain.SequenceEqual(wheel));
    }

    // a font whose one sample loops (512 frames, loop 32..480), so a note sounds as long as it is held
    private static byte[] LoopedFont() => Sf2Fixture.Build([("tone", 512, (32u, 480u), 60)], [("lead", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.SampleId, 0))])], [("Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]);

    [Fact]
    public async Task SoundFont_GM_gives_the_reverb_send_of_its_notes_on_the_send_output()
    {
        string soundFont = Path.Combine(folder, "midi-send.sf2");
        File.WriteAllBytes(soundFont, LoopedFont());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "midi-send.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 2));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":2,\"velocity\":127,\"channel\":0}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        IReadOnlyList<RenderOutput> dry = await renderer.RenderAsync(request, CancellationToken.None);
        IReadOnlyList<RenderOutput> wet = await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 91, 127)] }, CancellationToken.None);
        Assert.Equal(EngineRig.Spec.Templates["gm"].Outputs.Where(p => p.Type == "audio").Select(p => p.Name), dry.Where(o => o.Role is "audio" or "send" or "chorus").Select(o => o.Role));
        short[] silent = Wav.Decode(dry.Single(o => o.Role == "send").Data).Samples, audio = Wav.Decode(dry.Single(o => o.Role == "audio").Data).Samples;
        Assert.Equal(audio.Length, silent.Length);
        Assert.All(silent, sample => Assert.Equal((short)0, sample));   // nothing is sent: silence as long as the audio
        short[] send = Wav.Decode(wet.Single(o => o.Role == "send").Data).Samples;
        Assert.Equal(audio, Wav.Decode(wet.Single(o => o.Role == "audio").Data).Samples);   // the send does not change the audio
        double ratio = (double)send.Sum(x => Math.Abs((long)x)) / audio.Sum(x => Math.Abs((long)x));
        Assert.InRange(ratio, 0.19, 0.21);   // controller 91 at 127 sends 20 % (the default modulator, SF2 8.4.9)
    }

    [Fact]
    public async Task SoundFont_GM_plays_chord_notes_that_have_no_beat_field()
    {
        string soundFont = Path.Combine(folder, "chord-notes.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Tiny());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "chord-notes.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beats\":4,\"velocity\":96}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        short[] audio = Wav.Decode((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;

        Assert.Contains(audio, sample => sample != 0);
    }

    [Fact]
    public async Task SoundFont_GM_sustain_event_extends_a_note_until_pedal_up()
    {
        string soundFont = Path.Combine(folder, "midi-sustain.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Build(
            [("sustained", 512, (10u, 480u), 60)],
            [("lead", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.SampleId, 0))])],
            [("Sustained Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]));
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "midi-sustain.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":0.25,\"velocity\":96,\"channel\":0}]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        short[] held = Wav.Decode((await renderer.RenderAsync(request with { PerformanceEvents = [new MidiPerformanceEvent(0, SmfEventKind.ControlChange, 0, 64, 127), new MidiPerformanceEvent(Rate / 2, SmfEventKind.ControlChange, 0, 64, 0)] }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;

        Assert.Contains(held.Skip(Rate / 3).Take(Rate / 10), sample => sample != 0);
    }

    [Fact]
    public async Task SoundFont_slide_continues_one_voice_and_changes_its_pitch()
    {
        File.WriteAllBytes(Path.Combine(folder, "slide.sf2"), Sf2Fixture.Build(
            [("sustained", 512, (10u, 480u), 60)],
            [("lead", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.SampleId, 0))])],
            [("Sustained Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]));
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "slide.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 2));
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        async Task<byte[]> Render(object[] notes)
        {
            Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = JsonSerializer.SerializeToUtf8Bytes(new { notes }) };
            RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
            return (await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data;
        }
        object first = new { bar = 1, pitch = 60, beat = 0, beats = 0.25, velocity = 96, channel = 0 };
        byte[] held = await Render([new { bar = 1, pitch = 60, beat = 0, beats = 0.5, velocity = 96, channel = 0 }]);
        byte[] samePitchSlide = await Render([first, new { bar = 1, pitch = 60, beat = 0.25, beats = 0.25, velocity = 96, channel = 0, slide = true }]);
        byte[] upwardSlide = await Render([first, new { bar = 1, pitch = 72, beat = 0.25, beats = 0.25, velocity = 96, channel = 0, slide = true }]);
        Assert.Equal(held, samePitchSlide); // one voice, with no second attack
        Assert.NotEqual(held, upwardSlide);
    }

    [Fact]
    public async Task SoundFont_GM_short_notes_fade_in_and_out_without_click_edges()
    {
        string soundFont = Path.Combine(folder, "short-note.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Tiny());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "short-note.sf2", 0));
        block.Parameters.Add(new Parameter("bank", "0", 1));
        block.Parameters.Add(new Parameter("program", "5", 2));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":0.001,\"velocity\":96}]}")
        };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");

        short[] samples = SampleSet.ReadWav((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data, Rate);

        Assert.Equal(0, samples[0]);
        // the attack ramps over 4 ms and the release lasts at least 10 ms: no sudden edge at either end
        Assert.InRange(Math.Abs((int)samples[1]), 0, 500);
        Assert.InRange(Math.Abs((int)samples[Rate / 100 + 60]), 0, 100);
    }

    [Fact]
    public async Task SoundFont_GM_renderer_routes_midi_channel_ten_to_the_percussion_bank()
    {
        string soundFont = Path.Combine(folder, "tiny-drums.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Build(
            [("kick", 100, (10u, 90u), 36)],
            [("kit", [new Sf2Fixture.Zone((Sf2Fixture.SampleId, 0))])],
            [("Standard Kit", 0, 128, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]));
        BlockNode block = new() { Type = "gm", Name = "drums" };
        block.Parameters.Add(new Parameter("file", "tiny-drums.sf2", 0));
        block.Parameters.Add(new Parameter("bank", "0", 1));
        block.Parameters.Add(new Parameter("program", "0", 2));
        block.Parameters.Add(new Parameter("channel", "9", 3));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":36,\"beat\":0,\"beats\":1,\"velocity\":96,\"channel\":9,\"bank\":0,\"program\":0}]}")
        };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");

        byte[] wave = (await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data;
        Assert.Contains(SampleSet.ReadWav(wave, Rate), sample => sample != 0);
    }

    [Fact]
    public async Task SoundFont_GM_soft_limiter_prevents_hard_clipping_and_can_be_disabled()
    {
        string soundFont = Path.Combine(folder, "hot.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Build(
            [("hot", 100, (10u, 90u), 120)],
            [("lead", [new Sf2Fixture.Zone((Sf2Fixture.SampleId, 0))])],
            [("Hot Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]));
        BlockNode block = new() { Type = "gm", Name = "hot" };
        block.Parameters.Add(new Parameter("file", "hot.sf2", 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 3));   // this test is about the limiter, not about the auto level
        string notes = JsonSerializer.Serialize(new { notes = Enumerable.Repeat(new { bar = 1, pitch = 60, beat = 0, beats = 1, velocity = 127 }, 6) });
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes(notes) };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");

        short[] limited = Wav.Decode((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;
        block.Parameters.Add(new Parameter("limiter", "off", 2));
        short[] hardClipped = Wav.Decode((await renderer.RenderAsync(request with { Block = block }, CancellationToken.None)).Single(o => o.Role == "audio").Data).Samples;

        Assert.InRange(limited.Max(s => Math.Abs((int)s)), 30_000, 32_766);
        Assert.Equal(short.MaxValue, hardClipped.Max());
    }

    [Fact]
    public async Task SoundFont_GM_falls_back_to_the_matching_general_midi_program_for_an_unavailable_bank()
    {
        string soundFont = Path.Combine(folder, "fallback.sf2");
        File.WriteAllBytes(soundFont, Sf2Fixture.Tiny());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", "fallback.sf2", 0));
        block.Parameters.Add(new Parameter("bank", "1", 1));
        block.Parameters.Add(new Parameter("program", "5", 2));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":96}]}")
        };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "gm");

        IReadOnlyList<RenderOutput> outputs = await renderer.RenderAsync(request, CancellationToken.None);

        Assert.Contains(SampleSet.ReadWav(outputs.Single(o => o.Role == "audio").Data, Rate), sample => sample != 0);
        Assert.Contains("using bank 0, program 5", Encoding.UTF8.GetString(outputs.Single(o => o.Role == "warnings").Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixer_soft_limits_overlapping_full_scale_inputs()
    {
        BlockNode block = new() { Type = "mixer", Name = "mix" };
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal)
        {
            ["in1"] = Wav.Encode(Flat(30_000, 8), Rate),
            ["in2"] = Wav.Encode(Flat(30_000, 8), Rate)
        };
        RenderRequest request = new(block, EngineRig.Spec.Templates["mixer"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "mixer");

        short[] output = Wav.Decode((await renderer.RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "mix").Data).Samples;

        Assert.All(output.Take(8), sample => Assert.InRange(sample, 30_000, 32_766));
    }

    [Fact]
    public void Integer_limiter_is_symmetric_monotonic_and_preserves_values_below_its_knee()
    {
        Assert.Equal((short)29_999, FixedAudio.Limit(29_999, true));
        Assert.Equal(FixedAudio.Limit(40_000, true), -FixedAudio.Limit(-40_000, true));
        Assert.True(FixedAudio.Limit(40_000, true) < FixedAudio.Limit(60_000, true));
        Assert.Equal(short.MaxValue, FixedAudio.Limit(40_000, false));
    }

    private static void Kit(string root) { Wave(root, "samples/kit/kick.wav", Impulse(30000)); Wave(root, "samples/kit/snare.wav", Impulse(20000)); Wave(root, "samples/kit/hat.wav", Impulse(10000)); }

    private static string DrumSong(string body, string extra = "") => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\ndrums d at 1 for 1 {{\n  kit samples/kit\n  pattern {body}\n{extra}}}\n";

    private static async Task<short[]> RenderAsync(Rig rig, string song, string block = "d")
    {
        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        BlockResult r = report.Blocks.Single(b => b.Block == block);
        Assert.True(r.Status is "rendered" or "cached", r.Message);
        TakeRecord take = rig.Bank.GetTake(r.TakeId!);
        await using Stream data = rig.Bank.OpenData(take.Data[0].DataId);
        using MemoryStream ms = new();
        await data.CopyToAsync(ms);
        return Wav.Decode(ms.ToArray()).Samples;
    }

    [Theory]
    [InlineData("e2", 40)]
    [InlineData("a2", 45)]
    [InlineData("c3", 48)]
    [InlineData("eb3", 51)]
    [InlineData("c#4", 61)]
    [InlineData("gb4", 66)]
    [InlineData("c-1", 0)]
    public void Note_names_are_midi_notes(string name, int midi) => Assert.Equal(midi, SampleSet.NoteOf(name));

    [Theory]
    [InlineData("kick")]
    [InlineData("hat-open")]
    [InlineData("x")]
    [InlineData("h3")]
    public void Other_names_are_not_notes(string name) => Assert.Null(SampleSet.NoteOf(name));

    [Theory]
    [InlineData("kick", "kick", 1, 1)]
    [InlineData("Kick_v2_r3", "kick", 2, 3)]
    [InlineData("c#4_v3", "c#4", 3, 1)]
    [InlineData("eb3_r2", "eb3", 1, 2)]
    [InlineData("kick_v0", "kick_v0", 1, 1)]
    [InlineData("kick_vx", "kick_vx", 1, 1)]
    [InlineData("_v2", "_v2", 1, 1)]
    [InlineData("kick_r1000", "kick_r1000", 1, 1)]
    [InlineData("kick_r2_v2", "kick_r2", 2, 1)]
    public void Name_parser_reads_only_the_exact_suffix_pattern(string name, string baseName, int layer, int rr) => Assert.Equal((baseName, layer, rr), SampleName.Parse(name));

    [Fact]
    public void Note_names_ignore_the_layer_suffix() => Assert.Equal(SampleSet.NoteOf("c#4"), SampleSet.NoteOf("c#4_v2_r3"));

    [Fact]
    public async Task Extra_layers_are_counted_hashed_and_loaded_under_their_full_names()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        SampleSet before = SampleSet.Load(rig.Root, "samples/kit", Rate);
        Wave(rig.Root, "samples/kit/kick_v2_r1.wav", Impulse(1000));
        Wave(rig.Root, "samples/kit/kick_v2_r2.wav", Impulse(2000));
        SampleSet after = SampleSet.Load(rig.Root, "samples/kit", Rate);
        Assert.Contains("kick_v2_r1", after.Files.Keys);
        Assert.Equal(before.Need("kick").Hash, after.Need("kick").Hash);
        Assert.Equal(2, after.Layers("kick"));
        Assert.Equal(2, after.RoundRobins("kick"));
        Assert.Equal(1, after.Layers("snare"));
        Assert.Equal(5, SampleSet.Hashes(rig.Root, "samples/kit").Count);
    }

    [Fact]
    public void Pitch_shifting_changes_the_length_like_a_tape_and_zero_semitones_changes_nothing()
    {
        short[] s = Flat(1000, 44100);
        Assert.Same(s, SampleSet.Shift(s, 0));
        Assert.InRange(SampleSet.Shift(s, 12).Length, 22049, 22051);
        Assert.InRange(SampleSet.Shift(s, -12).Length, 88198, 88200);
        Assert.InRange(SampleSet.Shift(s, 7).Length, 29425, 29440);
        Assert.All(SampleSet.Shift(s, 5), v => Assert.Equal(1000, v));
    }

    [Fact]
    public void Wav_files_of_other_depths_channels_and_rates_are_played_as_16_bit_mono_at_the_build_rate()
    {
        static byte[] Build(int channels, int rate, int bits, int code, byte[] data)
        {
            using MemoryStream ms = new();
            using BinaryWriter w = new(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + data.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)code);
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * channels * bits / 8);
            w.Write((short)(channels * bits / 8));
            w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(data.Length);
            w.Write(data);
            return ms.ToArray();
        }

        byte[] bits24 = [0, 0, 0x40, 0, 0, 0x20];                                               // 24-bit: 0x400000 and 0x200000 (half and a quarter of full scale)
        short[] mono24 = SampleSet.ReadWav(Build(1, Rate, 24, 1, bits24), Rate);
        Assert.Equal([16384, 8192], mono24);

        byte[] stereo16 = [0x00, 0x20, 0x00, 0x60];                                             // left 8192, right 24576: the mix is 16384
        Assert.Equal([16384], SampleSet.ReadWav(Build(2, Rate, 16, 1, stereo16), Rate));

        Assert.Equal([0, 25600], SampleSet.ReadWav(Build(1, Rate, 8, 1, [128, 228]), Rate));

        byte[] floats = [.. BitConverter.GetBytes(0.5f), .. BitConverter.GetBytes(-0.25f)];
        Assert.Equal([16384, -8192], SampleSet.ReadWav(Build(1, Rate, 32, 3, floats), Rate));

        short[] slow = SampleSet.ReadWav(Build(1, 22050, 16, 1, [.. Enumerable.Range(0, 2205).SelectMany(_ => new byte[] { 0x10, 0x27 })]), Rate);
        Assert.InRange(slow.Length, 4408, 4411);
        Assert.All(slow, v => Assert.Equal(10000, v));

        Assert.Throws<InvalidDataException>(() => SampleSet.ReadWav("not a wave file at all, but long enough to have a header"u8, Rate));
        Assert.Throws<InvalidDataException>(() => SampleSet.ReadWav(Build(6, Rate, 16, 1, [0, 0]), Rate));
    }

    private static SampleFile F(string name) => new(name, "sha256:" + name, [1]);

    [Fact]
    public void The_resolver_picks_the_region_holding_the_note_else_the_nearest_root_and_the_lower_one_on_a_tie()
    {
        Instrument inst = Instrument.FromFiles([F("a2"), F("c3"), F("kick")]);
        Assert.Equal("c3", RegionResolver.Pick(inst, 48, 20000, 0)!.Name);
        Assert.Equal("a2", RegionResolver.Pick(inst, 46, 20000, 0)!.Name);
        Assert.Equal("c3", RegionResolver.Pick(inst, 47, 20000, 0)!.Name);
        Assert.Equal("a2", RegionResolver.Pick(inst, 10, 20000, 0)!.Name);
        Assert.Equal("kick", RegionResolver.PickVoice(inst, "kick", 100, 5)!.Name);
        Assert.Null(RegionResolver.PickVoice(inst, "snare", 100, 0));
        Assert.True(inst.HasPitched);
        Assert.False(Instrument.FromFiles([F("kick")]).HasPitched);
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(16384, 2, 0)]
    [InlineData(16385, 2, 1)]
    [InlineData(32768, 2, 1)]
    [InlineData(32768, 3, 2)]
    [InlineData(10923, 3, 1)]
    [InlineData(10922, 3, 0)]
    [InlineData(5000, 1, 0)]
    public void Layer_thresholds_are_integer_and_cover_the_edges(int velocity, int layers, int expected) => Assert.Equal(expected, Instrument.LayerOf(velocity, layers));

    [Fact]
    public void Layers_follow_the_file_names_and_a_missing_layer_falls_back_to_the_nearest()
    {
        Assert.Equal(("kick", 2, 3), Instrument.ParseName("kick_v2_r3"));
        Assert.Equal(("hat-open", 1, 1), Instrument.ParseName("hat-open"));
        Assert.Equal(("a2", 1, 1), Instrument.ParseName("a2"));
        Instrument two = Instrument.FromFiles([F("kick"), F("kick_v2_r1")]);
        Assert.Equal("kick", RegionResolver.PickVoice(two, "kick", 16384, 0)!.Sample.Name);
        Assert.Equal("kick_v2_r1", RegionResolver.PickVoice(two, "kick", 16385, 0)!.Sample.Name);
        Instrument gap = Instrument.FromFiles([F("snare"), F("snare_v3_r1")]);      // layer 2 is missing: ties go to the lower layer
        Assert.Equal("snare", RegionResolver.PickVoice(gap, "snare", 20000, 0)!.Sample.Name);
        Assert.Equal("snare_v3_r1", RegionResolver.PickVoice(gap, "snare", 32768, 0)!.Sample.Name);
        Instrument notes = Instrument.FromFiles([F("c3"), F("c3_v2_r1"), F("e3")]);
        Assert.Equal("c3_v2_r1", RegionResolver.Pick(notes, 48, 30000, 0)!.Sample.Name);
        Assert.Equal("e3", RegionResolver.Pick(notes, 52, 30000, 0)!.Sample.Name);
    }

    [Fact]
    public async Task A_dry_run_tells_which_blocks_have_a_take_and_renders_nothing()
    {
        Rig rig = await RigAsync();
        static string Song(string pattern) => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\nmidi-clip c {{\n  chords Am\n  bars 1\n}}\ndrums d at 1 for 1 {{\n  pattern {pattern}\n}}\nmixer m {{\n  from d.audio -> audio1\n}}\n";
        BuildOptions dry = new() { PlanOnly = true };
        BuildReport none = await rig.Engine.BuildAsync(Parser.Parse(Song("beat-A"), EngineRig.Spec), dry);
        Assert.All(none.Blocks.Where(b => b.Block == "d"), b => Assert.Equal("stale", b.Status));
        Assert.Empty(rig.Bank.ListTakes());                                  // a dry run stores nothing
        await rig.Engine.BuildAsync(Parser.Parse(Song("beat-A"), EngineRig.Spec));
        int takes = rig.Bank.ListTakes().Count;
        BuildReport ready = await rig.Engine.BuildAsync(Parser.Parse(Song("beat-A"), EngineRig.Spec), dry);
        Assert.All(ready.Blocks, b => Assert.Equal("cached", b.Status));
        BuildReport changed = await rig.Engine.BuildAsync(Parser.Parse(Song("beat-B"), EngineRig.Spec), dry);
        Assert.Equal("cached", changed.Blocks.Single(b => b.Block == "c").Status);
        Assert.Equal("stale", changed.Blocks.Single(b => b.Block == "d").Status);
        Assert.Equal("waiting", changed.Blocks.Single(b => b.Block == "m").Status);
        Assert.Equal(takes, rig.Bank.ListTakes().Count);
    }

    [Fact]
    public async Task A_song_plays_the_named_pattern_of_each_bar_and_a_block_without_a_song_is_unchanged()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        string Song(string extra) => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\ndrums d at 1 for 3 {{\n  kit samples/kit\n  pattern kick:x...\n{extra}}}\n";
        short[] plain = await RenderAsync(rig, Song(string.Empty));
        int bar = 88200;
        short kick = (short)(30000 * (26000 * 3 / 4) >> 15), snare = (short)(20000 * (26000 * 3 / 4) >> 15);
        Assert.Equal(kick, plain[0]);
        Assert.Equal(kick, plain[bar]);                                   // the plain pattern plays on every bar
        short[] sections = await RenderAsync(rig, Song("  patterns fill=snare:x...\n  song pattern fill pattern\n"));
        Assert.Equal(kick, sections[0]);
        Assert.Equal(snare, sections[bar]);                               // bar two is the fill: a snare and no kick
        Assert.Equal(kick, sections[2 * bar]);
        Assert.Equal(plain.Take(bar).ToArray(), sections.Take(bar).ToArray());
        short[] wrapped = await RenderAsync(rig, Song("  patterns fill=snare:x...\n  song pattern fill\n"));
        Assert.Equal(kick, wrapped[2 * bar]);                             // the sequence starts again in bar three
    }

    // P06 (plan/patterns/P06-tests.md): per-voice options (@l, @m, ...) read the same inside a named pattern as in
    // the plain one, and a voice missing from the kit but filled by the block's own fallback warns once for the
    // whole block (SourceOf's per-voice memoization), not once per bar the named pattern plays.
    [Fact]
    public async Task A_named_patterns_voice_options_work_like_the_plain_patterns_and_a_missing_voice_warns_once()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        Wave(rig.Root, "samples/other/tom.wav", Impulse(16000));
        string song = $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\ndrums d at 1 for 4 {{\n  kit samples/kit\n  fallback samples/other\n  pattern kick:x...\n  patterns verse=kick:x...@l-6 tom:.x.. / outro=tom:x...\n  song verse*2 outro*2\n}}\n";
        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        string takeId = report.Blocks.Single(b => b.Block == "d").TakeId!;
        TakeDataRef warningsRef = rig.Bank.GetTake(takeId).Data.Single(d => d.Role == "warnings");
        using StreamReader reader = new(rig.Bank.OpenData(warningsRef.DataId));
        string message = await reader.ReadToEndAsync();
        Assert.Single(Regex.Matches(message, "AMP5256"));   // 4 bars, 2 patterns both using 'tom' - still one warning
        short[] audio = await RenderAsync(rig, song);
        string loudSong = song.Replace("verse=kick:x...@l-6 tom:.x..", "verse=kick:x... tom:.x..", StringComparison.Ordinal);
        short[] loud = await RenderAsync(rig, loudSong);
        Assert.InRange((int)audio[0], loud[0] / 2 - 200, loud[0] / 2 + 200);   // bar one (verse): the @l-6 option applies inside a named pattern too
    }

    [Fact]
    public async Task A_wrong_name_in_the_song_or_a_wrong_grid_in_a_pattern_fails_the_block_with_AMP5351()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        string Song(string extra) => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\ndrums d at 1 for 1 {{\n  kit samples/kit\n{extra}}}\n";
        BuildReport name = await rig.Engine.BuildAsync(Parser.Parse(Song("  song nope\n"), EngineRig.Spec));
        Assert.Contains("AMP5351", name.Blocks.Single().Message, StringComparison.Ordinal);
        BuildReport grid = await rig.Engine.BuildAsync(Parser.Parse(Song("  patterns bad=kick:q...\n"), EngineRig.Spec));
        Assert.Contains("AMP5351", grid.Blocks.Single().Message, StringComparison.Ordinal);   // reported even though the song does not use it
        BuildReport builtin = await rig.Engine.BuildAsync(Parser.Parse("composition \"s\" { ample 1  tempo 120  master-seed 1 }\ndrums d at 1 for 1 {\n  patterns verse=kick:x...\n}\n", EngineRig.Spec));
        Assert.Equal("rendered", builtin.Blocks.Single().Status);   // the built-in sound takes named patterns too (it only had beat-A and beat-B before the steps parameter)
    }

    [Fact]
    public async Task The_master_seed_only_changes_the_blocks_that_derive_their_seed_from_it()
    {
        Rig rig = await RigAsync();
        static string Song(int master) => $"composition \"s\" {{ ample 1  tempo 120  master-seed {master} }}\nmidi-clip c {{\n  chords Am\n  bars 1\n}}\ndrums d at 1 for 1 {{\n  pattern beat-A\n}}\ndrums f at 1 for 1 {{\n  pattern beat-A\n  seed 77\n}}\n";
        await rig.Engine.BuildAsync(Parser.Parse(Song(1), EngineRig.Spec));
        BuildReport again = await rig.Engine.BuildAsync(Parser.Parse(Song(2), EngineRig.Spec));
        Assert.Equal("cached", again.Blocks.Single(b => b.Block == "c").Status);   // no seed to derive
        Assert.Equal("cached", again.Blocks.Single(b => b.Block == "f").Status);   // a custom seed does not follow the master seed
        Assert.Equal("rendered", again.Blocks.Single(b => b.Block == "d").Status);   // its seed is derived from the master seed
    }

    [Fact]
    public async Task A_block_lists_the_files_it_reads_and_a_block_without_files_lists_none()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        string both = DrumSong("kick:x...") + "midi-clip c {\n  chords Am\n  bars 1\n}\n";
        AmpleDocument doc = Parser.Parse(both, EngineRig.Spec);
        IReadOnlyList<string> drums = BlockFiles.Of(rig.Root, null, doc, doc.Blocks.Single(b => b.Name == "d"));
        Assert.NotEmpty(drums);
        Assert.All(drums, f => Assert.StartsWith(Path.GetFullPath(Path.Combine(rig.Root, "samples", "kit")), Path.GetFullPath(f)));
        Assert.Empty(BlockFiles.Of(rig.Root, null, doc, doc.Blocks.Single(b => b.Name == "c")));
        string before = BlockFiles.Stamp(drums);
        Wave(rig.Root, "samples/kit/kick_v2_r1.wav", Impulse(6000));
        Assert.NotEqual(before, BlockFiles.Stamp(BlockFiles.Of(rig.Root, null, doc, doc.Blocks.Single(b => b.Name == "d"))));
    }

    [Fact]
    public async Task A_layered_kit_plays_the_layer_of_the_accent_and_plain_folders_keep_their_bytes()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        Wave(rig.Root, "samples/kit/kick_v2_r1.wav", Impulse(5000));
        short[] buf = await RenderAsync(rig, DrumSong("kick:oxX."));
        int step = 88200 / 16;
        Assert.Equal((short)(30000 * (14000 * 3 / 4) >> 15), buf[0]);
        Assert.Equal((short)(5000 * (26000 * 3 / 4) >> 15), buf[step]);
        Assert.Equal((short)(5000 * (32768 * 3 / 4) >> 15), buf[2 * step]);

        string both = DrumSong("kick:x...") + "midi-clip c {\n  chords Am\n  bars 1\n}\n";
        BuildReport first = await rig.Engine.BuildAsync(Parser.Parse(both, EngineRig.Spec));
        Wave(rig.Root, "samples/kit/kick_v2_r1.wav", Impulse(6000));
        BuildReport changed = await rig.Engine.BuildAsync(Parser.Parse(both, EngineRig.Spec));
        Assert.Equal("rendered", changed.Blocks.Single(b => b.Block == "d").Status);
        Assert.Equal("cached", changed.Blocks.Single(b => b.Block == "c").Status);
        Assert.NotEqual(first.Blocks.Single(b => b.Block == "d").TakeId, changed.Blocks.Single(b => b.Block == "d").TakeId);
    }

    [Fact]
    public void A_melody_note_may_carry_a_velocity_and_defaults_to_96()
    {
        var notes = BuiltInRenderers.MelodyWithVelocity("c4:1:110 d4:1 e4:1:500", 1, 1).Select(n => n.Velocity).ToList();
        Assert.Equal([110, 96, 127], notes);
    }

    [Fact]
    public async Task Round_robins_are_chosen_from_the_seed_and_are_the_same_on_every_run_and_project()
    {
        Rig one = await RigAsync("one");
        Rig two = await RigAsync("two");
        foreach (Rig rig in new[] { one, two })
        {
            Kit(rig.Root);
            for (int i = 2; i <= 4; i++)
            {
                Wave(rig.Root, $"samples/kit/kick_v1_r{i}.wav", Impulse((short)(30000 - (i * 1000))));
            }
        }

        string song = DrumSong("kick:xxxxxxxxxxxxxxxx");
        short[] a = await RenderAsync(one, song);
        Assert.Equal(a, await RenderAsync(two, song));
        int step = 88200 / 16;
        Assert.True(Enumerable.Range(0, 16).Select(i => a[i * step]).Distinct().Count() > 1);
        Assert.NotEqual(a, await RenderAsync(one, song.Replace("master-seed 1", "master-seed 2", StringComparison.Ordinal)));

        // The humanize stream is separate: the same hits get the same timing with or without extra round robins.
        Rig plain = await RigAsync("plain");
        Kit(plain.Root);
        string human = DrumSong("kick:xxxxxxxx", "  humanize 60\n");
        int[] timing(short[] b) => [.. Enumerable.Range(0, b.Length).Where(i => b[i] != 0)];
        Assert.Equal(timing(await RenderAsync(plain, human)), timing(await RenderAsync(one, human)));
    }

    [Fact]
    public async Task A_closing_hat_cuts_the_open_hat_with_a_fade_and_no_rule_changes_nothing()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        Wave(rig.Root, "samples/kit/open.wav", Flat(10000, 20000));
        int step = 88200 / 16;
        short[] free = await RenderAsync(rig, DrumSong("open:x... hat:..x."));
        short[] cut = await RenderAsync(rig, DrumSong("open:x... hat:..x.", "  choke open:hat\n"));
        int cutAt = 2 * step;
        int fade = Rate * 7 / 1000;
        Assert.Equal(free[..(cutAt - fade - 1)], cut[..(cutAt - fade - 1)]);
        Assert.True(free[cutAt + 50] > 0);
        Assert.True(cut[cutAt + 100] <= free[cutAt + 100]);
        Assert.True(cut[cutAt - 1] < free[cutAt - 1]);
        Assert.True(cut[cutAt + 1000] < free[cutAt + 1000] && cut[cutAt + 1000] == 0);
        Assert.Equal(await RenderAsync(rig, DrumSong("open:x... hat:..x.")), free);
        BuildReport bad = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("open:x...", "  choke nothing\n"), EngineRig.Spec));
        Assert.Contains("AMP5351", bad.Blocks.Single(b => b.Block == "d").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_voice_map_plays_a_sample_of_another_kit_and_a_fallback_kit_fills_missing_voices()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        Wave(rig.Root, "samples/other/kick2.wav", Impulse(12000));
        Wave(rig.Root, "samples/other/tom.wav", Impulse(16000));
        short[] plain = await RenderAsync(rig, DrumSong("kick:x..."));
        short[] mapped = await RenderAsync(rig, DrumSong("kick:x...", "  map kick:samples/other/kick2\n"));
        Assert.NotEqual(plain, mapped);
        Assert.True(mapped[0] > 0 && mapped[0] < plain[0]);

        BuildReport missing = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("tom:x..."), EngineRig.Spec));
        Assert.Contains("AMP5350", missing.Blocks.Single(b => b.Block == "d").Message, StringComparison.Ordinal);
        short[] filled = await RenderAsync(rig, DrumSong("tom:x...", "  fallback samples/other\n"));
        Assert.True(filled[0] > 0);

        BuildReport badMap = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x...", "  map kick:nothing\n"), EngineRig.Spec));
        Assert.Contains("AMP5351", badMap.Blocks.Single(b => b.Block == "d").Message, StringComparison.Ordinal);
        BuildReport noSample = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x...", "  map kick:samples/other/none\n"), EngineRig.Spec));
        Assert.Contains("AMP5350", noSample.Blocks.Single(b => b.Block == "d").Message, StringComparison.Ordinal);

        Wave(rig.Root, "samples/other/kick2.wav", Impulse(9000));
        Assert.NotEqual(mapped, await RenderAsync(rig, DrumSong("kick:x...", "  map kick:samples/other/kick2\n")));
    }

    [Fact]
    public async Task Drum_voice_options_mute_set_the_level_and_pan_into_a_stereo_render()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        short[] plain = await RenderAsync(rig, DrumSong("kick:x... snare:..x."));
        short[] muted = await RenderAsync(rig, DrumSong("kick:x...@m snare:..x."));
        int step = 88200 / 16;
        Assert.Equal(0, muted[0]);
        Assert.Equal(plain[2 * step], muted[2 * step]);
        short[] quieter = await RenderAsync(rig, DrumSong("kick:x...@l-6 snare:..x."));
        Assert.InRange((int)quieter[0], plain[0] / 2 - 200, plain[0] / 2 + 200);

        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x...@p-32 snare:..x."), EngineRig.Spec));
        BlockResult block = report.Blocks.Single(b => b.Block == "d");
        await using Stream data = rig.Bank.OpenData(rig.Bank.GetTake(block.TakeId!).Data[0].DataId);
        using MemoryStream ms = new();
        await data.CopyToAsync(ms);
        StereoBuffer stereo = StereoWav.Decode(ms.ToArray()).Buffer;
        Assert.True(stereo.Left[0] > 0 && stereo.Right[0] == 0);
        Assert.True(stereo.Left[2 * step] > 0 && stereo.Right[2 * step] > 0);

        BuildReport bad = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x...@z"), EngineRig.Spec));
        Assert.Contains("AMP5351", bad.Blocks.Single(b => b.Block == "d").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_digit_step_is_a_velocity_between_the_softest_and_the_loudest()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        short[] steps = await RenderAsync(rig, DrumSong("kick:9.5.1..."));
        int step = 88200 / 16;
        Assert.True(steps[0] > steps[2 * step] && steps[2 * step] > steps[4 * step] && steps[4 * step] > 0);
        short[] accent = await RenderAsync(rig, DrumSong("kick:X..."));
        Assert.Equal(accent[0], steps[0]);
    }

    [Fact]
    public async Task A_set_json_sets_gain_and_tune_of_the_set_and_goes_into_the_plan()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        short[] plain = await RenderAsync(rig, DrumSong("kick:x..."));
        await File.WriteAllTextAsync(Path.Combine(rig.Root, "samples/kit/set.json"), "{ \"gain\": -6 }");
        short[] quieter = await RenderAsync(rig, DrumSong("kick:x..."));
        Assert.InRange(quieter[0], plain[0] * 49 / 100, plain[0] * 52 / 100);
        BuildReport rep = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x..."), EngineRig.Spec));
        PlanRecord plan = rig.Bank.GetPlan(rig.Bank.GetTake(rep.Blocks.Single().TakeId!).PlanId).Plan;
        Assert.Contains(plan.Inputs, i => i.Kind == "sample" && i.Toolprint == "samples/kit/set.json");
        await File.WriteAllTextAsync(Path.Combine(rig.Root, "samples/kit/set.json"), "[1]");
        Assert.Contains("AMP5350", (await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:x..."), EngineRig.Spec))).Blocks.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drums_play_their_samples_on_the_grid_with_accent_ghost_and_swing()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        short[] buf = await RenderAsync(rig, DrumSong("kick:x...", string.Empty));
        int step = 88200 / 16;
        int[] hits = [.. Enumerable.Range(0, buf.Length).Where(i => buf[i] != 0)];
        Assert.Equal([0, 4 * step, 8 * step, 12 * step], hits);
        Assert.Equal((short)(30000 * (26000 * 3 / 4) >> 15), buf[0]);

        short[] mixed = await RenderAsync(rig, DrumSong("kick:X... snare:..o. hat:....", string.Empty));
        Assert.Equal((short)(30000 * (32768 * 3 / 4) >> 15), mixed[0]);
        Assert.Equal((short)(20000 * (14000 * 3 / 4) >> 15), mixed[2 * step]);
        Assert.Equal(0, mixed[step]);

        short[] swung = await RenderAsync(rig, DrumSong("kick:.x", "  swing 50\n"));
        int[] swungHits = [.. Enumerable.Range(0, swung.Length).Where(i => swung[i] != 0)];
        Assert.Equal(step + (step / 2), swungHits[0]);
        Assert.Equal(8, swungHits.Length);
    }

    [Fact]
    public async Task The_named_patterns_still_work_with_a_kit_and_a_bad_grid_says_what_is_allowed()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        short[] a = await RenderAsync(rig, DrumSong("beat-A"));
        Assert.Contains(a, v => v != 0);
        short[] b = await RenderAsync(rig, DrumSong("beat-B"));
        Assert.NotEqual(a, b);

        BuildReport bad = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick:xyz."), EngineRig.Spec));
        Assert.Contains("AMP5351", bad.Blocks.Single(r => r.Block == "d").Message, StringComparison.Ordinal);
        BuildReport missing = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("tom:x..."), EngineRig.Spec));
        Assert.Contains("has no 'tom.wav'", missing.Blocks.Single(r => r.Block == "d").Message, StringComparison.Ordinal);
        BuildReport noColon = await rig.Engine.BuildAsync(Parser.Parse(DrumSong("kick"), EngineRig.Spec));
        Assert.Contains("AMP5351", noColon.Blocks.Single(r => r.Block == "d").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Humanize_changes_the_feel_but_the_same_input_always_gives_the_same_bytes_on_any_project()
    {
        Rig one = await RigAsync("one");
        Rig two = await RigAsync("two");
        Kit(one.Root);
        Kit(two.Root);
        string song = DrumSong("kick:x.x.x.x. hat:xxxxxxxx", "  humanize 60\n");
        short[] a = await RenderAsync(one, song);
        short[] b = await RenderAsync(two, song);
        Assert.Equal(a, b);
        Assert.NotEqual(a, await RenderAsync(one, DrumSong("kick:x.x.x.x. hat:xxxxxxxx")));
    }

    [Fact]
    public async Task A_changed_sample_makes_a_new_take_and_the_plan_names_the_files_while_other_blocks_stay_cached()
    {
        Rig rig = await RigAsync();
        Kit(rig.Root);
        string song = DrumSong("kick:x...") + "midi-clip c {\n  chords Am\n  bars 1\n}\n";
        BuildReport first = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        Assert.All(first.Blocks, b => Assert.Equal("rendered", b.Status));
        PlanRecord plan = rig.Bank.GetPlan(rig.Bank.GetTake(first.Blocks.Single(b => b.Block == "d").TakeId!).PlanId).Plan;
        Assert.Equal(["samples/kit/hat.wav", "samples/kit/kick.wav", "samples/kit/snare.wav"], plan.Inputs.Where(i => i.Kind == "sample").Select(i => i.Toolprint));
        Assert.All(plan.Inputs.Where(i => i.Kind == "sample"), i => Assert.StartsWith("sha256:", i.Id, StringComparison.Ordinal));

        BuildReport again = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        Assert.All(again.Blocks, b => Assert.Equal("cached", b.Status));

        Wave(rig.Root, "samples/kit/kick.wav", Impulse(12345));
        BuildReport changed = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        Assert.Equal("rendered", changed.Blocks.Single(b => b.Block == "d").Status);
        Assert.Equal("cached", changed.Blocks.Single(b => b.Block == "c").Status);
    }

    private static string GuitarSong(string extra = "", string strum = "D...") => $"composition \"g\" {{ ample 1  tempo 120  master-seed 1 }}\nmidi-clip c {{\n  chords Am\n  bars 1\n}}\nguitar g at 1 for 1 {{\n  set samples/gtr\n  strum {strum}\n  voicing power\n{extra}  from c.notes -> notes\n}}\n";

    private static void Strings(string root, bool varied = false)
    {
        string[] names = ["e2", "a2", "c3", "eb3", "gb3", "a3"];
        for (int i = 0; i < names.Length; i++)
        {
            Wave(root, $"samples/gtr/{names[i]}.wav", Flat((short)(varied ? 1000 * (i + 1) : 1000), 44100));
        }
    }

    [Fact]
    public async Task A_strum_plays_the_strings_one_after_the_other_and_a_muted_or_empty_pattern_is_silent()
    {
        Rig rig = await RigAsync();
        Strings(rig.Root);
        short[] strum = await RenderAsync(rig, GuitarSong("  stagger 40\n"), "g");
        int gap = 40 * Rate / 1000;
        Assert.True(strum[100] > 0);
        int level1 = strum[100];
        Assert.InRange(strum[gap + 100], (level1 * 2) - 2, (level1 * 2) + 2);
        Assert.InRange(strum[(2 * gap) + 100], (level1 * 3) - 3, (level1 * 3) + 3);

        short[] silent = await RenderAsync(rig, GuitarSong(string.Empty, "x..."), "g");
        Assert.All(silent, v => Assert.Equal(0, v));
        short[] rests = await RenderAsync(rig, GuitarSong(string.Empty, "...."), "g");
        Assert.All(rests, v => Assert.Equal(0, v));

        short[] soft = await RenderAsync(rig, GuitarSong("  stagger 40\n", "d..."), "g");
        Assert.True(soft[100] < strum[100]);
        short[] louder = await RenderAsync(rig, GuitarSong("  stagger 40\n  gain 6\n"), "g");
        Assert.True(louder[100] > strum[100]);
    }

    [Fact]
    public async Task Sample_set_guitar_applies_per_note_pan_through_stereo_rendering()
    {
        Rig rig = await RigAsync();
        Strings(rig.Root);
        string song = GuitarSong().Replace("  chords Am", "  notes \"1:0:2:45:96;pan=-100;fine=25;cut=50\"", StringComparison.Ordinal);
        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        BlockResult block = report.Blocks.Single(result => result.Block == "g");
        Assert.True(block.Status is "rendered" or "cached", block.Message);
        TakeRecord take = rig.Bank.GetTake(block.TakeId!);
        await using Stream data = rig.Bank.OpenData(take.Data[0].DataId);
        using MemoryStream bytes = new();
        await data.CopyToAsync(bytes);
        StereoBuffer stereo = ChannelAudio.Decode(bytes.ToArray()).Buffer;
        Assert.Contains(stereo.Left, sample => sample != 0);
        Assert.All(stereo.Right, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task The_voicings_and_the_order_of_the_chord_change_what_is_heard_and_the_notes_come_from_the_nearest_sample()
    {
        Rig rig = await RigAsync();
        Strings(rig.Root, varied: true);
        short[] power = await RenderAsync(rig, GuitarSong(string.Empty, "D..."), "g");
        short[] open = await RenderAsync(rig, GuitarSong(string.Empty, "D...").Replace("voicing power", "voicing open", StringComparison.Ordinal), "g");
        short[] triad = await RenderAsync(rig, GuitarSong(string.Empty, "D...").Replace("voicing power", "voicing triad", StringComparison.Ordinal), "g");
        int gap = 22 * Rate / 1000;
        Assert.True(open[(4 * gap) + 100] > power[(4 * gap) + 100]);                             // five strings against three
        Assert.NotEqual(power, triad);

        short[] down = await RenderAsync(rig, GuitarSong("  stagger 40\n", "D..."), "g");
        short[] up = await RenderAsync(rig, GuitarSong("  stagger 40\n", "U..."), "g");
        Assert.Equal(down.Length, up.Length);
        Assert.True(down[100] > 0 && up[100] > 0);
    }

    [Fact]
    public async Task A_guitar_without_a_set_or_with_a_bad_pattern_says_what_to_do()
    {
        Rig rig = await RigAsync();
        Strings(rig.Root);
        string noSet = GuitarSong().Replace("set samples/gtr", "set felt", StringComparison.Ordinal);
        Assert.Contains("needs a sample folder", (await rig.Engine.BuildAsync(Parser.Parse(noSet, EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Message, StringComparison.Ordinal);
        Assert.Contains("AMP5351", (await rig.Engine.BuildAsync(Parser.Parse(GuitarSong(string.Empty, "Z..."), EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Message, StringComparison.Ordinal);
        Assert.Contains("voicing must be", (await rig.Engine.BuildAsync(Parser.Parse(GuitarSong().Replace("voicing power", "voicing wide", StringComparison.Ordinal), EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Message, StringComparison.Ordinal);

        Directory.Delete(Path.Combine(rig.Root, "samples", "gtr"), true);
        Wave(rig.Root, "samples/gtr/kick.wav", Impulse(1));
        Assert.Contains("named by note", (await rig.Engine.BuildAsync(Parser.Parse(GuitarSong(), EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_words_come_from_the_lyrics_file_without_bar_markers_and_a_change_of_the_file_makes_a_new_take()
    {
        Rig rig = await RigAsync();
        Directory.CreateDirectory(Path.Combine(rig.Root, "lyrics"));
        string file = Path.Combine(rig.Root, "lyrics", "song.lyr");
        await File.WriteAllTextAsync(file, "[bar 1] Morning light\n[bar 2] on the road\n");
        string song = "composition \"l\" { ample 1  tempo 100  master-seed 1 }\nlyrics w {\n  lang en\n  file lyrics/song.lyr\n}\n";
        BuildReport first = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        string text = await ReadAsync(rig, first.Blocks.Single().TakeId!);
        Assert.Equal("Morning light\non the road", text);
        PlanRecord plan = rig.Bank.GetPlan(rig.Bank.GetTake(first.Blocks.Single().TakeId!).PlanId).Plan;
        Assert.Contains(plan.Inputs, i => i.Kind == "file" && i.Toolprint == "lyrics/song.lyr");

        Assert.Equal("cached", (await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec))).Blocks.Single().Status);
        await File.WriteAllTextAsync(file, "[bar 1] Evening light\n");
        BuildReport changed = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        Assert.Equal("rendered", changed.Blocks.Single().Status);
        Assert.Equal("Evening light", await ReadAsync(rig, changed.Blocks.Single().TakeId!));

        File.Delete(file);
        BuildReport fallback = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        // The lyrics block no longer has a `text` parameter: without its file there are no words.
        Assert.Equal("", await ReadAsync(rig, fallback.Blocks.Single().TakeId!));
    }

    private static async Task<string> ReadAsync(Rig rig, string takeId)
    {
        await using Stream data = rig.Bank.OpenData(rig.Bank.GetTake(takeId).Data[0].DataId);
        using StreamReader reader = new(data);
        return await reader.ReadToEndAsync();
    }

    // SFZ02 (plan/sfz-import/README.md): a `set` value naming an .sfz file resolves through SfzReader, plays
    // through the same guitar renderer and the same RegionResolver.Pick as a plain note-named WAV folder.
    [Fact]
    public async Task A_guitar_set_can_name_an_sfz_file_instead_of_a_folder_of_note_named_WAVs()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/gtr/a2.wav", Flat(1000, 200));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        BlockResult block = report.Blocks.Single(b => b.Block == "g");
        Assert.True(block.Status is "rendered" or "cached", block.Message);
        PlanRecord plan = rig.Bank.GetPlan(rig.Bank.GetTake(block.TakeId!).PlanId).Plan;
        Assert.Contains(plan.Inputs, i => i.Kind == "sample" && i.Toolprint == "samples/gtr.sfz");
        Assert.Contains(plan.Inputs, i => i.Kind == "sample" && i.Toolprint == "samples/gtr/a2.wav");
    }

    [Fact]
    public async Task Changing_the_sample_an_sfz_file_references_makes_a_new_take_changing_an_unrelated_file_does_not()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/gtr/a2.wav", Flat(1000, 200));
        Wave(rig.Root, "samples/gtr/unused.wav", Flat(1, 200));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        BuildReport first = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        Assert.Equal("cached", (await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Status);

        Wave(rig.Root, "samples/gtr/unused.wav", Flat(2, 200));
        Assert.Equal("cached", (await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Status);

        Wave(rig.Root, "samples/gtr/a2.wav", Flat(2000, 200));
        Assert.Equal("rendered", (await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec))).Blocks.Single(b => b.Block == "g").Status);
    }

    // S08 (plan/sampler/S08-release-and-sustain-loops.md): set.json "loops" turns a sustain loop on for one base
    // name only; a set without it plays unchanged (silence past the end of the raw recording, as always).
    [Fact]
    public async Task A_set_json_loop_sustains_past_the_end_of_the_raw_recording_a_set_without_one_does_not()
    {
        Rig rig = await RigAsync();
        short[] raw = [.. Enumerable.Range(1, 12).Select(i => (short)(i * 1000))]; // 12 distinct, non-zero frames
        Wave(rig.Root, "samples/gtr/a2.wav", raw);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr", "set.json"), """{ "loops": { "a2": { "start": 0, "end": 10 } } }""");
        short[] looped = await RenderAsync(rig, GuitarSong(), "g");
        Assert.NotEqual(0, looped[20000]); // long past the 12-frame recording: still sounding because of the loop

        Rig rig2 = await RigAsync("proj2");
        Wave(rig2.Root, "samples/gtr/a2.wav", raw);
        short[] unlooped = await RenderAsync(rig2, GuitarSong(), "g");
        Assert.Equal(0, unlooped[20000]); // unchanged: no set.json, so no loop, silence once the 12 frames are used up
    }

    [Fact]
    public async Task A_set_json_envelope_attack_ramps_up_from_zero_before_the_sustain_level()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/gtr/a2.wav", Flat(20000, 20000));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr", "set.json"), """{ "envelope": { "attack": 50, "decay": 0, "sustain": 100, "release": 0 } }""");
        short[] shaped = await RenderAsync(rig, GuitarSong(), "g");
        int early = Math.Abs((int)shaped[100]);   // well inside the 50 ms (~2205 frame) attack ramp
        int late = Math.Abs((int)shaped[4000]);    // past the attack, at the sustain level
        Assert.True(early < late / 4, $"early (attack) {early} should be much quieter than late (sustain) {late}");
    }

    // SFZ08 (plan/sfz-import/README.md Phase 2): cutoff measurably darkens the output at a known frequency.
    [Fact]
    public async Task An_sfz_cutoff_measurably_darkens_a_high_frequency_sample()
    {
        Rig rig = await RigAsync();
        short[] alternating = [.. Enumerable.Range(0, 400).Select(i => (short)(i % 2 == 0 ? 20000 : -20000))]; // near-Nyquist content
        Wave(rig.Root, "samples/gtr/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45\n");
        string plain = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] unfiltered = await RenderAsync(rig, plain, "g");

        Wave(rig.Root, "samples/gtr2/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr2.sfz"), "<region> sample=gtr2/a2.wav key=45 cutoff=200 fil_type=lpf_2p\n");
        string filteredSong = GuitarSong().Replace("set samples/gtr", "set samples/gtr2.sfz", StringComparison.Ordinal);
        short[] filtered = await RenderAsync(rig, filteredSong, "g");

        int peak(short[] s) => s.Select(v => Math.Abs((int)v)).DefaultIfEmpty(0).Max();
        Assert.True(peak(filtered) < peak(unfiltered) / 2, $"filtered peak {peak(filtered)} should be much lower than unfiltered peak {peak(unfiltered)}");
    }

    // SFZ08 (plan/sfz-import/README.md Phase 2): fil_veltrack opens the cutoff at high velocity - a soft hit stays
    // darker than a hard hit of the exact same sample, through the real guitar render path.
    [Fact]
    public async Task Fil_veltrack_opens_the_cutoff_at_higher_velocity()
    {
        Rig rig = await RigAsync();
        short[] alternating = [.. Enumerable.Range(0, 400).Select(i => (short)(i % 2 == 0 ? 20000 : -20000))];
        Wave(rig.Root, "samples/gtr/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45 cutoff=100 fil_veltrack=4800\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal).Replace("strum D...", "strum d...", StringComparison.Ordinal);
        short[] soft = await RenderAsync(rig, song, "g");
        string loud = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] hard = await RenderAsync(rig, loud, "g");

        int peak(short[] s) => s.Select(v => Math.Abs((int)v)).DefaultIfEmpty(0).Max();
        Assert.True(peak(soft) < peak(hard), $"a softer strum ({peak(soft)}) should sound darker (quieter at this high-frequency content) than a harder one ({peak(hard)}) once fil_veltrack opens the cutoff");
    }

    // SFZ05 (plan/sfz-import/README.md): a drums "kit" naming an .sfz file maps its keyed regions to voice names
    // through the General MIDI drum map, and plays through the real drums pattern/choke path unchanged.
    [Fact]
    public async Task A_drums_kit_can_name_an_sfz_file_mapped_through_the_GM_drum_map()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/kit/kick.wav", Impulse(30000));
        Wave(rig.Root, "samples/kit/snare.wav", Impulse(20000));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "kit.sfz"), "<region> sample=kit/kick.wav key=36\n<region> sample=kit/snare.wav key=38\n");
        string song = DrumSong("kick:x...x... snare:....x.......").Replace("kit samples/kit", "kit samples/kit.sfz", StringComparison.Ordinal);
        short[] audio = await RenderAsync(rig, song);
        Assert.True(audio[0] != 0); // the kick hit at step 0
    }

    // SFZ03 (plan/sfz-import/README.md): group/off_by from an .sfz kit choke a voice exactly like the drums block's
    // own choke parameter would, through the one existing choke-and-fade mixing pass.
    [Fact]
    public async Task An_sfz_kits_group_and_off_by_choke_an_open_hat_voice()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/kit/hatopen.wav", Flat(20000, 20000));  // a long ringing open hat
        Wave(rig.Root, "samples/kit/hatclosed.wav", Impulse(10000));    // GM key 44 (pedal hi-hat) maps to the generic "hat" voice name
        File.WriteAllText(Path.Combine(rig.Root, "samples", "kit.sfz"), """
            <region> sample=kit/hatopen.wav key=46 group=1
            <region> sample=kit/hatclosed.wav key=44 off_by=1

            """);
        string song = DrumSong("hat-open:x............... hat:....x...........").Replace("kit samples/kit", "kit samples/kit.sfz", StringComparison.Ordinal);
        short[] audio = await RenderAsync(rig, song);
        int stepLength = audio.Length / 16;
        Assert.True(audio[stepLength / 2] != 0);        // the open hat is ringing right after its own hit
        Assert.Equal(0, audio[(4 * stepLength) + 2000]); // long after the closed-hat (closer) hit, it has been choked silent
    }

    // SFZ12 (plan/sfz-import/README.md): region-level choke precision. A voice with two regions - a soft,
    // ungrouped articulation and a hard, grouped one - only has its grouped region choked; the ungrouped one keeps
    // ringing even though it shares the same voice name (which a purely voice-name choke rule could not tell apart).
    [Fact]
    public async Task An_sfz_kits_off_by_chokes_only_the_grouped_region_not_the_whole_voice()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/kit/hatsoft.wav", Flat(20000, 20000));   // ungrouped: should keep ringing
        Wave(rig.Root, "samples/kit/hathard.wav", Flat(20000, 20000));   // group=1: should be choked
        Wave(rig.Root, "samples/kit/crash.wav", Impulse(10000));         // off_by=1
        File.WriteAllText(Path.Combine(rig.Root, "samples", "kit.sfz"), """
            <region> sample=kit/hatsoft.wav key=42 lovel=0 hivel=63
            <region> sample=kit/hathard.wav key=42 lovel=64 hivel=127 group=1
            <region> sample=kit/crash.wav key=49 off_by=1

            """);
        string song = DrumSong("hat:ox.............. crash:...x............").Replace("kit samples/kit", "kit samples/kit.sfz", StringComparison.Ordinal);
        short[] audio = await RenderAsync(rig, song);
        int stepLength = audio.Length / 16;
        // hatsoft starts at step 0 (no group): still ringing well into its own natural length, unaffected by the crash.
        Assert.NotEqual(0, audio[15000]);
        // hathard starts at step 1 (group=1): the crash at step 3 (off_by=1) cuts it a step later, well before its
        // own natural end (it is a 20000-sample flat tone) - silent here, where it would otherwise still be ringing.
        Assert.Equal(0, audio[stepLength + 15000]);
    }

    // SFZ04 (plan/sfz-import/README.md Phase 2): loop_mode=loop_continuous actually sustains past the raw recording,
    // through the real guitar render path, now that S08's loop playback is wired in.
    [Fact]
    public async Task An_sfz_sourced_loop_sustains_past_the_end_of_the_recording()
    {
        Rig rig = await RigAsync();
        short[] raw = [.. Enumerable.Range(1, 12).Select(i => (short)(i * 1000))];
        Wave(rig.Root, "samples/gtr/a2.wav", raw);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45 loop_mode=loop_continuous loop_start=0 loop_end=10\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] looped = await RenderAsync(rig, song, "g");
        Assert.NotEqual(0, looped[20000]); // long past the 12-frame recording: still sounding because of the loop
    }

    [Fact]
    public async Task An_sfz_ampeg_attack_shapes_the_rendered_note()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/gtr/a2.wav", Flat(20000, 20000));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45 ampeg_attack=0.05 ampeg_sustain=100\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] shaped = await RenderAsync(rig, song, "g");
        Assert.True(Math.Abs((int)shaped[100]) < Math.Abs((int)shaped[4000]) / 4);
    }

    // SFZ09 (block 1): a pitch LFO measurably changes the rendered waveform of an otherwise-identical note.
    [Fact]
    public async Task An_sfz_pitch_lfo_measurably_changes_the_waveform()
    {
        Rig rig = await RigAsync();
        short[] alternating = [.. Enumerable.Range(0, 20000).Select(i => (short)(i % 2 == 0 ? 20000 : -20000))];
        Wave(rig.Root, "samples/gtr/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45\n");
        string plain = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] steady = await RenderAsync(rig, plain, "g");

        Wave(rig.Root, "samples/gtr2/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr2.sfz"), "<region> sample=gtr2/a2.wav key=45 lfo01_freq=8 lfo01_pitch=400\n");
        string wobbled = GuitarSong().Replace("set samples/gtr", "set samples/gtr2.sfz", StringComparison.Ordinal);
        short[] lfo = await RenderAsync(rig, wobbled, "g");

        Assert.NotEqual(steady, lfo);
    }

    // SFZ08 (block 2): a filter envelope measurably darkens the note early and opens it up later.
    [Fact]
    public async Task An_sfz_filter_envelope_opens_the_cutoff_over_time()
    {
        Rig rig = await RigAsync();
        short[] alternating = [.. Enumerable.Range(0, 20000).Select(i => (short)(i % 2 == 0 ? 20000 : -20000))];
        Wave(rig.Root, "samples/gtr/a2.wav", alternating);
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), "<region> sample=gtr/a2.wav key=45 cutoff=200 fileg_attack=0.3 fileg_depth=6000\n");
        string song = GuitarSong().Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] shaped = await RenderAsync(rig, song, "g");

        int peakAround(int center, int radius) => shaped.Skip(Math.Max(0, center - radius)).Take(radius * 2).Select(v => Math.Abs((int)v)).DefaultIfEmpty(0).Max();
        int early = peakAround(500, 200);
        int late = peakAround(10000, 200);
        Assert.True(early < late, $"early (closed filter) {early} should be quieter than late (open filter) {late} at this high-frequency content");
    }

    // S08/SFZ12 (block 3): trigger=release actually plays a distinct companion sample at note-off.
    [Fact]
    public async Task An_sfz_release_triggered_region_plays_at_note_off()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/gtr/a2.wav", Flat(1000, 200));
        Wave(rig.Root, "samples/gtr/relnote.wav", Flat(30000, 20000));
        File.WriteAllText(Path.Combine(rig.Root, "samples", "gtr.sfz"), """
            <region> sample=gtr/a2.wav key=45
            <region> sample=gtr/relnote.wav key=45 trigger=release
            """);
        // "Dx..." (D at step 0, an explicit mute at step 1) gives the D hit a short, predictable "until next event"
        // length: stepLength(=samplesPerBar/16) + sampleRate/8, at 120 BPM/44100 Hz = 5512 + 5512 = 11024 samples.
        string song = GuitarSong(string.Empty, "Dx..............").Replace("set samples/gtr", "set samples/gtr.sfz", StringComparison.Ordinal);
        short[] audio = await RenderAsync(rig, song, "g");
        Assert.True(Math.Abs((int)audio[2000]) < 100);       // the 200-frame main recording has long since ended, release not due yet (power voicing stacks several staggered strings, so a tiny residual is expected)
        Assert.True(Math.Abs((int)audio[12000]) > 10000);    // past the ~11024-sample note length: the release companion is sounding
    }

    // SFZ12 (block 8): polyphony caps simultaneous hits of one voice.
    [Fact]
    public async Task An_sfz_kits_polyphony_limit_drops_excess_simultaneous_hits()
    {
        Rig rig = await RigAsync();
        Wave(rig.Root, "samples/kit/kick.wav", Flat(20000, 20000)); // long ringing hit so hits overlap
        File.WriteAllText(Path.Combine(rig.Root, "samples", "kit.sfz"), "<region> sample=kit/kick.wav key=36 polyphony=1\n");
        string song = DrumSong("kick:xxxxxxxxxxxxxxxx").Replace("kit samples/kit", "kit samples/kit.sfz", StringComparison.Ordinal);
        short[] limited = await RenderAsync(rig, song);

        Rig rig2 = await RigAsync("proj2");
        Wave(rig2.Root, "samples/kit/kick.wav", Flat(20000, 20000));
        File.WriteAllText(Path.Combine(rig2.Root, "samples", "kit.sfz"), "<region> sample=kit/kick.wav key=36\n");
        string songUnlimited = DrumSong("kick:xxxxxxxxxxxxxxxx").Replace("kit samples/kit", "kit samples/kit.sfz", StringComparison.Ordinal);
        short[] unlimited = await RenderAsync(rig2, songUnlimited);

        int countNonSilent(short[] s) => s.Count(v => v != 0);
        Assert.True(countNonSilent(limited) < countNonSilent(unlimited), "capping polyphony to 1 should leave fewer non-silent samples than the unlimited render");
    }
}
