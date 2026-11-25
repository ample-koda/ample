namespace Ample.Language.Tests;

/// <summary>
/// AMP2098 for the effect levels that are used in whole decibels (Studio bug hunt 2026-11-24, bug 23): a knob showed -3 dB, the song said -2.8 dB and the build
/// cut it to -2 dB without a word. The build now rounds, and the check says what it plays.
/// </summary>
public sealed class WholeDecibelsTests
{
    private static Diagnostic[] Warnings(string block, string line)
    {
        string song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n" +
            "click a at 1 for 1 {\n  per_beat 1\n}\n" +
            block + " fx {\n  " + line + "\n  from a.audio -> in\n}\n";
        return [.. Parser.Parse(song, Corpus.Spec).Diagnostics.Where(d => d.Code == "AMP2098")];
    }

    [Theory]
    [InlineData("eq", "low -2.8 dB", "-3 dB")]
    [InlineData("eq", "mid 0.5 dB", "1 dB")]
    [InlineData("compressor", "threshold -18.5 dB", "-19 dB")]
    [InlineData("limiter", "ceiling -0.7 dB", "-1 dB")]
    [InlineData("gate", "threshold -50.4", "-50 dB")]
    [InlineData("deesser", "threshold -30.5 dB", "-31 dB")]
    public void A_decimal_level_is_a_warning_that_names_the_rounded_value(string block, string line, string said)
    {
        Diagnostic one = Assert.Single(Warnings(block, line));
        Assert.Equal(Severity.Warning, one.Severity);
        Assert.Contains(said, one.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eq", "low -3 dB")]
    [InlineData("compressor", "threshold -18 dB")]
    [InlineData("compressor", "ratio 4")]
    [InlineData("limiter", "ceiling -1 dB")]
    public void A_whole_level_and_other_parameters_are_quiet(string block, string line)
    {
        Assert.Empty(Warnings(block, line));
    }

    [Fact]
    public void The_table_names_the_levels_and_nothing_else()
    {
        Assert.True(WholeDecibels.Is("eq", "mid"));
        Assert.True(WholeDecibels.Is("duck", "depth"));
        Assert.True(WholeDecibels.Is("master", "limiter"));
        Assert.False(WholeDecibels.Is("volume", "gain"));
        Assert.False(WholeDecibels.Is("eq", "low_hz"));
        Assert.False(WholeDecibels.Is("peq", "band1_gain"));
    }
}
