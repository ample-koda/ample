using System.Text.Json;
using Ample.Language;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>The built-in demos of Lite (plan/lite/PLAN.md, L9 and L10): every demo builds with the limits of Lite, its files are all there, and every sound has its licence next to it.</summary>
public sealed class LiteDemosTests : IDisposable
{
    private static readonly string Root = FindRoot();
    private static readonly string Web = Path.Combine(Root, "web", "remix");
    private static readonly string[] Categories = ["Basics", "Instruments", "Effects", "Components", "Genres", "Advanced"];
    private static readonly string[] Levels = ["Beginner", "Intermediate", "Advanced"];
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-lite-demos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(home, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private static JsonElement Catalog() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Web, "demos", "catalog.json"))).RootElement.Clone();

    public static IEnumerable<object[]> DemoIds() => Catalog().EnumerateArray().Select(d => new object[] { d.GetProperty("id").GetString()! });

    private static List<LiteFile> FilesOf(JsonElement demo)
    {
        string id = demo.GetProperty("id").GetString()!;
        List<LiteFile> files = [.. demo.GetProperty("files").EnumerateArray().Select(f => new LiteFile(f.GetString()!, File.ReadAllBytes(Path.Combine(Web, "demos", id, f.GetString()!))))];
        if (demo.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
                foreach (JsonElement file in asset.GetProperty("files").EnumerateArray())
                    files.Add(new LiteFile($"{asset.GetProperty("to").GetString()}/{file.GetString()}", File.ReadAllBytes(Path.Combine(Web, "assets", asset.GetProperty("pack").GetString()!, file.GetString()!))));
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(DemoIds))]
    public async Task Every_demo_builds_with_the_limits_of_Lite(string id)
    {
        JsonElement demo = Catalog().EnumerateArray().First(d => d.GetProperty("id").GetString() == id);
        List<LiteFile> files = FilesOf(demo);
        string entry = demo.TryGetProperty("entry", out JsonElement e) ? e.GetString()! : files.First(f => f.Path.EndsWith(".amplet", StringComparison.Ordinal)).Path;
        LitePolicy.CheckFiles(entry, files, LiteLimits.Default);
        SpecData spec = Ample.Docs.SpecRoot.At(Root).LoadSpecData();
        string buildHome = home;
        if (files.Any(f => f.Path.EndsWith(".ampleseq", StringComparison.Ordinal) && System.Text.Encoding.UTF8.GetString(f.Data).Contains(LitePolicy.SharedBank, StringComparison.Ordinal)))
        {
            // a demo that plays the shared sound bank: it is built with the library of this machine, and skipped where the bank is not installed
            string real = Ample.Storage.Library.Library.DefaultHome();
            if (!Directory.Exists(Path.Combine(real, "audio-packs", "licensed", "musescore-general"))) return;
            buildHome = real;
        }

        LiteBuildResult result = await LiteBuilder.BuildAsync(spec, buildHome, entry, files, LiteLimits.Default, new LiteOutputs(), CancellationToken.None);
        Assert.True(result.Ok, string.Join("; ", result.Diagnostics.Where(d => d.Severity == "error").Select(d => $"{d.Code} line {d.Line}: {d.Message}")));
        Assert.InRange(result.AudioSeconds, 3, 240);
        Assert.InRange(result.Seconds, 0, 100);
        Assert.False(Directory.Exists(LiteWorkspace.WorkRoot(buildHome)) && Directory.GetDirectories(LiteWorkspace.WorkRoot(buildHome)).Length > 0);
    }

    [Fact]
    public void The_catalog_is_complete_and_every_file_of_a_demo_exists()
    {
        JsonElement catalog = Catalog();
        Assert.True(catalog.GetArrayLength() >= 20);
        HashSet<string> ids = [];
        foreach (JsonElement demo in catalog.EnumerateArray())
        {
            Assert.True(ids.Add(demo.GetProperty("id").GetString()!), "an id is used twice");
            Assert.Contains(demo.GetProperty("category").GetString(), Categories);
            Assert.Contains(demo.GetProperty("level").GetString(), Levels);
            Assert.False(string.IsNullOrWhiteSpace(demo.GetProperty("description").GetString()));
            Assert.NotEmpty(FilesOf(demo));
        }
    }

    [Fact]
    public void Every_sound_has_its_licence_and_source_next_to_it_and_is_free_to_share()
    {
        JsonElement index = JsonDocument.Parse(File.ReadAllText(Path.Combine(Web, "assets", "index.json"))).RootElement;
        Assert.True(index.GetArrayLength() >= 1);
        foreach (JsonElement pack in index.EnumerateArray())
        {
            string folder = Path.Combine(Web, "assets", pack.GetProperty("pack").GetString()!);
            Assert.Matches("CC0|public domain|Public domain|own", pack.GetProperty("licence").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(pack.GetProperty("source").GetString()));
            Assert.True(File.Exists(Path.Combine(folder, "SOURCE.md")) || File.Exists(Path.Combine(folder, "OWN.json")), $"{folder} has no SOURCE.md");
            foreach (JsonElement file in pack.GetProperty("files").EnumerateArray()) Assert.True(File.Exists(Path.Combine(folder, file.GetProperty("file").GetString()!)));
            if (File.Exists(Path.Combine(folder, "SOURCE.md"))) Assert.True(File.Exists(Path.Combine(folder, "LICENSE.txt")), $"{folder} has no LICENSE.txt");
        }

        Assert.True(Directory.GetFiles(Path.Combine(Web, "assets"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) < 20 * 1024 * 1024, "the shared sounds stay small");
    }
}
