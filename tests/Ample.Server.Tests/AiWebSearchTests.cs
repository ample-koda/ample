using System.Text.Json;

namespace Ample.Server.Tests;

public sealed class AiWebSearchTests
{
    [Fact]
    public void DuckDuckGo_instant_answers_parse_summary_and_related_links()
    {
        using JsonDocument fixture = JsonDocument.Parse("""{"Heading":"Topic","AbstractText":"An instant summary","AbstractURL":"https://example.test/topic","RelatedTopics":[{"Text":"Related item","FirstURL":"https://example.test/related"}]}""");

        System.Text.Json.Nodes.JsonArray results = AiWebSearchSafety.DuckDuckGoResults(fixture.RootElement, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("https://example.test/related", results[1]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Brave_search_results_reject_non_http_urls()
    {
        using JsonDocument fixture = JsonDocument.Parse("""{"web":{"results":[{"title":"One","url":"https://example.test/one","description":"Snippet"},{"title":"Three","url":"javascript:alert(1)","description":"Invalid"}]}}""");

        System.Text.Json.Nodes.JsonArray results = AiWebSearchSafety.Results(fixture.RootElement, 3);

        Assert.Single(results);
        Assert.Equal("https://example.test/one", results[0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Brave_rate_limit_headers_report_monthly_limit_remaining_and_reset()
    {
        var quota = AiWebSearchSafety.ParseBraveQuota("1, 1000", "0, 742", "1, 2592000");

        Assert.Equal(1000, quota.Limit);
        Assert.Equal(742, quota.Remaining);
        Assert.Equal(2592000, quota.ResetSeconds);
    }

    [Fact]
    public void Brave_rate_limit_headers_without_monthly_values_are_unknown()
    {
        var quota = AiWebSearchSafety.ParseBraveQuota("1", "0", null);

        Assert.Null(quota.Limit);
        Assert.Null(quota.Remaining);
        Assert.Null(quota.ResetSeconds);
    }

    [Fact]
    public async Task Brave_usage_estimate_persists_only_a_key_fingerprint_and_resets_by_utc_month()
    {
        string root = Directory.CreateTempSubdirectory("ample-brave-usage-test-").FullName;
        const string apiKey = "synth-" + "etic-brave-key-marker";
        try
        {
            DateTimeOffset october = new(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(2));
            BraveUsageEstimate used = await BraveUsageTracker.ReserveAsync(root, apiKey, 2, october, CancellationToken.None);
            BraveUsageEstimate persisted = await BraveUsageTracker.ReadAsync(root, apiKey, 2, october, CancellationToken.None);
            BraveUsageEstimate november = await BraveUsageTracker.ReadAsync(root, apiKey, 2, october.AddHours(3), CancellationToken.None);

            Assert.Equal((1, 1, "2026-10"), (used.Used, used.Remaining, used.Month));
            Assert.Equal((1, 1, "2026-10"), (persisted.Used, persisted.Remaining, persisted.Month));
            Assert.Equal((0, 2, "2026-11"), (november.Used, november.Remaining, november.Month));
            string[] stateFiles = Directory.GetFiles(Path.Combine(root, "web-search-usage"), "*.json");
            Assert.Single(stateFiles);
            Assert.DoesNotContain(apiKey, await File.ReadAllTextAsync(stateFiles[0]), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Brave_duplicate_cache_expires_after_five_minutes()
    {
        const string apiKey = "synth-" + "etic-cache-key-marker";
        const string query = "synthetic-cache-query-marker";
        DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        System.Text.Json.Nodes.JsonObject result = new() { ["results"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["url"] = "https://example.test/synthetic" }) };
        AiWebSearchSafety.CacheBrave(apiKey, query, 2, 0, now, result);

        Assert.True(AiWebSearchSafety.TryGetCachedBrave(apiKey, query, 2, 0, now.AddMinutes(4), out System.Text.Json.Nodes.JsonObject cached));
        Assert.True(cached["cached"]!.GetValue<bool>());
        Assert.False(AiWebSearchSafety.TryGetCachedBrave(apiKey, query, 2, 0, now.AddMinutes(5), out _));
    }

    [RepoFact("src/Ample.Cli/McpCommands.cs")]
    public void Web_search_tool_is_approval_gated_and_has_a_dedicated_route()
    {
        string root = FindRoot();
        string mcp = File.ReadAllText(Path.Combine(root, "src", "Ample.Cli", "McpCommands.cs"));
        string routes = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "Routes.cs"));
        string instructions = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiService.cs"));

        Assert.Contains("ample_web_search", mcp, StringComparison.Ordinal);
        Assert.Contains("ample_web_search", mcp.Split('\n').Single(line => line.Contains("readOnlyHint", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("ample_web_search", mcp.Split('\n').Single(line => line.Contains("if (tool is", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("/api/ai/context/web-search", routes, StringComparison.Ordinal);
        Assert.Contains("AMPLE_BRAVE_SEARCH_API_KEY_", File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiWebSearch.cs")), StringComparison.Ordinal);
        Assert.Contains("Choose DuckDuckGo for lightweight Instant Answers", instructions, StringComparison.Ordinal);
        Assert.Contains("hard cap of 10 actual API calls per Codex turn", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monthly remaining", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Brave_turn_cap_is_enforced_and_reset_at_each_new_turn_start()
    {
        string root = FindRoot();
        string search = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiWebSearch.cs"));
        string service = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiService.cs"));

        Assert.Contains("task.BraveSearchCount >= 10", search, StringComparison.Ordinal);
        Assert.Equal(3, service.Split("task.BraveSearchCount = 0", StringSplitOptions.None).Length - 1);
    }

    [RepoFact("src/Ample.Cli/McpCommands.cs")]
    public void Plain_web_reader_and_browser_show_are_registered_and_routed()
    {
        string root = FindRoot();
        string mcp = File.ReadAllText(Path.Combine(root, "src", "Ample.Cli", "McpCommands.cs"));
        string routes = File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "Routes.cs"));

        Assert.Contains("\"ample_web_read\"", mcp, StringComparison.Ordinal);
        Assert.Contains("\"ample_browser_show\"", mcp, StringComparison.Ordinal);
        Assert.Contains("/api/ai/context/web-read", routes, StringComparison.Ordinal);
        Assert.Contains("/api/ai/context/browser-show", routes, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("AMPLE repository root was not found.");
    }
}
