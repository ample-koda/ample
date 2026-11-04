using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>The public Lite (<c>--lite-only</c>): only Lite exists, nobody signs in, the public limits hold with a friendly "try again in n seconds", and the worker of an isolated build speaks its protocol.</summary>
public sealed class LiteOnlyTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] SongFiles = ["song.amplet"];

    private const string Song = """
        composition "t" { ample 1  tempo 120  master-seed 1 }

        midi-clip m at 1 for 1 {
          notes 1:0:1:60:100 1:1:1:64:100
        }

        synth s at 1 for 1 {
          from m.notes -> notes
        }

        master out {
          from s.audio -> audio
        }
        """;

    private string folder = string.Empty;
    private string home = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-liteonly-" + Guid.NewGuid().ToString("N"));
        home = folder + "-library";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "none", null, CancellationToken.None))
        {
        }

        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: home) { LiteOnly = true });
        http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(server.Url) };   // no token, no sign-in
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
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

    private static MultipartFormDataContent Form() => new()
    {
        { new StringContent(JsonSerializer.Serialize(new { entry = "song.amplet", files = SongFiles })), "manifest" },
        { new ByteArrayContent(Encoding.UTF8.GetBytes(Song)), "f0", "file" },
    };

    [Fact]
    public async Task The_front_page_is_Lite_itself_and_needs_no_token()
    {
        using HttpResponseMessage page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("lite.js", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/remix/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/styles/tokens.css")).StatusCode);
    }

    [Theory]
    [InlineData("/api/project")]
    [InlineData("/api/ping")]
    [InlineData("/api/projects")]
    [InlineData("/help")]
    [InlineData("/lsp")]
    [InlineData("/login")]
    [InlineData("/index.html")]
    [InlineData("/app/main.js")]
    [InlineData("/remix/test/lite-store.test.mjs")]
    public async Task Everything_that_is_not_Lite_is_a_404(string path)
    {
        using HttpResponseMessage response = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_module_that_the_Lite_page_imports_is_served()
    {
        string web = Path.Combine(FindAmpleRoot(), "web");
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> todo = new();
        todo.Enqueue("/remix/lite.js");
        while (todo.TryDequeue(out string? url))
        {
            if (!seen.Add(url)) continue;
            using HttpResponseMessage response = await http.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, url + " answered " + (int)response.StatusCode);
            if (!url.EndsWith(".js", StringComparison.Ordinal) && !url.EndsWith(".mjs", StringComparison.Ordinal)) continue;
            string source = await File.ReadAllTextAsync(Path.Combine(web, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"(?:from|import)\s*\(?\s*'(\.[^']*)'"))
            {
                string next = new Uri(new Uri("http://x" + url), m.Groups[1].Value).AbsolutePath;
                todo.Enqueue(next);
            }
        }

        Assert.Contains("/lib/notes-model.mjs", seen);
    }

    [Fact]
    public void Lite_does_not_know_a_block_that_needs_a_plugin()
    {
        string root = FindAmpleRoot();
        Ample.Language.SpecData spec = Ample.Docs.SpecRoot.At(root).LoadSpecData();
        Ample.Language.SpecData lite = spec.WithoutTemplates(LitePolicy.IsForbidden);
        foreach (string id in new[] { "ace-music", "ai-voice", "choir", "voice-clone", "scorer", "lyrics", "mood-scene" })
        {
            Assert.True(spec.Templates.ContainsKey(id), id + " exists in Studio");
            Assert.False(lite.Templates.ContainsKey(id), id + " must not exist in Lite");
        }

        Assert.DoesNotContain(lite.Templates.Values, t => t.Plugin is not null);
        Assert.Contains("synth", lite.Templates.Keys);
        Assert.Contains("gm", lite.Templates.Keys);
    }

    [Fact]
    public async Task The_favicon_is_an_empty_answer_not_a_404()
    {
        using HttpResponseMessage response = await http.GetAsync("/favicon.ico");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public void One_browser_holds_three_sockets_and_one_address_three_times_as_many()
    {
        using LiteGovernor governor = new(LiteLimits.Public);
        List<IDisposable> held = [];
        for (int i = 0; i < 3; i++) held.Add(governor.EnterSocket(["c:a", "ip:1"], 3));
        Assert.Throws<LiteRejectedException>(() => governor.EnterSocket(["c:a", "ip:1"], 3));
        for (int i = 0; i < 6; i++) held.Add(governor.EnterSocket(["c:b" + i, "ip:1"], 3));
        Assert.Throws<LiteRejectedException>(() => governor.EnterSocket(["c:late", "ip:1"], 3));
        foreach (IDisposable slot in held) slot.Dispose();
        governor.EnterSocket(["c:late", "ip:1"], 3).Dispose();
    }

    [Fact]
    public async Task The_limits_say_public_and_are_the_public_profile()
    {
        JsonElement limits = JsonDocument.Parse(await http.GetStringAsync("/api/remix/limits")).RootElement;
        Assert.True(limits.GetProperty("public").GetBoolean());
        Assert.Equal(4, limits.GetProperty("buildsPerMinute").GetInt32());
        Assert.Equal(LiteLimits.Public.MaxProjectBytes, limits.GetProperty("maxProjectBytes").GetInt64());
    }

    [Fact]
    public async Task Every_answer_carries_the_security_headers()
    {
        using HttpResponseMessage page = await http.GetAsync("/");
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_foreign_origin_is_refused()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/remix/build") { Content = Form() };
        request.Headers.Add("Origin", "https://evil.example");
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_fifth_build_in_a_minute_is_refused_and_says_how_long_to_wait()
    {
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage ok = await http.PostAsync("/api/remix/build", Form());
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using HttpResponseMessage refused = await http.PostAsync("/api/remix/build", Form());
        Assert.Equal((HttpStatusCode)429, refused.StatusCode);
        Assert.InRange(int.Parse(refused.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
        JsonElement error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("AMPL932", error.GetProperty("code").GetString());
        Assert.InRange(error.GetProperty("retryAfter").GetInt32(), 1, 60);
        Assert.Contains("Try again in", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_of_an_isolated_build_reads_a_request_builds_it_and_writes_the_wav()
    {
        using MemoryStream request = new();
        await LiteWorker.WriteRequestAsync(request, "song.amplet", [new LiteFile("song.amplet", Encoding.UTF8.GetBytes(Song))], LiteLimits.Public, CancellationToken.None);
        string work = Path.Combine(Path.GetTempPath(), "ample-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using MemoryStream output = new();
            int code = await LiteWorker.RunAsync(new MemoryStream(request.ToArray()), output, FindAmpleRoot(), work, CancellationToken.None);
            Assert.Equal(0, code);
            string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(lines, l => l.StartsWith("{\"t\":\"log\"", StringComparison.Ordinal));
            JsonElement last = JsonDocument.Parse(lines[^1]).RootElement;
            Assert.Equal("result", last.GetProperty("t").GetString());
            Assert.True(last.GetProperty("result").GetProperty("ok").GetBoolean());
            Assert.True(File.Exists(Path.Combine(work, "out.wav")));
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task The_worker_refuses_a_path_that_leaves_the_project_with_a_line_and_no_audio()
    {
        using MemoryStream request = new();
        await LiteWorker.WriteRequestAsync(request, "../song.amplet", [new LiteFile("../song.amplet", Encoding.UTF8.GetBytes(Song))], LiteLimits.Public, CancellationToken.None);
        string work = Path.Combine(Path.GetTempPath(), "ample-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using MemoryStream output = new();
            await LiteWorker.RunAsync(new MemoryStream(request.ToArray()), output, FindAmpleRoot(), work, CancellationToken.None);
            JsonElement last = JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement;
            Assert.Equal("error", last.GetProperty("t").GetString());
            Assert.Equal("AMPL909", last.GetProperty("code").GetString());
            Assert.False(File.Exists(Path.Combine(work, "out.wav")));
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }
}
