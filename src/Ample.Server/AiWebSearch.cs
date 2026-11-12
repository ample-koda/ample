using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;

namespace Ample.Server;

internal static class AiWebSearchSafety
{
    private static readonly ConcurrentDictionary<string, SearchCacheEntry> SearchCache = new(StringComparer.Ordinal);

    internal static bool TryGetCachedBrave(string apiKey, string query, int count, int offset, DateTimeOffset now, out JsonObject result)
    {
        string key = CacheKey(apiKey, query, count, offset);
        if (SearchCache.TryGetValue(key, out SearchCacheEntry? entry) && entry.ExpiresAt > now)
        {
            result = (JsonObject)entry.Result.DeepClone();
            result["cached"] = true;
            return true;
        }
        if (entry is not null) SearchCache.TryRemove(key, out _);
        result = [];
        return false;
    }

    internal static void CacheBrave(string apiKey, string query, int count, int offset, DateTimeOffset now, JsonObject result)
    {
        string key = CacheKey(apiKey, query, count, offset);
        SearchCache[key] = new SearchCacheEntry(now.AddMinutes(5), (JsonObject)result.DeepClone());
        if (SearchCache.Count > 100)
        {
            foreach ((string oldKey, SearchCacheEntry oldEntry) in SearchCache.Where(item => item.Value.ExpiresAt <= now).ToArray()) SearchCache.TryRemove(oldKey, out _);
            if (SearchCache.Count > 100)
                foreach ((string oldKey, SearchCacheEntry oldEntry) in SearchCache.OrderBy(item => item.Value.ExpiresAt).Take(SearchCache.Count - 100).ToArray()) SearchCache.TryRemove(oldKey, out _);
        }
    }

    private static string CacheKey(string apiKey, string query, int count, int offset)
    {
        string apiKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        string normalizedQuery = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        string queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery)));
        return $"{apiKeyHash}:{count}:{offset}:{queryHash}";
    }

    private sealed record SearchCacheEntry(DateTimeOffset ExpiresAt, JsonObject Result);

    internal static (int? Limit, int? Remaining, int? ResetSeconds) ParseBraveQuota(string? limit, string? remaining, string? reset)
    {
        static int? Monthly(string? value) => value?.Split(',').Select(part => int.TryParse(part.Trim(), out int parsed) ? parsed : (int?)null).Skip(1).FirstOrDefault();
        return (Monthly(limit), Monthly(remaining), Monthly(reset));
    }

    internal static void RememberResultUrls(AiTask task, JsonArray results)
    {
        foreach (JsonObject? item in results.OfType<JsonObject>())
        {
            if (item["url"]?.GetValue<string>() is string url) task.WebSearchResultUrls.TryAdd(url, 0);
        }
    }

    internal static JsonArray DuckDuckGoResults(JsonElement root, int limit)
    {
        JsonArray results = [];
        if (root.TryGetProperty("Heading", out JsonElement heading) && root.TryGetProperty("AbstractText", out JsonElement abstractText) && abstractText.GetString() is { Length: > 0 } summary && root.TryGetProperty("AbstractURL", out JsonElement abstractUrl))
            Add(results, heading.GetString() ?? "DuckDuckGo Instant Answer", abstractUrl.GetString() ?? string.Empty, summary, limit);
        if (root.TryGetProperty("RelatedTopics", out JsonElement topics)) AddTopics(results, topics, limit);
        return results;
    }

    internal static JsonArray Results(JsonElement root, int limit)
    {
        JsonArray results = [];
        if (!root.TryGetProperty("web", out JsonElement web) || !web.TryGetProperty("results", out JsonElement items) || items.ValueKind != JsonValueKind.Array) return results;
        foreach (JsonElement item in items.EnumerateArray().Take(limit))
        {
            string title = item.TryGetProperty("title", out JsonElement titleValue) ? titleValue.GetString() ?? string.Empty : string.Empty;
            string url = item.TryGetProperty("url", out JsonElement urlValue) ? urlValue.GetString() ?? string.Empty : string.Empty;
            string description = item.TryGetProperty("description", out JsonElement descriptionValue) ? descriptionValue.GetString() ?? string.Empty : string.Empty;
            if (title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) continue;
            results.Add(new JsonObject { ["title"] = title[..Math.Min(title.Length, 300)], ["url"] = uri.ToString(), ["snippet"] = description[..Math.Min(description.Length, 1200)] });
        }
        return results;
    }

    private static void AddTopics(JsonArray results, JsonElement topics, int limit)
    {
        if (topics.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement topic in topics.EnumerateArray())
        {
            if (results.Count >= limit) return;
            if (topic.TryGetProperty("Topics", out JsonElement nested)) { AddTopics(results, nested, limit); continue; }
            if (topic.TryGetProperty("Text", out JsonElement text) && topic.TryGetProperty("FirstURL", out JsonElement url)) Add(results, text.GetString() ?? string.Empty, url.GetString() ?? string.Empty, text.GetString() ?? string.Empty, limit);
        }
    }

    private static void Add(JsonArray results, string title, string url, string snippet, int limit)
    {
        if (results.Count >= limit || title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return;
        results.Add(new JsonObject { ["title"] = title[..Math.Min(title.Length, 300)], ["url"] = uri.ToString(), ["snippet"] = snippet[..Math.Min(snippet.Length, 1200)] });
    }
}

internal sealed partial class AiService
{
    private static readonly SemaphoreSlim BraveSearchGate = new(1, 1);
    private static DateTimeOffset? LastBraveSearchAt;
    private const int EstimatedBraveMonthlyLimit = 1000;

    public static async Task<JsonObject> WebSearchAsync(string token, string libraryHome, string query, string limitText, string offsetText, string providerText, string? projectApiKey, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? found) ? found : throw new AmpleException("AMP5458", "This task is no longer running.");
        string clean = query.Trim();
        if (clean.Length is < 2 or > 300) throw new AmpleException("AMP5458", "A web search query must contain 2 to 300 characters.");
        int limit = int.TryParse(limitText, out int parsed) ? Math.Clamp(parsed, 1, 10) : 5;
        int offset = int.TryParse(offsetText, out int parsedOffset) ? Math.Clamp(parsedOffset, 0, 9) : 0;
        string provider = providerText.Equals("brave", StringComparison.OrdinalIgnoreCase) ? "brave" : "duckduckgo";
        if (!task.WebSearchAllowed && task.Mode != "unsafe")
        {
            AiApproval ask = new() { Kind = "webSearch", Summary = $"{provider}: {clean}", Detail = provider == "brave" ? "AMPLE will send this query to Brave Search API and return up to 10 titles, snippets and links. It will not open result pages. Provider use requires a key; the free credit is limited, and no paid fallback is used." : "AMPLE will send this query to DuckDuckGo's free Instant Answer API. It may return no result; this is not a full web-results API. It will not open result pages or fall back to scraping." };
            task.Approvals[ask.Id] = ask;
            task.Status = "waiting-approval";
            task.Add("approval", "Codex asks AMPLE to search the public web using the query shown in the approval card.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
            string decision;
            try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
            ask.Answered = decision;
            task.Status = "running";
            if (decision is not ("accept" or "acceptForSession")) return new JsonObject { ["allowed"] = false, ["note"] = "Web search was not approved." };
            task.WebSearchAllowed = decision == "acceptForSession";
        }
        if (provider == "duckduckgo")
        {
            using HttpClient ddgHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
            string address = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(clean) + "&format=json&no_html=1&no_redirect=1";
            using HttpResponseMessage ddgResponse = await ddgHttp.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!ddgResponse.IsSuccessStatusCode) return new JsonObject { ["allowed"] = false, ["provider"] = "DuckDuckGo Instant Answer API", ["note"] = $"DuckDuckGo returned HTTP {(int)ddgResponse.StatusCode}. No HTML scraping fallback was attempted." };
            await using Stream ddgStream = await ddgResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument ddgDocument = await JsonDocument.ParseAsync(ddgStream, cancellationToken: ct).ConfigureAwait(false);
            JsonArray ddgResults = AiWebSearchSafety.DuckDuckGoResults(ddgDocument.RootElement, limit);
            AiWebSearchSafety.RememberResultUrls(task, ddgResults);
            return new JsonObject { ["allowed"] = true, ["provider"] = "DuckDuckGo Instant Answer API", ["query"] = clean, ["results"] = ddgResults, ["note"] = "DuckDuckGo's official free API returns Instant Answers, not full web search results. Results from DuckDuckGo." };
        }
        string? apiKey = projectApiKey ?? Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY_" + task.ProjectId.ToString("N", CultureInfo.InvariantCulture).ToUpperInvariant()) ?? Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return new JsonObject { ["allowed"] = false, ["configured"] = false, ["note"] = "No Brave Search key is configured for this project. Add it in AMPLE Project settings; a server-wide AMPLE_BRAVE_SEARCH_API_KEY is also accepted. Keys are never returned or logged." };
        if (AiWebSearchSafety.TryGetCachedBrave(apiKey, clean, limit, offset, DateTimeOffset.UtcNow, out JsonObject cached))
            return await CachedBraveResultAsync(libraryHome, task, apiKey, cached, ct).ConfigureAwait(false);
        await BraveSearchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (AiWebSearchSafety.TryGetCachedBrave(apiKey, clean, limit, offset, DateTimeOffset.UtcNow, out cached))
                return await CachedBraveResultAsync(libraryHome, task, apiKey, cached, ct).ConfigureAwait(false);
            if (task.BraveSearchCount >= 10) return new JsonObject { ["allowed"] = false, ["provider"] = "Brave Search API", ["note"] = "The limit of 10 actual Brave API calls for this Codex turn has been reached. Choose DuckDuckGo or continue with the information already gathered." };
            if (LastBraveSearchAt is DateTimeOffset last)
            {
                TimeSpan pause = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - last);
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
            using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using HttpRequestMessage request = new(HttpMethod.Get, "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(clean) + "&count=" + limit + "&offset=" + offset);
            request.Headers.TryAddWithoutValidation("X-Subscription-Token", apiKey);
            request.Headers.Accept.ParseAdd("application/json");
            BraveUsageEstimate usage = await BraveUsageTracker.ReserveAsync(libraryHome, apiKey, EstimatedBraveMonthlyLimit, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            if (usage.Remaining == 0) return new JsonObject { ["allowed"] = false, ["provider"] = "Brave Search API", ["estimatedMonthlyLimit"] = usage.Limit, ["estimatedMonthlyUsed"] = usage.Used, ["estimatedMonthlyRemaining"] = usage.Remaining, ["estimatedMonth"] = usage.Month, ["note"] = "AMPLE's local estimate reached 1,000 Brave requests for this API key this UTC month. No request was sent. This estimate counts only requests AMPLE makes with this key; Brave's response headers do not expose the free-credit balance." };
            task.BraveSearchCount++;
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            LastBraveSearchAt = DateTimeOffset.UtcNow;
            if ((int)response.StatusCode is 401 or 403) return new JsonObject { ["allowed"] = false, ["note"] = "Brave Search rejected the configured API key; the key was not logged.", ["estimatedMonthlyLimit"] = usage.Limit, ["estimatedMonthlyUsed"] = usage.Used, ["estimatedMonthlyRemaining"] = usage.Remaining, ["estimatedMonth"] = usage.Month };
            if ((int)response.StatusCode == 429) return new JsonObject { ["allowed"] = false, ["provider"] = "Brave Search API", ["estimatedMonthlyLimit"] = usage.Limit, ["estimatedMonthlyUsed"] = usage.Used, ["estimatedMonthlyRemaining"] = usage.Remaining, ["estimatedMonth"] = usage.Month, ["note"] = "Brave Search returned HTTP 429. AMPLE tracks an estimate from requests made with this key, but the provider response headers do not reveal the free-credit balance." };
            if (!response.IsSuccessStatusCode) return new JsonObject { ["allowed"] = false, ["note"] = $"Brave Search returned HTTP {(int)response.StatusCode}.", ["estimatedMonthlyLimit"] = usage.Limit, ["estimatedMonthlyUsed"] = usage.Used, ["estimatedMonthlyRemaining"] = usage.Remaining, ["estimatedMonth"] = usage.Month };
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            JsonArray results = AiWebSearchSafety.Results(document.RootElement, limit);
            AiWebSearchSafety.RememberResultUrls(task, results);
            bool more = document.RootElement.TryGetProperty("query", out JsonElement queryInfo) && queryInfo.TryGetProperty("more_results_available", out JsonElement moreResults) && moreResults.ValueKind == JsonValueKind.True;
            JsonObject result = new() { ["allowed"] = true, ["provider"] = "Brave Search API", ["query"] = clean, ["offset"] = offset, ["moreResultsAvailable"] = more, ["results"] = results, ["estimatedMonthlyLimit"] = usage.Limit, ["estimatedMonthlyUsed"] = usage.Used, ["estimatedMonthlyRemaining"] = usage.Remaining, ["estimatedMonth"] = usage.Month, ["note"] = $"AMPLE estimates {usage.Remaining} of {usage.Limit} Brave requests remaining for {usage.Month} (UTC), based only on requests AMPLE has made with this API key. This is not Brave's live credit balance; calls made elsewhere are not counted." };
            AiWebSearchSafety.CacheBrave(apiKey, clean, limit, offset, DateTimeOffset.UtcNow, result);
            return result;
        }
        finally { BraveSearchGate.Release(); }
    }

    private static async Task<JsonObject> CachedBraveResultAsync(string libraryHome, AiTask task, string apiKey, JsonObject cached, CancellationToken ct)
    {
        BraveUsageEstimate estimate = await BraveUsageTracker.ReadAsync(libraryHome, apiKey, EstimatedBraveMonthlyLimit, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        cached["estimatedMonthlyLimit"] = estimate.Limit;
        cached["estimatedMonthlyUsed"] = estimate.Used;
        cached["estimatedMonthlyRemaining"] = estimate.Remaining;
        cached["estimatedMonth"] = estimate.Month;
        if (cached["results"] is JsonArray results) AiWebSearchSafety.RememberResultUrls(task, results);
        cached["note"] = $"AMPLE reused this identical Brave query and result page from its five-minute in-memory cache; no API request was made. Local estimate: {estimate.Remaining} of {estimate.Limit} remaining for {estimate.Month} UTC. This is not Brave's live credit balance and does not count calls made elsewhere.";
        return cached;
    }
}
