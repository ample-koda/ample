using System.Text;
using Ample.Audio.Engine;
using Ample.Audio.Instruments;
using Ample.Audio.Midi;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Core.Tests.Instruments;

/// <summary>
/// The SF2 features beyond the basics: 24-bit and SF3 samples, linked modulators, the pitch wheel and pan as modulators, RPN and NRPN, pressure, sostenuto, soft pedal,
/// the chorus output, fixed key and velocity, and times that follow a controller during a note.
/// </summary>
public sealed class Sf2FeatureTests : IDisposable
{
    private const int Rate = 44100;
    private static readonly string[] AudioOutputs = ["audio", "send", "chorus"];
    private readonly string folder = Directory.CreateTempSubdirectory("ample-sf2-features-").FullName;

    // a 0.25 s sine of 441 Hz at 22050 Hz, mono Ogg Vorbis (made with ffmpeg, libvorbis -q 0): the sample of the SF3 fixture
    private const string ToneOgg = "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";

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static int[] Gens()
    {
        int[] gens = new int[61];
        gens[8] = 13500;
        foreach (int oper in new[] { 21, 23, 25, 26, 27, 28, 30, 33, 34, 35, 36, 38 }) gens[oper] = -12000;
        gens[46] = gens[47] = gens[58] = -1;
        gens[56] = 100;
        return gens;
    }

    private static Sf2PlayableRegion Region(int[] gens, params Sf2Mod[] mods) =>
        new(0, 0, 127, 0, 127, 60, 0, 32768, 0, 100, 0, 100, false, false, 0, 0, 32768, 0, 13500, 0, gens, mods);

    private static byte[] Looped(params Sf2Fixture.Zone[] extra) =>
        Sf2Fixture.Build([("tone", 512, (32u, 480u), 60)], [("lead", [new Sf2Fixture.Zone([(Sf2Fixture.SampleModes, (short)1), .. extra.SelectMany(z => z.Gens), (Sf2Fixture.SampleId, (short)0)])])], [("Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]);

    private Task<IReadOnlyList<RenderOutput>> RenderAsync(byte[] font, string notes, params MidiPerformanceEvent[] events) => RenderAsync(font, notes, null, events);

    private async Task<IReadOnlyList<RenderOutput>> RenderAsync(byte[] font, string notes, string? engine, params MidiPerformanceEvent[] events)
    {
        string file = $"font-{Guid.NewGuid():N}.sf2";
        File.WriteAllBytes(Path.Combine(folder, file), font);
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", file, 0));
        block.Parameters.Add(new Parameter("program", "5", 1));
        block.Parameters.Add(new Parameter("level", "raw", 2));
        if (engine is not null) block.Parameters.Add(new Parameter("engine", engine, 3));
        Dictionary<string, byte[]> inputs = new(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[" + notes + "]}") };
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, inputs, 0, folder) { PerformanceEvents = events.Length == 0 ? null : events };
        return await BuiltInRenderers.All.Single(r => r.BlockType == "gm").RenderAsync(request, CancellationToken.None);
    }

    private static string Note(int pitch, string beats = "2", int velocity = 100) => $"{{\"bar\":1,\"pitch\":{pitch},\"beat\":0,\"beats\":{beats},\"velocity\":{velocity},\"channel\":0}}";

    private static short[] Audio(IReadOnlyList<RenderOutput> outputs, string role = "audio") => Wav.Decode(outputs.Single(o => o.Role == role).Data).Samples;

    private static long Energy(short[] audio, int from, int to) => audio[from..Math.Min(to, audio.Length)].Sum(x => Math.Abs((long)x));

    private static MidiPerformanceEvent Cc(int sample, int number, int value) => new(sample, SmfEventKind.ControlChange, 0, number, value);

    [Fact]
    public void A_24_bit_font_gives_the_low_byte_below_the_16_bits()
    {
        Sf2Bank bank = Sf2Reader.Read(Sf2Fixture.Build([("tone", 64, (8u, 56u), 60)], [("i", [new Sf2Fixture.Zone((Sf2Fixture.SampleId, 0))])], [("p", 0, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])], sm24: true));
        Assert.True(bank.Has24Bit);
        Assert.Equal(8, bank.ExtraBits);
        short[] high = bank.ReadSampleData(0);
        int[] frames = bank.ReadSampleFrames(0);
        for (int i = 0; i < high.Length; i++) Assert.Equal((high[i] << 8) | (byte)(i * 37), frames[i]);
        Assert.False(Sf2Reader.Read(Sf2Fixture.Tiny()).Has24Bit);
    }

    [Fact]
    public async Task A_16_bit_font_renders_as_before_and_a_24_bit_one_plays()
    {
        byte[] plain = Looped(), deep = Sf2Fixture.Build([("tone", 512, (32u, 480u), 60)], [("lead", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.SampleId, 0))])], [("Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])], sm24: true);
        short[] a = Audio(await RenderAsync(plain, Note(60))), b = Audio(await RenderAsync(deep, Note(60)));
        Assert.Contains(b, x => x != 0);
        // the low byte adds less than one 16-bit step per sample, so the two stay within a step or two of each other
        Assert.All(Enumerable.Range(0, a.Length), i => Assert.InRange(b[i] - a[i], -3, 3));
    }

    [Fact]
    public async Task An_SF3_sample_is_decoded_from_Ogg_Vorbis_and_played()
    {
        byte[] font = Sf2Fixture.Build([("tone", 0, (100u, 5000u), 60)], [("i", [new Sf2Fixture.Zone((Sf2Fixture.SampleModes, 1), (Sf2Fixture.SampleId, 0))])], [("Lead", 5, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])], ogg: Convert.FromBase64String(ToneOgg));
        Sf2Bank bank = Sf2Reader.Read(font);
        Assert.True(bank.Samples[0].Compressed);
        bank.LayOut(0);
        short[] data = bank.ReadSampleData(0);
        Assert.InRange(data.Length, 5400, 5700);   // 0.25 s at 22050 Hz
        Assert.Equal(bank.Samples[0].Start + (uint)data.Length, bank.Samples[0].End);
        Assert.Equal(bank.Samples[0].Start + 100u, bank.Samples[0].LoopStart);   // the loop counts from the decoded start
        int crossings = Enumerable.Range(1000, 2205).Count(i => data[i - 1] < 0 && data[i] >= 0);
        Assert.InRange(crossings, 42, 46);   // 441 Hz: 44 upward crossings in 0.1 s
        // an SF3 font needs engine float: its samples are decoded with floating point
        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(() => RenderAsync(font, Note(60)));
        Assert.Equal("AMP5311", refused.DiagnosticCode);
        Assert.Contains("engine float", refused.Message, StringComparison.Ordinal);
        IReadOnlyList<RenderOutput> played = await RenderAsync(font, Note(60), "float");
        short[] audio = Audio(played);
        Assert.Contains("machine-exact", Encoding.UTF8.GetString(played.Single(o => o.Role == "warnings").Data), StringComparison.Ordinal);
        Assert.True(Energy(audio, 0, Rate) > 0);
    }

    [Fact]
    public void Linked_modulators_feed_their_output_into_another_one()
    {
        // velocity goes, through a link, into a modulator that opens the filter by up to 2400 cents
        Sf2Mod velocityIntoLink = new(0x0002, 0x8001, 32767, 0, 0, LinkTarget: 1);
        Sf2Mod linkToFilter = new(0x007F, 8, 2400, 0, 0);
        int[] gens = Gens();
        gens[8] = 6000;
        Sf2PlayableRegion region = Region(gens, velocityIntoLink, linkToFilter);
        int soft = Sf2Program.Note(region, 60, 16).FilterCents, loud = Sf2Program.Note(region, 60, 127).FilterCents;
        Assert.InRange(soft - 6000, 250, 350);   // 16/128 of 2400
        Assert.InRange(loud - 6000, 2350, 2400);
    }

    [Fact]
    public void The_pitch_wheel_is_a_modulator_with_the_range_of_RPN_0()
    {
        Sf2PlayableRegion region = Region(Gens(), Sf2Modulators.Defaults);
        Sf2Controllers controllers = new() { PitchWheel = 16383 };
        Assert.InRange(Sf2Program.Note(region, 60, 100, controllers).TuneCents, 190, 200);   // two semitones by default
        foreach ((int number, int value) in new[] { (101, 0), (100, 0), (6, 12), (38, 0) }) controllers.Control(number, value);
        Assert.Equal(1200, controllers.BendRangeCents);
        Assert.InRange(Sf2Program.Note(region, 60, 100, controllers).TuneCents, 1180, 1200);
        controllers.PitchWheel = 0;
        Assert.InRange(Sf2Program.Note(region, 60, 100, controllers).TuneCents, -1200, -1180);
    }

    [Fact]
    public void An_SF2_NRPN_offsets_a_generator()
    {
        Sf2PlayableRegion region = Region(Gens(), Sf2Modulators.Defaults);
        Sf2Controllers controllers = new();
        foreach ((int number, int value) in new[] { (99, 120), (98, 8), (6, 64), (38, 0) }) controllers.Control(number, value);   // generator 8 (filter cutoff), 8192 + 0
        Assert.Equal(0, controllers.NrpnOffsets[8]);
        controllers.Control(6, 56);   // (56 * 128 - 8192) steps of 2 cents (the NRPN scale of the filter cutoff) = -2048
        Assert.Equal(-2048, controllers.NrpnOffsets[8]);
        Assert.Equal(13500 - 2048, Sf2Program.Note(region, 60, 100, controllers).FilterCents);
    }

    [Fact]
    public void A_retuned_LFO_keeps_its_phase_and_a_changed_envelope_does_not_jump()
    {
        Sf2Lfo slow = new(-12000, 0, Rate), fast = new(-12000, 1200, Rate);
        Sf2Lfo retuned = slow.Retuned(fast, 3000);
        Assert.Equal(slow.At(3000), retuned.At(3000));
        Assert.NotEqual(slow.At(6000), retuned.At(6000));

        Sf2VolEnvelope envelope = new(-12000, 0, -12000, 0, 200, Rate / 10, Rate);   // attack about 1 s, decay 1 s to -20 dB
        Sf2VolEnvelope shorter = envelope.Following(new Sf2VolEnvelope(-12000, -2400, -12000, -2400, 200, Rate / 10, Rate), 10000, 40000);
        Assert.Equal(envelope.Gain(10000, 40000), shorter.Gain(10000, 40000));   // the attack has begun: it keeps its length
        Assert.NotEqual(envelope.Gain(Rate + 4000, 80000), shorter.Gain(Rate + 4000, 80000));   // the decay had not begun: it is shorter now
    }

    [Fact]
    public async Task Sostenuto_holds_a_sounding_note_and_the_soft_pedal_makes_a_note_softer()
    {
        byte[] font = Looped();
        int beat = Rate;
        short[] plain = Audio(await RenderAsync(font, Note(60, "0.5")));
        short[] held = Audio(await RenderAsync(font, Note(60, "0.5"), Cc(beat / 4, 66, 127), Cc(beat * 2, 66, 0)));
        Assert.True(Energy(held, beat, beat * 2) > 10 * Math.Max(1, Energy(plain, beat, beat * 2)), "sostenuto keeps the note after its end");
        short[] before = Audio(await RenderAsync(font, Note(60, "0.5"), Cc(0, 66, 127), Cc(beat * 2, 66, 0)));
        // pressed at the very start counts as pressed while the note sounds (same sample); a note struck after the press is not held:
        short[] later = Audio(await RenderAsync(font, "{\"bar\":1,\"pitch\":60,\"beat\":1,\"beats\":0.5,\"velocity\":100,\"channel\":0}", Cc(beat / 2, 66, 127), Cc(beat * 3, 66, 0)));
        Assert.True(Energy(before, beat, beat * 2) > 0);
        Assert.True(Energy(later, beat * 2, beat * 3) < Energy(held, beat, beat * 2) / 10, "a note that starts while sostenuto is down is not held");

        short[] soft = Audio(await RenderAsync(font, Note(60), Cc(0, 67, 127)));
        double ratio = (double)Energy(soft, 0, beat) / Energy(Audio(await RenderAsync(font, Note(60))), 0, beat);
        Assert.InRange(ratio, 0.55, 0.7);   // 4 dB softer (the sample is almost DC, so the darker filter takes little)
    }

    [Fact]
    public async Task The_chorus_output_carries_the_chorus_send()
    {
        IReadOnlyList<RenderOutput> outputs = await RenderAsync(Looped(), Note(60), Cc(0, 93, 127));
        short[] audio = Audio(outputs), chorus = Audio(outputs, "chorus"), send = Audio(outputs, "send");
        Assert.InRange((double)chorus.Sum(x => Math.Abs((long)x)) / audio.Sum(x => Math.Abs((long)x)), 0.19, 0.21);
        Assert.All(send, x => Assert.Equal((short)0, x));   // no reverb send
        Assert.Equal(AudioOutputs, EngineRig.Spec.Templates["gm"].Outputs.Where(p => p.Type == "audio").Select(p => p.Name));
    }

    [Fact]
    public async Task A_zone_with_a_fixed_key_plays_every_note_at_that_key()
    {
        byte[] font = Looped(new Sf2Fixture.Zone((46, 72)));
        Assert.Equal(Audio(await RenderAsync(font, Note(60))), Audio(await RenderAsync(font, Note(64))));
        Assert.NotEqual(Audio(await RenderAsync(Looped(), Note(60))), Audio(await RenderAsync(Looped(), Note(64))));
    }

    [Fact]
    public async Task Pressure_and_the_pitch_wheel_range_reach_the_note()
    {
        byte[] font = Looped();
        short[] plain = Audio(await RenderAsync(font, Note(60)));
        Assert.NotEqual(plain, Audio(await RenderAsync(font, Note(60), new MidiPerformanceEvent(0, SmfEventKind.ChannelPressure, 0, 127, 0))));   // pressure deepens the vibrato
        Assert.Equal(plain, Audio(await RenderAsync(font, Note(60), new MidiPerformanceEvent(0, SmfEventKind.PolyphonicPressure, 0, 61, 127))));   // key pressure of another key
        MidiPerformanceEvent bend = new(0, SmfEventKind.PitchBend, 0, 127, 127);
        short[] two = Audio(await RenderAsync(font, Note(60), bend));
        short[] twelve = Audio(await RenderAsync(font, Note(60), Cc(0, 101, 0), Cc(0, 100, 0), Cc(0, 6, 12), bend));
        Assert.NotEqual(two, twelve);
    }

    [Fact]
    public void Clip_controllers_parse_and_scale()
    {
        Assert.Equal([(74, "auto(1:20, 4:100)"), (67, "127")], ClipControls.ParseCc("74=auto(1:20, 4:100) 67=127"));
        Assert.Null(ClipControls.ParseCc("74:20"));
        Assert.Null(ClipControls.ParseCc("300=1"));
        Assert.Null(ClipControls.ParseCc("74=200"));
        Assert.InRange(ClipControls.Raw(-12, "db"), 62, 66);   // -12 dB on the SoundFont curve
        Assert.Equal(127, ClipControls.Raw(0, "db"));
        Assert.True(ClipControls.Valid("-6dB", "db"));
        Assert.False(ClipControls.Valid("loud", "db"));
        Assert.False(ClipControls.Valid("6", "db"));
        Assert.True(ClipControls.Valid("on", "onoff"));
        Assert.Equal(63, ClipControls.Raw(50, "percent"));
        Assert.Equal(1, ClipControls.Raw(-100, "pan"));
        Assert.Equal(64, ClipControls.Raw(0, "pan"));
        Assert.Equal(127, ClipControls.Raw(300, "raw"));
        Assert.Equal([(0, 63)], ClipControls.Events("50", "percent", 1, Rate * 4, Rate, Rate * 4));
        IReadOnlyList<(int Sample, int Value)> ramp = ClipControls.Events("auto(1:0, 2:100)", "percent", 1, Rate * 4, Rate, Rate * 8);
        Assert.Equal(0, ramp[0].Value);
        Assert.Equal(127, ramp[^1].Value);
        Assert.True(ramp.Count > 50, "a ramp gives an event for every step it climbs");
    }

    [Fact]
    public async Task A_clip_writes_its_controllers_and_the_gm_block_plays_them()
    {
        BlockNode clip = new() { Type = "midi-clip", Name = "lead" };
        clip.Parameters.Add(new Parameter("notes", "1:0:2:60:100", 0));
        clip.Parameters.Add(new Parameter("volume", "auto(1:0, 2:-12)", 1));
        clip.Parameters.Add(new Parameter("cc", "\"74=20 67=127\"", 2));
        RenderRequest request = new(clip, EngineRig.Spec.Templates["midi-clip"], Rate, Rate * 4, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, folder);
        byte[] notes = (await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(request, CancellationToken.None)).Single(o => o.Role == "notes").Data;
        string json = Encoding.UTF8.GetString(notes);
        Assert.Contains("\"controls\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cc\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"cc\":74", json, StringComparison.Ordinal);

        short[] plain = Audio(await RenderAsync(Looped(), Note(60)));
        short[] half = Audio(await RenderAsync(Looped(), Note(60) + "],\"controls\":[{\"cc\":7,\"value\":\"-12\",\"unit\":\"db\"}"));
        Assert.InRange((double)Energy(half, 0, Rate) / Energy(plain, 0, Rate), 0.22, 0.29);   // volume -12 dB: -12 dB in the gm block too (0.25)

        BlockNode bad = new() { Type = "midi-clip", Name = "bad" };
        bad.Parameters.Add(new Parameter("notes", "1:0:2:60:100", 0));
        bad.Parameters.Add(new Parameter("volume", "loud", 1));
        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(async () => await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(request with { Block = bad }, CancellationToken.None));
        Assert.Equal("AMP5506", refused.DiagnosticCode);
    }

    [Fact]
    public async Task Another_instrument_follows_volume_and_pan_of_the_clip_and_names_the_rest()
    {
        BlockNode synth = new() { Type = "synth", Name = "lead" };
        string notes = "{\"notes\":[" + Note(60) + "],\"controls\":[{\"cc\":7,\"value\":\"-6\",\"unit\":\"db\"},{\"cc\":10,\"value\":\"-100\",\"unit\":\"pan\"},{\"cc\":1,\"value\":\"100\",\"unit\":\"percent\"}]}";
        RenderRequest request = new(synth, EngineRig.Spec.Templates["synth"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes(notes) }, 0, folder);
        IReadOnlyList<RenderOutput> dry = await BuiltInRenderers.All.Single(r => r.BlockType == "synth").RenderAsync(request, CancellationToken.None);
        IReadOnlyList<RenderOutput> shaped = ClipControls.Apply([.. dry, new RenderOutput("warnings", [])], request, CancellationToken.None);
        (StereoBuffer sides, _) = ChannelAudio.Decode(shaped.Single(o => o.Role == "audio").Data);
        short[] plain = ChannelAudio.Decode(dry.Single(o => o.Role == "audio").Data).Buffer.Left;
        double left = sides.Left.Sum(x => Math.Abs((long)x)), right = sides.Right.Sum(x => Math.Abs((long)x)), before = plain.Sum(x => Math.Abs((long)x));
        Assert.True(right < left / 20, "pan -100 is hard left");
        Assert.InRange(left / before, 0.45, 0.56);   // -6 dB, and the hard-left side of the pan law at full
        Assert.Contains("controller(s) 1", Encoding.UTF8.GetString(shaped.Single(o => o.Role == "warnings").Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Level_auto_evens_out_the_preset_but_keeps_volume_and_the_soft_pedal()
    {
        string file = $"font-{Guid.NewGuid():N}.sf2";
        File.WriteAllBytes(Path.Combine(folder, file), Looped());
        BlockNode block = new() { Type = "gm", Name = "gm" };
        block.Parameters.Add(new Parameter("file", file, 0));
        block.Parameters.Add(new Parameter("program", "5", 1));   // level auto, the default
        RenderRequest request = new(block, EngineRig.Spec.Templates["gm"], Rate, Rate * 4, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[" + Note(60) + "]}") }, 0, folder);
        IBlockRenderer gm = BuiltInRenderers.All.Single(r => r.BlockType == "gm");
        short[] plain = Audio(await gm.RenderAsync(request, CancellationToken.None));
        short[] quiet = Audio(await gm.RenderAsync(request with { PerformanceEvents = [Cc(0, 7, 64)] }, CancellationToken.None));
        short[] soft = Audio(await gm.RenderAsync(request with { PerformanceEvents = [Cc(0, 67, 127)] }, CancellationToken.None));
        Assert.InRange((double)Energy(quiet, 0, Rate) / Energy(plain, 0, Rate), 0.22, 0.29);   // volume 64 stays -12 dB under level auto
        Assert.InRange((double)Energy(soft, 0, Rate) / Energy(plain, 0, Rate), 0.55, 0.7);    // the soft pedal stays 4 dB
    }
}
