using Ample.Audio.IO;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Cli.Tests;

public sealed class BlackboxCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-bb-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Tail_verify_restore_and_recover_work_on_a_real_project()
    {
        string project = Path.Combine(folder, "p");
        Assert.Equal(0, Run("new", project).Code);
        PhysicalFileSystem fs = new();
        SystemClock clock = new();
        using (OpenProject open = await ProjectStore.OpenAsync(fs, clock, project))
        using (Journal journal = await Journal.OpenAsync(fs, clock, project, open.Manifest.Id.ToString("D")))
        {
            CommandBus bus = new(journal, new CommandContext(fs, project, clock), "test");
            await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "one"));
            await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "two"));
            await journal.AppendAsync("test", "file.write", "start", "crash", "{\"path\":\"song.amplet\"}", "{\"path\":\"song.amplet\",\"existed\":true,\"text\":\"one\"}");
        }

        (int code, string text) = Run("blackbox", "tail", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("file.write", text, StringComparison.Ordinal);

        Assert.Equal(0, Run("blackbox", "verify", "--project", project).Code);
        (code, text) = Run("recover", "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("interrupted: record 5", text, StringComparison.Ordinal);

        Assert.Equal(0, Run("blackbox", "restore", "3", "--project", project).Code);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(project, "song.amplet")));

        Assert.Equal(0, Run("blackbox", "export", Path.Combine(folder, "out.txt"), "--project", project).Code);
        Assert.Equal(0, Run("blackbox", "compact", "--project", project).Code);
        Assert.Equal(1, Run("blackbox", "show", "9999", "--project", project).Code);

        string seg = Directory.GetFiles(Path.Combine(project, ".ample", "blackbox"), "*.seg").Single();
        await File.WriteAllTextAsync(seg, (await File.ReadAllTextAsync(seg)).Replace("two", "TWO", StringComparison.Ordinal));
        (code, text) = Run("blackbox", "verify", "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("is damaged or changed", text, StringComparison.Ordinal);
    }
}
