using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The built-in drums (no kit) use the steps of the pattern, as a sample kit does: sixteen steps in a 4/4 bar, a shorter grid repeats inside the bar.</summary>
public sealed class BuiltInDrumsTests
{
    private const int Rate = 44100;
    private const int SamplesPerBar = Rate * 2;   // 120 bpm, four beats

    private static async Task<byte[]> Render(string pattern, int bars = 2, params (string Name, string Value)[] more)
    {
        BlockNode block = new() { Type = "drums", Name = "d", Class = "stable", Version = "1.0" };
        block.Parameters.Add(new Parameter("pattern", pattern, 1));
        foreach ((string name, string value) in more) block.Parameters.Add(new Parameter(name, value, 1));
        RenderRequest request = new(block, EngineRig.Spec.Templates["drums"], Rate, SamplesPerBar, 1, bars, new HashSet<int>(Enumerable.Range(1, bars)), new Dictionary<string, byte[]>(), 0, Path.GetTempPath(), null, null);
        IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.First(r => r.BlockType == "drums").RenderAsync(request, CancellationToken.None);
        return outputs.First(o => o.Role == "audio").Data;
    }

    [Fact]
    public async Task The_steps_of_a_grid_decide_where_the_hits_fall()
    {
        byte[] early = await Render("kick:x...............");
        byte[] later = await Render("kick:....x...........");
        byte[] every = await Render("kick:xxxxxxxxxxxxxxxx");
        Assert.NotEqual(early, later);
        Assert.NotEqual(early, every);
    }

    [Fact]
    public async Task Another_voice_row_is_heard()
    {
        Assert.NotEqual(await Render("kick:x..............."), await Render("kick:x............... snare:........x......."));
    }

    [Fact]
    public async Task A_grid_longer_than_a_bar_goes_on_over_the_next_bars()
    {
        // 32 steps are two bars: the kick on step 17 falls on the first step of the second bar, and the first bar is silent
        short[] samples = Wav.Decode(await Render("kick:" + new string('.', 16) + "x" + new string('.', 15))).Samples;
        Assert.All(samples.Take(SamplesPerBar), s => Assert.Equal(0, s));
        Assert.Contains(samples.Skip(SamplesPerBar).Take(Rate / 10), s => s != 0);
        // and the grid starts again after its two bars: a third bar plays the first bar's steps (here: silence again)
        short[] three = Wav.Decode(await Render("kick:" + new string('.', 16) + "x" + new string('.', 15), 4)).Samples;
        Assert.All(three.Skip(2 * SamplesPerBar).Take(SamplesPerBar), s => Assert.Equal(0, s));
    }

    [Fact]
    public async Task The_other_voices_sound_different_from_a_hat_and_from_each_other()
    {
        byte[] hat = await Render("hat:x...............");
        foreach (string voice in new[] { "tom", "lowtom", "hightom", "clap", "rim", "openhat", "crash", "snare" })
        {
            Assert.NotEqual(hat, await Render(voice + ":x..............."));
        }

        Assert.NotEqual(await Render("tom:x..............."), await Render("lowtom:x..............."));
    }

    [Fact]
    public async Task Swing_humanize_and_the_options_of_a_voice_change_the_sound()
    {
        const string grid = "hat:xxxxxxxxxxxxxxxx";
        byte[] plain = await Render(grid);
        Assert.NotEqual(plain, await Render(grid, 2, ("swing", "30")));
        Assert.NotEqual(plain, await Render(grid, 2, ("humanize", "50")));
        Assert.Equal(await Render(grid, 2, ("humanize", "50")), await Render(grid, 2, ("humanize", "50")));   // the same every time
        Assert.NotEqual(plain, await Render("hat:xxxxxxxxxxxxxxxx@l-12"));
        Assert.NotEqual(plain, await Render("hat:xxxxxxxxxxxxxxxx@s40"));
        Assert.All(Wav.Decode(await Render("hat:xxxxxxxxxxxxxxxx@m")).Samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public async Task A_choke_rule_cuts_an_open_hat_when_a_closed_hat_is_struck()
    {
        const string grid = "openhat:x....... hat:..x.....";   // the closed hat falls inside the open one
        Assert.NotEqual(await Render(grid), await Render(grid, 2, ("choke", "openhat:hat")));
    }

    [Fact]
    public async Task The_steps_parameter_decides_how_long_a_step_lasts()
    {
        // kick on the second step: with 16 steps it falls 1/16 of a bar in, with steps 8 1/8 of a bar in
        short[] sixteen = Wav.Decode(await Render("kick:.x..............", 1)).Samples, eight = Wav.Decode(await Render("kick:.x..............", 1, ("steps", "8"))).Samples;
        int FirstSound(short[] samples) => Array.FindIndex(samples, s => s != 0);
        Assert.InRange(FirstSound(sixteen), SamplesPerBar / 16 - 2, SamplesPerBar / 16 + 2);
        Assert.InRange(FirstSound(eight), SamplesPerBar / 8 - 2, SamplesPerBar / 8 + 2);
    }

    [Fact]
    public async Task A_beat_follows_the_steps_too()
    {
        // beat-A at steps 8: the groove is the same pattern, spread over two bars; it is not the groove of the usual steps
        Assert.NotEqual(await Render("beat-A", 2), await Render("beat-A", 2, ("steps", "8")));
        // and a name is the same as its grid
        Assert.Equal(await Render("beat-A", 2, ("steps", "8")), await Render("kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.", 2, ("steps", "8")));
    }

    [Fact]
    public async Task Named_patterns_and_a_song_play_with_the_built_in_sound()
    {
        byte[] song = await Render("beat-A", 4, ("patterns", "verse=kick:x....... / fill=snare:xxxxxxxx"), ("song", "verse fill verse*2"));
        Assert.NotEqual(await Render("beat-A", 4), song);
    }
}
