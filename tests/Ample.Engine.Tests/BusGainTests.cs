using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>
/// The gain of a bus, written with its unit ("-12 dB") or without, changes the level. Before, the unit made the number unreadable and the gain became 0 dB without a word.
/// </summary>
public class BusGainTests
{
    private static async Task<short[]> RenderBusAsync(string gainLine)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"i\" { ample 1  tempo 120  master-seed 3 }\nmidi-clip phrase {\n  chords Am F\n  bars 2\n}\n" +
            "synth s at 1 for 2 {\n  from phrase.notes -> notes\n}\n" +
            "bus b at 1 for 2 {\n" + gainLine + "  from s.audio -> audio1\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string take = report.Blocks.First(b => b.Block == "b").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(take).Data[0].DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Wav.Decode(bytes.ToArray()).Samples;
    }

    private static int Peak(short[] samples) => samples.Max(x => Math.Abs((int)x));

    [Fact]
    public async Task A_bus_gain_with_its_unit_lowers_the_level()
    {
        int plain = Peak(await RenderBusAsync(string.Empty));
        int quiet = Peak(await RenderBusAsync("  gain -12 dB\n"));
        Assert.True(quiet < plain / 2, $"-12 dB is about a quarter of the level ({plain} against {quiet})");
    }

    [Fact]
    public async Task A_bus_gain_reads_the_same_with_and_without_the_unit()
    {
        short[] withUnit = await RenderBusAsync("  gain -6 dB\n");
        short[] without = await RenderBusAsync("  gain -6\n");
        Assert.Equal(without, withUnit);
        Assert.True(Peak(withUnit) < Peak(await RenderBusAsync(string.Empty)));
    }

    [Fact]
    public async Task A_bus_gain_of_zero_leaves_the_sound_as_it_was()
    {
        Assert.Equal(await RenderBusAsync(string.Empty), await RenderBusAsync("  gain 0 dB\n"));
    }
}
