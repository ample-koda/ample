using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Storage.Takes;

namespace Ample.Cli.Tests;

[Trait("Category", "Analysis")]
public sealed class AnalyzeCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-analyze-").FullName;

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
    public async Task Analyze_lists_computes_caches_and_refuses_what_it_cannot_do()
    {
        string project = Path.Combine(folder, "p");
        Assert.Equal(0, Run("new", project).Code);
        short[] samples = new short[44100 * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 44100));
        }

        Takebank bank = Takebank.Open(new PhysicalFileSystem(), new SystemClock(), project);
        PlanRecord plan = new(Guid.NewGuid(), "u", "tone", "sha256:" + new string('b', 64), [], [], 1, "test", "1", "{}", "{}", "bake", null);
        TakeRecord take = await (await bank.BeginAsync(plan)).CommitAsync([("audio", new MemoryStream(Wav.Encode(samples, 44100)))]);
        string id = take.TakeId[..14];

        (int code, string text) = Run("analyze", "--list");
        Assert.Equal(0, code);
        Assert.Contains("melody 1.0", text, StringComparison.Ordinal);

        (code, text) = Run("analyze", id, "--with", "pitch", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("computed now", text, StringComparison.Ordinal);
        Assert.Contains("\"hz\"", text, StringComparison.Ordinal);
        (code, text) = Run("analyze", id, "--with", "pitch", "--project", project);
        Assert.Contains("from the stored result", text, StringComparison.Ordinal);

        (code, text) = Run("analyze", id, "--with", "loudness", "--project", project);
        Assert.Equal(0, code);
        Assert.Contains("integratedLufs", text, StringComparison.Ordinal);

        (code, text) = Run("analyze", id, "--with", "nothing", "--project", project);
        Assert.Equal(1, code);
        Assert.Contains("AMP5341", text, StringComparison.Ordinal);
        (code, text) = Run("analyze", id, "--project", project);
        Assert.Equal(1, code);

        string plugins = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins"));
        Assert.True(Directory.Exists(plugins));
        string cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetDirectoryName(plugins)!);
        try
        {
            (code, text) = Run("analyze", id, "--with", "tempo", "--plugin", "analysis-fake", "--project", project);
            Assert.Equal(0, code);
            Assert.Contains("\"bpm\":120", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }
}
