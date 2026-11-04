using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The language tooling of Lite: the language server over the files the page sends, and the component checks with the files in the body. plan/lite/PLAN.md, D5 and D6.</summary>
public sealed class LiteLanguageTests : IAsyncLifetime, IDisposable
{
    private const string UpComponent = """
        component koda.notes.up version 1.0.0 current {
          id "demo.koda.test.up"
          input source: Notes
          output notes: Notes
          process {
            emit source |> transpose(12)
          }
        }
        """;

    private const string UsingSong = """
        composition "t" { ample 1  tempo 120  master-seed 1 }
        import "components/up.amplec"

        midi-clip m at 1 for 1 {
          notes 1:0:1:60:100
        }

        koda.notes.up higher at 1 for 1 {
          from m.notes -> source
        }
        """;

    private const string CallingComponent = """
        import "components/up.amplec"

        component koda.notes.twice version 1.0.0 current {
          id "demo.koda.test.twice"
          input source: Notes
          output notes: Notes
          process {
            emit source |> merge(koda.notes.up(source))
          }
        }
        """;

    private string folder = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-litelang-" + Guid.NewGuid().ToString("N"));
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
        }

        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library"));
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-Ample-Token", server.Token);
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Dispose();
        await server.DisposeAsync();
        foreach (string path in new[] { folder, folder + "-library" })
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

    private Uri SocketUrl => new(server.Url.Replace("http", "ws", StringComparison.Ordinal) + "/lsp/remix");

    private static async Task Send(ClientWebSocket socket, object message) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        byte[] buffer = new byte[1 << 20];
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(20));
        WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, limit.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement.Clone();
    }

    /// <summary>Opens the socket, sends the snapshot of the files (when given) and opens the song; returns the diagnostics that come back.</summary>
    private async Task<(ClientWebSocket Socket, JsonElement Diagnostics)> OpenSong(bool sendFiles)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("X-Ample-Token", server.Token);
        await socket.ConnectAsync(SocketUrl, CancellationToken.None);
        await Send(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        await Read(socket);
        if (sendFiles) await Send(socket, new { jsonrpc = "2.0", method = "ample/lite/files", @params = new { files = new[] { new { path = "components/up.amplec", text = UpComponent } } } });
        await Send(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri = "lite:///song.amplet", languageId = "amplet", version = 1, text = UsingSong } } });
        for (; ; )
        {
            JsonElement message = await Read(socket);
            if (message.TryGetProperty("method", out JsonElement method) && method.GetString() == "textDocument/publishDiagnostics") return (socket, message.GetProperty("params").GetProperty("diagnostics"));
        }
    }

    [Fact]
    public async Task The_language_server_of_lite_reads_the_components_the_page_sent()
    {
        (ClientWebSocket socket, JsonElement diagnostics) = await OpenSong(true);
        using (socket)
        {
            Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("severity").GetInt32() == 1);
            await Send(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/hover", @params = new { textDocument = new { uri = "lite:///song.amplet" }, position = new { line = 7, character = 5 } } });
            for (; ; )
            {
                JsonElement message = await Read(socket);
                if (message.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 2)
                {
                    Assert.False(message.TryGetProperty("error", out _));   // answered (a hover text or nothing), not refused
                    break;
                }
            }
        }
    }

    [Fact]
    public async Task Without_the_files_the_import_cannot_be_found_and_no_file_of_the_server_is_read_instead()
    {
        (ClientWebSocket socket, JsonElement diagnostics) = await OpenSong(false);
        using (socket) Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "AMP2216");
    }

    [Fact]
    public async Task The_language_socket_needs_the_token()
    {
        using ClientWebSocket anonymous = new();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.ConnectAsync(SocketUrl, CancellationToken.None));
    }

    [Fact]
    public async Task The_component_checks_of_lite_use_the_files_in_the_body()
    {
        using HttpResponseMessage with = await http.PostAsJsonAsync("/api/remix/components/check", new { files = new[] { new { path = "components/up.amplec", text = UpComponent } }, text = CallingComponent });
        JsonElement diagnostics = (await with.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnostics");
        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("severity").GetInt32() == 1);
        using HttpResponseMessage without = await http.PostAsJsonAsync("/api/remix/components/check", new { files = Array.Empty<object>(), text = CallingComponent });
        JsonElement missing = (await without.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnostics");
        Assert.Contains(missing.EnumerateArray(), d => d.GetProperty("code").GetString() == "AMP2248");   // the called component is not in sight
        using HttpResponseMessage escape = await http.PostAsJsonAsync("/api/remix/components/check", new { files = new[] { new { path = "../up.amplec", text = UpComponent } }, text = CallingComponent });
        JsonElement stillMissing = (await escape.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnostics");
        Assert.Contains(stillMissing.EnumerateArray(), d => d.GetProperty("code").GetString() == "AMP2248");
        using HttpResponseMessage complete = await http.PostAsJsonAsync("/api/remix/components/complete", new { files = Array.Empty<object>(), text = "component koda.x.y version 1.0.0 {\n  #[", line = 1, character = 4 });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        JsonElement items = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
    }
}
