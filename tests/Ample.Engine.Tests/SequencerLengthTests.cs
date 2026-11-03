using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>An unplaced sequencer is as long as its playlist, not 8 bars; a placed one keeps its placement.</summary>
public sealed class SequencerLengthTests
{
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz
    private const string Seq = "sequencer 1\nchannel kick drum sample=samples/kick.wav vol=-3\npattern p bars=1\n  kick x...x...\nplaylist\n  clip p track=1 at=1 for=12\n";

    private static async Task<int> MixLengthAsync(string block)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Seq);
        string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\n" + block;
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "sq").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == "mix").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer.Length;
    }

    [Fact]
    public async Task An_unplaced_sequencer_is_as_long_as_its_playlist()
    {
        Assert.Equal(12 * Bar, await MixLengthAsync("sequencer sq {\n  file sequences/a.seq\n}\n"));
    }

    [Fact]
    public async Task The_blocks_that_follow_an_unplaced_sequencer_are_as_long_as_it_is()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Seq);
        const string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\n"
            + "sequencer sq {\n  file sequences/a.seq\n}\n"
            + "volume v {\n  gain -3 dB\n  from sq.audio -> audio\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "v").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role is "mixed" or "audio" or "out").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        Assert.Equal(12 * Bar, StereoWav.Decode(ms.ToArray()).Buffer.Length);
    }

    [Fact]
    public async Task A_song_with_nothing_placed_is_as_long_as_the_playlist_of_its_sequencer_and_the_master_keeps_all_of_it()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Seq);
        const string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\n"
            + "sequencer sq {\n  file sequences/a.seq\n}\n"
            + "master master {\n  slice_from 1\n  slice_to 12\n  from sq.audio -> audio\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.MasterTakeId!);
        using Stream data = rig.Bank.OpenData((take.Data.FirstOrDefault(d => d.Role == "master") ?? take.Data[0]).DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        Assert.Equal(12 * Bar, StereoWav.Decode(ms.ToArray()).Buffer.Length);
    }

    [Fact]
    public async Task A_take_whose_data_was_moved_away_renders_again_instead_of_failing()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Seq);
        const string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nsequencer sq {\n  file sequences/a.seq\n}\n";
        await rig.Engine.BuildAsync(EngineRig.Parse(song));
        foreach (string file in Directory.GetFiles(Path.Combine(rig.ProjectRoot, ".ample", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        BuildReport again = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        Assert.Equal("rendered", again.Blocks.Single(b => b.Block == "sq").Status);
        Assert.All(rig.Bank.GetTake(again.Blocks.Single(b => b.Block == "sq").TakeId!).Data, d => Assert.True(rig.Bank.DataExists(d.DataId)));
    }

    [Fact]
    public async Task A_placed_sequencer_and_one_with_bars_keep_their_own_length()
    {
        Assert.Equal(4 * Bar, await MixLengthAsync("sequencer sq at 1 for 4 {\n  file sequences/a.seq\n}\n"));
    }
}
