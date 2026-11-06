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
        Assert.Matches("^AMPLE \\d+\\.\\d+\\.\\d+ \\(beta, commit [^)]+\\)$", version);   // any version: a release raises the number, the shape stays
        string shape = System.Text.RegularExpressions.Regex.Replace(version.Replace(CliVersionInfo.Current[26..^1], "<commit>", StringComparison.Ordinal), "^AMPLE \\d+\\.\\d+\\.\\d+", "AMPLE <version>");
        await GoldenFile.VerifyAsync(shape);
    }
}
