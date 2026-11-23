using System.Text;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

/// <summary>The private folder (.ample) of a project is not readable or writable through the text-file API, whatever way the path is spelled.</summary>
public sealed class PrivateFolderTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    [Theory]
    [InlineData(".ample/project.json")]
    [InlineData("./.ample/project.json")]
    [InlineData("sub/../.ample/project.json")]
    [InlineData("sub\\..\\.ample\\project.json")]
    [InlineData(".AMPLE/project.json")]
    [InlineData(".ample//project.json")]
    public async Task The_text_file_api_never_reads_or_writes_the_private_folder(string path)
    {
        string server = Path.Combine(Path.GetTempPath(), "ample-priv-" + Guid.NewGuid().ToString("N"));
        string home = server + "-home";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            string read = await Try(() => client.GetStringAsync("/api/file?path=" + Uri.EscapeDataString(path)));
            Assert.StartsWith("refused", read, StringComparison.Ordinal);

            string poc = path.Replace("project.json", "poc-" + Guid.NewGuid().ToString("N")[..6] + ".txt", StringComparison.Ordinal);
            string json = "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(poc) + ",\"text\":\"x\"}";
            using HttpResponseMessage put = await client.SendAsync(root => new HttpRequestMessage(HttpMethod.Put, root + "/api/file") { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            string write = ((int)put.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string created = string.Join(",", Directory.GetFiles(Path.Combine(server, ".ample"), "poc-*", SearchOption.AllDirectories));
            Assert.True(created.Length == 0, "written into the private folder via '" + poc + "' (" + write + "): " + created);
        }
        finally
        {
            try { Directory.Delete(server, true); Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(".ample", true)]
    [InlineData(".ample/x.json", true)]
    [InlineData("./.ample/x.json", true)]
    [InlineData("a/../.ample/x.json", true)]
    [InlineData("a\\..\\.AMPLE\\x.json", true)]
    [InlineData(".Ample//x.json", true)]
    [InlineData("a/.ample/x.json", false)]
    [InlineData(".amplet", false)]
    [InlineData("samples/.ample", false)]
    [InlineData("song.amplet", false)]
    [InlineData("", false)]
    public void IsPrivate_looks_at_the_resolved_path_not_at_the_text(string path, bool expected) =>
        Assert.Equal(expected, ProjectPath.IsPrivate(path));

    private static async Task<string> Try(Func<Task<string>> call)
    {
        try { return "ok:" + (await call()).Length; }
        catch (RemoteRequestException ex) { return "refused:" + ex.Status; }
    }
}
