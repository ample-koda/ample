namespace Ample.Cli.Tests;

public sealed class LibraryCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-lib-").FullName;

    public void Dispose()
    {
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

    private string Make(string name, params (string Rel, string Text)[] files)
    {
        string dir = Path.Combine(folder, name);
        foreach ((string rel, string text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!);
            File.WriteAllText(Path.Combine(dir, rel), text);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Link_list_scan_relocate_and_verify_work_end_to_end_without_changing_a_read_only_folder()
    {
        string home = Path.Combine(folder, "home");
        string kits = Make("kits", ("tape/kick.wav", "kick"), ("tape/snare.wav", "snare"));
        (int code, string text) = Run("library", "link", kits, "--name", "Kits", "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("read-only", text, StringComparison.Ordinal);
        Assert.Contains("2 asset(s)", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(kits, ".ample-catalog.json")));

        (code, text) = Run("library", "list", "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("Kits  online  read-only", text, StringComparison.Ordinal);

        (code, text) = Run("library", "scan", "Kits", "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("2 unchanged, 0 read for hashing", text.Replace("unchanged, 0 read for hashing", "unchanged, 0 read for hashing"), StringComparison.Ordinal);

        (code, text) = Run("library", "verify", "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("as the catalog knows them", text, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(kits, "tape/kick.wav"), "tampered");
        (code, text) = Run("library", "verify", "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("CHANGED  Kits/tape/kick.wav", text, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(kits, "tape/kick.wav"), "kick");
        string moved = Path.Combine(folder, "moved");
        Directory.Move(kits, moved);
        (code, text) = Run("library", "verify", "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("OFFLINE", text, StringComparison.Ordinal);
        (code, text) = Run("library", "relocate", "Kits", moved, "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("is now at", text, StringComparison.Ordinal);

        (code, text) = Run("library", "relocate", "Kits", Make("stranger", ("x.wav", "x")), "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("AMP5292", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_refuses_a_read_only_folder_and_copies_into_a_writable_one()
    {
        string home = Path.Combine(folder, "home");
        string ro = Make("ro");
        string rw = Make("rw");
        string file = Make("src", ("a.wav", "aaa")) + Path.DirectorySeparatorChar + "a.wav";
        Run("library", "link", ro, "--name", "RO", "--home", home);
        Run("library", "link", rw, "--name", "RW", "--read-write", "--home", home);
        (int code, string text) = Run("library", "add", file, "--into", "RO", "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("AMP5293", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ro, "a.wav")));
        (code, text) = Run("library", "add", file, "--into", "RW", "--as", "sub/a.wav", "--home", home);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(rw, "sub", "a.wav")));
    }

    [Fact]
    public void Lock_update_pins_the_assets_a_song_uses_and_verify_notices_a_change()
    {
        string home = Path.Combine(folder, "home");
        string kits = Make("kits", ("kit/tape.wav", "tape kit"));
        Run("library", "link", kits, "--name", "Kits", "--home", home);
        string project = Path.Combine(folder, "proj");
        Assert.Equal(0, Run("new", project).Code);
        File.WriteAllText(Path.Combine(project, "song.amplet"), "composition \"d\" { ample 1  tempo 92  master-seed 1 }\ndrums d {\n  kit kit/tape\n}\n");
        (int code, string text) = Run("lock", "update", "--project", project, "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("1 entry", text, StringComparison.Ordinal);
        Assert.Contains("kit/tape", File.ReadAllText(Path.Combine(project, "ample.lock")), StringComparison.Ordinal);
        (code, text) = Run("lock", "verify", "--project", project, "--home", home);
        Assert.Equal(0, code);
        Assert.Contains("matches", text, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(kits, "kit/tape.wav"), "tape kit changed");
        Run("library", "scan", "Kits", "--home", home);
        (code, text) = Run("lock", "verify", "--project", project, "--home", home);
        Assert.Equal(1, code);
        Assert.Contains("CHANGED", text, StringComparison.Ordinal);
        Assert.Equal(0, Run("lock", "update", "--project", project, "--home", home).Code);
        Assert.Equal(0, Run("lock", "verify", "--project", project, "--home", home).Code);
    }
}
