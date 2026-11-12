using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>The delay locked to the tempo (<c>sync</c>): the echo comes one note value after the sound, in whole samples of the bar.</summary>
public sealed class DelaySyncTests
{
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz

    private static async Task<StereoBuffer> RenderAsync(string delayLines)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), "sequencer 1\nchannel kick drum sample=samples/kick.wav vol=0\npattern p bars=1\n  kick x...............\nplaylist\n  clip p track=1 at=1 for=1\n");
        string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nsequencer sq at 1 for 1 {\n  file sequences/a.seq\n}\ndelay d {\n" + delayLines + "  from sq.audio -> audio\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "d").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role is "mix" or "audio" or "out").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    [Fact]
    public async Task Sync_puts_the_echo_one_note_value_after_the_sound()
    {
        StereoBuffer echoed = await RenderAsync("  time 250 ms\n  sync 1/4\n  feedback 0 %\n  mix 100 %\n");
        Assert.NotEqual(0, echoed.Left[Bar / 4]);       // a quarter note at 120 BPM
        Assert.Equal(0, echoed.Left[11025 + 5]);        // time is not read while sync is on
    }

    [Fact]
    public async Task Sync_off_keeps_the_time_in_milliseconds()
    {
        StereoBuffer echoed = await RenderAsync("  time 250 ms\n  feedback 0 %\n  mix 100 %\n");
        Assert.NotEqual(0, echoed.Left[11025]);
        Assert.Equal(0, echoed.Left[Bar / 4 + 5]);
    }

    [Fact]
    public async Task Triplets_divide_the_bar_by_the_number_of_triplets()
    {
        StereoBuffer echoed = await RenderAsync("  sync 1/8t\n  feedback 0 %\n  mix 100 %\n");
        Assert.NotEqual(0, echoed.Left[Bar / 12]);
    }
}
