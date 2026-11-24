using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>
/// A mono source gets a stereo room from a reverb and a stereo chorus (Studio bug hunt 2026-11-24, bug 29): the right channel of the reverb is a little longer and the
/// chorus voices are a quarter period apart, but a mono source took the mono path and stayed mono (reverb 6, chorus 3).
/// </summary>
public sealed class MonoStereoRoomTests
{
    private static async Task<StereoBuffer> RenderAsync(string effect)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000, -10000, -20000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(
            Path.Combine(rig.ProjectRoot, "sequences", "a.seq"),
            "sequencer 1\nchannel kick drum sample=samples/kick.wav vol=0\npattern p bars=1\n  kick x...x...x...x...\nplaylist\n  clip p track=1 at=1 for=1\n");
        string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\n"
            + "sequencer sq at 1 for 1 {\n  file sequences/a.seq\n}\n" + effect + " fx {\n  mix 100\n  from sq.audio -> in\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "fx").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role is "out" or "audio").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        Assert.True(ChannelAudio.IsStereo(ms.ToArray()), "the result of a mono source is stereo");
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    [Theory]
    [InlineData("reverb")]
    [InlineData("chorus")]
    public async Task A_mono_source_gets_two_different_channels(string effect)
    {
        StereoBuffer room = await RenderAsync(effect);
        Assert.Contains(Enumerable.Range(0, room.Left.Length), i => room.Left[i] != room.Right[i]);
    }
}
