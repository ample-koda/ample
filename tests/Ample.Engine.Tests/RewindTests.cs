using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

public sealed class RewindTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-rewind-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    // twelve seconds in which every sample has its own value, so where the music is can be read from the output
    private static short[] Source() => [.. Enumerable.Range(0, 12 * Rate).Select(i => (short)(i / 8))];

    private async Task<IReadOnlyList<RenderOutput>> RenderAsync(Dictionary<string, string> parameters, short[] source, short[]? fx = null)
    {
        BlockNode block = new() { Type = "rewind", Name = "fx" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = new() { ["in"] = Wav.Encode(source, Rate) };
        if (fx is not null)
        {
            inputs["fx"] = Wav.Encode(fx, Rate);
        }

        RenderRequest request = new(block, EngineRig.Spec.Templates["rewind"], Rate, Rate, 1, 12, new HashSet<int> { 1 }, inputs, 1, folder);
        return await ExtraEffects.All.Single(r => r.BlockType == "rewind").RenderAsync(request, CancellationToken.None);
    }

    private static short[] Out(IReadOnlyList<RenderOutput> outputs, string role = "out") => Wav.Decode(outputs.Single(o => o.Role == role).Data).Samples;

    [Fact]
    public async Task The_music_stops_a_scratch_is_heard_and_the_music_goes_on_five_seconds_back()
    {
        short[] source = Source();
        short[] output = Out(await RenderAsync(new() { ["events"] = "at 6s scratch 2s back 5s", ["fade"] = "0" }, source));
        Assert.Equal(source.Length, output.Length);
        Assert.Equal(source[..(6 * Rate)], output[..(6 * Rate)]);
        Assert.Contains(Enumerable.Range(7 * Rate, Rate), i => output[i] != source[i]);   // the scratch is another sound than the music
        Assert.InRange((int)output[(8 * Rate) - 1], -2000, 2000);   // and it has died away
        for (int k = 0; k < 4 * Rate; k += 997)
        {
            Assert.Equal(source[Rate + k], output[(8 * Rate) + k]);   // from 8 s the music is the sound from 1 s
        }
    }

    [Fact]
    public async Task A_jump_back_cannot_go_before_the_start_and_a_negative_back_skips_forward()
    {
        short[] source = Source();
        short[] clamped = Out(await RenderAsync(new() { ["events"] = "at 1s scratch 1s back 10s", ["fade"] = "0" }, source));
        Assert.Equal(source[..Rate], clamped[(2 * Rate)..(3 * Rate)]);
        short[] skipped = Out(await RenderAsync(new() { ["events"] = "at 2s scratch 1s back -3s", ["fade"] = "0" }, source));
        Assert.Equal(source[(5 * Rate)..(6 * Rate)], skipped[(3 * Rate)..(4 * Rate)]);
    }

    [Fact]
    public async Task Past_the_end_of_the_sound_it_stops_or_loops()
    {
        short[] source = Source();
        Dictionary<string, string> events = new() { ["events"] = "at 10s scratch 1s back -5s", ["fade"] = "0" };
        Assert.All(Out(await RenderAsync(events, source))[(11 * Rate)..], v => Assert.Equal(0, v));
        events["end"] = "loop";
        short[] looped = Out(await RenderAsync(events, source));
        Assert.Equal(source[(3 * Rate)..(4 * Rate)], looped[(11 * Rate)..(12 * Rate)]);
    }

    [Fact]
    public async Task Several_events_come_out_of_one_render_and_the_region_holds_only_the_scratches()
    {
        short[] source = Source();
        IReadOnlyList<RenderOutput> outputs = await RenderAsync(new() { ["events"] = "at 2s scratch 1s back 1s; at 7s scratch 1s back 2s", ["fade"] = "0" }, source);
        short[] region = Out(outputs, "region");
        Assert.All(region[..(2 * Rate)], v => Assert.Equal(0, v));
        Assert.Contains(region[(2 * Rate)..(3 * Rate)], v => v != 0);
        Assert.All(region[(3 * Rate)..(7 * Rate)], v => Assert.Equal(0, v));
        Assert.Contains(region[(7 * Rate)..(8 * Rate)], v => v != 0);
        Assert.All(region[(8 * Rate)..], v => Assert.Equal(0, v));
        Assert.Equal(Out(outputs), Out(await RenderAsync(new() { ["events"] = "at 2s scratch 1s back 1s; at 7s scratch 1s back 2s", ["fade"] = "0" }, source)));   // the same bytes again
    }

    [Fact]
    public async Task A_real_sample_on_the_fx_input_is_the_scratch_and_sound_fx_needs_it()
    {
        short[] source = Source();
        short[] fx = [.. Enumerable.Repeat((short)5000, 3 * Rate)];
        short[] output = Out(await RenderAsync(new() { ["events"] = "at 4s scratch 2s back 1s", ["fade"] = "0" }, source, fx));
        Assert.All(output[(4 * Rate)..((6 * Rate) - 1)], v => Assert.Equal(5000, v));
        await Assert.ThrowsAsync<AmpleException>(async () => await RenderAsync(new() { ["events"] = "at 4s scratch 2s back 1s", ["sound"] = "fx" }, source));
    }

    [Fact]
    public async Task The_start_up_of_the_record_runs_behind_the_straight_cut_and_unreadable_events_are_an_error()
    {
        short[] source = Source();
        short[] straight = Out(await RenderAsync(new() { ["events"] = "at 6s scratch 2s back 5s", ["fade"] = "0" }, source));
        short[] smeared = Out(await RenderAsync(new() { ["events"] = "at 6s scratch 2s back 5s", ["fade"] = "0", ["smear"] = "1000" }, source));
        Assert.True(smeared[(8 * Rate) + 8000] < straight[(8 * Rate) + 8000] - 250);   // slower at first
        int lag = straight[(10 * Rate)] - smeared[(10 * Rate)];
        Assert.InRange(lag * 8, (int)(0.1 * Rate), (int)(0.3 * Rate));   // and about a fifth of a second behind when it has settled
        AmpleException bad = await Assert.ThrowsAsync<AmpleException>(async () => await RenderAsync(new() { ["events"] = "at 6 seconds scratch two" }, source));
        Assert.Equal("AMP5396", bad.DiagnosticCode);
        Assert.Equal(2, RewindEvents.Parse("at 1s scratch 1s back 1s; at 4.5 scratch 500ms back -1s", Rate, Rate).Count);
        Assert.Equal(4 * Rate + (Rate / 2), RewindEvents.Parse("at 4.5", Rate, Rate)[0].At);
    }
}
