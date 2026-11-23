using System.Net;
using System.Text;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

public sealed class RemoteClientTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    private static RemoteOptions Direct(string url, string? password) => new()
    {
        Url = url,
        Mode = RemoteMode.Direct,
        User = "koda",
        PasswordProvider = () => password,
        Backoff = [TimeSpan.FromMilliseconds(5)],
    };

    [Fact]
    public async Task It_signs_in_to_a_server_with_a_login_and_reads_the_project_files()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ample-remote-" + Guid.NewGuid().ToString("N"));
        string home = folder + "-home";
        using (Ample.Storage.Projects.OpenProject p = await Ample.Storage.Projects.ProjectStore.CreateAsync(
            new Ample.Audio.IO.PhysicalFileSystem(), new Ample.Audio.IO.SystemClock(), new Ample.Audio.IO.GuidV7Generator(), folder, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project demo\n");
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient good = new(Direct(server.Url, "a-long-password"));
            string files = await good.GetStringAsync("/api/project/files");
            Assert.Contains("song.amplet", files, StringComparison.Ordinal);

            using RemoteClient wrong = new(Direct(server.Url, "not-the-password"));
            await Assert.ThrowsAsync<RemoteAuthException>(() => wrong.GetStringAsync("/api/project/files"));

            using RemoteClient none = new(Direct(server.Url, null));
            await Assert.ThrowsAsync<RemoteAuthException>(() => none.GetStringAsync("/api/project/files"));
        }
        finally
        {
            try { Directory.Delete(folder, true); Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }

    private sealed class Script(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath + (request.Headers.Contains("X-Ample-Token") ? " token=" + request.Headers.GetValues("X-Ample-Token").First() : string.Empty));
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task An_expired_session_is_signed_in_again_and_the_request_goes_through()
    {
        int logins = 0;
        Script script = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login") return Json(HttpStatusCode.OK, "{\"token\":\"t" + ++logins + "\"}");
            return request.Headers.GetValues("X-Ample-Token").First() == "t1" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(HttpStatusCode.OK, "[]");
        });
        using RemoteClient client = new(Direct("http://server.test", "pw"), script);
        string messages = string.Empty;
        client.Status += m => messages += m + "|";
        Assert.Equal("[]", await client.GetStringAsync("/api/project/files"));
        Assert.Equal(2, logins);
        Assert.Contains("signing in again", messages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_lost_connection_is_retried_with_a_message_and_then_works()
    {
        int failures = 2;
        Script script = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login") return Json(HttpStatusCode.OK, "{\"token\":\"t\"}");
            if (failures-- > 0) throw new HttpRequestException("connection reset");
            return Json(HttpStatusCode.OK, "[1]");
        });
        using RemoteClient client = new(Direct("http://server.test", "pw"), script);
        List<string> messages = [];
        client.Status += messages.Add;
        Assert.Equal("[1]", await client.GetStringAsync("/api/project/files"));
        Assert.Equal(2, messages.Count(m => m.Contains("lost", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_server_that_stays_away_gives_a_clear_error_after_the_attempts()
    {
        Script script = new(request => throw new HttpRequestException("no route to host"));
        RemoteOptions options = Direct("http://server.test", "pw");
        options.Attempts = 3;
        using RemoteClient client = new(options, script);
        RemoteConnectionException error = await Assert.ThrowsAsync<RemoteConnectionException>(() => client.GetStringAsync("/api/project/files"));
        Assert.Contains("3 attempts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connecting_to_a_server_that_is_away_retries_with_messages_before_it_gives_up()
    {
        Script script = new(request => throw new HttpRequestException("no route to host"));
        RemoteOptions options = Direct("http://server.test", "pw");
        options.Attempts = 3;
        using RemoteClient client = new(options, script);
        List<string> messages = [];
        client.Status += messages.Add;
        await Assert.ThrowsAsync<RemoteConnectionException>(() => client.ConnectAsync());
        Assert.Equal(3, messages.Count(m => m.Contains("trying again", StringComparison.OrdinalIgnoreCase)));
    }
}
