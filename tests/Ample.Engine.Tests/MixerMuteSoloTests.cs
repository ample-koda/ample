using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

public class MixerMuteSoloTests
{
    private static async Task<short[]> MixAsync(string mixerBody)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"p\" { ample 1  tempo 120  master-seed 1 }\n" +
            "click a at 1 for 2 {\n  per_beat 1\n}\nclick b at 1 for 2 {\n  per_beat 4\n}\n" +
            "mixer m {\n  inputs 2\n" + mixerBody + "  from a.audio -> audio1\n  from b.audio -> audio2\n}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        string take = report.Blocks.First(b => b.Block == "m").TakeId!;
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(take).Data[0].DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return ChannelAudio.Decode(bytes.ToArray()).Item1.Left;
    }

    [Fact]
    public async Task Without_the_new_parameters_or_with_everything_off_the_mix_is_unchanged()
    {
        short[] plain = await MixAsync(string.Empty);
        Assert.Equal(plain, await MixAsync("  mutes off off\n  solos off off\n"));
        Assert.Equal(plain, await MixAsync("  solos on on\n"));
        Assert.Equal(plain, await MixAsync("  mutes off\n"));
    }

    [Fact]
    public async Task A_muted_input_is_silent_and_equals_the_solo_of_the_other_one()
    {
        short[] plain = await MixAsync(string.Empty);
        short[] muted = await MixAsync("  mutes on off\n");
        Assert.NotEqual(plain, muted);
        Assert.Contains(muted, x => x != 0);
        Assert.Equal(muted, await MixAsync("  solos off on\n"));
    }

    [Fact]
    public async Task A_mute_wins_over_a_solo_and_muting_everything_is_silence()
    {
        Assert.All(await MixAsync("  mutes on on\n"), x => Assert.Equal(0, x));
        Assert.All(await MixAsync("  mutes on off\n  solos on off\n"), x => Assert.Equal(0, x));
    }

    [Fact]
    public async Task A_muted_input_keeps_its_gain_for_when_it_is_unmuted()
    {
        Assert.Equal(await MixAsync("  gains -6 0\n"), await MixAsync("  gains -6 0\n  mutes off off\n"));
    }
}
