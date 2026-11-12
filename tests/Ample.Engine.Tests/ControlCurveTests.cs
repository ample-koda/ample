using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The control that steers a parameter: its bytes, its value at a sample, and how a parameter that reads it becomes an automation curve (plan: control lanes, C02).</summary>
public sealed class ControlCurveTests
{
    private const int Rate = 44100;
    private const int SamplesPerBar = Rate * 2;

    private static SpecData Spec() => EngineRig.Spec;

    [Fact]
    public void A_control_survives_its_bytes_and_says_what_it_is()
    {
        ControlCurve curve = new(3, "Hz", [100000, 200000, 300000]);
        byte[] bytes = curve.Encode();
        Assert.True(ControlCurve.IsControl(bytes));
        Assert.False(ControlCurve.IsControl([1, 2, 3]));
        ControlCurve back = ControlCurve.Decode(bytes);
        Assert.Equal(3, back.FirstBar);
        Assert.Equal("Hz", back.Unit);
        Assert.Equal(curve.Values, back.Values);
        Assert.Null(ControlCurve.Decode(new ControlCurve(1, null, [5]).Encode()).Unit);
    }

    [Fact]
    public void The_value_at_a_sample_is_linear_between_two_values_and_holds_at_the_ends()
    {
        ControlCurve curve = new(1, null, [0, 1000, 500]);   // 1 ms apart
        Assert.Equal(0, curve.At(0, Rate, SamplesPerBar));
        Assert.Equal(0, curve.At(-50, Rate, SamplesPerBar));
        Assert.InRange(curve.At(Rate / 1000, Rate, SamplesPerBar), 990L, 1000L);   // (about) the second value: 44 samples is a little less than a millisecond
        Assert.InRange(curve.At(Rate / 2000, Rate, SamplesPerBar), 480L, 520L);       // half way up
        Assert.Equal(500, curve.At(Rate, Rate, SamplesPerBar));                  // after the end: the last value
    }

    [Fact]
    public void A_curve_that_starts_in_a_later_bar_is_read_on_the_timeline_of_the_song()
    {
        ControlCurve curve = new(3, null, [700, 700, 700]);
        Assert.Equal(700, curve.At(2L * SamplesPerBar, Rate, SamplesPerBar));
        Assert.Equal(700, curve.At(5, Rate, SamplesPerBar));   // before it starts: the first value
    }

    [Fact]
    public void A_parameter_with_a_control_expression_becomes_the_points_of_an_automation_curve()
    {
        SpecData spec = Spec();
        BlockNode filter = new() { Type = "filter", Name = "f", Class = "stable", Version = "1.0" };
        filter.Parameters.Add(new Parameter("cutoff", "600 + 3000 * level.level", 3));
        Dictionary<string, byte[]> inputs = new() { ["~level.level"] = new ControlCurve(1, null, [.. Enumerable.Repeat(500, 5000)]).Encode() };   // half way, 5 s
        (BlockNode block, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> points) = ControlBinding.Resolve(filter, spec.Templates["filter"], inputs, Rate, SamplesPerBar, 1, 1);
        Assert.Equal("ctl:cutoff", block.Parameters.First(p => p.Name == "cutoff").Value);
        IReadOnlyList<(long Sample, int Value)> cutoff = points["ctl:cutoff"];
        Assert.All(cutoff, p => Assert.Equal(2100, p.Value));   // 600 + 3000 * 0.5
        Assert.True(cutoff[^1].Sample >= SamplesPerBar);        // the curve covers the whole block
        // what the renderers read: the same as auto(...)
        using (ControlBinding.Use(points))
        {
            Assert.True(Automation.Is(block.Parameters.First(p => p.Name == "cutoff").Value));
            int[] curve = Automation.CurveAt(Automation.ParseSamples("ctl:cutoff", SamplesPerBar, Rate), 1, SamplesPerBar, 1000, v => Math.Clamp(v, 20, 20000));
            Assert.All(curve, v => Assert.Equal(2100, v));
        }
    }

    [Fact]
    public void A_general_control_that_is_connected_is_scaled_to_the_range_of_the_parameter_and_a_typed_one_is_used_as_it_is()
    {
        SpecData spec = Spec();
        BlockNode filter = new() { Type = "filter", Name = "f", Class = "stable", Version = "1.0" };
        Dictionary<string, byte[]> general = new() { ["cutoff"] = new ControlCurve(1, null, [.. Enumerable.Repeat(1000, 3000)]).Encode() };
        (_, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> top) = ControlBinding.Resolve(filter, spec.Templates["filter"], general, Rate, SamplesPerBar, 1, 1);
        Assert.All(top["ctl:cutoff"], p => Assert.Equal(20000, p.Value));   // the top of the cutoff range
        Dictionary<string, byte[]> typed = new() { ["cutoff"] = new ControlCurve(1, "Hz", [.. Enumerable.Repeat(1500000, 3000)]).Encode() };
        (_, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> hz) = ControlBinding.Resolve(filter, spec.Templates["filter"], typed, Rate, SamplesPerBar, 1, 1);
        Assert.All(hz["ctl:cutoff"], p => Assert.Equal(1500, p.Value));     // 1500 Hz
    }

    [Fact]
    public void A_block_without_controls_is_left_alone()
    {
        SpecData spec = Spec();
        BlockNode filter = new() { Type = "filter", Name = "f", Class = "stable", Version = "1.0" };
        filter.Parameters.Add(new Parameter("cutoff", "auto(1:200, 3:2000)", 2));
        (BlockNode block, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> points) = ControlBinding.Resolve(filter, spec.Templates["filter"], new Dictionary<string, byte[]>(), Rate, SamplesPerBar, 1, 1);
        Assert.Same(filter, block);
        Assert.Empty(points);
    }

    [Fact]
    public void A_block_that_reads_a_control_depends_on_the_block_that_makes_it()
    {
        BlockNode filter = new() { Type = "filter", Name = "f" };
        filter.ControlRefs.Add(new ControlRef("traffic", "level", "cutoff", 4));
        FromLine dependency = Assert.Single(filter.Dependencies());
        Assert.Equal(("traffic", "level", "~traffic.level"), (dependency.Block, dependency.Output, dependency.Input));
    }
}

/// <summary>A whole build: a control steers a parameter of another block and the sound really changes (plan: control lanes).</summary>
public sealed class ControlBuildTests
{
    private const string Song = "composition \"c\" { ample 1  tempo 120  master-seed 1 }\n" +
        "drums beat at 1 for 4 {\n  pattern kick:x...x...x...x... hat:x.x.x.x.x.x.x.x.\n}\n" +
        "curve ramp {\n  value VALUE\n}\n" +
        "volume level {\n  gain -40 dB + 40 dB * ramp.level\n  from beat.audio -> audio\n}\n";

    private static async Task<(BuildReport Report, byte[] Sound, EngineRig Rig)> Build(string value)
    {
        EngineRig rig = await EngineRig.CreateAsync();
        AmpleDocument doc = EngineRig.Parse(Song.Replace("VALUE", value, StringComparison.Ordinal));
        BlockNode volume = doc.Blocks.First(b => b.Name == "level");
        Assert.Contains(volume.ControlRefs, c => c is { Block: "ramp", Output: "level", Parameter: "gain" });
        BuildReport report = await rig.Engine.BuildAsync(doc);
        string take = report.Blocks.First(b => b.Block == "level").TakeId!;
        string plan = rig.Bank.GetPlan(rig.Bank.GetTake(take).PlanId).CanonicalJson;
        Assert.Contains(report.Blocks.First(b => b.Block == "ramp").TakeId!, plan);   // the plan of the block holds the take of the control it reads
        using Stream s = rig.Bank.OpenData(rig.Bank.GetTake(take).Data.First(d => d.Role == "out" || d.Role == "mixed").DataId);
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return (report, ms.ToArray(), rig);
    }

    private static double Rms(byte[] wav, double fromSeconds, double toSeconds)
    {
        (StereoBuffer both, int rate) = ChannelAudio.Decode(wav);
        int a = (int)(fromSeconds * rate), b = Math.Min(both.Left.Length, (int)(toSeconds * rate));
        double sum = 0;
        for (int i = a; i < b; i++) sum += (double)both.Left[i] * both.Left[i];
        return Math.Sqrt(sum / Math.Max(1, b - a));
    }

    [Fact]
    public async Task A_curve_that_rises_makes_the_volume_it_steers_louder_and_the_block_waits_for_it()
    {
        (BuildReport quiet, byte[] quietSound, EngineRig a) = await Build("0");
        (BuildReport loud, byte[] loudSound, EngineRig b) = await Build("auto(1:0, 3:1000)");
        using (a) using (b)
        {
            Assert.Contains(loud.Blocks, x => x.Block == "ramp");   // the control was built first
            Assert.NotEqual(quietSound, loudSound);
            double early = Rms(loudSound, 0.0, 1.0), late = Rms(loudSound, 3.0, 4.0);
            Assert.True(late > early * 5, $"the sound should rise with the curve: early {early:F1}, late {late:F1}");
            Assert.True(Rms(quietSound, 3.0, 4.0) < late / 5, "without the curve it stays at -40 dB");
        }
    }

    [Fact]
    public async Task A_curve_that_is_not_placed_lasts_as_long_as_the_song_and_the_notes_of_a_fire_follow()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        const string text = "composition \"c\" { ample 1  tempo 120  master-seed 1 }\n" +
            "drums beat at 1 for 12 {\n  pattern kick:x...x...x...x...\n}\n" +
            "curve ramp {\n  value lfo(2, 0, 1000)\n}\n" +
            "fire hit {\n  note 36\n  from ramp.level -> input\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string curveTake = report.Blocks.First(b => b.Block == "ramp").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(curveTake).Data.First(d => d.Role == "level").DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        ControlCurve curve = ControlCurve.Decode(bytes.ToArray());
        Assert.True(curve.Values.Length >= 23000, $"12 bars at 120 bpm are 24 seconds, the curve has {curve.Values.Length} values");
        using Stream notes = rig.Bank.OpenData(rig.Bank.GetTake(report.Blocks.First(b => b.Block == "hit").TakeId!).Data.First(d => d.Role == "notes").DataId);
        using StreamReader reader = new(notes);
        string json = reader.ReadToEnd();
        Assert.Contains("\"bar\":11", json.Replace(" ", string.Empty, StringComparison.Ordinal));   // a note in bar 11, past the first 8 bars
    }
}
