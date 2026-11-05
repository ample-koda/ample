using System.IO.Compression;
using System.Security.Cryptography;
using Ample.Core.Abstractions;

namespace Ample.Cli.Tests;

public sealed class KodaBrowserInstallTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-cli-kodabrowser-").FullName;
    private readonly string? platform = KodaBrowserInstall.CurrentPlatform();

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Makes a release folder with a pack zip (given entries) and a manifest that lists it.</summary>
    private string Release(IEnumerable<string> entries, string? shaOverride = null, bool list = true)
    {
        string release = Path.Combine(folder, "release");
        Directory.CreateDirectory(release);
        string zip = Path.Combine(release, KodaBrowserInstall.AssetNameOf(platform!));
        using (FileStream fs = File.Create(zip))
        using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
        {
            foreach (string name in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter w = new(entry.Open());
                w.Write("x");
            }
        }

        string artefacts = list ? $"{{ \"sha256\": \"{shaOverride ?? Sha(zip)}\", \"name\": \"{KodaBrowserInstall.AssetNameOf(platform!)}\", \"size\": {new FileInfo(zip).Length} }}" : string.Empty;
        File.WriteAllText(Path.Combine(release, "ample-9.9.9.manifest.json"), $"{{ \"schema\": 1, \"version\": \"9.9.9\", \"artefacts\": [ {artefacts} ] }}");
        File.WriteAllText(Path.Combine(release, "ample-9.9.9.manifest.json.sig"), "{}");
        return release;
    }

    private IEnumerable<string> GoodEntries() =>
    [
        ".playwright/package/package.json",
        ".playwright/package/cli.js",
        $".playwright/node/{KodaBrowserInstall.NodeFolderOf(platform!)}/{KodaBrowserInstall.NodeFileOf(platform!)}",
    ];

    private async Task<(int Code, string Text)> Run(string release, Func<string, string>? verify = null)
    {
        using StringWriter output = new();
        int code = await KodaBrowserInstall.RunAsync(output, Path.Combine(folder, "home"), "9.9.9", new Uri(release + Path.DirectorySeparatorChar).AbsoluteUri, skipChromium: true, verify ?? (_ => "testkey"), CancellationToken.None);
        return (code, output.ToString());
    }

    /// <summary>Staging or moved-aside folders next to the pack (the pack itself is named exactly koda-browser).</summary>
    private string[] Leftovers() => [.. Directory.GetDirectories(Path.Combine(folder, "home")).Where(d => Path.GetFileName(d).StartsWith("koda-browser.", StringComparison.Ordinal))];

    private bool Installed() => File.Exists(Path.Combine(folder, "home", "koda-browser", ".playwright", "package", "package.json"));

    [Fact]
    public async Task A_good_pack_listed_in_a_verified_manifest_is_installed_and_leaves_no_staging_folder()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;

        (int code, string text) = await Run(Release(GoodEntries()));

        Assert.Equal(0, code);
        Assert.True(Installed(), text);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_manifest_that_is_not_signed_by_a_release_key_stops_everything_before_the_pack_is_fetched()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;
        string release = Release(GoodEntries());

        (int code, string text) = await Run(release, _ => throw new AmpleException("AMP5312", "The signature is not valid."));

        Assert.Equal(1, code);
        Assert.Contains("not signed by an AMPLE release key", text, StringComparison.Ordinal);
        Assert.False(Installed());
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_manifest_is_refused()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;

        (int code, string text) = await Run(Release(GoodEntries(), shaOverride: new string('a', 64)));

        Assert.Equal(1, code);
        Assert.Contains("does not match the manifest", text, StringComparison.Ordinal);
        Assert.False(Installed());
    }

    [Fact]
    public async Task A_pack_that_the_manifest_does_not_list_is_refused()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;

        (int code, string text) = await Run(Release(GoodEntries(), list: false));

        Assert.Equal(1, code);
        Assert.Contains("is not listed", text, StringComparison.Ordinal);
        Assert.False(Installed());
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("other/file.txt")]
    [InlineData(".playwright/../../escape.txt")]
    public async Task A_zip_with_an_unsafe_or_foreign_path_is_refused_before_anything_is_unpacked(string badEntry)
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;

        (int code, string text) = await Run(Release(GoodEntries().Append(badEntry)));

        Assert.Equal(1, code);
        Assert.Contains("Nothing was installed", text, StringComparison.Ordinal);
        Assert.False(Installed());
        Assert.False(File.Exists(Path.Combine(folder, "evil.txt")) || File.Exists(Path.Combine(folder, "escape.txt")) || File.Exists(Path.Combine(folder, "home", "evil.txt")));
    }

    [Fact]
    public async Task A_zip_without_the_node_program_or_the_package_is_refused()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;

        (int code, string text) = await Run(Release([".playwright/package/package.json"]));

        Assert.Equal(1, code);
        Assert.Contains("expected driver", text, StringComparison.Ordinal);
        Assert.False(Installed());
    }

    [Fact]
    public async Task An_older_pack_is_replaced_and_what_was_moved_aside_is_removed()
    {
        if (platform is null || Environment.IsPrivilegedProcess) return;
        string old = Path.Combine(folder, "home", "koda-browser");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "marker-of-the-old-pack.txt"), "old");

        (int code, _) = await Run(Release(GoodEntries()));

        Assert.Equal(0, code);
        Assert.False(File.Exists(Path.Combine(old, "marker-of-the-old-pack.txt")));
        Assert.True(Installed());
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void The_manifest_is_read_whatever_the_order_of_the_fields_is()
    {
        string manifest = Path.Combine(folder, "m.json");
        File.WriteAllText(manifest, "{ \"artefacts\": [ { \"size\": 7, \"name\": \"other.zip\", \"sha256\": \"" + new string('b', 64) + "\" }, { \"sha256\": \"" + new string('c', 64) + "\", \"size\": 42, \"name\": \"wanted.zip\" } ] }");

        Assert.Equal((new string('c', 64), 42L), KodaBrowserInstall.FindArtefact(manifest, "wanted.zip"));
        Assert.Equal((null, 0L), KodaBrowserInstall.FindArtefact(manifest, "missing.zip"));
        File.WriteAllText(manifest, "not json");
        Assert.Equal((null, 0L), KodaBrowserInstall.FindArtefact(manifest, "wanted.zip"));
    }
}
