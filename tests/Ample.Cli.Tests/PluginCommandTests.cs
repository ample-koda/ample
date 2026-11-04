namespace Ample.Cli.Tests;

public sealed class PluginCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-plugins-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void List_describe_validate_and_run_work()
    {
        (int code, string text) = Run("plugins", "list");
        Assert.Equal(0, code);
        Assert.Contains("echo 1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("flaky 1.0.0", text, StringComparison.Ordinal);

        (code, text) = Run("plugins", "describe", "echo");
        Assert.Equal(0, code);
        Assert.Contains("determinism exact", text, StringComparison.Ordinal);
        Assert.Contains("pitchOffset", text, StringComparison.Ordinal);

        string good = Path.Combine(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(good, "plugins", "echo")))
        {
            good = Path.GetDirectoryName(good)!;
        }

        Assert.Equal(0, Run("plugins", "validate", Path.Combine(good, "plugins", "echo")).Code);
        (code, text) = Run("plugins", "run", "echo");
        Assert.Equal(0, code);
        Assert.Contains("handshake ok", text, StringComparison.Ordinal);
        Assert.Equal(1, Run("plugins", "describe", "nothing").Code);
    }

    [Fact]
    public void A_broken_manifest_is_reported_by_validate_and_by_list()
    {
        string bad = Path.Combine(folder, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "plugin.json"), "{}");
        (int code, string text) = Run("plugins", "validate", bad);
        Assert.Equal(1, code);
        Assert.Contains("AMP5270: id must be", text, StringComparison.Ordinal);
        (code, text) = Run("plugins", "list", "--folder", folder);
        Assert.Equal(1, code);
        Assert.Contains("problem:", text, StringComparison.Ordinal);
    }
}
