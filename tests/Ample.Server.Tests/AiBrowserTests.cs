using System.Net;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.BlackBox;
using Microsoft.Playwright;

namespace Ample.Server.Tests;

public sealed class AiBrowserTests
{
    [Theory]
    [InlineData("Listen", "", true)]
    [InlineData("Preview track", "", true)]
    [InlineData("Play", "Preview", true)]
    [InlineData("Buy preview", "", false)]
    [InlineData("Confirm", "Listen", false)]
    [InlineData("Search", "", false)]
    public void AudioHero_preview_injection_accepts_only_non_purchase_preview_controls(string text, string ariaLabel, bool expected) =>
        Assert.Equal(expected, BrowserSafety.IsAudioHeroPreviewControl(text, ariaLabel));

    [Fact]
    public async Task Browser_reader_falls_back_to_accessible_controls_missing_from_dom_heuristics()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<button style='display:none'>Responsive copy</button><div role='switch' aria-checked='false'>Enable filter</div>");

        IReadOnlyList<IElementHandle> elements = await BrowserSafety.FindInteractiveElementsAsync(page, false);

        Assert.Equal("Enable filter", await Assert.Single(elements).InnerTextAsync());
    }

    [Fact]
    public async Task Browser_reader_finds_visible_controls_inside_open_shadow_roots()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<nav><div>Search library</div></nav><audio-hero-nav></audio-hero-nav><script>document.querySelector('audio-hero-nav').attachShadow({mode:'open'}).innerHTML='<a href=\"/library\">Search</a><input type=\"search\" placeholder=\"Search sounds\"><button>See results</button><div style=\"cursor:pointer\">Browse library</div>';</script>");

        IReadOnlyList<IElementHandle> elements = await BrowserSafety.FindInteractiveElementsAsync(page, false);

        // the text fields come first (the reader ranks them above the rest so the listing cap never hides a search box); the others keep their page order
        Assert.Equal(5, elements.Count);
        Assert.Equal("search", await elements[0].GetAttributeAsync("type"));
        IElementHandle library = elements[1];
        Assert.Equal("Search library", await library.InnerTextAsync());
        Assert.True(await BrowserSafety.IsNavigationFallbackAsync(library));
        Assert.Equal("Search", await elements[2].InnerTextAsync());
        Assert.Equal("See results", await elements[3].InnerTextAsync());
        Assert.Equal("Browse library", await elements[4].InnerTextAsync());
    }

    [Fact]
    public async Task Browser_reader_finds_visible_controls_inside_iframes()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<iframe srcdoc=\"<button>Search sounds</button><input type='search' aria-label='Search'>\"></iframe>");
        IFrame frame = page.Frames.Single(frame => !ReferenceEquals(frame, page.MainFrame));
        await frame.Locator("button").WaitForAsync();

        IReadOnlyList<IElementHandle> elements = await BrowserSafety.FindInteractiveElementsAsync(page, false);

        Assert.Equal(2, elements.Count);
        Assert.Equal("search", await elements[0].GetAttributeAsync("type"));   // the text field is ranked first
        Assert.Equal("Search sounds", await elements[1].InnerTextAsync());
    }

    [Fact]
    public async Task Browser_reader_does_not_turn_static_AudioHero_footer_labels_into_click_targets()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<header><a href='/search'>Search</a><a href='/login'>Login</a></header><footer><span>Search Sound Effects</span><span>Search Music</span><span>Free Sounds</span></footer>");

        IReadOnlyList<IElementHandle> elements = await BrowserSafety.FindInteractiveElementsAsync(page, true);

        string[] labels = await Task.WhenAll(elements.Select(element => element.EvaluateAsync<string>("e => (e.innerText || e.textContent || '').trim()")));
        Assert.Contains("Search", labels);
        Assert.Contains("Login", labels);
        Assert.DoesNotContain("Search Sound Effects", labels);
        Assert.DoesNotContain("Search Music", labels);
        Assert.DoesNotContain("Free Sounds", labels);
        Assert.True(BrowserSafety.IsAudioHeroPage("https://www.audiohero.com/"));
        Assert.False(BrowserSafety.IsAudioHeroPage("https://audiohero.com.example.test/"));
    }

    [Fact]
    public async Task AudioHero_fallback_clicks_the_visible_label_at_the_reader_viewport_hit_point()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 400, Height = 300 } });
        await page.SetContentAsync("<label style='display:inline-block;width:900px' onclick='window.clicked = true'>Search</label>");
        IElementHandle handle = await page.GetByText("Search", new PageGetByTextOptions { Exact = true }).ElementHandleAsync() ?? throw new InvalidOperationException("Search label missing");

        Assert.True(await BrowserSafety.ClickVisibleAudioHeroFallbackAsync(page, handle));
        Assert.True(await page.EvaluateAsync<bool>("() => window.clicked === true"));
    }

    [Fact]
    public async Task AudioHero_fallback_reacquires_a_replaced_numbered_label_before_clicking()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<nav><label onclick='window.clicked=true'>Search</label></nav>");
        IElementHandle stale = await page.GetByText("Search", new PageGetByTextOptions { Exact = true }).ElementHandleAsync() ?? throw new InvalidOperationException("Search label missing");
        await page.EvaluateAsync("() => { const replacement=document.createElement('label'); replacement.textContent='Search'; replacement.onclick=()=>window.clicked=true; replacement.style.cursor='pointer'; document.querySelector('label').replaceWith(replacement); }");

        IElementHandle fresh = await BrowserSafety.FindAudioHeroFallbackAsync(page, "label", "Search") ?? throw new InvalidOperationException("Replaced Search label missing");

        Assert.False(ReferenceEquals(stale, fresh));
        Assert.True(await BrowserSafety.ClickVisibleAudioHeroFallbackAsync(page, fresh));
        Assert.True(await page.EvaluateAsync<bool>("() => window.clicked === true"));
    }

    [Fact]
    public async Task Browser_reader_cannot_find_controls_hidden_inside_a_closed_shadow_root()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<audio-hero-nav></audio-hero-nav><script>document.querySelector('audio-hero-nav').attachShadow({mode:'closed'}).innerHTML='<button>Search</button>';</script>");

        IReadOnlyList<IElementHandle> elements = await BrowserSafety.FindInteractiveElementsAsync(page, true);
        int textLocatorCount = await page.GetByText("Search", new PageGetByTextOptions { Exact = true }).CountAsync();

        Assert.DoesNotContain("Search", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Empty(elements);
        Assert.Equal(0, textLocatorCount);
    }

    [Theory]
    [InlineData("Buy now", true)]
    [InlineData("PURCHASE", true)]
    [InlineData("Go to checkout", true)]
    [InlineData("Confirm payment", true)]
    [InlineData("Complete payment", true)]
    [InlineData("Proceed to payment", true)]
    [InlineData("Confirm", true)]
    [InlineData("Place order", true)]
    [InlineData("Add to cart", true)]
    [InlineData("Bestelling bevestigen", true)]
    [InlineData("Bestellen", true)]
    [InlineData("Bevestig", true)]
    [InlineData("Bevestig betaling", true)]
    [InlineData("Afrekenen", true)]
    [InlineData("Toevoegen aan winkelwagen", true)]
    [InlineData("Listen to preview", false)]
    [InlineData("Search AudioHero", false)]
    [InlineData("PayPal help", false)]
    public void Purchase_blocklist_matches_actions_without_matching_unrelated_labels(string label, bool expected) =>
        Assert.Equal(expected, BrowserSafety.IsPurchaseAction(label));

    [Fact]
    public void Browser_approval_grants_are_task_scoped_and_expire_after_thirty_minutes()
    {
        DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        AiTask timed = new();
        BrowserSafety.Grant(timed, "accept30", now);
        Assert.True(BrowserSafety.HasAccess(timed, now.AddMinutes(29)));
        Assert.False(BrowserSafety.HasAccess(timed, now.AddMinutes(30)));

        AiTask taskGrant = new();
        BrowserSafety.Grant(taskGrant, "acceptForSession", now);
        Assert.True(BrowserSafety.HasAccess(taskGrant, now.AddDays(1)));
        Assert.False(BrowserSafety.HasAccess(new AiTask(), now));

        BrowserSafety.Grant(taskGrant, "decline", now);
        Assert.False(BrowserSafety.HasAccess(taskGrant, now));
    }

    [Fact]
    public void Browser_grants_survive_task_snapshot_restore_without_sharing_with_new_tasks()
    {
        string root = Directory.CreateTempSubdirectory("ample-browser-test-").FullName;
        try
        {
            AssistantSessionStore store = new(root);
            AssistantSession session = store.Create("browser test", archiveConversation: false);
            DateTimeOffset until = new(2026, 10, 17, 12, 30, 0, TimeSpan.Zero);
            AiTask task = new() { SessionId = session.Id, Request = new AiTaskRequest("custom", "project", "prompt", "song.amplet", "song.amplet", null, null, null, null), BrowserAllowedForTask = true, BrowserAccessUntil = until };
            store.SaveTask(task);
            AssistantTaskSnapshot restored = Assert.Single(store.SavedTasks(session.Id));
            Assert.Equal(until, restored.BrowserAccessUntil);
            Assert.True(restored.BrowserAllowedForTask);
            Assert.False(BrowserSafety.HasAccess(new AiTask(), until.AddMinutes(-1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Conversation_capture_is_off_by_default_and_older_messages_are_hidden_until_enabled()
    {
        string root = Directory.CreateTempSubdirectory("ample-redux-session-test-").FullName;
        try
        {
            AssistantSessionStore store = new(root);
            AssistantSession session = store.Create("archive test", false);
            AiTask task = new() { SessionId = session.Id, Id = "task1", Request = new AiTaskRequest("custom", "project", "prompt", "song.amplet", "song.amplet", null, null, null, null) };
            AiLog conversation = new() { Kind = "user", Text = "earlier message" };
            store.AppendConversationLog(task, conversation);

            Assert.False(session.ArchiveConversation);
            Assert.Empty(store.SavedLog(session.Id, task.Id));
            Assert.Single(store.SavedConversation(session.Id, task.Id));

            store.SetConversationArchive(session.Id, true, includeEarlier: true);
            Assert.Single(store.SavedLog(session.Id, task.Id));
            Assert.True(new AssistantSessionStore(root).Get(session.Id).ArchiveConversation);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Redux_task_redaction_masks_credentials_and_bearer_tokens()
    {
        const string secret = "SENT-" + "INEL-secret-value";
        string archived = ReduxTaskArchive.Redact($"password={secret} Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345");

        Assert.DoesNotContain(secret, archived, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", archived, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", archived, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redux_task_record_keeps_redacted_content_addressed_proposal_text()
    {
        string root = Directory.CreateTempSubdirectory("ample-redux-task-test-").FullName;
        try
        {
            using Journal journal = await Journal.OpenAsync(new PhysicalFileSystem(), new SystemClock(), root, "redux-test-project", cancellationToken: CancellationToken.None);
            ReduxTaskArchive archive = new(root, journal);
            AiTask task = new() { Id = "task123", SessionId = "session123", Request = new AiTaskRequest("modify-file", "file", "password=SECRET_SENTINEL", "song.amplet", "song.amplet", null, null, null, null), Summary = "Done" };
            task.Changes.Add(new AiChange { Path = "song.amplet", Before = "token=SECRET_SENTINEL", After = "token=SECRET_SENTINEL_UPDATED", Added = 1, Removed = 1 });

            archive.RecordTask(task, "accepted", "node:history-base");

            JournalRecord record = Assert.Single(await journal.ReadAllAsync(CancellationToken.None));
            string payload = record.PayloadJson;
            Assert.DoesNotContain("SECRET_SENTINEL", payload, StringComparison.Ordinal);
            using JsonDocument parsed = JsonDocument.Parse(payload);
            Assert.Equal("node:history-base", parsed.RootElement.GetProperty("historyRevision").GetString());
            string hash = parsed.RootElement.GetProperty("changes")[0].GetProperty("before").GetProperty("sha256").GetString()!;
            string artifact = File.ReadAllText(Path.Combine(root, ".ample", "blackbox", "artifacts", hash[..2], hash + ".txt"));
            Assert.DoesNotContain("SECRET_SENTINEL", artifact, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", artifact, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Project_browser_permission_persists_across_tasks_and_can_be_revoked()
    {
        string root = Directory.CreateTempSubdirectory("ample-browser-project-test-").FullName;
        try
        {
            AssistantSessionStore store = new(root);
            AiTask firstTask = new();
            DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            store.SetBrowserAllowedForProject(true);
            Assert.True(store.BrowserAllowedForProject());
            Assert.True(BrowserSafety.HasAccess(firstTask, now, store.BrowserAllowedForProject()));

            AssistantSessionStore restarted = new(root);
            Assert.True(restarted.BrowserAllowedForProject());
            Assert.True(BrowserSafety.HasAccess(new AiTask(), now, restarted.BrowserAllowedForProject()));
            restarted.SetBrowserAllowedForProject(false);
            Assert.False(new AssistantSessionStore(root).BrowserAllowedForProject());
            Assert.False(BrowserSafety.HasAccess(new AiTask(), now, false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("password", "", "", "")]
    [InlineData("text", "username", "", "")]
    [InlineData("text", "", "current-password", "")]
    [InlineData("email", "", "", "")]
    [InlineData("text", "", "", "Email address")]
    [InlineData("search", "q", "", "")]
    public void Credentials_are_excluded_from_typeable_fields(string type, string name, string autocomplete, string label)
    {
        Assert.Equal(type != "search", BrowserSafety.IsCredentialField(type, name, autocomplete, label));
    }

    [Fact]
    public void Credential_navigation_remains_visible_while_credential_fields_stay_hidden()
    {
        Assert.False(BrowserSafety.ShouldHideCredentialControl(false, BrowserSafety.IsCredentialField("", "", "", "Login")));
        Assert.True(BrowserSafety.ShouldHideCredentialControl(true, BrowserSafety.IsCredentialField("password", "", "", "")));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.0.0.2", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.168.1.2", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("172.31.1.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    public void Browser_navigation_rejects_private_and_loopback_addresses(string address, bool expected) =>
        Assert.Equal(expected, AiService.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://audiohero.com", true)]
    [InlineData("http://example.com/search", true)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:owner" + "@example.com", false)]
    [InlineData("https://user:pass" + "@example.com", false)]
    public void Browser_actions_accept_only_credential_free_http_urls(string url, bool expected) =>
        Assert.Equal(expected, BrowserSafety.IsWebUrl(url));

    [Theory]
    [InlineData("audio/mpeg", "https://cdn.example.test/play?id=1", true)]
    [InlineData("application/octet-stream", "https://cdn.example.test/preview.MP3?token=hidden", true)]
    [InlineData("text/html", "https://cdn.example.test/search", false)]
    public void Browser_network_diagnostics_identify_audio_responses(string contentType, string url, bool expected) =>
        Assert.Equal(expected, BrowserSafety.IsAudioResponse(contentType, url));

    [RepoFact("src/Ample.Cli/McpCommands.cs", "web/app/ai.js")]
    public void Browser_tools_are_wired_as_non_read_only_and_excluded_from_batches()
    {
        string root = FindRoot();
        string mcp = File.ReadAllText(Path.Combine(root, "src", "Ample.Cli", "McpCommands.cs"));
        string ui = File.ReadAllText(Path.Combine(root, "web", "app", "ai.js"));
        string[] names = ["ample_browser_open", "ample_browser_read", "ample_browser_click", "ample_browser_type", "ample_browser_mouse_move", "ample_browser_media_mute", "ample_browser_screenshot", "ample_browser_network_stats", "ample_browser_js"];
        foreach (string name in names)
        {
            Assert.Contains($"\"{name}\"", mcp, StringComparison.Ordinal);
            if (name != "ample_browser_js") Assert.Contains($"\"{name}\" =>", mcp, StringComparison.Ordinal);
        }

        string batchLine = mcp.Split('\n').Single(line => line.Contains("if (tool is", StringComparison.Ordinal));
        string readOnlyLine = mcp.Split('\n').Single(line => line.Contains("readOnlyHint", StringComparison.Ordinal));
        foreach (string name in names)
        {
            Assert.Contains(name, batchLine, StringComparison.Ordinal);
            Assert.Contains(name, readOnlyLine, StringComparison.Ordinal);
        }

        Assert.All(names.Where(name => name != "ample_browser_js"), name => Assert.Contains(Routes.Catalog, route => route.Method == "GET" && route.Path == "/api/ai/context/" + name.Replace("ample_browser_", "browser-").Replace('_', '-')));
        Assert.Contains(Routes.Catalog, route => route.Path == "/api/ai/tasks/{id}/answer" && route.Summary.Contains("acceptForProject", StringComparison.Ordinal));
        Assert.Contains(Routes.Catalog, route => route.Method == "POST" && route.Path == "/api/ai/context/browser-js" && route.Summary.Contains("bypass", StringComparison.Ordinal));
        Assert.Contains("name == \"ample_browser_js\" ? \"/api/ai/context/browser-js\"", mcp, StringComparison.Ordinal);
        Assert.Contains("audioPreviewUrls", File.ReadAllText(Path.Combine(root, "src", "Ample.Server", "AiBrowser.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("browser-js?script=", mcp, StringComparison.Ordinal);
        Assert.Contains("Always allow in this project", ui, StringComparison.Ordinal);
        Assert.Contains("acceptForProject", ui, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hidden_login_form_does_not_make_a_signed_in_page_need_a_person_but_a_visible_one_does()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        IPage page = await browser.NewPageAsync();
        await page.SetContentAsync("<title>Freesound - Search</title><nav>Sounds Tags Forum</nav><div style='display:none'><form><input type='password'></form></div>");
        Assert.False(await BrowserSafety.NeedsHumanInputAsync(page));
        await page.SetContentAsync("<title>Log in</title><form><input type='text'><input type='password'></form>");
        Assert.True(await BrowserSafety.NeedsHumanInputAsync(page));
        await page.SetContentAsync("<title>Just a moment</title><p>Please verify you are human</p>");
        Assert.True(await BrowserSafety.NeedsHumanInputAsync(page));
    }

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("AMPLE repository root was not found.");
    }
}
