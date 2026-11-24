namespace Ample.Language.Tests;

/// <summary>
/// The settings of the composition line come in any order (owner, 2026-11-24): "meter 3/4" written after the master-seed made the line unreadable (AMP1003),
/// and a build fell back to the default tempo. Each setting may be given once; tempo and master-seed are required.
/// </summary>
public sealed class CompositionLineOrderTests
{
    private static AmpleDocument Parse(string line) => Parser.Parse(line + "\nclick a at 1 for 1 {\n  per_beat 1\n}\n", Corpus.Spec);

    [Theory]
    [InlineData("composition \"x\" { ample 1  tempo 120  meter 3/4  master-seed 7 }")]
    [InlineData("composition \"x\" { ample 1  tempo 120  master-seed 7  meter 3/4 }")]
    [InlineData("composition \"x\" { master-seed 7  meter 3/4  tempo 120  ample 1 }")]
    [InlineData("composition \"x\" {meter 3/4 tempo 120 ample 1 master-seed 7}")]
    public void Every_order_gives_the_same_song(string line)
    {
        AmpleDocument doc = Parse(line);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "AMP1003" or "AMP1001");
        Assert.NotNull(doc.Project);
        Assert.Equal(120, doc.Project!.Tempo);
        Assert.Equal(7UL, doc.Project.MasterSeed);
        Assert.Equal(3.0, doc.Project.BeatsPerBar);
    }

    [Theory]
    [InlineData("composition \"x\" { tempo 120  tempo 90  master-seed 7 }")]
    [InlineData("composition \"x\" { tempo 120 }")]
    [InlineData("composition \"x\" { master-seed 7 }")]
    [InlineData("composition \"x\" { tempo 120  master-seed 7  swing 50 }")]
    [InlineData("composition \"x\" { tempo 120master-seed 7 }")]
    public void A_setting_twice_missing_or_unknown_is_still_a_malformed_line(string line)
    {
        Assert.Contains(Parse(line).Diagnostics, d => d.Code == "AMP1003");
    }

    [Fact]
    public void The_formatter_writes_the_usual_order()
    {
        string formatted = Formatter.Format("composition \"x\" { master-seed 7  meter 3/4  tempo 120  ample 1 }\n", Corpus.Spec);
        Assert.StartsWith("composition \"x\" { ample 1  tempo 120  meter 3/4  master-seed 7 }", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_ample_version_in_another_place_is_marked_where_it_is()
    {
        string line = "composition \"x\" { tempo 120  master-seed 7  ample 99 }";
        Diagnostic version = Assert.Single(Parse(line).Diagnostics, d => d.Code == "AMP1119");
        Assert.Equal(line.IndexOf("99", StringComparison.Ordinal) + 1, version.Col);
    }
}
