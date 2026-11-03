using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;
using Ample.Core.Tests.Instruments;

namespace Ample.Core.Tests;

// Plan sequencer-next SQ01 to SQ04: guitar and gm channels, markers and the loop range, pattern variations.
[Trait("Category", "Sequencer")]
public sealed class SequencerNextTests
{
    private const string Base = "sequencer 1\nchannel kick drum sample=samples/kick.wav\nchannel hat drum sample=samples/hat.wav\npattern verse bars=2\n  kick x...x...x...x...|x...x...x...x...\n  hat x.x.x.x.x.x.x.x.\n";

    private static void Wave(string root, string rel, short[] samples, int rate = 44100)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Wav.Encode(samples, rate));
    }

    private static short[] Impulse(short value, int length = 200)
    {
        short[] s = new short[length];
        s[0] = value;
        return s;
    }

    private static async Task<EngineRig> RigAsync(string seq)
    {
        EngineRig rig = await EngineRig.CreateAsync();
        Wave(rig.ProjectRoot, "samples/kick.wav", Impulse(30000));
        Wave(rig.ProjectRoot, "samples/hat.wav", Impulse(20000));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), seq);
        return rig;
    }

    private static string Song(int bars = 4) => $"composition \"s\" {{ ample 1  tempo 120  master-seed 1 }}\nsequencer sq at 1 for {bars} {{\n  file sequences/a.seq\n}}\n";

    private static short[] Mix(EngineRig rig, BuildReport report)
    {
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "sq").TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role == "mix").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer.Left.ToArray();
    }

    [Fact]
    public void A_variation_starts_from_its_base_and_replaces_or_silences_rows()
    {
        SequenceFile seq = SequenceFile.Parse(Base + "pattern verse-b from=verse\n  hat x...x...x...x...\n  kick -\n");
        SeqPattern b = seq.Patterns[1];
        Assert.Equal("verse", b.From);
        Assert.Equal(2, b.Bars);   // inherited
        IReadOnlyDictionary<string, string> rows = seq.RowsOf(b);
        Assert.Equal("x...x...x...x...", rows["hat"]);
        Assert.False(rows.ContainsKey("kick"));   // silenced
        Assert.Equal(seq.Format(), SequenceFile.Parse(seq.Format()).Format());
        Assert.Contains("pattern verse-b bars=2 from=verse", seq.Format(), StringComparison.Ordinal);
        Assert.Equal("kick x...x...x...x...|x...x...x...x...", $"kick {seq.RowsOf(seq.Patterns[0])["kick"]}");
    }

    [Fact]
    public void A_variation_of_something_undefined_or_of_a_variation_is_AMP5380()
    {
        Assert.Equal("AMP5380", Assert.Throws<AmpleException>(() => SequenceFile.Parse(Base + "pattern x from=nope\n")).DiagnosticCode);
        Assert.Equal("AMP5380", Assert.Throws<AmpleException>(() => SequenceFile.Parse(Base + "pattern b from=verse\npattern c from=b\n")).DiagnosticCode);
    }

    [Fact]
    public void Markers_and_the_loop_range_are_read_and_written_and_the_loop_maps_bars()
    {
        SequenceFile seq = SequenceFile.Parse(Base + "playlist\n  clip verse track=1 at=1 for=4\n  marker Drop at=3\n  loop at=2 for=2\n");
        Assert.Equal([new SeqMarker("Drop", 3)], seq.Markers);
        Assert.Equal(new SeqLoop(2, 2), seq.Loop);
        Assert.Equal(1, seq.SourceBar(1));
        Assert.Equal(2, seq.SourceBar(2));
        Assert.Equal(3, seq.SourceBar(3));
        Assert.Equal(2, seq.SourceBar(4));
        Assert.Equal(3, seq.SourceBar(7));
        Assert.Equal(seq.Format(), SequenceFile.Parse(seq.Format()).Format());
        Assert.Equal("AMP5380", Assert.Throws<AmpleException>(() => SequenceFile.Parse(Base + "playlist\n  loop at=1 for=2\n  loop at=3 for=2\n")).DiagnosticCode);
    }

    [Fact]
    public async Task The_loop_repeats_its_bars_over_the_rest_of_the_block()
    {
        // bar 1 has a hit, bars 2 to 4 are empty; a loop of bar 1 puts the hit in every bar
        string seq = "sequencer 1\nchannel kick drum sample=samples/kick.wav\npattern one bars=1\n  kick x...............\nplaylist\n  clip one track=1 at=1 for=1\n  loop at=1 for=1\n";
        using EngineRig rig = await RigAsync(seq);
        short[] mix = Mix(rig, await rig.Engine.BuildAsync(EngineRig.Parse(Song())));
        const int bar = 88200;
        for (int b = 0; b < 4; b++) Assert.NotEqual(0, mix[b * bar]);
        // without the loop only the first bar has the hit
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), seq.Replace("  loop at=1 for=1\n", string.Empty, StringComparison.Ordinal));
        short[] plain = Mix(rig, await rig.Engine.BuildAsync(EngineRig.Parse(Song())));
        Assert.NotEqual(0, plain[0]);
        Assert.Equal(0, plain[bar]);
    }

    [Fact]
    public async Task A_variation_plays_the_changed_row_and_keeps_the_others()
    {
        string seq = Base + "pattern verse-b from=verse\n  kick -\nplaylist\n  clip verse-b track=1 at=1 for=2\n";
        using EngineRig rig = await RigAsync(seq);
        short[] changed = Mix(rig, await rig.Engine.BuildAsync(EngineRig.Parse(Song(2))));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), seq.Replace("clip verse-b", "clip verse", StringComparison.Ordinal));
        short[] plain = Mix(rig, await rig.Engine.BuildAsync(EngineRig.Parse(Song(2))));
        // the kick is silenced and the hat stays: the first sample is the hat alone, the plain pattern has both
        Assert.NotEqual(0, changed[0]);
        Assert.True(Math.Abs(changed[0]) < Math.Abs(plain[0]));
    }

    [Fact]
    public async Task A_guitar_channel_strums_a_chord_from_a_note_set_and_needs_chords_and_a_set()
    {
        using EngineRig rig = await RigAsync("sequencer 1\nchannel gtr guitar set=samples/gtr\npattern p bars=2\n  gtr D...D.u.D.u.D.u. chords Am F\nplaylist\n  clip p track=1 at=1 for=2\n");
        foreach (string note in new[] { "e2", "a2", "c3", "e3", "a3", "c4" })
            Wave(rig.ProjectRoot, $"samples/gtr/{note}.wav", [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 9.0)))]);
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(Song(2)));
        short[] mix = Mix(rig, report);
        Assert.Contains(mix, s => Math.Abs(s) > 500);

        // the same text gives the same bytes
        using EngineRig again = await RigAsync("sequencer 1\nchannel gtr guitar set=samples/gtr\npattern p bars=2\n  gtr D...D.u.D.u.D.u. chords Am F\nplaylist\n  clip p track=1 at=1 for=2\n");
        foreach (string note in new[] { "e2", "a2", "c3", "e3", "a3", "c4" })
            Wave(again.ProjectRoot, $"samples/gtr/{note}.wav", [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 9.0)))]);
        Assert.Equal(mix, Mix(again, await again.Engine.BuildAsync(EngineRig.Parse(Song(2)))));

        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), "sequencer 1\nchannel gtr guitar set=samples/gtr\npattern p bars=1\n  gtr D...D.u.D.u.D.u.\nplaylist\n  clip p track=1 at=1 for=1\n");
        BuildReport noChords = await rig.Engine.BuildAsync(EngineRig.Parse(Song(2)));
        Assert.Equal("failed", noChords.Blocks.Single(b => b.Block == "sq").Status);
        Assert.Contains("chords", noChords.Blocks.Single(b => b.Block == "sq").Message, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), "sequencer 1\nchannel gtr guitar\npattern p bars=1\n  gtr D...D.u.D.u.D.u. chords Am\nplaylist\n  clip p track=1 at=1 for=1\n");
        BuildReport noSet = await rig.Engine.BuildAsync(EngineRig.Parse(Song(2)));
        Assert.Equal("failed", noSet.Blocks.Single(b => b.Block == "sq").Status);
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), "sequencer 1\nchannel gtr guitar set=samples/gtr\npattern p bars=1\n  gtr D...D.u.D.u.D.u. chords H#\nplaylist\n  clip p track=1 at=1 for=1\n");
        BuildReport badChord = await rig.Engine.BuildAsync(EngineRig.Parse(Song(2)));
        Assert.Contains("not a chord name", badChord.Blocks.Single(b => b.Block == "sq").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gm_channel_plays_its_notes_through_a_soundfont_preset()
    {
        using EngineRig rig = await RigAsync("sequencer 1\nchannel keys gm file=tiny.sf2 program=5\npattern p bars=1\n  keys 1:0:1:60:96\nplaylist\n  clip p track=1 at=1 for=1\n");
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "tiny.sf2"), Sf2Fixture.Tiny());
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(Song(1)));
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "sq").Status);
        Assert.Contains(Mix(rig, report), s => s != 0);
    }

    [Fact]
    public async Task A_gm_channel_renders_its_drum_notes_with_the_drum_kit_and_the_others_with_its_own_program()
    {
        // 9 is the General MIDI drum channel written on the note (channel 10): those notes are hits of the drum kit, the plain note sounds with the channel's program
        const string song = "sequencer 1\nchannel keys gm file=tiny.sf2 program=5\npattern p bars=1\n  keys {0}\nplaylist\n  clip p track=1 at=1 for=1\n";
        using EngineRig plain = await RigAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, song, "1:0:1:60:96"));
        File.WriteAllBytes(Path.Combine(plain.ProjectRoot, "tiny.sf2"), Sf2Fixture.Tiny());
        BuildReport plainReport = await plain.Engine.BuildAsync(EngineRig.Parse(Song(1)));
        Assert.Equal("rendered", plainReport.Blocks.Single(b => b.Block == "sq").Status);

        using EngineRig mixed = await RigAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, song, "1:0:1:60:96 1:1:1:36:90:9:0:0"));
        File.WriteAllBytes(Path.Combine(mixed.ProjectRoot, "tiny.sf2"), Sf2Fixture.Tiny());
        BuildReport mixedReport = await mixed.Engine.BuildAsync(EngineRig.Parse(Song(1)));
        // the tiny soundfont has no drum kit: a drum note asks the kit (bank 128, program 0), which proves it did not sound with the channel's program, and says so
        Assert.Equal("failed", mixedReport.Blocks.Single(b => b.Block == "sq").Status);
        Assert.Contains("preset", mixedReport.Blocks.Single(b => b.Block == "sq").Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_gm_channel_without_its_soundfont_fails_with_the_soundfont_error()
    {
        using EngineRig rig = await RigAsync("sequencer 1\nchannel keys gm file=missing.sf2 program=4\npattern p bars=1\n  keys 1:0:1:60:96\nplaylist\n  clip p track=1 at=1 for=1\n");
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(Song(1)));
        BlockResult sq = report.Blocks.Single(b => b.Block == "sq");
        Assert.Equal("failed", sq.Status);
        Assert.Contains("SoundFont", sq.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_stay_above_the_line_they_were_written_for_and_survive_a_write()
    {
        const string text = "# the band\nsequencer 1\n# the kick\nchannel kick drum sample=samples/kick.wav\n# a verse\npattern verse bars=1\n  # every beat\n  kick x...x...x...x...\n# when\nplaylist\n  # first\n  clip verse track=1 at=1 for=4\n  # a flag\n  marker Drop at=3\n  # the loop\n  loop at=1 for=4\n# the end\n";
        SequenceFile seq = SequenceFile.Parse(text);
        Assert.Equal(["# the band"], seq.Intro);
        Assert.Equal(["# the kick"], seq.Channels[0].Notes);
        Assert.Equal(["# a verse"], seq.Patterns[0].Notes);
        Assert.Equal(["# every beat"], seq.Patterns[0].RowNotes!["kick"]);
        Assert.Equal(["# when"], seq.PlaylistNotes);
        Assert.Equal(["# first"], seq.Clips[0].Notes);
        Assert.Equal(["# a flag"], seq.Markers[0].Notes);
        Assert.Equal(["# the loop"], seq.Loop!.Notes);
        Assert.Equal(["# the end"], seq.Footer);
        Assert.Equal(text, seq.Format());
        Assert.Equal(text, SequenceFile.Parse(seq.Format()).Format());
        // blank lines and extra spaces are not kept, comments are
        Assert.Equal("# note\nsequencer 1\nchannel a drum sample=\"a b.wav\"\n", SequenceFile.Parse("# note\nsequencer 1\n\nchannel a   drum  sample=\"a b.wav\"\n").Format());
    }
}
