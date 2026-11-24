using System.Text;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Storage.Package;
using Ample.Storage.Projects;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

/// <summary>A package comes from outside: the files in it must end up inside the project that is made of it, whatever path the package gives them.</summary>
public sealed class PackageImportPathTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    [Theory]
    [InlineData("../escape/pwned.txt")]
    [InlineData("a/../../escape/pwned.txt")]
    [InlineData(".ample/pwned.txt")]
    [InlineData("./.ample/pwned.txt")]
    [InlineData("sub/../.ample/pwned.txt")]
    [InlineData("/escape/pwned.txt")]
    [InlineData("C:/escape/pwned.txt")]
    public async Task A_file_of_a_package_never_lands_outside_the_new_project_or_in_its_private_folder(string entryPath)
    {
        string work = Path.Combine(Path.GetTempPath(), "ample-pkgimp-" + Guid.NewGuid().ToString("N"));
        string server = work + "-server";
        string home = work + "-home";
        Directory.CreateDirectory(work);
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        string package = Path.Combine(work, "evil.ampletx");
        TxWriter.Write(package, "project", new JsonObject(),
        [
            new TxSource("song.amplet", AmpleTx.Roles.Song, "text/plain", "song.amplet", "song.amplet", Text: "composition \"x\" { ample 1  tempo 100 }\n"),
            new TxSource(entryPath, AmpleTx.Roles.Source, "text/plain", "pwned.txt", entryPath, Text: "owned\n"),
        ]);
        string created = Path.Combine(work, "new", "inner");
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            string body = new JsonObject { ["path"] = package, ["mode"] = "new-project", ["newFolder"] = created }.ToJsonString();
            HttpRequestMessage Apply(string root) => new(HttpMethod.Post, root + "/api/tx/import/apply") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using HttpResponseMessage response = await client.SendAsync(Apply);
            string answer = await response.Content.ReadAsStringAsync();

            string inside = created + Path.DirectorySeparatorChar;
            string[] outside = [.. Directory.GetFiles(work, "pwned.txt", SearchOption.AllDirectories).Where(f => !f.StartsWith(inside, StringComparison.OrdinalIgnoreCase))];
            string[] insidePrivate = Directory.Exists(Path.Combine(created, ".ample")) ? Directory.GetFiles(Path.Combine(created, ".ample"), "pwned.txt", SearchOption.AllDirectories) : [];
            string where = string.Join(",", outside.Concat(insidePrivate));
            Assert.True(outside.Length == 0 && insidePrivate.Length == 0, $"'{entryPath}' was written to {where} (answer {(int)response.StatusCode}: {answer[..Math.Min(160, answer.Length)]})");
        }
        finally
        {
            foreach (string d in new[] { work, server, home })
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
            }
        }
    }
}
