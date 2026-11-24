using System.Text;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

/// <summary>A folder of a project can be a link (a junction or a symbolic link) to somewhere else; the text-file API must not read or write through it.</summary>
public sealed class ProjectLinkTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    private static bool MakeLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // a junction needs no special right; the paths of this test are made of letters and digits only, so cmd reads nothing special in them
            System.Diagnostics.ProcessStartInfo info = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public async Task The_text_file_api_does_not_read_or_write_through_a_link_that_leads_out_of_the_project()
    {
        string work = Path.Combine(Path.GetTempPath(), "ample-link-" + Guid.NewGuid().ToString("N"));
        string server = Path.Combine(work, "project");
        string outside = Path.Combine(work, "outside");
        string home = work + "-home";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.json"), "{\"secret\":\"outside-the-project\"}");
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        string link = Path.Combine(server, "linked");
        if (!MakeLink(link, outside)) return;   // no right to make a link on this machine: nothing to test
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            string read;
            try { read = await client.GetStringAsync("/api/file?path=" + Uri.EscapeDataString("linked/secret.json")); }
            catch (RemoteRequestException ex) { read = "refused " + ex.Status; }
            Assert.DoesNotContain("outside-the-project", read, StringComparison.Ordinal);

            string json = "{\"path\":\"linked/written.txt\",\"text\":\"x\"}";
            HttpRequestMessage Put(string root) => new(HttpMethod.Put, root + "/api/file") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using HttpResponseMessage response = await client.SendAsync(Put);
            Assert.False(File.Exists(Path.Combine(outside, "written.txt")), "a file was written outside the project through the link (answer " + (int)response.StatusCode + ")");
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (IOException) { }
            foreach (string d in new[] { work, home })
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task The_project_listing_shows_a_link_folder_as_a_link_and_does_not_list_what_is_behind_it()
    {
        string work = Path.Combine(Path.GetTempPath(), "ample-linklist-" + Guid.NewGuid().ToString("N"));
        string server = Path.Combine(work, "project");
        string outside = Path.Combine(work, "outside");
        string home = work + "-home";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret-name.txt"), "x");
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        Directory.CreateDirectory(Path.Combine(server, "plain"));
        string link = Path.Combine(server, "samples-link");
        if (!MakeLink(link, outside)) return;   // no right to make a link on this machine: nothing to test
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            using System.Text.Json.JsonDocument root = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/project/tree?depth=1"));
            System.Text.Json.JsonElement[] folders = [.. root.RootElement.GetProperty("children").EnumerateArray().Where(item => item.GetProperty("isDir").GetBoolean())];
            System.Text.Json.JsonElement linked = Assert.Single(folders, item => item.GetProperty("name").GetString() == "samples-link");
            Assert.True(linked.GetProperty("isLink").GetBoolean());
            System.Text.Json.JsonElement plain = Assert.Single(folders, item => item.GetProperty("name").GetString() == "plain");
            Assert.False(plain.GetProperty("isLink").GetBoolean());

            string inside = await client.GetStringAsync("/api/project/tree?path=samples-link&depth=1");
            using System.Text.Json.JsonDocument opened = System.Text.Json.JsonDocument.Parse(inside);
            Assert.True(opened.RootElement.GetProperty("link").GetBoolean());
            Assert.Empty(opened.RootElement.GetProperty("children").EnumerateArray());
            Assert.DoesNotContain("secret-name", inside, StringComparison.Ordinal);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (IOException) { }
            foreach (string d in new[] { work, home })
            {
                try { Directory.Delete(d, true); }
                catch (IOException) { }
            }
        }
    }
}
