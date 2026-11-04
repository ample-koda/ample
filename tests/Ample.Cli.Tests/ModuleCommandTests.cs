namespace Ample.Cli.Tests;

[Trait("Category", "Modules")]
public sealed class ModuleCommandTests : IDisposable
{
    private const string Groove = "module groove version 1.0\nparam kit kit/tape\ninput chords\noutput audio mix.mix\n\nbass bass {\n  from in.chords -> notes\n}\ndrums drums {\n  kit $kit\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n}\n";
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\nuse groove@1.0 as intro {\n  from chords.notes -> chords\n}\nmaster master {\n  from intro.audio -> audio\n}\n";
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-modules-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Add_list_check_expand_build_lock_and_credits_work_end_to_end()
    {
        string project = Path.Combine(folder, "song");
        Assert.Equal(0, Run("new", project).Code);
        string module = Path.Combine(folder, "groove.module");
        File.WriteAllText(module, Groove);
        File.WriteAllText(Path.Combine(project, "song.amplet"), Song);
        string home = Path.Combine(folder, "home");
        Environment.SetEnvironmentVariable("AMPLE_HOME", home);
        try
        {
            (int code, string text) = Run("check", Path.Combine(project, "song.amplet"));
            Assert.Equal(1, code);
            Assert.Contains("AMP5320", text, StringComparison.Ordinal);

            (code, text) = Run("modules", "add", module, "--project", project);
            Assert.Equal(0, code);
            Assert.Contains("Added module groove 1.0", text, StringComparison.Ordinal);
            Assert.Equal(1, Run("modules", "add", module, "--project", project).Code);
            Assert.Contains("groove 1.0: params kit; inputs chords; outputs audio", Run("modules", "list", "--project", project).Output, StringComparison.Ordinal);

            (code, text) = Run("check", Path.Combine(project, "song.amplet"));
            Assert.Equal(0, code);

            (code, text) = Run("modules", "expand", Path.Combine(project, "song.amplet"), "--project", project);
            Assert.Equal(0, code);
            Assert.Contains("bass \"intro/bass\" {", text, StringComparison.Ordinal);

            (code, text) = Run("build", Path.Combine(project, "song.amplet"), "--project", project);
            Assert.Equal(0, code);
            Assert.Contains("intro/drums", text, StringComparison.Ordinal);

            (code, text) = Run("lock", "update", "--project", project, "--home", home);
            Assert.Equal(0, code);
            Assert.Contains("modules", File.ReadAllText(Path.Combine(project, "ample.lock")), StringComparison.Ordinal);
            (code, text) = Run("lock", "verify", "--project", project, "--home", home);
            Assert.Equal(0, code);

            File.AppendAllText(Path.Combine(project, "modules", "groove.module"), "# changed\n");
            (code, text) = Run("lock", "verify", "--project", project, "--home", home);
            Assert.Equal(1, code);
            Assert.Contains("AMP5328", text, StringComparison.Ordinal);

            (code, text) = Run("check", "quality", "--project", project);
            Assert.Equal(0, code);
            Assert.Contains("master", text, StringComparison.Ordinal);

            (code, text) = Run("credits", "--project", project, "--write");
            Assert.Equal(0, code);
            Assert.Contains("# Credits: song", text, StringComparison.Ordinal);
            Assert.Contains("groove", text, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(project, ".ample", "credits", "credits.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMPLE_HOME", null);
        }
    }
}
