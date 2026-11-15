using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A mixer that gets a clip as it was made and the place where it starts gives, sample for sample, what it gives from the clip padded to the whole range (issue 294).</summary>
[Trait("Category", "Engine")]
public sealed class MixerPlacementTests
{
    private const int Rate = 8000;
    private const int PerBar = 400;

    private static short[] Ramp(int frames, int seed) => [.. Enumerable.Range(0, frames).Select(i => (short)(((i * 37) + seed) % 9000 - 4500))];

    private static async Task<byte[]> MixAsync(Dictionary<string, byte[]> inputs, Dictionary<string, InputPlacement>? placements, int bars)
    {
        BlockNode block = new() { Type = "mixer", Name = "mix" };
        block.Parameters.Add(new Parameter("gains", "\"0 -6 3\"", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["mixer"], Rate, PerBar, 1, bars, new HashSet<int>(Enumerable.Range(1, bars)), inputs, 1, Path.GetTempPath()) { Placements = placements };
        IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.First(r => r.BlockType == "mixer").RenderAsync(request, CancellationToken.None);
        return outputs.First(o => o.Role == "mix").Data;
    }

    [Theory]
    [InlineData(1, 200, 8)]    // from the start
    [InlineData(5, 300, 8)]    // late in the arrangement
    [InlineData(7, 900, 8)]    // runs over the end of the range
    [InlineData(9, 100, 8)]    // wholly after the range
    [InlineData(1, 50, 4)]     // shorter than a bar
    public async Task A_mono_clip_gives_the_same_mix_placed_or_padded(int originBar, int frames, int bars)
    {
        short[] clip = Ramp(frames, 3), other = Ramp(bars * PerBar, 11);
        byte[] padded = BlockTiming.Place(Wav.Encode(clip, Rate), originBar, new BarRange(1, bars), PerBar);

        byte[] expected = await MixAsync(new() { ["in1"] = Wav.Encode(other, Rate), ["in2"] = padded }, null, bars);
        byte[] actual = await MixAsync(new() { ["in1"] = Wav.Encode(other, Rate), ["in2"] = Wav.Encode(clip, Rate) }, new() { ["in2"] = new InputPlacement((long)(originBar - 1) * PerBar) }, bars);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1, 200, 8)]
    [InlineData(6, 700, 8)]
    [InlineData(8, 2000, 8)]
    public async Task A_stereo_clip_gives_the_same_mix_placed_or_padded(int originBar, int frames, int bars)
    {
        byte[] clip = StereoWav.Encode(new StereoBuffer(Ramp(frames, 5), [.. Ramp(frames, 9).Select(v => (short)-v)]), Rate);
        byte[] padded = BlockTiming.Place(clip, originBar, new BarRange(1, bars), PerBar);

        byte[] expected = await MixAsync(new() { ["in1"] = Wav.Encode(Ramp(bars * PerBar, 2), Rate), ["in3"] = padded }, null, bars);
        byte[] actual = await MixAsync(new() { ["in1"] = Wav.Encode(Ramp(bars * PerBar, 2), Rate), ["in3"] = clip }, new() { ["in3"] = new InputPlacement((long)(originBar - 1) * PerBar) }, bars);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task A_range_that_starts_after_bar_one_puts_the_start_of_the_clip_before_the_range()
    {
        short[] clip = Ramp(1200, 7);   // three bars, starting at bar 2
        byte[] padded = BlockTiming.Place(Wav.Encode(clip, Rate), 2, new BarRange(3, 2), PerBar);   // the range is bars 3 and 4

        RenderRequest request(Dictionary<string, byte[]> inputs, Dictionary<string, InputPlacement>? placements)
        {
            BlockNode block = new() { Type = "mixer", Name = "mix" };
            return new RenderRequest(block, EngineRig.Spec.Templates["mixer"], Rate, PerBar, 3, 2, new HashSet<int> { 3, 4 }, inputs, 1, Path.GetTempPath()) { Placements = placements };
        }

        IBlockRenderer mixer = BuiltInRenderers.All.First(r => r.BlockType == "mixer");
        byte[] expected = (await mixer.RenderAsync(request(new() { ["in1"] = padded }, null), CancellationToken.None)).First(o => o.Role == "mix").Data;
        byte[] actual = (await mixer.RenderAsync(request(new() { ["in1"] = Wav.Encode(clip, Rate) }, new() { ["in1"] = new InputPlacement((long)(2 - 3) * PerBar) }), CancellationToken.None)).First(o => o.Role == "mix").Data;

        Assert.Equal(expected, actual);
    }
}
