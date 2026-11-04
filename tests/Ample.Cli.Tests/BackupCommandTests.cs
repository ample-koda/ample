namespace Ample.Cli.Tests;

public sealed class BackupCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-backup-").FullName;

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
    public void Backup_create_list_verify_prune_and_restore_work_end_to_end()
    {
        string project = Path.Combine(folder, "song");
        string target = Path.Combine(folder, "backups");
        Assert.Equal(0, Run("new", project).Code);
        File.WriteAllText(Path.Combine(project, "song.amplet"), "composition \"d\" { ample 1  tempo 92  master-seed 1 }\n");

        (int code, string text) = Run("backup", "create", "--target", target, "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("Backup ", text, StringComparison.Ordinal);
        string id = text.Split(' ')[1].TrimEnd(':');

        (code, text) = Run("backup", "list", "--target", target);
        Assert.Equal(0, code);
        Assert.Contains(id, text, StringComparison.Ordinal);
        Assert.DoesNotContain("last good", text, StringComparison.Ordinal);

        (code, text) = Run("backup", "verify", "--target", target);
        Assert.Equal(0, code);
        Assert.Contains("intact", text, StringComparison.Ordinal);
        Assert.Contains("last good", Run("backup", "list", "--target", target).Output, StringComparison.Ordinal);

        (code, text) = Run("backup", "prune", "--target", target, "--keep", "1", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("Nothing was removed", text, StringComparison.Ordinal);

        string restored = Path.Combine(folder, "restored");
        (code, text) = Run("restore", "latest", restored, "--target", target);
        Assert.Equal(0, code);
        Assert.Equal(File.ReadAllText(Path.Combine(project, "song.amplet")), File.ReadAllText(Path.Combine(restored, "song.amplet")));
        (code, text) = Run("restore", "latest", restored, "--target", target);
        Assert.Equal(1, code);
        Assert.Contains("AMP5301", text, StringComparison.Ordinal);

        (code, text) = Run("backup", "verify", "nonsense", "--target", target);
        Assert.Equal(1, code);
        Assert.Contains("AMP5303", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_and_import_make_a_new_project_and_in_place_restore_needs_the_typed_words()
    {
        string project = Path.Combine(folder, "song");
        string target = Path.Combine(folder, "backups");
        Run("new", project);
        File.WriteAllText(Path.Combine(project, "song.amplet"), "original\n");
        string archive = Path.Combine(folder, "song.ample");
        (int code, string text) = Run("export", archive, "--project", project, "--no-takes");
        Assert.Equal(0, code);
        Assert.Contains("without takes", text, StringComparison.Ordinal);
        string imported = Path.Combine(folder, "imported");
        (code, text) = Run("import", archive, imported, "--new-id");
        Assert.Equal(0, code);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(imported, "song.amplet")));

        Run("backup", "create", "--target", target, "--project", project);
        File.WriteAllText(Path.Combine(project, "song.amplet"), "ruined\n");
        (code, text) = Run("restore", "latest", "--in-place", "--target", target, "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("Nothing was changed", text, StringComparison.Ordinal);
        Assert.Equal("ruined\n", File.ReadAllText(Path.Combine(project, "song.amplet")));
        string accept = text[(text.IndexOf("--accept \"", StringComparison.Ordinal) + 10)..];
        accept = accept[..accept.IndexOf('"', StringComparison.Ordinal)];
        (code, text) = Run("restore", "latest", "--in-place", "--target", target, "--project", project, "--accept", accept, "--reason", "test");
        Assert.Equal(0, code);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(project, "song.amplet")));
    }
}
