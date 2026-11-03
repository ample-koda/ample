using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>The built-in bass and piano (no sample set) read the gain and humanize parameters like the sampled instruments do.</summary>
public class BuiltInInstrumentTests
{
    private static async Task<byte[]> RenderAsync(string type, string body)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"i\" { ample 1  tempo 120  master-seed 3 }\nmidi-clip phrase {\n  chords Am F\n  bars 2\n}\n" +
            type + " t at 1 for 2 {\n" + body + "  from phrase.notes -> notes\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string take = report.Blocks.First(b => b.Block == "t").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(take).Data[0].DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Theory]
    [InlineData("bass")]
    [InlineData("piano")]
    public async Task Gain_changes_the_level_and_zero_leaves_the_sound_as_it_was(string type)
    {
        byte[] plain = await RenderAsync(type, string.Empty);
        byte[] zero = await RenderAsync(type, "  gain 0 dB\n");
        byte[] quiet = await RenderAsync(type, "  gain -12 dB\n");
        Assert.Equal(plain, zero);
        short[] a = Wav.Decode(plain).Samples, b = Wav.Decode(quiet).Samples;
        int peakA = a.Max(x => Math.Abs((int)x)), peakB = b.Max(x => Math.Abs((int)x));
        Assert.True(peakB < peakA / 2, $"-12 dB is about a quarter of the level ({peakA} against {peakB})");
    }

    [Theory]
    [InlineData("bass")]
    [InlineData("piano")]
    public async Task Humanize_moves_the_notes_deterministically_and_zero_changes_nothing(string type)
    {
        byte[] plain = await RenderAsync(type, string.Empty);
        byte[] zero = await RenderAsync(type, "  humanize 0 %\n");
        byte[] human = await RenderAsync(type, "  humanize 100 %\n");
        byte[] again = await RenderAsync(type, "  humanize 100 %\n");
        Assert.Equal(plain, zero);
        Assert.NotEqual(plain, human);
        Assert.Equal(human, again);
    }
}
