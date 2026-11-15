namespace Ample.Setup.Tests;

public class ExtrasResultTests
{
    [Fact]
    public void A_line_reads_back_as_the_extra_its_state_and_its_reason()
    {
        string text = ExtrasResult.Line("musescore-general", "failed", "no network\tat all\r\n") + "\n" + ExtrasResult.Line("koda-browser", "installed", string.Empty) + "\n";

        List<ExtrasResult.Entry> entries = ExtrasResult.Parse(text);

        Assert.Equal(["musescore-general", "koda-browser"], entries.Select(e => e.Pack));
        Assert.Equal(["failed", "installed"], entries.Select(e => e.State));
        Assert.Equal("no network at all", entries[0].Message);
    }

    [Fact]
    public void Lines_that_are_not_results_are_left_out()
    {
        Assert.Empty(ExtrasResult.Parse(null!));
        Assert.Empty(ExtrasResult.Parse("garbage\n\tfailed\nx\tmaybe\tsoon\n"));
    }

    [Fact]
    public void A_failed_extra_that_is_still_missing_is_named_with_its_reason_and_how_to_try_again()
    {
        List<ExtrasResult.Entry> entries = ExtrasResult.Parse("musescore-general\tfailed\tcould not download\naudiveris\tinstalled\t\nkoda-browser\tskipped\tcancelled\n");

        string text = ExtrasResult.Describe(entries, pack => false)!;

        Assert.Contains("musescore-general (could not download)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("audiveris", text, StringComparison.Ordinal);
        Assert.DoesNotContain("koda-browser", text, StringComparison.Ordinal);
        Assert.Contains("try again", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_said_when_nothing_failed_or_when_the_extra_was_fetched_since()
    {
        List<ExtrasResult.Entry> entries = ExtrasResult.Parse("musescore-general\tfailed\tcould not download\n");

        Assert.Null(ExtrasResult.Describe(entries, pack => pack == "musescore-general"));
        Assert.Null(ExtrasResult.Describe(ExtrasResult.Parse("a\tinstalled\t\n"), pack => false));
        Assert.Null(ExtrasResult.Describe(null!, pack => false));
    }

    [Fact]
    public void The_file_is_in_the_ample_folder_of_the_local_application_data()
    {
        Assert.EndsWith(Path.Combine("Ample", "install-extras.txt"), ExtrasResult.PathIn(Path.Combine("C:", "Users", "x", "AppData", "Local")), StringComparison.Ordinal);
    }
}
