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

    private const string Logged = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\nsettings { logging.level debug }\n\n"
        + "helper shown(Notes n) -> Notes {\n  debug \"checking\", code=\"CHK\", context={count: 2}\n  warn \"careful\"\n  return n\n}\n\n"
        + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> shown()\n}\n";

    [Fact]
    public void Check_prints_the_log_view_only_when_asked()
    {
        string path = Write("logged.amplet", Logged);
        (_, string plain) = Run("check", path);
        Assert.DoesNotContain("checking", plain, StringComparison.Ordinal);
        Assert.Contains("AMP6001", plain, StringComparison.Ordinal);   // a warning is a diagnostic either way
        (int code, string text) = Run("check", path, "--log");
        Assert.Equal(0, code);
        Assert.Contains("debug checking [CHK] {\"count\":2}", text, StringComparison.Ordinal);
        Assert.Contains("warning careful", text, StringComparison.Ordinal);
        Assert.Contains("2 log event(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void With_json_the_log_view_is_one_object_per_line_after_the_diagnostics()
    {
        (_, string text) = Run("check", Write("logged.amplet", Logged), "--log", "--format", "json");
        string[] lines = [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("{\"log\"", StringComparison.Ordinal))];
        Assert.Equal(2, lines.Length);
        using System.Text.Json.JsonDocument first = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Equal("debug", first.RootElement.GetProperty("log").GetString());
        Assert.Equal("CHK", first.RootElement.GetProperty("code").GetString());
        Assert.Equal("checking", first.RootElement.GetProperty("message").GetString());   // one JSON string, not encoded twice
        Assert.Equal(2, first.RootElement.GetProperty("context").GetProperty("count").GetInt32());   // the context is JSON itself
    }

    [Fact]
    public void A_log_level_of_the_run_wins_over_the_song_and_is_validated()
    {
        string path = Write("logged.amplet", Logged);
        (_, string quiet) = Run("check", path, "--log-level", "warning");
        Assert.DoesNotContain("debug checking", quiet, StringComparison.Ordinal);   // the song says debug, the run says warning
        Assert.Contains("warning careful", quiet, StringComparison.Ordinal);
        (int code, string bad) = Run("check", path, "--log-level", "loud");
        Assert.Equal(2, code);
        Assert.Contains("logging.level", bad, StringComparison.Ordinal);
    }

    private const string Asserted = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\n"
        + "helper checked(Notes n) -> Notes {\n  assert 1 > 2, \"never true\"\n  return n\n}\n\n"
        + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> checked()\n}\n";

    [Fact]
    public void Assertions_are_off_unless_the_run_switches_them_on()
    {
        string path = Write("asserted.amplet", Asserted);
        (int off, string plain) = Run("check", path);
        Assert.Equal(0, off);
        Assert.DoesNotContain("AMP6004", plain, StringComparison.Ordinal);
        (int on, string failed) = Run("check", path, "--assertions", "on");
        Assert.Equal(1, on);
        Assert.Contains("AMP6004", failed, StringComparison.Ordinal);
        Assert.Contains("never true", failed, StringComparison.Ordinal);
    }
}
