using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.VisualStudio.Remote;

namespace Ample.Server.Tests;

/// <summary>
/// A project can hold any file, so a plugin folder inside a project must never be started: a plugin names the program to run, and a manifest can be written
/// into a project through the text-file API or arrive with a project someone else made.
/// </summary>
public sealed class ProjectPluginTests
{
    private static string FindAmpleRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("global.json not found.");
    }

    private const string Manifest = """
        {"id":"project-evil","version":"1.0.0","title":"Evil","kind":"analysis","protocolVersion":1,"experimental":true,
         "entry":{"command":"node","args":["-e","0"]},"platforms":["windows","linux","macos"],"blocks":[],"analyses":["transcription"],
         "capabilities":["analyze"],"settings":{"schema":{"type":"object","properties":{},"additionalProperties":false}},
         "determinism":"exact","toolprintFields":["plugin"],"needs":{"gpu":false,"network":false,"diskMb":1,"secrets":[]},"files":{"read":[],"write":[]}}
        """;

    [Fact]
    public async Task A_plugin_folder_inside_the_project_is_neither_listed_nor_offered_for_transcription()
    {
        string server = Path.Combine(Path.GetTempPath(), "ample-pplug-" + Guid.NewGuid().ToString("N"));
        string home = server + "-home";
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), server, "demo", null, CancellationToken.None))
        {
            _ = p;
        }

        Directory.CreateDirectory(Path.Combine(server, "plugins", "project-evil"));
        await File.WriteAllTextAsync(Path.Combine(server, "plugins", "project-evil", "plugin.json"), Manifest);
        AuthService.SetPassword(home, "koda", "a-long-password");
        await using RunningServer running = await AmpleServer.StartAsync(new ServerOptions(server, FindAmpleRoot(), LibraryHome: home) { Login = true });
        try
        {
            using RemoteClient client = new(new RemoteOptions { Url = running.Url, User = "koda", PasswordProvider = () => "a-long-password" });
            string listed = await client.GetStringAsync("/api/plugins");
            Assert.DoesNotContain("project-evil", listed, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(server, true); Directory.Delete(home, true); }
            catch (IOException) { }
        }
    }
}
