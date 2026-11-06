using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Engine.Modules;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The working copies of text files (plan/editor-tabs.md, part 1): a component file with unsaved changes is what a build reads.</summary>
public sealed class WorkingTextsTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (string folder in folders)
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
            try { Directory.Delete(folder + "-library", true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task An_unsaved_component_file_is_read_instead_of_the_disk_and_the_disk_again_after_a_revert_or_a_save()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-wc-" + Guid.NewGuid().ToString("N"));
        folders.Add(folder);
        using (await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "p", null, CancellationToken.None)) { }
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        string file = Path.Combine(folder, "components", "b.amplec");
        await File.WriteAllTextAsync(file, "# on disk\n");

        await using RunningServer server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        using HttpClient http = new() { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
        ModuleService modules = new(folder, (Ample.Storage.Library.Library?)null);

        Assert.Equal("# on disk\n", modules.ReadComponentFile("b"));

        using HttpResponseMessage put = await http.PutAsync("/api/wc", Json(new { path = "components/b.amplec", text = "# unsaved\n" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("dirty").GetBoolean());
        Assert.Equal("# unsaved\n", modules.ReadComponentFile("b"));   // what a build of a song that imports b reads
        Assert.Equal("# on disk\n", await File.ReadAllTextAsync(file));   // the file itself is untouched
        Assert.Contains("components/b.amplec", await http.GetStringAsync("/api/wc"), StringComparison.Ordinal);

        using HttpResponseMessage revert = await http.PostAsync("/api/wc/revert", Json(new { path = "components/b.amplec" }));
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        Assert.Equal("# on disk\n", modules.ReadComponentFile("b"));

        await http.PutAsync("/api/wc", Json(new { path = "components/b.amplec", text = "# saved now\n" }));
        using HttpResponseMessage save = await http.PostAsync("/api/wc/save", Json(new { path = "components/b.amplec" }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal("# saved now\n", await File.ReadAllTextAsync(file));
        Assert.Equal("# saved now\n", modules.ReadComponentFile("b"));
        Assert.DoesNotContain("components/b.amplec", await http.GetStringAsync("/api/wc"), StringComparison.Ordinal);   // saved: no longer unsaved
    }

    [Fact]
    public async Task The_private_folder_and_paths_outside_the_project_have_no_working_copy()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-wc-" + Guid.NewGuid().ToString("N"));
        folders.Add(folder);
        using (await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "p", null, CancellationToken.None)) { }
        await using RunningServer server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        using HttpClient http = new() { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);

        foreach (string path in new[] { ".ample/project.json", "../outside.txt", "a/../../b.txt" })
        {
            using HttpResponseMessage put = await http.PutAsync("/api/wc", Json(new { path, text = "x" }));
            Assert.NotEqual(HttpStatusCode.OK, put.StatusCode);
        }
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }
}
