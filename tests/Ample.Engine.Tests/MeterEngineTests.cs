using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>A song in 3/4 (or 6/8, 5/4) has bars of that many beats; a song in 4/4, with or without the word, sounds exactly as before.</summary>
public class MeterEngineTests
{
    private static async Task<short[]> MasterAsync(string meter, string blocks)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string text = "composition \"m\" { ample 1  tempo 120  " + (meter.Length == 0 ? string.Empty : "meter " + meter + "  ") + "master-seed 1 }\n" + blocks;
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(text));
        using Stream stream = rig.Bank.OpenData(rig.Bank.GetTake(report.MasterTakeId!).Data[0].DataId);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Wav.Decode(bytes.ToArray()).Samples;
    }

    private const string Song = "midi-clip c {\n  chords Am F\n  bars 2\n}\nsynth s at 1 for 2 {\n  from c.notes -> notes\n}\nbass b at 1 for 2 {\n  from c.notes -> notes\n}\nmixer m {\n  inputs 2\n  from s.audio -> audio1\n  from b.audio -> audio2\n}\nmaster output {\n  from m.audio -> audio\n}\n";

    [Theory]
    [InlineData("", 176400)]       // 4/4: two bars of two seconds
    [InlineData("4/4", 176400)]
    [InlineData("3/4", 132300)]
    [InlineData("6/8", 132300)]
    [InlineData("5/4", 220500)]
    public async Task The_length_of_a_song_follows_the_meter(string meter, int samples) =>
        Assert.Equal(samples, (await MasterAsync(meter, Song)).Length);

    [Fact]
    public async Task Four_four_with_the_word_sounds_exactly_like_four_four_without_it() =>
        Assert.Equal(await MasterAsync(string.Empty, Song), await MasterAsync("4/4", Song));

    [Fact]
    public async Task A_twelve_step_drum_pattern_fills_a_three_four_bar_and_a_sixteen_step_pattern_fills_a_four_four_bar()
    {
        const string drums = "drums d at 1 for 2 {\n  kit builtin\n  pattern kick:x..x..x..x..\n}\nmaster output {\n  from d.audio -> audio\n}\n";
        short[] waltz = await MasterAsync("3/4", drums);
        Assert.Equal(132300, waltz.Length);
        // the kicks are one dotted-eighth apart: 3 sixteenths = 0.375 s at 120 bpm, so the second kick starts after 3 steps of 12 per 1.5 s bar
        int step = 66150 / 12;
        Assert.Contains(waltz.Skip(3 * step).Take(step).ToArray(), x => x != 0);
    }

    [Fact]
    public async Task The_click_block_has_the_beats_of_the_meter_in_each_bar_and_accents_the_first()
    {
        short[] waltz = await MasterAsync("3/4", "click tick at 1 for 2 {\n  accent on\n}\nmaster output {\n  from tick.audio -> audio\n}\n");
        Assert.Equal(132300, waltz.Length);
        int beat = 66150 / 3;
        int Peak(int from) => waltz.Skip(from).Take(beat).Max(x => Math.Abs((int)x));
        Assert.True(Peak(0) > Peak(beat), "the first beat of a bar is louder");
        Assert.Equal(Peak(beat), Peak(2 * beat));
        Assert.True(Peak(3 * beat) > Peak(4 * beat), "and again in the second bar, which starts after three beats");
    }
}
