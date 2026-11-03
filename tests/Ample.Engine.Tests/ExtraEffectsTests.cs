using System.Security.Cryptography;
using System.Text;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "Effects")]
public sealed class ExtraEffectsTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-extra-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private async Task<IReadOnlyList<RenderOutput>> RenderAsync(string type, Dictionary<string, string> parameters, byte[]? input, ulong seed = 1, int bars = 1)
    {
        BlockNode block = new() { Type = type, Name = "fx" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = input is null ? [] : new() { ["in"] = input };
        RenderRequest request = new(block, EngineRig.Spec.Templates[type], Rate, Rate, 1, bars, new HashSet<int> { 1 }, inputs, seed, folder);
        return await ExtraEffects.All.Single(r => r.BlockType == type).RenderAsync(request, CancellationToken.None);
    }

    private static short[] Samples(IReadOnlyList<RenderOutput> outputs) => Wav.Decode(outputs.Single(o => o.Role == "out").Data).Samples;

    private static short[] Noise(int length, int amplitude, int seed = 12345)
    {
        short[] x = new short[length];
        uint state = (uint)seed;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            x[i] = (short)(((int)(state >> 16) % (2 * amplitude + 1)) - amplitude);
        }

        return x;
    }

    [Fact]
    public async Task Normalize_brings_the_peak_to_the_target_and_never_adds_more_than_max_gain()
    {
        short[] quiet = [.. Enumerable.Range(0, Rate).Select(i => (short)(100 * Math.Sin(i / 20.0)))];
        short[] loud = Samples(await RenderAsync("normalize", new() { ["peak"] = "-6" }, Wav.Encode(quiet, Rate)));
        Assert.InRange(loud.Max(s => Math.Abs((int)s)), 16300, 16500);   // -6 dBFS is 16423
        short[] capped = Samples(await RenderAsync("normalize", new() { ["peak"] = "-6", ["max_gain"] = "12" }, Wav.Encode(quiet, Rate)));
        Assert.InRange(capped.Max(s => Math.Abs((int)s)), 380, 420);     // 100 * 3.98
    }

    [Fact]
    public async Task Reverse_plays_the_sound_backwards_and_meter_passes_it_on_with_a_report()
    {
        short[] ramp = [.. Enumerable.Range(0, 1000).Select(i => (short)i)];
        short[] back = Samples(await RenderAsync("reverse", [], Wav.Encode(ramp, Rate)));
        Assert.Equal(ramp.Reverse().ToArray(), back);
        IReadOnlyList<RenderOutput> metered = await RenderAsync("meter", [], Wav.Encode(ramp, Rate));
        Assert.Equal(ramp, Wav.Decode(metered.Single(o => o.Role == "out").Data).Samples);
        string report = Encoding.UTF8.GetString(metered.Single(o => o.Role == "report").Data);
        Assert.Contains("peak", report, StringComparison.Ordinal);
        Assert.Contains("loudness", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tremolo_moves_the_level_and_autopan_moves_the_sound_between_the_sides()
    {
        short[] flat = Enumerable.Repeat((short)10000, Rate).ToArray();
        short[] trem = Samples(await RenderAsync("tremolo", new() { ["depth"] = "100", ["rate"] = "1" }, Wav.Encode(flat, Rate)));
        Assert.True(trem.Min() < 500 && trem.Max() > 9500, "the level goes from silence to full");
        byte[] panned = (await RenderAsync("tremolo", new() { ["mode"] = "autopan", ["depth"] = "100", ["rate"] = "1" }, Wav.Encode(flat, Rate))).Single(o => o.Role == "out").Data;
        StereoBuffer sides = StereoWav.Decode(panned).Buffer;
        Assert.True(sides.Left[Rate / 4] != sides.Right[Rate / 4], "left and right differ");
        Assert.True(Math.Abs(sides.Left[Rate / 4] + sides.Right[Rate / 4] - 10000) < 400, "the two sides together stay about constant");
    }

    [Fact]
    public void Filter_slopes_are_one_to_four_passes_and_unknown_words_stay_one()
    {
        Assert.Equal(1, ResonantFilter.PassesOf("12"));
        Assert.Equal(1, ResonantFilter.PassesOf(string.Empty));
        Assert.Equal(2, ResonantFilter.PassesOf("24"));
        Assert.Equal(3, ResonantFilter.PassesOf("36"));
        Assert.Equal(4, ResonantFilter.PassesOf("48"));
        Assert.Equal(1, ResonantFilter.PassesOf("60"));
    }

    [Fact]
    public async Task The_trance_gate_opens_and_closes_in_time_with_the_pattern_and_other_shapes_are_untouched()
    {
        short[] flat = Enumerable.Repeat((short)10000, Rate).ToArray();
        // RenderAsync tempo gives SamplesPerBar; measure the step length from the result instead of assuming it
        short[] gated = Samples(await RenderAsync("tremolo", new() { ["shape"] = "gate", ["sync"] = "1/16", ["depth"] = "100", ["pattern"] = "\"x.\"" }, Wav.Encode(flat, Rate)));
        Assert.Equal(10000, gated.Max());
        Assert.Equal(0, gated.Min());
        Assert.Equal(1, TremoloRenderer.GateLevel(string.Empty, 5, 100, 44100));
        Assert.Equal(-1, TremoloRenderer.GateLevel("x.", 150, 100, 44100));
        Assert.Equal(1, TremoloRenderer.GateLevel("x.", 250, 100, 44100));
        Assert.Equal(0, TremoloRenderer.GateLevel("e", 250, 100, 44100), 9);
        Assert.Equal(1, TremoloRenderer.GateLevel("1", 250, 100, 44100));
    }

    [Fact]
    public async Task The_parametric_eq_lifts_cuts_and_notches_by_band_and_a_flat_block_changes_nothing()
    {
        static short[] Tone(int hz) => [.. Enumerable.Range(0, Rate).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * hz * i / Rate)))];
        static double Rms(short[] x) => Math.Sqrt(x.Skip(2000).Select(v => (double)v * v).Average());
        short[] t1000 = Tone(1000);
        Assert.Equal(t1000, Samples(await RenderAsync("peq", [], Wav.Encode(t1000, Rate))));
        double plain = Rms(t1000);
        double lifted = Rms(Samples(await RenderAsync("peq", new() { ["band3_hz"] = "1000 Hz", ["band3_gain"] = "12 dB", ["band3_q"] = "2" }, Wav.Encode(t1000, Rate))));
        double cut = Rms(Samples(await RenderAsync("peq", new() { ["band3_hz"] = "1000 Hz", ["band3_gain"] = "-12 dB", ["band3_q"] = "2" }, Wav.Encode(t1000, Rate))));
        double notched = Rms(Samples(await RenderAsync("peq", new() { ["band4_type"] = "notch", ["band4_hz"] = "1000 Hz", ["band4_q"] = "6" }, Wav.Encode(t1000, Rate))));
        Assert.True(lifted > plain * 3 && cut < plain * 0.35 && notched < plain * 0.1, $"plain {plain} lifted {lifted} cut {cut} notched {notched}");
        short[] t100 = Tone(100);
        double shelf = Rms(Samples(await RenderAsync("peq", new() { ["band1_type"] = "lowshelf", ["band1_hz"] = "300 Hz", ["band1_gain"] = "12 dB" }, Wav.Encode(t100, Rate))));
        Assert.True(shelf > Rms(t100) * 3);
    }

    [Fact]
    public async Task The_parametric_eq_low_and_high_cut_remove_a_side_and_a_band_can_be_switched_off()
    {
        static short[] Tone(int hz) => [.. Enumerable.Range(0, Rate).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * hz * i / Rate)))];
        static double Rms(short[] x) => Math.Sqrt(x.Skip(2000).Select(v => (double)v * v).Average());
        short[] t100 = Tone(100);
        short[] t5000 = Tone(5000);
        double low = Rms(Samples(await RenderAsync("peq", new() { ["band1_type"] = "lowcut", ["band1_hz"] = "1000 Hz", ["band1_q"] = "0.7" }, Wav.Encode(t100, Rate))));
        double high = Rms(Samples(await RenderAsync("peq", new() { ["band6_type"] = "highcut", ["band6_hz"] = "1000 Hz", ["band6_q"] = "0.7" }, Wav.Encode(t5000, Rate))));
        Assert.True(low < Rms(t100) * 0.05, $"the low cut removes 100 Hz: {low}");
        Assert.True(high < Rms(t5000) * 0.1, $"the high cut removes 5 kHz: {high}");
        short[] off = Samples(await RenderAsync("peq", new() { ["band1_type"] = "lowcut", ["band1_hz"] = "1000 Hz", ["band1_enabled"] = "off" }, Wav.Encode(t100, Rate)));
        Assert.Equal(t100, off);
    }

    [Fact]
    public async Task Denoise_lowers_steady_noise_and_granular_and_convolution_keep_the_length()
    {
        short[] hiss = Noise(Rate * 2, 1000);
        short[] clean = Samples(await RenderAsync("denoise", new() { ["reduction"] = "20" }, Wav.Encode(hiss, Rate), bars: 2));
        double Rms(short[] x) => Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);
        Assert.True(Rms(clean) < Rms(hiss) / 2, "the noise is at least 6 dB lower");

        short[] grains = Samples(await RenderAsync("granular", new() { ["grain"] = "50", ["density"] = "60" }, Wav.Encode(hiss, Rate), seed: 7, bars: 2));
        Assert.Equal(hiss.Length, grains.Length);
        Assert.Equal(grains, Samples(await RenderAsync("granular", new() { ["grain"] = "50", ["density"] = "60" }, Wav.Encode(hiss, Rate), seed: 7, bars: 2)));   // the same seed, the same grains

        // an impulse response that is one tap, 100 samples late, gives the input 100 samples later
        short[] ir = new short[200];
        ir[100] = 32767;
        File.WriteAllBytes(Path.Combine(folder, "ir.wav"), Wav.Encode(ir, Rate));
        short[] tone = [.. Enumerable.Range(0, Rate).Select(i => (short)(8000 * Math.Sin(i / 15.0)))];
        short[] wet = Samples(await RenderAsync("convolution", new() { ["file"] = "ir.wav", ["mix"] = "100" }, Wav.Encode(tone, Rate)));
        Assert.Equal(tone.Length, wet.Length);
        Assert.InRange(Math.Abs(wet[1100] - tone[1000]), 0, 40);
    }

    [Fact]
    public async Task Ambience_fills_any_length_from_a_short_recording_and_is_deterministic()
    {
        File.WriteAllBytes(Path.Combine(folder, "rain.wav"), Wav.Encode(Noise(Rate * 3, 3000), Rate));
        Dictionary<string, string> parameters = new() { ["file"] = "rain.wav", ["segment"] = "2", ["crossfade"] = "0.5" };
        short[] one = Samples(await RenderAsync("ambience", parameters, null, seed: 3, bars: 10));
        short[] two = Samples(await RenderAsync("ambience", parameters, null, seed: 3, bars: 10));
        Assert.Equal(Rate * 10, one.Length);   // ten seconds from three
        Assert.Equal(one, two);
        Assert.True(one.Skip(Rate * 5).Any(v => v != 0), "the end is not silent either");
    }

    [Fact]
    public void Automation_points_may_be_written_in_seconds_and_in_decimal_bars()
    {
        Assert.Equal([(8000L, -60), (16000L, 0)], Automation.ParseSamples("auto(0.5s:-60, 1s:0)", 16000, 16000));
        Assert.Equal([(0L, 0), (8000L, -6)], Automation.ParseSamples("auto(1:0, 1.5:-6)", 16000, 16000));
        Assert.Equal([(0L, -40), (32000L, 0)], Automation.ParseSamples("auto(1:-40, 3:0 dB)", 16000, 16000));
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => Automation.ParseSamples("auto(0.5:0)", 16000, 16000));   // a bar starts at 1
        int[] curve = Automation.CurveAt(Automation.ParseSamples("auto(0s:-60, 1s:0)", 16000, 16000), 1, 16000, 16000, v => v + 100);
        Assert.Equal(40, curve[0]);
        Assert.InRange(curve[8000], 68, 72);
    }

    [Fact]
    public async Task Import_audio_loops_with_a_crossfade_so_the_join_has_no_jump()
    {
        short[] ramp = [.. Enumerable.Range(0, 1000).Select(i => (short)(i * 20))];
        File.WriteAllBytes(Path.Combine(folder, "ramp.wav"), Wav.Encode(ramp, Rate));
        async Task<short[]> LoopAsync(string crossfade)
        {
            BlockNode block = new() { Type = "import-audio", Name = "loop" };
            block.Parameters.Add(new Parameter("file", "ramp.wav", 0));
            block.Parameters.Add(new Parameter("loop", "on", 1));
            if (crossfade.Length > 0) block.Parameters.Add(new Parameter("crossfade", crossfade, 2));
            RenderRequest request = new(block, EngineRig.Spec.Templates["import-audio"], Rate, Rate, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 1, folder);
            IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "import-audio").RenderAsync(request, CancellationToken.None);
            return Wav.Decode(outputs.Single(o => o.Role == "audio").Data).Samples;
        }

        static int BiggestJump(short[] x) => Enumerable.Range(1, x.Length - 1).Max(i => Math.Abs(x[i] - x[i - 1]));
        Assert.True(BiggestJump(await LoopAsync(string.Empty)) > 15000, "a plain repeat jumps from the top of the ramp to the bottom");
        Assert.True(BiggestJump(await LoopAsync("0.0625")) < 400, "with a crossfade of 1000 samples the join is a slope");   // 1000 samples at 16 kHz
    }

    [Fact]
    public async Task Denoise_learns_its_noise_from_the_stretch_that_is_marked()
    {
        // one second of quiet hiss, then one second of loud noise: learning from the first second removes the hiss part, learning from the second one does not
        short[] quiet = Noise(Rate, 200, 5);
        short[] loud = Noise(Rate, 3000, 9);
        byte[] input = Wav.Encode([.. quiet, .. loud], Rate);
        double Rms(short[] x) => Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);
        short[] fromQuiet = Samples(await RenderAsync("denoise", new() { ["noise"] = "start", ["learn"] = "0.8", ["learn_from"] = "0", ["reduction"] = "30" }, input, bars: 2));
        short[] fromLoud = Samples(await RenderAsync("denoise", new() { ["noise"] = "start", ["learn"] = "0.8", ["learn_from"] = "1.1", ["reduction"] = "30" }, input, bars: 2));
        Assert.True(Rms(fromLoud[(Rate + 2000)..]) < Rms(fromQuiet[(Rate + 2000)..]) / 2, "learning the loud noise takes the loud part down much further");
    }

    [Theory]
    [InlineData("start", "5EDB4717F1D93C1DB9840E061A2245EA804F4C7264D477134299F8D7DC8DD54F")]
    [InlineData("auto", "F1F0E0FE9AC674003DAD6DD9A74D77B56A51BA174F4967A224047C6BB1CB6851")]
    public async Task Denoise_keeps_the_exact_output_for_each_noise_profile(string noise, string expectedHash)
    {
        byte[] input = Wav.Encode(Noise(Rate * 3, 1000), Rate);
        byte[] output = (await RenderAsync("denoise", new() { ["noise"] = noise, ["learn"] = "0.5", ["learn_from"] = "1", ["reduction"] = "6", ["sensitivity"] = "50" }, input, bars: 3)).Single(o => o.Role == "out").Data;
        string actualHash = Convert.ToHexString(SHA256.HashData(output));
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public async Task Denoise_optimize_is_an_execution_policy_that_never_changes_the_sound()
    {
        // noise=auto is the only path "optimize" affects (it decides whether the noise-floor scan's spectrum is kept or redone); every value must
        // still give the exact same cleaned sound, since it is declared affectsOutput:false in spec/templates/denoise.json.
        byte[] input = Wav.Encode(Noise(Rate * 3, 1000), Rate);
        async Task<byte[]> Run(string optimize) => (await RenderAsync("denoise", new() { ["noise"] = "auto", ["reduction"] = "6", ["sensitivity"] = "50", ["optimize"] = optimize }, input, bars: 3)).Single(o => o.Role == "out").Data;
        byte[] auto = await Run("auto"), speed = await Run("speed"), memory = await Run("memory");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(memory)), Convert.ToHexString(SHA256.HashData(speed)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(memory)), Convert.ToHexString(SHA256.HashData(auto)));
    }

    [Fact]
    public async Task Bus_sums_its_inputs_then_applies_one_gain_and_a_mute()
    {
        short[] a = [.. Enumerable.Range(0, Rate).Select(_ => (short)1000)], b = [.. Enumerable.Range(0, Rate).Select(_ => (short)500)];
        async Task<short[]> Run(Dictionary<string, string> parameters)
        {
            BlockNode block = new() { Type = "bus", Name = "scene" };
            foreach ((string name, string value) in parameters) block.Parameters.Add(new Parameter(name, value, 0));
            Dictionary<string, byte[]> inputs = new() { ["in1"] = Wav.Encode(a, Rate), ["in2"] = Wav.Encode(b, Rate) };
            RenderRequest request = new(block, EngineRig.Spec.Templates["bus"], Rate, Rate, 1, 1, new HashSet<int> { 1 }, inputs, 1, folder);
            IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "bus").RenderAsync(request, CancellationToken.None);
            return Wav.Decode(outputs.Single(o => o.Role == "mix").Data).Samples;
        }

        Assert.Equal(1500, (await Run([]))[100]);
        Assert.InRange((await Run(new() { ["gain"] = "-6" }))[100], 740, 760);
        Assert.Equal(0, (await Run(new() { ["mute"] = "on" }))[100]);
    }
}
