using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Fades")]
public sealed class FadeTests
{
    private const string Head = "composition \"f\" { ample 1  tempo 120  master-seed 1 }\ndrums a at 1 for 6 {\n  pattern beat-A\n}\ndrums b at 1 for 6 {\n  pattern beat-B\n}\n";
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz

    private static StereoBuffer Take(EngineRig rig, BuildReport report, string block, string role)
    {
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == block).TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == role).DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    private static StereoBuffer Mono(EngineRig rig, BuildReport report, string block)
    {
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == block).TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data[0].DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoBuffer.FromMono(Wav.Decode(ms.ToArray()).Samples);
    }

    private static async Task<(EngineRig Rig, BuildReport Report)> BuildAsync(string tail)
    {
        EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(Head + tail));
        return (rig, report);
    }

    [Theory]
    [InlineData("fadein(5, 2)", true, 5_000_000, false, 2_000_000, false, FadeShape.Linear)]
    [InlineData("fadeout(12s, 1.5s, equal)", false, 12_000_000, true, 1_500_000, true, FadeShape.Equal)]
    [InlineData("fadein(5.5, 0.25s, exp)", true, 5_500_000, false, 250_000, true, FadeShape.Exp)]
    [InlineData(" fadeout( 0s , 3 , LIN ) ", false, 0, true, 3_000_000, false, FadeShape.Linear)]
    public void Fade_expressions_are_read_with_their_units(string text, bool fadeIn, long start, bool startSeconds, long length, bool lengthSeconds, FadeShape shape)
    {
        FadeSpec spec = Fades.Parse(text);
        Assert.Equal((fadeIn, new FadePosition(start, startSeconds), new FadePosition(length, lengthSeconds), shape), (spec.In, spec.Start, spec.Length, spec.Shape));
    }

    [Theory]
    [InlineData("fadein(0, 2)")]
    [InlineData("fadein(5)")]
    [InlineData("fadein(5, 0)")]
    [InlineData("fadein(5, 2, wobble)")]
    [InlineData("fadein(5, 2, lin, 4)")]
    [InlineData("fadein(1.1234567, 2)")]
    [InlineData("fadeup(5, 2)")]
    [InlineData("fadein(-1s, 2)")]
    public void A_malformed_fade_says_AMP5371_and_names_where_fades_work(string text)
    {
        AmpleException e = Assert.Throws<AmpleException>(() => Fades.Parse(text));
        Assert.Equal("AMP5371", e.DiagnosticCode);
        Assert.Contains("volume gain", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_straight_fade_in_is_silent_before_its_start_a_line_in_between_and_full_after_its_end()
    {
        int[] amount = Fades.Amount(Fades.Parse("fadein(2, 1)"), 44100, 1000, 1, 4000);
        Assert.All(amount.Take(1001), a => Assert.Equal(0, a));
        Assert.Equal(16384, amount[1500]);
        Assert.Equal(32768, amount[2000]);
        Assert.All(amount.Skip(2000), a => Assert.Equal(32768, a));
    }

    [Fact]
    public void A_fade_out_starts_full_and_ends_in_exact_silence_for_every_shape()
    {
        foreach (string shape in new[] { "lin", "equal", "exp" })
        {
            int[] amount = Fades.Amount(Fades.Parse($"fadeout(2, 1, {shape})"), 44100, 1000, 1, 4000);
            Assert.All(amount.Take(1001), a => Assert.Equal(32768, a));
            Assert.All(amount.Skip(2000), a => Assert.Equal(0, a));
            for (int i = 1000; i < 2000; i++) Assert.True(amount[i] >= amount[i + 1], $"{shape} must not rise (sample {i})");
        }
    }

    [Fact]
    public void Equal_power_adds_up_to_constant_power_and_the_straight_shape_to_constant_amplitude()
    {
        for (int p = 0; p <= 32768; p += 512)
        {
            double inAmount = Fades.ShapeOf(FadeShape.Equal, p) / 32768.0, outAmount = Fades.ShapeOf(FadeShape.Equal, 32768 - p) / 32768.0;
            Assert.InRange(inAmount * inAmount + outAmount * outAmount, 0.999, 1.001);
            Assert.Equal(32768, Fades.ShapeOf(FadeShape.Linear, p) + Fades.ShapeOf(FadeShape.Linear, 32768 - p));
        }

        Assert.Equal(8192, Fades.ShapeOf(FadeShape.Exp, 16384));
    }

    [Fact]
    public void Bars_and_seconds_that_describe_the_same_time_give_the_same_samples_and_a_decimal_bar_is_exact()
    {
        int[] bars = Fades.Amount(Fades.Parse("fadein(3, 1, equal)"), 44100, Bar, 1, 5 * Bar);
        int[] seconds = Fades.Amount(Fades.Parse("fadein(4s, 2s, equal)"), 44100, Bar, 1, 5 * Bar);
        Assert.Equal(bars, seconds);
        int[] half = Fades.Amount(Fades.Parse("fadeout(2.5, 0.5)"), 44100, Bar, 1, 4 * Bar);
        Assert.Equal(32768, half[(int)(1.5 * Bar)]);
        Assert.Equal(0, half[2 * Bar]);
        // a block that starts at bar 3 sees the same fade as the song does
        int[] late = Fades.Amount(Fades.Parse("fadein(3, 1)"), 44100, Bar, 3, Bar);
        Assert.Equal(0, late[0]);
        Assert.Equal(32768 / 2, late[Bar / 2]);
    }

    [Fact]
    public async Task A_gain_formula_fades_the_sound_and_leaves_exact_silence_after_the_fade_out()
    {
        (EngineRig rig, BuildReport plain) = await BuildAsync("volume v {\n  from a.audio -> audio\n}\n");
        using EngineRig keep = rig;
        BuildReport faded = await rig.Engine.BuildAsync(EngineRig.Parse(Head + "volume v {\n  from a.audio -> audio\n  gain fadeout(2, 1, lin)\n}\n"));
        Assert.Equal("rendered", faded.Blocks.Single(b => b.Block == "v").Status);
        StereoBuffer open = Take(rig, plain, "v", "mixed"), fade = Take(rig, faded, "v", "mixed");
        Assert.Equal(open.Left.Take(Bar).ToArray(), fade.Left.Take(Bar).ToArray());   // before the fade: untouched
        Assert.All(fade.Left.Skip(2 * Bar), s => Assert.Equal(0, s));                  // after the fade: exact silence
        Assert.All(fade.Right.Skip(2 * Bar), s => Assert.Equal(0, s));
        Assert.Contains(fade.Left.Skip(Bar).Take(Bar), s => s != 0);                  // there is sound inside the fade
        Assert.True(fade.Left.Skip(Bar).Take(Bar).Select(s => Math.Abs((long)s)).Sum() < open.Left.Skip(Bar).Take(Bar).Select(s => Math.Abs((long)s)).Sum());
    }

    [Fact]
    public async Task The_fade_block_and_the_gain_formula_with_the_same_settings_give_the_same_samples()
    {
        (EngineRig rig, BuildReport report) = await BuildAsync(
            "volume v {\n  from a.audio -> audio\n  gain fadeout(3, 1.5s, equal)\n}\nfade f {\n  from a.audio -> audio\n  direction out\n  start 3\n  length 1.5s\n  shape equal\n}\n");
        using EngineRig keep = rig;
        Assert.All(report.Blocks, b => Assert.Equal("rendered", b.Status));
        StereoBuffer formula = Take(rig, report, "v", "mixed"), block = Take(rig, report, "f", "out");
        Assert.Equal(formula.Left, block.Left);
        Assert.Equal(formula.Right, block.Right);
    }

    [Fact]
    public async Task A_fade_in_block_is_silent_until_its_start()
    {
        (EngineRig rig, BuildReport report) = await BuildAsync("fade f {\n  from a.audio -> audio\n  direction in\n  start 3\n  length 1\n}\n");
        using EngineRig keep = rig;
        StereoBuffer block = Take(rig, report, "f", "out");
        Assert.All(block.Left.Take(2 * Bar), s => Assert.Equal(0, s));
        Assert.Contains(block.Left.Skip(3 * Bar), s => s != 0);
    }

    [Fact]
    public async Task A_crossfade_is_the_first_input_before_it_the_second_after_it_and_a_missing_input_is_silence()
    {
        (EngineRig rig, BuildReport report) = await BuildAsync("crossfade x {\n  from a.audio -> a\n  from b.audio -> b\n  start 3\n  length 1\n}\ncrossfade only {\n  from b.audio -> b\n  start 3\n  length 1\n}\n");
        using EngineRig keep = rig;
        Assert.All(report.Blocks, b => Assert.Equal("rendered", b.Status));
        StereoBuffer x = Take(rig, report, "x", "out"), a = Mono(rig, report, "a"), b = Mono(rig, report, "b");
        Assert.Equal(a.Left.Take(2 * Bar).ToArray(), x.Left.Take(2 * Bar).ToArray());
        Assert.Equal(b.Left.Skip(4 * Bar).Take(Bar).ToArray(), x.Left.Skip(4 * Bar).Take(Bar).ToArray());
        StereoBuffer only = Take(rig, report, "only", "out");
        Assert.All(only.Left.Take(2 * Bar), s => Assert.Equal(0, s));   // b is silent before its fade in, and a is missing
    }

    [Fact]
    public async Task A_fade_on_a_pan_is_refused_with_AMP5371_and_a_fade_on_an_effect_mix_renders()
    {
        (EngineRig rig, BuildReport report) = await BuildAsync(
            "pan p {\n  from a.audio -> audio\n  pan fadein(2, 1)\n}\nreverb r {\n  from a.audio -> audio\n  mix fadein(2, 2s, equal)\n}\n");
        using EngineRig keep = rig;
        BlockResult pan = report.Blocks.Single(b => b.Block == "p");
        Assert.Equal("failed", pan.Status);
        Assert.Contains("AMP5371", pan.Message, StringComparison.Ordinal);
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "r").Status);
    }

    [Fact]
    public async Task The_fade_blocks_do_not_follow_the_master_seed()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string Song(int seed) => $"composition \"f\" {{ ample 1  tempo 120  master-seed {seed} }}\nmidi-clip c {{\n  chords Am\n  bars 1\n}}\nfade f {{\n  start 1\n  length 1\n}}\n";
        await rig.Engine.BuildAsync(EngineRig.Parse(Song(1)));
        BuildReport again = await rig.Engine.BuildAsync(EngineRig.Parse(Song(2)));
        Assert.Equal("cached", again.Blocks.Single(b => b.Block == "f").Status);
    }
}
