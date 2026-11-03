namespace Ample.Language.Tests;

/// <summary>AMP2049 compares a block with the length of the clip that feeds it: a clip that says <c>bars</c> is that long, whatever its last note is.</summary>
public sealed class ClipSpanTests
{
    private static IReadOnlyList<Diagnostic> Check(string clipBars, string synthFor)
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip c at 1 for " + clipBars + " {\n  notes 1:0:1:60:100 2:0:1:62:100\n  bars " + clipBars + "\n}\n\nsynth s at 1 for " + synthFor + " {\n  wave sine\n  from c.notes -> notes\n}\n\nmaster out {\n  from s.audio -> audio\n}\n";
        return Parser.Parse(song, Corpus.Spec).Diagnostics;
    }

    [Fact]
    public void A_block_as_long_as_the_bars_of_its_clip_is_not_reported_though_the_notes_end_earlier()
    {
        Assert.DoesNotContain(Check("4", "4"), d => d.Code == "AMP2049");
    }

    [Fact]
    public void A_block_of_another_length_than_the_bars_of_its_clip_is_still_reported()
    {
        Assert.Contains(Check("4", "3"), d => d.Code == "AMP2049");
    }
}
