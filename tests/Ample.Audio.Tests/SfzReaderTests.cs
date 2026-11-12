using Ample.Audio.Engine;
using Ample.Audio.Engine.Sfz;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

[Trait("Category", "Sampler")]
public sealed class SfzReaderTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ample-sfz-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static void WriteWav(string path, short value = 1000) => WriteWav(path, [value, value], 44100);

    private static void WriteWav(string path, short[] samples, int nativeRate)
    {
        using FileStream fs = File.Create(path);
        using BinaryWriter w = new(fs);
        int dataSize = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataSize); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(nativeRate); w.Write(nativeRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataSize);
        foreach (short s in samples) w.Write(s);
    }

    [Fact]
    public void A_plain_multisampled_instrument_builds_and_plays_through_RegionResolver_Pick()
    {
        WriteWav(Path.Combine(dir, "c3.wav"));
        WriteWav(Path.Combine(dir, "e3.wav"), 2000);
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=c3.wav lokey=60 hikey=63 pitch_keycenter=60 lovel=0 hivel=127
            <region> sample=e3.wav lokey=64 hikey=67 pitch_keycenter=64 lovel=0 hivel=127
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Instrument.Regions.Count);

        Region? atC = RegionResolver.Pick(result.Instrument, 61, 32768, 0);
        Assert.NotNull(atC);
        Assert.Equal(60, atC!.Root);
        Region? atE = RegionResolver.Pick(result.Instrument, 65, 32768, 0);
        Assert.NotNull(atE);
        Assert.Equal(64, atE!.Root);
    }

    [Fact]
    public void Velocity_layers_lovel_hivel_are_honoured()
    {
        WriteWav(Path.Combine(dir, "soft.wav"));
        WriteWav(Path.Combine(dir, "hard.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=soft.wav key=60 lovel=0 hivel=63
            <region> sample=hard.wav key=60 lovel=64 hivel=127
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Region? soft = RegionResolver.Pick(result.Instrument, 60, 1000, 0);
        Assert.Equal("soft", soft!.Name);
        Region? hard = RegionResolver.Pick(result.Instrument, 60, 32000, 0);
        Assert.Equal("hard", hard!.Name);
    }

    [Fact]
    public void Group_header_opcodes_are_inherited_and_region_pitch_keycenter_defaults_to_key()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <group> volume=-6
            <region> sample=a.wav key=60
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Region region = result.Instrument.Regions[0];
        Assert.Equal(60, region.Root);
        Assert.True(region.GainQ15 < 32768); // volume=-6 dB reduces gain below unity
    }

    [Fact]
    public void An_unknown_opcode_is_kept_and_warned_not_dropped()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 unsupported_opcode=1");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Single(result.Instrument.Regions); // the region is still built
        Assert.Contains(result.Warnings, w => w.Contains("unsupported_opcode", StringComparison.Ordinal));
    }

    // SFZ10a (plan/sfz-import/README.md Phase 2): a static amp_velcurve_NNN table replaces the default linear
    // velocity response for that region's gain, without touching layer selection (lovel/hivel stay linear).
    [Fact]
    public void A_region_without_amp_velcurve_has_no_curve()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Null(result.Instrument.Regions[0].VelCurve);
    }

    [Fact]
    public void An_amp_velcurve_table_is_built_and_interpolated_between_given_points()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 amp_velcurve_000=0.0 amp_velcurve_063=0.5 amp_velcurve_127=1.0");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        int[]? curve = result.Instrument.Regions[0].VelCurve;
        Assert.NotNull(curve);
        Assert.Equal(128, curve!.Length);
        Assert.Equal(0, curve[0]);
        Assert.Equal(32768, curve[127]);
        Assert.InRange(curve[63], 32768 * 49 / 100, 32768 * 51 / 100); // about half way
        Assert.True(curve[32] < curve[63]); // monotonically rising between the given points
        Assert.DoesNotContain(result.Warnings, w => w.Contains("amp_velcurve", StringComparison.Ordinal)); // amp_velcurve_NNN is a known opcode, not an unknown one
    }

    [Fact]
    public void A_velocity_below_the_lowest_or_above_the_highest_given_point_holds_that_points_level()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 amp_velcurve_040=0.25 amp_velcurve_100=0.75");
        int[] curve = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].VelCurve!;
        Assert.Equal(curve[40], curve[0]);
        Assert.Equal(curve[100], curve[127]);
    }

    // SFZ08 (plan/sfz-import/README.md Phase 2): a static cutoff/fil_type only, no resonance/fil_veltrack/fileg_*.
    [Fact]
    public void A_region_without_cutoff_has_no_filter()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(0, region.FilterCutoffHz);
    }

    [Fact]
    public void Cutoff_defaults_to_lpf_2p_when_fil_type_is_not_given()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=1000");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(1000, region.FilterCutoffHz);
        Assert.Equal(2, region.FilterPoles);
    }

    [Fact]
    public void An_explicit_lpf_1p_is_one_pole()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=2000 fil_type=lpf_1p");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(2000, region.FilterCutoffHz);
        Assert.Equal(1, region.FilterPoles);
    }

    [Fact]
    public void An_unsupported_fil_type_warns_and_does_not_apply_a_filter()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=1000 fil_type=hpf_1p");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Equal(0, result.Instrument.Regions[0].FilterCutoffHz);
        Assert.Contains(result.Warnings, w => w.Contains("hpf_1p", StringComparison.Ordinal));
    }

    [Fact]
    public void Resonance_is_read_as_an_approximation_not_warned()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=500 resonance=6");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Equal(500, result.Instrument.Regions[0].FilterCutoffHz);
        Assert.Equal(6, result.Instrument.Regions[0].ResonanceDb);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Fil_veltrack_is_read_without_a_warning()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=500 fil_veltrack=1200");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Equal(500, result.Instrument.Regions[0].FilterCutoffHz);
        Assert.Equal(1200, result.Instrument.Regions[0].FilterVelTrackCents);
        Assert.Empty(result.Warnings);
    }

    // SFZ04 (plan/sfz-import/README.md Phase 2, now that S08 plays a "sustain" LoopMode): loop_mode/loop_start/loop_end.
    [Fact]
    public void A_region_without_loop_mode_has_no_loop()
    {
        WriteWav(Path.Combine(dir, "a.wav"), [.. Enumerable.Range(0, 20).Select(i => (short)i)], 44100);
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal("none", region.LoopMode);
    }

    [Fact]
    public void Loop_continuous_and_loop_sustain_both_become_the_engines_sustain_loop_mode()
    {
        WriteWav(Path.Combine(dir, "a.wav"), [.. Enumerable.Range(0, 20).Select(i => (short)i)], 44100);
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 loop_mode=loop_continuous loop_start=2 loop_end=18");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal("sustain", region.LoopMode);
        Assert.Equal(2, region.LoopStart);
        Assert.Equal(18, region.LoopEnd);

        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 loop_mode=loop_sustain loop_start=2 loop_end=18");
        Assert.Equal("sustain", SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].LoopMode);

        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 loop_mode=one_shot loop_start=2 loop_end=18");
        Assert.Equal("none", SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].LoopMode);
    }

    [Fact]
    public void Loop_points_are_rescaled_from_the_files_native_rate_to_the_build_rate()
    {
        WriteWav(Path.Combine(dir, "a.wav"), [.. Enumerable.Range(0, 40).Select(i => (short)i)], 48000);
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 loop_mode=loop_continuous loop_start=4 loop_end=24");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(4, region.LoopStart);
        Assert.Equal(22, region.LoopEnd);

        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 loop_mode=loop_continuous");
        Region wholeSample = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(wholeSample.Sample.Samples.Length, wholeSample.LoopEnd);
    }

    // SFZ04's end-to-end "it actually loops when played" is covered in SamplerTests.cs
    // (An_sfz_sourced_loop_sustains_past_the_end_of_the_recording), through the real guitar render path.

    // SFZ07 (plan/sfz-import/README.md Phase 2, now that S08 wires an integer ADSR into the renderer): ampeg_*.
    [Fact]
    public void A_region_without_ampeg_opcodes_has_no_envelope()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Null(region.Envelope);
    }

    [Fact]
    public void Ampeg_opcodes_build_an_integer_envelope_in_ticks()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 ampeg_attack=0.05 ampeg_decay=0.1 ampeg_sustain=50 ampeg_release=0.2");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.NotNull(region.Envelope);
        IntegerEnvelope env = region.Envelope!.Value;
        Assert.Equal((int)Math.Round(0.05 * 44100), env.Attack);
        Assert.Equal((int)Math.Round(0.1 * 44100), env.Decay);
        Assert.Equal(IntegerEnvelope.One / 2, env.SustainQ15);
        Assert.Equal((int)Math.Round(0.2 * 44100), env.Release);
    }

    [Fact]
    public void An_sfz_ampeg_release_shapes_the_played_tail()
    {
        WriteWav(Path.Combine(dir, "a.wav"), [.. Enumerable.Repeat((short)10000, 2000)], 44100);
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 ampeg_release=0.01");
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument;
        short[] output = new short[1000];
        SampledInstruments.PlayNote(output, instrument, [], 60, 32768, 0, 0, 200, 32768, 44100);
        Assert.NotEqual(0, output[200 + 220]);
        Assert.Equal(0, output[200 + 441]);
    }

    [Fact]
    public void Ampeg_hold_becomes_the_envelopes_hold_plateau()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 ampeg_attack=0.05 ampeg_hold=0.02");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Empty(result.Warnings);
        IntegerEnvelope env = result.Instrument.Regions[0].Envelope!.Value;
        Assert.Equal((int)Math.Round(0.02 * 44100), env.Hold);
    }

    // SFZ05 (plan/sfz-import/README.md): a drums "kit" value maps each region's key to a voice name via the General
    // MIDI drum map instead of building a pitched region, so an SFZ drum rack can be read straight into a kit.
    [Fact]
    public void A_drum_kit_region_becomes_an_unpitched_voice_named_region_via_the_GM_drum_map()
    {
        WriteWav(Path.Combine(dir, "kick.wav"));
        WriteWav(Path.Combine(dir, "snare.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=kick.wav key=36 <region> sample=snare.wav key=38");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Instrument.Regions.Count);
        Assert.True(result.Instrument.HasVoice("kick"));
        Assert.True(result.Instrument.HasVoice("snare"));
        Assert.False(result.Instrument.HasPitched);
        Region? kick = RegionResolver.PickVoice(result.Instrument, "kick", 32768, 0);
        Assert.NotNull(kick);
        Assert.Equal(-1, kick!.Root);
    }

    [Fact]
    public void A_key_outside_the_GM_drum_map_still_builds_a_region_with_a_generic_name_and_warns()
    {
        WriteWav(Path.Combine(dir, "weird.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=weird.wav key=12");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.True(result.Instrument.HasVoice("key12"));
        Assert.Contains(result.Warnings, w => w.Contains("key12", StringComparison.Ordinal));
    }

    [Fact]
    public void A_drum_kit_region_without_any_key_is_skipped_and_warned()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav");
        AmpleException ex = Assert.Throws<AmpleException>(() => SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true));
        Assert.Equal("AMP5395", ex.DiagnosticCode);
    }

    [Fact]
    public void Velocity_layers_still_work_for_a_drum_kit_region()
    {
        WriteWav(Path.Combine(dir, "soft.wav"));
        WriteWav(Path.Combine(dir, "hard.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=soft.wav key=36 lovel=0 hivel=63 <region> sample=hard.wav key=36 lovel=64 hivel=127");
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true).Instrument;
        Assert.Equal("soft", RegionResolver.PickVoice(instrument, "kick", 1000, 0)!.Sample.Name);
        Assert.Equal("hard", RegionResolver.PickVoice(instrument, "kick", 32000, 0)!.Sample.Name);
    }

    // SFZ03 (plan/sfz-import/README.md): group/off_by become the same voice:closer1,closer2 choke-rule text a
    // drums block's own choke parameter uses, feeding the one existing choke-and-fade mixing pass.
    [Fact]
    public void Group_and_off_by_derive_a_choke_rule_in_the_drums_choke_text_form()
    {
        WriteWav(Path.Combine(dir, "hat.wav"));
        WriteWav(Path.Combine(dir, "hatopen.wav"));
        WriteWav(Path.Combine(dir, "pedal.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=hat.wav key=42 group=1
            <region> sample=hatopen.wav key=46 group=1
            <region> sample=pedal.wav key=99 off_by=1
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.Contains("hat:key99", result.ChokeRules);
        Assert.Contains("hat-open:key99", result.ChokeRules);
    }

    [Fact]
    public void A_voice_without_a_group_or_off_by_gets_no_choke_rule()
    {
        WriteWav(Path.Combine(dir, "kick.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=kick.wav key=36");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.Empty(result.ChokeRules);
    }

    [Fact]
    public void An_include_pulls_in_its_region_and_the_sample_and_include_are_both_hashed_into_the_plan()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "regions.txt"), "<region> sample=a.wav key=60");
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "#include \"sub/regions.txt\"");
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100);
        Assert.Single(result.Instrument.Regions);
        Assert.Contains(result.UsedFiles, f => f.RelativePath == "kit.sfz");
        Assert.Contains(result.UsedFiles, f => f.RelativePath.Replace('\\', '/') == "sub/regions.txt");
        Assert.Contains(result.UsedFiles, f => f.RelativePath.Replace('\\', '/') == "a.wav");
    }

    [Fact]
    public void A_sample_path_that_escapes_the_sfz_folder_is_refused()
    {
        string outside = Directory.CreateTempSubdirectory("ample-sfz-outside-").FullName;
        try
        {
            WriteWav(Path.Combine(outside, "a.wav"));
            File.WriteAllText(Path.Combine(dir, "kit.sfz"), $"<region> sample=../{Path.GetFileName(outside)}/a.wav key=60");
            AmpleException ex = Assert.Throws<AmpleException>(() => SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100));
            Assert.Equal("AMP5395", ex.DiagnosticCode);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void A_missing_sfz_file_fails_with_AMP5395()
    {
        AmpleException ex = Assert.Throws<AmpleException>(() => SfzReader.Read(Path.Combine(dir, "nope.sfz"), 44100));
        Assert.Equal("AMP5395", ex.DiagnosticCode);
    }

    [Fact]
    public void A_file_with_no_pitched_region_fails_with_AMP5395()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav");
        AmpleException ex = Assert.Throws<AmpleException>(() => SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100));
        Assert.Equal("AMP5395", ex.DiagnosticCode);
    }

    // SFZ06 (plan/sfz-import/README.md): the real-world jail-root fix below was found via a one-time conformance
    // check against the actual upstream Karoryfer "Frankensnare" pack (Programs/mappings/snarekey_more.sfz +
    // 14a_basic_top.sfz, fetched verbatim from github.com/sfzinstruments/karoryfer.frankensnare and trimmed to two
    // of its eleven velocity groups to avoid vendoring audio) - see the plan's done note for the full writeup; the
    // real pack's own structure (Programs/ reaching samples via ../Samples/...) is exactly what SFZ03/04's root-jail
    // had to learn to allow (JailRoot/jailRoot below), and reading it produced byte-identical audio (SHA-256) to the
    // same pack's hand-mapped packs.json regex rule for every velocity/round-robin compared.
    [Fact]
    public void A_real_pack_layout_with_samples_in_a_sibling_folder_of_the_sfz_file_needs_a_wider_jail_root()
    {
        Directory.CreateDirectory(Path.Combine(dir, "Programs"));
        Directory.CreateDirectory(Path.Combine(dir, "Samples"));
        WriteWav(Path.Combine(dir, "Samples", "snare.wav"));
        File.WriteAllText(Path.Combine(dir, "Programs", "kit.sfz"), "<region> sample=../Samples/snare.wav key=38");

        AmpleException narrow = Assert.Throws<AmpleException>(() => SfzReader.Read(Path.Combine(dir, "Programs", "kit.sfz"), 44100, asDrumKit: true));
        Assert.Equal("AMP5395", narrow.DiagnosticCode);

        SfzReader.Result wide = SfzReader.Read(Path.Combine(dir, "Programs", "kit.sfz"), 44100, asDrumKit: true, jailRoot: dir);
        Assert.Single(wide.Instrument.Regions);
        Assert.True(wide.Instrument.HasVoice("snare"));
    }

    // SFZ09 (block 1): lfo01_freq/lfo01_pitch/lfo01_delay.
    [Fact]
    public void Lfo_opcodes_are_read_into_the_region()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 lfo01_freq=5 lfo01_pitch=50 lfo01_delay=0.1");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(5000, region.LfoFreqMilliHz);
        Assert.Equal(50, region.LfoDepthCents);
        Assert.Equal((int)Math.Round(0.1 * 44100), region.LfoDelaySamples);
    }

    [Fact]
    public void A_region_without_lfo_opcodes_has_no_lfo_and_Instrument_NeedsPerFrameModulation_is_false()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument;
        Assert.Equal(0, instrument.Regions[0].LfoFreqMilliHz);
        Assert.False(instrument.NeedsPerFrameModulation);
    }

    [Fact]
    public void An_lfo_with_zero_depth_or_zero_freq_is_not_built()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 lfo01_freq=5 lfo01_pitch=0");
        Assert.Equal(0, SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].LfoFreqMilliHz);
    }

    // SFZ08 (block 2): fileg_attack/decay/sustain/release/hold and fileg_depth.
    [Fact]
    public void Fileg_opcodes_build_a_filter_envelope_and_force_NeedsPerFrameModulation()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=500 fileg_attack=0.05 fileg_depth=1200");
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument;
        Region region = instrument.Regions[0];
        Assert.NotNull(region.FilterEnvelope);
        Assert.Equal(1200, region.FilterEnvelopeDepthCents);
        Assert.Equal((int)Math.Round(0.05 * 44100), region.FilterEnvelope!.Value.Attack);
        Assert.True(instrument.NeedsPerFrameModulation);
    }

    [Fact]
    public void Fileg_opcodes_without_a_depth_are_not_built()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=500 fileg_attack=0.05");
        Assert.Null(SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].FilterEnvelope);
    }

    // S08/SFZ12 (block 3): trigger=release.
    [Fact]
    public void Trigger_release_marks_the_region_as_a_release_companion_excluded_from_normal_pick()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 trigger=release");
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument;
        Assert.True(instrument.Regions[0].IsRelease);
        Assert.Null(RegionResolver.Pick(instrument, 60, 32768, 0));
        Assert.NotNull(RegionResolver.PickRelease(instrument, 60, 32768, 0));
    }

    [Fact]
    public void A_folder_set_with_a_rel_suffix_file_builds_a_release_companion()
    {
        SampleFile main = new("kick", "sha256:a", [1, 2, 3]);
        SampleFile rel = new("kick_rel", "sha256:b", [4, 5, 6]);
        Instrument instrument = Instrument.FromFiles([main, rel]);
        Assert.Equal(2, instrument.Regions.Count);
        Region? normal = RegionResolver.PickVoice(instrument, "kick", 32768, 0);
        Region? release = RegionResolver.PickReleaseVoice(instrument, "kick", 32768, 0);
        Assert.NotNull(normal);
        Assert.NotNull(release);
        Assert.Equal("sha256:a", normal!.Sample.Hash);
        Assert.Equal("sha256:b", release!.Sample.Hash);
        Assert.False(normal.IsRelease);
        Assert.True(release.IsRelease);
    }

    // SFZ12 (block 4): xfin/xfout velocity crossfade taper.
    [Fact]
    public void Xfade_opcodes_are_read_into_the_region()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 xfin_lovel=0 xfin_hivel=20 xfout_lovel=100 xfout_hivel=127");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(0, region.XfinLoVel);
        Assert.Equal(20 * 32768 / 127, region.XfinHiVel);
        Assert.Equal(100 * 32768 / 127, region.XfoutLoVel);
        Assert.Equal(127 * 32768 / 127, region.XfoutHiVel);
    }

    [Fact]
    public void A_region_without_xfade_opcodes_has_no_taper()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal(-1, region.XfinLoVel);
        Assert.Equal(-1, region.XfoutLoVel);
    }

    // SFZ12 (block 5): trigger=first/legato.
    [Fact]
    public void Trigger_first_and_legato_are_read_and_filtered_by_RegionResolver_Pick()
    {
        WriteWav(Path.Combine(dir, "first.wav"));
        WriteWav(Path.Combine(dir, "legato.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=first.wav key=60 trigger=first
            <region> sample=legato.wav key=60 trigger=legato
            """);
        Instrument instrument = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument;
        Assert.Equal("first", instrument.Regions[0].Trigger);
        Assert.Equal("legato", instrument.Regions[1].Trigger);
        Assert.Equal("first.wav".Replace(".wav", ""), RegionResolver.Pick(instrument, 60, 32768, 0, legato: false)!.Sample.Name);
        Assert.Equal("legato", RegionResolver.Pick(instrument, 60, 32768, 0, legato: true)!.Sample.Name);
    }

    [Fact]
    public void A_region_without_an_explicit_trigger_defaults_to_attack_and_is_always_eligible()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60");
        Region region = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0];
        Assert.Equal("attack", region.Trigger);
    }

    // SFZ08 (block 6): resonance.
    [Fact]
    public void Resonance_above_24dB_is_clamped()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=60 cutoff=500 resonance=40");
        Assert.Equal(24, SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100).Instrument.Regions[0].ResonanceDb);
    }

    // SFZ03 (block 7): off_time overriding the choke fade.
    [Fact]
    public void Off_time_becomes_the_derived_choke_fade_in_milliseconds()
    {
        WriteWav(Path.Combine(dir, "hat.wav"));
        WriteWav(Path.Combine(dir, "pedal.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=hat.wav key=46 group=1
            <region> sample=pedal.wav key=99 off_by=1 off_time=0.05
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.Equal(50, result.ChokeFadeMs);
    }

    [Fact]
    public void Off_mode_normal_is_kept_and_warned()
    {
        WriteWav(Path.Combine(dir, "hat.wav"));
        WriteWav(Path.Combine(dir, "pedal.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), """
            <region> sample=hat.wav key=46 group=1
            <region> sample=pedal.wav key=99 off_by=1 off_mode=normal
            """);
        SfzReader.Result result = SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true);
        Assert.Contains(result.Warnings, w => w.Contains("off_mode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_kit_without_off_time_has_no_derived_choke_fade()
    {
        WriteWav(Path.Combine(dir, "kick.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=kick.wav key=36");
        Assert.Null(SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true).ChokeFadeMs);
    }

    // SFZ12 (block 8): polyphony/note_polyphony.
    [Fact]
    public void Polyphony_and_note_polyphony_are_read_first_one_wins()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=36 polyphony=2 note_polyphony=5");
        Assert.Equal(2, SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true).Instrument.Regions[0].Polyphony);
    }

    [Fact]
    public void A_region_without_polyphony_opcodes_is_unlimited()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        File.WriteAllText(Path.Combine(dir, "kit.sfz"), "<region> sample=a.wav key=36");
        Assert.Equal(0, SfzReader.Read(Path.Combine(dir, "kit.sfz"), 44100, asDrumKit: true).Instrument.Regions[0].Polyphony);
    }

    // S13 (plan/sampler/S13-golden-audio-and-conformance.md): a fuzz run over the SFZ reader, mirroring
    // Sf2ReaderTests' RandomCorruptionNeverCrashes/EveryTruncationEndsInADiagnosticOrAValidBank - every truncation
    // and every random byte corruption of a real, valid .sfz text must end in a usable Instrument or a clean
    // AmpleException, never an unhandled exception of another type (SFZ is untrusted, pack-supplied text).
    [Fact]
    public void Every_truncation_or_random_corruption_of_a_valid_sfz_ends_in_a_result_or_a_clean_AmpleException()
    {
        WriteWav(Path.Combine(dir, "a.wav"));
        WriteWav(Path.Combine(dir, "b.wav"));
        byte[] good = System.Text.Encoding.UTF8.GetBytes("""
            <group> loop_mode=loop_continuous loop_start=100 loop_end=900 ampeg_attack=0.1 ampeg_release=0.3 cutoff=2000 fil_type=lpf_2p
            <region> sample=a.wav lokey=0 hikey=60 pitch_keycenter=40 lovel=0 hivel=63 group=1 off_by=2
            <region> sample=b.wav lokey=61 hikey=127 pitch_keycenter=72 lovel=64 hivel=127 off_by=1 xfin_lovel=1 xfin_hivel=100
            """);
        string path = Path.Combine(dir, "fuzz.sfz");
        void TryRead(byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            try
            {
                SfzReader.Read(path, 44100, asDrumKit: false);
            }
            catch (AmpleException)
            {
            }
        }

        for (int n = 0; n < good.Length; n += 5) TryRead(good[..n]);
        Instruments.Lcg rnd = new(2026);
        for (int i = 0; i < 1000; i++)
        {
            byte[] b = (byte[])good.Clone();
            for (int k = rnd.Next(1, 6); k > 0; k--) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            TryRead(b);
        }

        for (int i = 0; i < 200; i++) TryRead([.. Enumerable.Range(0, rnd.Next(0, 300)).Select(_ => (byte)rnd.Next(256))]);
    }
}
