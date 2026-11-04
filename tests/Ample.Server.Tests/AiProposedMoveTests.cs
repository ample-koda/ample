using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Server.Tests;

/// <summary>Codex proposes moving a project file or folder (ample_move): nothing moves before Accept, then the references in the songs follow, and Undo takes it back.</summary>
public sealed class AiProposedMoveTests : IAsyncLifetime
{
    private string folder = string.Empty;
    private string library = string.Empty;
    private ProjectSession session = null!;

    public async Task InitializeAsync()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Ample.sln"))) root = Path.GetDirectoryName(root);
        folder = Path.Combine(Path.GetTempPath(), "ample-ai-move-" + Guid.NewGuid().ToString("N"));
        library = folder + "-library";
        using (OpenProject project = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), folder, "move", null, CancellationToken.None)) { }
        Directory.CreateDirectory(Path.Combine(folder, "sounds"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "sounds", "kick.wav"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(folder, "song.amplet"), "project move\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "other.amplet"), "project other\nimport-audio kick at 1 for 1 {\n  file sounds/kick.wav\n}\n");
        session = await ProjectSession.OpenAsync(new ServerOptions(folder, root ?? throw new InvalidOperationException("AMPLE root not found."), LibraryHome: library), new EventBus(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await session.DisposeAsync();
        try { Directory.Delete(folder, true); Directory.Delete(library, true); }
        catch (IOException) { }
    }

    private static AiTask NewTask(string mode)
    {
        AiTask task = new() { Request = new AiTaskRequest("comment", "file", "move the kick", "song.amplet", "song.amplet", null, null, null, null), Mode = mode };
        FieldInfo tokens = typeof(AiService).GetField("ContextTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((ConcurrentDictionary<string, AiTask>)tokens.GetValue(null)!)[task.ContextToken] = task;
        return task;
    }

    private static JsonElement Body(string source, string destination, string confirm) =>
        JsonSerializer.SerializeToElement(new { source, destination, confirm, reason = "the kick belongs with the drums" });

    private async Task<string?> Apply(AiTask task, params string[] ticked)
    {
        MethodInfo apply = typeof(AiService).GetMethod("ApplyMovesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<string?>)apply.Invoke(session.Ai, [task, ticked, null, CancellationToken.None])!;
    }

    [Fact]
    public async Task A_proposed_move_changes_nothing_until_it_is_applied_and_the_references_follow()
    {
        AiTask task = NewTask("manual");
        System.Text.Json.Nodes.JsonObject answer = await session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", "drums/kick.wav", "propose"), CancellationToken.None);
        Assert.True(answer["allowed"]!.GetValue<bool>());
        Assert.Equal(1, answer["references"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));
        Assert.Single(task.Moves);

        // not ticked: nothing happens
        await Apply(task);
        Assert.True(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));

        await Apply(task, task.Moves[0].Id);
        Assert.False(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));
        Assert.True(File.Exists(Path.Combine(folder, "drums", "kick.wav")));
        Assert.Contains("file drums/kick.wav", File.ReadAllText(Path.Combine(folder, "other.amplet")), StringComparison.Ordinal);
        Assert.True(task.Moves[0].Applied);

        // undo puts it back, the reference too
        MethodInfo undo = typeof(AiService).GetMethod("UndoMovesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)undo.Invoke(session.Ai, [task, CancellationToken.None])!;
        Assert.True(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));
        Assert.Contains("file sounds/kick.wav", File.ReadAllText(Path.Combine(folder, "other.amplet")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_move_is_refused_when_it_would_overwrite_leave_the_project_or_move_the_song()
    {
        AiTask task = NewTask("manual");
        await File.WriteAllBytesAsync(Path.Combine(folder, "sounds", "snare.wav"), [9]);
        await Assert.ThrowsAsync<Ample.Core.Abstractions.AmpleException>(() => session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", "sounds/snare.wav", "propose"), CancellationToken.None));
        await Assert.ThrowsAsync<Ample.Core.Abstractions.AmpleException>(() => session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", ".ample/kick.wav", "propose"), CancellationToken.None));
        await Assert.ThrowsAsync<Ample.Core.Abstractions.AmpleException>(() => session.Ai.ProposeMoveAsync(task.ContextToken, Body("song.amplet", "moved.amplet", "propose"), CancellationToken.None));
        await Assert.ThrowsAsync<Ample.Core.Abstractions.AmpleException>(() => session.Ai.ProposeMoveAsync(task.ContextToken, Body("missing.wav", "elsewhere.wav", "propose"), CancellationToken.None));
        Assert.Empty(task.Moves);
    }

    private AiTask ReadyTask(string mode)
    {
        AiTask task = NewTask(mode);
        task.Status = "ready";
        task.BuildStatus = "ok";
        task.Workspace = Path.Combine(folder, ".ample", "ai-test-work");
        Directory.CreateDirectory(task.Workspace);
        task.Changes.Add(new AiChange { Path = "notes/new.txt", Before = null, After = "hello" });
        FieldInfo tasks = typeof(AiService).GetField("tasks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ConcurrentDictionary<string, AiTask>)tasks.GetValue(session.Ai)!)[task.Id] = task;
        return task;
    }

    [Fact]
    public async Task Accept_selected_makes_the_approved_move_and_leaves_a_proposed_one_that_is_not_ticked()
    {
        AiTask task = ReadyTask("unsafe");   // in this mode a question is not asked: the move counts as approved
        await session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", "drums/kick.wav", "ask"), CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(folder, "sounds", "snare.wav"), [9]);
        await session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/snare.wav", "drums/snare.wav", "propose"), CancellationToken.None);
        Assert.True(task.Moves[0].Approved);
        Assert.False(task.Moves[1].Approved);

        await session.Ai.AcceptSelectedAsync(task.Id, ["notes/new.txt"], string.Empty, null, false, CancellationToken.None);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(folder, "notes", "new.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "drums", "kick.wav")));      // approved: made
        Assert.False(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));
        Assert.True(File.Exists(Path.Combine(folder, "sounds", "snare.wav")));    // proposed and not ticked: still there
        Assert.Contains("file drums/kick.wav", File.ReadAllText(Path.Combine(folder, "other.amplet")), StringComparison.Ordinal);
        Assert.True(task.Moves[0].Applied);
        Assert.False(task.Moves[1].Applied);
    }

    [Fact]
    public async Task A_move_that_Codex_asks_for_waits_for_the_owner_and_a_no_is_passed_on()
    {
        AiTask task = NewTask("manual");
        Task<System.Text.Json.Nodes.JsonObject> pending = session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", "drums/kick.wav", "ask"), CancellationToken.None);
        for (int i = 0; i < 50 && task.Approvals.IsEmpty; i++) await Task.Delay(50);
        AiApproval ask = Assert.Single(task.Approvals.Values);
        Assert.Equal("move", ask.Kind);
        Assert.Equal("waiting-approval", task.Status);
        ask.Decision.TrySetResult("decline");
        System.Text.Json.Nodes.JsonObject answer = await pending;
        Assert.False(answer["allowed"]!.GetValue<bool>());
        Assert.Empty(task.Moves);
        Assert.True(File.Exists(Path.Combine(folder, "sounds", "kick.wav")));

        // a yes is recorded as approved and needs no tick at Accept
        task.Approvals.Clear();
        Task<System.Text.Json.Nodes.JsonObject> second = session.Ai.ProposeMoveAsync(task.ContextToken, Body("sounds/kick.wav", "drums/kick.wav", "ask"), CancellationToken.None);
        for (int i = 0; i < 50 && task.Approvals.IsEmpty; i++) await Task.Delay(50);
        task.Approvals.Values.Single().Decision.TrySetResult("accept");
        Assert.True((await second)["allowed"]!.GetValue<bool>());
        Assert.True(task.Moves.Single().Approved);
        await Apply(task);
        Assert.True(File.Exists(Path.Combine(folder, "drums", "kick.wav")));
    }
}
