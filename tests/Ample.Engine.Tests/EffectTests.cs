using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "Effects")]
public sealed class EffectTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-fx-").FullName;

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

    private async Task<(short[] Dry, short[] Wet)> RunAsync(string effect, string body)
    {
        string root = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "fx");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        BuildEngine engine = new(bank, EngineRig.Spec, p.Manifest.Id, root);
        string song = $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\nmidi-clip c {{\n  chords Am\n  bars 1\n}}\npiano pn pinned@2.0 at 1 for 1 {{\n  set felt-piano\n  from c.notes -> notes\n}}\n{effect} fx at 1 for 1 {{\n  from pn.audio -> audio\n{body}}}\n";
        BuildReport report = await engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        async Task<short[]> Take(string block)
        {
            BlockResult r = report.Blocks.Single(b => b.Block == block);
            Assert.True(r.Status is "rendered" or "cached", r.Message);
            await using Stream data = bank.OpenData(bank.GetTake(r.TakeId!).Data[0].DataId);
            using MemoryStream ms = new();
            await data.CopyToAsync(ms);
            return Wav.Decode(ms.ToArray()).Samples;
        }

        return (await Take("pn"), await Take("fx"));
    }

    private static double Energy(short[] s) => s.Sum(v => (double)v * v);

    [Fact]
    public async Task Bypass_passes_the_sound_through_untouched()
    {
        (short[] dry, short[] wet) = await RunAsync("distortion", "  enabled off\n");
        Assert.Equal(dry, wet);
    }

    [Fact]
    public async Task Duck_dips_without_jumping_the_gain_between_two_samples()
    {
        (short[] dry, short[] wet) = await RunAsync("duck", "  depth 12\n  release 150\n  pattern x...x...x...x...\n");
        double maxStep = 0;
        double lowest = 1;
        for (int i = 1; i < dry.Length; i++)
        {
            if (Math.Abs(dry[i]) < 3000 || Math.Abs(dry[i - 1]) < 3000)
            {
                continue;
            }

            double step = Math.Abs((double)wet[i] / dry[i] - (double)wet[i - 1] / dry[i - 1]);
            maxStep = Math.Max(maxStep, step);
            lowest = Math.Min(lowest, (double)wet[i] / dry[i]);
        }

        Assert.True(lowest < 0.5, "the duck must really dip");
        Assert.True(maxStep < 0.05, $"the gain jumped by {maxStep:0.000} between two samples (a click)");
    }

    private static async Task<byte[]> RenderDirectAsync(string type, Dictionary<string, string> parameters, byte[]? input, string folder)
    {
        const int sampleRate = 16000;
        BlockNode block = new() { Type = type, Name = "fx" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = input is null ? [] : new() { ["in"] = input };
        RenderRequest request = new(block, EngineRig.Spec.Templates[type], sampleRate, sampleRate, 1, 1, new HashSet<int> { 1 }, inputs, 1, folder);
        IReadOnlyList<RenderOutput> outputs = await AudioEffects.All.Single(renderer => renderer.BlockType == type).RenderAsync(request, CancellationToken.None);
        return outputs.Single(o => o.Role == "out").Data;
    }

    private static short[] Sine(int hz, int amplitude = 16000) => [.. Enumerable.Range(0, 16000).Select(i => (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hz * i / 16000.0)))];

    private static double Rms(short[] s) => Math.Sqrt(s.Skip(4000).Average(v => (double)v * v));

    [Theory]
    [InlineData("lowpass", 2000)]
    [InlineData("lowpass", 500)]
    [InlineData("highpass", 500)]
    public async Task The_filter_is_about_3_dB_down_at_its_cutoff(string type, int hz)
    {
        short[] input = Sine(hz);
        short[] output = Wav.Decode(await RenderDirectAsync("filter", new() { ["type"] = type, ["cutoff"] = hz.ToString(System.Globalization.CultureInfo.InvariantCulture) }, Wav.Encode(input, 16000), folder)).Samples;
        double db = 20 * Math.Log10(Rms(output) / Rms(input));
        Assert.InRange(db, -4.5, -2.0);
    }

    [Fact]
    public async Task Compressor_make_up_gain_does_not_wrap_loud_samples_round()
    {
        short[] loud = Enumerable.Repeat((short)30000, 16000).ToArray();
        short[] output = Wav.Decode(await RenderDirectAsync("compressor", new() { ["makeup"] = "12", ["ratio"] = "2" }, Wav.Encode(loud, 16000), folder)).Samples;
        Assert.All(output.Skip(10), v => Assert.True(v > 0, "a positive sample came out negative (it wrapped round)"));
    }

    [Fact]
    public async Task A_chorus_gives_a_stereo_sound_width()
    {
        short[] tone = Sine(440);
        StereoBuffer result = StereoWav.Decode(await RenderDirectAsync("chorus", new() { ["mix"] = "50" }, StereoWav.Encode(new StereoBuffer(tone, tone), 16000), folder)).Buffer;
        Assert.NotEqual(result.Left, result.Right);
    }

    [Fact]
    public async Task Stereo_duck_uses_one_curve_for_both_channels_and_matches_the_mono_duck()
    {
        short[] mono = Enumerable.Repeat((short)10000, 16000).ToArray();
        Dictionary<string, string> duck = new() { ["depth"] = "12", ["release"] = "150", ["pattern"] = "x...x...x...x..." };
        byte[] monoOut = await RenderDirectAsync("duck", duck, Wav.Encode(mono, 16000), folder);
        byte[] stereoOut = await RenderDirectAsync("duck", duck, StereoWav.Encode(new StereoBuffer(mono, mono), 16000), folder);
        short[] expected = Wav.Decode(monoOut).Samples;
        StereoBuffer result = StereoWav.Decode(stereoOut).Buffer;
        Assert.Equal(expected, result.Left);
        Assert.Equal(expected, result.Right);
        Assert.Contains(expected, v => v < 5000);
    }

    [Fact]
    public async Task A_bypassed_stereo_duck_returns_the_input_bytes_unchanged()
    {
        byte[] input = StereoWav.Encode(new StereoBuffer(Enumerable.Repeat((short)7000, 16000).ToArray(), Enumerable.Repeat((short)-3000, 16000).ToArray()), 16000);
        byte[] output = await RenderDirectAsync("duck", new() { ["enabled"] = "off" }, input, folder);
        Assert.Equal(input, output);
    }

    [Fact]
    public async Task Scratch_moves_the_marked_steps_cuts_the_dashes_and_leaves_the_rest()
    {
        // one bar is 16000 samples here, so a step of sixteen is 1000 samples
        short[] tone = [.. Enumerable.Range(0, 16000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 200 * i / 16000)))];
        short[] output = Wav.Decode(await RenderDirectAsync("scratch", new() { ["pattern"] = "x--.............", ["depth"] = "100" }, Wav.Encode(tone, 16000), folder)).Samples;
        Assert.Contains(Enumerable.Range(100, 800), i => Math.Abs(output[i] - tone[i]) > 2000);
        Assert.All(Enumerable.Range(1100, 1800), i => Assert.Equal(0, output[i]));
        Assert.Equal(tone[3000..], output[3000..]);
    }

    [Fact]
    public async Task Volume_without_a_connected_input_renders_silence_and_bypass_keeps_the_sound()
    {
        Assert.All(StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = "-6" }, null, folder)).Buffer.Left, v => Assert.Equal(0, v));
        short[] tone = Enumerable.Repeat((short)9000, 16000).ToArray();
        StereoBuffer kept = StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = "-6", ["enabled"] = "off" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.Equal(tone, kept.Left);
        StereoBuffer quieter = StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = "-6" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.InRange((int)quieter.Left[0], 4400, 4600);
    }

    private static async Task<byte[]> RenderKeyedAsync(Dictionary<string, string> parameters, byte[] input, byte[]? key, string folder)
    {
        const int sampleRate = 16000;
        BlockNode block = new() { Type = "compressor", Name = "fx" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = new() { ["in"] = input };
        if (key is not null)
        {
            inputs["key"] = key;
        }

        RenderRequest request = new(block, EngineRig.Spec.Templates["compressor"], sampleRate, sampleRate, 1, 1, new HashSet<int> { 1 }, inputs, 1, folder);
        IReadOnlyList<RenderOutput> outputs = await AudioEffects.All.Single(renderer => renderer.BlockType == "compressor").RenderAsync(request, CancellationToken.None);
        return outputs.Single(o => o.Role == "out").Data;
    }

    [Fact]
    public async Task A_key_input_drives_the_compressor_like_a_sidechain()
    {
        short[] bass = Enumerable.Repeat((short)8000, 16000).ToArray();
        short[] kick = [.. Enumerable.Range(0, 16000).Select(i => (short)(i is >= 4000 and < 8000 ? 30000 : 0))];
        Dictionary<string, string> settings = new() { ["threshold"] = "-30", ["ratio"] = "20", ["attack"] = "1", ["release"] = "20" };
        short[] ducked = Wav.Decode(await RenderKeyedAsync(settings, Wav.Encode(bass, 16000), Wav.Encode(kick, 16000), folder)).Samples;
        Assert.True(ducked[7000] < 4000, "the bass must duck while the key is loud");
        Assert.True(ducked[2000] > 7900 && ducked[15000] > 7000, "the bass stays up while the key is silent");
        short[] free = Wav.Decode(await RenderKeyedAsync(settings, Wav.Encode(bass, 16000), null, folder)).Samples;
        Assert.True(free[15000] < 3000, "without a key the loud bass itself is compressed as before");
        StereoBuffer stereo = StereoWav.Decode(await RenderKeyedAsync(settings, StereoWav.Encode(new StereoBuffer(bass, bass), 16000), Wav.Encode(kick, 16000), folder)).Buffer;
        Assert.True(stereo.Left[7000] < 4000 && stereo.Left[7000] == stereo.Right[7000]);
    }

    [Fact]
    public async Task The_volume_gain_goes_up_to_48_dB_and_stops_there()
    {
        // a very quiet recording needs more than +12 dB: the volume block takes up to +48 dB (the other blocks keep their own ranges)
        short[] quiet = Enumerable.Repeat((short)20, 16000).ToArray();
        async Task<short> At(string gain) => StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = gain }, Wav.Encode(quiet, 16000), folder)).Buffer.Left[100];
        Assert.InRange(await At("12"), 78, 82);       // x3.98
        Assert.InRange(await At("24"), 312, 322);     // x15.85
        Assert.InRange(await At("36"), 1250, 1290);   // x63.1
        short top = await At("48");
        Assert.InRange(top, 4980, 5060);              // x251.2
        Assert.Equal(top, await At("60"));            // more than +48 is the same as +48
        // an automated gain reaches the same range
        short[] tone = Enumerable.Repeat((short)20, 16000).ToArray();
        StereoBuffer ramp = StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = "auto(1:0, 2:36)" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.InRange(ramp.Left[15999], 1200, 1300);
        // and the left/right channel gains reach it too
        StereoBuffer sides = StereoWav.Decode(await RenderDirectAsync("volume", new() { ["left_gain"] = "24", ["right_gain"] = "0" }, Wav.Encode(quiet, 16000), folder)).Buffer;
        Assert.InRange(sides.Left[100], 312, 322);
        Assert.Equal(20, sides.Right[100]);
    }

    [Fact]
    public async Task An_automated_volume_gain_fades_in_a_straight_line_and_rejects_bad_text()
    {
        // one bar is 16000 samples at tempo 240 in the direct render below (SamplesPerBar = the sample rate)
        short[] tone = Enumerable.Repeat((short)10000, 16000).ToArray();
        StereoBuffer faded = StereoWav.Decode(await RenderDirectAsync("volume", new() { ["gain"] = "auto(1:-60, 2:0)" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.True(faded.Left[0] < 50, "the fade starts nearly silent");
        Assert.True(faded.Left[8000] is > 4000 and < 6000, "halfway the gain is about half");
        Assert.True(faded.Left[15999] > 9900);
        for (int i = 1; i < faded.Left.Length; i++)
        {
            Assert.True(faded.Left[i] >= faded.Left[i - 1], "the fade rises without a step back");
        }

        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Automation.Parse("auto(1, 5:0)"));
        Assert.Equal([(1, -60), (3, 0)], Automation.Parse("auto(3:0 dB, 1:-60)"));
    }

    [Fact]
    public async Task An_automated_gain_passes_the_language_checks_and_builds_in_a_song()
    {
        (short[] dry, short[] wet) = await RunAsync("volume", "  gain auto(1:-30, 2:0)\n");
        Assert.NotEqual(dry, wet);
        Assert.True(Math.Abs((int)wet[10]) < Math.Abs((int)dry[10]) || dry[10] == 0);
    }

    [Fact]
    public async Task The_key_filter_ignores_a_low_key_that_would_otherwise_trigger_the_sidechain()
    {
        short[] bass = Enumerable.Repeat((short)8000, 16000).ToArray();
        short[] lowKey = [.. Enumerable.Range(0, 16000).Select(i => (short)Math.Round(30000 * Math.Sin(2 * Math.PI * i / 400)))];
        Dictionary<string, string> settings = new() { ["threshold"] = "-30", ["ratio"] = "20", ["attack"] = "1", ["release"] = "20" };
        short[] plain = Wav.Decode(await RenderKeyedAsync(settings, Wav.Encode(bass, 16000), Wav.Encode(lowKey, 16000), folder)).Samples;
        Assert.True(plain[8000] < 4000, "a low key ducks without a filter");
        settings["keyFilter"] = "2000";
        short[] filtered = Wav.Decode(await RenderKeyedAsync(settings, Wav.Encode(bass, 16000), Wav.Encode(lowKey, 16000), folder)).Samples;
        Assert.True(filtered[8000] > 7000, "the filter takes the low key away from the detector");
    }

    [Fact]
    public async Task An_automated_filter_cutoff_opens_up_over_the_bar()
    {
        short[] nyquist = [.. Enumerable.Range(0, 16000).Select(i => (short)(i % 2 == 0 ? 10000 : -10000))];
        short[] swept = Wav.Decode(await RenderDirectAsync("filter", new() { ["cutoff"] = "auto(1:100, 2:7000)" }, Wav.Encode(nyquist, 16000), folder)).Samples;
        int early = swept[2000..2200].Max(v => Math.Abs((int)v)), late = swept[15700..15900].Max(v => Math.Abs((int)v));
        Assert.True(late > early * 5 && late > 1000, $"the high tone passes more and more (early {early}, late {late})");
        StereoBuffer stereo = StereoWav.Decode(await RenderDirectAsync("filter", new() { ["cutoff"] = "auto(1:100, 2:7000)" }, StereoWav.Encode(new StereoBuffer(nyquist, nyquist), 16000), folder)).Buffer;
        Assert.Equal(swept, stereo.Left);
    }

    [Fact]
    public void An_lfo_is_a_triangle_between_two_values_with_an_even_period_in_bars()
    {
        IReadOnlyList<(int Bar, int Value)> points = Automation.Parse("lfo(4, -12, 0)");
        Assert.Equal([(1, -12), (3, 0), (5, -12), (7, 0)], points.Take(4));
        Assert.True(Automation.Is("lfo(4, 0, 1)"));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Automation.Parse("lfo(3, 0, 1)"));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Automation.Parse("lfo(4, 0)"));
    }

    [Fact]
    public async Task An_automated_mix_lets_the_echoes_of_a_delay_grow_over_the_bar()
    {
        // the delay keeps the dry sound and adds its echoes at the mix level, so with the mix rising from 0 to 100 percent the echo of a late click is louder than the echo of an early one
        short[] click = new short[16000];
        click[100] = 20000;
        click[12000] = 20000;
        short[] blended = Wav.Decode(await RenderDirectAsync("delay", new() { ["time"] = "100", ["mix"] = "auto(1:0, 2:100)" }, Wav.Encode(click, 16000), folder)).Samples;
        short[] steady = Wav.Decode(await RenderDirectAsync("delay", new() { ["time"] = "100", ["mix"] = "100" }, Wav.Encode(click, 16000), folder)).Samples;
        Assert.Equal(20000, blended[100]);
        int early = blended[1700], late = blended[13600];
        Assert.True(early > 0 && late > early * 3, $"the echo grows with the mix (early {early}, late {late})");
        Assert.True(late < steady[13600] && late > steady[13600] / 2, $"a mix of about 85 percent gives about 85 percent of the echo (late {late}, full {steady[13600]})");
    }

    [Fact]
    public void An_envelope_rises_holds_and_falls_between_two_values()
    {
        Assert.Equal([(2, -40), (4, 0), (8, 0), (10, -40)], Automation.Parse("env(2, 2, 4, 2, -40, 0)"));
        Assert.Equal([(1, 0), (3, 100), (5, 0)], Automation.Parse("env(1, 2, 0, 2, 0, 100)"));
        Assert.True(Automation.Is("env(1, 1, 1, 1, 0, 1)"));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Automation.Parse("env(1, 0, 1, 1, 0, 1)"));
    }

    [Fact]
    public async Task A_stereo_compressor_is_linked_so_the_balance_between_the_channels_stays()
    {
        short[] loud = Enumerable.Repeat((short)20000, 16000).ToArray();
        short[] quiet = Enumerable.Repeat((short)5000, 16000).ToArray();
        byte[] output = await RenderDirectAsync("compressor", new() { ["threshold"] = "-30", ["ratio"] = "8", ["attack"] = "1" }, StereoWav.Encode(new StereoBuffer(loud, quiet), 16000), folder);
        StereoBuffer result = StereoWav.Decode(output).Buffer;
        Assert.True(result.Left[^1] < 20000, "the compressor must act");
        Assert.InRange(Math.Abs(result.Left[^1] / 4.0 - result.Right[^1]), 0, 2);
    }

    [Fact]
    public async Task A_stereo_reverb_gives_a_mono_source_a_real_stereo_tail()
    {
        short[] click = new short[16000];
        click[100] = 20000;
        StereoBuffer result = StereoWav.Decode(await RenderDirectAsync("reverb", new() { ["size"] = "80" }, StereoWav.Encode(new StereoBuffer(click, click), 16000), folder)).Buffer;
        Assert.NotEqual(result.Left, result.Right);
    }

    [Fact]
    public async Task The_tail_of_a_reverb_is_dark_not_hissy()
    {
        short[] click = new short[32000];
        click[100] = 20000;
        short[] tail = StereoWav.Decode(await RenderDirectAsync("reverb", new() { ["size"] = "60", ["decay"] = "85", ["mix"] = "100" }, StereoWav.Encode(new StereoBuffer(click, click), 16000), folder)).Buffer.Left;
        // after the first tenth of a second: how much of the energy is in the highs (the energy of the difference between neighbours, over the energy of the signal; white noise gives 2, a dark tail far less)
        double signal = 0, difference = 0;
        for (int i = 1600; i < tail.Length; i++)
        {
            signal += (double)tail[i] * tail[i];
            double d = tail[i] - tail[i - 1];
            difference += d * d;
        }

        Assert.True(signal > 0, "there must be a tail");
        Assert.True(difference / signal < 1.0, $"the tail is too bright: {difference / signal:F2}");
    }

    [Fact]
    public async Task More_decay_rings_longer_and_the_room_does_not_blow_up_the_level()
    {
        short[] click = new short[32000];
        click[100] = 20000;
        async Task<short[]> Room(string decay) => StereoWav.Decode(await RenderDirectAsync("reverb", new() { ["decay"] = decay, ["mix"] = "100" }, StereoWav.Encode(new StereoBuffer(click, click), 16000), folder)).Buffer.Left;
        static double Late(short[] s) => s.Skip(16000).Sum(v => (double)v * v);
        short[] shortRoom = await Room("20"), longRoom = await Room("90");
        Assert.True(Late(longRoom) > Late(shortRoom) * 4, "a long decay must ring on after a second");
        Assert.True(longRoom.Skip(200).Max(v => Math.Abs((int)v)) < 20000, "the tail must stay below the click that made it");
    }

    [Fact]
    public async Task The_limiter_keeps_every_sample_under_the_ceiling_and_leaves_quiet_sound_alone()
    {
        short[] loud = [.. Enumerable.Range(0, 16000).Select(i => (short)(i % 2000 < 40 ? 32000 : 6000))];
        Dictionary<string, string> limiter = new() { ["ceiling"] = "-6", ["release"] = "50", ["lookahead"] = "5" };
        short[] limited = Wav.Decode(await RenderDirectAsync("limiter", limiter, Wav.Encode(loud, 16000), folder)).Samples;
        int ceiling = (int)((long)FixedAudio.GainFromDb(-6) * 32767 >> 15);
        Assert.True(limited.Max(v => Math.Abs((int)v)) <= ceiling + 2, "a sample passed the ceiling");
        Assert.True(limited.Max() > 3000, "the limiter must not silence the sound");
        short[] quiet = Enumerable.Repeat((short)3000, 16000).ToArray();
        short[] untouched = Wav.Decode(await RenderDirectAsync("limiter", limiter, Wav.Encode(quiet, 16000), folder)).Samples;
        Assert.All(untouched, v => Assert.InRange((int)v, 2995, 3000));
        byte[] stereoIn = StereoWav.Encode(new StereoBuffer(loud, quiet), 16000);
        StereoBuffer stereo = StereoWav.Decode(await RenderDirectAsync("limiter", limiter, stereoIn, folder)).Buffer;
        Assert.True(stereo.Left.Max(v => Math.Abs((int)v)) <= ceiling + 2);
        byte[] bypass = await RenderDirectAsync("limiter", new() { ["enabled"] = "off" }, stereoIn, folder);
        Assert.Equal(stereoIn, bypass);
    }

    [Fact]
    public void The_master_limiter_caps_peaks_at_the_ceiling_and_keeps_quiet_music()
    {
        short[] loud = [.. Enumerable.Range(0, 16000).Select(i => (short)(i % 2000 < 40 ? 32000 : 6000))];
        short[] capped = Wav.Decode(AudioEffects.LimitAudio(Wav.Encode(loud, 16000), -3)).Samples;
        int ceiling = (int)((long)FixedAudio.GainFromDb(-3) * 32767 >> 15);
        Assert.True(capped.Max(v => Math.Abs((int)v)) <= ceiling + 2);
        short[] quiet = Enumerable.Repeat((short)3000, 16000).ToArray();
        Assert.All(Wav.Decode(AudioEffects.LimitAudio(Wav.Encode(quiet, 16000), -3)).Samples, v => Assert.InRange((int)v, 2995, 3000));
        Assert.True(ChannelAudio.IsStereo(AudioEffects.LimitAudio(StereoWav.Encode(new StereoBuffer(loud, quiet), 16000), -3)));
    }

    [Fact]
    public async Task Pan_makes_a_mono_source_stereo_and_balances_a_stereo_source()
    {
        short[] tone = Enumerable.Repeat((short)10000, 1600).ToArray();
        StereoBuffer left = StereoWav.Decode(await RenderDirectAsync("pan", new() { ["pan"] = "-32" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.True(left.Left[0] > 9000 && left.Right[0] == 0);
        StereoBuffer centre = StereoWav.Decode(await RenderDirectAsync("pan", new() { ["pan"] = "0" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.Equal(centre.Left[0], centre.Right[0]);
        StereoBuffer balanced = StereoWav.Decode(await RenderDirectAsync("pan", new() { ["pan"] = "32" }, StereoWav.Encode(new StereoBuffer(tone, tone), 16000), folder)).Buffer;
        Assert.Equal(0, balanced.Left[0]);
        Assert.Equal(tone[0], balanced.Right[0]);
    }

    [Fact]
    public async Task An_automated_pan_sweeps_from_left_to_right_without_a_step()
    {
        short[] tone = Enumerable.Repeat((short)10000, 16000).ToArray();
        StereoBuffer swept = StereoWav.Decode(await RenderDirectAsync("pan", new() { ["pan"] = "auto(1:-32, 2:32)" }, Wav.Encode(tone, 16000), folder)).Buffer;
        Assert.True(swept.Left[0] > 9000 && swept.Right[0] < 200, "starts at the left");
        Assert.True(swept.Right[15999] > 9000 && swept.Left[15999] < 200, "ends at the right");
        for (int i = 1; i < swept.Left.Length; i++)
        {
            Assert.True(swept.Left[i] <= swept.Left[i - 1] + 1 && swept.Right[i] >= swept.Right[i - 1] - 1, "the sweep moves one way without a jump");
        }
    }

    [Fact]
    public async Task Width_zero_folds_to_mono_and_one_hundred_keeps_the_image()
    {
        short[] l = Enumerable.Repeat((short)8000, 1600).ToArray(), r = Enumerable.Repeat((short)2000, 1600).ToArray();
        byte[] input = StereoWav.Encode(new StereoBuffer(l, r), 16000);
        StereoBuffer mono = StereoWav.Decode(await RenderDirectAsync("width", new() { ["width"] = "0" }, input, folder)).Buffer;
        Assert.Equal(5000, mono.Left[0]);
        Assert.Equal(mono.Left[0], mono.Right[0]);
        StereoBuffer same = StereoWav.Decode(await RenderDirectAsync("width", new() { ["width"] = "100" }, input, folder)).Buffer;
        Assert.Equal(l, same.Left);
        Assert.Equal(r, same.Right);
        StereoBuffer wide = StereoWav.Decode(await RenderDirectAsync("width", new() { ["width"] = "200" }, input, folder)).Buffer;
        Assert.True(wide.Left[0] > 8000 && wide.Right[0] < 2000);
    }

    [Fact]
    public async Task The_gate_silences_quiet_noise_and_lets_loud_sound_through()
    {
        short[] input = [.. Enumerable.Range(0, 16000).Select(i => (short)(i < 8000 ? 40 : 12000))];
        short[] gated = Wav.Decode(await RenderDirectAsync("gate", new() { ["threshold"] = "-40", ["hold"] = "5", ["release"] = "10" }, Wav.Encode(input, 16000), folder)).Samples;
        Assert.Equal(0, gated[4000]);
        Assert.InRange((int)gated[15000], 11900, 12000);
    }

    [Fact]
    public async Task The_deesser_lowers_a_high_tone_but_leaves_a_low_tone()
    {
        short Tone(int i, int period) => (short)(10000 * (i % period < period / 2 ? 1 : -1));
        short[] high = [.. Enumerable.Range(0, 16000).Select(i => Tone(i, 4))];
        short[] low = [.. Enumerable.Range(0, 16000).Select(i => Tone(i, 160))];
        Dictionary<string, string> settings = new() { ["frequency"] = "2000", ["threshold"] = "-30", ["ratio"] = "10" };
        short[] ho = Wav.Decode(await RenderDirectAsync("deesser", settings, Wav.Encode(high, 16000), folder)).Samples;
        short[] lo = Wav.Decode(await RenderDirectAsync("deesser", settings, Wav.Encode(low, 16000), folder)).Samples;
        Assert.True(Energy(ho[8000..]) < Energy(high[8000..]) * 0.5);
        Assert.True(Energy(lo[8000..]) > Energy(low[8000..]) * 0.8);
    }

    [Theory]
    [InlineData("chorus")]
    [InlineData("flanger")]
    [InlineData("phaser")]
    [InlineData("bitcrusher")]
    public async Task Colour_effects_change_the_sound_deterministically_and_can_be_bypassed(string effect)
    {
        short[] tone = [.. Enumerable.Range(0, 16000).Select(i => (short)(8000 * (i % 80 < 40 ? 1 : -1) + (i % 7 * 100)))];
        byte[] input = Wav.Encode(tone, 16000);
        byte[] first = await RenderDirectAsync(effect, new(), input, folder);
        Assert.NotEqual(input, first);
        Assert.Equal(first, await RenderDirectAsync(effect, new(), input, folder));
        Assert.Equal(input, await RenderDirectAsync(effect, new() { ["enabled"] = "off" }, input, folder));
        byte[] stereo = StereoWav.Encode(new StereoBuffer(tone, tone), 16000);
        Assert.True(ChannelAudio.IsStereo(await RenderDirectAsync(effect, new(), stereo, folder)));
    }

    [Theory]
    [InlineData("eq", "  low 12\n  high -12\n")]
    [InlineData("distortion", "  drive 30\n  mode hard\n")]
    [InlineData("delay", "  time 100\n")]
    [InlineData("reverb", "  size 80\n")]
    [InlineData("compressor", "  threshold -30\n  ratio 8\n")]
    [InlineData("filter", "  type highpass\n  cutoff 4000\n")]
    public async Task Every_effect_changes_the_sound_and_renders_the_same_twice(string effect, string body)
    {
        (short[] dry, short[] wet) = await RunAsync(effect, body);
        (_, short[] again) = await RunAsync(effect, body);
        Assert.NotEqual(dry, wet);
        Assert.Equal(wet, again);
    }

    [Fact]
    public async Task A_lowpass_filter_removes_energy_and_a_compressor_lowers_peaks()
    {
        (short[] dry, short[] low) = await RunAsync("filter", "  cutoff 200\n");
        Assert.True(Energy(low) < Energy(dry));
        (_, short[] squashed) = await RunAsync("compressor", "  threshold -40\n  ratio 20\n");
        Assert.True(Energy(squashed) < Energy(dry));
    }

    [Fact]
    public async Task Compressor_steady_state_matches_the_linear_amplitude_transfer_curve_fixture()
    {
        const int sampleRate = 1000, input = 16000, thresholdDb = -18, ratio = 4;
        TemplateSpec template = EngineRig.Spec.Templates["compressor"];
        BlockNode block = new() { Type = "compressor", Name = "fx" };
        block.Parameters.Add(new Parameter("threshold", thresholdDb.ToString(System.Globalization.CultureInfo.InvariantCulture), 0));
        block.Parameters.Add(new Parameter("ratio", ratio.ToString(System.Globalization.CultureInfo.InvariantCulture), 0));
        block.Parameters.Add(new Parameter("attack", "1", 0));
        block.Parameters.Add(new Parameter("release", "5", 0));
        RenderRequest request = new(block, template, sampleRate, sampleRate, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]> { ["in"] = Wav.Encode(Enumerable.Repeat((short)input, sampleRate).ToArray(), sampleRate) }, 1, folder);
        IReadOnlyList<RenderOutput> outputs = await AudioEffects.All.Single(renderer => renderer.BlockType == "compressor").RenderAsync(request, CancellationToken.None);
        short[] rendered = Wav.Decode(outputs.Single().Data).Samples;
        long threshold = (long)FixedAudio.GainFromDb(thresholdDb) * 32767 >> 15;
        long reduced = threshold + (input - threshold) / ratio;
        long gain = reduced * 32768 / input;
        int expected = (int)((long)input * gain >> 15);
        Assert.Equal(expected, rendered[^1]);
        double inputDb = 20 * Math.Log10(input / 32768d), actualDb = 20 * Math.Log10(rendered[^1] / 32768d);
        double expectedDb = 20 * Math.Log10(Math.Pow(10, thresholdDb / 20d) + (Math.Pow(10, inputDb / 20d) - Math.Pow(10, thresholdDb / 20d)) / ratio);
        Assert.InRange(Math.Abs(actualDb - expectedDb), 0, 0.02);
    }


}

[Trait("Category", "Effects")]
public sealed class MelodyTests
{
    [Fact]
    public void A_melody_becomes_notes_per_bar_with_rests_and_accidentals()
    {
        var notes = BuiltInRenderers.Melody("c4 r:1 f#4:2 bb3 c4:4", 1, 2).Select(n => (n.Bar, n.Pitch, n.Beat, n.Beats)).ToList();
        Assert.Equal([(1, 60, 0.0, 1.0), (1, 66, 2.0, 2.0), (2, 58, 0.0, 1.0), (2, 60, 1.0, 3.0)], notes);
    }

    [Fact]
    public void Dynamic_markers_set_the_velocity_and_take_no_time()
    {
        var notes = BuiltInRenderers.MelodyWithVelocity("p c4 d4:1:100 mf e4 ff g4", 1, 1).Select(n => (n.Beat, n.Velocity)).ToList();
        Assert.Equal([(0.0, 48), (1.0, 100), (2.0, 80), (3.0, 112)], notes);
    }

    [Fact]
    public void A_crescendo_ramps_to_the_next_marker_by_time()
    {
        var notes = BuiltInRenderers.MelodyWithVelocity("mf c4 c4 < c4 c4 ff", 1, 1).Select(n => n.Velocity).ToList();
        Assert.Equal([80, 80, 80, 96], notes);
    }

    [Fact]
    public void A_hairpin_without_a_marker_runs_to_the_end_of_the_melody()
    {
        var notes = BuiltInRenderers.MelodyWithVelocity("f > c4 c4 c4 c4", 1, 1).Select(n => n.Velocity).ToList();
        Assert.Equal([96, 80, 64, 48], notes);
    }

    [Fact]
    public void Notes_stop_at_the_end_of_the_clip()
    {
        Assert.Empty(BuiltInRenderers.Melody("c4:4 d4", 1, 1).Skip(1));
    }

    [Fact]
    public async Task Bus_solo_mutes_the_other_buses_of_the_song()
    {
        string root = Path.Combine(Directory.CreateTempSubdirectory("ample-bus-").FullName, "p");
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "solo");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        BuildEngine engine = new(bank, EngineRig.Spec, p.Manifest.Id, root);
        string song = """
composition "s" { ample 1  tempo 120  master-seed 1 }
midi-clip c {
  chords Am
  bars 1
}
piano pn pinned@2.0 at 1 for 1 {
  set felt-piano
  from c.notes -> notes
}
bus one {
  solo on
  from pn.audio -> audio1
}
bus two {
  from pn.audio -> audio1
}

""";
        BuildReport report = await engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        async Task<long> Loudness(string block)
        {
            BlockResult r = report.Blocks.Single(b => b.Block == block);
            Assert.True(r.Status is "rendered" or "cached", r.Message);
            await using Stream data = bank.OpenData(bank.GetTake(r.TakeId!).Data[0].DataId);
            using MemoryStream ms = new();
            await data.CopyToAsync(ms);
            return Wav.Decode(ms.ToArray()).Samples.Sum(v => Math.Abs((long)v));
        }

        Assert.True(await Loudness("one") > 0);
        Assert.Equal(0, await Loudness("two"));
    }
}
