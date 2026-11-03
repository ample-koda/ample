using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

public class ClickBlockTests
{
    private static async Task<short[]> RenderAsync(string body)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"c\" { ample 1  tempo 120  master-seed 1 }\nclick tick at 1 for 2 {\n" + body + "}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string take = report.Blocks.First(b => b.Block == "tick").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(take).Data.First(d => d.Role == "out").DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Wav.Decode(bytes.ToArray()).Samples;
    }

    [Fact]
    public async Task One_click_per_beat_over_the_length_of_the_block_with_a_louder_first_beat()
    {
        short[] s = await RenderAsync(string.Empty);
        Assert.Equal(2 * 88200, s.Length);   // 2 bars at 120 bpm
        int beat = 88200 / 4;
        int Peak(int from) => s.Skip(from).Take(beat).Max(x => Math.Abs((int)x));
        Assert.True(Peak(0) > Peak(beat), "the first beat of a bar is louder");
        Assert.Equal(Peak(beat), Peak(2 * beat));
        Assert.True(Peak(4 * beat) > Peak(5 * beat), "and again in the second bar");
        Assert.Equal(0, s[beat - 1]);   // silent before the next click
    }

    [Fact]
    public async Task Per_beat_four_has_four_times_as_many_clicks_and_accent_off_makes_them_equal()
    {
        short[] four = await RenderAsync("  per_beat 4\n");
        int clicks = 0;
        for (int i = 1; i < four.Length; i++) if (four[i - 1] == 0 && four[i] != 0) clicks++;
        short[] one = await RenderAsync("  accent off\n");
        int beat = 88200 / 4;
        Assert.Equal(one.Skip(0).Take(beat).Max(x => Math.Abs((int)x)), one.Skip(beat).Take(beat).Max(x => Math.Abs((int)x)));
        Assert.True(clicks >= 30, $"expected about 32 clicks, found {clicks}");
        Assert.Equal(four.Length, one.Length);
    }
}
