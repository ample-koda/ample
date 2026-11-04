using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Server.Tests;

public sealed class AiAudioPreviewsTests
{
    [Fact]
    public void Preview_batch_accepts_only_urls_observed_in_the_current_browser_task()
    {
        const string url = "https://cdn.example.test/preview.mp3?token=synthetic";
        using JsonDocument body = JsonDocument.Parse("""{"samples":[{"url":"https://cdn.example.test/preview.mp3?token=synthetic","label":"Guitar A"}]}""");
        List<AudioPreviewRequest> parsed = AiAudioPreviewSafety.ParseBatch(body.RootElement, [url], 10);
        Assert.Equal("Guitar A", parsed.Single().Label);
        Assert.Throws<AmpleException>(() => AiAudioPreviewSafety.ParseBatch(body.RootElement, [], 10));
    }

    [Fact]
    public void Preview_limit_defaults_to_ten_and_only_explicit_requests_raise_it_to_fifty()
    {
        Assert.Equal(10, AiAudioPreviewSafety.RequestedLimit(["Find some guitar samples"]));
        Assert.Equal(18, AiAudioPreviewSafety.RequestedLimit(["Find 18 guitar samples"]));
        Assert.Equal(50, AiAudioPreviewSafety.RequestedLimit(["Find 80 guitar samples"]));
    }

    [Theory]
    [InlineData("audio/mpeg", "", "", true)]
    [InlineData("audio/mp3", "", "", true)]
    [InlineData("application/octet-stream", "preview.mp3", "ID3", true)]
    [InlineData("audio/wav", "preview.wav", "RIFF", false)]
    [InlineData("text/html", "preview.mp3", "<ht", false)]
    public void Preview_batch_accepts_only_mp3_responses(string mediaType, string path, string prefix, bool expected) => Assert.Equal(expected, AiAudioPreviewSafety.IsMp3(mediaType, path, System.Text.Encoding.ASCII.GetBytes(prefix)));

    [RepoFact("src/Ample.Cli/McpCommands.cs", "web/app/ai.js")]
    public void Preview_batch_is_wired_as_one_approval_gated_non_batch_mcp_action()
    {
        string root = FindRoot();
        string mcp = File.ReadAllText(Path.Combine(root, "src", "Ample.Cli", "McpCommands.cs"));
        string routes = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "Routes.cs"));
        string ui = File.ReadAllText(Path.Combine(root, "web", "app", "ai.js"));
        Assert.Contains("\"ample_fetch_audio_previews\"", mcp, StringComparison.Ordinal);
        Assert.Contains("ample_fetch_audio_previews", mcp.Split('\n').Single(line => line.Contains("readOnlyHint", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("ample_fetch_audio_previews", mcp.Split('\n').Single(line => line.Contains("if (tool is", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("/api/ai/context/audio-previews", routes, StringComparison.Ordinal);
        Assert.Contains("previewBatch", ui, StringComparison.Ordinal);
        Assert.Contains("Use this preview", ui, StringComparison.Ordinal);
        Assert.Contains("audio/mpeg", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_batch_without_a_list_takes_the_previews_the_task_started_and_keeps_the_last_ones()
    {
        using JsonDocument body = JsonDocument.Parse("""{}""");
        KeyValuePair<string, string>[] chosen = [new("https://cdn.example.test/a.mp3", "MARIMBA C4"), new("https://cdn.example.test/a.mp3", "again"), new("https://cdn.example.test/b.mp3", ""), new("https://cdn.example.test/c.mp3", "SHAKER")];
        List<AudioPreviewRequest> parsed = AiAudioPreviewSafety.ParseBatch(body.RootElement, [], 2, chosen);
        Assert.Equal(["https://cdn.example.test/b.mp3", "https://cdn.example.test/c.mp3"], parsed.Select(p => p.Url));
        Assert.Equal("AudioHero preview", parsed[0].Label);
        Assert.Throws<AmpleException>(() => AiAudioPreviewSafety.ParseBatch(body.RootElement, [], 2));
    }

    [Fact]
    public void The_task_token_gate_lets_the_sample_set_route_through_and_binaries_of_a_proposal_travel_with_the_builds()
    {
        string root = FindRoot();
        string server = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AmpleServer.cs"));
        string service = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiService.cs"));
        Assert.Contains("context.Request.Method == HttpMethods.Post) && context.Request.Path.StartsWithSegments(\"/api/ai/context\"", server, StringComparison.Ordinal);   // every context route, so also sample-set-add
        Assert.Contains("CopyStagedBinariesToStage(task, root, stagedBinaries)", service, StringComparison.Ordinal);
        Assert.Contains("task.IncludedPaths.Add(rel)", service, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("AMPLE repository root was not found.");
    }
}
