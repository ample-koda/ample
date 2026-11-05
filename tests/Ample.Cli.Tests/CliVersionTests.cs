using Ample.Golden;

namespace Ample.Cli.Tests;

public sealed class CliVersionTests
{
    [Fact]
    public async Task Version_is_reported()
    {
        using StringWriter output = new();

        int exitCode = CliApplication.Run(["--version"], output);
        string version = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        Assert.Equal(0, exitCode);
        Assert.Matches("^AMPLE 0\\.1\\.0 \\(beta, commit [^)]+\\)$", version);
        await GoldenFile.VerifyAsync(version.Replace(CliVersionInfo.Current[26..^1], "<commit>", StringComparison.Ordinal));
    }
}
