using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>Access outside the project: findings, requests with a confirmation, pass-through writes, the emergency stop, and the score import.</summary>
public sealed class AiAccessTests : IAsyncLifetime
{
    private string folder = string.Empty;
    private string library = string.Empty;
    private string outside = string.Empty;
    private ProjectSession session = null!;

    public async Task InitializeAsync()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root);
        folder = Path.Combine(Path.GetTempPath(), "ample-ai-access-" + Guid.NewGuid().ToString("N"));
        library = folder + "-library";
        outside = folder + "-outside";
        Directory.CreateDirectory(outside);
        using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "access", null, CancellationToken.None)) { }
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project access\n");
        const string xml = "<score-partwise><part-list><score-part id=\"P1\"><part-name>Lead</part-name><midi-instrument id=\"i\"><midi-program>41</midi-program></midi-instrument></score-part></part-list>"
            + "<part id=\"P1\"><measure number=\"1\"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time></attributes>"
            + "<note><pitch><step>C</step><octave>4</octave></pitch><duration>2</duration></note>"
            + "<note><pitch><step>E</step><alter>-1</alter><octave>4</octave></pitch><duration>1</duration></note>"
            + "<note><rest/><duration>1</duration></note></measure>"
            + "<measure number=\"2\"><note><pitch><step>G</step><octave>4</octave></pitch><duration>4</duration></note></measure></part></score-partwise>";
        await File.WriteAllTextAsync(Path.Combine(folder, "tune.musicxml"), xml);
        session = await ProjectSession.OpenAsync(new ServerOptions(folder, root ?? throw new InvalidOperationException("AMPLE root not found."), LibraryHome: library), new EventBus(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await session.DisposeAsync();
        foreach (string path in new[] { folder, library, outside })
        {
            try { Directory.Delete(path, true); } catch (IOException) { }
        }
    }

    private AiTask NewTask()
    {
        AiTask task = new() { Request = new AiTaskRequest("custom", "project", "x", "song.amplet", "song.amplet", null, null, null, null), Mode = "manual", Workspace = Path.Combine(folder, ".ample", "ai", "work", "t" + Guid.NewGuid().ToString("N")[..6]) };
        Directory.CreateDirectory(Path.Combine(task.Workspace, "project"));
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    private async Task<JsonObject> AskAsync(AiTask task, string path, string decision, int minutes = 5)
    {
        Task<JsonObject> request = session.Ai.RequestAccessAsync(task.ContextToken, path, "the findings of the Mozart task", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), "write", CancellationToken.None);
        for (int i = 0; i < 200 && task.Approvals.IsEmpty; i++) await Task.Delay(10);
        AiApproval ask = task.Approvals.Values.Single(a => a.Answered is null);
        Assert.Equal("access", ask.Kind);
        Assert.Contains("the findings of the Mozart task", ask.Detail, StringComparison.Ordinal);
        Assert.Contains(minutes + " minute", ask.Detail, StringComparison.Ordinal);
        ask.Decision.TrySetResult(decision);
        return await request;
    }

    [Fact]
    public void Findings_go_into_the_project_and_a_write_outside_needs_a_permission_first()
    {
        AiTask task = NewTask();
        JsonObject found = session.Ai.Findings(task.ContextToken, "Mozart K525", "# missing\n- a tool\n");
        Assert.StartsWith("notes/ai-findings/Mozart-K525-", found["path"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, found["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar))));

        string target = Path.Combine(outside, "findings.txt");
        Assert.Equal("AMP5611", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(task.ContextToken, target, "x")).DiagnosticCode);
        Assert.Equal("AMP5610", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(task.ContextToken, "findings.txt", "x")).DiagnosticCode);
        Assert.Equal("AMP5610", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(task.ContextToken, Path.Combine(folder, ".ample", "x.txt"), "x")).DiagnosticCode);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task An_allowed_request_lets_the_pass_through_write_and_a_denied_one_does_not()
    {
        AiTask task = NewTask();
        string target = Path.Combine(outside, "findings.txt");
        JsonObject denied = await AskAsync(task, target, "decline");
        Assert.False(denied["allowed"]!.GetValue<bool>());
        Assert.Equal("AMP5611", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(task.ContextToken, target, "x")).DiagnosticCode);

        AiTask second = NewTask();
        JsonObject allowed = await AskAsync(second, target, "accept", 3);
        Assert.True(allowed["allowed"]!.GetValue<bool>());
        Assert.Equal(3, allowed["minutes"]!.GetValue<int>());
        Assert.True(session.Ai.WriteOutside(second.ContextToken, target, "hello")["written"]!.GetValue<bool>());
        Assert.Equal("hello", File.ReadAllText(target));

        // the permission was for that task: another task has none
        Assert.Equal("AMP5611", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(NewTask().ContextToken, target, "x")).DiagnosticCode);
    }

    [Fact]
    public async Task Always_for_the_project_is_kept_for_later_tasks_and_can_be_revoked()
    {
        AiTask task = NewTask();
        string target = Path.Combine(outside, "always.txt");
        await AskAsync(task, target, "acceptForSession");
        Assert.Contains(session.Ai.ProjectGrants(), g => string.Equals(g.Path, target, StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Ai.WriteOutside(NewTask().ContextToken, target, "again")["written"]!.GetValue<bool>());
        session.Ai.RevokeGrant(target);
        Assert.Equal("AMP5611", Assert.Throws<AmpleException>(() => session.Ai.WriteOutside(NewTask().ContextToken, target, "x")).DiagnosticCode);
    }

    [Theory]
    [InlineData("accept30", true, false)]
    [InlineData("acceptForSession", false, true)]
    [InlineData("decline", false, false)]
    public async Task Browser_approval_is_per_task_and_offers_expiring_or_task_scoped_access(string decision, bool timed, bool taskScoped)
    {
        AiTask task = NewTask();
        Task<JsonObject> request = session.Ai.BrowserOpenAsync(task.ContextToken, "not-a-url", "test browser approval", CancellationToken.None);
        for (int i = 0; i < 200 && task.Approvals.IsEmpty; i++) await Task.Delay(10);
        AiApproval ask = Assert.Single(task.Approvals.Values);
        Assert.Equal("browser", ask.Kind);
        Assert.Contains("automated browser", ask.Detail, StringComparison.Ordinal);
        ask.Decision.TrySetResult(decision);
        await Assert.ThrowsAsync<AmpleException>(async () => await request);
        Assert.Equal(taskScoped, task.BrowserAllowedForTask);
        Assert.Equal(timed, task.BrowserAccessUntil is not null);
        Assert.Null(task.BrowserPage);
    }

    [Fact]
    public void A_request_that_goes_outside_the_working_folder_is_found_and_one_inside_is_not()
    {
        AiTask task = NewTask();
        MethodInfo find = typeof(AiService).GetMethod("OutsideTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string? Find(string method, JsonObject p) => (string?)find.Invoke(session.Ai, [task, method, p]);

        Assert.Equal("C:\\temp\\x", Find("item/fileChange/requestApproval", new JsonObject { ["grantRoot"] = "C:\\temp\\x" }));
        Assert.Null(Find("item/fileChange/requestApproval", new JsonObject { ["grantRoot"] = Path.Combine(task.Workspace, "project") }));
        Assert.Equal("D:\\notes\\a.txt", Find("item/commandExecution/requestApproval", new JsonObject { ["command"] = "Set-Content D:\\notes\\a.txt 'hi'" }));
        Assert.Null(Find("item/commandExecution/requestApproval", new JsonObject { ["command"] = "Get-Content D:\\notes\\a.txt" }));
        Assert.Null(Find("item/commandExecution/requestApproval", new JsonObject { ["command"] = "node scratch\\a.mjs" }));
    }

    [Fact]
    public void Task_work_files_use_a_separate_location_and_are_listed_apart_from_project_files()
    {
        AiTask task = NewTask();
        const string text = "task-only context";
        File.WriteAllText(Path.Combine(task.Workspace, "TASK.md"), text);
        File.WriteAllText(Path.Combine(task.Workspace, "rpc.log"), "private rpc data");
        Directory.CreateDirectory(Path.Combine(task.ProjectDir, "work"));
        File.WriteAllText(Path.Combine(task.ProjectDir, "work", "song.txt"), "project work file");
        Assert.Equal(text, AiService.ProposalText(task.ContextToken, "TASK.md", "work"));
        Assert.Null(AiService.ProposalText(task.ContextToken, "rpc.log", "work"));
        Assert.Equal("project work file", AiService.ProposalText(task.ContextToken, "work/song.txt", "project"));
        Assert.Null(AiService.ProposalText(task.ContextToken, "../outside.txt", "work"));
        JsonObject listing = AiService.WithAssistantInfo(task.ContextToken, new JsonObject());
        Assert.Contains("TASK.md", listing["taskWorkFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("rpc.log", listing["taskWorkFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("work/song.txt", listing["taskWorkFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Non_strict_mode_prompts_on_requests_and_strict_mode_keeps_selected_policy()
    {
        AiPolicy policy = AiPolicy.Read();
        JsonObject relaxed = AiService.ModeParameters("unsafe", false, policy, false);
        Assert.Equal("on-request", relaxed["approvalPolicy"]!.GetValue<string>());
        Assert.Equal("workspace-write", relaxed["sandbox"]!.GetValue<string>());
        Assert.Equal("on-request", AiService.ModeParameters("auto", true, policy, false)["approvalPolicy"]!.GetValue<string>());
        Assert.Equal("never", AiService.ModeParameters("auto", true, policy, true)["approvalPolicy"]!.GetValue<string>());
        JsonObject strict = AiService.ModeParameters("manual", false, policy, true);
        Assert.Equal("untrusted", strict["approvalPolicy"]!.GetValue<string>());
    }

    [Fact]
    public void A_musicxml_score_becomes_a_sequence_with_the_notes_tempo_and_a_playlist()
    {
        AiTask task = NewTask();
        JsonElement body = JsonSerializer.SerializeToElement(new { path = "tune.musicxml", @out = "sequences/tune.ampleseq", bars_per_pattern = 8 });
        JsonObject result = session.Ai.ImportScore(task.ContextToken, body);
        Assert.Equal(2, result["bars"]!.GetValue<int>());
        Assert.Equal(1, result["parts"]!.GetValue<int>());
        string text = File.ReadAllText(Path.Combine(task.Workspace, "project", "sequences", "tune.ampleseq"));
        Assert.Contains("channel lead gm file=pack:musescore-general program=40", text, StringComparison.Ordinal);
        Assert.Contains("1:0:2:60:90 1:2:1:63:90", text, StringComparison.Ordinal);
        Assert.Contains("2:0:4:67:90", text, StringComparison.Ordinal);
        Assert.Contains("clip part-1 track=1 at=1 for=2", text, StringComparison.Ordinal);

        // it never overwrites, and a PDF needs Audiveris (optical music recognition), which this test environment does not have configured
        Assert.Equal("AMP5243", Assert.Throws<AmpleException>(() => session.Ai.ImportScore(task.ContextToken, body)).DiagnosticCode);
        File.WriteAllBytes(Path.Combine(folder, "page.pdf"), [1, 2, 3]);
        Assert.Equal("AMP5464", Assert.Throws<AmpleException>(() => session.Ai.ImportScore(task.ContextToken, JsonSerializer.SerializeToElement(new { path = "page.pdf" }))).DiagnosticCode);

        // a genuinely unsupported extension still gets the general message
        File.WriteAllBytes(Path.Combine(folder, "song.abc"), [1, 2, 3]);
        Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => session.Ai.ImportScore(task.ContextToken, JsonSerializer.SerializeToElement(new { path = "song.abc" }))).DiagnosticCode);
    }

    [Fact]
    public void A_zipped_mxl_score_is_unzipped_and_imported_without_an_external_tool()
    {
        AiTask task = NewTask();
        string xml = File.ReadAllText(Path.Combine(folder, "tune.musicxml"));
        string mxlPath = Path.Combine(folder, "tune.mxl");
        using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.Open(mxlPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (StreamWriter w = new(zip.CreateEntry("tune.xml").Open())) w.Write(xml);
            using (StreamWriter w = new(zip.CreateEntry("META-INF/container.xml").Open())) w.Write("<container/>");
        }

        JsonObject result = session.Ai.ImportScore(task.ContextToken, JsonSerializer.SerializeToElement(new { path = "tune.mxl", @out = "sequences/fromMxl.ampleseq" }));
        Assert.Equal(1, result["parts"]!.GetValue<int>());
        Assert.Equal(2, result["bars"]!.GetValue<int>());
    }

    [Fact]
    public void A_change_of_model_and_effort_is_applied_to_the_next_turn_told_to_codex_and_visible_in_ample_project()
    {
        AiTask task = NewTask();
        FieldInfo registry = typeof(AiService).GetField("tasks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ConcurrentDictionary<string, AiTask>)registry.GetValue(session.Ai)!)[task.Id] = task;
        session.Ai.ChangeAssistant(task.Id, "gpt-6-sol", "high");
        Assert.Equal("gpt-6-sol", task.ModelNow);
        Assert.Equal("high", task.EffortNow);
        Assert.Single(task.QueuedMessages);
        Assert.Contains("smarter", task.QueuedMessages[0], StringComparison.Ordinal);
        Assert.Contains("reasoning high", task.QueuedMessages[0], StringComparison.Ordinal);

        JsonObject project = AiService.WithAssistantInfo(task.ContextToken, []);
        string settings = project["assistant"]!["settings"]!.GetValue<string>();
        Assert.Contains("gpt-6-sol", settings, StringComparison.Ordinal);
        Assert.Contains("reasoning high", settings, StringComparison.Ordinal);

        // the same choice again tells nothing more; a wrong effort is refused
        session.Ai.ChangeAssistant(task.Id, "gpt-6-sol", "high");
        Assert.Single(task.QueuedMessages);
        Assert.Equal("AMP5285", Assert.Throws<AmpleException>(() => session.Ai.ChangeAssistant(task.Id, null, "turbo")).DiagnosticCode);
    }

    [Fact]
    public void Compacting_is_seen_in_the_task_with_its_context_fill()
    {
        AiTask task = NewTask();
        MethodInfo notify = typeof(AiService).GetMethod("OnNotification", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Send(string method, string json) => notify.Invoke(null, [task, method, JsonNode.Parse(json)!.AsObject()]);

        Send("thread/tokenUsage/updated", """{"tokenUsage":{"last":{"totalTokens":170000},"total":{"totalTokens":900000},"modelContextWindow":200000}}""");
        JsonObject before = AiService.TaskJson(task, false);
        Assert.Equal(85, before["context"]!["percent"]!.GetValue<int>());
        Assert.False(before["compacting"]!.GetValue<bool>());

        Send("item/started", """{"item":{"type":"contextCompaction","id":"c1"}}""");
        Assert.True(AiService.TaskJson(task, false)["compacting"]!.GetValue<bool>());

        Send("item/completed", """{"item":{"type":"contextCompaction","id":"c1"}}""");
        JsonObject after = AiService.TaskJson(task, false);
        Assert.False(after["compacting"]!.GetValue<bool>());
        Assert.Equal(1, after["compactions"]!.GetValue<int>());
    }
}
