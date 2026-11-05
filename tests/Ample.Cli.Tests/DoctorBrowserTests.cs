namespace Ample.Cli.Tests;

public sealed class DoctorBrowserTests
{
    [Fact]
    public void Missing_koda_browser_is_a_note_with_the_install_command()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-doctor-browser-" + Guid.NewGuid().ToString("N"));

        (string level, string text, string fix) = AssistantCheck.Browser(home);

        Assert.Equal("note", level);
        Assert.Contains("not installed", text, StringComparison.Ordinal);
        Assert.Contains("ample packs install koda-browser", fix, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_koda_browser_folder_counts_as_not_installed()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-doctor-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "koda-browser"));
        try
        {
            Assert.Equal("note", AssistantCheck.Browser(home).Level);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void An_installed_koda_browser_is_ok_and_needs_no_fix()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-doctor-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "koda-browser"));
        File.WriteAllText(Path.Combine(home, "koda-browser", "marker.txt"), "x");
        try
        {
            (string level, string _, string fix) = AssistantCheck.Browser(home);

            Assert.Equal("ok", level);
            Assert.Equal(string.Empty, fix);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
