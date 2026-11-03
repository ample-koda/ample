using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The blocks that make and use controls: follow, curve, map and fire (plan: control lanes, C03).</summary>
public sealed class ControlBlocksTests
{
    private const int Rate = 44100;
    private const int SamplesPerBar = Rate * 2;   // 120 bpm, four beats

    private static IBlockRenderer Renderer(string type) => BuiltInRenderers.All.First(r => r.BlockType == type);

    private static async Task<IReadOnlyList<RenderOutput>> Render(string type, Dictionary<string, string> parameters, Dictionary<string, byte[]> inputs, int bars = 5)
    {
        BlockNode block = new() { Type = type, Name = "b", Class = "stable", Version = "1.0" };
        foreach ((string name, string value) in parameters) block.Parameters.Add(new Parameter(name, value, 1));
        RenderRequest request = new(block, EngineRig.Spec.Templates[type], Rate, SamplesPerBar, 1, bars, new HashSet<int>(Enumerable.Range(1, bars)), inputs, 0, Path.GetTempPath(), null, null);
        return await Renderer(type).RenderAsync(request, CancellationToken.None);
    }

    /// <summary>10 seconds: a quiet steady bed of noise, and a low rumble (a car) between 4 and 6 seconds.</summary>
    private static byte[] BedWithCar()
    {
        short[] samples = new short[Rate * 10];
        uint seed = 12345;
        for (int i = 0; i < samples.Length; i++)
        {
            seed = (seed * 1664525) + 1013904223;
            int noise = (int)((seed >> 16) % 801) - 400;
            double t = i / (double)Rate;
            int car = t is >= 4 and < 6 ? (int)(9000 * Math.Sin(2 * Math.PI * 90 * t) * Math.Sin(Math.PI * (t - 4) / 2)) : 0;
            samples[i] = (short)(noise + car);
        }

        return Wav.Encode(samples, Rate);
    }

    private static ControlCurve Level(IReadOnlyList<RenderOutput> outputs, string role = "level") => ControlCurve.Decode(outputs.First(o => o.Role == role).Data);

    [Fact]
    public async Task Follow_makes_a_control_that_rises_when_a_car_passes_and_ignores_the_steady_bed()
    {
        IReadOnlyList<RenderOutput> outputs = await Render("follow", new() { ["band"] = "low", ["baseline_window"] = "3 s" }, new() { ["in"] = BedWithCar() });
        ControlCurve level = Level(outputs);
        Assert.Equal(1, level.FirstBar);
        Assert.Null(level.Unit);
        Assert.True(level.Values.Length is >= 9999 and <= 10002);
        Assert.InRange(level.Values[2000], 0, 150);    // 2 s: only the bed
        Assert.InRange(level.Values[5000], 600, 1000); // 5 s: the car
        Assert.InRange(level.Values[9800], 0, 250);    // 9.8 s: the car has gone
        ControlCurve gate = Level(outputs, "gate");
        Assert.Equal(0, gate.Values[2000]);
        Assert.Equal(1000, gate.Values[5000]);
        Assert.Equal(0, gate.Values[9500]);            // the hold time (500 ms) is over
    }

    [Fact]
    public async Task Follow_with_auto_chooses_the_band_where_the_car_stands_out_and_says_so_in_the_take()
    {
        byte[] audio = BedWithCar();
        (StereoBuffer both, int rate) = ChannelAudio.Decode(audio);
        FollowSuggestion s = FollowAnalysis.Suggest([.. both.Left.Select((v, i) => (v + both.Right[i]) / 2)], rate);
        Assert.Equal("low", s.Band);   // the rumble is at 90 Hz, the bed is noise
        Assert.InRange(s.AttackMilliseconds, 20, 300);
        IReadOnlyList<RenderOutput> outputs = await Render("follow", new() { ["band"] = "auto", ["attack"] = "auto", ["release"] = "auto", ["baseline_window"] = "auto" }, new() { ["in"] = audio });
        Assert.Contains("followAuto", outputs.First(o => o.Role == "level").MetadataJson);
        Assert.InRange(Level(outputs).Values[5000], 400, 1000);
        Assert.Contains("band low", s.ToBlockLines(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follow_is_the_same_every_time()
    {
        Dictionary<string, byte[]> inputs = new() { ["in"] = BedWithCar() };
        IReadOnlyList<RenderOutput> a = await Render("follow", new() { ["band"] = "40 Hz .. 400 Hz" }, inputs);
        IReadOnlyList<RenderOutput> b = await Render("follow", new() { ["band"] = "40 Hz .. 400 Hz" }, inputs);
        Assert.Equal(a.First(o => o.Role == "level").Data, b.First(o => o.Role == "level").Data);
    }

    [Fact]
    public async Task Follow_with_a_unit_and_a_range_makes_a_typed_control_in_that_unit()
    {
        IReadOnlyList<RenderOutput> outputs = await Render("follow", new() { ["band"] = "low", ["unit"] = "Hz", ["range"] = "600 .. 3600" }, new() { ["in"] = BedWithCar() });
        ControlCurve level = Level(outputs);
        Assert.Equal("Hz", level.Unit);
        Assert.InRange(level.Values[2000], 600000, 800000);       // about 600 Hz without a car (x 1000)
        Assert.InRange(level.Values[5000], 2400000, 3600000);     // up to 3600 Hz while it passes
    }

    [Fact]
    public async Task Curve_writes_a_control_like_a_parameter_and_a_unit_makes_it_typed()
    {
        ControlCurve general = Level(await Render("curve", new() { ["value"] = "auto(1:0, 3:1000)" }, []));
        Assert.Null(general.Unit);
        Assert.InRange(general.Values[0], 0, 5);
        Assert.InRange(general.Values[4000], 990, 1000);          // from bar 3 (4 s) on: 1000
        ControlCurve typed = Level(await Render("curve", new() { ["value"] = "auto(1:200, 3:2000)", ["unit"] = "Hz" }, []));
        Assert.Equal("Hz", typed.Unit);
        Assert.InRange(typed.Values[0], 199000, 201000);
        Assert.InRange(typed.Values[4000], 1990000, 2010000);
    }

    [Fact]
    public async Task Map_rescales_inverts_limits_and_gives_a_unit()
    {
        byte[] source = new ControlCurve(1, null, [0, 250, 500, 1000, 1000]).Encode();
        ControlCurve scaled = Level(await Render("map", new() { ["out_low"] = "200", ["out_high"] = "2000", ["unit"] = "Hz" }, new() { ["in"] = source }));
        Assert.Equal("Hz", scaled.Unit);
        Assert.Equal([200000, 650000, 1100000, 2000000, 2000000], scaled.Values);
        ControlCurve inverted = Level(await Render("map", new() { ["invert"] = "on" }, new() { ["in"] = source }));
        Assert.Equal([1000, 750, 500, 0, 0], inverted.Values);
        ControlCurve limited = Level(await Render("map", new() { ["in_high"] = "0.5" }, new() { ["in"] = source }));
        Assert.Equal([0, 500, 1000, 1000, 1000], limited.Values);
    }

    [Fact]
    public async Task Fire_plays_one_note_for_each_time_the_control_passes_the_threshold()
    {
        // two cars: 1000 ms and 3000 ms (1 ms per value); the second wobbles around the threshold and fires once
        int[] values = new int[5000];
        for (int k = 1000; k < 1600; k++) values[k] = 900;
        for (int k = 3000; k < 3600; k++) values[k] = k % 40 < 20 ? 900 : 300;
        IReadOnlyList<RenderOutput> outputs = await Render("fire", new() { ["note"] = "36", ["gap"] = "600 ms" }, new() { ["in"] = new ControlCurve(1, null, values).Encode() });
        using JsonDocument notes = JsonDocument.Parse(outputs.First(o => o.Role == "notes").Data);
        JsonElement[] list = [.. notes.RootElement.GetProperty("notes").EnumerateArray()];
        Assert.Equal(2, list.Length);
        Assert.All(list, n => Assert.Equal(36, n.GetProperty("pitch").GetInt32()));
        Assert.Equal(1, list[0].GetProperty("bar").GetInt32());                  // 1.0 s is inside bar 1 (2 s a bar)
        Assert.Equal(2m, list[0].GetProperty("beat").GetDecimal(), 1);            // half a bar: beat 2
        Assert.Equal(2, list[1].GetProperty("bar").GetInt32());                  // 3.0 s is in bar 2
    }

    [Fact]
    public void The_control_blocks_have_templates_with_control_ports()
    {
        Assert.Equal("control", EngineRig.Spec.Templates["follow"].Outputs.First(o => o.Name == "level").Type);
        Assert.Equal("control", EngineRig.Spec.Templates["map"].Inputs.First(i => i.Name == "in").Type);
        Assert.Equal("midi", EngineRig.Spec.Templates["fire"].Outputs.First(o => o.Name == "notes").Type);
        Assert.All(ControlBlocks.Types, t => Assert.True(BuildEngine.IsSeedFree(t), t));
    }

    private static byte[] TwoBursts()
    {
        short[] samples = new short[Rate * 10];
        for (int i = (int)(1.5 * Rate); i < (int)(3.0 * Rate); i++) samples[i] = (short)(8000 * Math.Sin(i * 0.05));
        for (int i = (int)(6.0 * Rate); i < (int)(7.5 * Rate); i++) samples[i] = (short)(8000 * Math.Sin(i * 0.05));
        return Wav.Encode(samples, Rate);
    }

    [Theory]
    [InlineData("warn", 6.1, true)]
    [InlineData("all", 6.1, false)]
    [InlineData("first", 1.6, false)]
    [InlineData("last", 1.6, false)]
    public async Task Trim_cuts_the_silence_and_says_so_when_there_are_several_stretches(string sections, double seconds, bool warning)
    {
        IReadOnlyList<RenderOutput> outputs = await Render("trim", new() { ["sections"] = sections }, new() { ["in"] = TwoBursts() });
        (short[] cut, int rate) = Wav.Decode(outputs.First(o => o.Role == "out").Data);
        Assert.InRange(cut.Length / (double)rate, seconds - 0.2, seconds + 0.2);   // first/last: one stretch of 1.5 s and 50 ms around it; all and warn: from 1.45 s to 7.55 s
        string report = System.Text.Encoding.UTF8.GetString(outputs.First(o => o.Role == "report").Data);
        Assert.Contains("2 sections", report, StringComparison.Ordinal);
        Assert.Equal(warning, report.Contains("WARNING", StringComparison.Ordinal));
        Assert.True(Math.Abs((int)cut[(int)(0.1 * rate)]) > 0 || Math.Abs((int)cut[(int)(0.2 * rate)]) > 0, "the cut starts at the sound, not at the silence");
    }

    [Fact]
    public async Task Trim_of_a_recording_with_one_stretch_has_no_warning_and_a_recording_of_silence_is_left_alone()
    {
        short[] one = new short[Rate * 6];
        for (int i = Rate; i < 2 * Rate; i++) one[i] = (short)(9000 * Math.Sin(i * 0.05));
        IReadOnlyList<RenderOutput> a = await Render("trim", [], new() { ["in"] = Wav.Encode(one, Rate) });
        Assert.DoesNotContain("WARNING", System.Text.Encoding.UTF8.GetString(a.First(o => o.Role == "report").Data), StringComparison.Ordinal);
        Assert.InRange(Wav.Decode(a.First(o => o.Role == "out").Data).Samples.Length / (double)Rate, 1.0, 1.3);
        byte[] silence = Wav.Encode(new short[Rate * 3], Rate);
        IReadOnlyList<RenderOutput> b = await Render("trim", [], new() { ["in"] = silence });
        Assert.Equal(silence, b.First(o => o.Role == "out").Data);
        Assert.Contains("no sound", System.Text.Encoding.UTF8.GetString(b.First(o => o.Role == "report").Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trim_fades_by_a_share_of_what_is_kept()
    {
        short[] one = new short[Rate * 6];
        for (int i = Rate; i < 2 * Rate; i++) one[i] = 10000;
        IReadOnlyList<RenderOutput> o = await Render("trim", new() { ["fadein"] = "50%", ["fadeout"] = "25%", ["padding"] = "0 ms" }, new() { ["in"] = Wav.Encode(one, Rate) });
        (short[] cut, _) = Wav.Decode(o.First(x => x.Role == "out").Data);
        int peak = Array.IndexOf(cut, cut.Max());
        Assert.InRange(peak, (int)(cut.Length * 0.45), (int)(cut.Length * 0.8));   // up over the first half, down over the last quarter
        Assert.True(Math.Abs((int)cut[0]) < 200 && Math.Abs((int)cut[^1]) < 200);
    }
}
