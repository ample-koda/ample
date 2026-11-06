using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Language;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The demo song every project starts with: created with the project, given to a project without songs, edited but never deleted or moved.</summary>
public sealed class DemoSongTests : IDisposable
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
    public void The_demo_text_ships_with_the_spec_and_is_a_song_without_problems()
    {
        string? text = DemoSong.Text(FindAmpleRoot());

        Assert.NotNull(text);
        Assert.DoesNotContain('\r', text);
        SpecData spec = Ample.Docs.SpecRoot.At(FindAmpleRoot()).LoadSpecData();
        AmpleDocument document = Parser.Parse(text, spec);
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(text, document.ToText());   // written in the canonical form, so the first save changes nothing
    }

    [Fact]
    public async Task A_new_project_gets_the_demo_and_an_existing_file_of_that_name_is_never_overwritten()
    {
        string folder = NewFolder();
        using (await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "p", null, CancellationToken.None)) { }

        Assert.True(DemoSong.SeedNew(folder, FindAmpleRoot()));
        Assert.Equal(DemoSong.Text(FindAmpleRoot()), await File.ReadAllTextAsync(Path.Combine(folder, DemoSongRules.FileName)));

        await File.WriteAllTextAsync(Path.Combine(folder, DemoSongRules.FileName), "mine\n");
        Assert.False(DemoSong.SeedNew(folder, FindAmpleRoot()));
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(folder, DemoSongRules.FileName)));
    }

    [Fact]
    public async Task A_project_without_songs_gets_the_demo_when_the_server_seeds_and_a_project_with_a_song_is_left_alone()
    {
        string empty = await NewProjectAsync();
        await using (RunningServer server = await StartAsync(empty, seed: true))
        {
            Assert.Equal(DemoSong.Text(FindAmpleRoot()), await File.ReadAllTextAsync(Path.Combine(empty, DemoSongRules.FileName)));
        }

        string withSong = await NewProjectAsync();
        Directory.CreateDirectory(Path.Combine(withSong, "songs"));
        await File.WriteAllTextAsync(Path.Combine(withSong, "songs", "mine.amplet"), "composition \"x\" { ample 1 }\n");
        await using (RunningServer server = await StartAsync(withSong, seed: true))
        {
            Assert.False(File.Exists(Path.Combine(withSong, DemoSongRules.FileName)));
        }

        string notSeeded = await NewProjectAsync();
        await using (RunningServer server = await StartAsync(notSeeded, seed: false))
        {
            Assert.False(File.Exists(Path.Combine(notSeeded, DemoSongRules.FileName)));   // tests and helper servers never get a demo
        }
    }

    [Fact]
    public async Task The_demo_can_be_edited_but_not_deleted_or_moved_or_renamed()
    {
        string folder = await NewProjectAsync();
        await using RunningServer server = await StartAsync(folder, seed: true);
        using HttpClient http = new() { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
        string path = Path.Combine(folder, DemoSongRules.FileName);

        // edit: through the same save route as every song
        string original = await File.ReadAllTextAsync(path);
        string edited = original.Replace("tempo 175", "tempo 160", StringComparison.Ordinal);
        Assert.NotEqual(original, edited);
        using HttpResponseMessage open = await http.GetAsync("/api/doc?path=demo.amplet");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        long version = JsonDocument.Parse(await open.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetInt64();
        using HttpResponseMessage put = await http.PutAsync("/api/doc", Json(new { path = DemoSongRules.FileName, text = edited, baseVersion = version }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using HttpResponseMessage save = await http.PostAsync("/api/doc/save", Json(new { path = DemoSongRules.FileName }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("tempo 160", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        // never deleted: the route says why, whatever the spelling, and the file stays
        foreach (string spelling in new[] { "demo.amplet", "DEMO.AMPLET", "%2Fdemo.amplet" })
        {
            using HttpResponseMessage delete = await http.DeleteAsync("/api/project/file?path=" + spelling);
            Assert.NotEqual(HttpStatusCode.OK, delete.StatusCode);
            Assert.Contains("demo song stays", await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // not moved away or renamed
        using HttpResponseMessage move = await http.PostAsync("/api/project/move", Json(new { source = DemoSongRules.FileName, destination = "other.amplet" }));
        Assert.NotEqual(HttpStatusCode.OK, move.StatusCode);
        using HttpResponseMessage rename = await http.PostAsync("/api/project/rename", Json(new { source = DemoSongRules.FileName, destination = "renamed.amplet" }));
        Assert.NotEqual(HttpStatusCode.OK, rename.StatusCode);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(folder, "other.amplet")));
        Assert.False(File.Exists(Path.Combine(folder, "renamed.amplet")));
    }

    [Fact]
    public async Task After_the_first_time_setup_the_demo_can_be_deleted_and_does_not_come_back()
    {
        string folder = await NewProjectAsync();
        await using (RunningServer server = await StartAsync(folder, seed: true))
        {
            using HttpClient http = new() { BaseAddress = new Uri(server.Url) };
            http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
            string path = Path.Combine(folder, DemoSongRules.FileName);
            using HttpResponseMessage refused = await http.DeleteAsync("/api/project/file?path=demo.amplet");
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);   // while the setup is still to be done
            using HttpResponseMessage done = await http.PostAsync("/api/project/first-run/complete", Json(new { }));
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            using HttpResponseMessage deleted = await http.DeleteAsync("/api/project/file?path=demo.amplet");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.False(File.Exists(path));
        }

        await using (RunningServer again = await StartAsync(folder, seed: true))
        {
            Assert.False(File.Exists(Path.Combine(folder, DemoSongRules.FileName)));   // a project without songs gets the demo only while its setup is pending
        }
    }

    [Theory]
    [InlineData("demo.amplet", true)]
    [InlineData("DEMO.amplet", true)]
    [InlineData("/demo.amplet", true)]
    [InlineData("demo.amplet/", true)]
    [InlineData("sub/demo.amplet", false)]
    [InlineData("demo.amplet.bak", false)]
    [InlineData("my-demo.amplet", false)]
    [InlineData("", false)]
    public void Only_the_demo_in_the_project_folder_itself_is_protected(string path, bool expected)
    {
        Assert.Equal(expected, DemoSongRules.IsDemo(path));
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-demosong-" + Guid.NewGuid().ToString("N"));
        folders.Add(folder);
        return folder;
    }

    private async Task<string> NewProjectAsync()
    {
        string folder = NewFolder();
        using (await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "p", null, CancellationToken.None)) { }
        return folder;
    }

    private static Task<RunningServer> StartAsync(string folder, bool seed) =>
        AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library") { SeedDemo = seed });

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }
}
