using System.Net;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>The files that a build does not have to send again: the files of the demos, found by the hash of their content, and the uploads of earlier builds, kept for an hour up to a size.</summary>
public sealed class LiteBlobsTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] SongFile = ["song.amplet"];

    private string folder = string.Empty;
    private string home = string.Empty;
    private RunningServer server = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        folder = Path.Combine(Path.GetTempPath(), "ample-liteblobs-" + Guid.NewGuid().ToString("N"));
        home = folder + "-library";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "none", null, CancellationToken.None))
        {
        }

        server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: home) { LiteOnly = true });
        http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri(server.Url) };
    }

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

    public void Dispose() => http?.Dispose();

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private static string DemoSong() => File.ReadAllText(Path.Combine(FindAmpleRoot(), "web", "remix", "demos", "hello-melody", "song.amplet"));

    private static MultipartFormDataContent Form(string manifest, params (string Name, byte[] Data)[] parts)
    {
        MultipartFormDataContent form = new() { { new StringContent(manifest), "manifest" } };
        foreach ((string name, byte[] data) in parts) form.Add(new ByteArrayContent(data), name, "file");
        return form;
    }

    private static string Manifest(string hash) => JsonSerializer.Serialize(new { entry = "song.amplet", files = new[] { new { path = "song.amplet", sha256 = hash } } });

    [Fact]
    public void The_store_knows_the_demo_files_by_the_hash_of_their_content()
    {
        LiteBlobs blobs = new(Path.Combine(FindAmpleRoot(), "web"), 1024, TimeSpan.FromHours(1));
        Assert.True(blobs.KnownFiles > 50);
        byte[] song = Encoding.UTF8.GetBytes(DemoSong());
        Assert.True(blobs.Has(LiteBlobs.Hash(song)));
        Assert.True(blobs.TryGet(LiteBlobs.Hash(song), out byte[] back));
        Assert.Equal(song, back);
        Assert.False(blobs.Has(LiteBlobs.Hash("not a demo file"u8)));
        Assert.False(blobs.TryGet("../../etc/passwd", out _), "a path is not a hash");
        Assert.False(LiteBlobs.IsHash("ABC"));
    }

    [Fact]
    public void An_upload_is_kept_for_an_hour_since_it_was_last_used_and_the_oldest_go_when_the_store_is_full()
    {
        DateTime clock = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        LiteBlobs blobs = new(null, 100, TimeSpan.FromHours(1), () => clock);
        byte[] a = new byte[40], b = new byte[41], c = new byte[42];
        a[0] = 1; b[0] = 2; c[0] = 3;
        blobs.Put(a);
        clock += TimeSpan.FromMinutes(30);
        blobs.Put(b);
        Assert.True(blobs.Has(LiteBlobs.Hash(a)));
        clock += TimeSpan.FromMinutes(45);   // a is 75 minutes old, b 45
        Assert.False(blobs.Has(LiteBlobs.Hash(a)), "an hour without use");
        Assert.True(blobs.TryGet(LiteBlobs.Hash(b), out _));   // using it keeps it
        clock += TimeSpan.FromMinutes(50);   // b was used 50 minutes ago
        Assert.True(blobs.Has(LiteBlobs.Hash(b)));
        blobs.Put(a);
        blobs.Put(c);   // 40 + 41 + 42 is more than 100: the oldest (b) goes
        Assert.False(blobs.Has(LiteBlobs.Hash(b)));
        Assert.True(blobs.Has(LiteBlobs.Hash(a)) && blobs.Has(LiteBlobs.Hash(c)));
        Assert.True(blobs.KeptBytes <= 100);
        blobs.Put(new byte[500]);   // larger than the whole store: not kept
        Assert.True(blobs.KeptBytes <= 100);
    }

    [Fact]
    public async Task The_server_says_which_hashes_it_does_not_have()
    {
        string known = LiteBlobs.Hash(Encoding.UTF8.GetBytes(DemoSong()));
        string unknown = LiteBlobs.Hash("nobody has this"u8);
        using HttpResponseMessage response = await http.PostAsync("/api/remix/blobs/missing", new StringContent(JsonSerializer.Serialize(new { hashes = new[] { known, unknown, unknown } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement missing = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("missing");
        Assert.Equal([unknown], missing.EnumerateArray().Select(e => e.GetString()!).ToArray());
        using HttpResponseMessage bad = await http.PostAsync("/api/remix/blobs/missing", new StringContent("{\"hashes\":[\"../x\"]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_build_needs_no_upload_for_a_demo_file_and_for_a_file_that_an_earlier_build_sent()
    {
        // a demo file by its hash alone: no part at all
        string demoHash = LiteBlobs.Hash(Encoding.UTF8.GetBytes(DemoSong()));
        using HttpResponseMessage fromDemo = await http.PostAsync("/api/remix/build", Form(Manifest(demoHash)));
        Assert.Equal(HttpStatusCode.OK, fromDemo.StatusCode);

        // my own song: the first build sends it, the next one does not have to
        byte[] mine = Encoding.UTF8.GetBytes(DemoSong() + "\n# my own change\n");
        string myHash = LiteBlobs.Hash(mine);
        using HttpResponseMessage first = await http.PostAsync("/api/remix/build", Form(Manifest(myHash), ("f0", mine)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using HttpResponseMessage second = await http.PostAsync("/api/remix/build", Form(Manifest(myHash)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task A_hash_that_the_server_does_not_know_is_a_409_and_a_part_that_does_not_match_its_hash_a_400()
    {
        string unknown = LiteBlobs.Hash("nobody has this"u8);
        using HttpResponseMessage gone = await http.PostAsync("/api/remix/build", Form(Manifest(unknown)));
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Equal("AMPL942", JsonDocument.Parse(await gone.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());

        using HttpResponseMessage lie = await http.PostAsync("/api/remix/build", Form(Manifest(unknown), ("f0", Encoding.UTF8.GetBytes(DemoSong()))));
        Assert.Equal(HttpStatusCode.BadRequest, lie.StatusCode);
        Assert.Equal("AMPL943", JsonDocument.Parse(await lie.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_old_form_with_plain_paths_still_works()
    {
        string plain = JsonSerializer.Serialize(new { entry = "song.amplet", files = SongFile });
        using HttpResponseMessage response = await http.PostAsync("/api/remix/build", Form(plain, ("f0", Encoding.UTF8.GetBytes(DemoSong()))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
