using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Microsoft.Playwright;

namespace Ample.Server;

internal sealed record BrowserElement(IElementHandle Handle, string Tag, string Text, string AriaLabel, string Href, bool CanType);

internal static class BrowserSafety
{
    private const string InteractiveSelector = "a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=link],[contenteditable=true],[onclick],[tabindex]:not([tabindex='-1'])";
    private static readonly string[] AudioHeroFallbackLabels = ["Search", "Free Sounds", "Search Music", "Search Sound Effects", "See results", "See Results", "Login"];
    private static readonly string[] HumanInputPhrases = ["captcha", "verify you are human", "confirm you are not a robot", "checking your browser", "human verification", "sign in to continue", "log in to continue", "enter your password", "access denied"];
    private static readonly string[] PurchasePhrases = ["buy", "purchase", "checkout", "pay", "payment", "confirm", "place order", "place your order", "order now", "add to cart", "add item to cart", "add to basket", "buy now", "purchase now", "confirm order", "confirm payment", "confirm checkout", "confirm purchase", "complete order", "complete payment", "proceed to payment", "kopen", "koop", "koop nu", "bestel", "bestellen", "bestelling", "bevestig", "bevestigen", "bevestig bestelling", "bevestig betaling", "betaling", "betalen", "betaal", "betaal nu", "afrekenen", "winkelwagen", "winkelmand", "in winkelwagen", "toevoegen aan winkelwagen", "bestelling plaatsen"];
    private static readonly string[] CredentialWords = ["password", "username", "user name", "login", "account", "e-mail", "email"];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg", ".m4a", ".aac", ".flac"];

    internal static bool IsPurchaseAction(string? text) => PurchasePhrases.Any(phrase => System.Text.RegularExpressions.Regex.IsMatch(text ?? string.Empty, $@"(?<!\p{{L}}){System.Text.RegularExpressions.Regex.Escape(phrase)}(?!\p{{L}})", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    internal static string SanitizeConsoleMessage(string message)
    {
        string safe = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)(authorization|set-cookie|cookie|password|passwd|token|secret|api[_-]?key)\s*[:=]\s*[^\s,;]+", "$1=[redacted]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"https?://[^\s""'<>]+", match => Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) + "/[path and query omitted]" : "[url omitted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return safe.Length > 600 ? safe[..600] + "…" : safe;
    }

    internal static bool HasAccess(AiTask task, DateTimeOffset now, bool projectAllowed) => projectAllowed || task.BrowserAllowedForTask || task.BrowserAccessUntil is { } until && until > now;

    internal static bool HasAccess(AiTask task, DateTimeOffset now) => HasAccess(task, now, false);

    internal static async Task<bool> NeedsHumanInputAsync(IPage page) => (await HumanInputReasonAsync(page).ConfigureAwait(false)).Length > 0;

    /// <summary>Why a page needs a person (a visible password field, a sign-in or check address, a check phrase on the page), or an empty string when it does not.</summary>
    internal static async Task<string> HumanInputReasonAsync(IPage page)
    {
        string url = page.Url;
        string title = await page.TitleAsync().ConfigureAwait(false);
        // only a password field a person can see counts: a signed-in page often keeps a hidden login form in its markup
        if (await page.Locator("input[type='password']:visible").CountAsync().ConfigureAwait(false) > 0) return "a visible password field";
        if (Regex.IsMatch(url, @"/(login|log-in|signin|sign-in|captcha|challenge)(/|\?|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "a sign-in or check address";
        string body = string.Empty;
        try { body = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3000 }).ConfigureAwait(false); }
        catch (PlaywrightException) { }
        string sample = (title + " " + body).ToLowerInvariant();
        string phrase = HumanInputPhrases.FirstOrDefault(sample.Contains) ?? string.Empty;
        return phrase.Length > 0 ? $"the text '{phrase}' on the page" : string.Empty;
    }

    internal static bool IsWebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static bool IsAudioResponse(string contentType, string url) => contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && AudioExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    internal static bool IsAudioHeroPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Host.Equals("audiohero.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".audiohero.com", StringComparison.OrdinalIgnoreCase));

    internal static bool IsAudioHeroPreviewControl(string text, string ariaLabel) =>
        !IsPurchaseAction(text + " " + ariaLabel) && System.Text.RegularExpressions.Regex.IsMatch(text + " " + ariaLabel, @"(?<!\p{L})(listen|preview|play)(?!\p{L})", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void Grant(AiTask task, string decision, DateTimeOffset now)
    {
        task.BrowserAllowedForTask = decision == "acceptForSession";
        task.BrowserAccessUntil = decision == "accept30" ? now.AddMinutes(30) : null;
    }

    internal static bool IsCredentialField(string type, string name, string autocomplete, string label) =>
        type is "password" or "email" or "tel" || autocomplete.Contains("password", StringComparison.OrdinalIgnoreCase) || autocomplete.Contains("username", StringComparison.OrdinalIgnoreCase) || autocomplete.Contains("email", StringComparison.OrdinalIgnoreCase) ||
        CredentialWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase) || label.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldHideCredentialControl(bool canType, bool credential) => canType && credential;

    internal static async Task<IReadOnlyList<IElementHandle>> FindInteractiveElementsAsync(IPage page, bool audioHeroFallback)
    {
        List<IElementHandle> all = [];
        foreach (IFrame frame in page.Frames.ToArray()) all.AddRange(await FindInteractiveElementsInFrameAsync(frame, audioHeroFallback).ConfigureAwait(false));
        return all;
    }

    private static async Task<IReadOnlyList<IElementHandle>> FindInteractiveElementsInFrameAsync(IFrame frame, bool audioHeroFallback)
    {
        IJSHandle result = await frame.EvaluateHandleAsync("""() => { const found=[]; const seen=new Set(); for (const p of document.querySelectorAll('.play_button')) { if (p.closest('.player_controls_container') || (p.innerText || p.getAttribute('aria-label') || p.title || '').trim()) continue; let row=p.parentElement; while (row.parentElement && row.parentElement.querySelectorAll('.play_button').length === 1) row=row.parentElement; const name=((row && row.innerText) || '').split('\n').map(x => x.trim()).filter(x => x.length > 2 && x.length <= 80 && !/^[\d:.\s]+$/.test(x)).slice(0, 3).join(' | ') || 'track'; p.setAttribute('aria-label', 'Play preview of ' + name); } const selector="a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=link],[contenteditable=true],[onclick],[tabindex]:not([tabindex='-1']),.play_button"; const hasInteractiveAncestor=e => { let p=e.parentElement || e.getRootNode().host; while (p) { const label=(p.innerText || p.getAttribute('aria-label') || p.title || '').trim(); if (p.matches(selector) || getComputedStyle(p).cursor === 'pointer' && label.length > 0) return true; p=p.parentElement || p.getRootNode().host; } return false; }; const inNavigation=e => { let p=e; while (p) { if (p.matches('nav,header,[role=navigation]')) return true; p=p.parentElement || p.getRootNode().host; } return false; }; const visit=root => { for (const e of root.querySelectorAll('*')) { const explicit=e.matches(selector); const label=(e.innerText || e.getAttribute('aria-label') || e.title || '').trim(); const pointer=e !== document.body && e !== document.documentElement && getComputedStyle(e).cursor === 'pointer' && label.length > 0; const navigationItem=inNavigation(e) && !explicit && !pointer && e.children.length === 0 && label.length > 0 && label.length <= 80 && !hasInteractiveAncestor(e); if ((explicit || pointer && !hasInteractiveAncestor(e) || navigationItem) && !seen.has(e) && e.getClientRects().length > 0) { seen.add(e); found.push(e); } if (e.shadowRoot) visit(e.shadowRoot); } }; visit(document); const rank=e => e.matches("input:not([type]),input[type=text],input[type=search],textarea,[contenteditable=true],.play_button") ? 0 : 1; return found.map((e, i) => [rank(e), i, e]).sort((a, b) => a[0] - b[0] || a[1] - b[1]).slice(0, 250).map(x => x[2]); }""").ConfigureAwait(false);
        IReadOnlyDictionary<string, IJSHandle> properties = await result.GetPropertiesAsync().ConfigureAwait(false);
        List<IElementHandle> elements = properties.OrderBy(pair => int.TryParse(pair.Key, out int index) ? index : int.MaxValue).Select(pair => pair.Value.AsElement()).OfType<IElementHandle>().ToList();
        await result.DisposeAsync().ConfigureAwait(false);
        List<IElementHandle> visible = [];
        foreach (IElementHandle element in elements)
        {
            if (!await element.IsVisibleAsync().ConfigureAwait(false)) continue;
            if (audioHeroFallback && await IsAudioHeroFallbackAsync(element).ConfigureAwait(false) && !await IsVisibleAudioHeroFallbackAsync(element).ConfigureAwait(false)) continue;
            visible.Add(element);
        }
        elements = visible;
        if (elements.Count == 0)
        {
            foreach (AriaRole role in new[] { AriaRole.Searchbox, AriaRole.Textbox, AriaRole.Combobox, AriaRole.Button, AriaRole.Link, AriaRole.Checkbox, AriaRole.Radio, AriaRole.Switch, AriaRole.Tab, AriaRole.Menuitem, AriaRole.Option, AriaRole.Slider, AriaRole.Spinbutton, AriaRole.Treeitem })
            {
                ILocator matches = frame.GetByRole(role);
                int count = await matches.CountAsync().ConfigureAwait(false);
                for (int index = 0; index < count && elements.Count < 100; index++)
                {
                    IElementHandle? element = await matches.Nth(index).ElementHandleAsync().ConfigureAwait(false);
                    if (element is not null && await element.IsVisibleAsync().ConfigureAwait(false)) elements.Add(element);
                }
            }
        }
        if (audioHeroFallback)
        {
            IJSHandle nav = await frame.EvaluateHandleAsync("""() => { const labels=new Set(['Search','Free Sounds','Search Music','Search Sound Effects','See results','See Results','Login']); const selector='a[href],button,[role=button],[role=link],[onclick]'; const found=[]; const seen=new Set(); const includes=(node,target) => { for (let e=node; e; e=e.parentElement || e.getRootNode().host) if (e === target) return true; return false; }; const actionable=e => { for (let p=e; p; p=p.parentElement || p.getRootNode().host) if (p.matches(selector) || p.tabIndex >= 0 || getComputedStyle(p).cursor==='pointer') return true; return false; }; const visit=root => { for (const e of root.querySelectorAll('*')) { const text=(e.innerText || e.getAttribute('aria-label') || e.title || e.textContent || '').trim(); const style=getComputedStyle(e); const rect=e.getBoundingClientRect(); const inViewport=rect.right > 0 && rect.bottom > 0 && rect.left < innerWidth && rect.top < innerHeight; const visible=e.getClientRects().length > 0 && style.visibility !== 'hidden' && style.display !== 'none' && inViewport; const hit=visible ? document.elementFromPoint(Math.min(innerWidth-1,Math.max(0,rect.left+rect.width/2)),Math.min(innerHeight-1,Math.max(0,rect.top+rect.height/2))) : null; const receivesHit=hit && (includes(hit,e) || includes(e,hit)); if (visible && receivesHit && actionable(e) && labels.has(text) && !seen.has(text)) { seen.add(text); found.push(e); } if (e.shadowRoot) visit(e.shadowRoot); } }; visit(document); return found; }""").ConfigureAwait(false);
            IReadOnlyDictionary<string, IJSHandle> navProperties = await nav.GetPropertiesAsync().ConfigureAwait(false);
            elements.AddRange(navProperties.OrderBy(pair => int.TryParse(pair.Key, out int index) ? index : int.MaxValue).Select(pair => pair.Value.AsElement()).OfType<IElementHandle>());
            await nav.DisposeAsync().ConfigureAwait(false);
        }
        return elements;
    }

    internal static Task<bool> IsNavigationFallbackAsync(IElementHandle handle) => handle.EvaluateAsync<bool>("e => { const selector=\"a[href],button,input:not([type=hidden]),textarea,select,[role=button],[role=link],[contenteditable=true],[onclick],[tabindex]:not([tabindex='-1'])\"; if (e.matches(selector) || getComputedStyle(e).cursor === 'pointer') return false; let p=e; while (p) { if (p.matches('nav,header,[role=navigation]')) return true; p=p.parentElement || p.getRootNode().host; } return false; }");

    internal static bool IsAudioHeroFallbackLabel(string text) => AudioHeroFallbackLabels.Contains(text, StringComparer.OrdinalIgnoreCase);

    internal static Task<bool> IsAudioHeroFallbackAsync(IElementHandle handle) => handle.EvaluateAsync<bool>("e => ['Search','Free Sounds','Search Music','Search Sound Effects','See results','See Results','Login'].includes((e.innerText || e.getAttribute('aria-label') || e.title || e.textContent || '').trim())");

    internal static Task<bool> IsVisibleAudioHeroFallbackAsync(IElementHandle handle) => handle.EvaluateAsync<bool>("e => { const text=(e.innerText||e.getAttribute('aria-label')||e.title||e.textContent||'').trim(),r=e.getBoundingClientRect(),w=innerWidth,h=innerHeight,selector='a[href],button,[role=button],[role=link],[onclick]';if(!['Search','Free Sounds','Search Music','Search Sound Effects','See results','See Results','Login'].includes(text)||!e.isConnected||!e.getClientRects().length||getComputedStyle(e).visibility==='hidden'||getComputedStyle(e).display==='none'||r.right<=0||r.bottom<=0||r.left>=w||r.top>=h)return false;let actionable=false;for(let p=e;p;p=p.parentElement||p.getRootNode().host)if(p.matches(selector)||p.tabIndex>=0||getComputedStyle(p).cursor==='pointer'){actionable=true;break;}if(!actionable)return false;const x=Math.min(w-1,Math.max(0,r.left+r.width/2)),y=Math.min(h-1,Math.max(0,r.top+r.height/2)),hit=document.elementFromPoint(x,y),contains=(node,target)=>{for(let n=node;n;n=n.parentElement||n.getRootNode().host)if(n===target)return true;return false;};return !!hit&&(contains(hit,e)||contains(e,hit)); }");

    internal static async Task<IElementHandle?> FindAudioHeroFallbackAsync(IPage page, string tag, string text)
    {
        if (!IsAudioHeroFallbackLabel(text)) return null;
        IReadOnlyList<IElementHandle> handles = await FindInteractiveElementsAsync(page, true).ConfigureAwait(false);
        foreach (IElementHandle handle in handles)
        {
            if (!await handle.IsVisibleAsync().ConfigureAwait(false) || !await IsVisibleAudioHeroFallbackAsync(handle).ConfigureAwait(false)) continue;
            string currentTag = await handle.EvaluateAsync<string>("e => e.localName || ''").ConfigureAwait(false);
            string currentText = await handle.EvaluateAsync<string>("e => (e.innerText || e.getAttribute('aria-label') || e.title || e.textContent || '').trim()").ConfigureAwait(false);
            if (tag.Equals(currentTag, StringComparison.OrdinalIgnoreCase) && text.Equals(currentText, StringComparison.OrdinalIgnoreCase)) return handle;
        }

        return null;
    }

    internal static async Task<bool> ClickVisibleAudioHeroFallbackAsync(IPage page, IElementHandle handle)
    {
        double[] point = await handle.EvaluateAsync<double[]>("e => { const r=e.getBoundingClientRect(),w=innerWidth,h=innerHeight,s=getComputedStyle(e); if(!e.isConnected||!e.getClientRects().length||s.visibility==='hidden'||s.display==='none'||r.right<=0||r.bottom<=0||r.left>=w||r.top>=h)return [0,0,0]; return [Math.min(w-1,Math.max(0,r.left+r.width/2)),Math.min(h-1,Math.max(0,r.top+r.height/2)),1]; }").ConfigureAwait(false);
        if (point.Length < 3 || point[2] == 0) return false;
        await page.Mouse.ClickAsync((float)point[0], (float)point[1]).ConfigureAwait(false);
        return true;
    }
}

internal sealed partial class AiService
{
    private readonly SemaphoreSlim browserGate = new(1, 1);
    private readonly SemaphoreSlim browserActionGate = new(1, 1);
    private readonly SemaphoreSlim browserPermissionGate = new(1, 1);
    private IPlaywright? browserPlaywright;
    private IBrowserContext? browserContext;
    private AiTask? browserDiagnosticsTask;
    private readonly HashSet<IPage> browserDiagnosticPages = [];
    private string? lastAudioUrl;
    private bool browserHeaded;
    private ICDPSession? browserCacheSession;   // kept referenced: the cache stays off only while the session lives

    internal bool BrowserAccessForProject() => sessionStore.BrowserAllowedForProject();

    internal void RevokeBrowserAccessForProject() => sessionStore.SetBrowserAllowedForProject(false);

    public async Task<JsonObject> BrowserOpenAsync(string token, string url, string reason, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, reason, ct).ConfigureAwait(false);
        if (!BrowserSafety.IsWebUrl(url.Trim()) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
            throw new AmpleException("AMP5458", "Browser URLs must use http or https and cannot contain credentials.");
        if (!await IsPublicTargetAsync(uri, ct).ConfigureAwait(false)) return new JsonObject { ["allowed"] = false, ["note"] = "The automated browser is restricted to public internet addresses." };
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = await BrowserPageAsync(task, ct).ConfigureAwait(false);
            await page.GotoAsync(uri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 }).ConfigureAwait(false);
            string humanReason = await BrowserSafety.HumanInputReasonAsync(page).ConfigureAwait(false);
            bool humanInput = humanReason.Length > 0;
            if (humanInput && !browserHeaded) page = await SetBrowserVisibilityAsync(task, page.Url, true, ct).ConfigureAwait(false);
            else if (!humanInput && browserHeaded) page = await SetBrowserVisibilityAsync(task, page.Url, false, ct).ConfigureAwait(false);
            return new JsonObject { ["allowed"] = true, ["url"] = page.Url, ["title"] = await page.TitleAsync().ConfigureAwait(false), ["humanInputRequired"] = humanInput, ["humanInputReason"] = humanReason, ["note"] = humanInput ? "The page appears to need manual sign-in or human verification. AMPLE opened the persistent session in a visible window; please complete that step yourself, then ask Codex to continue. The window is a real Chromium window on the computer that runs AMPLE: a person who works remotely cannot see or click it. If the person says they do not see it, do not say again that it is visible and do not reopen it: say plainly that the page needs a human check that cannot be done from here, and go on with another source or ask for the file (score, MIDI, audio) instead." : "The page is open in AMPLE's headless browser. No sign-in or CAPTCHA was detected; if the page looks incomplete, ask to show the browser so you can intervene." };
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The automated browser could not open that page. Check that the Playwright Chromium browser is installed and try again." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserShowAsync(string token, string reason, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, reason, ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            if (!BrowserSafety.IsWebUrl(page.Url)) return new JsonObject { ["allowed"] = false, ["note"] = "Open a public web page before asking AMPLE to show the browser." };
            page = await SetBrowserVisibilityAsync(task, page.Url, true, ct).ConfigureAwait(false);
            return new JsonObject { ["shown"] = true, ["url"] = page.Url, ["title"] = await page.TitleAsync().ConfigureAwait(false), ["note"] = "The isolated AMPLE browser is now visible in the foreground so you can inspect or handle an unexpected page state. The window is a real Chromium window on the computer that runs AMPLE: a person who works remotely cannot see or click it. If the person says they do not see it, do not say again that it is visible and do not reopen it: say plainly that the page needs a human check that cannot be done from here, and go on with another source or ask for the file (score, MIDI, audio) instead." };
        }
        finally { browserActionGate.Release(); }
    }

    /// <summary>The window of the assistant's browser as a picture, for a person who is not at this computer: only while the window is visible for a human step (sign-in, a check that asks for a human). Nothing is shown when there is no such step.</summary>
    public async Task<JsonObject> BrowserViewAsync(CancellationToken ct)
    {
        IPage? page = browserHeaded ? browserContext?.Pages.FirstOrDefault(p => !p.IsClosed) : null;
        if (page is null || !BrowserSafety.IsWebUrl(page.Url)) return new JsonObject { ["active"] = false };
        if (!await browserActionGate.WaitAsync(3000, ct).ConfigureAwait(false)) return new JsonObject { ["active"] = true, ["busy"] = true };
        try
        {
            byte[] picture = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 60, Timeout = 8000 }).ConfigureAwait(false);
            int[] size = await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]").ConfigureAwait(false);
            return new JsonObject { ["active"] = true, ["url"] = page.Url, ["title"] = await page.TitleAsync().ConfigureAwait(false), ["width"] = size[0], ["height"] = size[1], ["image"] = Convert.ToBase64String(picture) };
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return new JsonObject { ["active"] = false }; }
        finally { browserActionGate.Release(); }
    }

    /// <summary>A click, a scroll, some typing or a key of the person who looks at the window of the assistant's browser (see BrowserViewAsync); only while the window is visible for a human step. Purchases stay blocked by the assistant's own tools, this is the person's own hand.</summary>
    public async Task<JsonObject> BrowserInputAsync(JsonElement body, CancellationToken ct)
    {
        IPage? page = browserHeaded ? browserContext?.Pages.FirstOrDefault(p => !p.IsClosed) : null;
        if (page is null || !BrowserSafety.IsWebUrl(page.Url)) return new JsonObject { ["ok"] = false, ["note"] = "There is no browser window waiting for a person." };
        string kind = body.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        float X() => body.TryGetProperty("x", out JsonElement v) ? (float)v.GetDouble() : 0;
        float Y() => body.TryGetProperty("y", out JsonElement v) ? (float)v.GetDouble() : 0;
        string Text() => body.TryGetProperty("text", out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty;
        if (!await browserActionGate.WaitAsync(3000, ct).ConfigureAwait(false)) return new JsonObject { ["ok"] = false, ["note"] = "The browser is busy." };
        try
        {
            switch (kind)
            {
                case "click": await page.Mouse.ClickAsync(X(), Y()).ConfigureAwait(false); break;
                case "move": await page.Mouse.MoveAsync(X(), Y()).ConfigureAwait(false); break;
                case "scroll": await page.Mouse.MoveAsync(X(), Y()).ConfigureAwait(false); await page.Mouse.WheelAsync(0, body.TryGetProperty("dy", out JsonElement dy) ? (float)dy.GetDouble() : 0).ConfigureAwait(false); break;
                case "type": if (Text().Length is > 0 and <= 500) await page.Keyboard.TypeAsync(Text()).ConfigureAwait(false); break;
                case "key": if (Text().Length is > 0 and <= 40) await page.Keyboard.PressAsync(Text()).ConfigureAwait(false); break;
                default: return new JsonObject { ["ok"] = false, ["note"] = "Unknown input." };
            }

            return new JsonObject { ["ok"] = true };
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return new JsonObject { ["ok"] = false, ["note"] = "The page did not take that input." }; }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserAudioHeroPreviewAsync(string token, int index, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Trigger one AudioHero preview through its visible page control.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            if (!BrowserSafety.IsAudioHeroPage(page.Url)) return new JsonObject { ["allowed"] = false, ["note"] = "Open an AudioHero page and read it before requesting a preview." };
            BrowserElement element = BrowserElementAt(task, index);
            if (!BrowserSafety.IsAudioHeroPreviewControl(element.Text, element.AriaLabel)) return new JsonObject { ["allowed"] = false, ["note"] = "That numbered element is not a Listen/Preview control, or it matches the purchase blocklist. Read the AudioHero results and choose a visible preview control." };
            await page.BringToFrontAsync().ConfigureAwait(false);
            await page.EvaluateAsync("() => document.querySelectorAll('audio').forEach(a => a.pause())").ConfigureAwait(false);   // a track still playing from the last click would answer before this one
            await Task.Delay(500, ct).ConfigureAwait(false);
            task.BrowserCapturedAudioUrl = null;
            task.BrowserCaptureAudioPreview = true;
            try
            {
                await element.Handle.EvaluateAsync("element => { element.click(); return true; }").ConfigureAwait(false);
                for (int attempt = 0; attempt < 20 && task.BrowserCapturedAudioUrl is null; attempt++) await Task.Delay(250, ct).ConfigureAwait(false);
            }
            finally { task.BrowserCaptureAudioPreview = false; }
            // the numbered list stays valid: the play buttons are still on the page, so the next preview needs no new read (a read took 14 s)
            string? url = task.BrowserCapturedAudioUrl;
            task.BrowserCapturedAudioUrl = null;
            await page.EvaluateAsync("() => document.querySelectorAll('audio').forEach(a => a.pause())").ConfigureAwait(false);   // stop the page from playing on through the list
            if (url is not null) task.BrowserChosenPreviews.Enqueue(new KeyValuePair<string, string>(url, element.Text.StartsWith("Play preview of ", StringComparison.Ordinal) ? element.Text["Play preview of ".Length..] : element.Text));
            return url is null
                ? new JsonObject { ["allowed"] = false, ["note"] = "AudioHero's page did not expose an audio response for that preview. No download_auth request was made by AMPLE." }
                : new JsonObject { ["url"] = url, ["audioPreview"] = true, ["note"] = "AudioHero's own page JavaScript handled the preview request. Use ample_fetch_audio_previews to retrieve this URL as a separate, approved Output choice." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserReadAsync(string token, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Read the current page and its visible controls.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        IPage page = await BrowserPageAsync(task, ct).ConfigureAwait(false);
        string humanReason = await BrowserSafety.HumanInputReasonAsync(page).ConfigureAwait(false);
        if (humanReason.Length > 0)
        {
            if (!browserHeaded) page = await SetBrowserVisibilityAsync(task, page.Url, true, ct).ConfigureAwait(false);
            return new JsonObject { ["humanInputRequired"] = true, ["humanInputReason"] = humanReason, ["url"] = page.Url, ["title"] = await page.TitleAsync().ConfigureAwait(false), ["note"] = "This page needs manual sign-in or human verification. It is open visibly; please complete the step yourself, then ask Codex to continue. The window is a real Chromium window on the computer that runs AMPLE: a person who works remotely cannot see or click it. If the person says they do not see it, do not say again that it is visible and do not reopen it: say plainly that the page needs a human check that cannot be done from here, and go on with another source or ask for the file (score, MIDI, audio) instead." };
        }
        if (browserHeaded) page = await SetBrowserVisibilityAsync(task, page.Url, false, ct).ConfigureAwait(false);
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 6000 }).ConfigureAwait(false);
            if (BrowserSafety.IsAudioHeroPage(page.Url)) await page.WaitForSelectorAsync(page.Url.Contains("s=", StringComparison.Ordinal) ? ".track_art_wave_row_inner .play_button" : "#search_box", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30000 }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }   // a page that never goes idle (streams, players) is read as it is
        IReadOnlyList<IElementHandle> handles = await BrowserSafety.FindInteractiveElementsAsync(page, BrowserSafety.IsAudioHeroPage(page.Url)).ConfigureAwait(false);
        task.BrowserElements.Clear();
        JsonArray elements = [];
        int skippedInvisible = 0, skippedCredential = 0, skippedNoLabel = 0, skippedDuplicate = 0;
        foreach (IElementHandle handle in handles)
        {
            if (task.BrowserElements.Count >= 100) break;
            if (!await handle.IsVisibleAsync().ConfigureAwait(false)) { skippedInvisible++; continue; }
            string tag = await handle.EvaluateAsync<string>("e => e.localName || ''").ConfigureAwait(false);
            string infoJson = await handle.EvaluateAsync<string>("e => { const s=v => typeof v === 'string' ? v : (v && typeof v.baseVal === 'string' ? v.baseVal : ''); const type=s(e.type); const actionValue=['button','submit','reset'].includes(type) ? s(e.value) : ''; return JSON.stringify({text:(s(e.innerText) || s(e.getAttribute('placeholder')) || actionValue || s(e.getAttribute('aria-label')) || s(e.title)).trim(),aria:s(e.getAttribute('aria-label')),href:s(e.href) || s(e.closest('a[href]')?.href),type,name:s(e.name) || s(e.id),autocomplete:s(e.autocomplete),label:s(e.labels?.[0]?.innerText),editable:String(e.isContentEditable)}); }").ConfigureAwait(false);   // one JSON text: a property that is not a string (an SVG href) must not empty the whole answer
            Dictionary<string, string> info = JsonSerializer.Deserialize<Dictionary<string, string>>(infoJson) ?? [];
            string Field(string key) => info.TryGetValue(key, out string? value) ? value : string.Empty;
            string elementText = Field("text"), aria = Field("aria"), type = Field("type"), href = Field("href");
            bool audioHeroFallback = BrowserSafety.IsAudioHeroPage(page.Url) && await BrowserSafety.IsAudioHeroFallbackAsync(handle).ConfigureAwait(false);
            if (elementText.Length == 0 && audioHeroFallback) elementText = await handle.EvaluateAsync<string>("e => (e.innerText || e.textContent || '').trim()").ConfigureAwait(false);
            bool canType = (tag == "input" && type is "text" or "search" or "url") || tag == "textarea" || Field("editable") == "true";
            bool credential = BrowserSafety.IsCredentialField(type, Field("name"), Field("autocomplete"), Field("label") + " " + aria + " " + elementText);
            if (BrowserSafety.ShouldHideCredentialControl(canType, credential)) { skippedCredential++; continue; }
            string text = elementText.Length > 300 ? elementText[..300] : elementText;
            if (text.Length == 0 && aria.Length == 0) { skippedNoLabel++; continue; }
            if (audioHeroFallback && task.BrowserElements.Any(element => element.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) { skippedDuplicate++; continue; }
            int index = task.BrowserElements.Count + 1;
            task.BrowserElements.Add(new BrowserElement(handle, tag, text, aria, href, canType && !credential));
            string kind = canType ? "text field" : audioHeroFallback ? "AudioHero navigation item" : await BrowserSafety.IsNavigationFallbackAsync(handle).ConfigureAwait(false) ? "navigation item" : tag;
            elements.Add(new JsonObject { ["index"] = index, ["kind"] = kind, ["tag"] = tag, ["text"] = text, ["ariaLabel"] = aria, ["href"] = href });
        }
        string body = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 10000 }).ConfigureAwait(false);
        return new JsonObject { ["url"] = page.Url, ["title"] = await page.TitleAsync().ConfigureAwait(false), ["elements"] = elements, ["elementsDiagnostic"] = $"{handles.Count} candidates, {elements.Count} listed (skipped: {skippedInvisible} not visible, {skippedCredential} credential field, {skippedNoLabel} without text, {skippedDuplicate} duplicate)", ["text"] = body.Length > 30000 ? body[..30000] + "\n… (truncated)" : body };   // the numbered controls come first: a long page text after them must never push them out of what the AI reads
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserClickAsync(string token, int index, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Click a numbered visible page element.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        BrowserElement element = BrowserElementAt(task, index);
        if (BrowserSafety.IsPurchaseAction($"{element.Text} {element.AriaLabel}"))
            return new JsonObject { ["allowed"] = false, ["note"] = "This purchase-related action is intentionally blocked. Please do it yourself in your own browser." };
        IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
        await page.BringToFrontAsync().ConfigureAwait(false);
        bool audioHeroFallback = BrowserSafety.IsAudioHeroPage(page.Url) && BrowserSafety.IsAudioHeroFallbackLabel(element.Text);
        IElementHandle clickHandle = element.Handle;
        if (audioHeroFallback)
        {
            IElementHandle? fresh = await BrowserSafety.FindAudioHeroFallbackAsync(page, element.Tag, element.Text).ConfigureAwait(false);
            if (fresh is null) return new JsonObject { ["allowed"] = false, ["note"] = "That numbered AudioHero control is no longer visible. Read the page again." };
            clickHandle = fresh;
        }
        if (element.Href.Length > 0 && (!BrowserSafety.IsWebUrl(element.Href) || !Uri.TryCreate(element.Href, UriKind.Absolute, out Uri? target) || !await IsPublicTargetAsync(target, ct).ConfigureAwait(false)))
            return new JsonObject { ["allowed"] = false, ["note"] = "This link is blocked because it does not point to a public http or https page." };
        if (element.Href.Length > 0 && IsAudioUrl(element.Href))
            return new JsonObject { ["url"] = element.Href, ["audioPreview"] = true, ["note"] = "Audio preview URL only; use ample_fetch_url to request its separate download approval." };
        lastAudioUrl = null;
        bool clicked;
        try
        {
            clicked = audioHeroFallback
                ? await BrowserSafety.ClickVisibleAudioHeroFallbackAsync(page, clickHandle).ConfigureAwait(false)
                : await ClickElementAsync(clickHandle).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            task.BrowserElements.Clear();
            return new JsonObject { ["allowed"] = false, ["note"] = "That numbered control did not become clickable in the visible viewport. Adjust the page in the browser, then run ample_browser_read again." };
        }
        if (!clicked) return new JsonObject { ["allowed"] = false, ["note"] = "That numbered control is not fully visible in the browser viewport. Adjust the visible page, then run ample_browser_read again." };
        await Task.Delay(750, ct).ConfigureAwait(false);
        if (browserContext is not null && browserContext.Pages.Count > 1) { task.BrowserPage = browserContext.Pages[^1]; AttachBrowserDiagnostics(task, task.BrowserPage); }
        task.BrowserElements.Clear();
        string? audioUrl = lastAudioUrl;
        lastAudioUrl = null;
        if (audioUrl is not null) return new JsonObject { ["url"] = audioUrl, ["audioPreview"] = true, ["note"] = "Audio preview URL only; use ample_fetch_url to request its separate download approval." };
        return new JsonObject { ["url"] = task.BrowserPage?.Url, ["title"] = task.BrowserPage is null ? null : await task.BrowserPage.TitleAsync().ConfigureAwait(false), ["note"] = "Click registered. Run ample_browser_read to refresh the numbered element list." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserTypeAsync(string token, int index, string text, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Enter search text in a numbered visible text field.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        BrowserElement element = BrowserElementAt(task, index);
        if (!element.CanType || BrowserSafety.IsCredentialField(await element.Handle.GetAttributeAsync("type").ConfigureAwait(false) ?? string.Empty, await element.Handle.GetAttributeAsync("name").ConfigureAwait(false) ?? string.Empty, await element.Handle.GetAttributeAsync("autocomplete").ConfigureAwait(false) ?? string.Empty, element.Text + " " + element.AriaLabel))
            return new JsonObject { ["allowed"] = false, ["note"] = "This is not an eligible search/text field. Credential fields are never filled by Codex." };
        IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
        await page.BringToFrontAsync().ConfigureAwait(false);
        await element.Handle.FillAsync(text).ConfigureAwait(false);
        task.BrowserElements.Clear();
        return new JsonObject { ["allowed"] = true, ["note"] = "Text entered. Submit it yourself or use a numbered visible search button." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserMouseMoveAsync(string token, int x, int y, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Move the pointer to hover a visible page control.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            int[] viewport = await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]").ConfigureAwait(false);
            if (x < 0 || y < 0 || viewport.Length < 2 || x >= viewport[0] || y >= viewport[1]) throw new AmpleException("AMP5458", "Mouse coordinates must be inside the visible page viewport.");
            await page.BringToFrontAsync().ConfigureAwait(false);
            await page.Mouse.MoveAsync(x, y).ConfigureAwait(false);
            return new JsonObject { ["moved"] = true, ["x"] = x, ["y"] = y };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserMediaMuteAsync(string token, bool muted, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Mute or unmute HTML media on the current page.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            await page.BringToFrontAsync().ConfigureAwait(false);
            int count = await page.EvaluateAsync<int>("muted => { window.__ampleMediaMuteObserver?.disconnect(); delete window.__ampleMediaMuteObserver; const apply=root => { if(root.matches?.('audio,video')) root.muted=muted; for(const item of root.querySelectorAll?.('audio,video') || []) item.muted=muted; }; apply(document); if(muted) window.__ampleMediaMuteObserver=new MutationObserver(records => records.forEach(record => record.addedNodes.forEach(apply))); if(muted) window.__ampleMediaMuteObserver.observe(document.documentElement,{childList:true,subtree:true}); return document.querySelectorAll('audio,video').length; }", muted).ConfigureAwait(false);
            return new JsonObject { ["muted"] = muted, ["mediaElements"] = count, ["note"] = "This changes HTML audio/video elements only; it does not control the browser tab's system audio." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserScreenshotAsync(string token, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Capture the visible page for visual inspection.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            await page.BringToFrontAsync().ConfigureAwait(false);
            byte[] screenshot = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, Animations = ScreenshotAnimations.Disabled }).ConfigureAwait(false);
            return new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(screenshot), ["note"] = "Screenshot captured on request; it was not saved as a download." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserNetworkStatsAsync(string token, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Read aggregate page console and network diagnostics.", ct).ConfigureAwait(false);
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            Dictionary<string, int> resources = await page.EvaluateAsync<Dictionary<string, int>>("() => performance.getEntriesByType('resource').reduce((counts,item) => { const kind=item.initiatorType || 'other'; counts[kind]=(counts[kind]||0)+1; return counts; }, {})").ConfigureAwait(false);
            return new JsonObject { ["consoleErrorsSinceAttached"] = Volatile.Read(ref task.BrowserConsoleErrorCount), ["consoleMessages"] = new JsonArray(task.BrowserConsoleMessages.TakeLast(50).Select(message => (JsonNode?)JsonValue.Create(message)).ToArray()), ["requestsSinceAttached"] = Volatile.Read(ref task.BrowserRequestCount), ["responsesSinceAttached"] = Volatile.Read(ref task.BrowserResponseCount), ["statusCounts"] = System.Text.Json.JsonSerializer.SerializeToNode(task.BrowserStatusCounts), ["resourceTypes"] = System.Text.Json.JsonSerializer.SerializeToNode(resources), ["audioPreviewUrls"] = new JsonArray(task.BrowserAudioPreviewUrls.Distinct(StringComparer.Ordinal).Select(url => (JsonNode?)JsonValue.Create(url)).ToArray()), ["note"] = "Console messages are limited to 50 recent entries with common credential-like values redacted and URLs reduced to their origin. Only URLs for responses identified as audio are exposed, so an audio preview can be passed to ample_fetch_url for its separate approval. Other network URLs, headers and response bodies are omitted." };
        }
        finally { browserActionGate.Release(); }
    }

    public async Task<JsonObject> BrowserRunJavaScriptAsync(string token, string script, CancellationToken ct)
    {
        AiTask task = await RequireBrowserAccessAsync(token, "Execute owner-authorized JavaScript in the current page.", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(script) || script.Length > 20000) throw new AmpleException("AMP5458", "Provide a JavaScript expression of at most 20000 characters.");
        await browserActionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IPage page = task.BrowserPage ?? await BrowserPageAsync(task, ct).ConfigureAwait(false);
            await page.BringToFrontAsync().ConfigureAwait(false);
            JsonElement result = await page.EvaluateAsync<JsonElement>(script).ConfigureAwait(false);
            return new JsonObject { ["result"] = JsonSerializer.SerializeToNode(result), ["warning"] = "Page JavaScript has unrestricted page-level effects and can bypass numbered-click purchase blocking. Do not inspect or return credentials, tokens or cookies." };
        }
        finally { browserActionGate.Release(); }
    }

    private async Task<AiTask> RequireBrowserAccessAsync(string token, string reason, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? found) ? found : throw new AmpleException("AMP5458", "This task is no longer running.");
        await browserPermissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (BrowserSafety.HasAccess(task, s.Clock.UtcNow, sessionStore.BrowserAllowedForProject())) return task;
            AiApproval ask = new() { Kind = "browser", Summary = "Automated browser access", Detail = $"Codex requests access to an automated browser (a browser AMPLE controls for this task). This includes page JavaScript that can read or change page content, submit forms and bypass the numbered-click purchase block. Never use scripts to inspect or return credentials, tokens or cookies. Credentials remain yours to enter manually. Reason: {reason}" };
            task.Approvals[ask.Id] = ask;
            task.Status = "waiting-approval";
            task.Add("approval", "Codex asks to use an automated browser session for this task.");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
            string decision;
            try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
            ask.Answered = decision;
            task.Status = "running";
            if (decision is "accept30" or "acceptForSession" or "acceptForProject")
            {
                BrowserSafety.Grant(task, decision, s.Clock.UtcNow);
                if (decision == "acceptForProject") sessionStore.SetBrowserAllowedForProject(true);
                sessionStore.SaveTask(task);
            }
            if (!BrowserSafety.HasAccess(task, s.Clock.UtcNow, sessionStore.BrowserAllowedForProject())) throw new AmpleException("AMP5458", "The owner did not allow browser access for this task.");
            return task;
        }
        finally { browserPermissionGate.Release(); }
    }

    private async Task<IPage> BrowserPageAsync(AiTask task, CancellationToken ct)
    {
        if (task.BrowserPage is { IsClosed: false } existing) { AttachBrowserDiagnostics(task, existing); return existing; }
        await browserGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await BrowserPageLockedAsync(task, ct).ConfigureAwait(false);
        }
        finally { browserGate.Release(); }
    }

    private async Task<IPage> SetBrowserVisibilityAsync(AiTask task, string url, bool visible, CancellationToken ct)
    {
        await browserGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (browserHeaded == visible && task.BrowserPage is { IsClosed: false } current) return current;
            if (browserContext is not null) await browserContext.CloseAsync().ConfigureAwait(false);
            browserContext = null;
            browserHeaded = visible;
            task.BrowserPage = null;
            IPage page = await BrowserPageLockedAsync(task, ct).ConfigureAwait(false);
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? target) && BrowserSafety.IsWebUrl(url))
                await page.GotoAsync(target.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 }).ConfigureAwait(false);
            if (visible) await page.BringToFrontAsync().ConfigureAwait(false);
            return page;
        }
        finally { browserGate.Release(); }
    }

    private async Task<IPage> BrowserPageLockedAsync(AiTask task, CancellationToken ct)
    {
        try
        {
            if (browserContext is null)
            {
                browserPlaywright = await Playwright.CreateAsync().ConfigureAwait(false);
                string profile = Path.Combine(s.Library.Home, "browser-profile");
                browserContext = await browserPlaywright.Chromium.LaunchPersistentContextAsync(profile, new BrowserTypeLaunchPersistentContextOptions { Headless = !browserHeaded, AcceptDownloads = false, ViewportSize = null, Args = browserHeaded ? ["--start-maximized"] : [] }).ConfigureAwait(false);
                await browserContext.RouteAsync("**/*", async route =>
                {
                    if (Uri.TryCreate(route.Request.Url, UriKind.Absolute, out Uri? requestUri) && await IsPublicTargetAsync(requestUri, CancellationToken.None).ConfigureAwait(false)) await route.ContinueAsync().ConfigureAwait(false);
                    else await route.AbortAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
                void Watch(IPage page)
                {
                    page.Response += (_, response) =>
                    {
                        if (response.Headers.TryGetValue("content-type", out string? type) && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) lastAudioUrl = response.Url;
                    };
                    page.Download += (_, download) => lastAudioUrl = download.Url;
                }
                foreach (IPage page in browserContext.Pages) Watch(page);
                browserContext.Page += (_, page) => Watch(page);
            }
            task.BrowserContext = browserContext;
            task.BrowserPage = browserContext.Pages.FirstOrDefault(p => !p.IsClosed) ?? await browserContext.NewPageAsync().ConfigureAwait(false);
            AttachBrowserDiagnostics(task, task.BrowserPage);
            return task.BrowserPage;
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new AmpleException("AMP5458", "The Playwright browser could not start. Check that Playwright Chromium is installed. Profile details are intentionally hidden.");
        }
    }

    private void AttachBrowserDiagnostics(AiTask task, IPage page)
    {
        Volatile.Write(ref browserDiagnosticsTask, task);
        if (!browserDiagnosticPages.Add(page)) return;
        _ = DisableBrowserCacheAsync(page);
        page.Console += (_, message) =>
        {
            AiTask? active = Volatile.Read(ref browserDiagnosticsTask);
            if (active is null) return;
            if (message.Type == "error") Interlocked.Increment(ref active.BrowserConsoleErrorCount);
            active.BrowserConsoleMessages.Enqueue(message.Type + ": " + BrowserSafety.SanitizeConsoleMessage(message.Text));
            while (active.BrowserConsoleMessages.Count > 100) active.BrowserConsoleMessages.TryDequeue(out string? _);
        };
        page.Request += (_, _) => { AiTask? active = Volatile.Read(ref browserDiagnosticsTask); if (active is not null) Interlocked.Increment(ref active.BrowserRequestCount); };
        page.Response += (_, response) =>
        {
            AiTask? active = Volatile.Read(ref browserDiagnosticsTask);
            if (active is null) return;
            Interlocked.Increment(ref active.BrowserResponseCount);
            active.BrowserStatusCounts.AddOrUpdate(response.Status, 1, static (_, count) => count + 1);
            string contentType = response.Headers.TryGetValue("content-type", out string? type) ? type : string.Empty;
            if (BrowserSafety.IsAudioHeroPage(page.Url) && BrowserSafety.IsAudioResponse(contentType, response.Url))
            {
                active.BrowserAudioPreviewUrls.Enqueue(response.Url);
                while (active.BrowserAudioPreviewUrls.Count > 100) active.BrowserAudioPreviewUrls.TryDequeue(out string? _);
                if (active.BrowserCaptureAudioPreview && active.BrowserCapturedAudioUrl is null) active.BrowserCapturedAudioUrl = response.Url;   // the first one is the clicked track; the page then goes on to the next tracks
            }
        };
    }

    /// <summary>A preview that came from the browser cache makes no network request, so AMPLE could not see which file a click played. AudioHero also preloads the next tracks, which is how the cache fills. With the cache off every click is one visible request.</summary>
    private async Task DisableBrowserCacheAsync(IPage page)
    {
        try
        {
            ICDPSession session = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            await session.SendAsync("Network.enable").ConfigureAwait(false);
            await session.SendAsync("Network.setCacheDisabled", new Dictionary<string, object> { ["cacheDisabled"] = true }).ConfigureAwait(false);
            browserCacheSession = session;
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException) { }   // not Chromium, or the page closed: previews then work as before
    }

    private static BrowserElement BrowserElementAt(AiTask task, int index) =>
        index > 0 && index <= task.BrowserElements.Count ? task.BrowserElements[index - 1] : throw new AmpleException("AMP5458", "That element number is not in the latest ample_browser_read result. Read the page again.");

    private static bool IsAudioUrl(string url) => BrowserSafety.IsAudioResponse(string.Empty, url);

    private static async Task<bool> ClickElementAsync(IElementHandle handle)
    {
        await handle.ClickAsync().ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> IsPublicTargetAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? address) ? [address] : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct).ConfigureAwait(false); }
        catch (SocketException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bool documentation = bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113;
            bool benchmarking = bytes[0] == 198 && bytes[1] is 18 or 19;
            return bytes[0] is not (0 or 10 or 127) && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) && !documentation && !benchmarking && bytes[0] < 224;
        }
        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}
