namespace Ample.Cli.Tests;

public sealed class HistoryCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-hist-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void A_session_like_the_design_works_end_to_end()
    {
        string p = Path.Combine(folder, "song");
        Assert.Equal(0, Run("new", p).Code);
        File.WriteAllText(Path.Combine(p, "song.amplet"), "v1");
        Assert.Equal(0, Run("checkpoint", "-m", "first", "--project", p).Code);
        Assert.Contains("Working on: garage", Run("history", "--project", p).Output, StringComparison.Ordinal);

        (int code, string text) = Run("fork", "idea", "--project", p);
        Assert.Equal(0, code);
        Assert.Contains("Line idea created", text, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(p, "song.amplet"), "v2");
        (code, text) = Run("go", "studio", "--project", p);
        Assert.Equal(0, code);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(p, "song.amplet")));

        (code, text) = Run("peek", "garage", "--file", "song.amplet", "--project", p);
        Assert.Equal("v2", text.Trim());

        (code, text) = Run("promote", "--project", p);
        Assert.Equal(0, code);
        Assert.Contains("1 change(s)", text, StringComparison.Ordinal);
        Assert.Contains("Promoted", Run("promote", "--yes", "--project", p).Output, StringComparison.Ordinal);

        Assert.Equal(0, Run("tag", "v1", "--reason", "release", "--project", p).Code);
        Assert.Equal(1, Run("tag", "v1", "--reason", "again", "--project", p).Code);

        (code, text) = Run("drop", "idea", "--project", p);
        Assert.Equal(0, code);
        Assert.Contains("Dry run only", text, StringComparison.Ordinal);
        (code, text) = Run("drop", "idea", "--accept", "drop idea", "--reason", "not needed", "--project", p);
        Assert.Equal(0, code);
        Assert.Contains("tombstone", text, StringComparison.Ordinal);
        Assert.Equal(1, Run("drop", "studio", "--project", p).Code);
        Assert.Contains("Dry run only", Run("cleanup", "--project", p).Output, StringComparison.Ordinal);
    }
}
