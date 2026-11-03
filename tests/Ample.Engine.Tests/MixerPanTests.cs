using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

public class MixerPanTests
{
    private static async Task<byte[]> MixAsync(string mixerBody)
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
        return bytes.ToArray();
    }

    [Fact]
    public async Task Without_pan_the_mix_of_mono_inputs_stays_mono()
    {
        Assert.False(ChannelAudio.IsStereo(await MixAsync(string.Empty)));
    }

    [Fact]
    public async Task Pan_left_and_right_places_the_two_mono_inputs_in_their_own_channel()
    {
        byte[] mix = await MixAsync("  pan l r\n");
        Assert.True(ChannelAudio.IsStereo(mix));
        (StereoBuffer both, _) = ChannelAudio.Decode(mix);
        int different = 0;
        for (int i = 0; i < both.Length; i++) if (both.Left[i] != both.Right[i]) different++;
        Assert.True(different > both.Length / 20, "left and right differ clearly");
        // input a (one click per beat) is hard left only: the right channel has clicks only where b has them, so it has more non-silent samples than the left of a alone
        Assert.True(both.Right.Count(x => x != 0) > both.Left.Count(x => x != 0));
    }

    [Fact]
    public async Task Channels_stereo_makes_a_stereo_mix_of_mono_inputs_with_equal_sides()
    {
        byte[] mix = await MixAsync("  channels stereo\n");
        Assert.True(ChannelAudio.IsStereo(mix));
        (StereoBuffer both, _) = ChannelAudio.Decode(mix);
        Assert.Equal(both.Left, both.Right);
    }

    [Fact]
    public async Task Channels_mono_sums_a_panned_mix_into_one_channel()
    {
        byte[] mono = await MixAsync("  pan l r\n  channels mono\n");
        Assert.False(ChannelAudio.IsStereo(mono));
        (StereoBuffer both, _) = ChannelAudio.Decode(mono);
        Assert.Contains(both.Left, x => x != 0);
    }
}
