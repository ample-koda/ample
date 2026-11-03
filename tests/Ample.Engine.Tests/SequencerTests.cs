using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Sequencer")]
public sealed class SequencerTests
{
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz
    private const string Sample = "sequencer 1\nchannel kick drum sample=samples/kick.wav vol=-3\nchannel hat drum sample=samples/hat.wav pan=10\nchannel lead notes wave=square\npattern verse bars=2\n  kick x...x...|x.......\n  hat x.x.x.x.x.x.x.x.\n  lead 1:0:1:60:96 2:0:2:67:96\nplaylist\n  clip verse track=1 at=1 for=4\n  section Intro at=1 for=2 color=#4a90d9\n";

    private static short[] Impulse(short value, int length = 200)
    {
        short[] s = new short[length];
        s[0] = value;
        return s;
    }

    private static void Wave(string root, string rel, short[] samples)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Wav.Encode(samples, 44100));
    }

    private static async Task<EngineRig> RigAsync(string seq = Sample)
    {
        EngineRig rig = await EngineRig.CreateAsync();
        Wave(rig.ProjectRoot, "samples/kick.wav", Impulse(30000));
        Wave(rig.ProjectRoot, "samples/hat.wav", Impulse(20000));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), seq);
        return rig;
    }

    private static string Song(string lanes = "", string extra = "") => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\nsequencer sq at 1 for 4 {{\n  file sequences/a.seq\n{lanes}}}\n{extra}";

    private static StereoBuffer Role(EngineRig rig, BuildReport report, string role)
    {
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "sq").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == role).DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    private static async Task<BuildReport> Build(EngineRig rig, string song) => await rig.Engine.BuildAsync(EngineRig.Parse(song));

    [Fact]
    public void A_sequence_file_reads_and_writes_the_same_text_and_a_mistake_names_its_line()
    {
        SequenceFile seq = SequenceFile.Parse(Sample);
        Assert.Equal(["kick", "hat", "lead"], seq.Channels.Select(c => c.Name));
        Assert.Equal("drum", seq.Channels[0].Type);
        Assert.Equal("-3", seq.Channels[0].Get("vol"));
        Assert.Equal(2, seq.Patterns[0].Bars);
        Assert.Equal("x...x...|x.......", seq.Patterns[0].Rows["kick"]);
        Assert.Equal(new SeqClip("verse", 1, 1, 4), seq.Clips[0]);
        Assert.Equal("#4a90d9", seq.Sections[0].Color);
        Assert.Equal(Sample, seq.Format());
        AmpleException e = Assert.Throws<AmpleException>(() => SequenceFile.Parse("sequencer 1\nchannel kick drum\npattern p bars=0\n"));
        Assert.Equal("AMP5380", e.DiagnosticCode);
        Assert.Contains("line 3", e.Message, StringComparison.Ordinal);
        Assert.Throws<AmpleException>(() => SequenceFile.Parse("channel kick drum\n"));
        Assert.Throws<AmpleException>(() => SequenceFile.Parse("sequencer 1\nchannel kick wobble\n"));
        Assert.Throws<AmpleException>(() => SequenceFile.Parse("sequencer 1\nchannel kick drum\nchannel kick drum\n"));
    }

    [Fact]
    public void The_lanes_name_channels_joined_by_plus_or_all_and_a_wrong_name_is_AMP5380()
    {
        SequenceFile seq = SequenceFile.Parse(Sample);
        IReadOnlyDictionary<int, string[]> lanes = seq.ParseLanes("lane1=kick lane2=hat+lead lane8=all");
        Assert.Equal(["kick"], lanes[1]);
        Assert.Equal(["hat", "lead"], lanes[2]);
        Assert.Equal(["kick", "hat", "lead"], lanes[8]);
        Assert.Empty(seq.ParseLanes(string.Empty));
        foreach (string bad in new[] { "lane0=kick", "lane9=kick", "lane1=nope", "kick", "lane1" }) Assert.Equal("AMP5380", Assert.Throws<AmpleException>(() => seq.ParseLanes(bad)).DiagnosticCode);
    }

    [Fact]
    public async Task A_drum_step_lands_on_its_sixteenth_with_the_level_and_the_pan_of_its_channel()
    {
        using EngineRig rig = await RigAsync("sequencer 1\nchannel kick drum sample=samples/kick.wav vol=-3\npattern p bars=1\n  kick x...X...\nplaylist\n  clip p track=1 at=1 for=1\n");
        StereoBuffer mix = Role(rig, await Build(rig, Song().Replace("for 4", "for 1", StringComparison.Ordinal)), "mix");
        int step = Bar / 16;
        long hit = (30000L * (26000 * 3 / 4) >> 15), accent = (30000L * (32768 * 3 / 4) >> 15);
        long gain = FixedAudio.GainFromDb(-3), centre = gain * PanTable.LeftGain(0) >> 15;
        Assert.Equal((short)(hit * centre >> 15), mix.Left[0]);
        Assert.Equal((short)(accent * centre >> 15), mix.Left[4 * step]);
        Assert.Equal(0, mix.Left[step]);
        Assert.Equal(mix.Left[0], mix.Right[0]);                     // centred
        Assert.All(mix.Left.Skip(5 * step + 200).Take(2 * step - 200), s => Assert.Equal(0, s));   // between the steps (the grid repeats: a hit every eighth step)
    }

    [Fact]
    public async Task The_lanes_add_up_to_the_mix_and_only_lanes_that_name_channels_come_out()
    {
        using EngineRig rig = await RigAsync();
        BuildReport report = await Build(rig, Song("  lanes lane1=kick lane2=hat+lead\n"));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single().TakeId!);
        Assert.Equal(["lane1", "lane2", "mix"], take.Data.Select(d => d.Role).Order());
        StereoBuffer mix = Role(rig, report, "mix"), one = Role(rig, report, "lane1"), two = Role(rig, report, "lane2");
        for (int i = 0; i < mix.Length; i += 7)
        {
            Assert.InRange(mix.Left[i] - (one.Left[i] + two.Left[i]), -2, 2);
            Assert.InRange(mix.Right[i] - (one.Right[i] + two.Right[i]), -2, 2);
        }

        Assert.Contains(one.Left, s => s != 0);
        Assert.Contains(two.Left, s => s != 0);
    }

    [Fact]
    public async Task A_clip_longer_than_its_pattern_repeats_it_and_the_second_bar_of_a_pattern_plays_its_own_notes()
    {
        using EngineRig rig = await RigAsync();
        StereoBuffer mix = Role(rig, await Build(rig, Song()), "mix");
        Assert.Equal(mix.Left.Skip(0).Take(2 * Bar).ToArray(), mix.Left.Skip(2 * Bar).Take(2 * Bar).ToArray());
        StereoBuffer lead = Role(rig, await Build(rig, Song("  lanes lane1=lead\n")), "lane1");
        long first = lead.Left.Take(Bar).Select(s => Math.Abs((long)s)).Sum(), second = lead.Left.Skip(Bar).Take(Bar).Select(s => Math.Abs((long)s)).Sum();
        Assert.True(first > 0 && second > 0);                          // a note in bar one and the longer note in bar two
        Assert.Contains(lead.Left.Skip(Bar).Take(Bar / 8), s => s != 0);   // the note of bar two starts on its downbeat
    }

    [Fact]
    public async Task Mute_and_solo_decide_which_channels_are_heard()
    {
        using EngineRig rig = await RigAsync();
        StereoBuffer full = Role(rig, await Build(rig, Song("  lanes lane1=hat\n")), "mix"), hatOnly = Role(rig, await Build(rig, Song("  lanes lane1=hat\n")), "lane1");
        Assert.NotEqual(hatOnly.Left[0], full.Left[0]);                  // the kick and the hat share the first step
        using EngineRig muted = await RigAsync(Sample.Replace("channel kick drum sample=samples/kick.wav vol=-3", "channel kick drum sample=samples/kick.wav vol=-3 mute=on", StringComparison.Ordinal));
        Assert.Equal(hatOnly.Left[0], Role(muted, await Build(muted, Song()), "mix").Left[0]);   // without the kick only the hat is left on the first step
        using EngineRig solo = await RigAsync(Sample.Replace("channel hat drum sample=samples/hat.wav pan=10", "channel hat drum sample=samples/hat.wav pan=10 solo=on", StringComparison.Ordinal));
        StereoBuffer only = Role(solo, await Build(solo, Song()), "mix");
        Assert.Equal(hatOnly.Left[0], only.Left[0]);
        Assert.Equal(hatOnly.Left.Skip(Bar).Take(Bar).ToArray(), only.Left.Skip(Bar).Take(Bar).ToArray());   // a soloed channel is the whole mix
    }

    [Fact]
    public async Task A_loop_plays_only_in_the_patterns_that_have_a_row_for_it()
    {
        string seq = "sequencer 1\nchannel bed loop file=samples/hat.wav bars=1\npattern on bars=1\n  bed on\npattern off bars=1\nplaylist\n  clip on track=1 at=1 for=1\n  clip off track=1 at=2 for=1\n";
        using EngineRig rig = await RigAsync(seq);
        StereoBuffer mix = Role(rig, await Build(rig, Song().Replace("for 4", "for 2", StringComparison.Ordinal)), "mix");
        Assert.Contains(mix.Left.Take(Bar), s => s != 0);
        Assert.All(mix.Left.Skip(Bar), s => Assert.Equal(0, s));
    }

    [Fact]
    public async Task Changing_a_sample_or_the_sequence_makes_a_new_take_and_nothing_else_does()
    {
        using EngineRig rig = await RigAsync();
        BuildReport first = await Build(rig, Song());
        Assert.Equal("rendered", first.Blocks.Single().Status);
        Assert.Equal("cached", (await Build(rig, Song())).Blocks.Single().Status);
        Wave(rig.ProjectRoot, "samples/kick.wav", Impulse(31000));
        Assert.Equal("rendered", (await Build(rig, Song())).Blocks.Single().Status);       // a sample is part of the plan
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Sample.Replace("x...x...|", "x...x..x|", StringComparison.Ordinal));
        Assert.Equal("rendered", (await Build(rig, Song())).Blocks.Single().Status);       // so is the sequence file
        BlockFiles.Of(rig.ProjectRoot, null, EngineRig.Parse(Song()), EngineRig.Parse(Song()).Blocks.Single()).ToList().ForEach(f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public async Task A_lane_that_is_connected_but_names_no_channel_stops_the_block_that_reads_it_and_the_mix_is_never_a_stand_in()
    {
        using EngineRig rig = await RigAsync();
        BuildReport report = await Build(rig, Song("  lanes lane1=kick\n", "volume v {\n  from sq.lane4 -> audio\n}\nvolume w {\n  from sq.lane1 -> audio\n}\n"));
        BlockResult stopped = report.Blocks.Single(b => b.Block == "v");
        Assert.Equal("skipped", stopped.Status);
        Assert.Contains("AMP5251", stopped.Message, StringComparison.Ordinal);
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "w").Status);
    }

    [Fact]
    public async Task A_wrong_lane_a_missing_pattern_a_missing_sample_and_a_missing_file_fail_the_block_with_AMP5380()
    {
        using EngineRig rig = await RigAsync();
        Assert.Contains("AMP5380", (await Build(rig, Song("  lanes lane1=nope\n"))).Blocks.Single().Message, StringComparison.Ordinal);
        Assert.Contains("AMP5380", (await Build(rig, Song().Replace("sequences/a.seq", "sequences/none.seq", StringComparison.Ordinal))).Blocks.Single().Message, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Sample.Replace("clip verse", "clip chorus", StringComparison.Ordinal));
        Assert.Contains("chorus", (await Build(rig, Song())).Blocks.Single().Message, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), Sample.Replace("samples/hat.wav", "samples/gone.wav", StringComparison.Ordinal));
        Assert.Contains("gone.wav", (await Build(rig, Song())).Blocks.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sequencer_does_not_follow_the_master_seed()
    {
        using EngineRig rig = await RigAsync();
        await Build(rig, Song());
        BuildReport again = await rig.Engine.BuildAsync(EngineRig.Parse(Song().Replace("master-seed 1", "master-seed 2", StringComparison.Ordinal)));
        Assert.Equal("cached", again.Blocks.Single().Status);
    }
}
