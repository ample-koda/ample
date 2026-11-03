using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A row of a sequencer pattern can be a formula (= a note pipeline), and its notes are the same as the plain row that says the same.</summary>
public sealed class SequenceRowsTests
{
    private const string Sequence = "sequencer 1\nchannel bass gm file=pack:musescore-general program=32 bank=0 vol=0 pan=0\npattern a bars=2\n  bass = 1:0:0.5:60:100 1:2:0.5:62:90 |> bars(1..=1) |> repeat(2) |> set(pitch=\"38 43 45 38\")\npattern b bars=2\n  bass 1:0:0.5:38:100 1:2:0.5:43:90 2:0:0.5:45:100 2:2:0.5:38:90\nplaylist\n  clip a track=1 at=1 for=2\n  clip b track=1 at=3 for=2\n";

    [Fact]
    public void A_formula_row_gives_the_same_notes_as_the_plain_row_that_says_the_same()
    {
        SequenceFile seq = SequenceFile.Parse(Sequence);
        Assert.True(SequenceRows.TryChannelNotes(seq, "bass", "100", out List<SemNote> all, out string error), error);
        Assert.Equal(8, all.Count);
        List<SemNote> first = [.. all.Where(n => n.Start < 8_000_000)], second = [.. all.Where(n => n.Start >= 8_000_000).Select(n => n with { Start = n.Start - 8_000_000 })];
        NoteComparison same = NoteSemantics.Compare(first, second);
        Assert.True(same.Equal, string.Join(" | ", same.Differences));
    }

    [Fact]
    public void A_formula_with_an_error_says_so_with_the_pattern_it_is_in()
    {
        SequenceFile seq = SequenceFile.Parse(Sequence.Replace("|> repeat(2)", "|> nonsense(2)", StringComparison.Ordinal));
        Assert.False(SequenceRows.TryChannelNotes(seq, "bass", "100", out _, out string error));
        Assert.Contains("pattern 'a'", error, StringComparison.Ordinal);
        Assert.Contains("nonsense", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_with_a_formula_row_is_written_back_unchanged()
    {
        Assert.Equal(Sequence, SequenceFile.Parse(Sequence).Format());
    }
}
