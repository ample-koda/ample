namespace Ample.Cli.Tests;

public sealed class ControlCommandTests : IDisposable
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\ndrums drums at 1 for 2 {\n  pattern beat-A\n}\nmixer mix {\n  from drums.audio -> audio1\n}\nmaster master {\n  from mix.audio -> audio\n}\n";
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-control-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Seeds_variants_choose_regen_and_explain_work_from_the_command_line()
    {
        string project = Path.Combine(folder, "p");
        Assert.Equal(0, Run("new", project).Code);
        string song = Path.Combine(project, "song.amplet");
        await File.WriteAllTextAsync(song, Song);

        (int code, string text) = Run("seed", song, "drums");
        Assert.Equal(0, code);
        Assert.Contains("locked", text, StringComparison.Ordinal);
        (code, text) = Run("seed", song, "drums", "--set", "5");
        Assert.Equal(1, code);
        Assert.Contains("AMP5260", text, StringComparison.Ordinal);
        (code, text) = Run("seed", song, "drums", "--unlock", "--set", "5", "--lock", "--reason", "try");
        Assert.Equal(0, code);
        Assert.Contains("5 (UserSet, locked)", text, StringComparison.Ordinal);
        Assert.Contains("try", text, StringComparison.Ordinal);

        (code, text) = Run("variants", song, "drums", "--count", "2");
        Assert.Equal(0, code);
        string takeId = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Equal(0, Run("choose", song, "drums", takeId[..14]).Code);

        (code, text) = Run("explain", song, "drums");
        Assert.Contains("freeze level Free", text, StringComparison.Ordinal);

        (code, text) = Run("regen", song, "drums");
        Assert.Equal(0, code);
        Assert.Contains("only guaranteed", text, StringComparison.Ordinal);
        Assert.Contains("restore the previous take", text, StringComparison.Ordinal);
        string id = text[(text.IndexOf("--decide ", StringComparison.Ordinal) + 9)..].Split(' ')[0];
        (code, text) = Run("regen", song, "drums", "--decide", id);
        Assert.Equal(0, code);
        Assert.Contains("Restored", text, StringComparison.Ordinal);

        Assert.Equal(0, Run("verify-reproduction", song, "drums").Code);
    }
}
