using System.Text;
using System.Text.Json;
using Ample.Storage.Projects;

namespace Ample.Server;

internal sealed record AssistantSessionTask(string Id, string Prompt, string Status, DateTimeOffset Updated, string Kind = "")
{
    /// <summary>Gets the state of the conversation of this task, worked out when the list is asked: live (Codex is attached), can (it can be resumed, with its thread and working copy) or no (it cannot).</summary>
    public string Resume { get; init; } = string.Empty;

    /// <summary>Gets whether the person archived the conversation (it is under Archived in the list, and can be resumed while its thread and working copy are kept).</summary>
    public bool Archived { get; init; }
}

internal sealed record AssistantSession(string Id, string Title, DateTimeOffset Created, DateTimeOffset Updated, string? ActiveTaskId, IReadOnlyList<AssistantSessionTask> Tasks)
{
    public bool ArchiveConversation { get; init; }
    public DateTimeOffset? ConversationArchiveFrom { get; init; }
}

internal sealed record AssistantTaskSnapshot(
    string Id,
    string SessionId,
    AiTaskRequest Request,
    string Status,
    DateTimeOffset Created,
    string Identity,
    string Mode,
    string Workspace,
    string ThreadId,
    string Summary,
    string? Error,
    Dictionary<string, string?> Baseline,
    AiChange[] Changes,
    string BuildStatus,
    string BuildMessage,
    bool Applied,
    bool Stale,
    string? RebaseMessage)
{
    public string[] AcceptedPaths { get; init; } = [];
    public string? OutputSong { get; init; }
    public bool SkipBuildRequested { get; init; }
    public string SkipBuildReason { get; init; } = string.Empty;
    public bool Archived { get; init; }
    public AiClip[] Clips { get; init; } = [];
    public int? ContinuationClipLimit { get; init; }
    public bool BrowserAllowedForTask { get; init; }
    public DateTimeOffset? BrowserAccessUntil { get; init; }
    public Dictionary<string, string?> Undo { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> Written { get; init; } = new(StringComparer.Ordinal);
    public string[] IncludedPaths { get; init; } = [];
    public string? SongBefore { get; init; }
}

/// <summary>Project-local durable records for assistant sessions and the recoverable portion of their Codex tasks.</summary>
internal sealed class AssistantSessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly string folder;
    private readonly string indexPath;
    private readonly string browserAccessPath;
    private readonly Dictionary<string, AssistantSession> sessions;

    public AssistantSessionStore(string projectRoot)
    {
        folder = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "ai", "assistant-sessions");
        indexPath = Path.Combine(folder, "index.json");
        browserAccessPath = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "ai", "browser-access.json");
        sessions = File.Exists(indexPath)
            ? JsonSerializer.Deserialize<Dictionary<string, AssistantSession>>(File.ReadAllText(indexPath)) ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
    }

    public bool BrowserAllowedForProject()
    {
        lock (gate) return File.Exists(browserAccessPath) && JsonSerializer.Deserialize<bool>(File.ReadAllText(browserAccessPath));
    }

    public void SetBrowserAllowedForProject(bool allowed)
    {
        lock (gate) WriteAtomic(browserAccessPath, JsonSerializer.Serialize(allowed));
    }

    public IReadOnlyList<AssistantSession> List()
    {
        lock (gate) return [.. sessions.Values.OrderByDescending(x => x.Updated)];
    }

    public AssistantSession Create(string title, bool archiveConversation)
    {
        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            AssistantSession session = new(Guid.NewGuid().ToString("N"), CleanTitle(title), now, now, null, []) { ArchiveConversation = archiveConversation, ConversationArchiveFrom = archiveConversation ? now : null };
            sessions.Add(session.Id, session);
            SaveIndex();
            return session;
        }
    }

    public AssistantSession Get(string id)
    {
        lock (gate) return sessions.TryGetValue(id, out AssistantSession? session) ? session : throw new Ample.Core.Abstractions.AmpleException("AMP5458", "There is no assistant session with that id.");
    }

    public AssistantSession Rename(string id, string title)
    {
        lock (gate)
        {
            AssistantSession current = Get(id);
            AssistantSession updated = current with { Title = CleanTitle(title), Updated = DateTimeOffset.UtcNow };
            sessions[id] = updated;
            SaveIndex();
            return updated;
        }
    }

    public AssistantSession SetConversationArchive(string id, bool enabled, bool includeEarlier)
    {
        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            AssistantSession current = Get(id);
            AssistantSession updated = current with { ArchiveConversation = enabled, ConversationArchiveFrom = enabled ? includeEarlier ? null : now : current.ConversationArchiveFrom, Updated = now };
            sessions[id] = updated;
            SaveIndex();
            return updated;
        }
    }

    public void Delete(string id)
    {
        lock (gate)
        {
            if (!sessions.Remove(id)) throw new Ample.Core.Abstractions.AmpleException("AMP5458", "There is no assistant session with that id.");
            string path = TaskPath(id);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            SaveIndex();
        }
    }

    public AssistantSession LinkTask(string id, string taskId, string prompt, string status)
    {
        lock (gate)
        {
            AssistantSession current = Get(id);
            AssistantSessionTask item = new(taskId, prompt, status, DateTimeOffset.UtcNow);
            AssistantSession updated = current with { Updated = item.Updated, ActiveTaskId = taskId, Tasks = [.. current.Tasks.Where(x => x.Id != taskId), item] };
            sessions[id] = updated;
            SaveIndex();
            return updated;
        }
    }

    public void UpdateTask(AiTask task, bool touch = true)
    {
        if (task.SessionId is null) return;
        lock (gate)
        {
            if (!sessions.TryGetValue(task.SessionId, out AssistantSession? current)) return;
            AssistantSessionTask? before = current.Tasks.FirstOrDefault(x => x.Id == task.Id);
            if (!touch && before is not null)
            {
                // a status that is put right when the server starts must not make the conversation look as if it was just used (and must not change the order of the list)
                AssistantSessionTask kept = new(task.Id, task.Request.Prompt, task.Status, before.Updated, task.Request.Kind);
                sessions[task.SessionId] = current with { Tasks = [.. current.Tasks.Select(x => x.Id == task.Id ? kept : x)] };
                SaveIndex();
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            AssistantSessionTask item = new(task.Id, task.Request.Prompt, task.Status, now, task.Request.Kind);
            AssistantSession updated = current with { Updated = now, ActiveTaskId = task.Id, Tasks = [.. current.Tasks.Where(x => x.Id != task.Id), item] };
            sessions[task.SessionId] = updated;
            SaveIndex();
        }
    }

    public void AppendLog(AiTask task, AiLog entry)
    {
        string? sessionId = task.SessionId;
        if (sessionId is null || IsConversation(entry.Kind)) return;
        AppendLogFile(task, entry, sessionId);
    }

    public void AppendConversationLog(AiTask task, AiLog entry)
    {
        string? sessionId = task.SessionId;
        if (sessionId is null || !IsConversation(entry.Kind)) return;
        AppendLogFile(task, entry, sessionId);
    }

    private void AppendLogFile(AiTask task, AiLog entry, string sessionId)
    {
        lock (gate)
        {
            Directory.CreateDirectory(TaskPath(sessionId));
            File.AppendAllText(Path.Combine(TaskPath(sessionId), task.Id + ".jsonl"), JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
        }
    }

    public void SaveTask(AiTask task)
    {
        if (task.SessionId is null) return;
        AssistantTaskSnapshot snapshot = new(
            task.Id,
            task.SessionId,
            task.Request,
            task.Status,
            task.Created,
            task.Identity,
            task.Mode,
            task.Workspace,
            task.ThreadId,
            task.Summary,
            task.Error,
            new(task.Baseline, StringComparer.Ordinal),
            [.. task.Changes],
            task.BuildStatus,
            task.BuildMessage,
            task.Applied,
            task.Stale,
            task.RebaseMessage)
        {
            AcceptedPaths = [.. task.AcceptedPaths],
            OutputSong = task.OutputSong,
            SkipBuildRequested = task.SkipBuildRequested,
            SkipBuildReason = task.SkipBuildReason,
            Archived = task.Archived,
            Clips = [.. task.ClipsNow()],
            ContinuationClipLimit = task.ContinuationClipLimit,
            BrowserAllowedForTask = task.BrowserAllowedForTask,
            BrowserAccessUntil = task.BrowserAccessUntil,
            Undo = new(task.Undo, StringComparer.Ordinal),
            Written = new(task.Written, StringComparer.Ordinal),
            IncludedPaths = [.. task.IncludedPaths],
            SongBefore = task.SongBefore,
        };
        lock (gate)
        {
            Directory.CreateDirectory(TaskPath(task.SessionId));
            WriteAtomic(Path.Combine(TaskPath(task.SessionId), task.Id + ".json"), JsonSerializer.Serialize(snapshot, Json));
        }
    }

    public IReadOnlyList<AssistantTaskSnapshot> SavedTasks(string sessionId)
    {
        lock (gate)
        {
            AssistantSession session = Get(sessionId);
            string path = TaskPath(session.Id);
            if (!Directory.Exists(path)) return [];
            return [.. Directory.EnumerateFiles(path, "*.json").Select(file => JsonSerializer.Deserialize<AssistantTaskSnapshot>(File.ReadAllText(file))).Where(x => x is not null).Select(x => x!)];
        }
    }

    public IReadOnlyList<AiLog> SavedLog(string sessionId, string taskId)
    {
        lock (gate)
        {
            string path = Path.Combine(TaskPath(Get(sessionId).Id), taskId + ".jsonl");
            if (!File.Exists(path)) return [];
            bool archiveConversation = Get(sessionId).ArchiveConversation;
            DateTimeOffset? from = Get(sessionId).ConversationArchiveFrom;
            return [.. File.ReadLines(path).Select(line => JsonSerializer.Deserialize<AiLog>(line)).Where(x => x is not null).Select(x => x!).Where(x => !IsConversation(x.Kind) || archiveConversation && (from is null || x.At >= from)).TakeLast(400)];
        }
    }

    public IReadOnlyList<AiLog> SavedConversation(string sessionId, string taskId)
    {
        lock (gate)
        {
            string path = Path.Combine(TaskPath(Get(sessionId).Id), taskId + ".jsonl");
            if (!File.Exists(path)) return [];
            DateTimeOffset? from = Get(sessionId).ConversationArchiveFrom;
            return [.. File.ReadLines(path).Select(line => JsonSerializer.Deserialize<AiLog>(line)).Where(x => x is not null).Select(x => x!).Where(x => IsConversation(x.Kind) && (from is null || x.At < from)).TakeLast(400)];
        }
    }

    private static bool IsConversation(string kind) => kind is "user" or "assistant" or "thought" or "tool" or "approval";

    private string TaskPath(string sessionId) => Path.Combine(folder, sessionId);

    private void SaveIndex() => WriteAtomic(indexPath, JsonSerializer.Serialize(sessions, Json));

    private static string CleanTitle(string title)
    {
        string clean = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.Length == 0) throw new Ample.Core.Abstractions.AmpleException("AMP5285", "Give the assistant session a name.");
        return clean.Length > 100 ? clean[..100] : clean;
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
