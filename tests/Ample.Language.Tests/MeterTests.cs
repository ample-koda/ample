namespace Ample.Language.Tests;

/// <summary>The song-level meter: <c>composition "x" { tempo 100  meter 3/4  master-seed 1 }</c>. Without it a song is in 4/4 and nothing changes.</summary>
public class MeterTests
{
    private static AmpleDocument Parse(string line) => Parser.Parse(line + "\n", Corpus.Spec);

    [Theory]
    [InlineData("composition \"x\" { ample 1  tempo 100  master-seed 1 }", 4, 4, 4.0)]
    [InlineData("composition \"x\" { ample 1  tempo 100  meter 4/4  master-seed 1 }", 4, 4, 4.0)]
    [InlineData("composition \"x\" { ample 1  tempo 100  meter 3/4  master-seed 1 }", 3, 4, 3.0)]
    [InlineData("composition \"x\" { ample 1  tempo 100  meter 6/8  master-seed 1 }", 6, 8, 3.0)]
    [InlineData("composition \"x\" { ample 1  tempo 100  meter 5/4  master-seed 1 }", 5, 4, 5.0)]
    [InlineData("composition \"x\" { ample 1  tempo 100  meter 7/8  master-seed 1 }", 7, 8, 3.5)]
    public void The_meter_gives_the_beats_of_a_bar(string line, int numerator, int denominator, double beats)
    {
        AmpleDocument doc = Parse(line);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal((numerator, denominator), (doc.Project!.MeterNumerator, doc.Project.MeterDenominator));
        Assert.Equal(beats, doc.Project.BeatsPerBar);
    }

    [Theory]
    [InlineData("3/5")]
    [InlineData("0/4")]
    [InlineData("17/4")]
    public void A_bad_meter_is_an_error_and_the_song_stays_in_4_4(string meter)
    {
        AmpleDocument doc = Parse($"composition \"x\" {{ ample 1  tempo 100  meter {meter}  master-seed 1 }}");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP1120" && d.Severity == Severity.Error);
        Assert.Equal(4.0, doc.Project!.BeatsPerBar);
    }

    [Fact]
    public void The_formatter_keeps_the_meter()
    {
        string once = Formatter.Format("composition \"x\" { ample 1 tempo 100 meter 3/4 master-seed 1 }\n", Corpus.Spec);
        Assert.Contains("meter 3/4", once, StringComparison.Ordinal);
        Assert.Equal(once, Formatter.Format(once, Corpus.Spec));
    }

    [Fact]
    public void A_bar_of_the_note_language_is_the_beats_of_the_meter()
    {
        const string expression = "1:0:1:60:100 |> shift(1bar)";
        PipeResult four = NotePipeline.Evaluate(expression, new NotePipeline.Context());
        PipeResult three = NotePipeline.Evaluate(expression, new NotePipeline.Context { BeatsPerBar = 3m });
        PipeResult seven = NotePipeline.Evaluate(expression, new NotePipeline.Context { BeatsPerBar = 3.5m });
        Assert.Equal(4 * NotePipeline.Beat, four.Value!.Events.Single().At);
        Assert.Equal(3 * NotePipeline.Beat, three.Value!.Events.Single().At);
        Assert.Equal((long)(3.5 * NotePipeline.Beat), seven.Value!.Events.Single().At);
        Assert.Equal(4 * NotePipeline.Beat, NotePipeline.Bar);   // the scope ended: the next evaluation is in 4/4 again
    }

    [Theory]
    [InlineData("4/4", false)]
    [InlineData("3/4", false)]
    [InlineData("6/8", false)]
    [InlineData("5/4", true)]
    [InlineData("7/8", false)]   // 3.5 beats: beat 4 is outside
    public void A_structured_note_on_beat_four_fits_only_in_a_bar_that_has_five_beats(string meter, bool fits)
    {
        AmpleDocument doc = Parse($"composition \"x\" {{ ample 1  tempo 100  meter {meter}  master-seed 1 }}\nmidi-clip c at 1 for 1 {{\n  notes 1:3:0.5:60:100 1:4:0.25:84:60\n}}");
        Assert.Equal(!fits, doc.Diagnostics.Any(d => d.Code == "AMP2012"));
    }

    [Fact]
    public void A_note_that_ends_exactly_at_the_end_of_a_seven_eight_bar_fits_and_one_that_crosses_it_does_not()
    {
        const string head = "composition \"x\" { ample 1  tempo 100  meter 7/8  master-seed 1 }\nmidi-clip c at 1 for 1 {\n  notes ";
        Assert.DoesNotContain(Parse(head + "1:3:0.5:60:100\n}").Diagnostics, d => d.Code == "AMP2012");   // 3 + 0.5 = 3.5 beats: exactly the bar
        Assert.Contains(Parse(head + "1:3:0.75:60:100\n}").Diagnostics, d => d.Code == "AMP2012");
    }
}
