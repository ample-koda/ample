using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ample.Cli.Tests;

public sealed class SamplePacksTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-packs-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    private string MakeZip(string id, Action<ZipArchive>? extra = null)
    {
        string path = Path.Combine(folder, id + ".zip");
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, id + "/a1.wav", "RIFFfake");
            Add(zip, id + "/SOURCE.md", "source");
            extra?.Invoke(zip);
        }

        return path;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static IReadOnlyList<SamplePack> Catalog(string id, string zip, string tier = "free") =>
        [new SamplePack(id, tier, "Test " + id, "CC0", "test", ["piano"], new FileInfo(zip).Length, 2, Sha(zip), id + ".zip")];

    private static HttpListener Serve(string zip, out string baseUrl)
    {
        int port = Random.Shared.Next(20000, 40000);
        HttpListener listener = new();
        baseUrl = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    byte[] bytes = File.ReadAllBytes(zip);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            }
        });
        return listener;
    }

    [Fact]
    public void The_catalog_keeps_valid_entries_and_leaves_out_the_rest()
    {
        string json = """[{"id":"piano-1","tier":"free","title":"Piano","sha256":"%S%","asset":"piano-1.zip","bytes":10,"files":2,"instruments":["piano"]},{"id":"../evil","tier":"free","sha256":"%S%","asset":"evil.zip"},{"id":"x","tier":"weird","sha256":"%S%","asset":"x.zip"},{"id":"y","tier":"free","sha256":"short","asset":"y.zip"}]""".Replace("%S%", new string('a', 64), StringComparison.Ordinal);
        IReadOnlyList<SamplePack> catalog = SamplePacks.Parse(json);
        SamplePack only = Assert.Single(catalog);
        Assert.Equal("piano-1", only.Id);
        Assert.Equal("piano", Assert.Single(only.Instruments));
        Assert.Empty(SamplePacks.Parse(string.Empty));
    }

    [Fact]
    public async Task A_missing_pack_is_downloaded_checked_and_unpacked_and_an_installed_one_is_kept()
    {
        string zip = MakeZip("tiny-piano");
        IReadOnlyList<SamplePack> catalog = Catalog("tiny-piano", zip);
        using HttpListener server = Serve(zip, out string baseUrl);
        string home = Path.Combine(folder, "home");
        StringWriter output = new();
        List<string> progress = [];
        Assert.Equal(0, await SamplePacks.InstallAsync(output, home, "tiny-piano", baseUrl, CancellationToken.None, (p, t) => progress.Add(t), catalog));
        Assert.True(SamplePacks.IsInstalled(home, catalog[0]));
        Assert.True(File.Exists(Path.Combine(home, "audio-packs", "free", "tiny-piano", "a1.wav")));
        Assert.NotEmpty(progress);
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "audio-packs", "free"), "*.part"));

        StringWriter again = new();
        Assert.Equal(0, await SamplePacks.InstallAsync(again, home, "all", baseUrl, CancellationToken.None, null, catalog));   // nothing is downloaded: it is there
        Assert.Contains("kept", again.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_pinned_hash_is_refused_and_leaves_nothing()
    {
        string zip = MakeZip("tiny-bass");
        IReadOnlyList<SamplePack> catalog = [catalogEntry(zip)];
        using HttpListener server = Serve(zip, out string baseUrl);
        string home = Path.Combine(folder, "home2");
        StringWriter output = new();
        Assert.Equal(1, await SamplePacks.InstallAsync(output, home, "tiny-bass", baseUrl, CancellationToken.None, null, catalog));
        Assert.Contains("SHA-256", output.ToString(), StringComparison.Ordinal);
        Assert.False(SamplePacks.IsInstalled(home, catalog[0]));

        static SamplePack catalogEntry(string zip) => new("tiny-bass", "free", "Bass", "CC0", "x", [], new FileInfo(zip).Length, 2, new string('0', 64), "tiny-bass.zip");
    }

    [Fact]
    public void A_zip_with_a_path_outside_its_pack_folder_is_refused()
    {
        string evil = MakeZip("evil-pack", zip => Add(zip, "evil-pack/../outside.txt", "x"));
        Assert.StartsWith("the zip holds an unsafe path", SamplePacks.Unpack(evil, Path.Combine(folder, "stage1"), "evil-pack"), StringComparison.Ordinal);
        string other = MakeZip("other-pack", zip => Add(zip, "somewhere-else/file.wav", "x"));
        Assert.Contains("outside other-pack/", SamplePacks.Unpack(other, Path.Combine(folder, "stage2"), "other-pack"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "outside.txt")));
    }

    [Fact]
    public async Task Remove_deletes_an_installed_pack_of_the_catalog_and_nothing_else()
    {
        string zip = MakeZip("tiny-guitar");
        IReadOnlyList<SamplePack> catalog = Catalog("tiny-guitar", zip, "licensed");
        using HttpListener server = Serve(zip, out string baseUrl);
        string home = Path.Combine(folder, "home3");
        Assert.Equal(0, await SamplePacks.InstallAsync(new StringWriter(), home, "tiny-guitar", baseUrl, CancellationToken.None, null, catalog));
        string neighbour = Path.Combine(home, "audio-packs", "licensed", "musescore-general");
        Directory.CreateDirectory(neighbour);
        File.WriteAllText(Path.Combine(neighbour, "font.sf2"), "keep");

        Assert.Equal(2, SamplePacks.Remove(new StringWriter(), home, "not-a-pack", catalog));   // not in the catalog: refused
        Assert.Equal(0, SamplePacks.Remove(new StringWriter(), home, "tiny-guitar", catalog));
        Assert.False(SamplePacks.IsInstalled(home, catalog[0]));
        Assert.True(File.Exists(Path.Combine(neighbour, "font.sf2")));   // another folder of the user is never touched
    }
}

public sealed class SamplePackDoctorTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("ample-doctor-packs-").FullName;

    public void Dispose() => Directory.Delete(home, true);

    private static SamplePack Pack(string id, string tier = "free") => new(id, tier, "Pack " + id, tier == "free" ? "CC0 1.0" : "CC BY 4.0", "a pack", [], 50L * 1024 * 1024, 3, new string('a', 64), id + ".zip");

    [Fact]
    public void The_author_and_the_credit_requirement_are_read_from_the_catalog_and_shown_as_json()
    {
        string json = "[{\"id\":\"a-pack\",\"tier\":\"licensed\",\"title\":\"A\",\"license\":\"CC BY 4.0\",\"author\":\"Some Author\",\"sha256\":\"" + new string('a', 64) + "\",\"asset\":\"a-pack.zip\"},{\"id\":\"b-pack\",\"tier\":\"free\",\"sha256\":\"" + new string('b', 64) + "\",\"asset\":\"b-pack.zip\"}]";
        IReadOnlyList<SamplePack> catalog = SamplePacks.Parse(json);
        Assert.True(catalog[0].CreditRequired);
        Assert.Equal("Some Author", catalog[0].Author);
        Assert.False(catalog[1].CreditRequired);
        StringWriter output = new();
        SamplePacks.Available(output, Path.Combine(Path.GetTempPath(), "ample-no-such-home"), true, catalog);
        Assert.Contains("\"creditRequired\": true", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"author\": \"Some Author\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_pack_is_a_note_with_a_download_and_an_installed_one_is_ok_with_a_removal()
    {
        SamplePack here = Pack("here"), missing = Pack("missing", "licensed");
        string folder = Path.Combine(home, "audio-packs", "free", "here");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a1.wav"), new byte[2 * 1024 * 1024]);

        List<DoctorJson.Item> items = DoctorJson.SamplePackItems(home, [here, missing]);
        DoctorJson.Item installed = items.Single(i => i.Name == "sound pack: Pack here");
        Assert.Equal("ok", installed.Level);
        Assert.Equal("pack-remove", installed.Fix!.Kind);
        Assert.Equal("here", installed.Fix.Id);
        DoctorJson.Item absent = items.Single(i => i.Name == "sound pack: Pack missing");
        Assert.Equal("note", absent.Level);   // optional: never a problem
        Assert.Equal("pack", absent.Fix!.Kind);
        Assert.Equal(50, absent.Fix.SizeMb);
        Assert.Contains("asks for a credit", absent.Fix.Title, StringComparison.Ordinal);   // a CC BY pack says so
    }
}
