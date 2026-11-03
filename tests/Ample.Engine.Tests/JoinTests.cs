using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Join")]
public sealed class JoinTests
{
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz

    private static StereoBuffer Output(EngineRig rig, BuildReport report, string block)
    {
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == block).TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == "audio").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    [Fact]
    public async Task Join_puts_the_inputs_one_after_the_other_with_a_gap_of_seconds_and_beats()
    {
        const string text = "composition \"j\" { ample 1  tempo 120  master-seed 1 }\n"
            + "drums a at 1 for 1 {\n  pattern beat-A\n}\n"
            + "drums b at 1 for 1 {\n  pattern beat-B\n}\n"
            + "join j at 1 for 4 {\n  seconds 0.5\n  beats 1\n  from a.audio -> audio1\n  from b.audio -> audio2\n}\n";
        EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "j").Status);

        StereoBuffer joined = Output(rig, report, "j");
        int gap = (int)Math.Round(0.5 * 44100) + Bar / 4;   // half a second and one beat
        Assert.Equal((2 * Bar) + gap, joined.Length);
        // the silence is where the gap is, and the second part starts after it
        Assert.All(joined.Left.Skip(Bar).Take(gap), s => Assert.Equal(0, s));
        Assert.Contains(joined.Left.Skip(Bar + gap), s => s != 0);
    }

    [Fact]
    public async Task Join_with_one_input_gives_that_input_and_off_passes_the_first_one_on()
    {
        const string text = "composition \"j\" { ample 1  tempo 120  master-seed 1 }\n"
            + "drums a at 1 for 1 {\n  pattern beat-A\n}\n"
            + "join j at 1 for 4 {\n  from a.audio -> audio1\n}\n";
        EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        Assert.Equal(Bar, Output(rig, report, "j").Length);
    }
}
