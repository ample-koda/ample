using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Microsoft.Playwright;

namespace Ample.Server;

/// <summary>What a page has to show before a sample is fetched from it: a CC0 or public-domain licence, on a host that is known for it.</summary>
internal static partial class SampleLicence
{
    private static readonly string[] Hosts = ["freesound.org", "archive.org", "opengameart.org"];
    private static readonly string[] Extensions = [".wav", ".mp3", ".ogg", ".flac", ".aif", ".aiff"];

    /// <summary>Gets a value that says whether an address is on one of the hosts a sample may come from (the host or one of its subdomains, over http or https, without a password).</summary>
    /// <param name="uri">The address.</param>
    /// <returns>True when it is allowed.</returns>
    internal static bool AllowedHost(Uri uri) => uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets the extension of an audio file that can be brought in (empty when the address is not one).</summary>
    /// <param name="uri">The address of the file.</param>
    /// <returns>The extension with its dot, lower case, or an empty string.</returns>
    internal static string AudioExtension(Uri uri) => Extensions.FirstOrDefault(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    /// <summary>
    /// Reads the licence from the text of a page (or from the metadata of an Internet Archive item): CC0 or a public-domain mark counts, and only when no other Creative Commons licence
    /// (by, by-nc, by-sa …) is named on the same page, so a page with several sounds of several licences is never taken for CC0.
    /// </summary>
    /// <param name="body">The page text or metadata.</param>
    /// <returns>Whether it is CC0 or public domain, its name and the address that showed it.</returns>
    internal static (bool Ok, string Licence, string Evidence) Check(string body)
    {
        string text = body.Replace("\\/", "/", StringComparison.Ordinal);
        Match zero = Zero().Match(text), mark = Mark().Match(text);
        if (OtherLicence().IsMatch(text) || !(zero.Success || mark.Success))
        {
            return (false, OtherLicence().IsMatch(text) ? "another Creative Commons licence" : "no CC0 or public-domain licence found", string.Empty);
        }

        return zero.Success ? (true, "CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/") : (true, "Public Domain Mark 1.0", "https://creativecommons.org/publicdomain/mark/1.0/");
    }

    /// <summary>Gets a file name for a fetched sample: the title, lower case letters and digits, with the extension.</summary>
    /// <param name="title">The title the AI gave.</param>
    /// <param name="extension">The extension with its dot.</param>
    /// <returns>A name without any path.</returns>
    internal static string FileName(string title, string extension)
    {
        string slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim('-');
        return (slug.Length == 0 ? "sample" : slug[..Math.Min(slug.Length, 40)]) + extension;
    }

    [GeneratedRegex(@"creativecommons\.org/publicdomain/zero/1\.0", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Zero();

    [GeneratedRegex(@"creativecommons\.org/publicdomain/mark/1\.0", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Mark();

    [GeneratedRegex(@"creativecommons\.org/licenses/", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OtherLicence();
}

internal sealed partial class AiService
{
    private const int SampleLimit = 25_000_000;

    /// <summary>
    /// Fetches a real sample for the proposal: AMPLE reads the page itself, and only a CC0 or public-domain licence on freesound.org, archive.org or opengameart.org lets the audio file
    /// through (so no approval is asked: the licence is checked here, not trusted from the AI). The file lands in assets/web of the working copy (listen to it, bring it in with
    /// ample_sample_set_add) and the answer holds a ready SOURCE.md text with the link, the licence and the date. At most 25 MB; nothing is bought.
    /// </summary>
    public static async Task<JsonObject> FetchSampleAsync(string token, string pageUrl, string fileUrl, string title, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? found) ? found : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (!Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out Uri? page) || !Uri.TryCreate(fileUrl.Trim(), UriKind.Absolute, out Uri? file) || !SampleLicence.AllowedHost(page) || !SampleLicence.AllowedHost(file))
        {
            return new JsonObject { ["ok"] = false, ["note"] = "Both addresses must be https pages and files on freesound.org, archive.org or opengameart.org. Another site is not fetched automatically: tell the owner where it is and let them decide." };
        }

        string extension = SampleLicence.AudioExtension(file);
        if (extension.Length == 0)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "The file address must end in .wav, .mp3, .ogg, .flac or .aif(f)." };
        }

        // an Internet Archive item states its licence in its metadata, not on the page
        Uri licenceSource = page.Host.EndsWith("archive.org", StringComparison.OrdinalIgnoreCase) && page.AbsolutePath.StartsWith("/details/", StringComparison.Ordinal) ? new Uri($"https://archive.org/metadata/{page.AbsolutePath["/details/".Length..].Split('/')[0]}") : page;
        using SocketsHttpHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 4, ConnectCallback = ConnectPublicOnlyAsync, ConnectTimeout = TimeSpan.FromSeconds(15) };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMPLE/1.0 (+AI assistant sample fetch for the owner)");
        try
        {
            using HttpResponseMessage pageResponse = await http.GetAsync(licenceSource, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!pageResponse.IsSuccessStatusCode || pageResponse.RequestMessage?.RequestUri is not { } landed || !SampleLicence.AllowedHost(landed) || pageResponse.Content.Headers.ContentLength is > 2_000_000)
            {
                return new JsonObject { ["ok"] = false, ["note"] = $"The page could not be read ({(int)pageResponse.StatusCode}) or it moved to another site." };
            }

            string body = await pageResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            (bool ok, string licence, string evidence) = SampleLicence.Check(body.Length > 2_000_000 ? body[..2_000_000] : body);
            if (!ok)
            {
                return new JsonObject { ["ok"] = false, ["licence"] = licence, ["note"] = "AMPLE did not find a CC0 or public-domain licence on that page (" + licence + "), so the file is not fetched. Look for another sample, or give the owner the page and let them decide." };
            }

            using HttpResponseMessage fileResponse = await http.GetAsync(file, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!fileResponse.IsSuccessStatusCode || fileResponse.RequestMessage?.RequestUri is not { } fileLanded || !SampleLicence.AllowedHost(fileLanded))
            {
                return new JsonObject { ["ok"] = false, ["note"] = $"The file could not be fetched ({(int)fileResponse.StatusCode}) or it moved to another site." };
            }

            if (fileResponse.Content.Headers.ContentLength is > SampleLimit)
            {
                return new JsonObject { ["ok"] = false, ["note"] = "The file is larger than 25 MB." };
            }

            await using Stream source = await fileResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > SampleLimit)
                {
                    return new JsonObject { ["ok"] = false, ["note"] = "The file is larger than 25 MB." };
                }
            }

            if (buffer.Length < 512)
            {
                return new JsonObject { ["ok"] = false, ["note"] = "The file is empty or not audio." };
            }

            return await StoreSampleAsync(task, buffer.ToArray(), page, file, title, extension, licence, evidence, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or RegexMatchTimeoutException)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "The sample could not be fetched: " + ex.Message };
        }
    }

    /// <summary>Puts a fetched sample in assets/web of the working copy, lists it as a clip to listen to and builds the answer with the ready SOURCE.md text.</summary>
    private static async Task<JsonObject> StoreSampleAsync(AiTask task, byte[] bytes, Uri page, Uri file, string title, string extension, string licence, string evidence, CancellationToken ct)
    {
        string name = SampleLicence.FileName(title.Trim().Length > 0 ? title : Path.GetFileNameWithoutExtension(file.AbsolutePath), extension);
        string target = Path.Combine(task.Workspace, "assets", "web", name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string sourceNote = $"# Source\n\n- File: {name}\n- Title: {title.Trim()}\n- Page: {page}\n- File address: {file}\n- Licence: {licence} ({evidence})\n- Fetched: {date} by AMPLE (the licence was read from the page, not assumed)\n";
        string id = Guid.NewGuid().ToString("N")[..10];
        AiClip clip = new() { Id = id, Label = title.Trim().Length > 0 ? title.Trim() : name, Block = "Sample", Source = "external", Why = $"{licence} sample from {page.Host}", File = target, WorkspaceAssetPath = "assets/web/" + name, AudioHash = Convert.ToHexStringLower(SHA256.HashData(bytes)), MediaType = extension is ".wav" ? "audio/wav" : extension is ".mp3" ? "audio/mpeg" : extension is ".flac" ? "audio/flac" : extension is ".ogg" ? "audio/ogg" : "audio/aiff" };
        lock (task.ClipsLock)
        {
            task.Clips.Add(clip);
        }

        task.Add("file", $"AMPLE fetched a {licence} sample from {page.Host} ({bytes.Length / 1000 + 1} kB) to assets/web/{name}.");
        return new JsonObject
        {
            ["ok"] = true,
            ["path"] = "assets/web/" + name,
            ["clipId"] = id,
            ["licence"] = licence,
            ["size"] = bytes.Length,
            ["sourceNote"] = sourceNote,
            ["note"] = "Fetched and licence-checked by AMPLE. Next: ample_analyze_audio source=workspace/assets/web/" + name + ", then bring it into the project: The fetched file is only in your working folder, NOT in the project: a block that names assets/web/<file> fails the build (AMP5350 (not in the project)). Stage it first with ample_copy_file source=workspace/assets/web/<file> to a project path such as samples/<file> (an mp3 or text asset; a wav goes in through ample_sample_set_add), and give the block that project path. Use ample_sample_set_add or a sampler block for a kit, and write the sourceNote text as SOURCE.md next to the sample (ample_edit create). Tell the owner where it came from.",
        };
    }

    /// <summary>
    /// The same fetch with the browser of AMPLE, for a sound that needs a sign-in: the owner signs in once in the visible browser window (its profile keeps the session), AMPLE reads the licence from the
    /// page as the signed-in browser shows it and downloads the file with the cookies of that browser. The same hosts, the same CC0 or public-domain rule and the same limits; nothing else is clicked or bought.
    /// </summary>
    public async Task<JsonObject> FetchSampleViaBrowserAsync(string token, string pageUrl, string fileUrl, string title, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Fetch a CC0 sample with the signed-in browser", ct).ConfigureAwait(false);
        if (!Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out Uri? page) || !Uri.TryCreate(fileUrl.Trim(), UriKind.Absolute, out Uri? file) || !SampleLicence.AllowedHost(page) || !SampleLicence.AllowedHost(file) || !await IsPublicTargetAsync(page, ct).ConfigureAwait(false) || !await IsPublicTargetAsync(file, ct).ConfigureAwait(false))
        {
            return new JsonObject { ["ok"] = false, ["note"] = "Both addresses must be https pages and files on freesound.org, archive.org or opengameart.org." };
        }

        string extension = SampleLicence.AudioExtension(file);
        if (extension.Length == 0)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "The file address must end in .wav, .mp3, .ogg, .flac or .aif(f)." };
        }

        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage browserPage = await BrowserPageAsync(task, ct).ConfigureAwait(false);
            await browserPage.GotoAsync(page.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 }).ConfigureAwait(false);
            string humanReason = await BrowserSafety.HumanInputReasonAsync(browserPage).ConfigureAwait(false);
            if (humanReason.Length > 0)
            {
                await SetBrowserVisibilityAsync(task, browserPage.Url, true, ct).ConfigureAwait(false);
                return new JsonObject { ["ok"] = false, ["humanInputRequired"] = true, ["note"] = "The page seems to ask for a sign-in or a check by a person (" + humanReason + "). The browser window of AMPLE is now visible: ask the owner to sign in there and to tell you when it is done, then call this tool again." };
            }

            if (!Uri.TryCreate(browserPage.Url, UriKind.Absolute, out Uri? landed) || !SampleLicence.AllowedHost(landed))
            {
                return new JsonObject { ["ok"] = false, ["note"] = "The page moved to another site, so nothing is fetched." };
            }

            string html = await browserPage.ContentAsync().ConfigureAwait(false);
            (bool ok, string licence, string evidence) = SampleLicence.Check(html.Length > 2_000_000 ? html[..2_000_000] : html);
            if (!ok)
            {
                return new JsonObject { ["ok"] = false, ["licence"] = licence, ["note"] = "The page does not show a CC0 or public-domain licence (" + licence + "), even signed in, so the file is not fetched. Look for another sample." };
            }

            IAPIResponse response = await browserContext!.APIRequest.GetAsync(file.ToString(), new APIRequestContextOptions { Timeout = 40000, MaxRedirects = 4 }).ConfigureAwait(false);
            if (!response.Ok || !Uri.TryCreate(response.Url, UriKind.Absolute, out Uri? fileLanded) || !SampleLicence.AllowedHost(fileLanded))
            {
                return new JsonObject { ["ok"] = false, ["note"] = $"The file could not be fetched ({response.Status}). If it says 401 or 403 the owner is not signed in yet: show the browser and ask them to sign in." };
            }

            byte[] bytes = await response.BodyAsync().ConfigureAwait(false);
            if (bytes.Length is < 512 or > SampleLimit)
            {
                return new JsonObject { ["ok"] = false, ["note"] = "The file is empty, not audio, or larger than 25 MB." };
            }

            return await StoreSampleAsync(task, bytes, page, file, title, extension, licence, evidence + "; read with the signed-in browser", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return new JsonObject { ["ok"] = false, ["note"] = "The browser could not fetch the sample: " + ex.Message };
        }
        finally
        {
            browserActionGate.Release();
        }
    }
}
