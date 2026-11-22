using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>The levels of a mixer are in whole decibels: an entry with a decimal part rounds to the nearest one (it used to count as 0 dB without a word).</summary>
public class MixerGainRoundingTests
{
    private static async Task<short[]> MixAsync(string gains)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"p\" { ample 1  tempo 120  master-seed 1 }\n" +
            "click a at 1 for 2 {\n  per_beat 1\n}\nclick b at 1 for 2 {\n  per_beat 4\n}\n" +
            "mixer m {\n  inputs 2\n  gains " + gains + "\n  from a.audio -> audio1\n  from b.audio -> audio2\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string take = report.Blocks.First(b => b.Block == "m").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(take).Data[0].DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return ChannelAudio.Decode(bytes.ToArray()).Item1.Left;
    }

    [Theory]
    [InlineData("-4.5", "-5")]
    [InlineData("-4.4", "-4")]
    [InlineData("-4.6", "-5")]
    [InlineData("-5.0", "-5")]
    [InlineData("-4.5dB", "-5")]
    [InlineData("+2.5", "3")]
    public async Task A_decimal_gain_is_the_nearest_whole_decibel(string written, string whole)
    {
        short[] expected = await MixAsync("0 " + whole);
        Assert.Equal(expected, await MixAsync("0 " + written));
        Assert.NotEqual(await MixAsync("0 0"), await MixAsync("0 " + written));
    }

    [Fact]
    public async Task Whole_decibels_and_a_missing_or_unreadable_entry_are_as_before()
    {
        short[] zero = await MixAsync("0 0");
        Assert.Equal(zero, await MixAsync("0 abc"));
        Assert.Equal(zero, await MixAsync("0"));
        Assert.NotEqual(zero, await MixAsync("0 -6"));
    }
}
