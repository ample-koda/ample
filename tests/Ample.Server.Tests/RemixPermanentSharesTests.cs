using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>Permanent shares of the owner on the hosted Remix: the admin route exists only with AMPLE_REMIX_ADMIN_KEY, from this machine and with the key; its shares never expire and survive every upkeep pass.</summary>
[Collection("RemixAdminKey")]
public sealed class RemixPermanentSharesTests : IAsyncLifetime, IDisposable
{
    private const string Key = "test-admin-key-0123456789";
    private const string Id = "ABCDEF12";

    private const string Song = """
        composition "t" { ample 1  tempo 120  master-seed 1 }

        midi-clip m at 1 for 1 {
          notes 1:0:1:60:100
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
    private string? previousKey;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        previousKey = Environment.GetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY");
        folder = Path.Combine(Path.GetTempPath(), "ample-permshare-" + Guid.NewGuid().ToString("N"));
        home = folder + "-library";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "none", null, CancellationToken.None))
        {
        }

        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: home) { LiteOnly = true });
        http = new HttpClient { BaseAddress = new Uri(server.Url) };
    }

    public void Dispose() => http?.Dispose();

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY", previousKey);
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

    [Fact]
    public async Task Without_a_key_the_route_does_not_exist()
    {
        Environment.SetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY", null);
        using HttpResponseMessage answer = await Post("My song", Key);
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_or_none_is_a_404()
    {
        Environment.SetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY", Key);
        using HttpResponseMessage wrong = await Post("My song", "not-the-key");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        using HttpResponseMessage none = await Post("My song", null);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task The_right_key_from_loopback_makes_a_permanent_share_and_a_second_post_replaces_it()
    {
        Environment.SetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY", Key);
        using (HttpResponseMessage made = await Post("First", Key))
        {
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await made.Content.ReadAsStringAsync());
            Assert.Equal(Id, body.RootElement.GetProperty("id").GetString());
            Assert.True(body.RootElement.GetProperty("permanent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expires").ValueKind);
            Assert.Equal("/mix/" + Id, body.RootElement.GetProperty("url").GetString());
        }

        using (JsonDocument shown = JsonDocument.Parse(await http.GetStringAsync($"/api/remix/shares/{Id}")))
        {
            Assert.True(shown.RootElement.GetProperty("permanent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, shown.RootElement.GetProperty("expires").ValueKind);
            Assert.Equal("available", shown.RootElement.GetProperty("state").GetString());
            Assert.Equal("First", shown.RootElement.GetProperty("title").GetString());
        }

        using (HttpResponseMessage again = await Post("Second", Key))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using (JsonDocument shown = JsonDocument.Parse(await http.GetStringAsync($"/api/remix/shares/{Id}")))
        {
            Assert.Equal("Second", shown.RootElement.GetProperty("title").GetString());
        }

        // no management code: the maker's route does not know it
        using StringContent manage = new(JsonSerializer.Serialize(new { code = "x", action = "delete" }), Encoding.UTF8, "application/json");
        using HttpResponseMessage managed = await http.PostAsync($"/api/remix/shares/{Id}/manage", manage);
        Assert.Equal(HttpStatusCode.NotFound, managed.StatusCode);
    }

    [Fact]
    public void The_expiry_and_upkeep_passes_keep_a_permanent_share()
    {
        DateTime clock = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        string root = Path.Combine(Path.GetTempPath(), "ample-permstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            LiteShares store = new(root, new ShareLimits(64, 1024 * 1024, 1024 * 1024), () => clock);
            store.PutPermanent(Id, "Kept", "koda", string.Empty, "song.amplet", [("song.amplet", Encoding.UTF8.GetBytes(Song))]);
            clock = clock.AddDays(400);
            store.Maintain();
            Assert.Equal("available", store.Find(Id)!.Value.State);
            Assert.Null(store.Find(Id)!.Value.Share.Expires);

            // a reopened store reads the flag back
            LiteShares reopened = new(root, new ShareLimits(64, 1024 * 1024, 1024 * 1024), () => clock);
            reopened.Maintain();
            Assert.True(reopened.Find(Id)!.Value.Share.Permanent);
            Assert.Equal("available", reopened.Find(Id)!.Value.State);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<HttpResponseMessage> Post(string title, string? key)
    {
        string json = JsonSerializer.Serialize(new
        {
            id = Id,
            title,
            author = "koda",
            description = "A permanent demo.",
            entry = "song.amplet",
            files = new[] { new { path = "song.amplet", text = Song } },
        });
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/remix/admin/shares") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("X-Ample-Admin", key);
        return await http.SendAsync(request);
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }
}
