namespace Ample.Language.Tests;

/// <summary>AMP2095: the steps of a drum grid fill whole bars.</summary>
public sealed class DrumGridTests
{
    private static IReadOnlyList<Diagnostic> Check(string pattern, string steps = "")
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\ndrums beat at 1 for 4 {\n  kit builtin\n  pattern " + pattern + "\n" + (steps.Length > 0 ? "  steps " + steps + "\n" : string.Empty) + "}\n\nmaster out {\n  from beat.audio -> audio\n}\n";
        return Parser.Parse(song, Corpus.Spec).Diagnostics;
    }

    [Theory]
    [InlineData("kick:x...")]                                        // 4 steps repeat four times in a bar
    [InlineData("kick:x.......")]                                    // 8
    [InlineData("kick:x...............")]                            // 16: one bar
    [InlineData("kick:x............... snare:........x.......")]    // 16 and 16
    [InlineData("kick:x...............................")]            // 32: two bars
    [InlineData("kick:x...x...x...x...@l-3,p-20")]                   // the options of a voice are not steps
    public void A_grid_that_fits_the_bar_is_not_reported(string pattern)
    {
        Assert.DoesNotContain(Check(pattern), d => d.Code == "AMP2095");
    }

    [Theory]
    [InlineData("kick:x...x...x...x...x...")]                        // 20 steps
    [InlineData("kick:x.......x....... snare:....x...x")]            // the second voice has 9
    public void A_grid_that_does_not_fit_the_bar_is_reported(string pattern)
    {
        Assert.Contains(Check(pattern), d => d.Code == "AMP2095" && d.Severity == Severity.Warning);
    }

    [Fact]
    public void A_pattern_name_has_no_grid_to_check()
    {
        Assert.DoesNotContain(Check("beat-A"), d => d.Code == "AMP2095");
    }

    [Theory]
    [InlineData("kick:x.......", "8")]                  // 8 steps in a bar of 8
    [InlineData("kick:x...............", "8")]          // 16 steps are two bars of 8
    [InlineData("kick:x...x...", "32")]                 // 8 steps repeat four times in a bar of 32
    public void A_grid_is_checked_against_the_steps_of_the_block(string pattern, string steps)
    {
        Assert.DoesNotContain(Check(pattern, steps), d => d.Code == "AMP2095");
    }

    [Theory]
    [InlineData("kick:x...x...x...x...", "12")]         // 16 steps do not fill bars of 12
    [InlineData("kick:x.......", "5")]                  // 8 steps do not divide a bar of 5
    public void A_grid_that_does_not_fit_the_steps_of_the_block_is_reported(string pattern, string steps)
    {
        Assert.Contains(Check(pattern, steps), d => d.Code == "AMP2095" && d.Severity == Severity.Warning);
    }
}
