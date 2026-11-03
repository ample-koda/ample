namespace Ample.Language.Tests;

public sealed class TintTests
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120 }\nmidi-clip chords {\n  chords C F\n  bars 2\n  tint purple\n}\n";

    private static Diagnostic[] Errors(string text) => Parser.Parse(text, Corpus.Spec, ParseOptions.Editor).Diagnostics.Where(d => d.Code is "AMP2003" or "AMP2022").ToArray();

    [Fact]
    public void Any_block_can_have_a_known_tint()
    {
        Assert.Empty(Errors(Song));
    }

    [Fact]
    public void An_unknown_tint_is_an_error_and_not_an_unknown_parameter()
    {
        Diagnostic d = Assert.Single(Errors(Song.Replace("purple", "mauve", StringComparison.Ordinal)));
        Assert.Equal("AMP2022", d.Code);
        Assert.Equal(5, d.Line);
    }
}
