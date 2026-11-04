using System.Diagnostics;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Cli.Tests;

public sealed class ProjectCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-proj-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void New_then_doctor_then_open_and_info_work_end_to_end()
    {
        string project = Path.Combine(folder, "song one");
        (int code, string text) = Run("new", project, "--seed", "424242");
        Assert.Equal(0, code);
        Assert.Contains("Created project 'song one'", text, StringComparison.Ordinal);

        (code, text) = Run("doctor", project);
        Assert.Equal(0, code);
        Assert.DoesNotContain("error AMP", text, StringComparison.Ordinal);

        (code, text) = Run("project", "open", project);
        Assert.Equal(0, code);
        Assert.Contains("Master seed: 424242", text, StringComparison.Ordinal);
        Assert.Contains("Opened for writing", text, StringComparison.Ordinal);

        (code, text) = Run("project", "info", project);
        Assert.Equal(0, code);
        Assert.Contains("Opened read-only.", text, StringComparison.Ordinal);

        (code, text) = Run("new", project);
        Assert.Equal(1, code);
        Assert.Contains("AMP5208", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_and_open_report_a_folder_that_is_not_a_project()
    {
        (int code, string text) = Run("doctor", folder);
        Assert.Equal(1, code);
        Assert.Contains("AMP5210", text, StringComparison.Ordinal);
        Assert.Equal(1, Run("project", "open", folder).Code);
    }

    [Fact]
    public async Task A_second_process_cannot_open_a_project_that_this_process_holds()
    {
        string project = Path.Combine(folder, "locked");
        using OpenProject held = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), project, "locked");

        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = Path.GetFileName(baseDir);
        string config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        string root = baseDir;
        while (!Directory.Exists(Path.Combine(root, "src", "Ample.Cli")))
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("AMPLE folder not found");
        }

        string dll = Path.Combine(root, "src", "Ample.Cli", "bin", config, tfm, "Ample.Cli.dll");
        Assert.True(File.Exists(dll), dll);

        using Process child = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" project open \"{project}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync();

        Assert.Equal(1, child.ExitCode);
        Assert.Contains("AMP5204", output, StringComparison.Ordinal);
    }
}
