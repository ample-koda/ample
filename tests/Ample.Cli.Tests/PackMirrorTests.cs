namespace Ample.Cli.Tests;

public sealed class PackMirrorTests
{
    [Fact]
    public void Mirror_and_from_together_are_refused_before_anything_is_fetched()
    {
        using StringWriter output = new();

        int exitCode = CliApplication.Run(["packs", "install", "soundfont", "--mirror", "--from", "http://127.0.0.1:1/"], output);

        Assert.Equal(2, exitCode);
        Assert.Contains("either --mirror or --from", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Koda_browser_has_no_mirror()
    {
        using StringWriter output = new();

        int exitCode = CliApplication.Run(["packs", "install", "koda-browser", "--mirror"], output);

        Assert.Equal(2, exitCode);
        Assert.Contains("no AMPLE mirror of koda-browser", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mirror_address_is_the_get_site_and_ends_with_a_slash()
    {
        Assert.StartsWith("https://get.ample-koda.dev/", PackInstall.MirrorUrl, StringComparison.Ordinal);
        Assert.EndsWith("/", PackInstall.MirrorUrl, StringComparison.Ordinal);
    }
}
