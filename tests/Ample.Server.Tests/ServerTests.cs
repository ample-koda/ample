using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

public sealed class ServerTests : IAsyncLifetime, IDisposable
{
    private const string Song = "project demo\n";
    private string folder = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;
    private bool serverDisposed;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-server-" + Guid.NewGuid().ToString("N"));
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), Song);
        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        if (!serverDisposed) await server.DisposeAsync();
        try
        {
            Directory.Delete(folder, true);
            Directory.Delete(folder + "-library", true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Api_without_token_is_refused()
    {
        using HttpClient bare = new() { BaseAddress = new Uri(server.Url) };
        HttpResponseMessage r = await bare.GetAsync("/api/project", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("AMP5284", await r.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assistant_session_conversation_archive_defaults_off_and_can_be_enabled()
    {
        using HttpResponseMessage created = await http.PostAsJsonAsync("/api/ai/sessions", new { title = "Redux privacy" }, CancellationToken.None);
        created.EnsureSuccessStatusCode();
        JsonElement session = await created.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        string id = session.GetProperty("id").GetString()!;
        Assert.False(session.GetProperty("archiveConversation").GetBoolean());

        using HttpResponseMessage updated = await http.PatchAsJsonAsync($"/api/ai/sessions/{id}", new { archiveConversation = true, includeEarlier = false }, CancellationToken.None);
        updated.EnsureSuccessStatusCode();
        JsonElement archived = await updated.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.True(archived.GetProperty("archiveConversation").GetBoolean());
    }

    [Fact]
    public async Task Instance_ping_is_public_and_returns_only_liveness_and_project_id()
    {
        using HttpClient bare = new() { BaseAddress = new Uri(server.Url) };

        JsonElement ping = await bare.GetFromJsonAsync<JsonElement>("/api/ping", CancellationToken.None);

        Assert.True(ping.GetProperty("ok").GetBoolean());
        using JsonDocument project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ".ample", "project.json")));
        Assert.Equal(project.RootElement.GetProperty("id").GetString(), ping.GetProperty("id").GetString());
        Assert.Equal(2, ping.EnumerateObject().Count());
    }

    [Fact]
    public async Task Server_registration_keeps_token_in_the_launch_file_and_cleans_up()
    {
        using JsonDocument project = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ".ample", "project.json")));
        string id = project.RootElement.GetProperty("id").GetString()!;
        string instances = Path.Combine(folder + "-library", "instances");
        string record = Path.Combine(instances, id + ".json");
        string launch = Path.Combine(instances, id + ".launch");
        string metadata = await File.ReadAllTextAsync(record);

        Assert.Contains("\"port\"", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Token, metadata, StringComparison.Ordinal);
        Assert.Contains(server.Token, await File.ReadAllTextAsync(launch), StringComparison.Ordinal);
        await server.DisposeAsync();
        serverDisposed = true;
        Assert.False(File.Exists(record));
        Assert.False(File.Exists(launch));
    }

    [Fact]
    public async Task Graceful_shutdown_requires_the_session_token()
    {
        using HttpClient bare = new() { BaseAddress = new Uri(server.Url) };

        HttpResponseMessage response = await bare.PostAsync("/api/server/shutdown", null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Server_remains_available_when_the_instance_registry_path_is_not_a_directory()
    {
        string separateProject = Path.Combine(Path.GetTempPath(), "ample-no-registry-" + Guid.NewGuid().ToString("N"));
        string separateHome = separateProject + "-library";
        Directory.CreateDirectory(separateHome);
        File.WriteAllText(Path.Combine(separateHome, "instances"), "blocks registry folder");
        using (OpenProject created = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), separateProject, "demo", null, CancellationToken.None))
        {
        }

        try
        {
            await using RunningServer unregistered = await AmpleServer.StartAsync(new ServerOptions(separateProject, FindAmpleRoot(), LibraryHome: separateHome));
            Assert.False(unregistered.IsRegistered);
            using HttpClient bare = new() { BaseAddress = new Uri(unregistered.Url) };
            Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/ping", CancellationToken.None)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(separateProject)) Directory.Delete(separateProject, true);
            if (Directory.Exists(separateHome)) Directory.Delete(separateHome, true);
        }
    }

    [Fact]
    public async Task Wrong_token_is_refused()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/project");
        req.Headers.Add("X-Ample-Token", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Foreign_host_header_is_refused()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/project");
        req.Headers.Host = "evil.example";
        HttpResponseMessage r = await http.SendAsync(req, CancellationToken.None);
        Assert.Equal((HttpStatusCode)421, r.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_refused()
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/project");
        req.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Project_is_served_with_security_headers()
    {
        HttpResponseMessage r = await http.GetAsync("/api/project", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        using StringContent big = new(JsonSerializer.Serialize(new { path = "song.amplet", text = new string('x', 9 * 1024 * 1024), baseVersion = 0 }), Encoding.UTF8, "application/json");
        try
        {
            HttpResponseMessage r = await http.PutAsync("/api/doc", big, CancellationToken.None);
            Assert.True((int)r.StatusCode >= 400);
        }
        catch (HttpRequestException)
        {
            // The server may close the connection while the client is still sending: that is a refusal too.
        }
    }

    [Fact]
    public async Task Path_outside_the_project_is_refused()
    {
        HttpResponseMessage r = await http.GetAsync("/api/doc?path=../outside.amplet", CancellationToken.None);
        Assert.True((int)r.StatusCode >= 400);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Document_edits_use_optimistic_concurrency()
    {
        JsonElement doc = await http.GetFromJsonAsync<JsonElement>("/api/doc?path=song.amplet", CancellationToken.None);
        int version = doc.GetProperty("version").GetInt32();
        HttpResponseMessage ok = await http.PutAsJsonAsync("/api/doc", new { path = "song.amplet", text = Song + "\n", baseVersion = version }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        HttpResponseMessage stale = await http.PutAsJsonAsync("/api/doc", new { path = "song.amplet", text = Song, baseVersion = version }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("AMP5280", await stale.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Templates_come_from_the_spec_with_their_category()
    {
        JsonElement t = await http.GetFromJsonAsync<JsonElement>("/api/templates", CancellationToken.None);
        JsonElement drums = t.GetProperty("templates").EnumerateArray().First(x => x.GetProperty("id").GetString() == "drums");
        Assert.Equal("Instruments", drums.GetProperty("category").GetString());
        Assert.True(drums.GetProperty("inputs").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Layout_and_flag_edits_change_the_text_and_the_view()
    {
        JsonElement doc = await http.GetFromJsonAsync<JsonElement>("/api/doc?path=song.amplet", CancellationToken.None);
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/doc/edit", new { path = "song.amplet", baseVersion = doc.GetProperty("version").GetInt32(), op = new { type = "addBlock", blockType = "drums", name = "d" } }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        int v = (await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("version").GetInt32();
        r = await http.PostAsJsonAsync("/api/doc/edit", new { path = "song.amplet", baseVersion = v, op = new { type = "setLayout", block = "d", x = 120, y = 40 } }, CancellationToken.None);
        JsonElement view = await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        JsonElement block = view.GetProperty("blocks").EnumerateArray().First(b => b.GetProperty("name").GetString() == "d");
        Assert.Equal(120, block.GetProperty("layout").GetProperty("x").GetInt32());
        Assert.Contains("layout d 120 40", view.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arrangement_parts_change_through_the_versioned_document_edit()
    {
        JsonElement doc = await http.GetFromJsonAsync<JsonElement>("/api/doc?path=song.amplet", CancellationToken.None);
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/doc/edit", new { path = "song.amplet", baseVersion = doc.GetProperty("version").GetInt32(), op = new { type = "addBlock", blockType = "drums", name = "d" } }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        doc = await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        r = await http.PostAsJsonAsync("/api/doc/edit", new { path = "song.amplet", baseVersion = doc.GetProperty("version").GetInt32(), op = new { type = "setParts", block = "d", parts = new[] { new { at = 3, length = 4, name = "intro" } } } }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement view = await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal(3, view.GetProperty("blocks").EnumerateArray().First(b => b.GetProperty("name").GetString() == "d").GetProperty("parts")[0].GetProperty("at").GetInt32());
        Assert.Contains("at 3 for 4 as intro", view.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_history_graph_and_a_state_that_is_only_looked_at_are_served_without_changing_anything()
    {
        JsonElement g = await http.GetFromJsonAsync<JsonElement>("/api/history/graph", CancellationToken.None);
        Assert.True(g.GetProperty("nodes").GetArrayLength() >= 1);
        Assert.Contains(g.GetProperty("lines").EnumerateArray(), l => l.GetProperty("name").GetString() == "studio");
        string node = g.GetProperty("nodes")[0].GetProperty("nodeId").GetString()!;
        JsonElement peek = await http.GetFromJsonAsync<JsonElement>("/api/history/peek?ref=" + Uri.EscapeDataString(node), CancellationToken.None);
        Assert.Equal(JsonValueKind.Object, peek.ValueKind);
        JsonElement after = await http.GetFromJsonAsync<JsonElement>("/api/history/graph", CancellationToken.None);
        Assert.Equal(g.GetRawText(), after.GetRawText());
        HttpResponseMessage parsed = await http.PostAsJsonAsync("/api/doc/parse", new { text = Song }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
    }

    [Fact]
    public async Task The_block_view_lists_seeds_as_strings_and_the_freeze_edit_changes_the_level()
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "voice.amplet"), "composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\nmaster master {\n}\n");
        JsonElement doc = await http.GetFromJsonAsync<JsonElement>("/api/doc?path=voice.amplet", CancellationToken.None);
        JsonElement block = await http.GetFromJsonAsync<JsonElement>("/api/control/block?path=voice.amplet&block=master", CancellationToken.None);
        Assert.Equal("free", block.GetProperty("level").GetString());
        Assert.Equal(JsonValueKind.String, block.GetProperty("seeds")[0].GetProperty("state").GetProperty("value").ValueKind);
        Assert.True(block.GetProperty("seeds")[0].GetProperty("state").GetProperty("locked").GetBoolean());
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/doc/edit", new { path = "voice.amplet", baseVersion = doc.GetProperty("version").GetInt32(), op = new { type = "setFreeze", block = "master", level = "plan" } }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("freeze plan {", (await r.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("text").GetString(), StringComparison.Ordinal);
        JsonElement after = await http.GetFromJsonAsync<JsonElement>("/api/control/block?path=voice.amplet&block=master", CancellationToken.None);
        Assert.Equal("plan", after.GetProperty("level").GetString());
    }

    [Fact]
    public async Task The_language_server_is_reachable_over_a_websocket_and_needs_the_token()
    {
        using System.Net.WebSockets.ClientWebSocket none = new();
        Uri uri = new(server.Url.Replace("http://", "ws://", StringComparison.Ordinal) + "/lsp");
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => none.ConnectAsync(uri, CancellationToken.None));

        using System.Net.WebSockets.ClientWebSocket ws = new();
        ws.Options.SetRequestHeader("Cookie", "ample-token=" + server.Token);
        await ws.ConnectAsync(uri, CancellationToken.None);
        async Task SendAsync(object o) => await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        async Task<JsonElement> ReceiveAsync()
        {
            byte[] buf = new byte[64 * 1024];
            System.Net.WebSockets.WebSocketReceiveResult r = await ws.ReceiveAsync(buf, CancellationToken.None);
            return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement.Clone();
        }

        await SendAsync(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        JsonElement init = await ReceiveAsync();
        Assert.True(init.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        await SendAsync(new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri = "file:///a.amplet", languageId = "amplet", version = 1, text = "midi-clip c {\n}\n" } } });
        JsonElement diag = await ReceiveAsync();
        Assert.Equal("textDocument/publishDiagnostics", diag.GetProperty("method").GetString());
        Assert.Contains(diag.GetProperty("params").GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("code").GetString() == "AMP1001");
    }

    [Fact]
    public async Task The_library_links_scans_resolves_and_unlinks_only_in_two_steps()
    {
        string kits = Path.Combine(folder, "kits");
        Directory.CreateDirectory(Path.Combine(kits, "tape"));
        await File.WriteAllTextAsync(Path.Combine(kits, "tape", "kick.wav"), "kick", CancellationToken.None);
        JsonElement none = await http.GetFromJsonAsync<JsonElement>("/api/library", CancellationToken.None);
        Assert.Equal(0, none.GetProperty("mounts").GetArrayLength());

        HttpResponseMessage linked = await http.PostAsJsonAsync("/api/library/mounts", new { name = "Kits", path = kits }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        JsonElement view = (await linked.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("library");
        Assert.Equal("online", view.GetProperty("mounts")[0].GetProperty("state").GetString());
        Assert.True(view.GetProperty("mounts")[0].GetProperty("readOnly").GetBoolean());
        Assert.Equal("tape/kick", view.GetProperty("assets")[0].GetProperty("name").GetString());
        string mountId = view.GetProperty("mounts")[0].GetProperty("id").GetString()!;

        HttpResponseMessage missing = await http.PostAsJsonAsync("/api/library/mounts", new { path = Path.Combine(folder, "nowhere") }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("AMP5290", await missing.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);

        JsonElement lockState = await http.GetFromJsonAsync<JsonElement>("/api/lock", CancellationToken.None);
        Assert.False(lockState.GetProperty("exists").GetBoolean());

        HttpResponseMessage dry = await http.PostAsJsonAsync("/api/acts/unlink-mount", new { mount = mountId }, CancellationToken.None);
        JsonElement plan = await dry.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Equal("unlink Kits", plan.GetProperty("acceptance").GetString());
        Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("/api/library", CancellationToken.None)).GetProperty("mounts").GetArrayLength());
        HttpResponseMessage wrong = await http.PostAsJsonAsync("/api/acts/confirm", new { token = plan.GetProperty("token").GetString(), typed = "nope", reason = "x" }, CancellationToken.None);
        Assert.True((int)wrong.StatusCode >= 400);
        HttpResponseMessage ok = await http.PostAsJsonAsync("/api/acts/confirm", new { token = plan.GetProperty("token").GetString(), typed = "unlink Kits", reason = "test" }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("/api/library", CancellationToken.None)).GetProperty("mounts").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(kits, "tape", "kick.wav")));
    }

    [Fact]
    public async Task Backups_are_made_verified_remembered_in_the_health_view_and_pruned_only_in_two_steps()
    {
        string target = folder + "-backups";
        try
        {
            JsonElement none = await http.GetFromJsonAsync<JsonElement>("/api/backup", CancellationToken.None);
            Assert.Equal(JsonValueKind.Null, none.GetProperty("status").ValueKind);
            Assert.Contains("no backup", none.GetProperty("suggestion").GetString(), StringComparison.Ordinal);

            HttpResponseMessage created = await http.PostAsJsonAsync("/api/backup/create", new { target }, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            string id = (await created.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("id").GetString()!;
            JsonElement status = await http.GetFromJsonAsync<JsonElement>("/api/backup", CancellationToken.None);
            Assert.Equal(id, status.GetProperty("status").GetProperty("id").GetString());
            Assert.Single(status.GetProperty("backups").EnumerateArray());
            JsonElement health = await http.GetFromJsonAsync<JsonElement>("/api/health", CancellationToken.None);
            Assert.Equal(id, health.GetProperty("backup").GetProperty("id").GetString());

            JsonElement verified = await (await http.PostAsJsonAsync("/api/backup/verify", new { id }, CancellationToken.None)).Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.True(verified.GetProperty("ok").GetBoolean());

            await http.PostAsJsonAsync("/api/backup/create", new { target }, CancellationToken.None);
            HttpResponseMessage dry = await http.PostAsJsonAsync("/api/acts/prune-backups", new { target, keepLast = 1 }, CancellationToken.None);
            JsonElement plan = await dry.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.StartsWith("prune ", plan.GetProperty("acceptance").GetString(), StringComparison.Ordinal);
            HttpResponseMessage wrong = await http.PostAsJsonAsync("/api/acts/confirm", new { token = plan.GetProperty("token").GetString(), typed = "yes", reason = "x" }, CancellationToken.None);
            Assert.True((int)wrong.StatusCode >= 400);
            Assert.True((await http.GetFromJsonAsync<JsonElement>("/api/backup", CancellationToken.None)).GetProperty("backups").GetArrayLength() >= 1);

            string archive = folder + "-export.ample";
            HttpResponseMessage exported = await http.PostAsJsonAsync("/api/export", new { file = archive, includeTakes = false }, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            Assert.True(File.Exists(archive));
            File.Delete(archive);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
    }

    [Fact]
    public async Task The_help_and_the_web_app_are_served()
    {
        HttpResponseMessage help = await http.GetAsync("/help", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, help.StatusCode);
        Assert.Contains("text/html", help.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        string index = await http.GetStringAsync("/", CancellationToken.None);
        Assert.Contains("id=\"cv\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dangerous_act_cannot_be_confirmed_without_a_dry_run()
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/acts/confirm", new { token = "made-up", typed = "x", reason = "y" }, CancellationToken.None);
        Assert.True((int)r.StatusCode >= 400);
    }

    [Fact]
    public void Every_catalog_operation_is_documented_and_unique()
    {
        string doc = OpenApi.Build();
        foreach (ApiOperation op in Routes.Catalog)
        {
            Assert.Contains($"\"{op.Path}\"", doc, StringComparison.Ordinal);
        }

        Assert.Equal(Routes.Catalog.Count, Routes.Catalog.Select(o => o.Method + " " + o.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Committed_openapi_matches_the_routes()
    {
        string committed = File.ReadAllText(Path.Combine(FindAmpleRoot(), "docs", "api", "openapi.json")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(OpenApi.Build(), committed);
    }

    /// <summary>
    /// A task token may read (GET) and act (POST) on every route under /api/ai/context: the gate is the prefix and the token, not a list of paths that has to be kept in step with the routes
    /// (a route that was not on the list answered every call of the assistant with a misleading 401). The routes that exist are all POST or GET under that prefix.
    /// </summary>
    [Fact]
    public void Every_ai_context_route_is_open_to_a_task_token_by_prefix_and_no_path_list_is_kept()
    {
        string source = File.ReadAllText(Path.Combine(FindAmpleRoot(), "src", "Ample.Server", "AmpleServer.cs"));
        Assert.Contains("(context.Request.Method == HttpMethods.Get || context.Request.Method == HttpMethods.Post) && context.Request.Path.StartsWithSegments(\"/api/ai/context\"", source, StringComparison.Ordinal);
        foreach (ApiOperation op in Routes.Catalog.Where(o => o.Path.StartsWith("/api/ai/context/", StringComparison.Ordinal)))
        {
            Assert.True(op.Method is "GET" or "POST");   // a task token cannot use another method
            Assert.DoesNotContain($"\"{op.Path}\"", source, StringComparison.Ordinal);   // no per-path list that can fall out of step
        }
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }
}
