namespace Ample.Cli.Tests;

public sealed class CheckCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string SpecFolder
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "spec")) && Directory.Exists(Path.Combine(dir.FullName, "docs")))
                {
                    return dir.FullName;
                }
            }

            throw new DirectoryNotFoundException("AMPLE folder not found");
        }
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run([.. args, "--root", SpecFolder], output);
        return (code, output.ToString());
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string UpComponent = """
        component koda.notes.up version 1.0.0 current {
          id "demo.koda.test.up"
          input source: Notes
          output notes: Notes
          process {
            emit source |> transpose(12)
          }
        }
        """;

    [Fact]
    public void Check_reads_a_component_file_as_a_component_file_without_a_project_line()
    {
        string good = Write("up.amplec", UpComponent);
        (int code, string text) = Run("check", good);
        Assert.Equal(0, code);
        Assert.DoesNotContain("AMP1001", text, StringComparison.Ordinal);
        string bad = Write("bad.amplec", UpComponent.Replace("transpose(12)", "transpos(12)", StringComparison.Ordinal));
        Assert.Equal(1, Run("check", bad).Code);
    }

    [Fact]
    public void Check_succeeds_on_a_valid_song_and_fails_with_position_and_hint_on_a_broken_one()
    {
        string good = Write("good.amplet", "composition \"x\" { ample 1  tempo 90  master-seed 1 }\n");
        Assert.Equal(0, Run("check", good).Code);

        string bad = Write("bad.amplet", "composition \"x\" { ample 1  tempo 90  master-seed 1 }\ndrumz d {\n}\n");
        (int code, string text) = Run("check", bad);
        Assert.Equal(1, code);
        Assert.Contains("bad.amplet:2:1: error AMP2001", text, StringComparison.Ordinal);
        Assert.Contains("hint: Did you mean \"drums\"?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_can_print_json()
    {
        string bad = Write("bad.amplet", "composition \"x\" { ample 1  tempo 999  master-seed 1 }\n");
        (int code, string text) = Run("check", bad, "--format", "json");
        Assert.Equal(1, code);
        Assert.Contains("\"code\": \"AMP1002\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_shows_only_lint_rules()
    {
        string path = Write("lint.amplet", "composition \"x\" { ample 1  tempo 90  master-seed 1 }\ndrums d {\n}\ndrumz e {\n}\n");
        (int code, string text) = Run("lint", path);
        Assert.Contains("L001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AMP2001", text, StringComparison.Ordinal);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Fmt_rewrites_and_check_reports()
    {
        string path = Write("fmt.amplet", "composition   \"x\"{ample 1  tempo 90 master-seed 1}\n\n\n");
        Assert.Equal(1, Run("fmt", path, "--check").Code);
        Assert.Equal(0, Run("fmt", path).Code);
        Assert.Equal("composition \"x\" { ample 1  tempo 90  master-seed 1 }\n", File.ReadAllText(path));
        Assert.Equal(0, Run("fmt", path, "--check").Code);
    }

    [Fact]
    public void A_missing_file_is_reported()
    {
        Assert.Equal(2, Run("check", Path.Combine(folder, "nope.amplet")).Code);
    }
}
