using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Language;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>Lite: the limits and the capability boundary (unit), and the routes with a real build (integration). plan/lite/PLAN.md, section 5 and 9.</summary>
public sealed class LiteTests : IAsyncLifetime, IDisposable
{
    private const string Song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n\nmidi-clip m at 1 for 1 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\n\nsynth s at 1 for 1 {\n  from m.notes -> notes\n}\n\nmaster out {\n  from s.audio -> audio\n}\n";

    private static readonly LiteLimits Limits = LiteLimits.Default;

    private string folder = string.Empty;
    private string home = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;
    private SpecData spec = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-lite-" + Guid.NewGuid().ToString("N"));
        home = folder + "-library";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        string root = FindAmpleRoot();
        spec = Ample.Docs.SpecRoot.At(root).LoadSpecData();
        server = await AmpleServer.StartAsync(new ServerOptions(folder, root, LibraryHome: home));
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        await server.DisposeAsync();
        foreach (string path in new[] { folder, home })
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private static LiteFile Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    private int Workspaces => Directory.Exists(LiteWorkspace.WorkRoot(home)) ? Directory.GetDirectories(LiteWorkspace.WorkRoot(home)).Length : 0;

    private static MultipartFormDataContent Form(string entry, params LiteFile[] files)
    {
        MultipartFormDataContent form = new() { { new StringContent(JsonSerializer.Serialize(new { entry, files = files.Select(f => f.Path) })), "manifest" } };
        for (int i = 0; i < files.Length; i++) form.Add(new ByteArrayContent(files[i].Data), $"f{i}", "file");
        return form;
    }

    // ---- the boundary (unit)

    [Theory]
    [InlineData("../evil.amplet")]
    [InlineData("a/../../evil.amplet")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("C:/Windows/x.wav")]
    [InlineData("a\\b.amplet")]
    [InlineData("a//b.amplet")]
    [InlineData(".ample/project.json")]
    [InlineData("samples/.hidden.wav")]
    [InlineData("CON.txt")]
    [InlineData("a:b.txt")]
    [InlineData("name .txt ")]
    [InlineData("a/b/c/d/e/f/g/h.txt")]
    [InlineData("")]
    public void A_hostile_path_is_refused(string path) => Assert.Throws<LiteRejectedException>(() => LitePolicy.CleanPath(path, Limits));

    [Fact]
    public void Plain_paths_are_accepted() => Assert.Equal("samples/kit/kick.wav", LitePolicy.CleanPath("samples/kit/kick.wav", Limits));

    [Fact]
    public void Types_sizes_counts_and_the_song_are_checked_before_anything_is_written()
    {
        Assert.Equal("AMPL904", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", Song), new LiteFile("run.exe", [1])], Limits)).Code);
        Assert.Equal("AMPL904", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", Song), new LiteFile("clip.mp3", [1])], Limits)).Code);
        Assert.Equal("AMPL905", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", new string('x', 600 * 1024))], Limits)).Code);
        Assert.Equal("AMPL906", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", Song), new LiteFile("b.wav", new byte[20 * 1024 * 1024]), new LiteFile("c.wav", new byte[20 * 1024 * 1024]), new LiteFile("d.wav", new byte[20 * 1024 * 1024]), new LiteFile("e.wav", new byte[20 * 1024 * 1024])], Limits)).Code);
        Assert.Equal("AMPL902", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("f0.amplet", [.. Enumerable.Range(0, 201).Select(i => Text($"f{i}.amplet", Song))], Limits)).Code);
        Assert.Equal("AMPL903", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", Song), Text("A.amplet", Song)], Limits)).Code);
        Assert.Equal("AMPL908", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("b.amplet", [Text("a.amplet", Song)], Limits)).Code);
        Assert.Equal("AMPL907", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckFiles("a.txt", [Text("a.txt", Song)], Limits)).Code);
        LitePolicy.CheckFiles("a.amplet", [Text("a.amplet", Song), new LiteFile("samples/k.wav", new byte[100])], Limits);
    }

    [Theory]
    [InlineData("ai-voice v {\n}\n", "AMPL911")]
    [InlineData("ace-music m {\n}\n", "AMPL911")]
    [InlineData("voice-clone c {\n}\n", "AMPL911")]
    [InlineData("drums d at 1 for 1 {\n  kit ../../secret\n  pattern kick:x...\n}\n", "AMPL912")]
    [InlineData("drums d at 1 for 1 {\n  kit C:/Windows\n  pattern kick:x...\n}\n", "AMPL912")]
    [InlineData("import-audio i {\n  file /etc/passwd\n}\n", "AMPL912")]
    [InlineData("settings { capabilities.environment on }\n", "AMPL910")]
    public void A_song_that_asks_for_more_than_Lite_gives_is_refused(string blocks, string code)
    {
        string text = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n" + blocks;
        AmpleDocument doc = Parser.Parse(text, spec);
        Assert.Equal(code, Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckSong(new Dictionary<string, string> { ["a.amplet"] = text }, doc, spec, Limits)).Code);
    }

    [Theory]
    [InlineData("channel v gm file=pack:musescore-general program=40 bank=0 vol=-5 pan=0", true)]
    [InlineData("channel k drum sample=samples/kit/kick.wav vol=-2", true)]
    [InlineData("channel v gm file=pack:other-pack program=40", false)]
    [InlineData("channel v gm file=/etc/passwd program=40", false)]
    [InlineData("channel v gm file=C:/Windows/x.sf2 program=40", false)]
    [InlineData("channel k drum sample=../secret/kick.wav", false)]
    [InlineData("channel p notes set=https://example.test/x", false)]
    public void A_sequence_may_name_only_files_of_the_project_and_the_shared_bank(string channel, bool allowed)
    {
        string song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n";
        AmpleDocument doc = Parser.Parse(song, spec);
        Dictionary<string, string> texts = new() { ["a.amplet"] = song, ["score.ampleseq"] = "sequencer 1\n" + channel + "\npattern p bars=1\nplaylist\n  clip p track=1 at=1 for=1\n" };
        if (allowed) LitePolicy.CheckSong(texts, doc, spec, Limits);
        else Assert.Equal("AMPL912", Assert.Throws<LiteRejectedException>(() => LitePolicy.CheckSong(texts, doc, spec, Limits)).Code);
    }

    [Fact]
    public void Messages_lose_the_paths_of_this_machine()
    {
        string root = Path.Combine(Path.GetTempPath(), "lite-work", "abc");
        string cleaned = LiteBuilder.Clean($"cannot read {root}{Path.DirectorySeparatorChar}samples{Path.DirectorySeparatorChar}k.wav and {"C:\\"}{"Users"}\\alex\\secret.txt and {"/"}{"home"}/alex/x", root);
        Assert.DoesNotContain(Path.GetTempPath(), cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alex", cleaned, StringComparison.Ordinal);
        Assert.Contains("samples", cleaned, StringComparison.Ordinal);
    }

    // ---- the routes (integration)

    [Fact]
    public async Task Lite_without_a_slash_goes_to_the_page_and_the_login_page_returns_to_lite_only_for_lite()
    {
        using HttpClient plain = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url) };
        using HttpResponseMessage redirect = await plain.GetAsync("/remix");
        Assert.True((int)redirect.StatusCode is 301 or 302 or 307 or 308);
        Assert.EndsWith("/remix/", redirect.Headers.Location?.OriginalString, StringComparison.Ordinal);
        string login = Ample.Server.AuthService.LoginPage;
        Assert.Contains("get('next')==='/remix/'?'/remix/':'/'", login, StringComparison.Ordinal);   // only that one address is honoured: no open redirect
    }

    [Fact]
    public async Task The_routes_need_the_token_like_every_api_route()
    {
        using HttpClient anonymous = new() { BaseAddress = new Uri(server.Url) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/remix/limits")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/remix/build", Form("a.amplet", Text("a.amplet", Song)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/remix/output/abc")).StatusCode);
        JsonElement limits = await http.GetFromJsonAsync<JsonElement>("/api/remix/limits");
        Assert.Equal(200, limits.GetProperty("maxFiles").GetInt32());
    }

    [Fact]
    public async Task A_streaming_build_tells_its_steps_one_line_at_a_time_and_ends_with_the_result()
    {
        using HttpResponseMessage response = await http.PostAsync("/api/remix/build?stream=1", Form("song.amplet", Text("song.amplet", Song)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        string[] lines = (await response.Content.ReadAsStringAsync()).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        List<JsonElement> messages = [.. lines.Select(l => JsonDocument.Parse(l).RootElement.Clone())];
        Assert.True(messages.Count(m => m.GetProperty("t").GetString() == "log") >= 4, "the steps of the build");
        double last = -1;
        foreach (JsonElement m in messages.Where(m => m.GetProperty("t").GetString() == "log"))
        {
            Assert.InRange(m.GetProperty("fraction").GetDouble(), last, 1);
            last = m.GetProperty("fraction").GetDouble();
            Assert.DoesNotMatch("[A-Za-z]:[/]|/tmp/|/home/", m.GetProperty("text").GetString()!);
        }

        JsonElement end = messages[^1];
        Assert.Equal("result", end.GetProperty("t").GetString());
        Assert.True(end.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(0, Workspaces);
    }

    [Fact]
    public async Task A_song_builds_and_its_audio_can_be_fetched_and_dropped_and_the_workspace_is_gone()
    {
        using HttpResponseMessage response = await http.PostAsync("/api/remix/build", Form("song.amplet", Text("song.amplet", Song)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        string id = result.GetProperty("outputId").GetString()!;
        Assert.InRange(result.GetProperty("audioSeconds").GetDouble(), 1.5, 8);
        byte[] wav = await http.GetByteArrayAsync($"/api/remix/output/{id}");
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length, result.GetProperty("bytes").GetInt64());
        Assert.Equal(0, Workspaces);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/remix/output/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/remix/output/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_song_with_an_error_gives_diagnostics_without_paths_and_leaves_no_workspace()
    {
        using HttpResponseMessage response = await http.PostAsync("/api/remix/build", Form("song.amplet", Text("song.amplet", Song.Replace("synth s", "synthh s", StringComparison.Ordinal))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("severity").GetString() == "error");
        Assert.DoesNotContain(Path.GetTempPath(), result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Workspaces);
    }

    [Fact]
    public async Task Hostile_input_is_refused_with_a_status_and_nothing_is_left()
    {
        using HttpResponseMessage escape = await http.PostAsync("/api/remix/build", Form("../song.amplet", Text("../song.amplet", Song)));
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
        using HttpResponseMessage plugin = await http.PostAsync("/api/remix/build", Form("song.amplet", Text("song.amplet", Song + "ai-voice v {\n}\n")));
        Assert.Equal(HttpStatusCode.BadRequest, plugin.StatusCode);
        Assert.Contains("AMPL911", await plugin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using HttpResponseMessage kit = await http.PostAsync("/api/remix/build", Form("song.amplet", Text("song.amplet", Song + "drums d at 1 for 1 {\n  kit ../../x\n  pattern kick:x...\n}\n")));
        Assert.Equal(HttpStatusCode.BadRequest, kit.StatusCode);
        using HttpResponseMessage noManifest = await http.PostAsync("/api/remix/build", new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal(HttpStatusCode.BadRequest, noManifest.StatusCode);
        Assert.Equal(0, Workspaces);
    }

    [Fact]
    public async Task A_build_that_takes_too_long_is_stopped_and_cleaned_up()
    {
        LiteOutputs outputs = new();
        LiteBuildResult result = await LiteBuilder.BuildAsync(spec, home, "song.amplet", [Text("song.amplet", Song)], Limits with { BuildSeconds = 0 }, outputs, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Code == "AMPL923");
        Assert.Equal(0, Workspaces);
        Assert.Equal(0, outputs.Count);
    }

    [Fact]
    public async Task A_cancelled_build_is_cleaned_up()
    {
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiteBuilder.BuildAsync(spec, home, "song.amplet", [Text("song.amplet", Song)], Limits, new LiteOutputs(), cancel.Token));
        Assert.Equal(0, Workspaces);
    }

    [Fact]
    public async Task A_song_longer_than_the_limit_is_refused_and_a_second_build_works_after_a_failure()
    {
        LiteOutputs outputs = new();
        LiteBuildResult tooLong = await LiteBuilder.BuildAsync(spec, home, "song.amplet", [Text("song.amplet", Song)], Limits with { MaxOutputSeconds = 1 }, outputs, CancellationToken.None);
        Assert.False(tooLong.Ok);
        Assert.Contains(tooLong.Diagnostics, d => d.Code == "AMPL922");
        LiteBuildResult fine = await LiteBuilder.BuildAsync(spec, home, "song.amplet", [Text("song.amplet", Song)], Limits, outputs, CancellationToken.None);
        Assert.True(fine.Ok);
        Assert.Equal(0, Workspaces);
    }

    [Fact]
    public void Leftover_workspaces_are_swept()
    {
        string leftover = Path.Combine(LiteWorkspace.WorkRoot(home), "old");
        Directory.CreateDirectory(leftover);
        Directory.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-3));
        string fresh = Path.Combine(LiteWorkspace.WorkRoot(home), "fresh");
        Directory.CreateDirectory(fresh);
        Assert.Equal(1, LiteWorkspace.Sweep(home, TimeSpan.FromHours(1)));
        Assert.False(Directory.Exists(leftover));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void A_workspace_holds_only_the_files_that_were_sent_and_never_escapes()
    {
        using LiteWorkspace workspace = LiteWorkspace.Create(home, [Text("song.amplet", Song), new LiteFile("samples/k.wav", [1, 2, 3])]);
        string[] all = [.. Directory.EnumerateFiles(workspace.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(workspace.Root, f).Replace('\\', '/'))];
        Assert.Equal(["samples/k.wav", "song.amplet"], all.Order(StringComparer.Ordinal));
        Assert.StartsWith(Path.GetFullPath(LiteWorkspace.WorkRoot(home)), workspace.Root, StringComparison.OrdinalIgnoreCase);
        Assert.False(workspace.Root.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "a workspace is never inside a project");
    }
}
