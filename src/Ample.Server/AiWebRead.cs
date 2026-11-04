using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Server;

internal sealed partial class AiService
{
    public static async Task<JsonObject> WebReadAsync(string token, string url, string modeText, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? found) ? found : throw new AmpleException("AMP5458", "This task is no longer running.");
        string clean = url.Trim();
        if (!Uri.TryCreate(clean, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AmpleException("AMP5458", "Only an http or https page address without credentials can be read.");
        bool userProvided = task.Request.Prompt.Contains(clean, StringComparison.OrdinalIgnoreCase) || task.Log.Any(item => item.Kind == "user" && item.Text.Contains(clean, StringComparison.OrdinalIgnoreCase));
        if (!userProvided && !task.WebSearchResultUrls.ContainsKey(uri.ToString()))
            return new JsonObject { ["allowed"] = false, ["note"] = "This no-extra-approval reader is limited to a page URL the user supplied or a URL returned by an approved web search in this task. Search for the page first or use ample_fetch_url for its separate approval flow." };

        try
        {
        using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, ConnectCallback = ConnectPublicOnlyAsync, ConnectTimeout = TimeSpan.FromSeconds(15) };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMPLE/1.0 (+AI assistant public page reader)");
        HttpResponseMessage response;
        for (int redirects = 0; ; redirects++)
        {
            response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null) break;
            using (response)
            {
                if (redirects >= 4) return new JsonObject { ["ok"] = false, ["note"] = "The page redirected more than four times." };
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return new JsonObject { ["ok"] = false, ["note"] = "The page redirected to an unsupported address." };
            }
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return new JsonObject { ["ok"] = false, ["status"] = (int)response.StatusCode, ["note"] = "The public page request failed; try the approved Playwright browser if the site requires JavaScript or human verification." };
            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase) && !contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                return new JsonObject { ["ok"] = false, ["contentType"] = contentType, ["note"] = "This reader handles HTML pages only. Use ample_fetch_url for a supported file or the browser when the page needs JavaScript." };
            if (response.Content.Headers.ContentLength is > 2_000_000) return new JsonObject { ["ok"] = false, ["note"] = "The HTML page is larger than the 2 MB read limit." };
            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[32_768];
            int read;
            while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > 2_000_000) return new JsonObject { ["ok"] = false, ["note"] = "The HTML page is larger than the 2 MB read limit." };
            }
            string html = Encoding.UTF8.GetString(buffer.ToArray());
            string mode = modeText.Trim().ToLowerInvariant();
            if (mode is not ("text" or "links" or "html")) mode = "text";
            JsonObject result = new() { ["ok"] = true, ["url"] = uri.ToString(), ["status"] = (int)response.StatusCode, ["contentType"] = contentType, ["mode"] = mode, ["links"] = ExtractLinks(html, uri) };
            if (mode is "text" or "html") result["title"] = ExtractTitle(html);
            if (mode == "text") result["text"] = ExtractText(html);
            if (mode == "html") result["html"] = html.Length > 40_000 ? html[..40_000] + "\n… (truncated at 40,000 characters)" : html;
            result["note"] = "Fetched without browser cookies or JavaScript; page controls were not activated. If content is missing because the site needs JavaScript, sign-in or CAPTCHA, use ample_browser_open after browser approval.";
            return result;
        }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or RegexMatchTimeoutException)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "The plain HTTP page read failed or exceeded its parsing limit. If the site needs JavaScript, sign-in or human verification, use ample_browser_open after browser approval." };
        }
    }

    private static string ExtractTitle(string html) => Decode(Regex.Match(html, @"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Groups[1].Value);

    private static string ExtractText(string html)
    {
        string text = Regex.Replace(html, @"<(script|style|noscript)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"<!--.*?-->|<[^>]+>", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = Regex.Replace(Decode(text), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
        return text.Length > 40_000 ? text[..40_000] + " … (truncated)" : text;
    }

    private static JsonArray ExtractLinks(string html, Uri baseUri)
    {
        JsonArray links = [];
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*?href\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))[^>]*>(.*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Cast<Match>().Take(100))
        {
            string rawUrl = Decode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
            string label = Decode(Regex.Replace(match.Groups[4].Value, @"<[^>]+>", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(1))).Trim();
            if (label.Length > 300) label = label[..300];
            if (Uri.TryCreate(baseUri, rawUrl, out Uri? link) && link.Scheme is "http" or "https" && string.IsNullOrEmpty(link.UserInfo)) links.Add(new JsonObject { ["text"] = label, ["url"] = link.ToString() });
        }
        return links;
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value).Trim();
}
