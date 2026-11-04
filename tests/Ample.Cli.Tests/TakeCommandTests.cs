using System.Text;
using Ample.Audio.IO;
using Ample.Storage.Takes;

namespace Ample.Cli.Tests;

public sealed class TakeCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-takes-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        using StringWriter output = new();
        int code = CliApplication.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public async Task List_show_plan_and_verify_work_and_verify_notices_damage()
    {
        string project = Path.Combine(folder, "p");
        Assert.Equal(0, Run("new", project).Code);
        Takebank bank = Takebank.Open(new PhysicalFileSystem(), new SystemClock(), project);
        PlanRecord plan = new(Guid.NewGuid(), "block-7", "voice lead", "sha256:" + new string('a', 64), [], [new PlanSeed("seed", 5)], 1, "acestep", "0.9", "{}", "{}", "bake", null);
        TakeRecord take = await (await bank.BeginAsync(plan)).CommitAsync([("audio", new MemoryStream(Encoding.UTF8.GetBytes("sound bytes")))]);
        string prefix = take.TakeId[..14];

        (int code, string text) = Run("takes", "list", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("block block-7", text, StringComparison.Ordinal);
        Assert.Contains("1 take(s)", text, StringComparison.Ordinal);

        (code, text) = Run("takes", "show", prefix, "--project", project);
        Assert.Equal(0, code);
        Assert.Contains(take.TakeId, text, StringComparison.Ordinal);
        Assert.Contains("Data (audio): sha256:", text, StringComparison.Ordinal);

        (code, text) = Run("plan", "show", prefix, "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("\"blockUuid\": \"block-7\"", text, StringComparison.Ordinal);

        (code, text) = Run("takes", "verify", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("1 take(s) ok", text, StringComparison.Ordinal);

        string data = Directory.GetFiles(Path.Combine(project, ".ample", "data"), "*", SearchOption.AllDirectories).Single();
        byte[] bytes = await File.ReadAllBytesAsync(data);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(data, bytes);
        (code, text) = Run("takes", "verify", "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("hash", text, StringComparison.Ordinal);

        File.Delete(data);
        (code, text) = Run("takes", "show", prefix, "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("MISSING", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_commands_refuse_a_folder_that_is_not_a_project_and_unknown_takes()
    {
        (int code, string text) = Run("takes", "list", "--project", folder);
        Assert.Equal(1, code);
        Assert.Contains("AMP5210", text, StringComparison.Ordinal);

        string project = Path.Combine(folder, "q");
        Assert.Equal(0, Run("new", project).Code);
        (code, text) = Run("takes", "show", "sha256:ffff", "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("AMP5223", text, StringComparison.Ordinal);
    }
}
