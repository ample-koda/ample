using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>The address a server listens on (<c>--bind</c>): behind a reverse proxy on the same machine it is 127.0.0.1, even when any host name is allowed.</summary>
public sealed class ServerBindTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ample-bind-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
            Directory.Delete(folder + "-library", true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private async Task MakeProjectAsync()
    {
        using OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "demo", null, CancellationToken.None);
    }

    [Fact]
    public async Task A_bind_address_that_is_not_an_ip_is_refused_with_its_own_code()
    {
        await MakeProjectAsync();
        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(async () =>
        {
            await using RunningServer server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library") { BindAddress = "localhost" });
        });
        Assert.Equal("AMP5940", refused.DiagnosticCode);
    }

    [RepoFact("web/index.html")]
    public async Task The_default_address_is_the_loopback_and_an_explicit_loopback_works()
    {
        await MakeProjectAsync();
        await using RunningServer server = await AmpleServer.StartAsync(new ServerOptions(folder, FindAmpleRoot(), LibraryHome: folder + "-library") { BindAddress = "127.0.0.1" });
        Assert.StartsWith("http://127.0.0.1:", server.Url, StringComparison.Ordinal);
        using HttpClient http = new() { BaseAddress = new Uri(server.Url) };
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);   // the page of the studio (/login only exists with --login)
    }
}
