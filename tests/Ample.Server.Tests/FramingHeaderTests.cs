using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>No page of the studio, also not the sign-in page, can be shown inside a frame of another site.</summary>
public sealed class FramingHeaderTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    [Fact]
    public async Task The_sign_in_page_and_the_api_refuse_to_be_framed_and_send_no_referrer()
    {
        string server = Path.Combine(Path.GetTempPath(), "ample-frame-" + Guid.NewGuid().ToString("N"));
        string home = server + "-home";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using HttpClient http = new();
            foreach (string path in new[] { "/login", "/api/help" })
            {
                using HttpResponseMessage response = await http.GetAsync(running.Url + path);
                Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
                Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
                Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            }
        }
        finally
        {
            try { Directory.Delete(server, true); Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }
}
