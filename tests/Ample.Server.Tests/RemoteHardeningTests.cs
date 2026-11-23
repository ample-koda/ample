using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

/// <summary>
/// The remote client and the sync must not trust the server or a cache folder that someone else made: no file outside the cache, no replacing the
/// files that name the program to run or the SSH target, no argument for ssh in the target, no password over plain http.
/// </summary>
public sealed class RemoteHardeningTests
{
    private sealed class FakeServer(Dictionary<string, byte[]> files, Dictionary<string, byte[]>? lies = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/login") return Task.FromResult(Text("{\"token\":\"t\"}"));
            if (path == "/api/project/files")
            {
                static string Entry(KeyValuePair<string, byte[]> f) =>
                    "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(f.Key) + ",\"size\":" + f.Value.Length + ",\"sha256\":\"" + Hash(f.Value) + "\"}";
                string list = string.Join(",", files.Select(Entry));
                return Task.FromResult(Text("[" + list + "]"));
            }

            if (path == "/api/project/export")
            {
                string name = WebUtility.UrlDecode(request.RequestUri.Query.Split("path=")[1]);
                byte[]? sent = lies is not null && lies.TryGetValue(name, out byte[]? other) ? other : files.GetValueOrDefault(name);
                return Task.FromResult(sent is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sent) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RemoteClient ClientFor(HttpMessageHandler handler) => new(
        new RemoteOptions { Url = "https://server.test", Mode = RemoteMode.Direct, User = "koda", PasswordProvider = () => "pw", Backoff = [TimeSpan.FromMilliseconds(5)] }, handler);

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "ample-harden-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_pull_never_writes_outside_the_cache_or_over_the_files_the_extension_keeps_for_itself()
    {
        string parent = NewFolder();
        string cache = Path.Combine(parent, "cache");
        Directory.CreateDirectory(cache);
        try
        {
            byte[] bad = Encoding.UTF8.GetBytes("evil");
            string[] refused =
            [
                "../evil.txt", "a/../../evil.txt", "..\\evil2.txt", "C:/Windows/evil3.txt", "/abs/evil4.txt", "d:evil5.txt", "file.txt:stream",
                ".amplerm/remote.txt", ".amplerm/manifest.txt", ".hidden/x.txt", "Ample.targets", "demo.ampleproj", "demo.ampleproj.user", "Demo.SLN",
                "_conflicts/x.txt", "dot./x.txt", "space /x.txt", "a//b.txt", "",
            ];
            Dictionary<string, byte[]> files = refused.Distinct().ToDictionary(p => p, _ => bad);
            files["ok/fine.txt"] = Encoding.UTF8.GetBytes("fine");
            using RemoteClient client = ClientFor(new FakeServer(files));
            SyncResult result = await new RemoteSync(client, cache).PullAsync();

            Assert.Equal(["ok/fine.txt"], result.Fetched);
            Assert.Equal(refused.Distinct().Order(StringComparer.Ordinal), result.Refused.Order(StringComparer.Ordinal));
            Assert.Equal("fine", await File.ReadAllTextAsync(Path.Combine(cache, "ok", "fine.txt")));
            Assert.Equal([".amplerm", "ok"], Directory.GetDirectories(cache).Order(StringComparer.Ordinal).Select(d => Path.GetFileName(d) ?? string.Empty).ToArray());
            Assert.Equal(["manifest.txt"], Directory.GetFiles(Path.Combine(cache, ".amplerm")).Select(d => Path.GetFileName(d) ?? string.Empty).ToArray());
            Assert.Equal(["cache"], Directory.GetFileSystemEntries(parent).Select(d => Path.GetFileName(d) ?? string.Empty).ToArray());
            Assert.Empty(Directory.GetFiles(cache));
        }
        finally
        {
            try { Directory.Delete(parent, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_file_whose_bytes_do_not_match_the_listed_hash_is_refused_and_not_written()
    {
        string cache = NewFolder();
        Directory.CreateDirectory(cache);
        try
        {
            using RemoteClient client = ClientFor(new FakeServer(
                new Dictionary<string, byte[]> { ["a.txt"] = Encoding.UTF8.GetBytes("listed") },
                new Dictionary<string, byte[]> { ["a.txt"] = Encoding.UTF8.GetBytes("something else") }));
            SyncResult result = await new RemoteSync(client, cache).PullAsync();

            Assert.Equal(["a.txt"], result.Refused);
            Assert.Empty(result.Fetched);
            Assert.False(File.Exists(Path.Combine(cache, "a.txt")));
        }
        finally
        {
            try { Directory.Delete(cache, true); }
            catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("user@example.org", true)]
    [InlineData("myalias", true)]
    [InlineData("alice@192.168.1.5", true)]
    [InlineData("", false)]
    [InlineData("-oProxyCommand=calc", false)]
    [InlineData("-F/tmp/evil", false)]
    [InlineData("host -oProxyCommand=calc", false)]
    [InlineData("user@host;calc", false)]
    [InlineData("host\"x", false)]
    [InlineData("host\nx", false)]
    [InlineData("h\u00F6st", false)]
    public void An_ssh_target_that_ssh_could_read_as_an_option_is_not_accepted(string target, bool safe) =>
        Assert.Equal(safe, SshTunnel.IsSafeTarget(target));

    [Fact]
    public async Task A_tunnel_does_not_start_for_a_target_that_is_an_option()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SshTunnel.StartAsync("-oProxyCommand=calc", "http://127.0.0.1:55077", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => SshTunnel.StartAsync("user@example.org", "file:///c:/x", CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.org")]
    [InlineData("http://192.168.1.5:8080")]
    [InlineData("ftp://example.org")]
    [InlineData("not a url")]
    public async Task A_direct_connection_never_sends_the_password_over_plain_http(string url)
    {
        int calls = 0;
        using RemoteClient client = new(
            new RemoteOptions { Url = url, Mode = RemoteMode.Direct, User = "koda", PasswordProvider = () => "pw", Backoff = [TimeSpan.FromMilliseconds(5)] },
            new CountingHandler(() => calls++));
        await Assert.ThrowsAsync<RemoteAuthException>(() => client.GetStringAsync("/api/ping"));
        Assert.Equal(0, calls);
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
