using System.Net;
using Ample.Audio.IO;
using Ample.Audio.Instruments;
using Ample.Storage.Projects;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

public sealed class AmpleHomeTests
{
    [Fact]
    public void Settings_folder_precedes_the_default_user_pack_folder()
    {
        using AmpleHomeTempFolder temp = new();
        string configured = Path.Combine(temp.Path, "configured");
        AmpleHome.WriteSettings(temp.Path, new AmpleSettings { PacksFolder = configured });

        IReadOnlyList<string> folders = AmpleHome.PackFolders(temp.Path, null, Path.Combine(temp.Path, "project"));

        Assert.True(Array.IndexOf([.. folders], Path.GetFullPath(configured)) < Array.IndexOf([.. folders], Path.Combine(temp.Path, "audio-packs")));
    }

    [Fact]
    public void Lookup_finds_the_server_ample_root_when_the_project_is_elsewhere()
    {
        using AmpleHomeTempFolder temp = new();
        string project = Path.Combine(temp.Path, "project");
        string root = Path.Combine(temp.Path, "server-copy");
        string relative = "licensed/musescore-general/MuseScore_General.sf2";
        string expected = Path.Combine(root, "audio-packs", "licensed", "musescore-general", "MuseScore_General.sf2");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllBytes(expected, [1]);

        PackLookup lookup = AmpleHome.LookupPackFile(Path.Combine(temp.Path, "home"), root, project, relative);

        Assert.Equal(expected, lookup.Path);
    }

    [Fact]
    public void Lookup_finds_the_user_level_pack_when_project_and_server_copy_are_elsewhere()
    {
        using AmpleHomeTempFolder temp = new();
        string home = Path.Combine(temp.Path, "home");
        string expected = Path.Combine(home, "audio-packs", "licensed", "musescore-general", "MuseScore_General.sf2");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllBytes(expected, [1]);

        PackLookup lookup = AmpleHome.LookupPackFile(home, Path.Combine(temp.Path, "server-copy"), Path.Combine(temp.Path, "project"), "licensed/musescore-general/MuseScore_General.sf2");

        Assert.Equal(expected, lookup.Path);
    }

    [Fact]
    public void Missing_soundfont_names_the_locations_that_were_searched()
    {
        using AmpleHomeTempFolder temp = new();
        string project = Path.Combine(temp.Path, "project");
        string root = Path.Combine(temp.Path, "server-copy");

        // the searched locations always include the user home, whatever is installed on this machine
        string home = Path.Combine(temp.Path, "empty-home");
        PackLookup searchedLookup = AmpleHome.LookupPackFile(home, root, project, "licensed/musescore-general/MuseScore_General.sf2");
        Assert.Contains(searchedLookup.Searched, location => location.StartsWith(home, StringComparison.Ordinal));
        if (searchedLookup.Path is not null)
        {
            return;   // the pack is installed next to this checkout (audio-packs): the error text below needs a machine without it
        }

        // the resolver reads the home and the pack folders from the environment: for this check they are empty (a machine that has the packs set up, like a CI server, must not change the answer)
        string? oldHome = Environment.GetEnvironmentVariable("AMPLE_HOME"), oldPacks = Environment.GetEnvironmentVariable("AMPLE_PACKS");
        AmpleException error;
        try
        {
            Environment.SetEnvironmentVariable("AMPLE_HOME", home);
            Environment.SetEnvironmentVariable("AMPLE_PACKS", null);
            error = Assert.Throws<AmpleException>(() => SoundFontAsset.Resolve(project, root, SoundFontAsset.GeneralMidi));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMPLE_HOME", oldHome);
            Environment.SetEnvironmentVariable("AMPLE_PACKS", oldPacks);
        }


        Assert.Equal("AMP5311", error.DiagnosticCode);
        Assert.Contains("Searched:", error.Message, StringComparison.Ordinal);
        Assert.Contains("ample packs link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ace_health_check_reports_http_failure_without_reading_a_response_body()
    {
        using HttpMessageHandler handler = new StaticHandler(HttpStatusCode.ServiceUnavailable);

        AceHealthResult result = await AceServerHealth.CheckAsync(new Uri("http://127.0.0.1:8085/"), TimeSpan.FromSeconds(1), handler, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal("HTTP 503", result.Problem);
    }

    private sealed class AmpleHomeTempFolder : IDisposable
    {
        internal AmpleHomeTempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ample-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status));
    }
}
