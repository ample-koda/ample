using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>ample_install_pack: finding pack.json in an extracted archive, and that the tool/route are wired up (the HTTP download itself needs a
/// real public address, which AiService.ConnectPublicOnlyAsync deliberately refuses for a local test server, so it is not exercised here).</summary>
public sealed class AiInstallPackTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ample-pack-folder-" + Guid.NewGuid().ToString("N"));
    private string projectFolder = string.Empty;
    private ProjectSession session = null!;

    public async Task InitializeAsync()
    {
        string? ampleRoot = AppContext.BaseDirectory;
        while (ampleRoot is not null && !File.Exists(Path.Combine(ampleRoot, "Ample.sln"))) ampleRoot = Path.GetDirectoryName(ampleRoot);
        projectFolder = Path.Combine(Path.GetTempPath(), "ample-pack-project-" + Guid.NewGuid().ToString("N"));
        using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), projectFolder, "pack-project", null, CancellationToken.None)) { }
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "song.amplet"), "project pack-project\n");
        session = await ProjectSession.OpenAsync(new ServerOptions(projectFolder, ampleRoot ?? throw new InvalidOperationException("AMPLE root not found."), LibraryHome: projectFolder + "-library"), new EventBus(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await session.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        if (Directory.Exists(projectFolder + "-library")) Directory.Delete(projectFolder + "-library", recursive: true);
    }

    [Fact]
    public void A_pack_json_at_the_archive_root_is_found()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pack.json"), "{\"name\":\"x\",\"version\":\"1\"}");
        Assert.Equal(root, AiService.FindPackFolder(root));
    }

    [Fact]
    public void A_pack_json_inside_the_one_wrapping_folder_most_zip_tools_add_is_found()
    {
        string wrapped = Path.Combine(root, "analog-808-v1");
        Directory.CreateDirectory(wrapped);
        File.WriteAllText(Path.Combine(wrapped, "pack.json"), "{\"name\":\"analog-808\",\"version\":\"1\"}");
        Assert.Equal(wrapped, AiService.FindPackFolder(root));
    }

    [Fact]
    public void An_archive_with_no_pack_json_anywhere_finds_nothing()
    {
        Directory.CreateDirectory(Path.Combine(root, "samples"));
        File.WriteAllText(Path.Combine(root, "samples", "kick.wav"), "not really audio");
        Assert.Null(AiService.FindPackFolder(root));
    }

    [Fact]
    public void An_archive_with_several_top_folders_and_no_root_pack_json_still_finds_the_one_that_has_it()
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        string wrapped = Path.Combine(root, "pack-contents");
        Directory.CreateDirectory(wrapped);
        File.WriteAllText(Path.Combine(wrapped, "pack.json"), "{\"name\":\"x\",\"version\":\"1\"}");
        Assert.Equal(wrapped, AiService.FindPackFolder(root));
    }

    [Fact]
    public void Ample_project_puts_catalogs_before_the_files_array_so_an_outer_truncation_never_drops_it()
    {
        // the files array lists every project file and can be large enough that the AI host's own size limit cuts the response off;
        // catalogs (and its note) must come first in the serialized JSON so it survives even when files does not.
        string source = File.ReadAllText(FindFile("src", "Ample.Server", "AiContext.cs"));
        int catalogsAt = source.IndexOf("[\"catalogs\"] = catalogs,", StringComparison.Ordinal);
        int filesAt = source.IndexOf("[\"files\"] = files,", StringComparison.Ordinal);
        Assert.True(catalogsAt >= 0 && filesAt >= 0 && catalogsAt < filesAt, "catalogs must be written to the JsonObject before files.");
    }

    [Fact]
    public async Task Ample_project_files_can_be_searched_filtered_and_paged()
    {
        Directory.CreateDirectory(Path.Combine(projectFolder, "samples"));
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "samples", "kick.wav"), "not really audio");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "samples", "snare.wav"), "not really audio");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "notes.txt"), "some lyrics notes");

        JsonObject all = AiContext.Project(session);
        Assert.True(all["totalFiles"]!.GetValue<int>() >= 4);   // song.amplet, kick.wav, snare.wav, notes.txt
        Assert.False(all["filesTruncated"]!.GetValue<bool>());

        JsonObject wavOnly = AiContext.Project(session, filesExt: "wav");
        Assert.Equal(2, wavOnly["files"]!.AsArray().Count);
        Assert.All(wavOnly["files"]!.AsArray(), f => Assert.Equal("binary", f!["kind"]!.GetValue<string>()));

        JsonObject textOnly = AiContext.Project(session, filesKind: "text");
        Assert.Contains(textOnly["files"]!.AsArray(), f => f!["path"]!.GetValue<string>() == "notes.txt");
        Assert.DoesNotContain(textOnly["files"]!.AsArray(), f => f!["path"]!.GetValue<string>().EndsWith(".wav", StringComparison.Ordinal));

        JsonObject searched = AiContext.Project(session, filesQuery: "snare");
        Assert.Single(searched["files"]!.AsArray());
        Assert.Equal("samples/snare.wav", searched["files"]!.AsArray()[0]!["path"]!.GetValue<string>());

        JsonObject firstPage = AiContext.Project(session, filesLimit: 1);
        Assert.Single(firstPage["files"]!.AsArray());
        Assert.True(firstPage["filesTruncated"]!.GetValue<bool>());
        JsonObject secondPage = AiContext.Project(session, filesOffset: 1, filesLimit: 1);
        Assert.NotEqual(firstPage["files"]!.AsArray()[0]!["path"]!.GetValue<string>(), secondPage["files"]!.AsArray()[0]!["path"]!.GetValue<string>());
    }

    [RepoFact("src/Ample.Cli/McpCommands.cs")]
    public void The_mcp_tool_and_its_route_exist_and_are_not_treated_as_read_only_or_batchable()
    {
        string mcp = File.ReadAllText(FindFile("src", "Ample.Cli", "McpCommands.cs"));
        Assert.Contains("\"ample_install_pack\"", mcp, StringComparison.Ordinal);
        Assert.Contains("\"ample_install_pack\" => $\"install-pack?", mcp, StringComparison.Ordinal);
        // readOnlyHint and the ample_batch exclusion list both list ample_fetch_url; ample_install_pack must be beside it in both (same risk: a side-effecting, approval-gated download).
        foreach (string line in mcp.Split('\n').Where(l => l.Contains("ample_fetch_url", StringComparison.Ordinal) && l.Contains("ample_request_asset", StringComparison.Ordinal)))
        {
            Assert.Contains("ample_install_pack", line, StringComparison.Ordinal);
        }

        Assert.Contains(Routes.Catalog, op => op.Method == "GET" && op.Path == "/api/ai/context/install-pack");
    }

    private static string FindFile(params string[] relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir!, .. relative]);
    }
}
