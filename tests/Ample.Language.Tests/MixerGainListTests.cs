namespace Ample.Language.Tests;

/// <summary>AMP2098: a list of levels (the gains of a mixer or a bus) is in whole decibels; the check says what the build does with an entry that is not.</summary>
public sealed class MixerGainListTests
{
    private static Diagnostic[] Warnings(string gains, string type = "mixer")
    {
        string song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n" +
            "click a at 1 for 1 {\n  per_beat 1\n}\nclick b at 1 for 1 {\n  per_beat 2\n}\n" +
            type + " m {\n  inputs 2\n  gains " + gains + "\n  from a.audio -> audio1\n  from b.audio -> audio2\n}\n";
        return [.. Parser.Parse(song, Corpus.Spec).Diagnostics.Where(d => d.Code == "AMP2098")];
    }

    [Fact]
    public void A_decimal_gain_is_a_warning_that_names_the_rounded_value()
    {
        Diagnostic[] found = Warnings("0 -4.5");
        Diagnostic one = Assert.Single(found);
        Assert.Equal(Severity.Warning, one.Severity);
        Assert.Contains("-5 dB", one.Message, StringComparison.Ordinal);
        Assert.Single(Warnings("-12.041 0", "bus"));
    }

    [Fact]
    public void An_entry_that_is_not_a_number_is_a_warning_and_whole_numbers_are_quiet()
    {
        Diagnostic one = Assert.Single(Warnings("0 abc"));
        Assert.Contains("0 dB", one.Message, StringComparison.Ordinal);
        Assert.Empty(Warnings("0 -6"));
        Assert.Empty(Warnings("3 +2"));
        Assert.Empty(Warnings("0 0"));
    }
}
