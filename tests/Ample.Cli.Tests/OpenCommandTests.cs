namespace Ample.Cli.Tests;

public sealed class OpenCommandTests
{
    [Fact]
    public void No_target_lists_recents_from_the_selected_home()
    {
        string home = Path.Combine(Path.GetTempPath(), "ample-open-home-" + Guid.NewGuid().ToString("N"));
        using StringWriter output = new();
        try
        {
            int result = CliApplication.Run(["open", "--home", home], output);

            Assert.Equal(0, result);
            Assert.Contains("No recent AMPLE projects.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }

    [Fact]
    public void Agent_requires_a_reason_before_starting()
    {
        using StringWriter output = new();

        int result = CliApplication.Run(["open", "--agent", "Codex", "--home", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], output);

        Assert.Equal(2, result);
        Assert.Contains("--reason is required", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_name_cannot_be_blank()
    {
        using StringWriter output = new();

        int result = CliApplication.Run(["open", ".", "--agent", " ", "--reason", "Some reason"], output);

        Assert.Equal(2, result);
        Assert.Contains("non-empty agent name", output.ToString(), StringComparison.Ordinal);
    }
}
