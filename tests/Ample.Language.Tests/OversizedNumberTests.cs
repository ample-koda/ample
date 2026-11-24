namespace Ample.Language.Tests;

/// <summary>
/// A number written in a song that does not fit an int or a long is a diagnostic or a bad note, never an exception that ends the parse
/// (security finding 47: <c>meter 99999999999999999999/4</c> and a layout line ended the CLI with an unhandled OverflowException).
/// </summary>
public sealed class OversizedNumberTests
{
    private const string Huge = "99999999999999999999";

    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec);

    [Theory]
    [InlineData(Huge + "/4")]
    [InlineData("4/" + Huge)]
    [InlineData("2147483648/4")]
    [InlineData("0/4")]
    public void A_meter_that_does_not_fit_is_not_allowed_and_does_not_throw(string meter)
    {
        AmpleDocument doc = Parse($"composition \"x\" {{ ample 1  tempo 100  meter {meter}  master-seed 1 }}\n");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP1120");
        Assert.Equal(4, doc.Project!.MeterNumerator);   // the default stays
    }

    [Theory]
    [InlineData(Huge, "1")]
    [InlineData("1", "-" + Huge)]
    [InlineData("2147483648", "0")]
    public void A_layout_position_that_does_not_fit_is_left_out_and_does_not_throw(string x, string y)
    {
        string text = $"composition \"x\" {{ ample 1  tempo 100  master-seed 1 }}\nmidi-clip a at 1 for 1 {{\n  notes \"1:1:1:60:100\"\n}}\nlayout a {x} {y}\n";
        AmpleDocument doc = Parse(text);
        Assert.DoesNotContain(doc.Layout.Keys, name => name == "a");
    }

    [Fact]
    public void A_layout_position_that_fits_is_kept()
    {
        string text = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\nmidi-clip a at 1 for 1 {\n  notes \"1:1:1:60:100\"\n}\nlayout a 40 -20\n";
        Assert.Equal((40, -20), Parse(text).Layout["a"]);
    }

    [Theory]
    [InlineData("1 + " + Huge)]
    [InlineData(Huge)]
    [InlineData("2 * (" + Huge + " - 1)")]
    public void A_control_expression_with_a_number_that_does_not_fit_gives_an_error_and_does_not_throw(string expression)
    {
        Assert.Null(ControlExpression.TryParse(expression, out string? error));
        Assert.Contains("too large", error);
    }

    [Fact]
    public void A_control_expression_with_a_number_that_fits_is_still_read()
    {
        Assert.NotNull(ControlExpression.TryParse("2 * 123456789012", out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(Huge + ":1:1:60:100")]
    [InlineData("1:1:1:" + Huge + ":100")]
    [InlineData("1:1:1:60:" + Huge)]
    [InlineData("1:" + Huge + ":1:60:100")]
    [InlineData("1:1:" + Huge + ":60:100")]
    public void A_note_with_a_number_that_does_not_fit_is_a_bad_note_and_does_not_throw(string entry)
    {
        List<PipeIssue> issues = [];
        List<PipeNote> notes = NotePipeline.ReadNotes(entry + " 1:1:1:62:100", issues);
        Assert.Single(issues);
        Assert.Equal("bad-notes", issues[0].Id);
        Assert.Single(notes);   // the entry after it is still read
        Assert.Equal(62, notes[0].Pitch);
    }
}
