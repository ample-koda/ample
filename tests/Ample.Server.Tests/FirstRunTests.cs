using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The first-time setup wizard of a project (plan/first-run-wizard.md): the state in project.json and the two routes.</summary>
public sealed class FirstRunTests : IDisposable
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
    public void A_new_project_is_pending_with_origin_new_and_a_file_without_the_field_is_pending_with_origin_existing()
    {
        ProjectManifest created = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, null);
        Assert.Equal("new", created.Settings.FirstRun.Origin);
        Assert.True(created.Settings.FirstRun.Pending);
        Assert.Contains("\"firstRun\"", ProjectFile.Serialize(created), StringComparison.Ordinal);

        ProjectManifest older = created with { Settings = created.Settings with { FirstRun = FirstRunSettings.Default } };
        string text = ProjectFile.Serialize(older);
        Assert.DoesNotContain("firstRun", text, StringComparison.Ordinal);   // nothing is written for a project that never did it: the file stays as it was
        FirstRunSettings read = ProjectFile.Parse(text).Settings.FirstRun;
        Assert.Equal("existing", read.Origin);
        Assert.True(read.Pending);
    }

    [Fact]
    public void The_done_state_survives_a_round_trip_and_a_higher_wizard_version_makes_it_pending_again()
    {
        DateTimeOffset now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        ProjectManifest created = ProjectFile.CreateNew(Guid.NewGuid(), "p", now, null);
        ProjectManifest done = created with { Settings = created.Settings with { FirstRun = created.Settings.FirstRun.Completed(now) } };
        FirstRunSettings read = ProjectFile.Parse(ProjectFile.Serialize(done)).Settings.FirstRun;
        Assert.Equal(new FirstRunSettings("new", FirstRunSettings.WizardVersion, now), read);
        Assert.False(read.Pending);
        Assert.True((read with { DoneVersion = FirstRunSettings.WizardVersion - 1 }).Pending);   // a later AMPLE raises the number and every project is asked again
    }

    [Fact]
    public async Task A_garbled_state_in_the_file_means_not_done()
    {
        ProjectManifest created = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, null);
        string text = ProjectFile.Serialize(created).Replace("\"origin\": \"new\"", "\"origin\": \"sideways\"", StringComparison.Ordinal).Replace("\"doneVersion\": 0", "\"doneVersion\": \"x\"", StringComparison.Ordinal);
        FirstRunSettings read = ProjectFile.Parse(text).Settings.FirstRun;
        Assert.Equal("existing", read.Origin);
        Assert.True(read.Pending);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_new_project_completes_into_the_demo_and_the_second_complete_changes_nothing()
    {
        string folder = await NewProjectAsync(existing: false);
        Assert.True(DemoSong.SeedNew(folder, FindAmpleRoot()));
        await using RunningServer server = await StartAsync(folder);
        using HttpClient http = Client(server);

        Assert.True(await Pending(http));
        Assert.Equal("new", (await Json(http, "/api/project/first-run")).GetProperty("origin").GetString());

        JsonElement first = await Post(http, "/api/project/first-run/complete");
        Assert.Equal("demo.amplet", first.GetProperty("open").GetString());
        Assert.True(first.GetProperty("play").GetBoolean());
        Assert.False(await Pending(http));
        DateTimeOffset? doneAt = ProjectFile.Parse(File.ReadAllText(ProjectFilePath(folder))).Settings.FirstRun.DoneAt;
        Assert.NotNull(doneAt);

        JsonElement second = await Post(http, "/api/project/first-run/complete");
        Assert.Equal("demo.amplet", second.GetProperty("open").GetString());
        Assert.Equal(doneAt, ProjectFile.Parse(File.ReadAllText(ProjectFilePath(folder))).Settings.FirstRun.DoneAt);   // done once, not renewed
    }

    [Fact]
    public async Task An_existing_project_is_asked_once_too_and_completes_without_the_demo_or_playing()
    {
        string folder = await NewProjectAsync(existing: true);
        Assert.True(DemoSong.SeedNew(folder, FindAmpleRoot()));   // it may even have the demo: only a new project starts it
        await using RunningServer server = await StartAsync(folder);
        using HttpClient http = Client(server);

        Assert.True(await Pending(http));
        Assert.Equal("existing", (await Json(http, "/api/project/first-run")).GetProperty("origin").GetString());
        JsonElement done = await Post(http, "/api/project/first-run/complete");
        Assert.Equal(JsonValueKind.Null, done.GetProperty("open").ValueKind);
        Assert.False(done.GetProperty("play").GetBoolean());
        Assert.False(await Pending(http));
        Assert.Equal("existing", ProjectFile.Parse(File.ReadAllText(ProjectFilePath(folder))).Settings.FirstRun.Origin);
    }

    [Fact]
    public async Task The_settings_route_keeps_the_first_run_state_when_other_settings_change()
    {
        string folder = await NewProjectAsync(existing: false);
        await using RunningServer server = await StartAsync(folder);
        using HttpClient http = Client(server);
        using HttpResponseMessage saved = await http.PostAsync("/api/project/settings", new StringContent("{\"kodaLanguage\":\"nl\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(await Pending(http));   // saving a page of the wizard does not finish it
        Assert.Equal("new", ProjectFile.Parse(File.ReadAllText(ProjectFilePath(folder))).Settings.FirstRun.Origin);
    }

    private static HttpClient Client(RunningServer server)
    {
        HttpClient http = new() { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
        return http;
    }

    private static async Task<JsonElement> Json(HttpClient http, string path) => JsonDocument.Parse(await http.GetStringAsync(path)).RootElement.Clone();

    private static async Task<JsonElement> Post(HttpClient http, string path)
    {
        using HttpResponseMessage response = await http.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<bool> Pending(HttpClient http)
    {
        bool viaRoute = (await Json(http, "/api/project/first-run")).GetProperty("pending").GetBoolean();
        Assert.Equal(viaRoute, (await Json(http, "/api/project")).GetProperty("firstRunPending").GetBoolean());   // the page asks at the start, in the project answer
        return viaRoute;
    }

    private static string ProjectFilePath(string folder) => Path.Combine(folder, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);

    private async Task<string> NewProjectAsync(bool existing)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-firstrun-" + Guid.NewGuid().ToString("N"));
        folders.Add(folder);
        using (await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "p", null, CancellationToken.None)) { }
        if (existing)
        {
            // a project from before the wizard: its file has no such field
            ProjectManifest manifest = ProjectFile.Parse(await File.ReadAllTextAsync(ProjectFilePath(folder)));
            await File.WriteAllTextAsync(ProjectFilePath(folder), ProjectFile.Serialize(manifest with { Settings = manifest.Settings with { FirstRun = FirstRunSettings.Default } }));
        }

        return folder;
    }

    private static Task<RunningServer> StartAsync(string folder) =>
        AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }
}
