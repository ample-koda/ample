using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The game state log and the file checks of the score API (no server needed).</summary>
public sealed class GameStateLogTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_report_is_checked_clamped_and_numbered()
    {
        GameStateLog log = new();
        GameStateEntry first = log.Add(Body("""{ "session": "match-1", "state": "tension", "previous": "calm", "bar": 12, "intensity": 7, "advantage": -3, "revision": "abc", "signals": { "armyRatio": 0.4, "bad": "x" } }"""), DateTimeOffset.UnixEpoch);
        Assert.Equal(1, first.Seq);
        Assert.Equal("tension", first.State);
        Assert.Equal("calm", first.Previous);
        Assert.Equal(1.0, first.Intensity);
        Assert.Equal(-1.0, first.Advantage);
        Assert.Equal(0.4, first.Signals["armyRatio"]);
        Assert.False(first.Signals.ContainsKey("bad"));
        Assert.Equal(2, log.Add(Body("""{ "state": "calm" }"""), DateTimeOffset.UnixEpoch).Seq);
        Assert.Equal("game", log.Latest()!.Session);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "state": "Tension" }""")]
    [InlineData("""{ "state": 5 }""")]
    public void A_report_without_a_valid_state_is_refused(string json)
    {
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => new GameStateLog().Add(Body(json), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Only_the_last_reports_are_kept()
    {
        GameStateLog log = new(3);
        for (int i = 0; i < 10; i++)
        {
            log.Add(Body("""{ "state": "calm" }"""), DateTimeOffset.UnixEpoch);
        }

        Assert.Equal([8, 9, 10], log.Recent(50).Select(e => (int)e.Seq));
        Assert.Equal([10], log.Recent(1).Select(e => (int)e.Seq));
    }

    [Theory]
    [InlineData("demo-score.pad.wav", true)]
    [InlineData("demo-score.soviet.manifest.json", true)]
    [InlineData("../project.json", false)]
    [InlineData("demo-score.exe", false)]
    [InlineData("Demo.wav", false)]
    [InlineData("a/b.wav", false)]
    [InlineData(".hidden.wav", false)]
    [InlineData("", false)]
    public void Only_stem_and_manifest_names_may_be_asked_for(string file, bool valid)
    {
        Assert.Equal(valid, GameScoreApi.ValidFile(file));
    }

    [Theory]
    [InlineData(null, "game/score.json")]
    [InlineData("", "game/score.json")]
    [InlineData("scores\\red.json", "scores/red.json")]
    public void The_score_path_defaults_and_normalizes(string? raw, string expected)
    {
        Assert.Equal(expected, GameScoreApi.ScorePath(raw));
    }

    [Theory]
    [InlineData("../score.json")]
    [InlineData("/etc/score.json")]
    [InlineData("score.txt")]
    [InlineData("c:/score.json")]
    public void A_score_path_outside_the_project_or_not_json_is_refused(string raw)
    {
        Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => GameScoreApi.ScorePath(raw));
    }
}

/// <summary>The game routes against a real server and a small song: build, manifest, stems, state reports and variants.</summary>
public sealed class GameRoutesTests : IAsyncLifetime, IDisposable
{
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 1 }\n"
        + "drums low at 1 for 2 {\n  kit samples/kit\n  pattern kick:x...............\n}\n"
        + "drums hat at 1 for 2 {\n  kit samples/kit\n  pattern kick:..x...x...x...x.\n}\n";

    private const string Score = """
        {
          "version": 1,
          "name": "demo-score",
          "song": "song.amplet",
          "loopBars": 1,
          "layers": [ { "id": "low", "block": "low" }, { "id": "offbeat", "block": "hat" } ],
          "states": [
            { "id": "calm", "levels": { "low": 1 } },
            { "id": "tension", "levels": { "low": 1, "offbeat": 1 } }
          ]
        }
        """;

    private string folder = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-gameroutes-" + Guid.NewGuid().ToString("N"));
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        Directory.CreateDirectory(Path.Combine(folder, "samples", "kit"));
        Directory.CreateDirectory(Path.Combine(folder, "game"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "samples", "kit", "kick.wav"), Wav.Encode([12000, 0, -8000, 0], 44100));
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), Song);
        await File.WriteAllTextAsync(Path.Combine(folder, "game", "score.json"), Score);
        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        await server.DisposeAsync();
        try
        {
            Directory.Delete(folder, true);
            Directory.Delete(folder + "-library", true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<HttpResponseMessage> Post(string path, object body) => await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), CancellationToken.None);

    [Fact]
    public async Task The_score_is_described_before_and_after_it_is_built_and_a_rebuild_changes_nothing()
    {
        JsonElement before = await http.GetFromJsonAsync<JsonElement>("/api/game/score", CancellationToken.None);
        Assert.True(before.GetProperty("ok").GetBoolean());
        Assert.Equal("demo-score", before.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("built")[0].GetProperty("revision").ValueKind);

        using HttpResponseMessage built = await Post("/api/game/score/build", new { score = "game/score.json" });
        string json = await built.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(built.IsSuccessStatusCode, json);
        JsonElement result = JsonDocument.Parse(json).RootElement;
        string revision = result.GetProperty("revision").GetString()!;
        Assert.Equal(16, revision.Length);
        Assert.Equal(2, result.GetProperty("files").GetArrayLength());
        Assert.Equal(3, result.GetProperty("written").GetInt32());

        JsonElement after = await http.GetFromJsonAsync<JsonElement>("/api/game/score", CancellationToken.None);
        Assert.Equal(revision, after.GetProperty("built")[0].GetProperty("revision").GetString());

        using HttpResponseMessage again = await Post("/api/game/score/build", new { score = "game/score.json" });
        JsonElement second = JsonDocument.Parse(await again.Content.ReadAsStringAsync(CancellationToken.None)).RootElement;
        Assert.Equal(revision, second.GetProperty("revision").GetString());
        Assert.Equal(0, second.GetProperty("written").GetInt32());
    }

    [Fact]
    public async Task The_manifest_and_the_stems_are_served_and_the_stems_are_bar_exact()
    {
        using HttpResponseMessage built = await Post("/api/game/score/build", new { });
        Assert.True(built.IsSuccessStatusCode, await built.Content.ReadAsStringAsync(CancellationToken.None));

        JsonElement manifest = await http.GetFromJsonAsync<JsonElement>("/api/game/manifest?id=demo-score", CancellationToken.None);
        Assert.Equal("ample-game-score", manifest.GetProperty("format").GetString());
        Assert.Equal(88200, manifest.GetProperty("loop").GetProperty("frames").GetInt32());
        Assert.Equal(120, manifest.GetProperty("tempo").GetDouble());
        Assert.Equal(2, manifest.GetProperty("provenance").GetProperty("takes").GetArrayLength());

        byte[] wav = await http.GetByteArrayAsync("/api/game/stems/demo-score.low.wav", CancellationToken.None);
        (StereoBuffer stem, int rate) = StereoWav.Decode(wav);
        Assert.Equal(44100, rate);
        Assert.Equal(88200, stem.Length);
        Assert.Contains(stem.Left, sample => sample != 0);

        using HttpRequestMessage ranged = new(HttpMethod.Get, "/api/game/stems/demo-score.low.wav");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 43);
        using HttpResponseMessage partial = await http.SendAsync(ranged, CancellationToken.None);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
    }

    [Fact]
    public async Task Asking_for_something_that_is_not_built_or_not_allowed_is_an_error_not_a_crash()
    {
        using HttpResponseMessage notBuilt = await http.GetAsync("/api/game/manifest?id=demo-score", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, notBuilt.StatusCode);
        using HttpResponseMessage missing = await http.GetAsync("/api/game/stems/nothing.wav", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, missing.StatusCode);
        using HttpResponseMessage bad = await http.GetAsync("/api/game/stems/..%2Fproject.json", CancellationToken.None);
        Assert.NotEqual(HttpStatusCode.OK, bad.StatusCode);
        using HttpResponseMessage noFile = await Post("/api/game/score/build", new { score = "game/none.json" });
        Assert.NotEqual(HttpStatusCode.OK, noFile.StatusCode);
        using HttpResponseMessage outside = await Post("/api/game/score/build", new { score = "../score.json" });
        Assert.NotEqual(HttpStatusCode.OK, outside.StatusCode);
        using HttpResponseMessage noVariant = await Post("/api/game/variant", new { score = "game/score.json", variant = "nope" });
        Assert.NotEqual(HttpStatusCode.OK, noVariant.StatusCode);
    }

    [Fact]
    public async Task A_broken_score_is_described_with_its_problems()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "game", "score.json"), """{ "version": 1, "name": "Bad" }""");
        JsonElement view = await http.GetFromJsonAsync<JsonElement>("/api/game/score", CancellationToken.None);
        Assert.False(view.GetProperty("ok").GetBoolean());
        Assert.True(view.GetProperty("problems").GetArrayLength() > 0);
    }

    [Fact]
    public async Task State_reports_are_remembered_and_never_touch_the_project()
    {
        string[] before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        using HttpResponseMessage reported = await Post("/api/game/state", new { session = "match-1", state = "tension", previous = "calm", intensity = 0.6, advantage = 0.1, signals = new { armyRatio = 1.2 } });
        Assert.True(reported.IsSuccessStatusCode);
        using HttpResponseMessage invalid = await Post("/api/game/state", new { state = "NOT VALID" });
        Assert.NotEqual(HttpStatusCode.OK, invalid.StatusCode);

        JsonElement state = await http.GetFromJsonAsync<JsonElement>("/api/game/state?count=5", CancellationToken.None);
        Assert.Equal("tension", state.GetProperty("latest").GetProperty("state").GetString());
        Assert.Equal(1, state.GetProperty("entries").GetArrayLength());
        Assert.Equal(before.Length, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length);
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("global.json not found.");
    }
}
