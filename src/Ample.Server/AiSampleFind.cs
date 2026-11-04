using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>Reads what a search page of a sample site shows: the pages of single sounds, the title and the preview file of one, and the audio file of an Internet Archive item.</summary>
internal static partial class SampleFinder
{
    /// <summary>Gets the pages of single sounds (not packs, not users) that a Freesound search page links to, in order and without doubles.</summary>
    /// <param name="html">The search page.</param>
    /// <returns>Paths such as <c>/people/someone/sounds/123/</c>.</returns>
    internal static IReadOnlyList<string> SoundPaths(string html) => [.. SoundPath().Matches(html).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>Gets the best preview file a Freesound sound page names (the high-quality mp3, else the other mp3), or an empty string.</summary>
    /// <param name="html">The page of one sound.</param>
    /// <returns>The address of the preview.</returns>
    internal static string PreviewUrl(string html)
    {
        string[] previews = [.. Preview().Matches(html).Select(m => m.Value).Distinct(StringComparer.Ordinal)];
        return previews.FirstOrDefault(p => p.EndsWith("-hq.mp3", StringComparison.Ordinal)) ?? previews.FirstOrDefault(p => p.EndsWith(".mp3", StringComparison.Ordinal)) ?? string.Empty;
    }

    /// <summary>Gets the title of a page without the site name in front of it.</summary>
    /// <param name="html">The page.</param>
    /// <returns>The title, or an empty string.</returns>
    internal static string PageTitle(string html)
    {
        Match m = Title().Match(html);
        string title = m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
        return title.StartsWith("Freesound - ", StringComparison.Ordinal) ? title["Freesound - ".Length..] : title;
    }

    /// <summary>Picks the audio file of an Internet Archive item to use: the smallest wav, mp3, ogg or flac that is at most 25 MB (from the metadata of the item), or an empty string.</summary>
    /// <param name="metadata">The JSON of <c>https://archive.org/metadata/&lt;id&gt;</c>.</param>
    /// <param name="id">The identifier of the item.</param>
    /// <returns>The address of the file.</returns>
    internal static string ArchiveFile(string metadata, string id)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadata);
            if (!doc.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            (string Name, long Size)? best = null;
            foreach (JsonElement file in files.EnumerateArray())
            {
                string name = file.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                long size = file.TryGetProperty("size", out JsonElement s) && long.TryParse(s.GetString(), out long parsed) ? parsed : 0;
                if (SampleLicence.AudioExtension(new Uri("https://archive.org/download/" + id + "/" + Uri.EscapeDataString(name))).Length > 0 && size is > 512 and <= 25_000_000 && (best is null || size < best.Value.Size))
                {
                    best = (name, size);
                }
            }

            return best is null ? string.Empty : "https://archive.org/download/" + id + "/" + string.Join('/', best.Value.Name.Split('/').Select(Uri.EscapeDataString));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"href=""(/people/[^""/]+/sounds/\d+/)""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SoundPath();

    [GeneratedRegex(@"https://cdn\.freesound\.org/previews/[A-Za-z0-9_/.\-]+?\.(?:mp3|ogg)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Preview();

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Title();
}

internal sealed partial class AiService
{
    /// <summary>
    /// Finds real samples for the AI by itself: the CC0 search of Freesound (and the public-domain search of the Internet Archive) is read without a sign-in and without JavaScript, every candidate page is read
    /// and its licence checked, and what comes back is ready for ample_fetch_sample (page and file). Read-only; nothing is downloaded.
    /// </summary>
    public static async Task<JsonObject> FindSamplesAsync(string token, string text, string source, int limit, CancellationToken ct)
    {
        if (!ContextTokens.ContainsKey(token))
        {
            throw new AmpleException("AMP5458", "This task is no longer running.");
        }

        string words = text.Trim();
        if (words.Length == 0 || words.Length > 120)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "Give the words of the sound you look for, for example dj scratch." };
        }

        int wanted = Math.Clamp(limit <= 0 ? 5 : limit, 1, 8);
        string where = source.Trim().ToLowerInvariant();
        using SocketsHttpHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 4, ConnectCallback = ConnectPublicOnlyAsync, ConnectTimeout = TimeSpan.FromSeconds(15) };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMPLE/1.0 (+AI assistant sample search for the owner)");
        List<JsonObject> found = [];
        JsonArray notes = [];
        try
        {
            if (where is "" or "freesound" or "all")
            {
                await FindOnFreesoundAsync(http, words, wanted, found, notes, ct).ConfigureAwait(false);
            }

            if (where is "archive" or "all" || (where.Length == 0 && found.Count == 0))
            {
                await FindOnArchiveAsync(http, words, wanted, found, notes, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RegexMatchTimeoutException or JsonException)
        {
            notes.Add("A search stopped: " + ex.Message);
        }

        return new JsonObject
        {
            ["ok"] = found.Count > 0,
            ["candidates"] = new JsonArray([.. found.Select(f => (JsonNode)f)]),
            ["notes"] = notes,
            ["next"] = found.Count > 0 ? "Pick the sound that fits and call ample_fetch_sample with its page and file (no browser, no sign-in needed). Each candidate's licence was read from its own page." : "Nothing with a CC0 or public-domain licence was found for these words: try other words (for example record scratch, turntable, vinyl), another source (archive) or tell the owner.",
        };
    }

    private static async Task FindOnFreesoundAsync(HttpClient http, string words, int wanted, List<JsonObject> found, JsonArray notes, CancellationToken ct)
    {
        string search = "https://freesound.org/search/?q=" + Uri.EscapeDataString(words) + "&f=license%3A%22Creative+Commons+0%22";
        string html = await http.GetStringAsync(search, ct).ConfigureAwait(false);
        string[] paths = [.. SampleFinder.SoundPaths(html).Take(wanted * 2)];
        if (paths.Length == 0)
        {
            notes.Add("Freesound listed no single sound for these words.");
            return;
        }

        using SemaphoreSlim gate = new(4);
        JsonObject?[] results = await Task.WhenAll(paths.Select(async path =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string page = "https://freesound.org" + path;
                string body = await http.GetStringAsync(page, ct).ConfigureAwait(false);
                (bool ok, string licence, _) = SampleLicence.Check(body);
                string preview = SampleFinder.PreviewUrl(body);
                return ok && preview.Length > 0 ? new JsonObject { ["source"] = "freesound", ["title"] = SampleFinder.PageTitle(body), ["page"] = page, ["file"] = preview, ["licence"] = licence, ["note"] = "mp3 preview of the sound (the original wav needs a sign-in)" } : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RegexMatchTimeoutException)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        found.AddRange(results.Where(r => r is not null).Cast<JsonObject>().Take(wanted));
    }

    private static async Task FindOnArchiveAsync(HttpClient http, string words, int wanted, List<JsonObject> found, JsonArray notes, CancellationToken ct)
    {
        string query = $"({words}) AND mediatype:audio AND (licenseurl:*publicdomain*)";
        string json = await http.GetStringAsync("https://archive.org/advancedsearch.php?q=" + Uri.EscapeDataString(query) + "&fl[]=identifier&fl[]=title&rows=" + (wanted * 2) + "&output=json", ct).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out JsonElement response) || !response.TryGetProperty("docs", out JsonElement docs))
        {
            notes.Add("The Internet Archive answered in a form AMPLE does not know.");
            return;
        }

        int before = found.Count;
        foreach (JsonElement item in docs.EnumerateArray())
        {
            if (found.Count - before >= wanted)
            {
                break;
            }

            string id = item.TryGetProperty("identifier", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
            if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            {
                continue;
            }

            string metadata = await http.GetStringAsync("https://archive.org/metadata/" + id, ct).ConfigureAwait(false);
            (bool ok, string licence, _) = SampleLicence.Check(metadata);
            string file = SampleFinder.ArchiveFile(metadata, id);
            if (ok && file.Length > 0)
            {
                found.Add(new JsonObject { ["source"] = "archive", ["title"] = item.TryGetProperty("title", out JsonElement t) ? t.ToString() : id, ["page"] = "https://archive.org/details/" + id, ["file"] = file, ["licence"] = licence, ["note"] = "the smallest audio file of the item" });
            }
        }

        if (found.Count == before)
        {
            notes.Add("The Internet Archive had no public-domain audio for these words.");
        }
    }
}
