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
        string shape = System.Text.RegularExpressions.Regex.Replace(version, "^AMPLE \\d+\\.\\d+\\.\\d+ \\(beta, commit [^)]+\\)$", "AMPLE <version> (beta, commit <commit>)");   // the number of digits of the version and of the commit may change
        await GoldenFile.VerifyAsync(shape);
    }
}
