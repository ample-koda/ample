using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Microsoft.Playwright;

namespace Ample.Server;

/// <summary>What the user asked Codex to do.</summary>
/// <param name="Kind">What kind of help: <c>generate-sequence</c>, <c>modify-sequence</c>, <c>generate-events</c>, <c>modify-selection</c>, <c>modify-file</c>, <c>modify-linked</c>, <c>refactor</c>, <c>comment</c>, <c>explain</c> or <c>custom</c>.</param>
/// <param name="Scope"><c>selection</c>, <c>block</c>, <c>sequence</c>, <c>file</c>, <c>linked</c> or <c>project</c>.</param>
/// <param name="Prompt">The request in the words of the user.</param>
/// <param name="Path">The file the request is about (a song or a sequence).</param>
/// <param name="Song">The song that is validated and built (defaults to <paramref name="Path"/>).</param>
/// <param name="FromLine">First line of the selection (1-based).</param>
/// <param name="ToLine">Last line of the selection.</param>
/// <param name="Block">The block the request is about.</param>
/// <param name="Model">An optional model override for this request; null inherits the server setting.</param>
/// <param name="Hums">The hummed recordings the user attached (ids kept by the server).</param>
/// <param name="Offered">Audio files the user offered as material (ids kept by the server).</param>
/// <param name="ContinueFrom">The id of an earlier AI request this one continues (its prompt, result and changes are handed to Codex).</param>
/// <param name="Effort">The reasoning effort chosen for this request (low, medium, high, xhigh), "default" to let the model choose, or null for the server setting.</param>
/// <param name="Blocks">The block IDs that form the initial focus when starting from a canvas selection or group.</param>
/// <param name="Attachments">The files the user attached to the request (ids kept by the server: pictures are shown to the model, other files are put in the working copy).</param>
internal sealed record AiTaskRequest(string Kind, string Scope, string Prompt, string Path, string Song, int? FromLine, int? ToLine, string? Block, string? Model, IReadOnlyList<string>? Hums = null, IReadOnlyList<string>? Offered = null, string? ContinueFrom = null, string? Effort = null, IReadOnlyList<string>? Blocks = null, IReadOnlyList<string>? Attachments = null)
{
    /// <summary>What the task sees (plan/editor-tabs.md, part 5): <c>unsaved</c> (the default: the working copies of the open files, what is on the screen) or <c>saved</c> (the files on disk only).</summary>
    public string Context { get; init; } = "unsaved";

    /// <summary>When true, retains the selected strict permission policy instead of requesting approval on access boundaries.</summary>
    public bool Strict { get; init; }
}

internal sealed class AiLog
{
    public JsonObject? Details { get; init; }

    /// <summary>Gets or sets the number of this entry in the log of its task (never reused), so a client can ask for what came after the last one it has.</summary>
    public long Seq { get; set; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public string Kind { get; init; } = "info";

    public string Text { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string? Why { get; init; }
}

/// <summary>A move of a project file or folder that Codex proposed: it is made at Accept (never in the temporary copy), with the references in the songs and modules following.</summary>
internal sealed class AiMove
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public bool IsDir { get; init; }

    /// <summary>Gets the number of lines in songs and modules that will be changed when the move is made (worked out when it was proposed).</summary>
    public int References { get; init; }

    /// <summary>Gets <c>ask</c> (the owner was asked while Codex waited) or <c>propose</c> (listed with the proposal, ticked at Accept).</summary>
    public string Mode { get; init; } = "propose";

    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the owner already said yes (an answer to a question while Codex waited); such a move is made at Accept.</summary>
    public bool Approved { get; set; }

    public bool Applied { get; set; }
}

internal sealed class AiApproval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];

    public string Kind { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? Answered { get; set; }
}

internal sealed class AiChange
{
    public string Path { get; init; } = string.Empty;

    public string Status { get; init; } = "modified";

    public string? Before { get; init; }

    public string? After { get; set; }

    public int Added { get; set; }

    public int Removed { get; set; }

    public string? Why { get; init; }
}

/// <summary>A piece of audio that Codex presents to the user: the render of a block or a slice of it, with what it is and why it is shown.</summary>
internal sealed class AiClip
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Block { get; init; } = string.Empty;

    /// <summary><c>proposal</c> (as Codex has changed it) or <c>current</c> (the project as it is now).</summary>
    public string Source { get; init; } = string.Empty;

    public double From { get; init; }

    public double To { get; init; }

    public double Seconds { get; init; }

    public string Why { get; init; } = string.Empty;

    public string? ObsoleteReason { get; set; }

    public string File { get; init; } = string.Empty;

    public string AudioHash { get; set; } = string.Empty;

    public string? IdenticalTo { get; init; }

    public JsonArray Settings { get; init; } = [];

    public string[] Trial { get; init; } = [];

    public string MediaType { get; init; } = "audio/wav";

    public string? WorkspaceAssetPath { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An audio file the user offered to Codex.</summary>
internal sealed class OfferedFile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The path the song uses for it (and the path it gets in the project when it comes along).</summary>
    public string ProjectPath { get; init; } = string.Empty;

    public long Size { get; init; }

    public double Seconds { get; init; }
}

/// <summary>One alternative of a proposal: a name, one line about it, the blocks that belong to it and the block to listen to.</summary>
internal sealed record AiVariant(string Name, string Summary, IReadOnlyList<string> Blocks, string Listen);

/// <summary>A list that other requests may read while it is rebuilt: every enumeration is a snapshot, and a whole new content is put in at once (Replace).</summary>
/// <typeparam name="T">The item type.</typeparam>
internal sealed class SnapshotList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly object gate = new();
    private List<T> items = [];

    public int Count { get { lock (gate) return items.Count; } }

    public bool IsReadOnly => false;

    public T this[int index]
    {
        get { lock (gate) return items[index]; }
        set { lock (gate) items[index] = value; }
    }

    public void Add(T item) { lock (gate) items.Add(item); }

    public void AddRange(IEnumerable<T> more) { T[] copy = [.. more]; lock (gate) items.AddRange(copy); }

    public void Replace(IEnumerable<T> content) { List<T> copy = [.. content]; lock (gate) items = copy; }

    public void Clear() { lock (gate) items = []; }

    public int RemoveAll(Predicate<T> match) { lock (gate) { List<T> kept = [.. items.Where(x => !match(x))]; int removed = items.Count - kept.Count; items = kept; return removed; } }

    public bool Contains(T item) { lock (gate) return items.Contains(item); }

    public void CopyTo(T[] array, int arrayIndex) { lock (gate) items.CopyTo(array, arrayIndex); }

    public int IndexOf(T item) { lock (gate) return items.IndexOf(item); }

    public void Insert(int index, T item) { lock (gate) items.Insert(index, item); }

    public bool Remove(T item) { lock (gate) return items.Remove(item); }

    public void RemoveAt(int index) { lock (gate) items.RemoveAt(index); }

    public IEnumerator<T> GetEnumerator() { T[] snapshot; lock (gate) snapshot = [.. items]; return ((IEnumerable<T>)snapshot).GetEnumerator(); }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class AiTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    public AiTaskRequest Request { get; init; } = null!;

    public Guid ProjectId { get; init; }

    public string? SessionId { get; init; }

    public Action<AiTask, AiLog>? LogAdded { get; set; }

    public string Status { get; set; } = "preparing";

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public string Identity { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Workspace { get; set; } = string.Empty;

    public string ProjectDir => System.IO.Path.Combine(Workspace, "project");

    public Dictionary<string, string?> Baseline { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<AiLog> Log { get; } = new();

    public ConcurrentDictionary<string, AiApproval> Approvals { get; } = new();

    public bool BrowserAllowedForTask { get; set; }

    public bool WebSearchAllowed { get; set; }

    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> WebSearchResultUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int BraveSearchCount;

    public DateTimeOffset? LastBraveSearchAt { get; set; }

    public int BrowserConsoleErrorCount;

    public int BrowserRequestCount;

    public int BrowserResponseCount;

    public System.Collections.Concurrent.ConcurrentQueue<string> BrowserConsoleMessages { get; } = new();

    public System.Collections.Concurrent.ConcurrentDictionary<int, int> BrowserStatusCounts { get; } = new();

    public DateTimeOffset? BrowserAccessUntil { get; set; }

    public IBrowserContext? BrowserContext { get; set; }

    public IPage? BrowserPage { get; set; }

    public List<BrowserElement> BrowserElements { get; } = [];

    public string Summary { get; set; } = string.Empty;

    public string? Error { get; set; }

    public SnapshotList<AiChange> Changes { get; } = new();

    public System.Collections.Concurrent.ConcurrentDictionary<string, string> ChangeReasons { get; } = new(StringComparer.Ordinal);

    public SnapshotList<string> Diagnostics { get; } = new();

    public string BuildStatus { get; set; } = "none";

    public string BuildMessage { get; set; } = string.Empty;

    public string? OutputSong { get; set; }

    public bool SkipBuildRequested { get; set; }

    public string SkipBuildReason { get; set; } = string.Empty;

    /// <summary>The block the last build check was limited to (null: the whole song up to its master).</summary>
    public string? BuildOnly { get; set; }

    /// <summary>Counts the successful builds, so that a page can tell when the audio is another one.</summary>
    public int BuildVersion { get; set; }

    /// <summary>Clips of audio that Codex presented to the user to listen to (the render of a block, or a slice of it), with what they are.</summary>
    public List<AiClip> Clips { get; } = [];

    /// <summary>Guards <see cref="Clips"/>: a tool thread adds clips while the window reads them.</summary>
    public object ClipsLock { get; } = new();

    /// <summary>A copy of the clips taken under the lock, for reading and for counting.</summary>
    public AiClip[] ClipsNow()
    {
        lock (ClipsLock)
        {
            return [.. Clips];
        }
    }
    public int? ContinuationClipLimit { get; set; }

    /// <summary>Gets or sets what a new Codex session of this task must be told before its first message (what was asked, what is already changed); empty otherwise.</summary>
    public string PendingContext { get; set; } = string.Empty;

    /// <summary>Gets the moves of project files and folders that Codex proposed.</summary>
    public List<AiMove> Moves { get; } = [];
    public SemaphoreSlim ReviewGate { get; } = new(1, 1);

    /// <summary>Gets the measuring calls (build and listen) that are still running, by call: a call that took longer than a tool call may last goes on here, and the same call joins it.</summary>
    public Dictionary<string, Task<JsonObject>> RunningTools { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, long> ToolStartTicks { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the answers of measuring calls that finished after the tool call gave up waiting; the next identical call gets it once.</summary>
    public Dictionary<string, JsonObject> ReadyTools { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the measuring calls that nobody waits for any more.</summary>
    public HashSet<string> AbandonedTools { get; } = new(StringComparer.Ordinal);

    /// <summary>The take of the last block that was built in the temporary listening project (so it can be presented).</summary>
    public (string Root, string TakeId, string Block)? LastListen { get; set; }

    /// <summary>For the reads of this task: the state of the working folder at the last read of each thing, and how often it was read.</summary>
    public ConcurrentDictionary<string, (string Stamp, int Count)> ReadCache { get; } = new(StringComparer.Ordinal);

    /// <summary>Audio files the user offered to this task: they exist in the temporary copy at their project path, and come along into the project only when the user says so at Accept.</summary>
    public List<OfferedFile> Offered { get; } = [];

    /// <summary>The files an accepted proposal copied into the project (to take them away again on Undo).</summary>
    public List<string> IncludedPaths { get; } = [];

    public List<string> AcceptedPaths { get; } = [];

    /// <summary>The hummed melodies the user handed to this task (ids of the recordings kept by the server).</summary>
    public List<string> Hums { get; } = [];

    /// <summary>The pictures the user attached that the next turn (or steer) shows to the model: paths in the working copy, taken when the turn is sent.</summary>
    public List<string> PendingImages { get; } = [];

    /// <summary>For each block of the last build check that did not build: why, a hint, what feeds it and its text.</summary>
    public JsonArray BuildDetails { get; set; } = [];

    /// <summary>How many times Codex was sent back because the proposal did not build.</summary>
    public int BuildRepairs { get; set; }

    public string ListenKey { get; set; } = string.Empty;
    public string CurrentListenKey { get; set; } = string.Empty;

    /// <summary>Gets or sets when Codex last sent anything (an event, a question): a long silence while a turn should be running means the turn may be gone.</summary>
    public DateTimeOffset LastNotification { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets whether a Codex turn is running (false while AMPLE checks and builds the result of a turn).</summary>
    public bool TurnActive { get; set; }

    /// <summary>Gets or sets whether the person stopped the turn that runs (turn/interrupt): the conversation stays open, and the turn that ends as interrupted counts as ended.</summary>
    public bool StoppedByUser { get; set; }

    /// <summary>Gets or sets whether the person archived the conversation.</summary>
    public bool Archived { get; set; }

    /// <summary>Gets or sets whether AMPLE is checking and building the result of a turn (from the end of a turn to the end of that flow): a message that arrives then is kept until it is done.</summary>
    public bool PostTurn { get; set; }

    /// <summary>Gets the messages of the user that arrived while no turn was running; they are sent to Codex when the check and the build are done.</summary>
    public List<string> QueuedMessages { get; } = [];

    /// <summary>Gets the answers of ample_listen by proposal state, block and analysers, so a repeated measurement of an unchanged proposal is answered at once.</summary>
    public Dictionary<string, JsonObject> ListenResults { get; } = new(StringComparer.Ordinal);

    /// <summary>How far the build check is: blocks done, blocks in the song, and what is being made right now (with its steps), like the real build shows.</summary>
    public int BuildDone { get; set; }

    public int BuildTotal { get; set; }

    public ConcurrentDictionary<string, BlockProgress> BuildLive { get; } = new(StringComparer.Ordinal);

    /// <summary>The Codex session of the task. It stays open after the first answer so that the user can go on (change the request, steer, ask for a small change) until the proposal is accepted or rejected.</summary>
    public CodexRpc? Rpc { get; set; }

    public string ThreadId { get; set; } = string.Empty;

    /// <summary>The turn Codex is working on (needed to steer it).</summary>
    public string TurnId { get; set; } = string.Empty;

    public TaskCompletionSource<string>? TurnDone { get; set; }

    public StringBuilder MessageText { get; } = new();

    /// <summary>The problems (errors and warnings) the song already had before the task; only new ones count against the proposal.</summary>
    public HashSet<string>? BaselineProblems { get; set; }

    /// <summary>How many times AMPLE sent Codex back to fix problems it introduced, in the last round of work.</summary>
    public int Repairs { get; set; }

    public DateTimeOffset LastActive { get; set; } = DateTimeOffset.UtcNow;

    public StringBuilder Reasoning { get; } = new();

    /// <summary>Gets or sets whether Codex is compacting its conversation right now (it summarizes what it has so far to make room).</summary>
    public bool Compacting { get; set; }

    /// <summary>Gets or sets how many times the conversation was compacted.</summary>
    public int Compactions { get; set; }

    /// <summary>Gets or sets how many tokens the conversation takes of the context window of the model (0: not known yet).</summary>
    public long ContextUsed { get; set; }

    /// <summary>Gets or sets the context window of the model in tokens (0: not known yet).</summary>
    public long ContextWindow { get; set; }

    /// <summary>Gets or sets the model chosen while the task runs (null: the one of the request or the server).</summary>
    public string? ModelNow { get; set; }

    /// <summary>Gets or sets the reasoning effort chosen while the task runs (null: the one of the request or the server).</summary>
    public string? EffortNow { get; set; }

    /// <summary>What happened to each block in the last build check (block, status), for the read-only preview.</summary>
    public Dictionary<string, (string Status, bool Audio)> BuildBlocks { get; } = new(StringComparer.Ordinal);

    /// <summary>The take of every built block that made audio, in the temporary project (so it can be played without building again).</summary>
    public Dictionary<string, string> BuildTakes { get; } = new(StringComparer.Ordinal);

    /// <summary>What the temporary project was staged from; it is kept while this does not change.</summary>
    public string StagedKey { get; set; } = string.Empty;

    public List<string> BuildFailures { get; } = [];

    public string? AudioFile { get; set; }

    public bool Stale { get; set; }

    public List<string> StaleFiles { get; } = [];

    public string? RebaseMessage { get; set; }

    public CancellationTokenSource Cancel { get; set; } = new();

    public Dictionary<string, string?> Undo { get; } = new(StringComparer.Ordinal);

    public string? SongBefore { get; set; }

    public bool Applied { get; set; }

    public string ContextToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The Codex account the task waits for and uses.</summary>
    public string Key { get; set; } = string.Empty;

    public string? ValidateRoot { get; set; }

    /// <summary>Gets or sets why the proposal is refused for good: it would widen the capability policy or the limits of a song (never accepted, not even with Accept anyway).</summary>
    public string? PolicyViolation { get; set; }

    /// <summary>What the AI says it is thinking about right now (shown to the user as a bubble).</summary>
    public string Thought { get; set; } = string.Empty;

    public string ThoughtSource { get; set; } = string.Empty;

    /// <summary>The alternatives the AI offered with ample_offer_variants: all are in the one proposal, each says which blocks belong to it.</summary>
    public List<AiVariant> Variants { get; set; } = [];

    /// <summary>The answer a Koda question was delivered with (ample_answer): a title, a short answer and the details that unfold (markdown).</summary>
    public string AnswerTitle { get; set; } = string.Empty;
    public string AnswerText { get; set; } = string.Empty;
    public string AnswerMore { get; set; } = string.Empty;

    /// <summary>What the Do that button of the answer would do, written by the AI; empty when the answer only explains (the button is then not shown).</summary>
    public string AnswerAction { get; set; } = string.Empty;

    public DateTimeOffset ThoughtAt { get; set; }

    public void Add(string kind, string text)
    {
        int limit = kind == "assistant" ? 8000 : 600;
        Enqueue(new AiLog { Kind = kind, Text = kind == "user" || text.Length <= limit ? text : text[..limit] + "…" });
    }

    /// <summary>Puts a log entry that was saved earlier back, with a new number.</summary>
    public void AddRestored(AiLog line)
    {
        line.Seq = Interlocked.Increment(ref logSeq);
        Log.Enqueue(line);
    }

    public void AddOperation(string kind, string text, JsonObject item)
    {
        Enqueue(new AiLog { Kind = kind, Text = CompactOperation(text), Details = (JsonObject)item.DeepClone() });
    }

    private static string CompactOperation(string text) => text.Length > 600 ? text[..600] + "…" : text;

    public void AddChange(AiChange change)
    {
        string action = change.Status switch { "added" => "Added", "deleted" => "Deleted", _ => "Changed" };
        Enqueue(new AiLog { Kind = "change", Text = $"{action}: {change.Path}", Path = change.Path, Why = change.Why });
    }

    private long logSeq;

    /// <summary>Gets the number of the newest log entry.</summary>
    public long LogSeq => Interlocked.Read(ref logSeq);

    private void Enqueue(AiLog entry)
    {
        entry.Seq = Interlocked.Increment(ref logSeq);
        Log.Enqueue(entry);
        while (Log.Count > 400)
        {
            Log.TryDequeue(out _);
        }

        LogAdded?.Invoke(this, entry);
    }
}

internal sealed class IgnoreTakeSelections(IBuildControl source) : IBuildControl
{
    public ulong? SeedFor(string block) => source.SeedFor(block);
    public string? SelectedTake(string block) => null;
    public string? PinnedMelodyDataId(string block) => source.PinnedMelodyDataId(block);
}

/// <summary>
/// The AI assistant of a project. Codex proposes, AMPLE validates, the user decides: Codex works in a temporary copy of the files in scope, AMPLE compares that copy with what the
/// project looked like when the task began, parses and builds the result, and only an explicit Accept brings it into the project.
/// </summary>
internal sealed partial class AiService(ProjectSession s) : IAsyncDisposable
{
    private const int MaxFiles = 300;
    private const long MaxFileBytes = 1_000_000, MaxTotalBytes = 8_000_000;
    private bool pruned;
    private static readonly string[] TextExtensions = [".amplet", ".ampleseq", ".seq", ".lyr", ".lrc", ".amplelrc", ".chordpro", ".chords", ".module", ".amplec", ".txt", ".md", ".csv", ".json"];
    private static readonly ConcurrentDictionary<string, AiTask> Live = new(StringComparer.Ordinal);
    private static string? versionCache;
    private static string? versionBinary;
    private readonly ConcurrentDictionary<string, AiTask> tasks = new(StringComparer.Ordinal);
    private readonly AssistantSessionStore sessionStore = new(s.Root);
    private readonly ReduxTaskArchive reduxTasks = new(s.Root, s.Journal);
    private bool restoredTasks;
    private readonly object restoreGate = new();
    private DeviceLogin? device;
    private static readonly ConcurrentDictionary<string, AiTask> ContextTokens = new(StringComparer.Ordinal);

    /// <summary>Whether this is the read-only context token of a running AI task.</summary>
    public static bool IsContextToken(string? token) => !string.IsNullOrEmpty(token) && ContextTokens.ContainsKey(token);

    /// <summary>
    /// Codex asks to read a source asset that is too big (or not text) to hand over as text: a sample, a recording. The user is asked like for any other request of Codex (in Unsafe / Auto-all
    /// it is allowed at once); when allowed, AMPLE copies the file into the temporary copy under <c>assets/</c>, where Codex can read it. The file is never part of the proposal.
    /// </summary>
    /// <summary>The AI tells what it is thinking about: the user sees the latest note as a bubble and in the activity log.</summary>
    public static JsonObject SetThought(string token, string text)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string clean = text.Trim();
        task.Thought = clean.Length > 400 ? clean[..400] + "…" : clean;
        task.ThoughtSource = "tool";
        task.ThoughtAt = DateTimeOffset.UtcNow;
        if (task.Thought.Length > 0)
        {
            task.Add("thought", task.Thought);
        }

        return new JsonObject { ["ok"] = true };
    }

    /// <summary>The tool <c>ample_answer</c>: the answer to a question, as a title, a short answer and the details (markdown) that the user can unfold. Delivered as fields, so nothing has to be cut out of free text.</summary>
    public static JsonObject DeliverAnswer(string token, JsonElement body)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        static string Field(JsonElement b, string name, int max)
        {
            string value = b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;
            return value.Length > max ? value[..max] : value;
        }

        string answer = Field(body, "answer", 1200);
        if (answer.Length == 0)
        {
            throw new AmpleException("AMP5458", "ample_answer needs the answer: a short text (at most three sentences). Optional: title (3 to 6 words) and details (markdown, for the part the user unfolds).");
        }

        task.AnswerTitle = Field(body, "title", 80);
        task.AnswerText = answer;
        task.AnswerMore = Field(body, "details", 20000);
        task.AnswerAction = Field(body, "action", 300);
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("info", "Delivered the answer" + (task.AnswerMore.Length > 0 ? " with details." : "."));
        return new JsonObject { ["ok"] = true, ["note"] = "The answer is delivered to the user. Do not repeat it in your final message." };
    }

    /// <summary>The AI offers alternatives (variants) of one thing, all made in the same proposal: the user compares them, hears each one and keeps one.</summary>
    public JsonObject OfferVariants(string token, JsonElement body)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Status is not ("running" or "waiting-approval") || body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("variants", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new AmpleException("AMP5458", "Variants can only be offered during an active task, as a list of 2 to 6 variants.");
        }

        static string Text(JsonElement e, string name, int max)
        {
            string value = e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;
            return value.Length > max ? value[..max] : value;
        }

        List<AiVariant> made = [];
        foreach (JsonElement item in list.EnumerateArray())
        {
            string name = Text(item, "name", 60);
            List<string> blocks = item.TryGetProperty("blocks", out JsonElement bl) && bl.ValueKind == JsonValueKind.Array ? [.. bl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal)] : [];
            if (name.Length == 0 || blocks.Count == 0)
            {
                throw new AmpleException("AMP5458", "Every variant needs a name and the names of the blocks that belong to it.");
            }

            string listen = Text(item, "listen", 120);
            made.Add(new AiVariant(name, Text(item, "summary", 240), blocks, listen.Length > 0 ? listen : blocks[^1]));
        }

        if (made.Count is < 2 or > 6)
        {
            throw new AmpleException("AMP5458", "Offer 2 to 6 variants.");
        }

        task.Variants = made;
        task.Add("info", $"Offered {made.Count} variants: {string.Join(", ", made.Select(v => v.Name))}.");
        sessionStore.SaveTask(task);
        return new JsonObject { ["offered"] = made.Count, ["note"] = "The user sees the variants side by side, can listen to each one and keeps one. Build and check the proposal as usual; beyond one short sentence do not describe the variants again in words." };
    }

    public JsonObject SkipBuild(string token, string reason)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string clean = reason.Trim();
        if (clean.Length == 0 || clean.Length > 240 || task.Status is not ("running" or "waiting-approval"))
        {
            throw new AmpleException("AMP5458", "A build can only be skipped during an active task, with a reason of 1 to 240 characters.");
        }

        task.SkipBuildRequested = true;
        task.SkipBuildReason = clean;
        task.BuildOnly = null;
        task.BuildStatus = "skipped";
        task.BuildMessage = "Build not run: " + clean;
        task.Add("info", task.BuildMessage);
        sessionStore.SaveTask(task);
        return new JsonObject { ["skipped"] = true, ["reason"] = clean, ["note"] = "AMPLE will validate the source but will not build or render this proposal. The user can accept it after validation." };
    }

    public async Task<JsonObject> RequestAssetAsync(string token, string path, string reason, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path);
        string full = ProjectPath.Resolve(s.Root, rel);
        if (rel.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new AmpleException("AMP5458", $"{rel} is not a file of the project that can be shared.");
        }

        long size = new FileInfo(full).Length;
        if (size > 512L * 1024 * 1024)
        {
            throw new AmpleException("AMP5458", $"{rel} is larger than 512 MB and cannot be shared with a task.");
        }

        string decision = "accept";
        if (task.Mode != "unsafe")
        {
            AiApproval ask = new() { Kind = "asset", Summary = $"{rel} ({(size >= 1_000_000 ? (size / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : (size / 1000 + 1).ToString(CultureInfo.InvariantCulture) + " kB")})", Detail = reason };
            task.Approvals[ask.Id] = ask;
            task.Status = "waiting-approval";
            task.Add("approval", $"Codex asks to read the asset {ask.Summary}: {reason}");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
            try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
            ask.Answered = decision;
            task.Status = "running";
        }

        if (decision is not ("accept" or "acceptForSession"))
        {
            task.Add("approval", "Denied: the asset is not shared.");
            return new JsonObject { ["allowed"] = false, ["note"] = "The user did not allow Codex to read this asset. Continue without it." };
        }

        string target = Path.Combine(task.Workspace, "assets", rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target) || new FileInfo(target).Length != size)
        {
            File.Copy(full, target, true);
        }

        task.Add("file", $"AMPLE copied {rel} to assets/{rel} for Codex to read.");
        return new JsonObject { ["allowed"] = true, ["path"] = "assets/" + rel, ["size"] = size, ["note"] = "AMPLE copied the asset into your working folder. Read it there; it is not part of ./project and changes to it are ignored." };
    }

    // The songs and modules of the project (not .ample), with the text of the open song of the task replaced by the text of the proposal when it is given.
    private List<(string Path, string Text)> RefactorSources(string? songPath = null, string? songText = null)
    {
        List<(string Path, string Text)> sources = [];
        foreach (string file in Directory.EnumerateFiles(s.Root, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".amplet", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".module", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(s.Root, file).Replace('\\', '/');
            if (relative.StartsWith(ProjectLayout.AmpleFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
            sources.Add((relative, songPath is not null && songText is not null && relative == songPath ? songText : File.ReadAllText(file)));
        }

        return sources;
    }

    /// <summary>
    /// Codex proposes moving a project file or folder (audio, a module, a song, a whole folder). Nothing is moved in the temporary copy: the move is made at Accept, after the file changes,
    /// with every path in the songs and modules that points at it following. With confirm "ask" the owner is asked right now and Codex waits for the answer; with "propose" the move is
    /// listed with the proposal and the owner ticks it at Accept.
    /// </summary>
    public async Task<JsonObject> ProposeMoveAsync(string token, JsonElement body, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Request.Kind == "explain") throw new AmpleException("AMP5458", "An explanation task cannot propose moves.");
        string from = AiContext.Norm(body.TryGetProperty("source", out JsonElement sourceValue) ? sourceValue.GetString() ?? string.Empty : string.Empty);
        string to = AiContext.Norm(body.TryGetProperty("destination", out JsonElement destinationValue) ? destinationValue.GetString() ?? string.Empty : string.Empty);
        string confirm = body.TryGetProperty("confirm", out JsonElement confirmValue) && confirmValue.GetString() == "ask" ? "ask" : "propose";
        string reason = body.TryGetProperty("reason", out JsonElement reasonValue) ? (reasonValue.GetString() ?? string.Empty).Trim() : string.Empty;
        if (from.Length == 0 || to.Length == 0 || from == to) throw new AmpleException("AMP5458", "A move needs a source and a different destination, both project-relative.");
        if (from.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase) || to.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Internal project files cannot be moved.");
        string fullFrom = ProjectPath.Resolve(s.Root, from), fullTo = ProjectPath.Resolve(s.Root, to);
        bool isDir = Directory.Exists(fullFrom);
        if (!isDir && !File.Exists(fullFrom)) throw new AmpleException("AMP5458", $"{from} does not exist in the project.");
        if (File.Exists(fullTo) || Directory.Exists(fullTo)) throw new AmpleException("AMP5458", $"{to} already exists; a move never overwrites.");
        if (isDir && to.StartsWith(from + "/", StringComparison.Ordinal)) throw new AmpleException("AMP5458", "A folder cannot be moved into itself.");
        if (task.Request.Song == from || task.Request.Song.StartsWith(from + "/", StringComparison.Ordinal)) throw new AmpleException("AMP5458", "The song of this task cannot be moved by the task itself.");
        if (task.Moves.Any(m => m.From == from || m.To == to)) throw new AmpleException("AMP5458", "This move or its destination is already proposed.");
        RefactorPlan plan = ProjectRefactor.Plan(RefactorSources(), from, to, isDir);
        AiMove move = new() { From = from, To = to, IsDir = isDir, References = plan.Edits.Count, Mode = confirm, Reason = reason };
        if (confirm == "ask")
        {
            string decision = "accept";
            if (task.Mode != "unsafe")
            {
                AiApproval ask = new() { Kind = "move", Summary = $"{from}  →  {to}   ({(move.References == 0 ? "nothing refers to it" : move.References + " line(s) in songs and modules will be updated")})", Detail = reason };
                task.Approvals[ask.Id] = ask;
                task.Status = "waiting-approval";
                task.Add("approval", $"Codex asks to move {from} to {to}" + (reason.Length > 0 ? ": " + reason : "."));
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
                try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
                ask.Answered = decision;
                task.Status = "running";
            }

            if (decision is not ("accept" or "acceptForSession"))
            {
                task.Add("approval", $"Denied: {from} stays where it is.");
                return new JsonObject { ["allowed"] = false, ["note"] = "The owner did not approve this move. Go on without it, or propose it again later in the proposal if it is still needed." };
            }

            move.Approved = true;
        }

        task.Moves.Add(move);
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("move", $"move {from} → {to} ({move.References} reference(s) will follow){(confirm == "ask" ? ", approved" : ", listed with the proposal")}");
        sessionStore.SaveTask(task);
        return new JsonObject
        {
            ["allowed"] = true,
            ["id"] = move.Id,
            ["references"] = move.References,
            ["note"] = confirm == "ask"
                ? "The owner approved the move. It is made when the proposal is accepted, with the references following; the temporary copy is not changed, so do not look for the file at the new place yet."
                : "The move is listed with the proposal and the owner ticks it at Accept. The temporary copy is not changed, so keep using the old path in your own edits.",
        };
    }

    /// <summary>Makes the approved and the ticked moves of an accepted proposal, one by one, after its file changes; returns the text of the open song when a reference in it followed.</summary>
    private async Task<string?> ApplyMovesAsync(AiTask task, IEnumerable<string>? ticked, string? songText, CancellationToken ct)
    {
        string[] chosen = [.. (ticked ?? [])];
        foreach (AiMove move in task.Moves.Where(m => !m.Applied && (m.Approved || chosen.Contains(m.Id))).ToList())
        {
            string fullFrom = ProjectPath.Resolve(s.Root, move.From), fullTo = ProjectPath.Resolve(s.Root, move.To);
            if ((!File.Exists(fullFrom) && !Directory.Exists(fullFrom)) || File.Exists(fullTo) || Directory.Exists(fullTo))
            {
                task.Add("info", $"Move of {move.From} was skipped: the source is gone or the destination exists now.");
                continue;
            }

            RefactorPlan plan = ProjectRefactor.Plan(RefactorSources(task.Request.Song, songText), move.From, move.To, move.IsDir);
            string? unsaved = plan.Texts.Keys.FirstOrDefault(path => path != task.Request.Song && s.IsDocDirty(path));
            if (unsaved is not null)
            {
                task.Add("info", $"Move of {move.From} was skipped: {unsaved} has changes that are not saved, so its references could not follow. Save or revert it and propose the move again.");
                continue;
            }

            string parent = move.To.Contains('/', StringComparison.Ordinal) ? move.To[..move.To.LastIndexOf('/')] : string.Empty;
            if (parent.Length > 0 && !Directory.Exists(ProjectPath.Resolve(s.Root, parent))) await s.Bus.ExecuteAsync(new CreateFolderCommand(parent), ct).ConfigureAwait(false);
            await s.Bus.ExecuteAsync(new MoveProjectPathCommand(move.From, move.To, move.IsDir), ct).ConfigureAwait(false);
            foreach ((string path, string text) in plan.Texts)
            {
                if (path == task.Request.Song) { songText = text; continue; }
                string target = ProjectRefactor.PathAfterMove(path, move.From, move.To, move.IsDir);
                await s.Bus.ExecuteAsync(new WriteFileCommand(target, text), ct).ConfigureAwait(false);
                s.ForgetDoc(path);
                s.ForgetDoc(target);
            }

            move.Applied = true;
            task.Add("info", $"Moved {move.From} to {move.To}; {plan.Edits.Count} reference(s) updated.");
        }

        return songText;
    }

    /// <summary>Puts the moves of an accepted proposal back (newest first), the references following again; the open song is restored from its text before the accept.</summary>
    private async Task UndoMovesAsync(AiTask task, CancellationToken ct)
    {
        foreach (AiMove move in task.Moves.Where(m => m.Applied).Reverse().ToList())
        {
            string fullFrom = ProjectPath.Resolve(s.Root, move.From), fullTo = ProjectPath.Resolve(s.Root, move.To);
            if ((!File.Exists(fullTo) && !Directory.Exists(fullTo)) || File.Exists(fullFrom) || Directory.Exists(fullFrom))
            {
                task.Add("info", $"The move of {move.From} could not be undone: {move.To} is gone or {move.From} exists again.");
                continue;
            }

            RefactorPlan plan = ProjectRefactor.Plan(RefactorSources(), move.To, move.From, move.IsDir);
            await s.Bus.ExecuteAsync(new MoveProjectPathCommand(move.To, move.From, move.IsDir), ct).ConfigureAwait(false);
            foreach ((string path, string text) in plan.Texts)
            {
                if (path == task.Request.Song) continue;
                string target = ProjectRefactor.PathAfterMove(path, move.To, move.From, move.IsDir);
                await s.Bus.ExecuteAsync(new WriteFileCommand(target, text), ct).ConfigureAwait(false);
                s.ForgetDoc(path);
                s.ForgetDoc(target);
            }

            move.Applied = false;
        }
    }

    private static readonly string[] FetchTypes = ["image/", "text/", "audio/", "application/json", "application/xml", "application/pdf"];

    /// <summary>
    /// Codex has no network of its own (its sandbox is closed on purpose), so a link the user gave is fetched by AMPLE: a link that is in the request or in a message of the user is
    /// downloaded at once, any other link needs the user's approval. Only web addresses of the public internet (never this computer or the local network), images, text, audio, JSON, XML and PDF,
    /// at most 10 MB; the file lands in assets/web of the temporary copy and is never part of the proposal.
    /// </summary>
    public static async Task<JsonObject> FetchUrlAsync(string token, string url, string reason, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AmpleException("AMP5458", "Only an http or https address without a password in it can be fetched.");
        }

        bool given = task.Request.Prompt.Contains(url.Trim(), StringComparison.OrdinalIgnoreCase) || task.Log.Any(l => l.Kind == "user" && l.Text.Contains(url.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!given && task.Mode != "unsafe")
        {
            AiApproval ask = new() { Kind = "download", Summary = uri.ToString() };
            task.Approvals[ask.Id] = ask;
            task.Status = "waiting-approval";
            task.Add("approval", $"Codex asks to download {uri}: {reason}");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
            string decision;
            try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
            ask.Answered = decision;
            task.Status = "running";
            if (decision is not ("accept" or "acceptForSession"))
            {
                task.Add("approval", "Denied: the address is not downloaded.");
                return new JsonObject { ["allowed"] = false, ["note"] = "The user did not allow this download. Continue without it." };
            }
        }

        using SocketsHttpHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 4, ConnectCallback = ConnectPublicOnlyAsync, ConnectTimeout = TimeSpan.FromSeconds(15) };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AMPLE/1.0 (+AI assistant download for the user)");
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}." };
            }

            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (!FetchTypes.Any(x => type.StartsWith(x, StringComparison.Ordinal)))
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = $"The content type '{type}' is not one that can be fetched (images, text, audio, JSON, XML, PDF)." };
            }

            if (response.Content.Headers.ContentLength is > 10_000_000)
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The file is larger than 10 MB." };
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > 10_000_000)
                {
                    return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The file is larger than 10 MB." };
                }
            }

            string name = new string([.. Path.GetFileName(uri.AbsolutePath.TrimEnd('/')).Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')]);
            string extension = type switch { "image/svg+xml" => ".svg", "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "application/json" => ".json", "application/pdf" => ".pdf", _ => Path.GetExtension(name) };
            name = Path.GetFileNameWithoutExtension(name);
            name = (name.Length == 0 ? "download" : name[..Math.Min(name.Length, 60)]) + (extension.Length > 0 && extension.Length <= 6 ? extension : string.Empty);
            string target = Path.Combine(task.Workspace, "assets", "web", name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, buffer.ToArray(), ct).ConfigureAwait(false);
            task.Add("file", $"AMPLE downloaded {uri} ({buffer.Length / 1000 + 1} kB) to assets/web/{name} for Codex.");
            JsonObject result = new() { ["allowed"] = true, ["ok"] = true, ["path"] = "assets/web/" + name, ["contentType"] = type, ["size"] = buffer.Length, ["note"] = "AMPLE downloaded it for you (you have no network). The file is in your working folder; it is not part of ./project." };
            if (type == "application/pdf")
            {
                using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(buffer.ToArray());
                StringBuilder extracted = new();
                int pages = Math.Min(pdf.NumberOfPages, 20);
                int extractedPages = 0;
                for (int page = 1; page <= pages && extracted.Length < 60000; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    extracted.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(page)));
                    extractedPages++;
                }
                result["text"] = extracted.ToString(0, Math.Min(extracted.Length, 60000));
                result["pages"] = pdf.NumberOfPages;
                result["extractedPages"] = extractedPages;
                result["note"] = "PDF text extracted by AMPLE; no Python or browser required. Limited to 20 pages/60000 characters. Scanned pages, notation and images require visual interpretation; no OCR is performed.";
            }
            if (type.StartsWith("text/", StringComparison.Ordinal) || type is "image/svg+xml" or "application/json" or "application/xml")
            {
                string text = Encoding.UTF8.GetString(buffer.ToArray());
                result["text"] = text.Length > 60000 ? text[..60000] + "\n… (cut)" : text;
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The address could not be fetched: " + ex.Message };
        }
    }

    private static readonly string[] PackContentTypes = ["application/zip", "application/x-zip-compressed", "application/octet-stream"];

    /// <summary>Finds the folder of an extracted archive that holds <c>pack.json</c>: the archive root, or the one folder most zip tools wrap an archive in. Null when neither has one.</summary>
    internal static string? FindPackFolder(string extractedRoot) =>
        File.Exists(Path.Combine(extractedRoot, "pack.json")) ? extractedRoot
            : Directory.GetDirectories(extractedRoot).FirstOrDefault(d => File.Exists(Path.Combine(d, "pack.json")));

    /// <summary>
    /// Downloads a sample-pack archive (a .zip with a pack.json inside) and installs it into the user's library, the same way "Link a folder" or a manual
    /// pack install would: copied into the library home under packs/&lt;name&gt;@&lt;version&gt;, never into the task's own workspace or the project.
    /// Always needs the user's approval, even when the link was in the request (installing into the shared library outlives this one task).
    /// Public web addresses only, at most 200 MB, must contain a valid pack.json; nothing is left behind in the library when any step fails.
    /// </summary>
    public async Task<JsonObject> InstallPackAsync(string token, string url, string reason, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AmpleException("AMP5458", "Only an http or https address without a password in it can be fetched.");
        }

        if (task.Mode != "unsafe")
        {
            AiApproval ask = new() { Kind = "installPack", Summary = uri.ToString(), Detail = reason };
            task.Approvals[ask.Id] = ask;
            task.Status = "waiting-approval";
            task.Add("approval", $"Codex asks to install a sample pack from {uri} into your library: {reason}");
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token);
            string decision;
            try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), limit.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "decline"; }
            ask.Answered = decision;
            task.Status = "running";
            if (decision is not ("accept" or "acceptForSession"))
            {
                task.Add("approval", "Denied: the pack is not installed.");
                return new JsonObject { ["allowed"] = false, ["note"] = "The user did not allow this pack install. Continue without it." };
            }
        }

        string staging = Path.Combine(Path.GetTempPath(), "ample-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            using SocketsHttpHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 4, ConnectCallback = ConnectPublicOnlyAsync, ConnectTimeout = TimeSpan.FromSeconds(15) };
            using HttpClient http = new(handler) { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AMPLE/1.0 (+AI assistant pack install for the user)");
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}." };
            }

            string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (!PackContentTypes.Contains(type) && !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = $"The content type '{type}' does not look like a .zip pack archive." };
            }

            if (response.Content.Headers.ContentLength is > 200_000_000)
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The archive is larger than 200 MB." };
            }

            Directory.CreateDirectory(staging);
            string zipPath = Path.Combine(staging, "pack.zip");
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (FileStream file = File.Create(zipPath))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
                if (file.Length > 200_000_000)
                {
                    return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The archive is larger than 200 MB." };
                }
            }

            string extracted = Path.Combine(staging, "extracted");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extracted);
            string? packFolder = FindPackFolder(extracted);
            if (packFolder is null)
            {
                return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The archive has no pack.json at its root (or inside one wrapping folder), so it is not an AMPLE pack." };
            }

            Ample.Storage.Library.Mount mount = s.Library.InstallPack(packFolder, link: false);
            task.Add("file", $"AMPLE installed the pack {mount.Name} from {uri} into the library.");
            return new JsonObject { ["allowed"] = true, ["ok"] = true, ["name"] = mount.Name, ["note"] = "Installed into the library (packs/<name>@<version>), available to every project. Use the name in a sampleset/file parameter the same way as a project folder." };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or AmpleException)
        {
            return new JsonObject { ["allowed"] = true, ["ok"] = false, ["note"] = "The pack could not be installed: " + ex.Message };
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { /* best effort cleanup */ }
        }
    }

    /// <summary>Connects only to addresses of the public internet, whatever the name says (also after a redirect).</summary>
    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        System.Net.IPAddress? chosen = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("That address is not on the public internet, so it is not fetched.");
        System.Net.Sockets.Socket socket = new(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new System.Net.IPEndPoint(chosen, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any))
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return !(b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224 || b[0] == 0);
        }

        return (b[0] & 0xFE) != 0xFC;   // fc00::/7 is the unique local range
    }

    /// <summary>The Codex home of the project identity: machine local, outside the project, keyed by the identity of the project.</summary>
    private string DeviceHome => Path.Combine(AmpleHome.DefaultPath(), "codex-homes", s.Manifest.Id.ToString("D"));

    /// <summary>The Codex home of Koda, the helper: its own login, so Koda and the assistant can work at the same time (a Codex login works for one process at a time).</summary>
    private string KodaHome => Path.Combine(AmpleHome.DefaultPath(), "codex-homes", s.Manifest.Id.ToString("D") + "-koda");

    /// <summary>Whether Koda has a login of its own. Without one Koda uses the lane of the assistant and waits for it.</summary>
    private bool KodaLinked => File.Exists(Path.Combine(KodaHome, "auth.json"));

    /// <summary>The Codex home a task runs in: Koda's own when Koda has one, else the home of the project identity (null is the Codex of the server's user).</summary>
    private string? HomeOf(AiTask task) => task.Request.Kind == "koda" && KodaLinked ? KodaHome : task.Identity == "codex-device" ? DeviceHome : null;

    private string Work => Path.Combine(s.Root, ProjectLayout.AmpleFolder, "ai");

    /// <summary>The reasoning effort for a turn: the one chosen for this request, else the one of the server settings, else Codex's own default (null).</summary>
    private static string? EffortOf(AiTask task, AiPolicy policy) => task.EffortNow is { Length: > 0 } now ? (string.Equals(now, "default", StringComparison.OrdinalIgnoreCase) ? null : now) : task.Request.Effort is { Length: > 0 } own ? (string.Equals(own, "default", StringComparison.OrdinalIgnoreCase) ? null : own) : policy.EffortToUse;

    /// <summary>The AI settings of the project, read from <c>project.json</c>.</summary>
    public AiAssistantSettings Settings() => ProjectFile.Parse(File.ReadAllText(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile))).Settings.Ai;

    // ---- the status ----

    /// <summary>Where Codex stands: what the server allows, what the project chose, whether Codex can run and, when asked, whether it is signed in.</summary>
    private static readonly Dictionary<string, (JsonArray Models, DateTimeOffset At)> modelsCache = [];

    private static long modelsGeneration;

    /// <summary>
    /// Forgets every catalog; a fetch that started before the clear (a sign-in finished meanwhile) does not store its result.
    /// </summary>
    private static void ClearModels() { lock (modelsCache) { modelsCache.Clear(); modelsGeneration++; } }

    /// <summary>The models Codex offers and the current account's usage limits.</summary>
    public async Task<JsonObject> ModelsAsync(CancellationToken ct, bool koda = false)
    {
        AiPolicy policy = AiPolicy.Read();
        AiAssistantSettings ai = Settings();
        string? home = koda && KodaLinked ? KodaHome : ai.Assistant == "codex-device" ? DeviceHome : null;
        string cacheKey = policy.CodexPath + "|" + (home ?? "server-account");
        JsonObject usage = new();
        JsonArray? cached = null;
        long generation;
        lock (modelsCache)
        {
            generation = modelsGeneration;
            bool fresh = modelsCache.TryGetValue(cacheKey, out (JsonArray Models, DateTimeOffset At) e)
                && DateTimeOffset.UtcNow - e.At <= TimeSpan.FromMinutes(10);
            if (fresh) { cached = e.Models; }
        }
        if (cached is null)
        {
            try
            {
                await using CodexRpc rpc = CodexRpc.Start(policy.CodexPath, home, s.Root, []);
                await rpc.InitializeAsync(ct).ConfigureAwait(false);
                JsonNode? list = await rpc.RequestAsync("model/list", new JsonObject(), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                JsonArray offered = list?["data"] as JsonArray ?? [];
                cached = [.. offered.Where(m => m?["hidden"]?.GetValue<bool>() != true).Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m!["model"]?.GetValue<string>() ?? m["id"]?.GetValue<string>(),
                    ["name"] = m["displayName"]?.GetValue<string>(),
                    ["description"] = m["description"]?.GetValue<string>(),
                    ["efforts"] = new JsonArray([.. (m["supportedReasoningEfforts"] as JsonArray ?? []).Select(e => (JsonNode?)e?["reasoningEffort"]?.GetValue<string>() ?? string.Empty)]),
                    ["defaultEffort"] = m["defaultReasoningEffort"]?.GetValue<string>(),
                    ["isDefault"] = m["isDefault"]?.GetValue<bool>() ?? false,
                })];
                lock (modelsCache) { if (generation == modelsGeneration) { modelsCache[cacheKey] = (cached, DateTimeOffset.UtcNow); } }
            }
            catch (Exception ex) when (ex is AmpleException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
            {
                return new JsonObject { ["models"] = new JsonArray(), ["error"] = "The model list of Codex could not be read: " + ex.Message, ["default"] = AiPolicy.DefaultModel };
            }
        }

        try
        {
            await using CodexRpc rpc = CodexRpc.Start(policy.CodexPath, home, s.Root, []);
            await rpc.InitializeAsync(ct).ConfigureAwait(false);
            JsonNode? account = await rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            usage["plan"] = account?["account"]?["planType"]?.DeepClone();
            if (string.Equals(account?["account"]?["type"]?.GetValue<string>(), "chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                JsonNode? limits = await rpc.RequestAsync("account/rateLimits/read", new JsonObject(), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                usage["rateLimits"] = limits?["rateLimits"]?.DeepClone();
                usage["rateLimitsByLimitId"] = limits?["rateLimitsByLimitId"]?.DeepClone();
            }
        }
        catch (Exception ex) when (ex is AmpleException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            usage["error"] = ex.Message;
        }

        return new JsonObject { ["models"] = cached!.DeepClone(), ["default"] = AiPolicy.DefaultModel, ["usage"] = usage };
    }

    public async Task<JsonObject> StatusAsync(bool checkAuth, CancellationToken ct)
    {
        AiPolicy policy = AiPolicy.Read();
        AiAssistantSettings ai = Settings();
        string? version = CodexVersion(policy.CodexPath);
        List<string> problems = [];
        if (ai.Assistant != "none")
        {
            if (!policy.CodexEnabled)
            {
                problems.Add("Codex is turned off on this server.");
            }
            else if (!policy.AllowsAssistant(ai.Assistant))
            {
                problems.Add(ai.Assistant == "codex-device" ? "This server no longer allows project Device ID identities." : "This server no longer allows the Codex account of the server user.");
            }

            if (!policy.AllowsMode(ai.Mode))
            {
                problems.Add($"This server no longer allows the execution mode {ai.Mode}.");
            }

            if (version is null)
            {
                problems.Add("Codex is not installed (or not found): install it with 'npm install -g @openai/codex'.");
            }

            if (ai.Assistant == "codex-device" && ai.LinkedAccount is null)
            {
                problems.Add("The Codex identity of this project is not linked yet.");
            }
        }

        List<string> limitations =
        [
            ai.Assistant == "codex-device"
                ? "Project Device ID: a Codex home of its own for this project (CODEX_HOME). Its login, configuration and AGENTS.md are separate from the Codex of the user; nothing is inherited and the user's login is not touched."
                : "Server account: this uses the Codex login of the user the server runs as. Its configuration and its global AGENTS.md live in the same Codex home and cannot be separated from the login, so they are read as they are.",
        ];
        if (ai.Assistant == "codex-server" && !policy.AllowInheritance)
        {
            limitations.Add("The server asks that the Codex configuration of the system user is not inherited. Codex has no switch for that for the global AGENTS.md of a shared Codex home: AMPLE only switches off the AGENTS.md files of the project folder. Use a project Device ID for real isolation.");
        }

        limitations.Add("Codex works in a temporary copy of the files in scope and its sandbox is that copy (a request to explain something is read-only). Unsafe / Auto-all only stops asking: what the sandbox allows runs at once, what it does not allow is refused." + (policy.UnsafeNoSandbox ? " This server has also switched the sandbox off for Unsafe / Auto-all: there Codex can touch anything the server user can." : string.Empty));
        JsonObject o = new()
        {
            ["policy"] = policy.ToJson(),
            ["project"] = new JsonObject { ["assistant"] = ai.Assistant, ["mode"] = ai.Mode, ["acknowledged"] = ai.Acknowledged is not null, ["unsafeAcknowledged"] = ai.UnsafeAcknowledged is not null, ["linkedAccount"] = ai.LinkedAccount, ["linkedAt"] = ai.LinkedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
            ["codex"] = new JsonObject { ["found"] = version is not null, ["version"] = version },
            ["assistantsAllowed"] = new JsonArray([.. AiAssistantSettings.Assistants.Where(policy.AllowsAssistant).Select(x => (JsonNode)x)]),
            ["modesAllowed"] = new JsonArray([.. AiAssistantSettings.Modes.Where(policy.AllowsMode).Select(x => (JsonNode)x)]),
            ["available"] = ai.Assistant != "none" && problems.Count == 0,
            ["problems"] = new JsonArray([.. problems.Select(x => (JsonNode)x)]),
            ["limitations"] = new JsonArray([.. limitations.Select(x => (JsonNode)x)]),
            ["identity"] = IdentityLabel(ai),
            ["modeLabel"] = ModeLabel(ai.Mode),
            ["device"] = DeviceJson(),
        };
        if (checkAuth && ai.Assistant != "none" && version is not null && problems.Count == 0)
        {
            o["auth"] = await AuthAsync(ai, policy, ct).ConfigureAwait(false);
        }

        return o;
    }

    private static string IdentityLabel(AiAssistantSettings ai) => ai.Assistant switch { "codex-server" => "Codex · Server account", "codex-device" => "Codex · Project Device ID", _ => "No AI assistant" };

    private static string ModeLabel(string mode) => mode switch { "manual" => "Manual", "unsafe" => "Unsafe / Auto-all", _ => "Auto" };

    private async Task<JsonObject> AuthAsync(AiAssistantSettings ai, AiPolicy policy, CancellationToken ct)
    {
        string? home = ai.Assistant == "codex-device" ? DeviceHome : null;
        if (home is not null && !Directory.Exists(home))
        {
            return new JsonObject { ["state"] = "not-linked", ["detail"] = "No Codex login exists for this project yet." };
        }

        try
        {
            await using CodexRpc rpc = CodexRpc.Start(policy.CodexPath, home, s.Root, []);
            await rpc.InitializeAsync(ct).ConfigureAwait(false);
            JsonNode? account = await rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            JsonNode? a = account?["account"];
            return a is null
                ? new JsonObject { ["state"] = "not-linked", ["detail"] = "Codex is not signed in." }
                : new JsonObject { ["state"] = "linked", ["detail"] = (a["email"]?.GetValue<string>() ?? a["type"]?.GetValue<string>() ?? "signed in"), ["plan"] = a["planType"]?.ToString() };
        }
        catch (Exception ex) when (ex is AmpleException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            return new JsonObject { ["state"] = "unknown", ["detail"] = ex.Message };
        }
    }

    /// <summary>The version of the installed Codex, or null when it cannot be run.</summary>
    public static string? CodexVersion(string? binary)
    {
        // the answer is kept per Codex program: another path in the server policy is another program
        if (versionCache is not null && versionBinary == binary)
        {
            return versionCache.Length == 0 ? null : versionCache;
        }

        versionBinary = binary;
        versionCache = null;

        try
        {
            ProcessStartInfo info = CodexRpc.Command(binary, ["--version"]);
            info.RedirectStandardOutput = info.RedirectStandardError = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using Process process = Process.Start(info)!;
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);
            versionCache = process.ExitCode == 0 && text.Length > 0 ? text : string.Empty;
        }
        catch (Exception ex) when (ex is AmpleException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            versionCache = string.Empty;
        }

        return versionCache.Length == 0 ? null : versionCache;
    }

    // ---- the settings of the project ----

    /// <summary>Changes the AI settings of the project; what the server does not allow cannot be chosen, and nothing falls back to something else.</summary>
    public async Task<JsonObject> SaveSettingsAsync(JsonElement b, CancellationToken ct)
    {
        AiPolicy policy = AiPolicy.Read();
        ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), ct).ConfigureAwait(false));
        AiAssistantSettings old = m.Settings.Ai;
        string assistant = b.TryGetProperty("assistant", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : old.Assistant;
        string mode = b.TryGetProperty("mode", out JsonElement md) && md.ValueKind == JsonValueKind.String ? md.GetString()! : old.Mode;
        if (!AiAssistantSettings.Assistants.Contains(assistant) || !AiAssistantSettings.Modes.Contains(mode))
        {
            throw new AmpleException("AMP5285", "Unknown assistant or execution mode.");
        }

        if (assistant != old.Assistant && !policy.AllowsAssistant(assistant))
        {
            throw new AmpleException("AMP5450", "This server does not allow that Codex identity. It is not replaced by another one.");
        }

        if (mode != old.Mode && !policy.AllowsMode(mode))
        {
            throw new AmpleException("AMP5450", "This server does not allow that execution mode.");
        }

        DateTimeOffset now = s.Clock.UtcNow;
        DateTimeOffset? acknowledged = old.Acknowledged, unsafeAck = old.UnsafeAcknowledged;
        bool ackRisk = b.TryGetProperty("acknowledgeRisk", out JsonElement ar) && ar.ValueKind == JsonValueKind.True;
        bool ackUnsafe = b.TryGetProperty("acknowledgeUnsafe", out JsonElement au) && au.ValueKind == JsonValueKind.True;
        if (assistant != "none" && acknowledged is null)
        {
            acknowledged = ackRisk ? now : throw new AmpleException("AMP5451", "The risks of an AI coding agent must be acknowledged first.");
        }

        if (assistant != "none" && mode == "unsafe" && unsafeAck is null)
        {
            unsafeAck = ackUnsafe ? now : throw new AmpleException("AMP5451", "The extra risks of Unsafe / Auto-all must be acknowledged first.");
        }

        // the Codex identity of the project stays linked while the project uses another assistant for a while (only Unlink removes it)
        string? linked = old.LinkedAccount;
        DateTimeOffset? linkedAt = old.LinkedAt;
        if (assistant == "codex-device" && linked is null && Directory.Exists(DeviceHome))
        {
            // a login that is still in the Codex home of this project is taken over instead of asking for the device flow again
            JsonObject auth = await AuthAsync(new AiAssistantSettings(assistant, mode, acknowledged, unsafeAck, null, null), AiPolicy.Read(), ct).ConfigureAwait(false);
            if (auth["state"]?.GetValue<string>() == "linked")
            {
                linked = auth["detail"]?.GetValue<string>();
                linkedAt = now;
            }
        }

        AiAssistantSettings next = new(assistant, mode, acknowledged, unsafeAck, linked, linkedAt);
        await s.Bus.ExecuteAsync(new SetSettingsCommand(m.Settings with { Ai = next }), ct).ConfigureAwait(false);
        return await StatusAsync(false, ct).ConfigureAwait(false);
    }

    private async Task SaveLinkAsync(string? account, CancellationToken ct)
    {
        ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), ct).ConfigureAwait(false));
        AiAssistantSettings next = m.Settings.Ai with { LinkedAccount = account, LinkedAt = account is null ? null : s.Clock.UtcNow };
        await s.Bus.ExecuteAsync(new SetSettingsCommand(m.Settings with { Ai = next }), ct).ConfigureAwait(false);
    }

    // ---- linking a Codex identity to the project (the device flow) ----

    private sealed class DeviceLogin
    {
        public CodexRpc Rpc { get; init; } = null!;

        public string UserCode { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string State { get; set; } = "waiting";

        public string? Error { get; set; }

        public string? LoginId { get; set; }
    }

    private const string KodaProfile = """


## You are Koda

You are Koda, a small friendly helper inside AMPLE. The user asked about one block, one field or one piece of code (see TASK.md for the subject). Keep to that subject and what it reads from and feeds.

- Answer in short, friendly sentences (at most four), in the language of the question. Do not use he or she for yourself.
- A question: answer it and change nothing.
- An instruction: make one small draft with ample_edit, in the song named in TASK.md, and say in one plain sentence what you changed. Do not build or render unless the user asked for it.
- Always tell the user what you are doing: start with `ample_thinking` (one short, plain sentence about what you are about to look at or do, in the language of the question), and give every tool call a short `thinking` argument. The user reads these lines while they wait, so write them like a person would (for example "Checking which blocks read from sunsetpan"), never a tool name or a timing.
- A file a block reads (a sequencer's `file`, lyrics, a score) is not in your copy but is in the project: read it with `ample_file` (location `project`, the path as the block names it) before you say you could not open it.
- Never touch anything outside the subject unless the instruction needs it (for example a link to the master).
- Several alternatives (variants, other instruments, beats): make all of them in this one working copy, each as copies of the source blocks (follow a compressor or EQ back to the instrument or sequencer) under new names, leave the original, change a sequencer's sound inside its `sequence { }` or `file` (never as loose song lines), keep every variant ready to use at normal level (never muted or turned down to keep the mix as it was), then call `ample_offer_variants` once.
- A sequencer whose notes are in a `file` (a .ampleseq): its instruments are the `channel` lines at the top of that file. To change an instrument give the .ampleseq as the `path` of ample_edit and use `{"op":"sequence_channel","channel":"violin1","text":"program=42"}` (settings alone change only those settings; a type with settings, like `gm program=42 vol=-6`, replaces the rest of the line; no block). One call with all the channels changes them all. In a song that keeps its sequence inside (`sequence { }`) the same op takes the `block`. When you rotate or swap instruments, read the old programs first and give every channel its new one in the same call. The `lanes` line of the sequencer in the song (`lane1=violin1 ...`) names the CHANNELS of the file: never write an instrument name there that is not a channel (a `contrabass` lane stops the build); rename nothing, change the `program` of the channel instead.
- A sequencer: change only the channel, pattern or row the user asked about, with the edits sequence_row and sequence_channel of ample_edit (never rewrite the whole sequence). Keep every other channel, pattern, clip and row exactly as it is, never write into the row of a channel you were not asked about (a gm or notes row is a melody: leave it), a note of a gm or notes row is bar:beat:length:pitch:velocity with the beat from 0 to 3.99 (the next bar starts with 2:0, never 1:4), so check every row you write; and when you add a channel or a beat, also connect its output (a lane or the block output) the way the other channels are, so it is heard.
- A singing voice from notes (a chant, ooh/aah, a choir of hums): the `delay-llama` block (notes in; `vowel` a letter, 0-100 or a curve; `voice`, `mouth`, `glide`, `unison`, `echo`), or a sequencer channel of type `llama`.
- Pitch bend: a midi-clip has `bend auto(...)` (percent of `bend_range`, 2 by default) and a note can have `;bend=<cents>`; every instrument that plays the notes bends with it. Any audio bends with a `pitch` block whose `semitones` is a curve, `auto(1:0, 4:12)` (or percent with `range`).
- A voice that makes a chord or a synth speak (vocoder, talking robot, Daft Punk): the `vocoder` block, the voice on `modulator` and a rich chord sound (a gm sawtooth lead, strings, a pad) on `carrier`.
- A spoken line cut into words on the beat (a chopped, stuttering vocal): the `chop` block (`slices auto`, `pattern "1 2 3 4 3*2 -"`, `step`, `repeat`), usually followed by a `vocoder`.
- Autotune or pitch correction of a voice: the `autotune` block after ONE voice (`from voice.audio -> audio`), never on a mix or a choir; it always states `method spectral` (or `method psola`). `key` auto, chromatic or a scale (A minor); `speed 0 ms` is the robot sound, 20-200 ms natural; to follow a melody exactly connect `melody.notes -> target`; single notes are fixed with `fixes` (`4.2s-4.7s E4; 12s-13s off`).
- A rewind, pull-up or "scratch back" (the music stops, only a scratch is heard, the music goes on from an earlier place of the same sound): use the `rewind` block, never the `scratch` block (that one only moves the record per step). Its `events` are written in the time of the block, for example `at 6s scratch 2s back 5s; at 14s scratch 1s back 2s` (a negative back skips forward); take the length of the sample from ample_analyze_audio and keep the events inside it. For the most realistic sound connect a real scratch sample to its `fx` input: find one with ample_find_samples, fetch it with ample_fetch_sample, copy it into the project with ample_copy_file (source=workspace/assets/web/<file>, destination samples/<file>; the block cannot read assets/web), bring it in with an import-audio block whose file is that project path and connect that block to `fx`. `smear` (the record kicks into gear after the scratch) and `brake` (it slows down before) are in milliseconds; `hardness` and `blend` soften the cuts. An effect on the scratches alone: rewind.region -> effect.region_in, effect.region -> region-merge.region, rewind.audio -> region-merge.body (the effect gets no audio input).
- A real sample the song needs (a DJ scratch, a vinyl crackle, a foley sound, a riser): call ample_sample_sources, find the sound with ample_find_samples (no browser or sign-in needed), and fetch it yourself with ample_fetch_sample (AMPLE reads the licence and only lets CC0 or public domain through); never invent a source or claim a licence you have not seen, and never buy anything. An effect-based stand-in is allowed but must be called a stand-in.
- A beat, drum pattern or groove: use the beats catalogue (the tool ample_beats, 160+ ready grooves) and never invent a basic pattern from nothing. Search it in small steps (genre, mood, tempo of the song), take a beat that fits, and copy its pattern into the song or the sequence; if the user asks to use the catalogue, always do that, and say which beat you took. A different request must give a different beat, not the same basic one again.
- Sheet music as a PDF or a picture that the user attached (its pages are pictures you can see, in attachments/): read the notes from the pictures yourself and write them into the song (a melody line, chords, or a midi-clip / sequence with note_add and sequence_row; ask nothing, say in one line which assumptions you made, such as the key and the tempo). ample_import_score needs Audiveris (OMR), which is usually not installed: if it answers AMP5464, do not stop there, carry on from the pictures.
- A MIDI or score file the person wants editable, in parts or in segments ("make it editable", "split it", "I want to change the notes"): never copy its notes into the song by hand and never say the notes are too many to embed. Run ample_import_score on it (the file is in the project, or stage a fetched one with ample_copy_file first): it writes a .ampleseq with one channel per part and one pattern per N bars, and that file keeps every note. Then replace the `midi` block (and the `midi-render` that plays it) by a `sequencer` block with `file sequences/<name>.ampleseq` (lanes for the parts to treat separately), connect its output to whatever the old block fed (the master or a mixer), set the project tempo to the tempo the import reports, run ample_check, and say how many parts, notes and bars it has. Keep the original MIDI file in the project.
- Writing notes shorter, with formulas and helpers ("find patterns", "less notes to write", "generate it with a formula"): the sound you calculate must be EXACTLY the same, so work in this order. (1) ample_find_patterns on the clip or on the sequencer channel: it looks for a rhythm that repeats with pitches, velocities and other settings that cycle, and every candidate it gives was already worked out and compared with the original (verified=true). (2) Apply a verified candidate: for a midi-clip put the expression in its notes (set_param), for a sequencer channel write the formula row it gives (a row that starts with =; sequence_row for a sequence kept in the song, or the .ampleseq file). (3) Prove it with ample_resolve_notes (block with compare_block, or expression with compare_expression): replace only when compare.same is true, and keep the original as a temporary clip until then. (4) For structure the finder cannot use (a motif that rises by a step, a motif transposed per bar, an arpeggio from chords) write a helper in the song (ample_docs name=language/14-helpers-types-and-dynamic) and prove it the same way. set(pitch=..., velocity=..., pan=..., ...) writes any setting of a note as a value or a list that cycles over the notes. Never replace notes by a formula you did not prove the same. If nothing shorter exists, say that in plain words (the music is not mathematical, or only a little) and leave the notes alone.
- Real samples first: there is almost never a reason to leave a sound on the built-in synth voice. A `drums` block always gets a `kit` (a sample folder of the project such as samples/drums-full; list the project's samples folders first and take the one that fits the style, never leave `kit` empty, which plays the built-in sound), and a bass, keys, pad, guitar or strings voice is a `gm` block with a SoundFont program (`file pack:musescore-general`, a fitting `program`) or a sample set, not a bare `synth`, `bass` or `piano` block, unless the person asked for a synthetic sound. Before you use a drum voice in a pattern (kick, snare, hat, tom ...) check that the kit has a sample for it (the folder lists its files); a voice the kit lacks gets a `map` or `fallback` kit, or a real sample you find (ample_find_samples, ample_fetch_sample), or another voice. When you change or add a sound, say which real sample set it plays.
- If you could not make what was asked, say exactly which part failed and why in plain words, and leave the song as it was; never report a partial result as if it was the whole.
- A request for variation that follows the time or the clock ("a seed based on the time", "different every minute", "changes per build"): do it. Declare a `dynamic helper` that reads `currentTime()` (the clock capability is on by default), for example `dynamic helper sectionSeed(RandomSource s) -> RandomSource { return s.fork(Floor(currentTime().minute / 4)) }` and give it to the generator as its seed (`seed=sectionSeed(stable("name"))`); a division gives a Float, so wrap it in Floor, Round or Int. Say in your answer that the song is now dynamic (it can sound different per build) and that the Bake clock values button freezes the values of the build the person liked. A fixed `stable("name")` seed is NOT what was asked, and neither is a small humanize pass.
- "Surprise me", "mix things up" or "more structure" asks for a clear, audible change: a new section, an extra layer, a structural helper, a variation per phase. Make one such change (still in a few edits). A rejected earlier proposal means the person did not like that proposal, not that the idea is forbidden: try the idea another way instead of falling back to a safer, smaller edit.
""";

    /// <summary>The language hint for Koda: English, or Dutch with the English term of AMPLE next to the Dutch one (the user is learning the terms).</summary>
    internal static string KodaLanguageText(string language) => language == "nl"
        ? """


## Language: Dutch

Answer in Dutch (Nederlands). AMPLE itself is in English: the first time you use a term of AMPLE (a block type, a parameter, a command such as bake, freeze or humanize), give the Dutch word and the English term of AMPLE in brackets, for example "afzwakken (fade)". Keep block names, parameter names and code exactly as they are in English. Short, friendly sentences.
"""
        : """


## Language: English

Answer in English, whatever language the question is in, unless the user asks for another.
""";

    private DeviceLogin? kodaDevice;

    private JsonObject KodaJson() => new() { ["linked"] = KodaLinked, ["lane"] = KodaLinked ? "own" : "shared", ["login"] = kodaDevice is null ? null : new JsonObject { ["state"] = kodaDevice.State, ["userCode"] = kodaDevice.UserCode, ["verificationUrl"] = kodaDevice.Url, ["error"] = kodaDevice.Error } };

    /// <summary>The state of Koda's lane: its own login (linked) or the shared lane of the assistant, and a device flow that is waiting.</summary>
    public JsonObject KodaStatus() => KodaJson();

    /// <summary>Starts the Codex device flow for Koda's own home: the same ChatGPT account may be linked a second time, which gives Koda a login that does not spoil the assistant's.</summary>
    public async Task<JsonObject> StartKodaLoginAsync(CancellationToken ct)
    {
        AiPolicy policy = AiPolicy.Read();
        if (!policy.CodexEnabled || CodexVersion(policy.CodexPath) is null)
        {
            throw new AmpleException("AMP5453", "Codex is not installed (or not found), or this server does not allow it.");
        }

        if (kodaDevice is { State: "waiting" })
        {
            await CancelKodaLoginAsync().ConfigureAwait(false);
        }

        Directory.CreateDirectory(KodaHome);
        CodexRpc rpc = CodexRpc.Start(policy.CodexPath, KodaHome, s.Root, []);
        DeviceLogin login = new() { Rpc = rpc };
        kodaDevice = login;
        rpc.OnNotification = (method, p) =>
        {
            if (method == "account/login/completed")
            {
                login.State = p["success"]?.GetValue<bool>() == true ? "linked" : "failed";
                ClearModels();
                login.Error = p["error"]?.GetValue<string>();
                _ = Task.Run(async () => { await login.Rpc.DisposeAsync().ConfigureAwait(false); });
            }
        };
        JsonNode? answer;
        try
        {
            await rpc.InitializeAsync(ct).ConfigureAwait(false);
            JsonObject start = new() { ["type"] = "chatgptDeviceCode" };
            answer = await rpc.RequestAsync("account/login/start", start, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (login.State == "waiting")
            {
                login.State = "failed";
                login.Error = "Codex sign-in could not be started: " + ex.Message;
            }

            await rpc.DisposeAsync().ConfigureAwait(false);
            return KodaJson();
        }

        login.UserCode = answer?["userCode"]?.GetValue<string>() ?? string.Empty;
        login.Url = answer?["verificationUrl"]?.GetValue<string>() ?? string.Empty;
        if (login.UserCode.Length == 0 || login.Url.Length == 0)
        {
            login.State = "failed";
            login.Error = "Codex did not return a device code.";
            await rpc.DisposeAsync().ConfigureAwait(false);
        }

        return KodaJson();
    }

    /// <summary>Stops a device flow of Koda that is waiting.</summary>
    public async Task<JsonObject> CancelKodaLoginAsync()
    {
        if (kodaDevice is { State: "waiting" } login)
        {
            login.State = "cancelled";
            await login.Rpc.DisposeAsync().ConfigureAwait(false);
        }

        return KodaJson();
    }

    /// <summary>Removes Koda's own login and home; Koda then uses the lane of the assistant again.</summary>
    public async Task<JsonObject> UnlinkKodaAsync()
    {
        await CancelKodaLoginAsync().ConfigureAwait(false);
        kodaDevice = null;
        ClearModels();
        if (Directory.Exists(KodaHome))
        {
            Directory.Delete(KodaHome, true);
        }

        return KodaJson();
    }

    private JsonObject? DeviceJson() => device is null ? null : new JsonObject { ["state"] = device.State, ["userCode"] = device.UserCode, ["verificationUrl"] = device.Url, ["error"] = device.Error };

    /// <summary>Starts the real Codex device flow in the Codex home of this project and returns the address and code the user has to enter.</summary>
    public async Task<JsonObject> StartDeviceLoginAsync(CancellationToken ct)
    {
        AiAssistantSettings ai = Settings();
        AiPolicy policy = AiPolicy.Read();
        if (ai.Assistant != "codex-device" || !policy.AllowsAssistant("codex-device"))
        {
            throw new AmpleException("AMP5450", "The project is not set to a project Device ID Codex identity, or the server does not allow it.");
        }

        if (device is { State: "waiting" })
        {
            await CancelDeviceLoginAsync().ConfigureAwait(false);
        }

        CodexRpc rpc = CodexRpc.Start(policy.CodexPath, DeviceHome, s.Root, []);
        DeviceLogin login = new() { Rpc = rpc };
        device = login;
        rpc.OnNotification = (method, p) =>
        {
            if (method == "account/login/completed")
            {
                login.State = p["success"]?.GetValue<bool>() == true ? "linked" : "failed";
                ClearModels();
                login.Error = p["error"]?.GetValue<string>();
                _ = Task.Run(async () => { await FinishDeviceAsync(login).ConfigureAwait(false); });
            }
        };
        JsonNode? answer;
        try
        {
            await rpc.InitializeAsync(ct).ConfigureAwait(false);
            JsonObject start = new() { ["type"] = "chatgptDeviceCode" };
            answer = await rpc.RequestAsync("account/login/start", start, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (login.State == "waiting")
            {
                login.State = "failed";
                login.Error = "Codex sign-in could not be started: " + ex.Message;
            }

            await rpc.DisposeAsync().ConfigureAwait(false);
            return DeviceJson()!;
        }

        login.UserCode = answer?["userCode"]?.GetValue<string>() ?? string.Empty;
        login.Url = answer?["verificationUrl"]?.GetValue<string>() ?? string.Empty;
        login.LoginId = answer?["loginId"]?.GetValue<string>();
        if (login.UserCode.Length == 0 || login.Url.Length == 0)
        {
            login.State = "failed";
            login.Error = "Codex did not return a device code.";
            await rpc.DisposeAsync().ConfigureAwait(false);
        }

        return DeviceJson()!;
    }

    private async Task FinishDeviceAsync(DeviceLogin login)
    {
        string label = "linked";
        if (login.State == "linked")
        {
            try
            {
                JsonNode? account = await login.Rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
                label = account?["account"]?["email"]?.GetValue<string>() ?? "ChatGPT account";
            }
            catch (Exception ex) when (ex is AmpleException or TimeoutException)
            {
                label = "ChatGPT account";
            }

            await SaveLinkAsync(label, CancellationToken.None).ConfigureAwait(false);
        }

        await login.Rpc.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The state of the device flow that was started.</summary>
    public JsonObject DeviceStatus() => DeviceJson() ?? new JsonObject { ["state"] = "none" };

    /// <summary>Stops a device flow that is waiting.</summary>
    public async Task CancelDeviceLoginAsync()
    {
        if (device is { State: "waiting" } login)
        {
            login.State = "cancelled";
            await login.Rpc.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Removes the Codex identity of this project: its login and its Codex home. The Codex of the user is not touched.</summary>
    public async Task UnlinkAsync(CancellationToken ct)
    {
        await CancelDeviceLoginAsync().ConfigureAwait(false);
        device = null;
        ClearModels();
        if (Directory.Exists(DeviceHome))
        {
            Directory.Delete(DeviceHome, true);
        }

        await SaveLinkAsync(null, ct).ConfigureAwait(false);
    }

    // ---- a task ----

    /// <summary>Starts a task: prepares the copy Codex works in and runs Codex in the background.</summary>
    public Task<AiTask> StartTaskAsync(AiTaskRequest request, string baseUrl, string sessionId, CancellationToken ct)
    {
        RestoreSavedTasks();
        PruneLeftovers();
        _ = sessionStore.Get(sessionId);
        AiAssistantSettings ai = Settings();
        AiPolicy policy = AiPolicy.Read();
        if (ai.Assistant == "none")
        {
            throw new AmpleException("AMP5450", "This project has no AI assistant. Choose one in the project settings.");
        }

        if (!policy.CodexEnabled || !policy.AllowsAssistant(ai.Assistant))
        {
            throw new AmpleException("AMP5450", "This server does not allow the Codex identity of this project. It is not replaced by another one.");
        }

        if (!policy.AllowsMode(ai.Mode))
        {
            throw new AmpleException("AMP5450", $"This server does not allow the execution mode {ModeLabel(ai.Mode)}. It is not replaced by another one.");
        }

        if (ai.Acknowledged is null || (ai.Mode == "unsafe" && ai.UnsafeAcknowledged is null))
        {
            throw new AmpleException("AMP5451", "The risks of the AI assistant have not been acknowledged for this project.");
        }

        if (CodexVersion(policy.CodexPath) is null)
        {
            throw new AmpleException("AMP5453", "Codex is not installed (or not found).");
        }

        if (ai.Assistant == "codex-device" && ai.LinkedAccount is null)
        {
            throw new AmpleException("AMP5452", "The Codex identity of this project is not linked yet. Link it in the project settings.");
        }

        if (request.Kind == "koda")
        {
            // Koda does one thing at a time, and asks little: a low effort unless the caller says otherwise
            if (Live.Values.Any(x => x.Request.Kind == "koda" && x.Status is "queued" or "preparing" or "running" or "waiting-approval"))
            {
                throw new AmpleException("AMP5464", "Koda is still busy with the last question.");
            }

            request = request with { Effort = request.Effort ?? "low" };
            DropKeptKodaCopies(request.ContinueFrom);
        }

        string key = request.Kind == "koda" && KodaLinked ? KodaHome : ai.Assistant == "codex-device" ? DeviceHome : "server-account";
        AiTask task = new() { Request = request, ProjectId = s.Manifest.Id, SessionId = sessionId, Identity = ai.Assistant, Mode = ai.Mode, BaseUrl = baseUrl, Key = key, Status = "queued" };
        task.LogAdded = AppendTaskLog;
        tasks[task.Id] = task;
        Live[task.Id] = task;
        sessionStore.LinkTask(sessionId, task.Id, request.Prompt, task.Status);
        sessionStore.SaveTask(task);
        reduxTasks.RecordTask(task, "queued");
        task.Add("user", request.Prompt.Trim());
        task.Add("info", "Queued: a Codex account is used by one task at a time, so a task waits until the one before it is done.");
        _ = Task.Run(async () =>
        {
            try
            {
                using IDisposable account = await CodexGate.AcquireAsync(key, task.Cancel.Token).ConfigureAwait(false);
                task.Status = "preparing";
                sessionStore.UpdateTask(task);
                await PrepareAsync(task, task.Cancel.Token).ConfigureAwait(false);
                sessionStore.SaveTask(task);
                await RunAsync(task, policy).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                task.Status = "cancelled";
                task.Add("warn", "The task was cancelled. Nothing in the project changed.");
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
            }
            catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
            {
                task.Status = "failed";
                task.Error = ex.Message;
                task.Add("error", ex.Message);
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
            }
            finally
            {
                Live.TryRemove(task.Id, out AiTask? _);
            }
        }, CancellationToken.None);
        return Task.FromResult(task);
    }

    /// <summary>Tasks do not survive a restart of the server, so the folders of earlier sessions are of no use: remove them once, before the first task of this session.</summary>
    private void PruneLeftovers()
    {
        if (pruned)
        {
            return;
        }

        pruned = true;
        foreach (string old in new[] { "attachments", "hums" })
        {
            string folder = Path.Combine(Work, old);
            if (Directory.Exists(folder))
            {
                foreach (string f in Directory.EnumerateFiles(folder).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-3)))
                {
                    try { File.Delete(f); } catch (IOException) { /* in use: next time */ }
                }
            }
        }

        foreach (string part in new[] { "work", "validate", "audio" })
        {
            string folder = Path.Combine(Work, part);
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (string d in Directory.EnumerateDirectories(folder))
                    {
                        string name = Path.GetFileName(d);
                        if (!tasks.ContainsKey(name))
                        {
                            DeleteTree(d);
                        }
                    }

                    foreach (string f in Directory.EnumerateFiles(folder, "*.wav"))
                    {
                        if (!tasks.ContainsKey(Path.GetFileNameWithoutExtension(f)))
                        {
                            File.Delete(f);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // what cannot be removed now stays for the next start
            }
        }
    }

    /// <summary>The task with this id.</summary>
    public AiTask GetTask(string id)
    {
        RestoreSavedTasks();
        return tasks.TryGetValue(id, out AiTask? task) ? task : throw new AmpleException("AMP5458", "There is no such AI task.");
    }

    /// <summary>
    /// Reattaches Codex to a project-local session: after a server restart (interrupted), or after the session of a proposal ended for being idle. With <paramref name="fresh"/> a new
    /// Codex thread is started in the same working copy and told what was asked and what is already changed, instead of resuming the old thread.
    /// </summary>
    public async Task<AiTask> ResumeTaskAsync(string id, string baseUrl, bool fresh, CancellationToken ct)
    {
        AiTask task = GetTask(id);
        task.BaseUrl = baseUrl;
        bool ended = task.Rpc is null && task.Status == "ready" && !task.Applied;
        bool finished = task.Rpc is null && task.Status is "accepted" or "rejected" && task.ThreadId.Length > 0;   // accepted or rejected, the copy and the conversation are kept for a day
        string priorStatus = task.Status;
        bool stopped = (task.Status is "cancelled" or "failed") && !Live.ContainsKey(task.Id);
        if ((task.Status != "interrupted" && !ended && !stopped && !finished) || (!fresh && task.ThreadId.Length == 0) || !Directory.Exists(task.Workspace))
        {
            throw new AmpleException("AMP5458", "This assistant session has no recoverable Codex thread and working copy.");
        }

        AiAssistantSettings settings = Settings();
        AiPolicy policy = AiPolicy.Read();
        if (settings.Assistant != task.Identity || !policy.CodexEnabled || !policy.AllowsAssistant(task.Identity) || !policy.AllowsMode(task.Mode))
        {
            throw new AmpleException("AMP5450", "The current server policy or project identity no longer allows this assistant session.");
        }

        if (task.Cancel.IsCancellationRequested) task.Cancel = new CancellationTokenSource();

        string? home = HomeOf(task);
        List<string> extra = task.Identity == "codex-server" && !policy.AllowInheritance ? ["-c", "project_doc_max_bytes=0", "-c", "features.memories=false"] : [];
        extra.AddRange(NoOutsideControl);
        extra.AddRange(McpArguments(task));
        CodexRpc rpc = CodexRpc.Start(policy.CodexPath, home, task.Workspace, extra);
        task.Rpc = rpc;
        rpc.OnNotification = (method, parameters) => OnNotification(task, method, parameters);
        rpc.OnServerRequest = (method, parameters) => ServerRequestAsync(task, method, parameters);
        try
        {
            await rpc.InitializeAsync(ct).ConfigureAwait(false);
            if (fresh)
            {
                JsonObject threadParams = ModeParameters(task.Mode, task.Request.Kind == "explain", policy, task.Request.Strict);
                threadParams["cwd"] = task.Workspace;
                threadParams["developerInstructions"] = Instructions(task);
                threadParams["ephemeral"] = false;
                string? model = task.Request.Model is null ? policy.ModelToUse : string.Equals(task.Request.Model, "default", StringComparison.OrdinalIgnoreCase) ? null : task.Request.Model;
                if (model is not null)
                {
                    threadParams["model"] = model;
                }

                JsonNode? started = await rpc.RequestAsync("thread/start", threadParams, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                task.ThreadId = started?["thread"]?["id"]?.GetValue<string>() ?? throw new AmpleException("AMP5453", "Codex did not start a thread.");
                task.PendingContext = SessionSummary(task);
            }
            else
            {
                // a resumed thread must be given the sandbox and the approval policy again: without them Codex uses its own default, which can be read-only ("the filesystem is read-only, I cannot edit the song")
                JsonObject resume = ModeParameters(task.Mode, task.Request.Kind == "explain", policy, task.Request.Strict);
                resume["threadId"] = task.ThreadId;
                resume["cwd"] = task.Workspace;
                JsonNode? thread = await rpc.RequestAsync("thread/resume", resume, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                task.ThreadId = thread?["thread"]?["id"]?.GetValue<string>() ?? task.ThreadId;
            }

            ContextTokens[task.ContextToken] = task;
            task.Status = "ready";
            task.Error = null;
            if (finished)
            {
                // what the conversation last saw is the copy: the next changes are measured from it, and the project may have moved on (ample_changes_since shows how)
                task.Applied = false;
                task.Changes.Clear();
                task.StaleFiles.Clear();
                foreach (string rel in task.Baseline.Keys.ToList())
                {
                    string copy = Path.Combine(task.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    task.Baseline[rel] = File.Exists(copy) ? SafeRead(copy) : null;
                }

                task.PendingContext = await ResumeNoteAsync(task, priorStatus).ConfigureAwait(false);
                ended = true;
            }

            task.Add("info", fresh
                ? "A new Codex session started in this working copy. It is told what was asked and what is already changed; your next message goes to it."
                : ended ? "The Codex conversation was resumed." : "The Codex conversation resumed after the server restart. Unaccepted proposal changes remain in the temporary copy.");
            if (!ended)
            {
                await BuildProposalAsync(task).ConfigureAwait(false);
            }

            sessionStore.SaveTask(task);
            sessionStore.UpdateTask(task);
            ScheduleExpiry(task);
            DeliverQueuedMessages(task);
            return task;
        }
        catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException or OperationCanceledException)
        {
            task.Rpc = null;
            task.Status = finished ? priorStatus : ended ? "ready" : "interrupted";
            task.Error = ended ? null : ex.Message;
            task.Add("error", (fresh ? "Codex could not start a new session. " : "Codex could not resume this thread. ") + "The proposal copy remains available: " + ex.Message);
            await rpc.DisposeAsync().ConfigureAwait(false);
            sessionStore.SaveTask(task);
            sessionStore.UpdateTask(task);
            throw;
        }
    }

    // What a new Codex session needs to know to go on with a proposal: the request, what the working copy already changes, and what was delivered.
    private static string SessionSummary(AiTask task)
    {
        StringBuilder summary = new();
        summary.AppendLine("You are continuing an earlier assistant session of this task in the same working copy; its conversation is not available, so this is what is known.");
        summary.AppendLine("Original request: " + task.Request.Prompt.Trim());
        string[] changed = [.. task.Changes.Select(c => c.Path)];
        summary.AppendLine(changed.Length == 0 ? "The working copy has no changes yet." : "The working copy already contains changes to: " + string.Join(", ", changed) + ". Read them before changing anything.");
        int delivered = task.ClipsNow().Count(c => c.Source == "proposal");
        if (delivered > 0)
        {
            summary.AppendLine($"{delivered} sample clip(s) were already presented to the user" + (task.ContinuationClipLimit is int limit ? $" (the requested quantity was {limit}); do not present more than were asked for." : "."));
        }

        summary.AppendLine("Keep the original requirements and do not redo finished work.");
        return summary.ToString().TrimEnd();
    }

    /// <summary>The tasks of this session, newest first.</summary>
    public IReadOnlyList<AiTask> Tasks()
    {
        RestoreSavedTasks();
        return [.. tasks.Values.OrderByDescending(t => t.Created)];
    }

    public IReadOnlyList<AssistantSession> AssistantSessions()
    {
        RestoreSavedTasks();
        // what each conversation can do now: it is live, it can be resumed (thread and working copy are kept), or it is over
        return [.. sessionStore.List().Select(session => session with { Tasks = [.. session.Tasks.Select(t => tasks.TryGetValue(t.Id, out AiTask? at) ? t with { Status = at.Status, Resume = ResumeState(at), Archived = at.Archived } : t with { Resume = "no" })] })];
    }

    private static string ResumeState(AiTask task)
    {
        if (task.Rpc is not null && task.Status is not ("cancelled" or "failed" or "rejected" or "accepted")) return "live";
        bool ended = task.Rpc is null && task.Status == "ready" && !task.Applied;
        bool finished = task.Rpc is null && task.Status is "accepted" or "rejected" && task.ThreadId.Length > 0;
        bool stopped = task.Status is "cancelled" or "failed" && !Live.ContainsKey(task.Id);
        return (task.Status == "interrupted" || ended || stopped || finished) && task.ThreadId.Length > 0 && Directory.Exists(task.Workspace) ? "can" : "no";
    }

    public AssistantSession CreateAssistantSession(string title, bool archiveConversation) => sessionStore.Create(title, archiveConversation);

    public AssistantSession RenameAssistantSession(string id, string title) => sessionStore.Rename(id, title);

    public AssistantSession SetConversationArchive(string id, bool enabled, bool includeEarlier)
    {
        AssistantSession before = sessionStore.Get(id);
        AssistantSession updated = sessionStore.SetConversationArchive(id, enabled, includeEarlier);
        if (enabled && includeEarlier && !before.ArchiveConversation)
        {
            foreach (AssistantTaskSnapshot task in sessionStore.SavedTasks(id))
            {
                foreach (AiLog entry in sessionStore.SavedConversation(id, task.Id)) reduxTasks.RecordConversation(new AiTask { Id = task.Id, SessionId = id }, entry);
            }
        }

        return updated;
    }

    private void AppendTaskLog(AiTask task, AiLog entry)
    {
        if (entry.Kind is "user" or "assistant" or "thought" or "tool" or "approval")
        {
            if (task.SessionId is not null && sessionStore.Get(task.SessionId).ArchiveConversation)
            {
                reduxTasks.RecordConversation(task, entry);
                sessionStore.AppendConversationLog(task, new AiLog { Seq = entry.Seq, At = entry.At, Kind = entry.Kind, Text = ReduxTaskArchive.Redact(entry.Text), Path = entry.Path is null ? null : ReduxTaskArchive.Redact(entry.Path), Why = entry.Why is null ? null : ReduxTaskArchive.Redact(entry.Why), Details = entry.Details is null ? null : JsonNode.Parse(ReduxTaskArchive.Redact(entry.Details.ToJsonString())) as JsonObject });
            }

            return;
        }

        sessionStore.AppendLog(task, entry);
    }

    public void DeleteAssistantSession(string id)
    {
        RestoreSavedTasks();
        AssistantSession session = sessionStore.Get(id);
        AiTask[] linked = session.Tasks.Select(item => tasks.GetValueOrDefault(item.Id)).Where(task => task is not null).Select(task => task!).ToArray();
        if (linked.Any(task => task.Status is "queued" or "preparing" or "running" or "waiting-approval" or "validating") || linked.Any(task => task.Status == "ready" && !task.Applied) || linked.Any(task => task.Status == "interrupted" && task.Changes.Count > 0))
        {
            throw new AmpleException("AMP5458", "Finish, accept or reject the session's pending proposal before deleting this session.");
        }

        sessionStore.Delete(id);
    }

    private void RestoreSavedTasks()
    {
        lock (restoreGate)
        {
            if (restoredTasks) return;
            restoredTasks = true;
            foreach (AssistantSession session in sessionStore.List())
            {
                foreach (AssistantTaskSnapshot saved in sessionStore.SavedTasks(session.Id))
                {
                    if (tasks.ContainsKey(saved.Id)) continue;
                    bool interrupted = !saved.Applied && saved.ThreadId.Length > 0 && saved.Status is ("queued" or "preparing" or "running" or "waiting-approval" or "validating" or "ready" or "interrupted");
                    AiTask task = new() { Id = saved.Id, ProjectId = s.Manifest.Id, SessionId = saved.SessionId, Request = saved.Request, Status = interrupted ? "interrupted" : saved.Status, Created = saved.Created, Identity = saved.Identity, Mode = saved.Mode, Workspace = saved.Workspace, ThreadId = saved.ThreadId, Summary = saved.Summary, Error = saved.Error, BuildStatus = saved.BuildStatus is "pending" or "running" ? "skipped" : saved.BuildStatus, BuildMessage = saved.BuildStatus is "pending" or "running" ? "The build check was interrupted by a server restart. Run it again before accepting." : saved.BuildMessage, OutputSong = saved.OutputSong, Applied = saved.Applied, Stale = saved.Stale, RebaseMessage = saved.RebaseMessage, SkipBuildRequested = saved.SkipBuildRequested, SkipBuildReason = saved.SkipBuildReason, Archived = saved.Archived, BrowserAllowedForTask = saved.BrowserAllowedForTask, BrowserAccessUntil = saved.BrowserAccessUntil };
                    foreach ((string path, string? text) in saved.Baseline) task.Baseline[path] = text;
                    task.Changes.AddRange(saved.Changes);
                    foreach (AiClip restored in saved.Clips)
                    {
                        if (restored.AudioHash.Length == 0 && File.Exists(restored.File))
                        {
                            restored.AudioHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(restored.File)));
                        }

                        task.Clips.Add(restored);
                    }

                    task.ContinuationClipLimit = saved.ContinuationClipLimit;
                    task.AcceptedPaths.AddRange(saved.AcceptedPaths ?? []);
                    task.LogAdded = AppendTaskLog;
                    foreach (AiLog line in sessionStore.SavedLog(session.Id, saved.Id)) task.AddRestored(line);
                    if (interrupted && saved.Status != "interrupted") task.Add("warn", "The server restarted while Codex was working. The temporary proposal was kept; resume this task to continue.");
                    tasks[task.Id] = task;
                    sessionStore.UpdateTask(task, touch: false);
                }
            }
        }
    }

    private async Task PrepareAsync(AiTask task, CancellationToken ct)
    {
        // Koda hanging around: the question goes on in the working copy of the last one (its song is brought up to date below), so the Codex conversation and the folder agree
        task.Workspace = KeptKodaCopy(task)?.Workspace ?? Path.Combine(Work, "work", task.Id);
        Directory.CreateDirectory(task.ProjectDir);
        DocSession song = await s.DocAsync(task.Request.Song, ct).ConfigureAwait(false);
        AmpleDocument parsed = s.ParseExpanded(song);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        string Rel(string full) => Path.GetRelativePath(s.Root, full).Replace('\\', '/');
        long total = 0;
        int skipped = 0;
        void Add(string rel)
        {
            if (files.ContainsKey(rel) || !IsTextFile(rel))
            {
                return;
            }

            string full = ProjectPath.Resolve(s.Root, rel);
            if (!File.Exists(full))
            {
                return;
            }

            long size = new FileInfo(full).Length;
            if (size > MaxFileBytes || files.Count >= MaxFiles || total + size > MaxTotalBytes)
            {
                skipped++;
                return;
            }

            // the choice of the person: the working copies of what is open (the song, other songs and text files with unsaved changes), or the saved files only
            files[rel] = task.Request.Context == "saved" ? File.ReadAllText(full) : rel == task.Request.Song ? song.Text : s.Texts.Read(rel) ?? s.DirtyDocText(rel) ?? File.ReadAllText(full);
            total += size;
        }

        files[task.Request.Song] = song.Text;
        Add(task.Request.Path);
        if (task.Request.Scope is "linked" or "project")
        {
            foreach (BlockNode b in parsed.Blocks.Where(b => !b.Skipped))
            {
                foreach (string f in BlockFiles.Of(s.Root, s.AmpleRoot, parsed, b))
                {
                    if (f.StartsWith(s.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(Rel(f));
                    }
                }
            }
        }

        if (task.Request.Scope == "project")
        {
            foreach (string f in Directory.EnumerateFiles(s.Root, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(f);
                if (!rel.StartsWith(".ample/", StringComparison.Ordinal) && !rel.StartsWith("out/", StringComparison.Ordinal) && !rel.StartsWith("exports/", StringComparison.Ordinal))
                {
                    Add(rel);
                }
            }
        }

        foreach ((string rel, string text) in files)
        {
            string target = Path.Combine(task.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
            task.Baseline[rel] = text;
        }

        // the reference: the language, the sequencer and the blocks, so Codex does not have to guess the syntax
        string reference = Path.Combine(task.Workspace, "reference");
        Directory.CreateDirectory(reference);
        Directory.CreateDirectory(Path.Combine(task.Workspace, "scratch"));   // a place for scripts and temporary files inside the working folder, so nothing asks for permission
        foreach (string doc in new[] { "language", "sequencer.md", "midi.md", "modules.md", "score-import.md", "ai-access.md", "sound-sources.md" })
        {
            string source = Path.Combine(s.AmpleRoot, "docs", doc);
            if (Directory.Exists(source))
            {
                foreach (string f in Directory.EnumerateFiles(source, "*.md"))
                {
                    File.Copy(f, Path.Combine(reference, "language-" + Path.GetFileName(f)), true);
                }
            }
            else if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(reference, doc), true);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(reference, "blocks.md"), BlockCatalog(), ct).ConfigureAwait(false);
        string skill = Path.Combine(task.Workspace, ".agents", "skills", "ample");
        Directory.CreateDirectory(skill);
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"), SkillText, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(task.Workspace, "AGENTS.md"), "# AMPLE task\n\nRead TASK.md, use the skill `ample` and the MCP tools of the server `ample` (ample_project, ample_faer_status, ample_docs, ample_song, ample_block, ample_block_types, ample_file, ample_web_search, ample_web_read, ample_browser_open, ample_browser_show, ample_browser_read, ample_browser_click, ample_browser_type, ample_browser_mouse_move, ample_browser_media_mute, ample_browser_screenshot, ample_browser_network_stats, ample_browser_js). Choose DuckDuckGo for lightweight Instant Answers and Brave for useful full results; avoid redundant searches. Brave is capped at 10 actual API calls per Codex turn; AMPLE provides estimated monthly remaining against 1,000 requests based only on its own API-key requests, not Brave's live balance. Identical Brave query/page results are cached in memory for five minutes only. Use ample_web_read for user-supplied or approved search-result URLs without cookies or JavaScript and without another approval; use Playwright only when the page needs JavaScript or human interaction. Browser tools require owner approval; the browser is headless by default and switches visible for detected manual input, or on ample_browser_show. The owner enters credentials manually. Page JavaScript can trigger page actions and bypass numbered-click purchase blocking; never return credentials, tokens or cookies. Change only files under ./project.\n", ct).ConfigureAwait(false);
        if (task.Request.Kind == "koda")
        {
            await File.AppendAllTextAsync(Path.Combine(task.Workspace, "AGENTS.md"), KodaProfile + KodaLanguageText(ProjectFile.Parse(File.ReadAllText(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile))).Settings.Koda.Language), ct).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(task.Workspace, "TASK.md"), TaskText(task, files), ct).ConfigureAwait(false);
        task.Add("info", $"Prepared a temporary copy with {files.Count} file(s) in scope. Nothing in the project is changed while Codex works.");
        if (skipped > 0)
        {
            task.Add("warn", $"{skipped} file(s) were left out of the copy because the scope is limited to {MaxFiles} files, {MaxFileBytes / 1_000_000} MB per file and {MaxTotalBytes / 1_000_000} MB in total. Choose a smaller scope if Codex needs them.");
        }
    }

    /// <summary>The words of Codex without the path of the temporary copy and without links that point into it.</summary>
    /// <summary>
    /// The song a measuring tool works on: the song named by <paramref name="path"/> when it is a song (.amplet), else the song of the task. A task of the whole project, or one that makes a new song,
    /// is not tied to the song it was started from.
    /// </summary>
    private static string SongOf(AiTask task, string? path)
    {
        string chosen = string.IsNullOrWhiteSpace(path) ? string.Empty : AiContext.Norm(path);
        if (chosen.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            chosen = chosen[8..];
        }

        return chosen.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) ? chosen : AiContext.Norm(task.Request.Song);
    }

    /// <summary>1st, 2nd, 3rd, 4th ... 11th, 12th, 13th, 21st.</summary>
    private static string Ordinal(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ((n % 100) is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string Tidy(AiTask task, string text)
    {
        string result = text.Replace(task.Workspace.Replace('\\', '/'), ".", StringComparison.OrdinalIgnoreCase).Replace(task.Workspace, ".", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(result, @"\[([^\]]+)\]\([^)]*\)", "$1", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Codex of an AMPLE task works in its own folder with the tools of AMPLE only: it does not drive the owner's browser or desktop (browser use and computer use attach to the browser
    /// of the owner and show "started debugging this browser"), and it has nothing to do there: AMPLE gives it what it needs.
    /// </summary>
    private static readonly string[] NoOutsideControl = ["-c", "features.browser_use=false", "-c", "features.browser_use_external=false", "-c", "features.browser_use_full_cdp_access=false", "-c", "features.computer_use=false"];

    /// <summary>The arguments that connect Codex to the task-scoped AMPLE MCP server, including its approval-gated actions (the AMPLE command line and task token).</summary>
    private static List<string> McpArguments(AiTask task)
    {
        string? exe = Environment.ProcessPath;
        string? entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is { } entryName ? Path.Combine(AppContext.BaseDirectory, entryName + ".dll") : null;
        if (exe is null || task.BaseUrl.Length == 0)
        {
            return [];
        }

        List<string> args = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (entry is null)
            {
                return [];
            }

            args.Add(entry);
        }

        args.AddRange(["mcp", "--url", task.BaseUrl]);
        string list = string.Join(",", args.Select(Toml));
        return ["-c", $"mcp_servers.ample={{command={Toml(exe)},args=[{list}],env={{AMPLE_CONTEXT_TOKEN={Toml(task.ContextToken)}}}}}"];
    }

    /// <summary>A TOML basic string: any path or text can be written, because the quote, the backslash and the control characters are escaped.</summary>
    internal static string Toml(string value)
    {
        StringBuilder text = new("\"");
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"),
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    private static bool IsTextFile(string rel) => TextExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase);

    private string BlockCatalog()
    {
        StringBuilder text = new("# Block types of this AMPLE\n\nEvery block in a song is one of these. A block is `type name [pinned@<version>] at <bar> for <bars> { parameter value ... from block.output -> input }`; leave the pin out unless the song must keep one version.\n\n");
        foreach (TemplateSpec t in s.Spec.Templates.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"## {t.Id} (version {t.Version})\n{t.Description}\n- inputs: {string.Join(", ", t.Inputs.Select(i => $"{PortNames.Written(i)}:{i.Type}").Distinct())}\n- outputs: {string.Join(", ", t.Outputs.Select(o => $"{PortNames.Written(o)}:{o.Type}").Distinct())}\n");
            foreach (KeyValuePair<string, string> p in t.Parameters)
            {
                text.Append(CultureInfo.InvariantCulture, $"- parameter {p.Key} (default: {(p.Value.Length == 0 ? "none" : p.Value)})\n");
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static string TaskText(AiTask task, IReadOnlyDictionary<string, string> files)
    {
        AiTaskRequest r = task.Request;
        StringBuilder text = new();
        text.Append("# Task\n\n").Append(r.Prompt.Trim()).Append("\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- kind: {r.Kind}\n- scope: {r.Scope}\n- main file: project/{r.Path}\n- song: project/{r.Song}\n");
        if (r.Block is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $"- block: {r.Block}\n");
        }

        if (r.Blocks is { Count: > 0 })
        {
            text.Append(CultureInfo.InvariantCulture, $"- initial block context: {string.Join(", ", r.Blocks)}\n- inspect these blocks with ample_block and keep changes focused here when practical; expand to connected blocks or broader project structure when the requested outcome needs it, and explain why the scope expanded\n");
        }

        if (r.FromLine is int a)
        {
            text.Append(CultureInfo.InvariantCulture, $"- selection: lines {a} to {r.ToLine ?? a} of project/{r.Path}\n");
            if (r.Scope == "selection" && files.TryGetValue(r.Path, out string? selectedFile))
            {
                string[] sourceLines = selectedFile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                int start = Math.Clamp(a - 1, 0, sourceLines.Length);
                int end = Math.Clamp(r.ToLine ?? a, start, sourceLines.Length);
                text.Append("\nSelected source text:\n```ample\n")
                    .Append(string.Join("\n", sourceLines.Skip(start).Take(end - start)))
                    .Append("\n```\n");
            }
        }

        text.Append("\nFiles in scope (you may change these, and only these, unless the task needs a new file next to them):\n");
        foreach (string f in files.Keys.Order(StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"- project/{f}\n");
        }

        return text.ToString();
    }

    private const string SkillText = """
        ---
        name: ample
        description: Use when editing an AMPLE music project (.amplet songs, .ampleseq or .seq sequences, lyrics, modules): how the language works, which block types exist, how to look at the project, the song and a block, and how to write valid AMPLE.
        ---
        # AMPLE in short

        A song is a text file (.amplet) of blocks. A block looks like `type name [pinned@<version>] at <bar> for <bars> { parameter value ... from other.output -> input }` (no class and no number: only a pinned block states `pinned@<version>`).
        The first line of a song is the project line: `composition "Name" { ample 1  tempo 92  master-seed 1 }`. A line that starts with # is a comment; comments directly above a block are that block's notes.
        If a request needs something the language cannot do (a loop, a variable number of blocks that the language has no word for), say so in your answer and propose what is possible; do not approximate it with something that only looks alike. In the process of an audio component, audio |> repeat(times=cars, delay=2bar, length=24bar) makes a number of copies (a property can give the number), spread over a time or at a distance (every=4bar), and mixes them.
        A control is a number over time that steers a parameter that can be automated: follow (from a sound), curve (written) and map (rescale, unit) make one (output level; follow also has gate), and a parameter reads it either as an expression, `cutoff 600 Hz + 3000 * traffic.level`, or by a connection, `from traffic.level -> cutoff`. A control with a unit (unit Hz on follow, curve or map) only steers a parameter in that unit; one without a unit is scaled to the range of the parameter.
        A connection is written in the block that receives: `from source.output -> input`. Everything that makes sound ends in the `master` block.
        A sequence (.ampleseq, older .seq) holds channels, patterns and a playlist; a sequencer block names it with `file sequences/name.ampleseq`.

        Choose the lyric format for the request. For exact, untimed lyrics supplied for ACE music generation, preserve the text in `.txt` and feed it through the lyrics block; `.txt` is an accepted lyrics source and avoids inventing bar timings. Use `.lyr` when the owner wants AMPLE's timed-lyrics workflow or Lyrics editor: one `[bar N] text` row per lyric line, referenced by a `lyrics` block whose `words` output connects to ACE music or AI voice. Preserve supplied words exactly; do not convert untimed lyrics to `.lyr` unless timing is requested or clearly useful and can be added without changing the words.

        ## ACE generation baseline and reproduction
        Treat model identity, CFG guidance, steps, shift and solver as essential generation inputs alongside seed and lm_seed. For SFT/Base (including XL SFT BF16), start with steps=50 and guidance=7; use shift=1 for SFT, shift=3 for Base, solver=euler. Turbo starts with steps=8, guidance=1, shift=3. Write these values explicitly in every standalone ACE block and preserve them across seed comparisons. An exact model filename overrides the family selector; use its actual family. For unfamiliar variants, consult its documentation and state uncertain settings. BF16 precision alone never proves better sound.
        Summaries must identify model, steps, CFG, both seeds and any changed shift/solver. Preserve the original take. Reproduction checks warn on model/software changes and compare new audio; never claim exact reproduction without matching outputs. Historical takes may lack a complete recipe; do not invent missing values. ample_random_seed returns masterSeed/seed for project seeds and blockSeed for seed/lm_seed (32-bit).

        ## Look before you write
        - `ample_project`: what the project is, its files, and its linked sample-pack catalogs (global and project-scoped). Check this before fetching or inventing a royalty-free/CC0 source: an already-linked catalog is preferred over a one-off web fetch.
        - `ample_random_seed`: obtain a genuinely random AMPLE seed from the OS cryptographic generator. Use it when you need a fresh random master or block seed; never pretend a number generated by language-model text is random.
        - `ample_faer_status`: the per-project song, voice and separation routes plus live Faer readiness and the installed ACE model filenames, roles and sizes. Read `faer-model-catalog` with `ample_docs` for model specialties and recommendations. Preserve AMPLE's hardcoded `synthModel` families and use the live server inventory. If the user asks for “the best”, quality-first, or the highest quality, start with the catalog's highest-precision compatible inventory-listed model using `synthModelFile` (currently XL SFT BF16 when listed), even if ACE `/props` does not advertise the exact file; the render attempt determines whether the worker accepts it. If that attempt fails, report its raw reason and retry explicitly with XL SFT Q5 as fallback. Never guess a filename or silently substitute a model. When ACE choices matter, say which family or exact file was used in the final proposal summary so it appears in Output, Changes and Checks. A route set to `local` stays local. Check this before choosing or recommending a remote engine; report unavailable capabilities instead of silently substituting an engine.
        - All AMPLE build, listen and arrangement-measure tools follow the project's configured routes, including Faer. Not calling a Faer-specific tool does not force local rendering. If the user asks to avoid Faer while the relevant route is Faer, explain that there is no per-call local-only override and do not claim the work stayed local.
        - If a later edit makes an earlier sample unrepresentative, use `ample_obsolete_sample` with its clipId and a concise reason. It stays visible until the user dismisses it; a newer sample alone does not make an earlier one obsolete.
        - `ample_docs`: the reference (read `language/03-syntax` and `sequencer` first; search with `query`).
        - `ample_block_types`: every block type, and one in full with its ports and parameters.
        - `ample_song`: the song with every block, its settings and connections, the comments and the problems the parser finds.
        - `ample_block`: one block with what feeds it and what it feeds.
        - `ample_file`: a text file such as a sequence.
        - Create songs with `ample_edit` op `create`, using a project-relative path such as `songs/new-song.amplet`. The tool places it under ./project; never create songs with shell writes in the workspace root. `ample_file` is read-only.
        - `ample_memory_read`: the notes AMPLE keeps for later tasks outside the song: scope=project (taste, agreements and decisions of the whole project) or scope=song (what this song is for, what was tried). Read both at the start of a task (ample_batch can do both at once).
        - `ample_memory_write`: keep a short note for later tasks: scope=project or scope=song, mode=append (one durable fact, the default) or replace (rewrite and tidy the whole memory). Write what stays true: the owner's taste, decisions and why, what was tried and did not work. Never put it in the song, and never write chat, audio data or what the proposal already says.
        - `ample_move_file`: move/rename a proposal text file without overwriting, source and destination project-relative. To recover a misplaced workspace-root song, use source `workspace/name.amplet` and destination `name.amplet`. This changes only the staged proposal; do not delete the file or claim moving it requires separate permission. Check the destination with ample_file and ample_check.
        - PDF downloads return extracted text through `ample_fetch_url`; do not depend on Python pypdf or browser access. Image-only pages and musical notation still need visual interpretation; do not invent their contents.
        - `ample_move`: move or rename a project file or folder (audio, a module, a song, a whole folder) with every path in the songs and modules that points at it following. It is NOT made in your working copy: it is made when the owner accepts. Use `confirm: "ask"` when the rest of your work depends on the move (the owner is asked now and you wait for the answer; if it is declined, go on without it), and `confirm: "propose"` (the default) when it is part of the change that the owner reviews together: it is listed with your proposal and the owner ticks it. Give a short `reason`. Never use it to delete or overwrite; the destination must not exist. Keep using the old path in your own edits.
        - `ample_request_asset`: ask the user to let you read a large or non-text source asset (a sample, a recording); AMPLE copies it into `assets/` in your working folder when they allow it. Say why you need it.
        - `ample_edit`: change a file with structured edits instead of shell commands or regular expressions. For compact MIDI notes use `{"op":"note_add","block":"lead","note":"4:0.5:0.25:67:90"}`, `{"op":"note_remove","block":"lead","bar":4,"pitch":67,"beat":0.5}`, or `{"op":"note_transform","block":"lead","action":"transpose","amount":2,"bar":4}` (also `velocity` with a delta or `quantize` with a beat-grid). These operations can target a midi-clip by bar and pitch. Expanded named note bodies are not supported by them yet. Other edits include for a sequence kept in the song (a sequencer block with a `sequence { }` part) use `{"op":"sequence_row","block":"beat-bed","pattern":"groove","channel":"bell","text":"1:0:1:80:90"}` to set exactly one row of one pattern (empty text removes it) and `{"op":"sequence_channel","block":"beat-bed","channel":"bell","text":"gm program=10 vol=-24"}` to add one channel, instead of rewriting the whole sequence. Other edits include `set_param`, `replace`, `insert`, `delete_lines`, `add_block`, `remove_block`, `connect`, `disconnect` and `create`. `why` is optional. All edits of one call apply or none.
        - `ample_fetch_url`: you have no network. When the user gives a link (an image, an SVG, a page, a sound), fetch it with this tool: AMPLE downloads it into `assets/web/` and, for text and SVG, returns the content. A link from the request is fetched at once; any other link needs the user's approval.
        - `ample_install_pack`: when the chosen source is a whole sample pack (a .zip with a pack.json) rather than a single file, install it into the library with this tool instead of `ample_fetch_url`: it always asks the user's approval first (even when the link was given), then the pack becomes available to every project afterward (check `ample_project`'s `catalogs` to confirm). Prefer this over a one-off fetch when a real pack is available; it is not for single audio files.
        - Web research: choose the provider based on the need: use DuckDuckGo's free official Instant Answer API for a lightweight fact or likely direct answer; choose Brave for useful full web results when configured. Do not repeat near-identical queries or search speculatively. Each search requires approval (or task permission). Brave has a hard cap of 10 actual API calls per Codex turn and serializes calls at one per second. AMPLE tracks its own Brave requests by API key and estimates against 1,000 per UTC calendar month; each response reports estimated monthly remaining. This is not the provider's live credit balance and cannot count calls made outside AMPLE. Identical Brave query/count/offset results are reused from a five-minute memory-only cache; Brave terms do not permit persistent result caches, so AMPLE does not write search results to disk. Use ample_web_read to fetch a user-supplied or search-result page without cookies or JavaScript and without asking again; it returns bounded text or links (or capped raw HTML). If that fails because the site needs JavaScript, sign-in, CAPTCHA or human input, use ample_browser_open after browser approval. That opens headless first and shows a visible window for detected human input; use ample_browser_show for unexpected states. Never scrape search result pages as a fallback.
        - Automated browser tools require owner permission: 30 minutes, this task, always in this project, or deny. Project permission can be revoked in Project > assistant permissions. The owner handles credentials. Use these tools only for external public sites the owner asked to inspect; never open AMPLE itself, localhost, or a local app in Playwright. Use numbered controls; no arbitrary click. ample_browser_mouse_move only hovers at viewport coordinates. ample_browser_screenshot captures the visible page only on request. ample_browser_network_stats returns recent sanitized console messages, and aggregate counts. Other network URLs, headers and bodies are omitted. ample_browser_media_mute changes HTML audio/video elements, not the browser tab/system audio. Purchase-related clicks remain blocked.
        - Before picking an unfamiliar royalty-free/CC0 audio source yourself (a bed, a loop, an SFX), read `reference/sound-sources.md` first: it lists which sources are actually safe for a commercial release and which are not. Also check `ample_project`'s `catalogs`: a sample already reachable through an already-linked catalog is always preferable to fetching a new one-off file or installing a new pack. Prefer a source from that list, an already-linked catalog, or a pack install over a one-off fetch; say which source you chose and why. A fetch of anything not in the request still needs the user's approval through `ample_fetch_url`. A non-commercial source (CC BY-NC and the rest marked "not suitable for releasing music" in that document) may still be used for a rough draft or exploratory overlay when the user is not asking for a release-ready result; say plainly that it is non-commercial/exploration-only and will need swapping before a release. Never use one silently or imply it is release-safe.
        - A new song of your own in the proposal (a request for the whole project, or for a new piece): the measuring tools (ample_listen, ample_build, ample_present, ample_arrangement) work on the song of the task unless you give `path` = the .amplet of your proposal. Give it; never put the new song into the file of the task to be able to measure it.
        - Timing smaller than a bar (a pause between spoken words, a sound that starts a little later): a placement is in WHOLE bars only (`at 1.8` is invalid, AMP1104/AMP1105). Use a `shift` block (seconds and/or beats later), a `join` block (in1, in2, ... one after the other with a gap), or `[pause 800 ms]` inside the text of a voice-clone block. `ample_lookup topic=howto query=pause` says more.
        - When a request asks for something that changes over time (a speed that slows down, an effect that grows, a fade) or something you cannot find: `ample_lookup topic=howto query=<word>` first (automation syntax and what accepts it, fades, speed, sections, sidechain), then `topic=diagnostic`/`search`. If it is not possible directly, do the closest workable thing and say plainly what it is; do not give up with "the docs have no syntax".
        - `ample_present`: let the user HEAR something: choose a musically useful slice of a block or master when that is the clearest preview; use the full render when the user asks for the full song or when a full listen is the sensible comparison. Slices use from/to in seconds or unit=bars. Present either the proposal (source=proposal) or the current project (source=current). The requested range is returned without an arbitrary duration cap, so never silently truncate a requested full-song preview. It appears in the task window as a card with a player; give it a label and say why, including the selected range when it is a slice. Use it when you are not sure which instrument, block or part the user means (present the candidates and say which you chose), to let them compare before and after, or to let them check a result. What you presented is remembered: ample_history lists it.
        - `ample_history`: what the user asked before in this project, what came out and whether it was accepted (the user may say "like before", "continue what we did", "undo that last thing"): `ample_history` lists the newest, `id=<id>` gives one in full with its changes. Look there before you say you do not know what they mean.
        - `ample_answer`: when the task is a question from Koda (the user asks what something is or does), deliver the answer with this tool: `title` (3 to 6 words), `answer` (at most three sentences) and `details` (markdown the user can unfold: bullets with the impact of each thing, a heading Summary at the end, a small table where facts line up). Do not repeat the answer in your final message. `action` (optional): one short sentence for what the Do that button would do, whenever your answer suggests a concrete change to the song (never write that the user can press Do that unless you give it, the button only exists with it); leave it out when the answer only explains, then the button is not shown.
        - `ample_revision`: a saved point (revision) of the project history, when the user points at one in the History tab: `id=<revision id>` says what it changed against the point before it, `path=<file>` adds that file's diff. A branch name such as garage is a line of the history, not a block of the song.
        - `ample_changes_since`: only in a RESUMED conversation (the AMPLE NOTE says so): what the project did since your working copy was based on it. No arguments is the overview, path=<file> that file's diff, path with import=true brings the project's version into your copy (merged with your edits). Look only at the files the new request needs.
        - `ample_lookup`: more when you need it. topic=units (what dB, %, ms, s, Hz, bars mean; how "20% quieter" converts to dB), topic=parameter name=type.parameter (unit, range, how to write it, what a change reaches), topic=impact name=<block> (what feeds it, what is rendered again when you change it, the files it reads), topic=search query=<word> (which block types, parameters and documents mention it). `ample_block_types type=<id>` lists every parameter with its unit, min, max, default and what it does.
        - `ample_arrangement`: where every block sits (in bars and seconds), the slice of the master, before the task and now, with what changed; without `measure=true` it calculates planned duration without rendering, while `measure=true` builds your proposal and tells the real length of the master in seconds, its peak and the silence at the start and end, next to the current project. If the user requests an output duration/range, use the non-rendering calculation first, adjust the master slice for a time-limited sample or the arrangement when the song itself should be shorter, and verify the planned duration is in range before the first expensive render. Use `measure=true` only after that preflight, when actual duration is needed; it builds the proposal. This is output duration, not render wall-clock time.
        - `ample_build`: does your proposal build? If not, you get why per block: message, code, hint from the documentation, what feeds it and its text.
        - Instrument comparisons with `ample_present`: changing label/why NEVER changes audio. Use source=proposal and edits=[{op:"set_param",block:"guitar-name",name:"program",value:"24"}] (optional path) for each temporary trial. Edits apply only to that call, without changing the proposal. Check returned settings/trial and identicalTo: if identicalTo is set, the clips contain identical audio; do not describe them as different sounds. Present a reference and real trials, then use ample_edit to apply the user's chosen settings. Never claim that you heard a difference yourself.
        - `ample_offer_variants`: when the user wants several alternatives of one thing (instruments, beats, sounds), make all of them in this one proposal, each as its own blocks, and call it once with a name, one line, the blocks and the block to listen to for each variant. AMPLE shows them side by side so the user can hear each one and keep one.
        - `ample_skip_build`: when the user explicitly asked for no build or render, explain that in `reason`. AMPLE still checks source errors and warnings, marks the proposal “Build not run”, and lets the user accept it without waiting for a build. Do not call ample_build, ample_listen, ample_present or ample_arrangement with measure=true for that request.
        - `ample_offered`: audio files the user offered you (dropped, picked or recorded), with what each one is (length, level, pitch), where they are in the song namespace (projectPath, like samples/offered/kick.wav) and whether your proposal uses them. When you use one, say so in your answer: the user decides at Accept which of them come along.
        - `ample_hum`: when the user hummed a melody for you (a recording in ./assets/hum), this gives the melody AMPLE found in it: notes with pitch names, the key, and the notes text of a midi-clip at the tempo of the song. It is an attempt (a hummed line is never exactly in tune or in time): check it against the request, adjust tempo, grid or key, and say how sure you are.
        - `ample_listen` / `ample_build` also take `edits` (the same list as ample_edit, and `path`): they are applied only in the temporary project of that call, so you can hear a variant (-3 dB or -6 dB?) next to the current proposal without changing it; apply the one you choose with ample_edit.
        - `ample_listen`: you cannot hear, so measure: builds your proposal in a temporary project and analyses a block (or the master): loudness and spectrum by default, next to the same block of the current project. Use it to check a change that can be heard. It is heavy (it builds the proposal and can take half a minute): measure once after your edits, not again for a proposal that has not changed (AMPLE then gives you the earlier answer).
        - `ample_check`: the new errors and warnings of your proposal (none means you can finish). `ample_batch`: many read calls in one request (`calls`: a list of `{tool, arguments}`), much faster than one by one.
        - `ample_thinking`: tell the user in one or two short sentences what you are thinking about (every tool also takes an optional `thinking` argument for the same). Use it when you start a new step, so they can follow you.
        - `ample_analyze`: measure the real sound of a block (tempo, key, loudness, spectrum, pitch) when the text alone cannot tell you, for example to compare a loop with the project tempo.

        ## Which tool when
        The skill is the knowledge (what a block is, how to write it); the tools are the live data (what this project and this sound actually are). Look up the song and the block before you change them, read the reference before you write syntax you are unsure of, and measure with ample_analyze before you claim something about how a block sounds.
        The files in ./project are a copy: change them there. AMPLE compares the copy with the project afterwards, checks it and shows the user the difference.

        ## Rules
        - Keep names without spaces, keep the style and the order of the file, change as little as the task needs.
        - A block type, a port and a parameter must exist (check `ample_block_types`); do not invent them.
        - Never touch audio files; you cannot read them and must not rename or delete them.
        - Say plainly in your last message what you changed and why, and what you could not do.
        """;

    private static string Instructions(AiTask task) => $"""
        You are helping with a music project made with AMPLE, a language in which a song is a text file of blocks. Read TASK.md first. Use the skill `ample` and the MCP tools of the server `ample` (ample_project, ample_faer_status, ample_docs, ample_song, ample_block, ample_block_types, ample_file, ample_analyze, ample_request_asset, ample_web_search, ample_web_read, ample_browser_open, ample_browser_show, ample_browser_read, ample_browser_click, ample_browser_type, ample_browser_mouse_move, ample_browser_media_mute, ample_browser_screenshot, ample_browser_network_stats, ample_browser_js) to look things up and, when asked, browse an external public site; never open AMPLE or a local app in Playwright. Choose DuckDuckGo's free Instant Answers for lightweight lookups and Brave for useful full results when configured; avoid redundant searches. Brave is capped at 10 actual API calls per Codex turn and 1 per second. AMPLE reports an estimated monthly remaining count against 1,000 requests per UTC calendar month per API key, counting only calls made by AMPLE; it is not the live provider balance. Identical query/count/offset results may be reused from a five-minute memory-only cache, never persistent storage. Use ample_web_read for user-supplied or approved-search-result URLs without another approval; it sends no cookies and runs no JavaScript. If page reading needs JavaScript, sign-in, CAPTCHA or human input, ample_browser_open starts headless by default and switches to a visible window when manual interaction is needed; ample_browser_show can expose unexpected states. Browser actions need approval, credentials stay manual. Brave is never selected silently. State the query and wait for approval in each task. The same texts are in reference/.
        For path-based read tools, pass a separate `location` argument: `project` for project files or `work` for this task's own work files; keep `path` as the normal relative filename. ample_project lists task-work files separately. Never read `rpc.log` or hidden internal files. Strict mode is {(task.Request.Strict ? "on: stay within the task work folder and do not request outside access" : "off: if more access is needed, ask the owner and wait for an explicit approval before proceeding")}. This setting is separate from the manual/auto/unsafe interaction choice.
        For a Dutch or Flemish ACE request asking for the best or quality-first result, start with the highest-precision compatible model listed in the live Faer inventory, currently XL SFT BF16 when present, by setting its exact filename through `synthModelFile`. Do not require ACE `/props` to advertise the inventory-listed filename first; the render attempt establishes acceptance. If BF16 fails, retain and report its raw failure reason, then retry with XL SFT Q5_K_M as an explicit fallback. Explain that BF16's roughly 10 GB weights leave limited headroom on a 16 GB GPU and that runtime success and perceived sound quality are not yet verified. Never claim BF16 sounds better without a listening comparison, guess the exact filename, or silently substitute a rejected model.
        Every standalone `.amplet` that contains `ace-music` or `ai-voice` must explicitly set per-song `acestep.backend` to `server` in its settings line, including each baseline and every seed variant; a project default does not satisfy this per-song requirement. Add this before checking or rendering, and apply it identically across comparable variants. This selects ACE's server backend; the project's separate local/Faer capability route determines where that server request runs, and a block can override the route and the model with its own `engine` parameter (for example `engine "faer:xl-sft"` or `engine "local:turbo"`; a model needs a place, a voice-clone block takes only a place, and an explicit faer that cannot be reached stops that block with an error instead of falling back to local). If a build reports AMP5370 or AMP2013, fix the missing setting before diagnosing Faer, model compatibility or VRAM. Render Faer variants sequentially and wait for each to complete before submitting the next; the GPU chain is shared, and concurrent requests risk competing model loads.
        For a request to make music sound more Flemish, treat that as an open musical direction, not one fixed genre or a caricature. Follow details the user gives; do not invent dialect. Consider Flemish Dutch wording/prosody, a local/traditional or contemporary Flemish feel, or another quality. If more than one interpretation could fit, offer a few genuinely distinct short variants and present them with ample_present so the user can choose. You may suggest an optional multi-step workflow (for example, explore contrasting directions, compare them, then refine the chosen one) and use extra iterations when helpful. Do not add rounds to a narrow request or imply extra rounds guarantee a more Flemish result; briefly explain the differences and let the user decide whether to continue.
        Work only inside ./project. Scratch work (a script that computes notes, a temporary file) goes in ./scratch inside your working folder, never in the system temp folder or anywhere else: writing outside the working folder asks the owner for permission. Prefer AMPLE's own way first (notes in the `notes` text of a midi-clip or in a sequence, written with ample_edit, a variant with a trial edit in ample_present); a script is only for real computation, and a score from outside is fetched with ample_fetch_url. Never write anywhere else on the computer: remarks about AMPLE (what is missing, what could be better, how to fix it) go to ample_findings (inside the project, no permission needed); a file somewhere else only after ample_request_access (a full path, why, minutes) was allowed, and then with ample_write_outside, never another way. A score as MIDI or MusicXML becomes a sequence with ample_import_score (docs: score-import) instead of your own conversion script; a PDF in the project is read with ample_file. When a tool says something is missing and gives a hint, tell the person and recommend the hint (ample_project_log lists them). Your model and your reasoning effort can be changed by the person while you work (they may say they made you smarter or faster): ample_project shows what you work with now (assistant.settings), and you are told when it changes; look it up when it matters to what you do. A question to the person with request_user_input_async takes `title` and `options` (a list of objects with label and description), not `question`. Never change ./reference: its documents are flat files (reference/language-03-syntax.md, reference/language-05-blocks-and-templates.md, reference/blocks.md, reference/midi.md, reference/sequencer.md, reference/modules.md); ample_docs gives the same documents by name. Keep every file valid AMPLE: a song needs its project line, block headers are `type name class@version`, a connection is `from block.output -> input`, and a comment is a line that starts with #.
        Honor an explicit owner exception to a general workflow instruction within the exact scope authorized in this conversation; do not ask again for the same authorization. Actual tool/sandbox permissions still apply. If a structured edit fails, read the current file, try a suitable structured operation, or use an explicitly authorized workspace edit inside the staged proposal; verify the resulting file and report the actual outcome.
        Do not run shell builds or long OS commands yourself; use AMPLE's ample_build, ample_listen and ample_present tools for requested renders. If the user asks you to create a song or a specified number of rendered variants, do not stop because there is no verified musical baseline yet: create the requested first version, render it, and present each successful sample immediately with ample_present before starting the next render; continue sequentially until the requested count is complete. Never wait until all variants finish before presenting the first available sample. A missing baseline only limits claims about comparative quality; it does not block making or rendering the requested music. Be clear about measured checks versus subjective listening, and never claim a render succeeded until the tool confirms it. If a render fails, preserve its raw reason and apply the documented fallback. Only call ample_skip_build when the user explicitly says not to build or render.
        Do not use arbitrary network or download commands. Fetch direct links through `ample_fetch_url`, which has its own approval. For live browsing use the AMPLE automated browser tools only for external public sites requested by the owner; never open AMPLE itself or a local app in Playwright. Permission may be task-only or persistent for this project and can be revoked in project permissions. The owner enters credentials manually. Numbered clicks block purchase labels, but explicitly requested page JavaScript can bypass that check and trigger arbitrary page actions; never use JavaScript to inspect or return credentials, tokens or cookies. Screenshot only on request. Network diagnostics return sanitized console messages and aggregate counts, plus exact URLs only for responses identified as audio; use an audio preview URL with `ample_fetch_url`, which has its own approval. Mouse movement only hovers; media mute changes HTML audio/video elements, not full tab audio. If you are unsure which instrument, block or part the user means, do not guess silently: present short clips of the candidates with ample_present and say which one you chose and why. Never guess a unit or a range: ample_block_types and ample_lookup say them. Before you finish, check your own work (and that the format is valid: ample_check checks every file you changed: the song, sequences, JSON): (1) read back what you changed (ample_batch with ample_block or ample_song) and compare it with every part of the request: does it do what was asked, in the right place, with the right values? (2) ample_check must say ok. (3) If the goal is about the arrangement or the length (place, length, a part, a slice), check it with ample_arrangement (measure=true when the real length matters). (4) If the change can be heard (levels, gains, effects, notes, tempo, which sounds are used), call ample_listen for the block that changed (or none for the whole song): it builds your proposal and measures it, next to the current project; check that the result sounds like what was asked (for example "20% quieter" is about -2 dB in the loudness) and say what you measured. A change that only touches comments or text needs no listening. Change files with `ample_edit` (never with PowerShell text replacement or regular expressions). Deliver without new problems: call ample_check when you are done editing; if it lists new errors or warnings, fix them and check again. AMPLE also checks and sends you back if you leave new problems. Be fast: batch your reads with `ample_batch`, do not re-read what you know, and tell the user what you are doing with `ample_thinking`. The user may send you messages while you work or after your answer ("a bit quieter"); treat each as the new instruction and answer quickly.
        {(task.Request.Kind == "explain" ? "This task only asks for an explanation: do not change any file." : "Finish with a short explanation (a few sentences) of what you changed and why. If you could not do something, say so plainly.")}
        """;

    /// <summary>
    /// What Codex may do while it works, as the real Codex settings. Codex only ever needs to write in its own temporary copy, so every mode keeps the sandbox on that copy: a request to explain
    /// something is even read-only (it cannot write at all). Unsafe / Auto-all only stops asking: everything the sandbox allows runs at once and what it does not allow is refused. The sandbox is
    /// only removed when the server says so on purpose (policy <c>unsafeNoSandbox</c>, off by default).
    /// </summary>
    internal static JsonObject ModeParameters(string mode, bool explain, AiPolicy policy, bool strict)
    {
        if (explain)
        {
            return new JsonObject { ["approvalPolicy"] = strict ? "never" : "on-request", ["sandbox"] = "read-only" };
        }

        if (!strict) return new JsonObject { ["approvalPolicy"] = "on-request", ["sandbox"] = "workspace-write" };

        return mode switch
        {
            "manual" => new JsonObject { ["approvalPolicy"] = "untrusted", ["sandbox"] = "workspace-write" },
            "unsafe" => new JsonObject { ["approvalPolicy"] = "never", ["sandbox"] = policy.UnsafeNoSandbox ? "danger-full-access" : "workspace-write" },
            _ => new JsonObject { ["approvalPolicy"] = "on-request", ["sandbox"] = "workspace-write" },
        };
    }

    private async Task RunAsync(AiTask task, AiPolicy policy)
    {
        StringBuilder rawLog = new();
        string logFile = Path.Combine(task.Workspace, "rpc.log");
        void Raw(string line)
        {
            lock (rawLog)
            {
                rawLog.AppendLine(line);
                if (rawLog.Length > 3_000_000) rawLog.Remove(0, 1_500_000);   // a long task keeps its most recent part only: the log is for diagnosing
            }
        }
        try
        {
            task.Status = "running";
            sessionStore.UpdateTask(task);
            sessionStore.SaveTask(task);
            string? home = HomeOf(task);
            List<string> extra = task.Identity == "codex-server" && !policy.AllowInheritance ? ["-c", "project_doc_max_bytes=0", "-c", "features.memories=false"] : [];
            ContextTokens[task.ContextToken] = task;
            extra.AddRange(NoOutsideControl);
            extra.AddRange(McpArguments(task));
            CodexRpc rpc = CodexRpc.Start(policy.CodexPath, home, task.Workspace, extra, Raw);
            task.Rpc = rpc;
            rpc.OnNotification = (method, p) => OnNotification(task, method, p);
            rpc.OnServerRequest = (method, p) => ServerRequestAsync(task, method, p);
            await rpc.InitializeAsync(task.Cancel.Token).ConfigureAwait(false);
            JsonNode? account = await rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(30), task.Cancel.Token).ConfigureAwait(false);
            if (account?["account"] is null)
            {
                throw new AmpleException("AMP5452", "Codex is not signed in for this project. Link the account in the project settings.");
            }

            JsonObject threadParams = ModeParameters(task.Mode, task.Request.Kind == "explain", policy, task.Request.Strict);
            threadParams["cwd"] = task.Workspace;
            threadParams["developerInstructions"] = Instructions(task);
            threadParams["ephemeral"] = false;
            string? model = task.Request.Model is null ? policy.ModelToUse : string.Equals(task.Request.Model, "default", StringComparison.OrdinalIgnoreCase) ? null : task.Request.Model;
            if (model is not null)
            {
                threadParams["model"] = model;
            }

            // Koda "hanging around": the new question goes into the Codex conversation of the earlier one, so "do that twice" or "undo it" is understood; if that thread cannot be opened a fresh one starts
            JsonNode? thread = null;
            if (task.Request.Kind == "koda" && !string.IsNullOrWhiteSpace(task.Request.ContinueFrom) && tasks.TryGetValue(task.Request.ContinueFrom, out AiTask? before) && before.Request.Kind == "koda" && before.Key == task.Key && before.ThreadId.Length > 0)
            {
                try
                {
                    if (before.Rpc is { } stale)
                    {
                        // its session is still open (it was only idle): closed first, or Codex refuses a second writer on the thread
                        before.Rpc = null;
                        before.PostTurn = false;
                        ContextTokens.TryRemove(before.ContextToken, out AiTask? _);
                        try { await stale.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* gone anyway */ }
                    }

                    thread = await rpc.RequestAsync("thread/resume", new JsonObject { ["threadId"] = before.ThreadId, ["cwd"] = task.Workspace, ["developerInstructions"] = threadParams["developerInstructions"]?.DeepClone(), ["approvalPolicy"] = threadParams["approvalPolicy"]?.DeepClone(), ["sandbox"] = threadParams["sandbox"]?.DeepClone() }, TimeSpan.FromSeconds(60), task.Cancel.Token).ConfigureAwait(false);
                    task.Add("info", "Koda went on in the Codex conversation of the earlier question.");
                }
                catch (AmpleException)
                {
                    thread = null;
                }
            }

            thread ??= await rpc.RequestAsync("thread/start", threadParams, TimeSpan.FromSeconds(60), task.Cancel.Token).ConfigureAwait(false);
            task.ThreadId = thread?["thread"]?["id"]?.GetValue<string>() ?? throw new AmpleException("AMP5453", "Codex did not start a thread.");
            sessionStore.SaveTask(task);
            task.Add("info", $"Codex started ({task.Identity}, {ModeLabel(task.Mode)}).");
            string failure = await TurnAsync(task, "Do the task described in TASK.md: " + task.Request.Prompt.Trim() + (task.Request.Kind == "koda" ? $"\n(The song of this task is `{task.Request.Song}`: read it with ample_song or ample_file, location project (the default) and exactly that path.)" : string.Empty) + AttachHums(task, task.Request.Hums) + OfferFiles(task, task.Request.Offered) + AttachFiles(task, task.Request.Attachments) + AttachMemory(task), policy).ConfigureAwait(false);
            if (failure.Length == 0)
            {
                failure = await RepairAsync(task, policy).ConfigureAwait(false);
            }

            if (failure.Length > 0)
            {
                throw new AmpleException("AMP5453", "Codex stopped: " + failure);
            }

            if (task.Summary.Length == 0)
            {
                task.Summary = task.MessageText.ToString().Trim();
            }

            RecordAssistantReply(task);
            if (task.Request.Kind == "koda") KeepKodaCopy(task);
        }
        catch (OperationCanceledException)
        {
            task.Status = "cancelled";
            task.Add("warn", "The task was cancelled. Nothing in the project changed.");
            EndSession(task);
            return;
        }
        catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            task.Status = "failed";
            task.Error = ex.Message;
            task.Add("error", ex.Message);
            RecordHistory(task, "failed");
            EndSession(task);
            return;
        }
        finally
        {
            if (task.Rpc is null)
            {
                ContextTokens.TryRemove(task.ContextToken, out AiTask? _);
            }

            try { lock (rawLog) { File.WriteAllText(logFile, rawLog.ToString()); } } catch (IOException) { /* the log is only for diagnosing */ }
        }

        try
        {
            await BuildProposalAsync(task).ConfigureAwait(false);
            RecordChanges(task);
            sessionStore.SaveTask(task);
            sessionStore.UpdateTask(task);
            ScheduleExpiry(task);
            DeliverQueuedMessages(task);
        }
        catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
        {
            task.Status = "failed";
            task.Error = "The proposal could not be prepared: " + ex.Message;
            task.Add("error", task.Error);
            sessionStore.SaveTask(task);
            sessionStore.UpdateTask(task);
        }
    }

    /// <summary>What the user is told to expect from Codex: quick answers, small edits, and nothing new that is wrong.</summary>
    private const string Delivery = "\n\nHow to work: be quick and focused. Read what you need in one go (ample_batch takes many read calls in one request), change as little as the request needs, and say what you are doing with ample_thinking. You are not finished until ample_check reports no new problems (errors or warnings that the song did not have before); fix them yourself and check again. The user may send a message while you work or after your answer to adjust the result ('a bit quieter', 'not that block'): take it as the new instruction and answer quickly.";

    private static void RecordAssistantReply(AiTask task)
    {
        string reply = Tidy(task, task.Summary.Trim());
        AiLog? lastSaid = task.Log.LastOrDefault(l => l.Kind == "assistant");
        if (reply.Length > 0 && lastSaid is not null && string.Equals(lastSaid.Text, reply.Length > 8000 ? reply[..8000] + "…" : reply, StringComparison.Ordinal))
        {
            return;   // the last message was shown when it came in
        }

        task.Add("assistant", reply.Length == 0 ? "Codex did not provide a text response." : reply);
    }

    private static void RecordChanges(AiTask task)
    {
        if (task.Changes.Count > 0)
        {
            task.Add("change-summary", $"Codex proposes changes to {task.Changes.Count} file(s).");
        }

        foreach (AiChange change in task.Changes)
        {
            task.AddChange(change);
        }
    }

    private static void OnNotification(AiTask task, string method, JsonObject p)
    {
        task.LastNotification = DateTimeOffset.UtcNow;
        switch (method)
        {
            case "item/agentMessage/delta":
                task.MessageText.Append(p["delta"]?.GetValue<string>());
                break;
            case "item/reasoning/summaryTextDelta":
                // the summary of what the model is reasoning about becomes the thought, when it did not say so itself a moment ago
                task.Reasoning.Append(p["delta"]?.GetValue<string>());
                if (task.Reasoning.Length > 0)
                {
                    string line = task.Reasoning.ToString().Replace("**", string.Empty, StringComparison.Ordinal).Trim().Split('\n')[0].Trim();
                    if (line.Length > 0 && (task.ThoughtSource != "tool" || DateTimeOffset.UtcNow - task.ThoughtAt > TimeSpan.FromSeconds(8)))
                    {
                        task.Thought = line.Length > 240 ? line[..240] + "…" : line;
                        task.ThoughtSource = "reasoning";
                    }
                }

                break;
            case "item/reasoning/summaryPartAdded":
                task.Reasoning.Clear();
                break;
            case "item/started" when (p["item"]?["type"]?.GetValue<string>() == "commandExecution"):
                task.AddOperation("command", $"running: {p["item"]?["command"]?.GetValue<string>()}", (JsonObject)p["item"]!);
                break;
            case "item/started" when (p["item"]?["type"]?.GetValue<string>() == "mcpToolCall"):
                {
                    JsonObject startedTool = (JsonObject)p["item"]!;
                    if (startedTool["id"]?.ToString() is { Length: > 0 } toolId) task.ToolStartTicks[toolId] = Stopwatch.GetTimestamp();
                    task.AddOperation("tool", $"{startedTool["tool"]}: running", startedTool);
                    break;
                }
            case "item/started" when (p["item"]?["type"]?.GetValue<string>() == "contextCompaction"):
                task.Compacting = true;
                task.Add("info", "Codex is compacting its memory of this conversation (it summarizes what it has so far to make room). A moment, it carries on by itself.");
                break;
            case "item/completed" when (p["item"]?["type"]?.GetValue<string>() == "contextCompaction"):
            case "thread/compacted":
                if (task.Compacting || method == "thread/compacted")
                {
                    task.Compacting = false;
                    task.Compactions++;
                    task.ContextUsed = 0;
                    task.ReadCache.Clear();   // the summary no longer holds the answers it read: the next read must give them again
                    task.Add("info", $"Codex compacted its conversation ({task.Compactions}x): older steps are summarized, the proposal and its files are unchanged.");
                }

                break;
            case "thread/tokenUsage/updated":
                {
                    JsonObject? usage = p["tokenUsage"] as JsonObject;
                    JsonNode? last = usage?["last"];
                    long used = (long?)last?["totalTokens"] ?? (long?)last?["inputTokens"] ?? 0;
                    if (used > 0)
                    {
                        task.ContextUsed = used;
                    }

                    long window = (long?)usage?["modelContextWindow"] ?? 0;
                    if (window > 0)
                    {
                        task.ContextWindow = window;
                    }

                    break;
                }
            case "item/completed":
                OnItem(task, p["item"] as JsonObject);
                break;
            case "error":
                task.Add("error", p["error"]?["message"]?.GetValue<string>() ?? "Codex reported an error.");
                break;
            case "turn/completed":
                string status = p["turn"]?["status"]?.GetValue<string>() ?? "completed";
                if (status == "interrupted" && task.StoppedByUser) { task.StoppedByUser = false; status = "completed"; }
                task.TurnDone?.TrySetResult(status == "completed" ? string.Empty : (status + ": " + (p["turn"]?["error"]?["message"]?.GetValue<string>() ?? "the turn did not complete")));
                break;
        }
    }

    /// <summary>
    /// Asks the running turn to carry on (a line that needs no answer). When Codex says it has no active turn, the turn ended without AMPLE hearing of it: a new turn is started so that the
    /// task does not wait for the full time out.
    /// </summary>
    private static async Task ProbeTurnAsync(AiTask task, AiPolicy policy)
    {
        try
        {
            await task.Rpc!.RequestAsync("turn/steer", new JsonObject { ["threadId"] = task.ThreadId, ["expectedTurnId"] = task.TurnId, ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "AMPLE status check: carry on with the task; this line needs no answer." }) }, TimeSpan.FromSeconds(20), task.Cancel.Token).ConfigureAwait(false);
        }
        catch (AmpleException ex) when (ex.Message.Contains("no active turn", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                task.BraveSearchCount = 0;
                JsonNode? started = await task.Rpc!.RequestAsync("turn/start", new JsonObject { ["threadId"] = task.ThreadId, ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Your previous turn ended before it was finished and without an answer. Look at the proposal as it is now (ample_project, ample_check), continue the request of the user where you stopped, and finish with a short summary. Do not repeat measurements of a proposal that has not changed." + Delivery }), ["effort"] = EffortOf(task, policy), ["model"] = task.ModelNow is { Length: > 0 } nowModel && !string.Equals(nowModel, "default", StringComparison.OrdinalIgnoreCase) ? nowModel : null, ["summary"] = "auto" }, TimeSpan.FromSeconds(60), task.Cancel.Token).ConfigureAwait(false);
                task.TurnId = started?["turn"]?["id"]?.GetValue<string>() ?? string.Empty;
                task.Add("warn", "Codex had stopped without finishing and without telling AMPLE. AMPLE started a new turn so the task goes on.");
            }
            catch (Exception retry) when (retry is AmpleException or TimeoutException or IOException)
            {
                task.Add("warn", "Codex had stopped and a new turn could not be started: " + retry.Message);
            }
        }
        catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException)
        {
            // another reason (a turn that is only slow): the check is not repeated for the next six minutes
        }
    }

    /// <summary>One turn of work in the Codex session: the text is sent, and the answer is waited for. Returns an empty text, or why the turn did not finish.</summary>
    /// <summary>
    /// A new turn forgets what the earlier turns read: the conversation of the model may no longer hold those answers (a thread that was started again after a restart, or a compacted one),
    /// and "unchanged since your last read" without the answer would leave it with nothing (Koda said it had no blocks to edit). Repeated reads inside one turn are still answered shortly.
    /// </summary>
    internal static void BeginTurnReads(AiTask task) => task.ReadCache.Clear();

    private static async Task<string> TurnAsync(AiTask task, string text, AiPolicy policy)
    {
        BeginTurnReads(task);
        task.TurnActive = true;
        try
        {
            return await TurnCoreAsync(task, text, policy).ConfigureAwait(false);
        }
        finally
        {
            task.TurnActive = false;
            task.PostTurn = true;
        }
    }

    private static async Task<string> TurnCoreAsync(AiTask task, string text, AiPolicy policy)
    {
        if (task.PendingContext.Length > 0)
        {
            text = task.PendingContext + "\n\n" + text;
            task.PendingContext = string.Empty;
        }

        task.TurnDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.MessageText.Clear();
        task.Reasoning.Clear();
        task.BraveSearchCount = 0;
        JsonNode? started = await task.Rpc!.RequestAsync("turn/start", new JsonObject { ["threadId"] = task.ThreadId, ["input"] = InputOf(task, text + Delivery), ["effort"] = EffortOf(task, policy), ["model"] = task.ModelNow is { Length: > 0 } nowModel && !string.Equals(nowModel, "default", StringComparison.OrdinalIgnoreCase) ? nowModel : null, ["summary"] = "auto" }, TimeSpan.FromSeconds(60), task.Cancel.Token).ConfigureAwait(false);
        task.TurnId = started?["turn"]?["id"]?.GetValue<string>() ?? string.Empty;
        task.LastNotification = DateTimeOffset.UtcNow;
        while (true)
        {
            task.Cancel.Token.ThrowIfCancellationRequested();
            Task finished = await Task.WhenAny(task.TurnDone.Task, Task.Delay(TimeSpan.FromSeconds(30), task.Cancel.Token)).ConfigureAwait(false);
            if (finished == task.TurnDone.Task)
            {
                break;
            }

            // Codex has said nothing for a long time and nobody is asked anything: ask whether the turn is still there, and start a new one when it is gone
            if (DateTimeOffset.UtcNow - task.LastNotification > TimeSpan.FromMinutes(6) && task.Approvals.IsEmpty)
            {
                task.LastNotification = DateTimeOffset.UtcNow;
                await ProbeTurnAsync(task, policy).ConfigureAwait(false);
            }
        }

        task.LastActive = DateTimeOffset.UtcNow;
        return await task.TurnDone.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Codex has answered; AMPLE looks at the song it left: errors or warnings that the song did not have before are sent back to Codex to fix (at most twice), so that what reaches the user does not come with new problems.
    /// </summary>
    private async Task<string> RepairAsync(AiTask task, AiPolicy policy)
    {
        task.Repairs = 0;
        while (task.Request.Kind != "explain" && task.Repairs < 2)
        {
            List<string> problems = NewProblems(task);
            if (problems.Count == 0)
            {
                break;
            }

            task.Repairs++;
            task.Add("warn", $"AMPLE found {problems.Count} new problem(s) in the proposal and sends Codex back to fix them (round {task.Repairs} of 2).");
            string failure = await TurnAsync(task, "AMPLE checked your result and found problems that were not there before (the format of a file, or errors and warnings of the song). Fix them, keep the rest as it is, and run ample_check again:\n- " + string.Join("\n- ", problems.Take(12)), policy).ConfigureAwait(false);
            if (failure.Length > 0)
            {
                return failure;
            }
        }

        // the last gate: does it build? Only a proposal that builds is handed in; when it does not, Codex gets why, block by block, with hints.
        // Koda is not held up by it: a small change would wait for a render of the whole song (minutes for a long one); the preview of a draft builds when the user asks for it
        task.BuildRepairs = 0;
        while (!task.SkipBuildRequested && task.Request.Kind is not ("explain" or "koda") && task.BuildRepairs < 1 && NewProblems(task).Count == 0)
        {
            await BuildProposalAsync(task, keepStatus: true).ConfigureAwait(false);
            if (task.Changes.Count == 0)
            {
                break;
            }

            for (int i = 0; i < 1800 && task.BuildStatus is "pending" or "running"; i++)
            {
                await Task.Delay(500, task.Cancel.Token).ConfigureAwait(false);
            }

            if (task.BuildStatus != "failed")
            {
                break;
            }

            task.BuildRepairs++;
            task.Add("warn", "The proposal does not build: Codex gets the details and is sent back to fix it.");
            string failure = await TurnAsync(task, "AMPLE built your proposal and it does not build. Here is why, block by block (with hints and the text of the block). Fix it with ample_edit, keep the rest as it is, then run ample_check and, if the sound can change, ample_build or ample_listen:\n" + BuildFailureText(task), policy).ConfigureAwait(false);
            if (failure.Length > 0)
            {
                return failure;
            }
        }

        return string.Empty;
    }

    /// <summary>The failure of a build in words for Codex: the message, the hint, what feeds the block and its text.</summary>
    private static string BuildFailureText(AiTask task)
    {
        StringBuilder text = new();
        foreach (JsonNode? d in task.BuildDetails)
        {
            text.Append("- block ").Append(d?["block"]).Append(" (").Append(d?["type"]).Append("): ").Append(d?["status"]).Append(" - ").Append(d?["message"]).Append('\n');
            if (d?["hint"] is JsonNode hint)
            {
                text.Append("  hint: ").Append(hint).Append('\n');
            }

            if (d?["fedBy"] is JsonArray fed && fed.Count > 0)
            {
                text.Append("  fed by: ").Append(string.Join("; ", fed.Select(x => x?.ToString()))).Append('\n');
            }

            if (d?["text"] is JsonNode block)
            {
                text.Append("  text:\n    ").Append(block.ToString().Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
            }
        }

        return text.Length > 0 ? text.ToString() : (task.BuildMessage.Length > 0 ? task.BuildMessage : "(no detail)");
    }

    private Dictionary<string, string> ProblemsOf(AiTask task, string text)
    {
        string root = Path.Combine(task.Workspace, "project");
        AmpleDocument doc = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(text, s.Spec);
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (Diagnostic d in doc.Diagnostics.Where(d => d.Severity is Severity.Error or Severity.Warning))
        {
            string key = $"{(d.Severity == Severity.Error ? "error" : "warning")} {d.Code}: {d.Message}";
            found.TryAdd(key, $"{key} (line {d.Line}){(string.IsNullOrWhiteSpace(d.Hint) ? string.Empty : " Hint: " + d.Hint)}");
        }

        // a sequence kept in a block of the song: a row of notes that cannot be read stops the build of that block, so it counts as a problem of the proposal
        foreach (BlockNode block in doc.Blocks.Where(b => b.Type == "sequencer"))
        {
            // a sequencer that reads a .ampleseq: its lanes must name channels of that file (a lane renamed to an instrument that is no channel stops the build: "the lane 2 names 'contrabass', which is not a channel")
            string? lanesText = block.Parameters.FirstOrDefault(p => p.Name == "lanes")?.Value;
            string? seqFile = block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(lanesText) && !string.IsNullOrWhiteSpace(seqFile) && seqFile.EndsWith(".ampleseq", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string folder in new[] { root, s.Root })
                    {
                        string full = Path.GetFullPath(Path.Combine(folder, seqFile));
                        if (full.StartsWith(Path.GetFullPath(folder), StringComparison.Ordinal) && File.Exists(full))
                        {
                            SequenceFile.Parse(File.ReadAllText(full)).ParseLanes(lanesText);
                            break;
                        }
                    }
                }
                catch (AmpleException ex)
                {
                    string laneKey = $"error {ex.DiagnosticCode}: {block.Name}: {ex.Message} (the lanes of a sequencer name the channels of its .ampleseq: to change an instrument change the program of the channel in that file, or swap lanes only between channels that exist)";
                    found.TryAdd(laneKey, laneKey);
                }
            }

            string? embedded = block.Parameters.FirstOrDefault(p => p.Name == "sequence")?.Value;
            if (string.IsNullOrWhiteSpace(embedded))
            {
                continue;
            }

            try
            {
                SequenceFile seq = SequenceFile.Parse(embedded);
                foreach (SeqPattern pattern in seq.Patterns)
                {
                    foreach ((string channel, string row) in pattern.Rows)
                    {
                        SeqChannel? c = seq.Channels.FirstOrDefault(x => x.Name == channel);
                        // read the row the way the sequencer does (SequenceRows: plain notes in the ticks of the channel, or a "= formula"), never with a parser of its own
                        if (c is { Type: "gm" or "notes" or "bass" or "piano" } && row.Trim().Length > 0)
                        {
                            int ppq = int.TryParse(c.Get("ppq"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedPpq) && parsedPpq is >= 1 and <= 1_000_000 ? parsedPpq : 1_000_000;
                            string tempo = (doc.Project?.Tempo ?? 120).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                            if (!SequenceRows.TryRead(row, ppq, tempo, out _, out string why))
                            {
                                string key = $"error AMP5380: {block.Name}: the notes of '{channel}' in the pattern '{pattern.Name}': {why}";
                                found.TryAdd(key, key + (SequenceRows.IsFormula(row) ? " (a formula row is = followed by a note pipeline; check it with ample_resolve_notes)" : " (a note is bar:beat:length:pitch:velocity, with the beat between 0 and 3.99; the next bar starts with 2:0)"));
                            }
                        }
                    }
                }
            }
            catch (AmpleException ex)
            {
                string key = $"error {ex.DiagnosticCode}: {block.Name}: the sequence cannot be read: {ex.Message}";
                found.TryAdd(key, key);
            }

            // a channel without a single note or step in any pattern makes no sound: a channel that a proposal adds that way is worth a warning (the preview would not sound different)
            try
            {
                SequenceFile seq = SequenceFile.Parse(embedded);
                foreach (SeqChannel c in seq.Channels.Where(c => !seq.Patterns.Any(p => p.Rows.TryGetValue(c.Name, out string? row) && row.Trim('.', ' ', '|').Length > 0)))
                {
                    string key = $"warning SEQ0001: {block.Name}: the channel '{c.Name}' has no notes or steps in any pattern, so it makes no sound";
                    found.TryAdd(key, key);
                }
            }
            catch (AmpleException)
            {
                // an unreadable sequence is already reported above
            }
        }

        return found;
    }

    /// <summary>The errors and warnings of the song in the temporary copy that the song did not have at the start.</summary>
    private List<string> NewProblems(AiTask task, string? path = null)
    {
        string root = Path.GetFullPath(task.ProjectDir) + Path.DirectorySeparatorChar;
        List<string> files = [];
        if (!string.IsNullOrWhiteSpace(path))
        {
            string rel = AiContext.Norm(path);
            files.Add(rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase) ? rel[8..] : rel);   // Codex works in the folder above ./project and names its files that way
        }
        else
        {
            // no file named: every file that was changed or added (a song, a sequence, a JSON file...)
            foreach (string f in Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(task.ProjectDir, f).Replace('\\', '/');
                if (!rel.StartsWith(".ample/", StringComparison.Ordinal) && (!task.Baseline.TryGetValue(rel, out string? was) || was != SafeRead(f)))
                {
                    files.Add(rel);
                }
            }
        }

        List<string> found = [];
        foreach (string rel in files.Distinct(StringComparer.Ordinal))
        {
            string file = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                continue;
            }

            string text = SafeRead(file);
            task.Baseline.TryGetValue(rel, out string? before);
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            try
            {
                if (ext == ".amplet")
                {
                    Dictionary<string, string> was = before is null ? [] : ProblemsOf(task, before);
                    found.AddRange(ProblemsOf(task, text).Where(p => !was.ContainsKey(p.Key)).Select(p => (files.Count > 1 ? rel + ": " : string.Empty) + p.Value));
                }
                else if (ext is ".seq" or ".ampleseq")
                {
                    string now = FormatError(ext, text), old = before is null ? string.Empty : FormatError(ext, before);
                    if (now.Length > 0 && now != old)
                    {
                        found.Add($"{rel}: the format is not valid: {now}");
                    }
                }
                else if (ext == ".json")
                {
                    string now = FormatError(ext, text), old = before is null ? string.Empty : FormatError(ext, before);
                    if (now.Length > 0 && now != old)
                    {
                        found.Add($"{rel}: the format is not valid: {now}");
                    }
                }
            }
            catch (Exception ex) when (ex is AmpleException or IOException)
            {
                found.Add($"{rel}: could not be checked ({ex.Message})");
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    private static string SafeRead(string file)
    {
        try
        {
            return new FileInfo(file).Length <= 2_000_000 ? File.ReadAllText(file) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Whether a file is valid in its own format (a sequence file, JSON): empty when it is, else what is wrong and where.</summary>
    private static string FormatError(string extension, string text)
    {
        try
        {
            if (extension == ".json")
            {
                using JsonDocument _ = JsonDocument.Parse(text);
            }
            else
            {
                _ = Ample.Audio.Engine.SequenceFile.Parse(text);
            }

            return string.Empty;
        }
        catch (AmpleException ex)
        {
            return $"{ex.DiagnosticCode}: {ex.Message}";
        }
        catch (JsonException ex)
        {
            return $"{ex.Message}";
        }
    }

    /// <summary>The tool <c>ample_edit</c>: structured edits of a file of the temporary copy (replace, insert, set a parameter, add or remove a block, connect), all or nothing, then the new problems.</summary>
    public JsonObject Edit(string token, JsonElement body)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Request.Kind == "explain")
        {
            throw new AmpleException("AMP5458", "This task only asks for an explanation: nothing is changed.");
        }

        string rel = AiContext.Norm(body.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : task.Request.Song);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[8..];
        }

        string root = Path.GetFullPath(Path.Combine(task.Workspace, "project")) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmpleException("AMP5458", $"{rel} is not a file of the proposal that can be edited.");
        }

        if (!body.TryGetProperty("edits", out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            throw new AmpleException("AMP5285", "give 'edits': a list like [{\"op\":\"set_param\",\"block\":\"band\",\"name\":\"gains\",\"value\":\"-5 -8\"}].");
        }

        bool staged = File.Exists(file);
        string live = ProjectPath.Resolve(s.Root, rel);
        bool existing = !staged && File.Exists(live);
        string before = staged ? File.ReadAllText(file) : existing ? File.ReadAllText(live) : string.Empty;
        AiEdit.Result result = AiEdit.Apply(before, JsonNode.Parse(list.GetRawText())!.AsArray(), s.Spec);
        if (existing) task.Baseline.TryAdd(rel, before);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, result.Text, new UTF8Encoding(false));
        if (body.TryGetProperty("why", out JsonElement whyNode) && whyNode.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(whyNode.GetString()))
        {
            string why = whyNode.GetString()!.Trim();
            task.ChangeReasons[rel] = why.Length > 500 ? why[..500] : why;
        }

        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", $"edited {rel}: {string.Join("; ", result.Summary)}");
        List<string> fresh = NewProblems(task, rel);
        string diff = AiDiff.Unified(before, result.Text, rel);
        bool changed = !string.Equals(before, result.Text, StringComparison.Ordinal);
        string[] warnings = [.. result.Summary.Where(x => x.StartsWith("WARNING:", StringComparison.Ordinal))];
        if (!changed) task.Add("warn", $"The edits to {rel} changed nothing (an empty diff).");
        return new JsonObject { ["ok"] = changed && fresh.Count == 0, ["changed"] = changed, ["warnings"] = new JsonArray([.. warnings.Select(x => (JsonNode)x)]), ["applied"] = result.Summary.Count, ["summary"] = new JsonArray([.. result.Summary.Select(x => (JsonNode)x)]), ["newProblems"] = new JsonArray([.. fresh.Select(x => (JsonNode)x)]), ["diff"] = diff.Length > 4000 ? diff[..4000] + "\n… (cut)" : diff, ["note"] = !changed ? "WARNING: these edits changed NOTHING: the file is exactly as before (an empty diff), so the request is NOT done yet. Check the edits (ops like set_flag, set_param or replace with different text) and try again." : fresh.Count > 0 ? "Applied, but these problems are new: fix them with another ample_edit." : warnings.Length > 0 ? "Applied as asked. Tell the user in your answer about each of the warnings." : "Applied. No new errors or warnings." };
    }

    /// <summary>Moves a text file within the staged proposal, without overwriting a destination.</summary>
    public static JsonObject MoveFile(string token, JsonElement body)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Request.Kind == "explain") throw new AmpleException("AMP5458", "An explanation task cannot move files.");
        string source = body.GetProperty("source").GetString() ?? string.Empty;
        string destination = AiContext.Norm(body.GetProperty("destination").GetString() ?? string.Empty);
        bool workspaceSource = source.StartsWith("workspace/", StringComparison.Ordinal);
        source = workspaceSource ? source[10..] : AiContext.Norm(source);
        if (workspaceSource && !source.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Workspace-root recovery is limited to misplaced song files.");
        string[] extensions = [".amplet", ".seq", ".ampleseq", ".lyr", ".lrc", ".amplelrc", ".chords", ".module", ".amplec", ".txt", ".md", ".csv", ".json"];
        if (!extensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase) || !extensions.Contains(Path.GetExtension(destination), StringComparer.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Only proposal text files can be moved.");
        if (workspaceSource && source != Path.GetFileName(source)) throw new AmpleException("AMP5458", "Workspace recovery accepts only a text file at the workspace root.");
        if (source.StartsWith(".ample", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(".ample", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Internal project files cannot be moved.");
        string from = ProjectPath.Resolve(workspaceSource ? task.Workspace : task.ProjectDir, source);
        string to = ProjectPath.Resolve(task.ProjectDir, destination);
        if (!File.Exists(from) || File.Exists(to)) throw new AmpleException("AMP5458", "Source must exist and destination must not exist; no overwrite is allowed.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", $"moved proposal text file to {destination}");
        return new JsonObject { ["ok"] = true, ["path"] = destination, ["note"] = "Moved only in the staged proposal. Run ample_check; the owner reviews project changes at Accept." };
    }

    private static readonly string[] CopyableTextExtensions = [".amplet", ".seq", ".ampleseq", ".lyr", ".lrc", ".amplelrc", ".chords", ".module", ".amplec", ".txt", ".md", ".csv", ".json"];
    private static readonly string[] WorkTextExtensions = [".amplet", ".ampleseq", ".seq", ".lyr", ".lrc", ".amplelrc", ".chordpro", ".chords", ".module", ".amplec", ".txt", ".md", ".csv", ".json"];

    // What ample_fetch_url can bring into assets/web/: see FetchTypes and FetchUrlAsync's extension mapping. Staging one of these into the project
    // never makes it text the AI model can edit; it just puts a reference asset (an image, a PDF, a fetched score) where the project can use or link it.
    private static readonly string[] CopyableAssetExtensions = [.. CopyableTextExtensions, ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".svg", ".mp3", ".mid", ".midi", ".mxl", ".musicxml"];

    /// <summary>Copies a text file within the staged proposal to a new path, without overwriting a destination; the source stays in place (unlike ample_move_file). A <c>workspace/</c>-prefixed source stages a file ample_fetch_url downloaded (images, PDF, text, MP3 preview) into the project.</summary>
    /// <summary>
    /// The beats catalogue, read-only and small by default: an overview, a filtered list of short summaries (limit, offset), similar entries, one entry or some of its parts, or
    /// several queries in one call (<c>queries</c> as JSON text). Everything arrives as query text.
    /// </summary>
    public static JsonObject Beats(IReadOnlyDictionary<string, string> args)
    {
        JsonObject query = [];
        foreach ((string key, string value) in args)
        {
            if (key == "queries")
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                try { query["queries"] = JsonNode.Parse(value) as JsonArray ?? throw new AmpleException("AMP5458", "queries must be a list of query objects."); }
                catch (JsonException) { throw new AmpleException("AMP5458", "queries is not valid JSON: give a list of query objects, for example [{\"genre\":\"rap\",\"form\":\"loop\"},{\"family\":\"country\"}]."); }
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                query[key] = value;
            }
        }

        return BeatsCatalog.Run(query);
    }

    /// <summary>
    /// SA01: a deterministic look at one audio file (a project file or <c>workspace/&lt;path&gt;</c> for a fetched preview): length, level, brightness, pitch of a single note, likely key, tempo and
    /// whether it is a loop of whole bars. Read-only; nothing is written. No AI and no GPU.
    /// </summary>
    public static JsonObject AnalyzeAudio(string token, string source, string? liveRoot = null)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        bool workspaceSource = source.StartsWith("workspace/", StringComparison.Ordinal);
        string name = workspaceSource ? source[10..] : AiContext.Norm(source);
        if (name.Length == 0) throw new AmpleException("AMP5458", "Give source: an audio file of the project, or workspace/<path> for a fetched preview.");
        if (!SampleSourceExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "The source must be an audio file (wav, mp3, flac, ogg, aiff or m4a).");
        if (name.StartsWith(".ample", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Internal project files cannot be analysed.");
        string from = ProjectPath.Resolve(workspaceSource ? task.Workspace : task.ProjectDir, name);
        if (!workspaceSource && !File.Exists(from) && liveRoot is not null)
        {
            string live = ProjectPath.Resolve(liveRoot, name);
            if (File.Exists(live)) from = live;
        }

        if (!File.Exists(from)) throw new AmpleException("AMP5458", $"There is no audio file '{source}' (check the path with ample_project, or use workspace/<path> for a fetched preview).");
        if (new FileInfo(from).Length > 200L * 1024 * 1024) throw new AmpleException("AMP5458", "That file is larger than 200 MB; cut it first.");
        byte[] wav = SampleSetTools.ReadAsWav(from);
        JsonObject result = AudioAnalyzer.Analyze(SampleSet.ReadWav(wav, 44100), 44100);
        result["source"] = source;
        task.LastActive = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>GN01: euclidean rhythms, grooves, arpeggios, basslines and fills from a small query (deterministic, with a seed). Read-only.</summary>
    public static JsonObject Generate(IReadOnlyDictionary<string, string> args) => PatternGenerators.Run(QueryObject(args));

    /// <summary>MT01: mix starting points (volume, pan, filter, EQ, compression, sends) per instrument and genre. Read-only.</summary>
    public static JsonObject MixHint(IReadOnlyDictionary<string, string> args) => MixHints.Run(QueryObject(args));

    private static JsonObject QueryObject(IReadOnlyDictionary<string, string> args)
    {
        JsonObject query = [];
        foreach ((string key, string value) in args)
        {
            if (!string.IsNullOrWhiteSpace(value)) query[key] = value;
        }

        return query;
    }

    /// <summary>
    /// AL01: the arrangement of a song of the proposal (which blocks play in which bars, how dense each bar is) and what looks wrong: silent bars, harmony without a bass, nothing that builds,
    /// the same layers for too long, every loop starting at bar 1. Reads only the placements, so it is instant. Without a path it takes the song the task changed.
    /// </summary>
    public JsonObject LintArrangement(string token, string path)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path ?? string.Empty);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase)) rel = rel[8..];
        if (rel.Length == 0)
        {
            string? changed = Directory.EnumerateFiles(task.ProjectDir, "*.amplet", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(task.ProjectDir, f).Replace('\\', '/'))
                .FirstOrDefault(f => !f.StartsWith(".ample/", StringComparison.Ordinal) && (!task.Baseline.TryGetValue(f, out string? was) || was != SafeRead(Path.Combine(task.ProjectDir, f))));
            rel = changed ?? throw new AmpleException("AMP5458", "Give path: the song (.amplet) to look at.");
        }

        string file = ProjectPath.Resolve(task.ProjectDir, rel);
        if (!File.Exists(file)) throw new AmpleException("AMP5458", $"There is no song '{rel}' in the proposal (check the path with ample_project).");
        string root = Path.Combine(task.Workspace, "project");
        AmpleDocument doc = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(File.ReadAllText(file), s.Spec);
        JsonObject result = ArrangementLint.Lint(doc);
        result["path"] = rel;
        return result;
    }

    private static readonly string[] SampleSourceExtensions = [".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff", ".m4a"];

    /// <summary>
    /// A sample of a sample set: the audio of a fetched preview or a project file as a WAV in a folder of the staged proposal (never overwriting), optionally with the loop points of
    /// that sample and the set's envelope written into the folder's set.json. Nothing in the live project changes until the proposal is accepted.
    /// </summary>
    public static JsonObject SampleSetAdd(string token, JsonElement body, string? liveRoot = null)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Request.Kind == "explain") throw new AmpleException("AMP5458", "An explanation task cannot add files.");
        string source = body.GetProperty("source").GetString() ?? string.Empty;
        string destination = AiContext.Norm(body.GetProperty("destination").GetString() ?? string.Empty);
        bool workspaceSource = source.StartsWith("workspace/", StringComparison.Ordinal);
        source = workspaceSource ? source[10..] : AiContext.Norm(source);
        if (!SampleSourceExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "The source must be an audio file (wav, mp3, flac, ogg, aiff or m4a).");
        if (!string.Equals(Path.GetExtension(destination), ".wav", StringComparison.OrdinalIgnoreCase) || !destination.Contains('/', StringComparison.Ordinal)) throw new AmpleException("AMP5458", "The destination must be a .wav inside a folder of the project, for example samples/campfire/c4.wav (the folder is the sample set).");
        if (source.StartsWith(".ample", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(".ample", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Internal project files cannot be used.");
        string from = ProjectPath.Resolve(workspaceSource ? task.Workspace : task.ProjectDir, source);
        if (!workspaceSource && !File.Exists(from) && liveRoot is not null)
        {
            string live = ProjectPath.Resolve(liveRoot, source);
            if (File.Exists(live)) from = live;
        }

        string to = ProjectPath.Resolve(task.ProjectDir, destination);
        if (!File.Exists(from)) throw new AmpleException("AMP5458", $"There is no audio file '{source}' to use (check the path with ample_project, or use workspace/<path> for a fetched preview).");
        if (File.Exists(to)) throw new AmpleException("AMP5458", $"The destination '{destination}' already exists in the proposal; no overwrite is allowed. Use another name (a layer is name_v2_r1.wav).");
        byte[] wav = SampleSetTools.ReadAsWav(from);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.WriteAllBytes(to, wav);
        JsonObject result = new() { ["ok"] = true, ["path"] = destination, ["bytes"] = wav.Length };
        bool wantLoop = body.TryGetProperty("loop", out JsonElement loopEl) && loopEl.ValueKind == JsonValueKind.True;
        JsonElement? envelope = body.TryGetProperty("envelope", out JsonElement envEl) && envEl.ValueKind == JsonValueKind.Object ? envEl : null;
        if (wantLoop || envelope is not null)
        {
            string folder = destination[..destination.LastIndexOf('/')];
            string settingsPath = ProjectPath.Resolve(task.ProjectDir, folder + "/" + SampleSet.SettingsFile);
            string existing = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : liveRoot is not null && File.Exists(ProjectPath.Resolve(liveRoot, folder + "/" + SampleSet.SettingsFile)) ? File.ReadAllText(ProjectPath.Resolve(liveRoot, folder + "/" + SampleSet.SettingsFile)) : string.Empty;
            LoopPoints? loop = wantLoop ? SampleSetTools.FindLoop(wav) : null;
            string baseName = SampleName.Parse(Path.GetFileNameWithoutExtension(destination).ToLowerInvariant()).Base;
            File.WriteAllText(settingsPath, SampleSetTools.MergeSettings(existing, baseName, loop, envelope));
            if (wantLoop) result["loop"] = loop is null ? new JsonObject { ["found"] = false, ["note"] = "The sample is too short or has no matching stretch: it plays as a one-shot." } : new JsonObject { ["found"] = true, ["start"] = loop.Start, ["end"] = loop.End, ["crossfade"] = loop.Crossfade, ["quality"] = loop.Quality, ["name"] = baseName };
            if (envelope is not null) result["envelope"] = JsonNode.Parse(envelope.Value.GetRawText());
            result["settings"] = folder + "/" + SampleSet.SettingsFile;
        }

        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", $"added {destination} to a sample set");
        result["note"] = "Added only in the staged proposal. Name the file by note (c3.wav, eb4.wav; a layer is c3_v2_r1.wav), point the block's or channel's set= at the folder, run ample_check.";
        return result;
    }

    public static JsonObject CopyFile(string token, JsonElement body, string? liveRoot = null)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Request.Kind == "explain") throw new AmpleException("AMP5458", "An explanation task cannot copy files.");
        string source = body.GetProperty("source").GetString() ?? string.Empty;
        string destination = AiContext.Norm(body.GetProperty("destination").GetString() ?? string.Empty);
        bool workspaceSource = source.StartsWith("workspace/", StringComparison.Ordinal);
        source = workspaceSource ? source[10..] : AiContext.Norm(source);
        string[] extensions = workspaceSource ? CopyableAssetExtensions : CopyableTextExtensions;
        if (!extensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase) || !extensions.Contains(Path.GetExtension(destination), StringComparer.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", workspaceSource ? "Only an image, PDF, text file, MIDI or MusicXML score or MP3 audio preview fetched by AMPLE can be staged this way. A fetched score is staged into the project (for example scores/<name>.mid) and then read with ample_import_score." : "Only proposal text files can be copied.");
        if (source.StartsWith(".ample", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(".ample", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "Internal project files cannot be copied.");
        string from = ProjectPath.Resolve(workspaceSource ? task.Workspace : task.ProjectDir, source);
        // a file the task did not stage (a linked sequence of a scope-file task) is copied from the project as it is now
        if (!workspaceSource && !File.Exists(from) && liveRoot is not null)
        {
            string live = ProjectPath.Resolve(liveRoot, source);
            if (File.Exists(live)) from = live;
        }

        string to = ProjectPath.Resolve(task.ProjectDir, destination);
        if (!File.Exists(from)) throw new AmpleException("AMP5458", $"There is no source file '{source}' in the project to copy (check the path with ample_project).");
        if (File.Exists(to)) throw new AmpleException("AMP5458", $"The destination '{destination}' already exists in the proposal; no overwrite is allowed. Use it, change it with ample_edit, or give another name.");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", workspaceSource ? $"staged the fetched file {source} into the proposal as {destination}" : $"copied proposal text file {source} to {destination}");
        return new JsonObject { ["ok"] = true, ["path"] = destination, ["note"] = "Copied only in the staged proposal, with its full content. Point whatever should use the copy at it, run ample_check; the owner reviews project changes at Accept." };
    }

    /// <summary>
    /// What Codex reads through the tools is its own proposal: the file in its working folder when it has one (it may have changed it), else the current project. Without this a file it just edited
    /// would still read as it was, and Codex would think the edit failed.
    /// </summary>
    public static string? ProposalText(string token, string path)
        => ProposalText(token, path, "project");

    public static string? ProposalText(string token, string path, string location)
    {
        if (!ContextTokens.TryGetValue(token, out AiTask? task) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string rel = AiContext.Norm(path);
        if (location == "work")
        {
            if (Path.GetFileName(rel).Equals("rpc.log", StringComparison.OrdinalIgnoreCase) || rel.Split('/').Any(x => x.Length > 0 && x[0] == '.') || !WorkTextExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase)) return null;
            string workFile = ProjectPath.Resolve(task.Workspace, rel);
            // the copy of the project files is in the folder project/: a path as the song names it (without project/) is read from there
            if (!File.Exists(workFile) && !rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase) && File.Exists(ProjectPath.Resolve(task.Workspace, "project/" + rel))) workFile = ProjectPath.Resolve(task.Workspace, "project/" + rel);
            string current = Path.GetFullPath(task.Workspace);
            foreach (string part in Path.GetRelativePath(current, workFile).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            return File.Exists(workFile) && new FileInfo(workFile).Length <= 2_000_000 ? File.ReadAllText(workFile) : null;
        }
        if (location != "project") throw new AmpleException("AMP5285", "Location must be project or work.");
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[8..];
        }

        string root = Path.GetFullPath(task.ProjectDir) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file) && new FileInfo(file).Length <= 2_000_000 ? File.ReadAllText(file) : null;
    }

    public static string? ContextText(string token, string path, string location)
    {
        string? text = ProposalText(token, path, location);
        if (location == "work" && text is null) throw new AmpleException("AMP5458", $"There is no readable task-work file '{path}'.");
        return text;
    }

    /// <summary>The tool <c>ample_lookup</c>: more about units, a parameter, what a block reaches or a word, for the song as Codex has it now.</summary>
    public JsonObject Lookup(string token, string topic, string name, string query)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string? text = ProposalText(token, task.Request.Song);
        return AiContext.Lookup(s, topic, name, query, text);
    }

    /// <summary>
    /// The tool <c>ample_arrangement</c>: the arrangement of the song before the task and now (where the blocks sit, in bars and seconds, the slice of the master), what changed, and with measure=true the real
    /// length and peak of the built master of the proposal next to the current project, so Codex can check that the goal (a length, a place, a part) is reached.
    /// </summary>
    public async Task<JsonObject> ArrangementAsync(string token, bool measure, CancellationToken ct, string? path = null)
        => measure ? await WithinToolBudgetAsync(token, "arrangement|measure|" + path, work => ArrangementCoreAsync(token, true, work, path), ct).ConfigureAwait(false) : await ArrangementCoreAsync(token, false, ct, path).ConfigureAwait(false);

    private async Task<JsonObject> ArrangementCoreAsync(string token, bool measure, CancellationToken ct, string? path = null)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string song = SongOf(task, path);
        string beforeText = task.Baseline.TryGetValue(song, out string? b0) ? b0 ?? string.Empty : string.Empty;
        string afterText = ProposalText(token, song) ?? beforeText;
        JsonObject before = AiContext.Arrangement(s, beforeText), after = AiContext.Arrangement(s, afterText);
        JsonObject result = new() { ["song"] = song, ["before"] = before, ["after"] = after, ["changes"] = AiContext.ArrangementChanges(before, after) };
        if (measure)
        {
            JsonObject built = await ListenCoreAsync(token, string.Empty, "none", path, null, null, false, ct).ConfigureAwait(false);
            result["measuredAfter"] = built["audio"]?.DeepClone() ?? built.DeepClone();
            try
            {
                DocSession d = await s.DocAsync(song, ct).ConfigureAwait(false);
                AmpleDocument parsed = s.ParseExpanded(d);
                IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
                BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, s.NewControl(parsed, renderers), null, null) { PlanOnly = true }, ct).ConfigureAwait(false);
                result["measuredBefore"] = report.MasterTakeId is null ? null : AiContext.AudioFacts(s.Bank, report.MasterTakeId);
                result["note"] = report.MasterTakeId is null ? "The current project has no built master to compare with." : "Compare measuredAfter.seconds with what was asked; peakDbfs near 0 means no headroom; silence at the start or end is time without sound (above -60 dBFS counts as sound).";
            }
            catch (Exception ex) when (ex is AmpleException or IOException or InvalidOperationException)
            {
                result["note"] = "The current project could not be measured: " + ex.Message;
            }
        }
        else
        {
            result["note"] = "Add measure=true to build the proposal and get the real length of the master.";
        }

        return result;
    }

    // ---- the memory of the AI work on this project: what was asked, what came out, what was accepted ----

    private string HistoryFile => Path.Combine(Work, "history.jsonl");

    private static readonly object HistoryLock = new();

    /// <summary>Writes down where a request stands (a proposal that is ready, accepted, rejected, failed), so that later requests can refer to it and Codex can be given it again after the server was restarted.</summary>
    private void RecordHistory(AiTask task, string status) => RecordHistory(task, status, null);

    private void RecordHistory(AiTask task, string status, string? historyRevision)
    {
        try
        {
            reduxTasks.RecordTask(task, status, historyRevision);
            JsonObject record = new()
            {
                ["id"] = task.Id,
                ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["song"] = task.Request.Song,
                ["kind"] = task.Request.Kind,
                ["prompt"] = ReduxTaskArchive.Redact(task.Request.Prompt.Length > 1500 ? task.Request.Prompt[..1500] + "…" : task.Request.Prompt),
                ["status"] = status,
                ["historyRevision"] = historyRevision,
                ["summary"] = ReduxTaskArchive.Redact(task.Summary.Length > 1200 ? task.Summary[..1200] + "…" : task.Summary),
                ["changes"] = new JsonArray([.. task.Changes.Select(c => (JsonNode)new JsonObject { ["path"] = ReduxTaskArchive.Redact(c.Path), ["added"] = c.Added, ["removed"] = c.Removed, ["status"] = c.Status, ["why"] = c.Why is null ? null : ReduxTaskArchive.Redact(c.Why) })]),
                ["messages"] = new JsonArray(),
                ["offered"] = new JsonArray([.. task.Offered.Select(o => (JsonNode)ReduxTaskArchive.Redact(o.Name))]),
                ["clips"] = new JsonArray([.. task.ClipsNow().Select(c => (JsonNode)ClipJson(c))]),
            };
            Directory.CreateDirectory(Work);
            lock (HistoryLock)
            {
                File.AppendAllText(HistoryFile, record.ToJsonString() + "\n");
                string[] lines = File.ReadAllLines(HistoryFile);
                if (lines.Length > 600)
                {
                    File.WriteAllLines(HistoryFile, lines.Skip(lines.Length - 400));
                }
            }

            if (task.Changes.Count > 0 && status is "ready" or "accepted")
            {
                string folder = Path.Combine(Work, "history");
                Directory.CreateDirectory(folder);
                string diff = string.Join("\n", task.Changes.Select(c => AiDiff.Unified(c.Before ?? string.Empty, c.After ?? string.Empty, c.Path)));
                File.WriteAllText(Path.Combine(folder, task.Id + ".diff"), diff.Length > 14000 ? diff[..14000] + "\n… (cut)" : diff);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the memory is a help: a disk problem must not stop the work
        }
    }

    /// <summary>The earlier requests of this project (newest first, one entry per request), optionally for one song or with a word in them.</summary>
    public JsonArray History(int limit, string? song, string? query)
    {
        if (!File.Exists(HistoryFile))
        {
            return [];
        }

        Dictionary<string, JsonObject> latest = new(StringComparer.Ordinal);
        string[] lines;
        lock (HistoryLock)
        {
            lines = File.ReadAllLines(HistoryFile);
        }

        foreach (string line in lines)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o && o["id"]?.GetValue<string>() is { } id)
                {
                    latest[id] = o;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // a damaged line is skipped
            }
        }

        IEnumerable<JsonObject> list = latest.Values.OrderByDescending(o => o["at"]?.GetValue<string>(), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(song))
        {
            list = list.Where(o => string.Equals(o["song"]?.GetValue<string>(), AiContext.Norm(song), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            list = list.Where(o => (o["prompt"]?.GetValue<string>() + " " + o["summary"]?.GetValue<string>()).Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return [.. list.Take(Math.Clamp(limit, 1, 40)).Select(o => (JsonNode)o.DeepClone())];
    }

    /// <summary>Read-only AMPLE Redux search scoped to the task, its session, or the whole project.</summary>
    public ValueTask<ReduxArchivePage> ReduxSearch(string token, ReduxArchiveQuery query, CancellationToken cancellationToken)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? current) ? current : throw new AmpleException("AMP5458", "This task is no longer running.");
        ReduxArchiveQuery scoped = query.Scope switch
        {
            "task" => query with { TaskId = string.IsNullOrWhiteSpace(query.TaskId) ? task.Id : query.TaskId, SessionId = null },
            "session" => query with { TaskId = null, SessionId = string.IsNullOrWhiteSpace(query.SessionId) ? task.SessionId ?? task.Id : query.SessionId },
            "project" => query,
            _ => throw new AmpleException("AMP5285", "AMPLE Redux scope must be task, session, or project."),
        };
        return ReduxArchive.SearchAsync(s.Journal, scoped, cancellationToken);
    }

    /// <summary>One earlier request in full, with the changes it made (as far as they were kept).</summary>
    public JsonObject HistoryOne(string id)
    {
        JsonObject? record = History(40, null, null).OfType<JsonObject>().FirstOrDefault(o => o["id"]?.GetValue<string>() == id);
        if (record is null)
        {
            return new JsonObject { ["found"] = false, ["note"] = $"There is no earlier request '{id}'." };
        }

        string diffFile = Path.Combine(Work, "history", id + ".diff");
        record["diff"] = File.Exists(diffFile) ? File.ReadAllText(diffFile) : null;
        record["found"] = true;
        return record;
    }

    /// <summary>The tool <c>ample_revision</c>: what one saved point of the project history changed against the point before it, and the diff of one file.</summary>
    public async Task<JsonObject> RevisionToolAsync(string reference, string path, CancellationToken ct)
    {
        Ample.Storage.History.NodeInfo node;
        try { node = s.History.Resolve(reference ?? string.Empty); }
        catch (AmpleException ex) { return new JsonObject { ["found"] = false, ["note"] = ex.Message + " Give the revision id (or its first characters), a branch name or a tag." }; }
        Ample.Storage.History.NodeInfo? before = node.ParentId is null ? null : s.History.Resolve(node.ParentId);
        JsonObject head = new() { ["id"] = node.NodeId, ["kind"] = node.Kind, ["message"] = node.Message, ["at"] = node.At.ToString("u", CultureInfo.InvariantCulture), ["parent"] = before?.NodeId, ["files"] = node.Files.Count };
        IReadOnlyList<Ample.Storage.History.FileChange> changes = before is null ? [.. node.Files.Keys.Order(StringComparer.Ordinal).Select(f => new Ample.Storage.History.FileChange(f, "added"))] : s.History.Diff(before.NodeId, node.NodeId);
        path = AiContext.Norm(path ?? string.Empty);
        if (path.Length == 0)
        {
            head["changed"] = new JsonArray([.. changes.Take(200).Select(c => (JsonNode)new JsonObject { ["path"] = c.Path, ["change"] = c.Change })]);
            head["note"] = "What this revision changed against the one before it. path=<file> gives that file's diff.";
            return head;
        }

        IReadOnlyDictionary<string, string> then = before is null ? new Dictionary<string, string>() : await s.History.PeekAsync(before.NodeId, ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> now = await s.History.PeekAsync(node.NodeId, ct).ConfigureAwait(false);
        string diff = AiDiff.Unified(then.GetValueOrDefault(path) ?? string.Empty, now.GetValueOrDefault(path) ?? string.Empty, path);
        head["path"] = path;
        head["diff"] = diff.Length > 20000 ? diff[..20000] + "\n… (cut)" : diff;
        return head;
    }

    /// <summary>The tool <c>ample_history</c>.</summary>
    public JsonObject HistoryTool(string id, string song, string query, int limit)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            return HistoryOne(id);
        }

        return new JsonObject { ["requests"] = History(limit > 0 ? limit : 8, song, query), ["note"] = "Newest first. ample_history id=<id> gives one request in full, with the changes it made. 'accepted' means the changes are in the project now; 'rejected' or 'ready' means they are not (check the current song with ample_song)." };
    }

    /// <summary>For the first turn: what the user asked before on this song (short), and, when the request continues an earlier one, that one in full.</summary>
    private string AttachMemory(AiTask task)
    {
        StringBuilder text = new();
        string? continued = task.Request.ContinueFrom;
        if (!string.IsNullOrWhiteSpace(continued) && HistoryOne(continued) is { } one && one["found"]?.GetValue<bool>() == true)
        {
            text.Append("\n\nThis request CONTINUES an earlier one from the user (").Append(one["at"]).Append("). Earlier the user asked: \"").Append(one["prompt"]).Append("\". Result: ").Append(one["status"]).Append(". What was said: ").Append(one["summary"]).Append('\n');
            if (one["diff"] is JsonNode diff)
            {
                text.Append("The changes of that request were:\n").Append(diff).Append('\n');
            }

            text.Append("It may or may not be in the project now: check the current song (ample_song) before you build on it.");
            task.Add("info", "Continues the earlier request " + continued + ": its prompt, result and changes were handed to Codex.");
        }

        JsonObject[] recent = [.. History(4, task.Request.Song, null).OfType<JsonObject>().Where(o => o["id"]?.GetValue<string>() != continued && o["id"]?.GetValue<string>() != task.Id).Take(3)];
        if (recent.Length > 0)
        {
            text.Append("\n\nEarlier AI requests of the user on this song (newest first; ample_history gives the details if the request refers to them):\n");
            foreach (JsonObject o in recent)
            {
                string p = o["prompt"]?.GetValue<string>() ?? string.Empty, sm = o["summary"]?.GetValue<string>() ?? string.Empty;
                text.Append("- ").Append(o["at"]?.GetValue<string>()?[..10]).Append(": \"").Append(p.Length > 140 ? p[..140] + "…" : p).Append("\" -> ").Append(o["status"]).Append(sm.Length == 0 ? string.Empty : ": " + (sm.Length > 160 ? sm[..160] + "…" : sm)).Append('\n');
            }
        }

        return text.ToString();
    }

    private string AttachmentDir => Path.Combine(Work, "attachments");

    /// <summary>Keeps an audio file the user offers (a WAV: dropped, picked or recorded) until it is offered to a task; nothing of it is in the project.</summary>
    public JsonObject SaveAttachment(byte[] wav, string name)
    {
        if (wav.Length > 60_000_000)
        {
            throw new AmpleException("AMP5470", "The file is too large (60 MB at most).");
        }

        JsonObject facts = HumTranscriber.Facts(wav);   // refuses what is not a WAV it can read
        string clean = new string([.. Path.GetFileNameWithoutExtension(name ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')]).Trim('_');
        clean = clean.Length == 0 ? "sound" : clean[..Math.Min(clean.Length, 40)];
        Directory.CreateDirectory(AttachmentDir);
        string id = Guid.NewGuid().ToString("N")[..12];
        File.WriteAllBytes(Path.Combine(AttachmentDir, id + ".wav"), wav);
        File.WriteAllText(Path.Combine(AttachmentDir, id + ".name"), clean + ".wav");
        facts["id"] = id;
        facts["name"] = clean + ".wav";
        facts["size"] = wav.Length;
        return facts;
    }

    public string AttachmentFile(string id) => id.Length == 12 && id.All(Uri.IsHexDigit) && File.Exists(Path.Combine(AttachmentDir, id + ".wav")) ? Path.Combine(AttachmentDir, id + ".wav") : throw new AmpleException("AMP5470", "There is no such audio file (it may have been removed).");

    /// <summary>Gives the audio the user offered to Codex: in its working folder (to read) and at its project path (so the song and the checks find it).</summary>
    private string OfferFiles(AiTask task, IEnumerable<string>? ids)
    {
        List<string> added = [];
        foreach (string id in (ids ?? []).Distinct(StringComparer.Ordinal).Take(12))
        {
            if (task.Offered.Any(o => o.Id == id) || task.Offered.Count >= 24)
            {
                continue;
            }

            string file = AttachmentFile(id);
            string name = File.ReadAllText(Path.Combine(AttachmentDir, id + ".name"));
            string path = "samples/offered/" + name;
            if (File.Exists(Path.Combine(s.Root, path.Replace('/', Path.DirectorySeparatorChar))) || task.Offered.Any(o => o.ProjectPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                path = "samples/offered/" + Path.GetFileNameWithoutExtension(name) + "-" + id[..4] + ".wav";
            }

            foreach (string target in new[] { Path.Combine(task.ProjectDir, path.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(task.Workspace, "assets", "offered", Path.GetFileName(path)) })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }

            double seconds = HumTranscriber.Facts(File.ReadAllBytes(file))["seconds"]?.GetValue<double>() ?? 0;
            task.Offered.Add(new OfferedFile { Id = id, Name = name, ProjectPath = path, Size = new FileInfo(file).Length, Seconds = seconds });
            added.Add(path);
        }

        if (added.Count > 0)
        {
            task.Add("file", $"The user offered {added.Count} audio file(s) to Codex: {string.Join(", ", added)}.");
        }

        return added.Count == 0 ? string.Empty : $"\n\nThe user offered {added.Count} audio file(s) as material: {string.Join(", ", added)}. They exist in your copy at that project path (and as raw files in ./assets/offered/), so a block can use them like any sample (for example file {added[0]}). They are only offered: when you use one, AMPLE asks the user at Accept whether it comes along into the project, so say in your answer which ones you used and why. The tool ample_offered tells what each one is (length, level, pitch). Do not copy or rename them.";
    }

    /// <summary>The tool <c>ample_offered</c>: the audio the user offered and what each file is.</summary>
    public static JsonObject Offered(string token)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        JsonArray list = [];
        foreach (OfferedFile o in task.Offered)
        {
            JsonObject facts = [];
            try { facts = HumTranscriber.Facts(File.ReadAllBytes(Path.Combine(task.ProjectDir, o.ProjectPath.Replace('/', Path.DirectorySeparatorChar)))); }
            catch (Exception ex) when (ex is AmpleException or IOException) { facts["note"] = "could not be read: " + ex.Message; }
            facts["id"] = o.Id;
            facts["projectPath"] = o.ProjectPath;
            facts["usedInTheProposal"] = ProposalUses(task, o);
            list.Add(facts);
        }

        return new JsonObject { ["offered"] = list, ["note"] = task.Offered.Count == 0 ? "The user offered no audio files." : "Reference a file by its projectPath. The user decides at Accept which of them come along into the project." };
    }

    private static bool ProposalUses(AiTask task, OfferedFile o)
    {
        foreach (string f in Directory.Exists(task.ProjectDir) ? Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories) : [])
        {
            string rel = Path.GetRelativePath(task.ProjectDir, f).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.StartsWith("samples/offered/", StringComparison.Ordinal) || rel.StartsWith(".ample/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = SafeRead(f);
            if (text.Contains(o.ProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Makes the offered files part of a staged project (a temporary one for building and measuring), without ever writing into the folders of the real project.</summary>
    private static void AddOfferedPlaceholders(AiTask task, Dictionary<string, string> proposed)
    {
        if (task.Offered.Count > 0)
        {
            proposed["samples/offered/.stage"] = string.Empty;   // makes samples/ and samples/offered/ real folders of the stage
        }
    }

    /// <summary>The binary files the proposal added to the working copy, such as the samples of a sample set. Only text files are part of the changes, so the temporary projects and Accept carry these separately; a file the real project already has is not one of them.</summary>
    private List<string> StagedBinaries(AiTask task)
    {
        HashSet<string> offered = new(task.Offered.Select(o => o.ProjectPath), StringComparer.Ordinal);
        List<string> found = [];
        foreach (string f in Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(task.ProjectDir, f).Replace('\\', '/');
            if (rel.StartsWith(".ample/", StringComparison.Ordinal) || rel.StartsWith("samples/offered/", StringComparison.Ordinal) || offered.Contains(rel) || File.Exists(ProjectPath.Resolve(s.Root, rel))) continue;
            FileInfo info = new(f);
            bool binary = info.Length > 2_000_000;
            if (!binary && info.Length > 0)
            {
                byte[] head = new byte[(int)Math.Min(8192, info.Length)];
                using (FileStream stream = File.OpenRead(f)) stream.ReadExactly(head);
                binary = Array.IndexOf(head, (byte)0) >= 0;
            }

            if (binary) found.Add(rel);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static string StagedSignature(AiTask task, IEnumerable<string> staged) =>
        string.Concat(staged.Select(r => "|" + r + ":" + new FileInfo(Path.Combine(task.ProjectDir, r.Replace('/', Path.DirectorySeparatorChar))).Length));

    /// <summary>Makes the folders of the staged binaries real folders of the stage (like the audio of a clip), so that copying a file never writes into a linked folder of the real project.</summary>
    private static void AddStagedBinaryFolders(Dictionary<string, string> proposed, IEnumerable<string> staged)
    {
        foreach (string rel in staged)
        {
            string dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty;
            if (dir.Length > 0) proposed[dir + "/.stage"] = string.Empty;
        }
    }

    private static void CopyStagedBinariesToStage(AiTask task, string stage, IEnumerable<string> staged)
    {
        foreach (string rel in staged)
        {
            string to = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(to)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(task.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar)), to, false);
        }
    }

    private static void CopyOfferedToStage(AiTask task, string stage)
    {
        foreach (OfferedFile o in task.Offered)
        {
            string from = Path.Combine(task.ProjectDir, o.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            string to = Path.Combine(stage, o.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
    }

    private string HumDir => Path.Combine(Work, "hums");

    /// <summary>Keeps a hummed recording (WAV) of the user: it is only handed to Codex when the user attaches it to a task or a message.</summary>
    public string SaveHum(byte[] wav)
    {
        if (wav.Length > 40_000_000)
        {
            throw new AmpleException("AMP5470", "The recording is too large (40 MB at most).");
        }

        _ = HumTranscriber.ReadWav(wav);   // refuses what is not a WAV it can read
        Directory.CreateDirectory(HumDir);
        string id = Guid.NewGuid().ToString("N")[..12];
        File.WriteAllBytes(Path.Combine(HumDir, id + ".wav"), wav);
        return id;
    }

    /// <summary>The file of a kept recording.</summary>
    public string HumFile(string id) => id.Length == 12 && id.All(Uri.IsHexDigit) && File.Exists(Path.Combine(HumDir, id + ".wav")) ? Path.Combine(HumDir, id + ".wav") : throw new AmpleException("AMP5470", "There is no such recording (it may have been removed).");

    /// <summary>Gives the recordings the user attached to Codex: the raw audio in its working folder (assets/hum), and a line in the instruction that says so.</summary>
    private AiAttachments? attachmentStore;

    /// <summary>Keeps a file the user attached (a screenshot, a score, a text) and returns the id that the message carries.</summary>
    internal AiAttachment SaveAttachment(string? name, byte[] data) => (attachmentStore ??= new AiAttachments(Path.Combine(s.Root, ".ample", "ai", "attachments"))).Save(name, data);

    /// <summary>
    /// What the user attached to a message: every file is copied into the working copy (attachments/), a picture is also queued for the next turn so that the model sees it,
    /// and the note that names them is returned for the text of the message.
    /// </summary>
    private string AttachFiles(AiTask task, IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return string.Empty;
        }

        AiAttachments store = attachmentStore ??= new AiAttachments(Path.Combine(s.Root, ".ample", "ai", "attachments"));
        List<string> lines = [];
        foreach (string id in ids.Distinct(StringComparer.Ordinal).Take(AiAttachments.MaxPerMessage))
        {
            AiAttachment? file = store.Find(id);
            if (file is null)
            {
                continue;
            }

            string dir = Path.Combine(task.Workspace, "attachments");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, file.Name);
            for (int n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(dir, Path.GetFileNameWithoutExtension(file.Name) + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(file.Name));
            }

            File.Copy(file.Path, target);
            if (file.IsImage)
            {
                lock (task.PendingImages)
                {
                    task.PendingImages.Add(target);
                }

                lines.Add($"- {Path.GetFileName(target)}: a picture, you can see it (it is also in the working copy: attachments/{Path.GetFileName(target)})");
            }
            else if (string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // sheet music and other PDFs: the pages become pictures, because the model can look at pictures
                IReadOnlyList<string> pages = AiAttachments.RenderPdf(target, dir);
                if (pages.Count > 0)
                {
                    lock (task.PendingImages)
                    {
                        task.PendingImages.AddRange(pages);
                    }

                    lines.Add($"- attachments/{Path.GetFileName(target)}: a PDF (for example sheet music); its {pages.Count} page(s) are pictures that you can see (attachments/{Path.GetFileNameWithoutExtension(target)}-page-N.png); read the music from those pictures yourself: ample_import_score needs Audiveris, which this machine usually does not have");
                }
                else
                {
                    lines.Add($"- attachments/{Path.GetFileName(target)} ({file.Size} bytes): a PDF that could not be turned into pictures on this machine; read it from the working copy if you have a tool for it");
                }
            }
            else
            {
                lines.Add($"- attachments/{Path.GetFileName(target)} ({file.Size} bytes): read it from the working copy");
            }
        }

        return lines.Count == 0 ? string.Empty : "\n\nThe user attached:\n" + string.Join('\n', lines);
    }

    /// <summary>The input of a turn: the text, and the pictures that were attached since the last turn.</summary>
    private static JsonArray InputOf(AiTask task, string text)
    {
        JsonArray input = new(new JsonObject { ["type"] = "text", ["text"] = text });
        lock (task.PendingImages)
        {
            foreach (string image in task.PendingImages)
            {
                input.Add(new JsonObject { ["type"] = "localImage", ["path"] = image });
            }

            task.PendingImages.Clear();
        }

        return input;
    }

    private string AttachHums(AiTask task, IEnumerable<string>? ids)
    {
        List<string> added = [];
        foreach (string id in (ids ?? []).Distinct(StringComparer.Ordinal).Take(6))
        {
            string file = HumFile(id);
            string target = Path.Combine(task.Workspace, "assets", "hum", id + ".wav");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            if (!task.Hums.Contains(id))
            {
                task.Hums.Add(id);
            }

            added.Add(id);
        }

        if (added.Count > 0)
        {
            task.Add("file", $"The user attached {added.Count} hummed melody recording(s) for Codex: {string.Join(", ", added)}.");
        }

        return added.Count == 0 ? string.Empty : $"\n\nThe user hummed what they mean and attached {added.Count} recording(s): ids {string.Join(", ", added)}. The raw audio is in ./assets/hum/<id>.wav. Use the tool ample_hum (id) to get the melody that AMPLE found in it (the notes, the key, and the notes text of a midi-clip at the tempo of the song); it is an attempt, so check it against the request, adjust tempo, grid or key in the tool when needed, and say how sure you are.";
    }

    /// <summary>The tool <c>ample_hum</c>: the melody found in an attached recording, at the tempo of the song or the one Codex asks for.</summary>
    public JsonObject Hum(string token, string id, double tempo, double grid, string key, int bar)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        if (task.Hums.Count == 0)
        {
            return new JsonObject { ["found"] = false, ["note"] = "The user did not attach a hummed melody to this task." };
        }

        string chosen = string.IsNullOrWhiteSpace(id) ? task.Hums[^1] : id;
        if (!task.Hums.Contains(chosen))
        {
            return new JsonObject { ["found"] = false, ["note"] = $"No recording '{chosen}'. Attached: {string.Join(", ", task.Hums)}." };
        }

        double songTempo = 120;
        try
        {
            string text = task.Baseline.TryGetValue(AiContext.Norm(task.Request.Song), out string? b) ? b ?? string.Empty : string.Empty;
            songTempo = Parser.Parse(text, s.Spec).Project?.Tempo is > 0 and double tp ? tp : 120;
        }
        catch (AmpleException)
        {
            songTempo = 120;
        }

        HumOptions options = new(tempo > 0 ? tempo : songTempo, grid >= 0 && grid <= 4 ? (grid == 0 ? 0.25 : grid) : 0.25, key ?? string.Empty, bar > 0 ? bar : 1);
        byte[] wav = File.ReadAllBytes(HumFile(chosen));
        JsonObject result = HumTranscriber.ToJson(HumTranscriber.Transcribe(wav, options));
        result["found"] = true;
        result["id"] = chosen;
        result["rawAudio"] = $"assets/hum/{chosen}.wav";
        result["usedTempo"] = options.Tempo;
        result["usedGrid"] = options.Grid;
        result["usedKey"] = string.IsNullOrWhiteSpace(options.Key) ? "none (notes as sung)" : options.Key;
        result["startBar"] = options.StartBar;
        result["howToUse"] = "notesText is the notes parameter of a midi-clip placed at the start bar (for example: midi-clip hum at 1 for 4 { notes \"<notesText>\" } feeding an instrument block). Re-run with tempo, grid (beats: 0.25 sixteenth, 0.5 eighth, 1 beat), key (auto or like 'A minor') or bar to change the result.";
        return result;
    }

    private static readonly SemaphoreSlim ListenGate = new(1, 1);

    /// <summary>
    /// The tool <c>ample_listen</c>: Codex cannot hear, so it measures. The proposal as it is now (the files of the temporary copy) is built in a temporary project of its own, the block (or the master)
    /// is analysed (loudness and spectrum by default), and the same block of the real project is measured too when it is built, so the change can be compared with what there was.
    /// </summary>
    public async Task<JsonObject> ListenAsync(string token, string block, string with, string? trialPath, JsonArray? trial, bool forceRender, CancellationToken ct)
        => await WithinToolBudgetAsync(token, $"listen|{block}|{with}|{trialPath}|{trial?.ToJsonString()}|force={forceRender}", work => ListenCoreAsync(token, block, with, trialPath, trial, null, forceRender, work), ct).ConfigureAwait(false);

    /// <summary>How long a measuring call waits for its answer before it tells the caller to ask again (a tool call of Codex is given up at 300 seconds).</summary>
    internal static TimeSpan ToolBudget { get; set; } = TimeSpan.FromSeconds(240);

    /// <summary>
    /// Runs a measuring call (a build of the proposal can take minutes) so that it never blocks a tool call for longer than <see cref="ToolBudget"/>. When the work is not done by then the caller
    /// is told it is still going on, the work continues in the background (stopped only when the task is cancelled), and the same call later joins it or gets the answer that is ready.
    /// </summary>
    private static async Task<JsonObject> WithinToolBudgetAsync(string token, string key, Func<CancellationToken, Task<JsonObject>> work, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        Task<JsonObject> running;
        lock (task.RunningTools)
        {
            if (task.ReadyTools.Remove(key, out JsonObject? ready))
            {
                return ready;
            }

            if (task.RunningTools.TryGetValue(key, out running!) && running.IsCompleted)
            {
                // it has finished and only its own clean-up has not run yet: an answer that nobody collected is handed over, in every other case this call is a new run
                task.RunningTools.Remove(key);
                if (!task.AbandonedTools.Remove(key)) running = null!;
            }

            if (running is null)
            {
                CancellationToken stop = task.Cancel.Token;
                running = Task.Run(() => work(stop), stop);
                task.RunningTools[key] = running;
                Task<JsonObject> own = running;
                _ = running.ContinueWith(_ =>
                {
                    lock (task.RunningTools)
                    {
                        if (task.RunningTools.TryGetValue(key, out Task<JsonObject>? now) && ReferenceEquals(now, own))
                        {
                            task.RunningTools.Remove(key);   // not the entry of a newer run of the same call
                        }
                    }

                    MemoryRelief.CollectWhenLarge();   // a build leaves large buffers behind
                }, TaskScheduler.Default);
            }
        }

        Stopwatch waited = Stopwatch.StartNew();
        Task first = await Task.WhenAny(running, Task.Delay(ToolBudget, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (first == running)
        {
            lock (task.RunningTools)
            {
                task.ReadyTools.Remove(key);   // the caller has the answer now
                task.AbandonedTools.Remove(key);   // and nobody has to keep it: without this a call that gave up earlier could store the same answer a moment later, for the next call
            }

            return await running.ConfigureAwait(false);
        }

        bool finished;
        lock (task.RunningTools)
        {
            finished = running.IsCompleted;
            if (!finished && task.AbandonedTools.Add(key))
            {
                // nobody waits for this one any more: keep its answer for the next identical call
                _ = running.ContinueWith(done =>
                {
                    lock (task.RunningTools)
                    {
                        if (!task.AbandonedTools.Remove(key)) return;   // somebody took the answer meanwhile
                        task.ReadyTools[key] = done.IsCompletedSuccessfully ? done.Result : new JsonObject { ["ok"] = false, ["built"] = false, ["why"] = done.Exception?.GetBaseException().Message ?? "The measuring call was stopped." };
                    }
                }, TaskScheduler.Default);
            }
        }

        if (finished)
        {
            return await running.ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["pending"] = true,
            ["waitedSeconds"] = (int)waited.Elapsed.TotalSeconds,
            ["note"] = "The build is still running (it takes longer than one tool call may last). It goes on in the background: call this tool again with the same arguments in a minute to get the result. Do not change the proposal in between, and do not start other measuring calls.",
        };
    }

    /// <summary>The other file of the proposal that holds the first line of an 'old' text of a trial edit, or null.</summary>
    internal static string? FileWithTextOfTrial(IReadOnlyDictionary<string, string> proposed, string rel, JsonArray trial)
    {
        foreach (JsonNode? edit in trial)
        {
            string old = edit is JsonObject o && o["old"] is JsonValue v ? v.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) : string.Empty;
            string first = old.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            if (first.Length < 8)
            {
                continue;
            }

            foreach ((string path, string text) in proposed)
            {
                if (!string.Equals(path, rel, StringComparison.OrdinalIgnoreCase) && text.Contains(first, StringComparison.Ordinal))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private async Task<JsonObject> ListenCoreAsync(string token, string block, string with, string? trialPath, JsonArray? trial, Action<Takebank, string, AmpleDocument>? capture, bool forceRender, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string song = SongOf(task, trialPath);
        task.OutputSong = song;
        string[] analysers = [.. (string.IsNullOrWhiteSpace(with) ? "loudness,spectrum" : with).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)];
        await ListenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> proposed = new(StringComparer.Ordinal);
            foreach (string f in Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(task.ProjectDir, f).Replace('\\', '/');
                byte[] bytes = await File.ReadAllBytesAsync(f, ct).ConfigureAwait(false);
                if (!rel.StartsWith(".ample/", StringComparison.Ordinal) && bytes.Length <= 2_000_000 && Array.IndexOf(bytes, (byte)0) < 0)
                {
                    proposed[rel] = new UTF8Encoding(false).GetString(bytes);
                }
            }

            // a trial: edits that are only applied in the temporary project of this call, so that a variant can be heard without changing the proposal
            string[] trialSummary = [];
            if (trial is { Count: > 0 })
            {
                string rel = AiContext.Norm(string.IsNullOrWhiteSpace(trialPath) ? task.Request.Song : trialPath);
                if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase))
                {
                    rel = rel[8..];
                }

                if (!proposed.TryGetValue(rel, out string? current))
                {
                    throw new AmpleException("AMP5458", $"{rel} is not a text file of the proposal, so the trial edits cannot be tried on it.");
                }

                AiEdit.Result tried;
                try
                {
                    tried = AiEdit.Apply(current, trial, s.Spec);
                }
                catch (AmpleException ex) when (ex.DiagnosticCode == "AMP5460" && FileWithTextOfTrial(proposed, rel, trial) is { } other)
                {
                    // the text is in another file of the proposal, typically the .ampleseq of a sequencer whose instruments are not in the song: say where it is
                    throw new AmpleException("AMP5460", $"{ex.Message} That text is in {other}, not in {rel}: give {other} as the `path` of the edit (an instrument of a sequencer is changed there with op sequence_channel).");
                }

                proposed[rel] = tried.Text;
                trialSummary = [.. tried.Summary];
            }

            string root = Path.Combine(Work, "validate", task.Id + "-listen");
            List<string> stagedBinaries = StagedBinaries(task);
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001', proposed.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + '\u0002' + x.Value)))));
            key += StagedSignature(task, stagedBinaries);
            if (task.ListenKey != key || !Directory.Exists(root))
            {
                if (Directory.Exists(root))
                {
                    DeleteStage(root);
                }

                using (OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), root, "ai-listen", null, ct).ConfigureAwait(false))
                {
                    _ = created;
                }

                try
                {
                    AddOfferedPlaceholders(task, proposed);
                    AddStagedBinaryFolders(proposed, stagedBinaries);
                    StageProject(s.Root, root, proposed);
                    CopyOfferedToStage(task, root);
                    CopyStagedBinariesToStage(task, root, stagedBinaries);
                }
                catch (StageException ex)
                {
                    throw new AmpleException("AMP5462", "The proposal could not be set up for measuring: " + ex.Message);
                }

                task.ListenKey = key;
            }

            AmpleDocument parsed = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(proposed.TryGetValue(song, out string? text) ? text : string.Empty, s.Spec);
            if (parsed.Diagnostics.Any(d => d.Severity == Severity.Error))
            {
                return new JsonObject { ["ok"] = false, ["built"] = false, ["why"] = "The proposed song has errors, so it cannot be built.", ["errors"] = new JsonArray([.. parsed.Diagnostics.Where(d => d.Severity == Severity.Error).Take(12).Select(d => (JsonNode)$"line {d.Line}: {d.Code} {d.Message}{(string.IsNullOrWhiteSpace(d.Hint) ? string.Empty : " Hint: " + d.Hint)}")]) };
            }

            string target = string.IsNullOrWhiteSpace(block) ? (parsed.Blocks.LastOrDefault(b => !b.Skipped && b.Type == "master")?.Name ?? string.Empty) : block;
            if (target.Length == 0 || parsed.Blocks.All(b => b.Name != target))
            {
                throw new AmpleException("AMP5458", $"There is no block called '{target}' in the proposal.");
            }

            if (forceRender && parsed.Blocks.First(b => b.Name == target).Type is not ("ace-music" or "ai-voice")) throw new AmpleException("AMP5605", "Force render requires one named ACE song or voice block.");

            string cacheKey = key + "|" + song + "|" + target + "|" + string.Join(',', analysers) + "|force=" + forceRender;
            if (capture is null && task.ListenResults.TryGetValue(cacheKey, out JsonObject? earlier))
            {
                JsonObject again = (JsonObject)earlier.DeepClone();
                again["repeated"] = true;
                again["repeatNote"] = "The proposal has not changed since this measurement, so nothing was built or measured again and this is the same answer. Change something first, then measure.";
                task.Add("tool", $"measured {target} again: the proposal is unchanged, the earlier answer was reused");
                return again;
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
            Takebank bank = Takebank.Open(s.Fs, s.Clock, root);
            bool exposeCache = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), ct).ConfigureAwait(false)).Settings.Ai.ExposeTakebankCache;
            Takebank? sourceCache = exposeCache && File.Exists(Path.Combine(s.Root, ProjectLayout.AmpleFolder, "history.db")) ? Takebank.OpenReadOnly(s.Fs, s.Clock, s.Root) : null;
            BuildEngine engine = new(bank, s.Spec, s.Manifest.Id, root, s.AmpleRoot, sourceCache);
            BuildReport report = await engine.BuildAsync(parsed, new BuildOptions("bake", null, target, 0, renderers, Override: forceRender ? new BlockOverride(target, null, null, true, null) : null), ct).ConfigureAwait(false);
            BlockResult? built = report.Blocks.FirstOrDefault(b => b.Block == target);
            if (built?.TakeId is null || built.Status is not ("rendered" or "cached"))
            {
                return new JsonObject { ["ok"] = false, ["built"] = false, ["why"] = $"The block {target} did not build in the proposal ({built?.Status ?? "not built"}).", ["failed"] = AiContext.BuildDetails(parsed, report, s.AmpleRoot) };
            }

            task.LastListen = (root, built.TakeId, target);
            capture?.Invoke(bank, built.TakeId, parsed);
            JsonObject? facts = null;
            try { facts = AiContext.AudioFacts(bank, built.TakeId); }
            catch (Exception ex) when (ex is AmpleException or IOException or InvalidOperationException) { facts = null; }
            if (string.Equals(with, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject { ["ok"] = true, ["built"] = true, ["block"] = target, ["takeId"] = built.TakeId, ["planId"] = bank.GetTake(built.TakeId).PlanId, ["buildStatus"] = built.Status, ["audio"] = facts, ["trial"] = trialSummary.Length == 0 ? null : new JsonArray([.. trialSummary.Select(x => (JsonNode)x)]), ["note"] = trialSummary.Length == 0 ? "The proposal builds (this block and what it needs)." : "The trial edits were applied only here: your proposal is unchanged. Apply them with ample_edit if you want them." };
            }

            JsonObject after = [];
            foreach (string a in analysers)
            {
                try { after[a] = JsonNode.Parse(new Ample.Plugins.Analysis.AnalysisService(bank, root).Analyze(built.TakeId, a, "{}").Json); }
                catch (Exception ex) when (ex is AmpleException or InvalidOperationException or IOException) { after[a] = "not available: " + ex.Message; }
            }

            JsonObject before = [];
            string beforeNote = string.Empty;
            foreach (string a in analysers)
            {
                try { before[a] = (await AiContext.AnalyzeAsync(s, song, target, a, ct).ConfigureAwait(false))["result"]?.DeepClone(); }
                catch (Exception ex) when (ex is AmpleException or InvalidOperationException or IOException)
                {
                    beforeNote = "The block of the current project is not built (or is new), so there is nothing to compare with.";
                    before.Clear();
                    break;
                }
            }

            task.Add("tool", $"measured {target} in the proposal ({string.Join(", ", analysers)})");
            JsonObject measured = new JsonObject { ["block"] = target, ["trial"] = trialSummary.Length == 0 ? null : new JsonArray([.. trialSummary.Select(x => (JsonNode)x)]), ["audio"] = facts, ["proposal"] = after, ["current"] = before.Count > 0 ? before : null, ["note"] = beforeNote.Length > 0 ? beforeNote : "Compare 'proposal' with 'current': a gain change of x dB shows as about x dB in the loudness." };
            if (capture is null)
            {
                if (task.ListenResults.Count >= 32) task.ListenResults.Clear();
                task.ListenResults[cacheKey] = (JsonObject)measured.DeepClone();
            }

            return measured;
        }
        finally
        {
            ListenGate.Release();
        }
    }

    /// <summary>
    /// The tool <c>ample_present</c>: Codex shows the user a piece of sound to listen to: the render of a block (or the master), whole or a slice of it, as the proposal has it now or as the project is.
    /// It appears in the task window as a card with a player, what it is and why it is shown, and it is written down in the history, so the user can later ask "play me what you thought I meant".
    /// </summary>
    public async Task<JsonObject> PresentAsync(string token, string block, double from, double to, string unit, string label, string why, string source, CancellationToken ct)
        => await PresentAsync(token, block, from, to, unit, label, why, source, null, null, false, ct).ConfigureAwait(false);

    /// <summary>Presents a temporary trial render with structured edits, without changing the proposal.</summary>
    public async Task<JsonObject> PresentAsync(string token, string block, double from, double to, string unit, string label, string why, string source, string? trialPath, JsonArray? trial, bool rebuildCurrent, CancellationToken ct)
        => await WithinToolBudgetAsync(token, $"present|{block}|{from}|{to}|{unit}|{label}|{source}|{trialPath}|{trial?.ToJsonString()}|{rebuildCurrent}", work => PresentCoreAsync(token, block, from, to, unit, label, why, source, trialPath, trial, rebuildCurrent, work), ct).ConfigureAwait(false);

    public JsonObject MarkClipObsoleteByToken(string token, string clipId, string reason)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? found) ? found : throw new AmpleException("AMP5458", "This task is no longer running.");
        return MarkClipObsolete(task, clipId, reason);
    }

    public JsonObject MarkClipObsolete(string taskId, string clipId, string reason) => MarkClipObsolete(GetTask(taskId), clipId, reason);

    private JsonObject MarkClipObsolete(AiTask task, string clipId, string reason)
    {
        string why = reason.Trim();
        if (why.Length is 0 or > 500) throw new AmpleException("AMP5458", "Give an obsolescence reason of 1 to 500 characters.");
        lock (task.ClipsLock)
        {
            AiClip clip = task.Clips.FirstOrDefault(item => item.Id == clipId) ?? throw new AmpleException("AMP5470", "There is no such sample.");
            clip.ObsoleteReason = why;
        }

        task.Add("info", $"Sample {clipId} marked obsolete: {why}");
        sessionStore.UpdateTask(task);
        return new JsonObject { ["clipId"] = clipId, ["obsoleteReason"] = why };
    }

    private async Task<JsonObject> PresentCoreAsync(string token, string block, double from, double to, string unit, string label, string why, string source, string? trialPath, JsonArray? trial, bool rebuildCurrent, CancellationToken ct)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        bool current = string.Equals(source, "current", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(source) && !current && !string.Equals(source, "proposal", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5458", "source must be proposal or current.");
        if (current && trial is { Count: > 0 }) throw new AmpleException("AMP5458", "Trial edits require source=proposal; the current project is read-only here.");
        if (rebuildCurrent && !current) throw new AmpleException("AMP5458", "rebuildCurrent requires source=current.");
        if (!current && SampleLimitReached(task)) return SampleLimitResult(task);
        string song = SongOf(task, trialPath);
        double tempo = 120;
        try
        {
            tempo = Parser.Parse(task.Baseline.TryGetValue(song, out string? b0) ? b0 ?? string.Empty : string.Empty, s.Spec).Project?.Tempo is > 0 and double tp ? tp : 120;
        }
        catch (AmpleException)
        {
            tempo = 120;
        }

        byte[]? wav = null;
        JsonArray settings = [];
        string[] trialSummary = [];
        string target = string.IsNullOrWhiteSpace(block) ? string.Empty : block;
        bool reconstructedCurrent = false;
        string? sourceTakeId = null;
        string? sourcePlanId = null;
        string sourceBuildStatus = string.Empty;
        string port = string.Empty;
        if (target.Contains('.', StringComparison.Ordinal))
        {
            port = target[(target.IndexOf('.', StringComparison.Ordinal) + 1)..];   // a lane or another output of the block: its own audio when the take keeps it
            target = target[..target.IndexOf('.', StringComparison.Ordinal)];
        }

        if (current)
        {
            DocSession d = await s.DocAsync(song, ct).ConfigureAwait(false);
            AmpleDocument parsed = s.ParseExpanded(d);
            tempo = parsed.Project?.Tempo is > 0 and double tp ? tp : tempo;
            settings = PresentationSettings(parsed);
            target = target.Length > 0 ? target : parsed.Blocks.LastOrDefault(b => !b.Skipped && b.Type == "master")?.Name ?? string.Empty;
            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
            string? take;
            if (rebuildCurrent)
            {
                string root = Path.Combine(Work, "validate", task.Id + "-current");
                string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(song + "\u0001" + d.Text)));
                if (task.CurrentListenKey != key || !Directory.Exists(root))
                {
                    if (Directory.Exists(root)) DeleteStage(root);
                    using (OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), root, "ai-current-listen", null, ct).ConfigureAwait(false)) { _ = created; }
                    StageProject(s.Root, root, new Dictionary<string, string>(StringComparer.Ordinal) { [song] = d.Text });
                    task.CurrentListenKey = key;
                }

                AmpleDocument staged = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(d.Text, s.Spec);
                Takebank bank = Takebank.Open(s.Fs, s.Clock, root);
                bool exposeCache = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), ct).ConfigureAwait(false)).Settings.Ai.ExposeTakebankCache;
                Takebank? sourceCache = exposeCache && File.Exists(Path.Combine(s.Root, ProjectLayout.AmpleFolder, "history.db")) ? Takebank.OpenReadOnly(s.Fs, s.Clock, s.Root) : null;
                BuildEngine engine = new(bank, s.Spec, s.Manifest.Id, root, s.AmpleRoot, sourceCache);
                Ample.Engine.Control.ControlService sourceControl = s.NewControl(staged, renderers);
                foreach (BlockNode pinned in staged.Blocks.Where(b => b.Freeze == "plan"))
                {
                    if (sourceControl.PinnedMelodyDataId(pinned.Name) is { } melodyId)
                    {
                        await using Stream melody = s.Bank.OpenData(melodyId);
                        _ = await bank.PutDataAsync(melody, ct).ConfigureAwait(false);
                    }
                }

                BuildReport report = await engine.BuildAsync(staged, new BuildOptions("bake", null, target, 0, renderers, Control: new IgnoreTakeSelections(sourceControl)), ct).ConfigureAwait(false);
                BlockResult? rendered = report.Blocks.FirstOrDefault(result => result.Block == target);
                take = rendered?.TakeId;
                wav = take is null ? null : AiContext.WavBytes(bank, take, port);
                sourceBuildStatus = rendered?.Status ?? "missing";
                sourceTakeId = take;
                sourcePlanId = take is null ? null : bank.GetTake(take).PlanId;
                reconstructedCurrent = true;
            }
            else
            {
                BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, s.NewControl(parsed, renderers), null, null) { PlanOnly = true }, ct).ConfigureAwait(false);
                take = report.Blocks.FirstOrDefault(b => b.Block == target)?.TakeId;
                wav = take is null ? null : AiContext.WavBytes(s.Bank, take, port);
                sourceBuildStatus = report.Blocks.FirstOrDefault(b => b.Block == target)?.Status ?? "missing";
                sourceTakeId = take;
                sourcePlanId = take is null ? null : s.Bank.GetTake(take).PlanId;
            }
            if (wav is null)
            {
                return new JsonObject { ["presented"] = false, ["note"] = rebuildCurrent ? $"The current-source rebuild of {target} produced no audio." : $"The block {target} of the current project has no built audio yet (it must be built first). Present it from the proposal instead (source=proposal)." };
            }
        }
        else
        {
            JsonObject built = await ListenCoreAsync(token, target, "none", trialPath, trial, (bank, take, parsed) =>
            {
                wav = AiContext.WavBytes(bank, take, port);
                sourceTakeId = take;
                sourcePlanId = bank.GetTake(take).PlanId;
                settings = PresentationSettings(parsed);
                tempo = parsed.Project?.Tempo is > 0 and double tp ? tp : tempo;
            }, false, ct).ConfigureAwait(false);
            if (built["ok"]?.GetValue<bool>() != true)
            {
                built["presented"] = false;
                return built;
            }

            target = built["block"]!.GetValue<string>();
            sourceBuildStatus = built["buildStatus"]?.GetValue<string>() ?? "unknown";
            trialSummary = built["trial"] is JsonArray summary ? [.. summary.Select(x => x!.GetValue<string>())] : [];
            if (wav is null)
            {
                return new JsonObject { ["presented"] = false, ["note"] = $"The block {target} makes no audio (it may be a notes or control block): present a block that sounds." };
            }
        }

        if (Slice(wav, from, to, string.Equals(unit, "bars", StringComparison.OrdinalIgnoreCase), 240.0 / tempo) is not { } cut)
        {
            return new JsonObject { ["presented"] = false, ["note"] = "The slice is empty (it starts after the end of the block, or is shorter than 50 ms)." };
        }

        string id = Guid.NewGuid().ToString("N")[..10];
        string folder = Path.Combine(Work, "clips", task.Id);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, id + ".wav");
        (double a, double z, double seconds) = (cut.From, cut.To, cut.Seconds);
        string audioHash = Convert.ToHexStringLower(SHA256.HashData(cut.Wav));
        AiClip? identical;
        AiClip clip;
        lock (task.ClipsLock)
        {
            if (!current && SampleLimitReached(task)) return SampleLimitResult(task);
            identical = task.Clips.FirstOrDefault(c => c.AudioHash.Length > 0 && c.AudioHash == audioHash);
            File.WriteAllBytes(file, cut.Wav);
            clip = new() { Id = id, Label = label.Trim().Length > 0 ? label.Trim() : target, Block = target, Source = reconstructedCurrent ? "current-rebuilt" : current && sourceBuildStatus == "frozen" ? "current-frozen" : current ? "current" : "proposal", From = a, To = z, Seconds = seconds, Why = why.Trim(), File = file, AudioHash = audioHash, IdenticalTo = identical?.Id, Settings = settings, Trial = trialSummary };
            task.Clips.Add(clip);
        }
        task.Add("audio", $"Codex presents \"{clip.Label}\": {target} ({clip.Source}) {clip.From:0.##}-{clip.To:0.##} s");
        sessionStore.UpdateTask(task);
        return new JsonObject { ["presented"] = true, ["clipId"] = id, ["block"] = target, ["from"] = clip.From, ["to"] = clip.To, ["seconds"] = clip.Seconds, ["audioHash"] = audioHash, ["identicalTo"] = identical?.Id, ["takeId"] = sourceTakeId, ["planId"] = sourcePlanId, ["buildStatus"] = sourceBuildStatus, ["reconstructedCurrent"] = reconstructedCurrent, ["settings"] = settings.DeepClone(), ["trial"] = new JsonArray([.. trialSummary.Select(x => (JsonNode)x)]), ["proposalChanged"] = false, ["note"] = reconstructedCurrent ? "This is a fresh build from the current song text in a temporary project. Selected take and pinned melody choices were bypassed; no source project files or takes were changed." : identical is null ? "The user can listen now. Settings list the rendered source parameters; trial edits affect only this clip. Labels and why do not alter sound." : $"This clip is byte-identical to '{identical.Label}' ({identical.Id}). Do not claim identical clips sound different: change the actual parameters with trial edits and present again." };
    }

    private static JsonArray PresentationSettings(AmpleDocument parsed) => new([.. parsed.Blocks.Where(b => !b.Skipped).Select(b => (JsonNode)new JsonObject { ["block"] = b.Name, ["type"] = b.Type, ["parameters"] = new JsonArray([.. b.Parameters.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["value"] = p.Value })]) })]);

    private static JsonObject ClipJson(AiClip c) => new() { ["id"] = c.Id, ["label"] = c.Label, ["block"] = c.Block, ["source"] = c.Source, ["from"] = c.From, ["to"] = c.To, ["seconds"] = c.Seconds, ["why"] = c.Why, ["obsoleteReason"] = c.ObsoleteReason, ["audioHash"] = c.AudioHash, ["identicalTo"] = c.IdenticalTo, ["settings"] = c.Settings.DeepClone(), ["trial"] = new JsonArray([.. c.Trial.Select(x => (JsonNode)x)]), ["mediaType"] = c.MediaType, ["workspaceAssetPath"] = c.WorkspaceAssetPath };

    private static bool SampleLimitReached(AiTask task) => task.ContinuationClipLimit is int limit && task.ClipsNow().Count(c => c.Source == "proposal") >= limit;
    private static JsonObject SampleLimitResult(AiTask task) => new() { ["presented"] = false, ["limitReached"] = true, ["limit"] = task.ContinuationClipLimit, ["note"] = "The original sample quantity has already been delivered. Continue cannot start another series; wait for a new explicit user request." };

    /// <summary>Cuts a piece out of a WAV: from and to in seconds (or bars, the last bar included), with a few milliseconds of fade at both ends so it does not click. Null when nothing is left.</summary>
    internal static (byte[] Wav, double From, double To, double Seconds)? Slice(byte[] wav, double from, double to, bool bars, double barSeconds)
    {
        Ample.Audio.Engine.StereoBuffer buffer;
        int rate;
        try
        {
            (buffer, rate) = Ample.Audio.Engine.StereoWav.Decode(wav);
        }
        catch (InvalidDataException)
        {
            // not stereo (a mono sound): read it as one channel and use it for both
            (float[] mono, rate) = HumTranscriber.ReadWav(wav);
            short[] pcm = [.. mono.Select(v => (short)Math.Clamp(v * 32767f, -32768f, 32767f))];
            buffer = new Ample.Audio.Engine.StereoBuffer(pcm, (short[])pcm.Clone());
        }

        double total = buffer.Length / (double)rate;
        double a = from, z = to;
        if (bars)
        {
            a = Math.Max(0, (from - 1) * barSeconds);
            z = to > 0 ? to * barSeconds : total;
        }

        a = Math.Clamp(a, 0, total);
        z = z <= 0 || z > total ? total : z;
        if (z - a < 0.05)
        {
            return null;
        }

        int i0 = (int)(a * rate), i1 = Math.Min(buffer.Length, (int)(z * rate)), n = i1 - i0, ramp = Math.Min(n / 2, rate / 200);
        short[] left = new short[n], right = new short[n];
        for (int i = 0; i < n; i++)
        {
            double gain = i < ramp ? i / (double)ramp : i >= n - ramp ? (n - 1 - i) / (double)ramp : 1;
            left[i] = (short)(buffer.Left[i0 + i] * gain);
            right[i] = (short)(buffer.Right[i0 + i] * gain);
        }

        return (Ample.Audio.Engine.StereoWav.Encode(new Ample.Audio.Engine.StereoBuffer(left, right), rate), Math.Round(a, 3), Math.Round(z, 3), Math.Round(n / (double)rate, 3));
    }

    public string ClipFile(string taskId, string clipId) => GetTask(taskId).ClipsNow().FirstOrDefault(c => c.Id == clipId) is { } c && File.Exists(c.File) ? c.File : throw new AmpleException("AMP5470", "There is no such clip (it may have been removed).");

    public string ClipMediaType(string taskId, string clipId) => GetTask(taskId).ClipsNow().FirstOrDefault(c => c.Id == clipId)?.MediaType ?? throw new AmpleException("AMP5470", "There is no such clip (it may have been removed).");

    /// <summary>The tool <c>ample_check</c>: the problems the proposal introduced so far, so that Codex can fix them before it answers.</summary>
    /// <summary>
    /// Reading the same thing twice while nothing changed is answered shortly ("unchanged since your last read") instead of with the whole answer again: Codex re-reads a lot after every round, and this
    /// saves its time and its context. What counts as a change: a file of its working folder (or the offered audio) that is newer, bigger or added since the last read of this task.
    /// </summary>
    public static async Task<JsonObject> CachedAsync(string token, string key, Func<Task<JsonObject>> make)
    {
        if (!ContextTokens.TryGetValue(token, out AiTask? task))
        {
            return await make().ConfigureAwait(false);
        }

        string stamp = WorkspaceStamp(task);
        if (task.ReadCache.TryGetValue(key, out (string Stamp, int Count) known) && known.Stamp == stamp)
        {
            task.ReadCache[key] = (stamp, known.Count + 1);
            return new JsonObject { ["unchanged"] = true, ["note"] = $"Nothing in your working folder changed since you read this ({Ordinal(known.Count + 1)} time). Use what you already have; read again only after you change something." };
        }

        JsonObject answer = await make().ConfigureAwait(false);
        task.ReadCache[key] = (stamp, 1);
        return answer;
    }

    internal static string WorkspaceStamp(AiTask task)
    {
        long ticks = 0, size = 0;
        int count = 0;
        if (Directory.Exists(task.ProjectDir))
        {
            foreach (string f in Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories))
            {
                FileInfo info = new(f);
                ticks = Math.Max(ticks, info.LastWriteTimeUtc.Ticks);
                size += info.Length;
                count++;
            }
        }

        return $"{count}:{size}:{ticks}";
    }

    public JsonObject Check(string token, string path)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        List<string> fresh = NewProblems(task, string.IsNullOrWhiteSpace(path) ? null : path);
        return new JsonObject { ["ok"] = fresh.Count == 0, ["newProblems"] = new JsonArray([.. fresh.Select(x => (JsonNode)x)]), ["message"] = fresh.Count == 0 ? "No new errors or warnings: the song is as clean as it was before." : "Fix these before you finish: they were not there before." };
    }

    /// <summary>The user goes on with a proposal: while Codex works the message steers it, afterwards it asks for a change on top of the proposal as it is now (the proposal is kept, extended and checked again).</summary>
    public AiTask SendMessage(string id, string text, IEnumerable<string>? hums = null, IEnumerable<string>? offered = null, IEnumerable<string>? attachments = null)
        => SendMessageCore(id, text, hums, offered, false, false, attachments);

    private AiTask SendMessageCore(string id, string text, IEnumerable<string>? hums, IEnumerable<string>? offered, bool continuation, bool alreadyLogged = false, IEnumerable<string>? attachments = null)
    {
        AiTask task = GetTask(id);
        string clean = text.Trim();
        if (clean.Length == 0)
        {
            throw new AmpleException("AMP5285", "Write what Codex should do.");
        }

        if (task.Rpc is null || task.Applied || task.Status is "rejected" or "failed" or "cancelled")
        {
            throw new AmpleException("AMP5458", "This proposal cannot take more messages (it was accepted, rejected or the Codex session has ended). Ask again.");
        }

        if (!continuation && QuantityRequest.IsRequest(clean)) task.ContinuationClipLimit = null;
        if (!alreadyLogged) task.Add("user", clean);
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        string humNote = AttachHums(task, hums);
        clean += humNote + OfferFiles(task, offered) + AttachFiles(task, attachments);
        if (task.Status is "running" or "waiting-approval" or "validating" && !task.TurnActive && task.PostTurn)
        {
            // Codex has finished its turn and AMPLE is checking and building the result: there is no turn to steer. The message is kept and goes to Codex as soon as that is done.
            lock (task.QueuedMessages)
            {
                task.QueuedMessages.Add(clean);
            }

            task.Add("info", "Codex is not working on a turn right now (AMPLE is checking and building the result). Your message is kept and goes to Codex as soon as that is done.");
            return task;
        }

        if (task.Status is "running" or "waiting-approval")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await task.Rpc.RequestAsync("turn/steer", new JsonObject { ["threadId"] = task.ThreadId, ["expectedTurnId"] = task.TurnId, ["input"] = InputOf(task, clean) }, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException)
                {
                    if (ex.Message.Contains("no active turn", StringComparison.OrdinalIgnoreCase))
                    {
                        // Codex is between two turns (or the turn it was steering is gone): the message starts a new turn on the same thread, and its end completes the wait of the task
                        try
                        {
                            AiPolicy policy = AiPolicy.Read();
                            task.BraveSearchCount = 0;
                            JsonNode? started = await task.Rpc.RequestAsync("turn/start", new JsonObject { ["threadId"] = task.ThreadId, ["input"] = InputOf(task, clean + Delivery), ["effort"] = EffortOf(task, policy), ["model"] = task.ModelNow is { Length: > 0 } nowModel && !string.Equals(nowModel, "default", StringComparison.OrdinalIgnoreCase) ? nowModel : null, ["summary"] = "auto" }, TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
                            task.TurnId = started?["turn"]?["id"]?.GetValue<string>() ?? string.Empty;
                            task.Add("info", "Codex was between two turns: your message started a new turn.");
                            return;
                        }
                        catch (Exception retry) when (retry is AmpleException or TimeoutException or IOException)
                        {
                            task.Add("warn", "Codex could not take the message: " + retry.Message);
                            return;
                        }
                    }

                    task.Add("warn", "Codex could not take the message while it works: " + ex.Message);
                }
            });
            return task;
        }

        if (task.Status != "ready")
        {
            throw new AmpleException("AMP5458", "The proposal is not ready for a new message yet.");
        }

        AiPolicy policy = AiPolicy.Read();
        task.Status = "running";
        task.BuildStatus = "none";
        task.SkipBuildRequested = false;
        task.SkipBuildReason = string.Empty;
        task.Thought = string.Empty;
        _ = Task.Run(async () =>
        {
            try
            {
                using IDisposable account = await CodexGate.AcquireAsync(task.Key, task.Cancel.Token).ConfigureAwait(false);
                string failure = await TurnAsync(task, "The user looks at your proposal as it is now (the files in ./project) and asks: " + clean, policy).ConfigureAwait(false);
                if (failure.Length == 0)
                {
                    failure = await RepairAsync(task, policy).ConfigureAwait(false);
                }

                if (failure.Length > 0)
                {
                    throw new AmpleException("AMP5453", "Codex stopped: " + failure);
                }

                task.Summary = Tidy(task, task.MessageText.ToString().Trim());
                RecordAssistantReply(task);
                await BuildProposalAsync(task).ConfigureAwait(false);
                RecordChanges(task);
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
                ScheduleExpiry(task);
                DeliverQueuedMessages(task);
            }
            catch (OperationCanceledException)
            {
                task.Status = "cancelled";
                EndSession(task);
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
            }
            catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                task.Status = "failed";
                task.Error = ex.Message;
                task.Add("error", ex.Message);
                EndSession(task);
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
            }
        });
        return task;
    }

    /// <summary>The tool <c>ample_memory_read</c>: the notes kept for later tasks, of the project or of the song of this task (outside the song).</summary>
    public JsonObject MemoryRead(string token, string scope)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        return AiMemory.Read(s.Root, (scope ?? string.Empty).Trim().ToLowerInvariant(), AiContext.Norm(task.Request.Song));
    }

    /// <summary>The tool <c>ample_memory_write</c>: adds a note to, or rewrites, the memory of the project or of the song of this task. Nothing of it enters a song or the proposal.</summary>
    public JsonObject MemoryWrite(string token, string scope, string text, string? mode)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        JsonObject written = AiMemory.Write(s.Root, (scope ?? string.Empty).Trim().ToLowerInvariant(), AiContext.Norm(task.Request.Song), text ?? string.Empty, mode, DateTimeOffset.Now);
        task.Add("info", $"Memory ({written["scope"]}) {(written["mode"]?.GetValue<string>() == "replace" ? "rewritten" : "note added")}.");
        return written;
    }

    /// <summary>Sends the messages that arrived while AMPLE was checking and building, as one new message, now that the proposal is ready.</summary>
    private void DeliverQueuedMessages(AiTask task)
    {
        task.PostTurn = false;
        string combined;
        lock (task.QueuedMessages)
        {
            if (task.QueuedMessages.Count == 0)
            {
                return;
            }

            combined = string.Join("\n\n", task.QueuedMessages);
            task.QueuedMessages.Clear();
        }

        try
        {
            SendMessageCore(task.Id, combined, null, null, false, alreadyLogged: true);
        }
        catch (AmpleException ex)
        {
            task.Add("warn", "A message that was waiting could not be sent to Codex (" + ex.Message + "). Send it again.");
        }
    }

    // Half an hour by default; AMPLE_AI_SESSION_IDLE_S (seconds) shortens it for tests.
    private static TimeSpan IdleLimit => int.TryParse(Environment.GetEnvironmentVariable("AMPLE_AI_SESSION_IDLE_S"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(30);

    /// <summary>A proposal that nobody answers for half an hour gives up its Codex process; the proposal itself stays and can still be accepted or rejected, only going on with it needs a new task.</summary>
    private void ScheduleExpiry(AiTask task)
    {
        task.LastActive = DateTimeOffset.UtcNow;
        DateTimeOffset mark = task.LastActive;
        _ = Task.Run(async () =>
        {
            await Task.Delay(IdleLimit).ConfigureAwait(false);
            if (task.LastActive == mark && task.Rpc is not null && task.Status == "ready")
            {
                task.Add("info", "The Codex session of this proposal ended after 30 minutes without a message. You can still accept or reject it.");
                EndSession(task);
                sessionStore.SaveTask(task);
                sessionStore.UpdateTask(task);
            }
        });
    }

    /// <summary>Ends the Codex session of a task (the process and the access of its tools).</summary>
    private static void EndSession(AiTask task)
    {
        CodexRpc? rpc = task.Rpc;
        task.Rpc = null;
        task.PostTurn = false;
        ContextTokens.TryRemove(task.ContextToken, out AiTask? _);
        if (rpc is not null)
        {
            _ = Task.Run(async () => { try { await rpc.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* the process is gone anyway */ } });
        }
    }

    private static string Compact(string text, int max)
    {
        string one = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length > max ? one[..max] + "…" : one;
    }

    private static void OnItem(AiTask task, JsonObject? item)
    {
        string type = item?["type"]?.GetValue<string>() ?? string.Empty;
        switch (type)
        {
            case "agentMessage":
                {
                    // what Codex says is shown at once, one entry per message, and not as one lump at the end of the turn; the last message is the summary of the turn
                    string said = item!["text"]?.GetValue<string>()?.Trim() ?? string.Empty;
                    if (said.Length > 0)
                    {
                        task.Summary = Tidy(task, said);
                        task.MessageText.Clear();
                        task.MessageText.Append(said);
                        task.Add("assistant", task.Summary);
                    }

                    break;
                }
            case "commandExecution":
                task.AddOperation("command", $"ran: {item!["command"]?.GetValue<string>()} (exit {item["exitCode"]})", item);
                break;
            case "mcpToolCall":
                string? toolId = item!["id"]?.ToString();
                long elapsedMs = toolId is not null && task.ToolStartTicks.TryRemove(toolId, out long start) ? (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds : 0;
                string toolError = item!["error"] is JsonNode e ? (e is JsonValue ? e.ToString() : e["message"]?.ToString() ?? e.ToJsonString()) : string.Empty;
                string toolArgs = item["arguments"] is JsonObject ao ? string.Join(", ", ao.Where(x => x.Key != "thinking" && x.Value is not null && x.Value.ToString().Length > 0).Select(x => x.Key + "=" + Compact(x.Value!.ToString(), 70))) : string.Empty;
                item["ampleElapsedMs"] = elapsedMs;
                string duration = elapsedMs < 1000 ? $"{elapsedMs} ms" : $"{(elapsedMs / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} s";
                task.AddOperation("tool", $"{item["tool"]?.GetValue<string>()}{(toolArgs.Length > 0 ? " (" + toolArgs + ")" : string.Empty)}: {item["status"]?.GetValue<string>()}{(elapsedMs > 0 ? " · " + duration : string.Empty)}{(toolError.Length > 0 ? " — " + (toolError.Length > 240 ? toolError[..240] : toolError) : string.Empty)}", item);
                break;
            case "fileChange":
                task.Add("file", "changed: " + string.Join(", ", (item!["changes"] as JsonArray ?? []).Select(c => c?["path"]?.GetValue<string>() ?? "?")));
                break;
        }
    }

    private async Task<JsonNode?> ServerRequestAsync(AiTask task, string method, JsonObject p)
    {
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            case "item/fileChange/requestApproval":
            case "item/permissions/requestApproval":
                {
                    bool command = method.Contains("command", StringComparison.Ordinal);
                    bool file = method.Contains("fileChange", StringComparison.Ordinal);
                    AiApproval ask = new()
                    {
                        Kind = command ? "command" : file ? "file change" : "permissions",
                        Summary = command ? (p["command"]?.GetValue<string>() ?? "a command") : file ? (p["reason"]?.GetValue<string>() ?? "Codex wants to change files in its copy") : (p["reason"]?.GetValue<string>() ?? "Codex asks for more permissions"),
                        Detail = $"{p["reason"]?.GetValue<string>() ?? string.Empty} {(p["cwd"] is JsonNode cwd ? "in " + cwd : string.Empty)}".Trim(),
                    };
                    // the emergency stop: a request that goes outside the working folder with no permission stops the run here, and the person decides
                    string? outside = OutsideTarget(task, method, p);
                    bool stop = outside is not null && !IsAllowedOutside(task, outside);
                    if (stop)
                    {
                        ask = new AiApproval { Kind = "stop", Summary = outside!, Detail = $"Codex tried to {(command ? "run something that writes" : "write")} outside its working folder, which is not allowed without your say-so. Allow it once, allow it for this project, or have Codex do it the correct way (it is told to use ample_findings or ample_request_access)." };
                    }

                    task.Approvals[ask.Id] = ask;
                    task.Status = "waiting-approval";
                    task.Add("approval", stop ? $"STOPPED: Codex tried to write outside the project: {outside}" : $"Codex asks to approve a {ask.Kind}: {ask.Summary}");
                    string decision;
                    try { decision = await ask.Decision.Task.WaitAsync(TimeSpan.FromMinutes(10), task.Cancel.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { decision = "cancel"; }
                    ask.Answered = decision;
                    task.Status = "running";
                    if (stop)
                    {
                        StopOutcome(task, outside!, decision);
                    }
                    else
                    {
                        task.Add("approval", decision is "accept" or "acceptForSession" ? "Approved." : "Denied: Codex continues without it.");
                    }
                    if (method.Contains("permissions", StringComparison.Ordinal))
                    {
                        return new JsonObject { ["permissions"] = new JsonObject() };
                    }

                    return new JsonObject { ["decision"] = decision };
                }

            case "mcpServer/elicitation/request":
                // Codex asks before it calls a tool of an MCP server that is not read-only: the tools of AMPLE are AMPLE's own and ask the user themselves where that is needed (an asset, a download),
                // so those are accepted; anything from another server stays declined
                return new JsonObject { ["action"] = p["serverName"]?.GetValue<string>() == "ample" ? "accept" : "decline" };
            case "item/tool/requestUserInput":
                return new JsonObject { ["answers"] = new JsonObject() };
            default:
                task.Add("warn", $"Codex asked for something AMPLE does not support ({method}); it was declined.");
                return new JsonObject();
        }
    }

    /// <summary>The answer of the user to an approval request of Codex: <c>accept</c>, <c>acceptForSession</c>, <c>decline</c> or <c>cancel</c>.</summary>
    public void Answer(string taskId, string approvalId, string decision)
    {
        AiTask task = GetTask(taskId);
        if (decision is not ("accept" or "acceptForSession" or "accept30" or "acceptForProject" or "decline" or "cancel"))
        {
            throw new AmpleException("AMP5285", "The decision must be accept, acceptForSession, accept30, acceptForProject, decline or cancel.");
        }

        if (!task.Approvals.TryGetValue(approvalId, out AiApproval? ask) || ask.Answered is not null)
        {
            throw new AmpleException("AMP5458", "There is no open request with that id.");
        }

        if (decision is "accept30" or "acceptForProject" && ask.Kind != "browser")
        {
            throw new AmpleException("AMP5285", "Time-limited and project-wide grants are available only for browser access.");
        }

        ask.Decision.TrySetResult(decision);
        if (decision == "cancel")
        {
            task.Cancel.Cancel();
        }
    }

    /// <summary>Stops a task. Nothing in the project changes.</summary>
    /// <summary>
    /// Stops the turn that Codex is working on and nothing else: the conversation (the thread) stays open, so the person can say what to do next in the same session. A task that is not working a turn is left alone.
    /// </summary>
    /// <param name="id">The task.</param>
    /// <returns>The task.</returns>
    public AiTask InterruptTask(string id)
    {
        AiTask task = GetTask(id);
        if (task.Status is not ("running" or "waiting-approval") || task.Rpc is null || !task.TurnActive || task.ThreadId.Length == 0 || task.TurnId.Length == 0)
        {
            return task;
        }

        foreach (AiApproval ask in task.Approvals.Values.Where(a => a.Answered is null))
        {
            ask.Decision.TrySetResult("cancel");
        }

        task.StoppedByUser = true;
        task.Add("info", "Stopped by the person: the conversation stays open.");
        _ = Task.Run(async () =>
        {
            try
            {
                await task.Rpc.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = task.ThreadId, ["turnId"] = task.TurnId }, TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AmpleException or TimeoutException or IOException)
            {
                task.StoppedByUser = false;
                task.Add("warn", "Codex could not stop the turn: " + ex.Message);
            }
        });
        return task;
    }

    /// <summary>
    /// Archives the conversation: a task that is working ends (nothing in the project changes), a task that waits for the person ends its Codex session and keeps its proposal. It moves to Archived in the list
    /// and can be resumed from there while its thread and working copy are kept.
    /// </summary>
    /// <param name="id">The task.</param>
    /// <returns>The task.</returns>
    public AiTask ArchiveTask(string id)
    {
        AiTask task = GetTask(id);
        task.Archived = true;
        if (task.Status is "queued" or "preparing" or "running" or "waiting-approval" or "validating")
        {
            CancelTask(id);
        }
        else if (task.Rpc is not null)
        {
            EndSession(task);
        }

        task.Add("info", "Archived by the person.");
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        return task;
    }

    public void CancelTask(string id)
    {
        AiTask task = GetTask(id);
        if (task.Status is not ("queued" or "preparing" or "running" or "waiting-approval" or "validating"))
        {
            return;
        }

        foreach (AiApproval ask in task.Approvals.Values.Where(a => a.Answered is null))
        {
            ask.Decision.TrySetResult("cancel");
        }

        task.Cancel.Cancel();
        task.Rpc?.StopNow();
        EndSession(task);
        task.Status = "cancelled";
    }

    // ---- the proposal ----

    private static void ClearBuildOutput(AiTask task)
    {
        task.AudioFile = null;
        task.BuildTakes.Clear();
        task.BuildBlocks.Clear();
        task.BuildFailures.Clear();
        task.BuildDetails = [];
    }

    private static string SongForBuild(AiTask task)
    {
        if (task.OutputSong is { Length: > 0 } outputSong && File.Exists(Path.Combine(task.ProjectDir, AiContext.Norm(outputSong).Replace('/', Path.DirectorySeparatorChar)))) return AiContext.Norm(outputSong);
        string[] addedSongs = task.Changes.Where(c => c.Status == "added" && c.Path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase)).Select(c => c.Path).ToArray();
        return addedSongs.Length == 1 ? addedSongs[0] : task.Request.Song;
    }

    private async Task BuildProposalAsync(AiTask task, bool keepStatus = false)
    {
        ClearBuildOutput(task);
        if (!keepStatus)
        {
            task.Status = "validating";
        }

        List<AiChange> fresh = [];
        Dictionary<string, string> now = new(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(task.ProjectDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(task.ProjectDir, f).Replace('\\', '/');
            if (rel.StartsWith(".ample/", StringComparison.Ordinal) || rel.StartsWith("samples/offered/", StringComparison.Ordinal) || rel.Split('/').Any(p => p is ".." or ""))
            {
                continue;
            }

            byte[] bytes = await File.ReadAllBytesAsync(f).ConfigureAwait(false);
            if (bytes.Length > 2_000_000 || Array.IndexOf(bytes, (byte)0) >= 0)
            {
                task.Add("warn", $"{rel} was ignored: it is not a text file of a reasonable size.");
                continue;
            }

            now[rel] = new UTF8Encoding(false).GetString(bytes);
        }

        foreach ((string rel, string after) in now.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (task.Baseline.TryGetValue(rel, out string? before))
            {
                if (before != after)
                {
                    (int add, int rem) = AiDiff.Count(before ?? string.Empty, after);
                    fresh.Add(new AiChange { Path = rel, Status = before is null ? "added" : "modified", Before = before, After = after, Added = add, Removed = rem, Why = task.ChangeReasons.GetValueOrDefault(rel) });
                }
            }
            else
            {
                fresh.Add(new AiChange { Path = rel, Status = "added", Before = null, After = after, Added = AiDiff.Lines(after).Length, Why = task.ChangeReasons.GetValueOrDefault(rel) });
            }
        }

        foreach ((string rel, string? before) in task.Baseline.Where(b => b.Value is not null && !now.ContainsKey(b.Key)))
        {
            fresh.Add(new AiChange { Path = rel, Status = "deleted", Before = before, After = null, Removed = AiDiff.Lines(before!).Length, Why = task.ChangeReasons.GetValueOrDefault(rel) });
        }

        task.Changes.Replace(fresh);
        await ValidateAsync(task).ConfigureAwait(false);
        if (task.SkipBuildRequested || task.Request.Kind == "koda")
        {
            // a Koda task is not built after its answer (the preview builds when asked), so nothing waits for a build that never starts
            task.BuildStatus = "skipped";
            task.BuildMessage = task.SkipBuildRequested ? "Build not run: " + task.SkipBuildReason : "Build not run: Koda changes are checked on the text; Preview builds them.";
        }
        else
        {
            task.BuildStatus = task.Changes.Count == 0 ? "none" : task.Diagnostics.Any(d => d.Contains(" error ", StringComparison.Ordinal)) ? "failed" : "pending";
            if (task.BuildStatus == "failed")
            {
                task.BuildMessage = "The proposal has errors in the song, so it cannot be built.";
            }
        }

        if (!keepStatus)
        {
            task.Status = "ready";
            RecordHistory(task, "ready");
        }

        if (!task.SkipBuildRequested) _ = Task.Run(async () => { await BuildCheckAsync(task).ConfigureAwait(false); });
    }

    private static string ProposedText(AiTask task, string path) => task.Changes.FirstOrDefault(c => c.Path == path) is { } c ? (c.After ?? string.Empty) : (task.Baseline.TryGetValue(path, out string? t) ? t ?? string.Empty : string.Empty);

    private Task ValidateAsync(AiTask task)
    {
        List<string> found = [];
        task.PolicyViolation = null;
        foreach (AiChange c in task.Changes.Where(c => c.After is not null && c.Path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase)))
        {
            AmpleDocument doc = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(c.After!, s.Spec);
            if (task.Baseline.TryGetValue(c.Path, out string? baselineText) && !string.IsNullOrEmpty(baselineText))
            {
                IReadOnlyList<string> widening = PolicyGuard.Widening(new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(baselineText, s.Spec), doc);
                if (widening.Count > 0)
                {
                    task.PolicyViolation = $"{c.Path}: " + string.Join("; ", widening);
                    found.Add($"{c.Path}:1 error AMP2080 The proposal widens the policy of the song ({string.Join("; ", widening)}). An assistant may only narrow capabilities and limits.");
                }
            }

            foreach (Diagnostic d in doc.Diagnostics.Where(x => x.Severity is Severity.Error or Severity.Warning).Take(40))
            {
                found.Add($"{c.Path}:{d.Line} {d.Severity.ToString().ToLowerInvariant()} {d.Code} {d.Message}");
            }
        }

        string song = ProposedText(task, task.Request.Song);
        if (!task.Changes.Any(c => c.Path == task.Request.Song) && !string.IsNullOrEmpty(song))
        {
            AmpleDocument doc = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(song, s.Spec);
            foreach (Diagnostic d in doc.Diagnostics.Where(x => x.Severity == Severity.Error).Take(20))
            {
                found.Add($"{task.Request.Song}:{d.Line} error {d.Code} {d.Message}");
            }
        }

        task.Diagnostics.Replace(found);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Builds the proposed song in a staged copy of the project (the project itself is not touched) so the result can be heard before it is accepted.</summary>
    private async Task BuildCheckAsync(AiTask task)
    {
        if (task.BuildStatus != "pending")
        {
            return;
        }

        ClearBuildOutput(task);
        task.BuildStatus = "running";
        Stopwatch buildTimer = Stopwatch.StartNew();
        string root = Path.Combine(Work, "validate", task.Id);
        task.ValidateRoot = root;
        try
        {
            Dictionary<string, string> proposed = new(StringComparer.Ordinal);
            foreach (string rel in task.Baseline.Keys.Concat(task.Changes.Where(c => c.After is not null).Select(c => c.Path)).Distinct(StringComparer.Ordinal))
            {
                string text = ProposedText(task, rel);
                if (task.Changes.Any(c => c.Path == rel && c.Status == "deleted"))
                {
                    continue;
                }

                proposed[rel] = text;
            }

            // the temporary project is kept while the proposal does not change, so that what was built earlier (and can be listened to) stays, and a new build only does what is missing
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('', proposed.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + '' + x.Value)))));
            List<string> stagedBinaries = StagedBinaries(task);
            key += StagedSignature(task, stagedBinaries);
            if (task.StagedKey != key || !Directory.Exists(root))
            {
                if (Directory.Exists(root))
                {
                    DeleteStage(root);
                }

                task.BuildTakes.Clear();
                task.BuildBlocks.Clear();
                using (OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), root, "ai-check", null, CancellationToken.None).ConfigureAwait(false))
                {
                    _ = created;
                }

                try
                {
                    AddOfferedPlaceholders(task, proposed);
                    AddStagedBinaryFolders(proposed, stagedBinaries);
                    StageProject(s.Root, root, proposed);
                    CopyOfferedToStage(task, root);
                    CopyStagedBinariesToStage(task, root, stagedBinaries);
                }
                catch (StageException ex)
                {
                    task.BuildStatus = "skipped";
                    task.BuildMessage = "The proposal was parsed but not built: " + ex.Message;
                    return;
                }

                task.StagedKey = key;
            }

            string song = SongForBuild(task);
            AmpleDocument parsed = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(ProposedText(task, song), s.Spec);
            if (parsed.Diagnostics.Any(d => d.Severity == Severity.Error))
            {
                task.BuildStatus = "failed";
                task.BuildMessage = "The proposed song has errors.";
                return;
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(CancellationToken.None).ConfigureAwait(false);
            Takebank bank = Takebank.Open(s.Fs, s.Clock, root);
            bool exposeCache = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), CancellationToken.None).ConfigureAwait(false)).Settings.Ai.ExposeTakebankCache;
            Takebank? sourceCache = exposeCache && File.Exists(Path.Combine(s.Root, ProjectLayout.AmpleFolder, "history.db")) ? Takebank.OpenReadOnly(s.Fs, s.Clock, s.Root) : null;
            BuildEngine engine = new(bank, s.Spec, s.Manifest.Id, root, s.AmpleRoot, sourceCache);
            task.BuildDone = 0;
            task.BuildTotal = parsed.Blocks.Count(b => !b.Skipped);
            task.BuildLive.Clear();
            BuildReport report = await engine.BuildAsync(parsed, new BuildOptions("bake", null, task.BuildOnly, 0, renderers, Progress: (block, status) => { task.BuildDone++; task.BuildLive.TryRemove(block, out _); }, Detail: step => task.BuildLive[step.Block] = step), task.Cancel.Token).ConfigureAwait(false);
            task.BuildLive.Clear();
            task.BuildDetails = AiContext.BuildDetails(parsed, report, s.AmpleRoot);
            TaskResult(task, report, bank, parsed);
        }
        catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            task.BuildStatus = "failed";
            task.BuildMessage = "The build check did not finish: " + ex.Message;
        }
        finally
        {
            long elapsedMs = buildTimer.ElapsedMilliseconds;
            task.BuildMessage += $" (completed in {(elapsedMs / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} s)";
        }
    }

    private void TaskResult(AiTask task, BuildReport report, Takebank bank, AmpleDocument parsed)
    {
        task.BuildFailures.Clear();
        // a master with nothing connected to it cannot build, whatever the proposal did: that is a loose end of the song, not a failure of the proposal (the preview then plays the mixer instead)
        HashSet<string> looseMasters = parsed.Blocks.Where(b => b.Type == "master" && !b.Froms.Any(f => !f.Disabled)).Select(b => b.Name).ToHashSet();
        if (looseMasters.Count > 0)
        {
            task.Add("warn", $"The master ({string.Join(", ", looseMasters)}) has no connected input, so the mix does not reach it. Connect the mixer to the master; the preview plays the mixer instead.");
        }

        foreach (BlockResult b in report.Blocks)
        {
            bool audio = false;
            if (b.TakeId is not null && b.Status is "rendered" or "cached")
            {
                try
                {
                    audio = AudioDataOf(bank, b.TakeId, null) is not null;
                }
                catch (Exception ex) when (ex is AmpleException or IOException)
                {
                    audio = false;
                }
            }

            task.BuildBlocks[b.Block] = (b.Status, audio);
            if (audio)
            {
                task.BuildTakes[b.Block] = b.TakeId!;
            }
        }

        foreach (BlockResult b in report.Blocks.Where(b => b.Status is "failed" or "skipped" or "cancelled" && !looseMasters.Contains(b.Block)))
        {
            task.BuildFailures.Add($"{b.Block}: {b.Status}{(string.IsNullOrEmpty(b.Message) ? string.Empty : " — " + b.Message)}");
        }

        string? listenTake = task.BuildOnly is null ? report.MasterTakeId : report.Blocks.FirstOrDefault(b => b.Block == task.BuildOnly)?.TakeId;
        if (listenTake is null && task.BuildOnly is null && looseMasters.Count > 0)
        {
            // nothing reaches the master: listen to the last mixer of the song that has sound, or else to the last block that has
            string? heard = parsed.Blocks.Where(b => b.Type == "mixer").Select(b => b.Name).Reverse().FirstOrDefault(n => task.BuildTakes.ContainsKey(n)) ?? parsed.Blocks.Select(b => b.Name).Reverse().FirstOrDefault(n => task.BuildTakes.ContainsKey(n));
            listenTake = heard is null ? null : task.BuildTakes[heard];
        }
        if (listenTake is null && task.BuildFailures.Count == 0)
        {
            task.BuildFailures.AddRange(report.Blocks.Where(b => b.Status is "disabled" or "deferred").Select(b => $"{b.Block}: {b.Status}{(string.IsNullOrEmpty(b.Message) ? string.Empty : " — " + b.Message)}"));
        }

        if (task.BuildFailures.Count > 0 || listenTake is null)
        {
            task.BuildStatus = "failed";
            task.BuildMessage = task.BuildFailures.Count > 0 ? "Some blocks did not build or have no active input." : task.BuildOnly is null ? "The proposed block chain produced no master audio." : $"The block {task.BuildOnly} made no audio.";
            return;
        }

        task.BuildStatus = "ok";
        task.BuildVersion++;
        task.BuildMessage = task.BuildOnly is null ? $"Built: {report.Blocks.Count} blocks." : $"Built the block {task.BuildOnly} (and what it needs).";
        try
        {
            TakeRead take = bank.ReadTake(bank.ResolveTakeId(listenTake));
            string? data = take.Take.Data.Select(d => d.DataId).FirstOrDefault();
            if (data is not null)
            {
                Directory.CreateDirectory(Path.Combine(Work, "audio"));
                string file = Path.Combine(Work, "audio", task.Id + ".wav");
                using Stream src = bank.OpenData(take.Take.Data.FirstOrDefault(d => d.Role == "master")?.DataId ?? data);
                using FileStream dst = new(file, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
                task.AudioFile = file;
            }
        }
        catch (Exception ex) when (ex is AmpleException or IOException)
        {
            task.Add("warn", "The result was built but could not be made available to listen to: " + ex.Message);
        }
    }

    /// <summary>
    /// Builds a song in a staged copy of the project (a temporary project that sees the files of the real one) with extra audio files put next to it, and returns the finished master as a WAV file.
    /// Used by the FLAC listening test of a Derived export; nothing in the real project changes.
    /// </summary>
    internal static async Task<string> BuildStagedMasterAsync(ProjectSession s, string stage, string songPath, string songText, IReadOnlyDictionary<string, string> extraFiles, string output, CancellationToken ct)
    {
        if (Directory.Exists(stage))
        {
            DeleteStage(stage);
        }

        using (OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), stage, "listen", null, ct).ConfigureAwait(false))
        {
            _ = created;
        }

        Dictionary<string, string> proposed = new(StringComparer.Ordinal) { [songPath] = songText };
        foreach (string rel in extraFiles.Keys)
        {
            proposed[Path.GetDirectoryName(rel)!.Replace('\\', '/') + "/.stage"] = string.Empty;   // makes the folder a real folder of the stage, so the audio files never land in the real project
        }

        try
        {
            StageProject(s.Root, stage, proposed);
        }
        catch (StageException ex)
        {
            throw new AmpleException("AMP5462", "The listening test could not be set up: " + ex.Message);
        }

        foreach ((string rel, string file) in extraFiles)
        {
            string target = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        AmpleDocument parsed = new Ample.Engine.Modules.ModuleService(stage, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(songText, s.Spec);
        if (parsed.Diagnostics.Any(d => d.Severity == Severity.Error))
        {
            throw new AmpleException("AMP5462", "The song of the package has errors, so it cannot be built to listen to.");
        }

        IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
        Takebank bank = Takebank.Open(s.Fs, s.Clock, stage);
        BuildEngine engine = new(bank, s.Spec, s.Manifest.Id, stage, s.AmpleRoot);
        BuildReport report = await engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, null, null, null), ct).ConfigureAwait(false);
        if (report.MasterTakeId is null || report.Blocks.Any(b => b.Status is "failed" or "skipped" or "cancelled"))
        {
            throw new AmpleException("AMP5462", "The song of the package did not build, so there is nothing to listen to: " + string.Join("; ", report.Blocks.Where(b => b.Status is "failed" or "skipped" or "cancelled").Select(b => b.Block + ": " + b.Status).Take(3)));
        }

        TakeRead take = bank.ReadTake(bank.ResolveTakeId(report.MasterTakeId));
        string data = take.Take.Data.FirstOrDefault(d => d.Role == "master")?.DataId ?? take.Take.Data.Select(d => d.DataId).First();
        await using Stream src = bank.OpenData(data);
        await using FileStream dst = new(output, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return output;
    }

    private static void StageProject(string source, string stage, Dictionary<string, string> proposed)
    {
        StageBudget budget = new();
        HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase) { string.Empty };
        foreach (string rel in proposed.Keys)
        {
            for (string d = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? string.Empty; d.Length > 0; d = Path.GetDirectoryName(d)?.Replace('\\', '/') ?? string.Empty)
            {
                dirs.Add(d);
            }
        }

        void Walk(string relDir)
        {
            string full = relDir.Length == 0 ? source : Path.Combine(source, relDir.Replace('/', Path.DirectorySeparatorChar));
            foreach (string entry in Directory.EnumerateFileSystemEntries(full))
            {
                string name = Path.GetFileName(entry);
                string rel = relDir.Length == 0 ? name : relDir + "/" + name;
                if (relDir.Length == 0 && (name is ProjectLayout.AmpleFolder or "out" or "exports" or ".git"))
                {
                    continue;
                }

                string target = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(entry))
                {
                    if (dirs.Contains(rel))
                    {
                        Directory.CreateDirectory(target);
                        Walk(rel);
                    }
                    else
                    {
                        LinkDirectory(target, entry, budget);
                    }
                }
                else if (!proposed.ContainsKey(rel))
                {
                    File.Copy(entry, target, true);
                }
            }
        }

        Walk(string.Empty);
        foreach ((string rel, string text) in proposed)
        {
            string target = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, new UTF8Encoding(false));
        }
    }

    private sealed class StageException(string message) : Exception(message);

    /// <summary>How much may be copied when a folder cannot be linked.</summary>
    private sealed class StageBudget
    {
        public long Left { get; set; } = 500L * 1024 * 1024;
    }

    /// <summary>
    /// Makes a folder of the staged project show the files of the project without copying them, in the best way this machine allows: an NTFS junction (no rights needed), else a symbolic link,
    /// else a real copy when the folder is small enough. When none of them is possible the build check says so (it is skipped, never wrong).
    /// </summary>
    private static void LinkDirectory(string link, string target, StageBudget budget)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                ProcessStartInfo info = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, target })
                {
                    info.ArgumentList.Add(arg);
                }

                using Process p = Process.Start(info)!;
                p.WaitForExit(15000);
                if (p.ExitCode == 0 && Directory.Exists(link))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // try the next way
            }
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            if (Directory.Exists(link))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // try the next way
        }

        long size = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        if (size > budget.Left)
        {
            throw new StageException($"The folder {Path.GetFileName(target)} could not be linked and is too big to copy ({size / 1_000_000} MB).");
        }

        budget.Left -= size;
        foreach (string dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(link, Path.GetRelativePath(target, dir)));
        }

        Directory.CreateDirectory(link);
        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(link, Path.GetRelativePath(target, file)), true);
        }
    }

    /// <summary>Deletes a folder tree without following links into other folders.</summary>
    private static void DeleteTree(string root)
    {
        DeleteStage(root);
    }

    private static void DeleteStage(string root)
    {
        foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToList())
        {
            if (Directory.Exists(d) && new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(d, false);
            }
        }

        Directory.Delete(root, true);
    }

    // ---- accept, reject, rebase, undo ----

    private async Task<string?> LiveText(string path)
    {
        if (path == string.Empty)
        {
            return null;
        }

        string full = ProjectPath.Resolve(s.Root, path);
        if (!File.Exists(full))
        {
            return null;
        }

        DocSession? open = path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) ? await s.DocAsync(path, CancellationToken.None).ConfigureAwait(false) : null;
        if (open is not null)
        {
            return open.Text;
        }

        return await File.ReadAllTextAsync(full).ConfigureAwait(false);
    }

    // ---- a resumed session: what the project did in the meantime ----

    /// <summary>For each file of the copy: how the project now compares with what the copy was based on, and whether the copy itself was edited.</summary>
    private async Task<List<(string Path, string State, bool Mine, string? Then, string? Live, string? Copy)>> DriftAsync(AiTask task)
    {
        List<(string Path, string State, bool Mine, string? Then, string? Live, string? Copy)> rows = [];
        foreach ((string rel, string? then) in task.Baseline.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            string? live;
            try { live = await LiveText(rel).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or AmpleException or UnauthorizedAccessException) { live = null; }
            string file = System.IO.Path.Combine(task.ProjectDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string? copy = File.Exists(file) ? SafeRead(file) : null;
            string state = live is null ? (then is null ? "same" : "removed") : then == live ? "same" : "changed";
            rows.Add((rel, state, copy != then, then, live, copy));
        }

        return rows;
    }

    /// <summary>The first words of a resumed turn: that it is a resumption, after how long, what happened to the proposal, and what the project did since (with the tool to look further).</summary>
    private async Task<string> ResumeNoteAsync(AiTask task, string prior)
    {
        TimeSpan gap = DateTimeOffset.UtcNow - task.LastActive;
        string ago = gap.TotalHours >= 1 ? $"{(int)gap.TotalHours} hour(s)" : $"{Math.Max(1, (int)gap.TotalMinutes)} minute(s)";
        StringBuilder note = new();
        note.Append("AMPLE NOTE: this is a RESUMED conversation, about ").Append(ago).Append(" after your last turn. ");
        note.Append(prior switch { "accepted" => "The user accepted your last proposal: it is in the project now. ", "rejected" => "The user rejected your last proposal: it is not in the project. ", _ => "Your last proposal was not decided. " });
        note.Append("Your working copy is as you left it, but the project may have changed since. ");
        List<(string Path, string State, bool Mine, string? Then, string? Live, string? Copy)> rows = await DriftAsync(task).ConfigureAwait(false);
        string[] moved = [.. rows.Where(r => r.State != "same").Select(r => r.Path + " (" + r.State + ")")];
        note.Append(moved.Length == 0 ? "Right now every file of your copy is still the same as in the project. " : "Since then: " + string.Join(", ", moved.Take(12)) + (moved.Length > 12 ? $" and {moved.Length - 12} more" : string.Empty) + ". ");
        note.Append("Use ample_changes_since to see what changed (no arguments: the overview; path=<file>: that file's diff, then to now; import=true with a path: bring the project's version into your copy, merged with your own edits). Look only at what the new request needs. Check with ample_song / ample_file before you rely on anything from before.");
        return note.ToString();
    }

    /// <summary>The tool behind ample_changes_since: what the project did since the copy of this task was based on it, a file's diff, or bringing a newer version into the copy.</summary>
    public async Task<JsonObject> ChangesSinceAsync(string token, string path, bool import)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        List<(string Path, string State, bool Mine, string? Then, string? Live, string? Copy)> rows = await DriftAsync(task).ConfigureAwait(false);
        path = AiContext.Norm(path ?? string.Empty);
        if (path.Length == 0)
        {
            return new JsonObject
            {
                ["files"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject { ["path"] = r.Path, ["project"] = r.State, ["editedInYourCopy"] = r.Mine, ["size"] = r.Live?.Length })]),
                ["note"] = "'project' compares the project now with what your copy was based on (same, changed, removed); 'editedInYourCopy' says you changed your copy. Files the project got after your copy was made are not listed: look with ample_project. Next: path=<file> for the diff, import=true with the path to bring the project's version in.",
            };
        }

        (string Path, string State, bool Mine, string? Then, string? Live, string? Copy) row = rows.FirstOrDefault(r => r.Path == path);
        if (row.Path is null)
        {
            // a file the copy does not know: the project's version against nothing
            string? known;
            try { known = await LiveText(path).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or AmpleException or UnauthorizedAccessException) { known = null; }
            row = (path, known is null ? "missing" : "added", false, null, known, null);
        }

        if (!import)
        {
            string diff = AiDiff.Unified(row.Then ?? string.Empty, row.Live ?? string.Empty, path);
            return new JsonObject { ["path"] = path, ["project"] = row.State, ["editedInYourCopy"] = row.Mine, ["diff"] = diff.Length > 20000 ? diff[..20000] + "\n… (cut: import the file and read it with ample_file, or ask for another file)" : diff };
        }

        if (row.Live is null) return new JsonObject { ["path"] = path, ["imported"] = false, ["note"] = "The project no longer has this file; nothing was brought in." };
        if (row.State == "same") return new JsonObject { ["path"] = path, ["imported"] = false, ["note"] = "Your copy is already based on the project's version." };
        string target = System.IO.Path.Combine(task.ProjectDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string result = row.Live;
        if (row.Mine && row.Copy is not null)
        {
            string? merged = AiDiff.Merge(row.Then ?? string.Empty, row.Live, row.Copy, out int clashes);
            if (merged is null) return new JsonObject { ["path"] = path, ["imported"] = false, ["note"] = $"You and the project changed the same place ({clashes}). Read the project's version with ample_file and apply your change to it by hand with ample_edit." };
            result = merged;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, result, new UTF8Encoding(false)).ConfigureAwait(false);
        task.Baseline[path] = row.Live;
        task.LastActive = DateTimeOffset.UtcNow;
        task.Add("file", "brought in the project's version of " + path + (row.Mine ? " (merged with the edits in the copy)" : string.Empty));
        return new JsonObject { ["path"] = path, ["imported"] = true, ["merged"] = row.Mine, ["note"] = "Your copy of this file now includes the project's version" + (row.Mine ? " and your own edits" : string.Empty) + "." };
    }

    /// <summary>Whether the project changed since the task began, per changed file.</summary>
    public async Task CheckStaleAsync(AiTask task)
    {
        task.StaleFiles.Clear();
        foreach (AiChange c in task.Changes)
        {
            string? live = await LiveText(c.Path).ConfigureAwait(false);
            string? baseline = task.Baseline.TryGetValue(c.Path, out string? b) ? b : null;
            if (live != baseline)
            {
                task.StaleFiles.Add(c.Path);
            }
        }

        task.Stale = task.StaleFiles.Count > 0;
    }

    /// <summary>Brings the proposal up to date with a project that changed in the meantime, where the changes do not overlap. Conflicts are reported, nothing is guessed.</summary>
    public async Task<JsonObject> RebaseAsync(string id)
    {
        AiTask task = GetTask(id);
        await CheckStaleAsync(task).ConfigureAwait(false);
        List<string> conflicts = [];
        foreach (string path in task.StaleFiles.ToList())
        {
            AiChange c = task.Changes.First(x => x.Path == path);
            string? live = await LiveText(path).ConfigureAwait(false);
            string baseText = task.Baseline.TryGetValue(path, out string? b) ? b ?? string.Empty : string.Empty;
            if (c.After is null || live is null)
            {
                conflicts.Add($"{path} (it was deleted or created on one side)");
                continue;
            }

            string? merged = AiDiff.Merge(baseText, live, c.After, out int n);
            if (merged is null)
            {
                conflicts.Add($"{path} ({n} place(s) were changed by both)");
                continue;
            }

            c.After = merged;
            task.Baseline[path] = live;
            (int add, int rem) = AiDiff.Count(live, merged);
            c.Added = add;
            c.Removed = rem;
        }

        if (conflicts.Count == 0)
        {
            await CheckStaleAsync(task).ConfigureAwait(false);
            await ValidateAsync(task).ConfigureAwait(false);
            task.RebaseMessage = "The proposal was brought up to date with the current project.";
            task.Add("info", task.RebaseMessage);
            _ = Task.Run(async () => { await BuildCheckAsync(task).ConfigureAwait(false); });
        }
        else
        {
            task.RebaseMessage = "The proposal could not be brought up to date: " + string.Join("; ", conflicts) + ". Regenerate it against the current project.";
            task.Add("warn", task.RebaseMessage);
        }

        return TaskJson(task);
    }

    /// <summary>Applies an accepted proposal: a checkpoint first, then the files other than the open song are written (journaled), and the new text of the song is returned for the editor, which applies it as one undoable change.</summary>
    public async Task<JsonObject> AcceptAsync(
        string id, bool anyway, IEnumerable<string>? include, CancellationToken ct,
        IEnumerable<string>? moves = null, string? activeSong = null)
    {
        // one review action at a time: a second press of Accept (or of Reject while it is applied) says so instead of racing the first
        AiTask gated = GetTask(id);
        if (!await gated.ReviewGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new AmpleException("AMP5458", "This proposal is being applied already: wait a moment.");
        try { return await AcceptCoreAsync(id, anyway, include, ct, moves, activeSong).ConfigureAwait(false); }
        finally { gated.ReviewGate.Release(); }
    }

    private async Task<JsonObject> AcceptCoreAsync(
        string id, bool anyway, IEnumerable<string>? include, CancellationToken ct,
        IEnumerable<string>? moves = null, string? activeSong = null)
    {
        AiTask task = GetTask(id);
        if (task.PolicyViolation is not null) throw new AmpleException("AMP5481", "This proposal is refused for good: it would widen the policy of the song (" + task.PolicyViolation + "). Capabilities and limits can only be widened by the owner, by hand.");
        string[] chosen = [.. (include ?? []).Distinct(StringComparer.Ordinal)];
        string[] missing = [.. task.Offered.Where(o => ProposalUses(task, o) && !chosen.Contains(o.Id)).Select(o => o.ProjectPath)];
        if (missing.Length > 0)
        {
            throw new AmpleException("AMP5463", "The proposal uses audio you offered, which has to come along or the song will not find it: " + string.Join(", ", missing) + ". Tick them (you can listen to each first), or ask Codex to leave them out.");
        }

        if (task.Status != "ready" || task.Applied)
        {
            throw new AmpleException("AMP5458", "This proposal cannot be accepted (it is not ready, or it was applied already).");
        }

        if (task.BuildStatus is "running" or "pending")
        {
            throw new AmpleException("AMP5455", "The proposal is still being checked. Wait for the result, or use Accept anyway.");
        }

        bool invalid = task.BuildStatus == "failed" || task.Diagnostics.Any(d => d.Contains(" error ", StringComparison.Ordinal));
        if (invalid && !anyway)
        {
            throw new AmpleException("AMP5455", "The proposal does not build. Use Accept anyway to apply it as it is.");
        }

        await CheckStaleAsync(task).ConfigureAwait(false);
        if (task.Stale)
        {
            throw new AmpleException("AMP5456", "The project changed while Codex was working: " + string.Join(", ", task.StaleFiles) + ". Try to rebase the proposal, review it anyway, or regenerate it.");
        }

        NodeInfo? checkpoint = await s.History.CheckpointAsync("Before AI change: " + (task.Request.Prompt.Length > 60 ? task.Request.Prompt[..60] + "…" : task.Request.Prompt), ct).ConfigureAwait(false);
        string historyRevision = (checkpoint ?? s.History.Node(s.History.LineOf(s.History.CurrentLine).Head)).NodeId;
        string? songText = null;
        foreach (AiChange c in task.Changes)
        {
            if (c.Path == task.Request.Song && c.After is not null)
            {
                task.SongBefore = c.Before;
                songText = c.After;
                continue;
            }

            string full = ProjectPath.Resolve(s.Root, c.Path);
            task.Undo[c.Path] = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
            if (c.After is null)
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            else
            {
                await s.Bus.ExecuteAsync(new WriteFileCommand(c.Path, c.After), ct).ConfigureAwait(false);
            }

            s.ForgetDoc(c.Path);
        }

        foreach (OfferedFile o in task.Offered.Where(o => chosen.Contains(o.Id)))
        {
            string target = ProjectPath.Resolve(s.Root, o.ProjectPath);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(AttachmentFile(o.Id), target, false);
                task.IncludedPaths.Add(o.ProjectPath);
            }
        }

        foreach (string rel in StagedBinaries(task))
        {
            string target = ProjectPath.Resolve(s.Root, rel);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(task.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar)), target, false);
            task.IncludedPaths.Add(rel);   // a sample the proposal added (such as a sample of a sample set) comes along with the text changes
        }

        songText = await ApplyMovesAsync(task, moves, songText, ct).ConfigureAwait(false);
        bool songWritten = await WriteSongNotOpenAsync(task, songText, activeSong, ct).ConfigureAwait(false);
        if (songWritten) songText = null;
        task.Applied = true;
        task.Status = "accepted";
        RecordHistory(task, "accepted", historyRevision);
        Cleanup(task);
        task.Add("info", invalid ? "Accepted anyway: the project does not build right now." : "Accepted.");
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        s.Events.Publish("files.changed", new { source = "ai" });
        JsonArray others = new([.. task.Changes.Where(c => c.Path != task.Request.Song).Select(c => (JsonNode)c.Path)]);
        return new JsonObject
        {
            ["song"] = task.Request.Song, ["songText"] = songText, ["songWritten"] = songWritten,
            ["others"] = others, ["invalid"] = invalid,
        };
    }

    /// <summary>
    /// The accepted text of the song goes to the editor of the browser, which only works when that song is the one that is open there.
    /// When the browser says another song is open (`activeSong`), the text is written to the song itself, so the accepted edit is not lost,
    /// and the previous text is kept for Undo. Returns true when the song was written.
    /// </summary>
    private async Task<bool> WriteSongNotOpenAsync(AiTask task, string? songText, string? activeSong, CancellationToken ct)
    {
        if (songText is null || string.IsNullOrEmpty(activeSong) || string.Equals(activeSong, task.Request.Song, StringComparison.Ordinal))
        {
            return false;
        }

        string full = ProjectPath.Resolve(s.Root, task.Request.Song);
        task.Undo[task.Request.Song] = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
        await s.Bus.ExecuteAsync(new WriteFileCommand(task.Request.Song, songText), ct).ConfigureAwait(false);
        s.ForgetDoc(task.Request.Song);
        return true;
    }

    /// <summary>Accepts checked files from a proposal and continues the same Codex thread.</summary>
    public async Task<JsonObject> AcceptSelectedAndContinueAsync(
        string id, IEnumerable<string> paths, string message, IEnumerable<string>? include, CancellationToken ct,
        IEnumerable<string>? moves = null, string? activeSong = null)
        => await AcceptSelectedAsync(id, paths, message, include, true, ct, moves, activeSong).ConfigureAwait(false);

    /// <summary>Applies selected files; continuation is an explicit separate choice.</summary>
    public async Task<JsonObject> AcceptSelectedAsync(
        string id, IEnumerable<string> paths, string message, IEnumerable<string>? include, bool continuation, CancellationToken ct,
        IEnumerable<string>? moves = null, string? activeSong = null)
    {
        AiTask task = GetTask(id);
        if (!await task.ReviewGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new AmpleException("AMP5458", "A review action is already running.");
        try
        {
            return await AcceptSelectedCoreAsync(id, paths, message, include, continuation, ct, moves, activeSong).ConfigureAwait(false);
        }
        finally { task.ReviewGate.Release(); }
    }

    private async Task<JsonObject> AcceptSelectedCoreAsync(
        string id, IEnumerable<string> paths, string message, IEnumerable<string>? include, bool continuation, CancellationToken ct,
        IEnumerable<string>? moves = null, string? activeSong = null)
    {
        AiTask task = GetTask(id);
        if (task.PolicyViolation is not null) throw new AmpleException("AMP5481", "This proposal is refused for good: it would widen the policy of the song (" + task.PolicyViolation + "). Capabilities and limits can only be widened by the owner, by hand.");
        string[] selected = [.. paths.Distinct(StringComparer.Ordinal)];
        if (selected.Length == 0 || selected.Any(path => task.Changes.All(change => change.Path != path))) throw new AmpleException("AMP5458", "Choose one or more files from the current proposal.");
        if (task.Status != "ready" || task.Applied || (continuation && task.Rpc is null) || task.BuildStatus is "pending" or "running" || (task.BuildStatus != "ok" && !task.SkipBuildRequested) || task.Diagnostics.Any(d => d.Contains(" error ", StringComparison.Ordinal)) || task.Stale) throw new AmpleException("AMP5455", "Accept selected needs a current, ready proposal with no source errors and either a successful build or an explicit no-build request.");
        await CheckStaleAsync(task).ConfigureAwait(false);
        if (task.Stale) throw new AmpleException("AMP5456", "The project changed while Codex was working: " + string.Join(", ", task.StaleFiles) + ". Rebase before accepting any part.");
        string[] chosen = [.. (include ?? []).Distinct(StringComparer.Ordinal)];
        string[] missing = [.. task.Offered.Where(offered => ProposalUses(task, offered) && !chosen.Contains(offered.Id)).Select(offered => offered.ProjectPath)];
        if (missing.Length > 0) throw new AmpleException("AMP5463", "The proposal uses offered audio that must be included: " + string.Join(", ", missing) + ".");
        List<AiChange> original = [.. task.Changes];
        string priorBuildStatus = task.BuildStatus, priorBuildMessage = task.BuildMessage;
        string[] priorDiagnostics = [.. task.Diagnostics];
        task.Changes.Clear();
        task.Changes.AddRange(original.Where(change => selected.Contains(change.Path, StringComparer.Ordinal)));
        bool invalid = false;
        try
        {
            await ValidateAsync(task).ConfigureAwait(false);
            invalid = task.Diagnostics.Any(diagnostic => diagnostic.Contains(" error ", StringComparison.Ordinal));
        }
        finally
        {
            task.Changes.Clear();
            task.Changes.AddRange(original);
            task.Diagnostics.Clear();
            task.Diagnostics.AddRange(priorDiagnostics);
            task.BuildStatus = priorBuildStatus;
            task.BuildMessage = priorBuildMessage;
        }
        if (invalid) throw new AmpleException("AMP5455", "The selected files introduce song errors on their own. Ask Codex to adjust them before accepting.");
        await s.History.CheckpointAsync("Before partial AI change", ct).ConfigureAwait(false);
        string? songText = null;
        foreach (AiChange change in original.Where(change => selected.Contains(change.Path, StringComparer.Ordinal)))
        {
            if (change.Path == task.Request.Song && change.After is not null) { task.SongBefore = change.Before; songText = change.After; }
            else
            {
                string full = ProjectPath.Resolve(s.Root, change.Path);
                task.Undo[change.Path] = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
                if (change.After is null) { if (File.Exists(full)) File.Delete(full); }
                else await s.Bus.ExecuteAsync(new WriteFileCommand(change.Path, change.After), ct).ConfigureAwait(false);
                s.ForgetDoc(change.Path);
            }
            task.Baseline[change.Path] = change.After;
            task.AcceptedPaths.Add(change.Path);
        }
        foreach (OfferedFile offered in task.Offered.Where(offered => chosen.Contains(offered.Id)))
        {
            string target = ProjectPath.Resolve(s.Root, offered.ProjectPath);
            if (!File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(AttachmentFile(offered.Id), target, false); task.IncludedPaths.Add(offered.ProjectPath); }
        }
        songText = await ApplyMovesAsync(task, moves, songText, ct).ConfigureAwait(false);
        bool songWritten = await WriteSongNotOpenAsync(task, songText, activeSong, ct).ConfigureAwait(false);
        if (songWritten) songText = null;
        task.Changes.RemoveAll(change => selected.Contains(change.Path, StringComparer.Ordinal));
        task.Add("info", "Accepted selected files: " + string.Join(", ", selected) + ". The remaining proposal stays in review; accepting does not start another turn.");
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        s.Events.Publish("files.changed", new { source = "ai" });
        JsonArray acceptedPaths = new([.. selected.Select(path => (JsonNode)path)]);
        JsonObject result = new()
        {
            ["song"] = task.Request.Song, ["songText"] = songText, ["songWritten"] = songWritten, ["accepted"] = acceptedPaths,
        };
        if (continuation) result["continuation"] = ContinueRemainingCore(task, message);
        return result;
    }

    /// <summary>Continues only unfinished work; duplicate review requests never steer a running turn.</summary>
    public async Task<JsonObject> ContinueRemainingAsync(string id, string message, CancellationToken ct)
    {
        AiTask task = GetTask(id);
        if (!await task.ReviewGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new AmpleException("AMP5458", "A review action is already running.");
        try { return ContinueRemainingCore(task, message); }
        finally { task.ReviewGate.Release(); }
    }

    private JsonObject ContinueRemainingCore(AiTask task, string message)
    {
        if (task.Status != "ready" || task.Applied || task.Rpc is null) throw new AmpleException("AMP5458", "Continue requires a ready conversation; a turn is already running or the conversation ended.");
        AiLog? quantityRequest = task.Log.LastOrDefault(line => line.Kind == "user" && !line.Text.StartsWith("Continue ", StringComparison.OrdinalIgnoreCase) && QuantityRequest.IsRequest(line.Text));
        string quantityPrompt = quantityRequest?.Text ?? task.Request.Prompt;
        int? limit = QuantityRequest.Limit(quantityPrompt);
        int earlier = quantityRequest is null ? 0 : task.ClipsNow().Count(c => c.Source == "proposal" && c.At < quantityRequest.At);
        int delivered = task.ClipsNow().Count(c => c.Source == "proposal") - earlier;
        if (limit is not null && delivered >= limit)
        {
            task.Add("info", $"Continue did not start: {delivered} proposal samples already delivered; the original request asked for {limit}. Use a new explicit message for additional work.");
            return new JsonObject { ["started"] = false, ["limitReached"] = true, ["delivered"] = delivered, ["limit"] = limit, ["note"] = "The requested samples have already been delivered. No new series was started." };
        }
        task.ContinuationClipLimit = limit is null ? null : limit + earlier;
        string checkpoint = new JsonObject { ["originalRequest"] = task.Request.Prompt, ["userRequirements"] = new JsonArray([.. task.Log.Where(line => line.Kind == "user" && !line.Text.StartsWith("Continue ", StringComparison.OrdinalIgnoreCase)).Select(line => (JsonNode)line.Text)]), ["quantityRequest"] = quantityPrompt, ["lastAnswer"] = task.Summary, ["acceptedFiles"] = new JsonArray([.. task.AcceptedPaths.Select(p => (JsonNode)p)]), ["deliveredClips"] = new JsonArray([.. task.ClipsNow().Select(c => (JsonNode)ClipJson(c))]), ["sampleLimit"] = limit, ["remainingSamples"] = limit is null ? null : Math.Max(0, limit.Value - delivered), ["userClarification"] = message.Trim() }.ToJsonString();
        SendMessageCore(task.Id, "Continue ONLY work not yet delivered. Keep the original requirements and quantity limits. Do not repeat completed steps, restart the original request, or create another series of samples. Count the clips in this checkpoint as already delivered. If nothing remains, report completion without producing more. Additional work requires a new explicit user request. Checkpoint: " + checkpoint, null, null, true);
        return new JsonObject { ["started"] = true, ["delivered"] = delivered, ["limit"] = limit };
    }

    /// <summary>Puts back the files other than the open song that an accepted proposal changed (the song itself is undone in the editor).</summary>
    public async Task<JsonObject> UndoAsync(string id, CancellationToken ct)
    {
        AiTask task = GetTask(id);
        if (!task.Applied)
        {
            throw new AmpleException("AMP5458", "This proposal was not applied.");
        }

        await UndoMovesAsync(task, ct).ConfigureAwait(false);
        foreach ((string path, string? before) in task.Undo)
        {
            string full = ProjectPath.Resolve(s.Root, path);
            if (before is null)
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            else
            {
                await s.Bus.ExecuteAsync(new WriteFileCommand(path, before), ct).ConfigureAwait(false);
            }

            s.ForgetDoc(path);
        }

        foreach (string path in task.IncludedPaths)
        {
            string file = ProjectPath.Resolve(s.Root, path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        task.IncludedPaths.Clear();
        task.Applied = false;
        task.Status = "ready";
        task.Add("info", "The accepted change was undone.");
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        return new JsonObject { ["song"] = task.Request.Song, ["songText"] = task.SongBefore };
    }

    /// <summary>Throws the proposal away; the project stays as it is.</summary>
    public void Reject(string id)
    {
        AiTask task = GetTask(id);
        if (task.Applied)
        {
            throw new AmpleException("AMP5458", "This proposal was applied. Undo it instead.");
        }

        task.Status = "rejected";
        RecordHistory(task, "rejected");
        task.Add("info", "Rejected. Nothing in the project changed.");
        sessionStore.SaveTask(task);
        sessionStore.UpdateTask(task);
        Cleanup(task);
    }

    /// <summary>The earlier Koda task this one goes on with (same account, conversation and working copy still there), or null.</summary>
    private AiTask? KeptKodaCopy(AiTask task)
        => task.Request.Kind == "koda" && !string.IsNullOrWhiteSpace(task.Request.ContinueFrom) && tasks.TryGetValue(task.Request.ContinueFrom, out AiTask? before) && before.Request.Kind == "koda" && before.Key == task.Key && before.ThreadId.Length > 0 && before.Workspace.Length > 0 && Directory.Exists(before.Workspace) ? before : null;

    /// <summary>A Koda copy stays for a while after its answer was accepted or rejected, so a question that goes on can use it; it is removed when another question starts, or after an hour.</summary>
    private static readonly TimeSpan KodaKeep = TimeSpan.FromHours(1);

    /// <summary>Another session keeps its working copy (and so its Codex conversation) for a day after the last use, when the copy is not bigger than <see cref="KeepLimit"/>.</summary>
    private static readonly TimeSpan SessionKeep = TimeSpan.FromHours(24);

    private const long KeepLimit = 500_000_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> kodaTouched = new(StringComparer.OrdinalIgnoreCase);

    private static bool SmallEnough(string folder)
    {
        long total = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(f).Length;
                if (total > KeepLimit) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Notes that the copy was used just now (an answer, an accept, a reject, a question that goes on) and makes sure it is removed an hour after the last of these.</summary>
    private void KeepKodaCopy(AiTask task)
    {
        string workspace = task.Workspace;
        TimeSpan keep = task.Request.Kind == "koda" ? KodaKeep : SessionKeep;
        bool first = !kodaTouched.ContainsKey(workspace);
        kodaTouched[workspace] = DateTimeOffset.UtcNow + keep;
        if (!first) return;   // a timer is already waiting for this copy; it looks again when it fires
        _ = Task.Run(async () =>
        {
            for (; ; )
            {
                TimeSpan left = kodaTouched.TryGetValue(workspace, out DateTimeOffset until) ? until - DateTimeOffset.UtcNow : TimeSpan.Zero;
                if (left > TimeSpan.Zero)
                {
                    await Task.Delay(left).ConfigureAwait(false);
                    continue;
                }

                if (tasks.Values.Any(x => x.Workspace == workspace && x.Status is "queued" or "preparing" or "running" or "waiting-approval" or "validating"))
                {
                    kodaTouched[workspace] = DateTimeOffset.UtcNow + keep;   // a question is working in it right now
                    continue;
                }

                kodaTouched.TryRemove(workspace, out _);
                try { if (Directory.Exists(workspace)) DeleteTree(workspace); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed with .ample/ai later */ }
                return;
            }
        });
    }

    /// <summary>A new Koda question that does not go on: the kept copies of the earlier ones are not needed any more.</summary>
    private void DropKeptKodaCopies(string? except)
    {
        string? keep = !string.IsNullOrWhiteSpace(except) && tasks.TryGetValue(except, out AiTask? b) ? b.Workspace : null;
        foreach (AiTask old in tasks.Values.Where(x => x.Request.Kind == "koda" && x.Rpc is null && x.Workspace.Length > 0 && x.Workspace != keep && x.Status is not ("queued" or "preparing" or "running" or "waiting-approval" or "validating")).ToList())
        {
            try { if (Directory.Exists(old.Workspace)) DeleteTree(old.Workspace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed with .ample/ai later */ }
        }
    }

    private void Cleanup(AiTask task)
    {
        EndSession(task);
        try
        {
            if (task.ValidateRoot is not null && Directory.Exists(task.ValidateRoot))
            {
                DeleteStage(task.ValidateRoot);
            }

            if (Directory.Exists(task.Workspace) && task.ThreadId.Length > 0 && (task.Request.Kind == "koda" || SmallEnough(task.Workspace)))
            {
                KeepKodaCopy(task);
            }
            else if (Directory.Exists(task.Workspace))
            {
                DeleteTree(task.Workspace);
            }

            if (task.AudioFile is not null && File.Exists(task.AudioFile))
            {
                File.Delete(task.AudioFile);
                task.AudioFile = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what could not be removed now is removed with .ample/ai later
        }
    }

    // ---- the view of a task ----

    /// <summary>A task as the user interface shows it.</summary>
    public static JsonObject TaskJson(AiTask task, bool diffs = true, long? since = null, bool details = false)
    {
        JsonArray changes = [];
        foreach (AiChange c in task.Changes)
        {
            JsonObject o = new() { ["path"] = c.Path, ["status"] = c.Status, ["added"] = c.Added, ["removed"] = c.Removed, ["why"] = c.Why };
            if (diffs)
            {
                string diff = AiDiff.Unified(c.Before ?? string.Empty, c.After ?? string.Empty, c.Path);
                o["diff"] = diff.Length > 60000 ? diff[..60000] + "\n… (cut)" : diff;
                if (c.Path.EndsWith(".seq", StringComparison.OrdinalIgnoreCase) || c.Path.EndsWith(".ampleseq", StringComparison.OrdinalIgnoreCase))
                {
                    o["musical"] = MusicalSummary(c.Before, c.After);
                }
            }

            changes.Add(o);
        }

        JsonObject json = new()
        {
            ["id"] = task.Id,
            ["sessionId"] = task.SessionId,
            ["acceptedPaths"] = new JsonArray([.. task.AcceptedPaths.Select(path => (JsonNode)path)]),
            ["resumable"] = (task.Status == "interrupted" || (task.Status is "cancelled" or "failed") && !Live.ContainsKey(task.Id)) && task.ThreadId.Length > 0 && Directory.Exists(task.Workspace),
            ["sessionEnded"] = task.Rpc is null && (task.Status == "ready" && !task.Applied || task.Status is "accepted" or "rejected" && task.ThreadId.Length > 0) && Directory.Exists(task.Workspace),
            ["canResumeThread"] = task.ThreadId.Length > 0,
            ["archived"] = task.Archived || task.Status is "cancelled" or "rejected" or "accepted" or "failed",
            ["status"] = task.Status,
            ["thought"] = task.Thought,
            ["answer"] = task.AnswerText.Length == 0 ? null : new JsonObject { ["title"] = task.AnswerTitle, ["text"] = task.AnswerText, ["more"] = task.AnswerMore, ["action"] = task.AnswerAction },
            ["variants"] = new JsonArray([.. task.Variants.Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["summary"] = v.Summary, ["blocks"] = new JsonArray([.. v.Blocks.Select(b => (JsonNode)JsonValue.Create(b)!)]), ["listen"] = v.Listen })]),
            ["clips"] = new JsonArray([.. task.ClipsNow().Select(c => (JsonNode)ClipJson(c))]),
            ["offered"] = new JsonArray([.. task.Offered.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["name"] = o.Name, ["path"] = o.ProjectPath, ["size"] = o.Size, ["seconds"] = o.Seconds, ["used"] = ProposalUses(task, o) })]),
            ["moves"] = new JsonArray([.. task.Moves.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["from"] = m.From, ["to"] = m.To, ["isDir"] = m.IsDir, ["references"] = m.References, ["mode"] = m.Mode, ["reason"] = m.Reason, ["approved"] = m.Approved, ["applied"] = m.Applied })]),
            ["canMessage"] = task.Rpc is not null && !task.Applied && task.Status is "running" or "waiting-approval" or "ready",
            ["repairs"] = task.Repairs,
            ["queueAhead"] = task.Status == "queued" ? Live.Values.Count(x => x.Key == task.Key && x.Created < task.Created && x.Status is "queued" or "preparing" or "running" or "waiting-approval" or "validating") : 0,
            ["prompt"] = task.Request.Prompt,
            ["kind"] = task.Request.Kind,
            ["scope"] = task.Request.Scope,
            ["path"] = task.Request.Path,
            ["song"] = task.Request.Song,
            ["outputSong"] = task.OutputSong,
            ["identity"] = IdentityLabel(new AiAssistantSettings(task.Identity, task.Mode, null, null, null, null)),
            ["compacting"] = task.Compacting,
            ["compactions"] = task.Compactions,
            ["context"] = new JsonObject { ["used"] = task.ContextUsed, ["window"] = task.ContextWindow, ["percent"] = task.ContextWindow > 0 ? (int)Math.Min(100, task.ContextUsed * 100 / task.ContextWindow) : null },
            ["model"] = task.ModelNow ?? task.Request.Model,
            ["effort"] = task.EffortNow ?? task.Request.Effort,
            ["modeLabel"] = ModeLabel(task.Mode),
            ["summary"] = task.Summary,
            ["error"] = task.Error,
            ["logSeq"] = task.LogSeq,
            ["log"] = new JsonArray([.. task.Log.Where(l => since is null || l.Seq > since).Select(l => (JsonNode)new JsonObject { ["seq"] = l.Seq, ["at"] = l.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["kind"] = l.Kind, ["text"] = l.Text, ["path"] = l.Path, ["why"] = l.Why, ["hasDetails"] = l.Details is not null, ["details"] = details ? l.Details?.DeepClone() : null })]),
            ["approvals"] = new JsonArray([.. task.Approvals.Values.Where(a => a.Answered is null).Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["kind"] = a.Kind, ["summary"] = a.Summary, ["detail"] = a.Detail })]),
            ["changes"] = changes,
            ["diagnostics"] = new JsonArray([.. task.Diagnostics.Select(x => (JsonNode)x)]),
            ["build"] = new JsonObject { ["status"] = task.BuildStatus, ["message"] = task.BuildMessage, ["song"] = SongForBuild(task), ["skipRequested"] = task.SkipBuildRequested, ["only"] = task.BuildOnly, ["version"] = task.BuildVersion, ["details"] = task.BuildDetails.DeepClone(), ["progress"] = new JsonObject { ["done"] = task.BuildDone, ["total"] = task.BuildTotal, ["running"] = new JsonArray([.. task.BuildLive.Values.Where(x => x.State == "running").Take(6).Select(x => (JsonNode)new JsonObject { ["block"] = x.Block, ["fraction"] = x.Fraction, ["steps"] = new JsonArray([.. x.Steps.Select(st => (JsonNode)new JsonObject { ["name"] = st.Name, ["fraction"] = st.Fraction })]) })]) }, ["blocks"] = new JsonArray([.. task.BuildBlocks.Select(x => (JsonNode)new JsonObject { ["block"] = x.Key, ["status"] = x.Value.Status, ["audio"] = x.Value.Audio })]), ["failures"] = new JsonArray([.. task.BuildFailures.Select(x => (JsonNode)x)]), ["audio"] = task.AudioFile is not null },
            ["stale"] = task.Stale,
            ["staleFiles"] = new JsonArray([.. task.StaleFiles.Select(x => (JsonNode)x)]),
            ["rebase"] = task.RebaseMessage,
            ["applied"] = task.Applied,
        };
        return json;
    }

    private static JsonObject MusicalSummary(string? before, string? after)
    {
        static int Count(string? text, string marker) => text is null ? 0 : AiDiff.Lines(text).Count(l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal));
        return new JsonObject { ["patternsBefore"] = Count(before, "pattern"), ["patternsAfter"] = Count(after, "pattern"), ["channelsBefore"] = Count(before, "channel"), ["channelsAfter"] = Count(after, "channel"), ["clipsBefore"] = Count(before, "clip"), ["clipsAfter"] = Count(after, "clip") };
    }

    /// <summary>The audio of the built proposal, when it was built.</summary>
    /// <summary>Builds the proposal again (a fresh check in the temporary project), the whole song or one block with what it needs, so it can be listened to.</summary>
    public AiTask RebuildTask(string id, string? block)
    {
        AiTask task = GetTask(id);
        if (task.Status != "ready" || task.BuildStatus is "running" or "pending")
        {
            throw new AmpleException("AMP5458", "The proposal is not ready to be built, or it is being built already.");
        }

        task.BuildOnly = string.IsNullOrWhiteSpace(block) ? null : block;
        task.SkipBuildRequested = false;
        task.SkipBuildReason = string.Empty;
        task.BuildStatus = "pending";
        task.AudioFile = null;
        _ = Task.Run(async () => { await BuildCheckAsync(task).ConfigureAwait(false); });
        return task;
    }

    /// <summary>The proposed text of a file (what it would be after Accept), for looking at it read-only in the block view.</summary>
    public string ProposedFile(string id, string path) => ProposedText(GetTask(id), path);

    /// <summary>The audio of one built block of the proposal, read from its temporary project (null when that block made none).</summary>
    public Stream? BlockAudio(string id, string block)
    {
        AiTask task = GetTask(id);
        if (!task.BuildTakes.TryGetValue(block, out string? take) || task.ValidateRoot is null || !Directory.Exists(task.ValidateRoot))
        {
            return null;
        }

        Takebank bank = Takebank.Open(s.Fs, s.Clock, task.ValidateRoot);
        string? data = AudioDataOf(bank, take, null);
        return data is null ? null : bank.OpenData(data);
    }

    private static string? AudioDataOf(Takebank bank, string takeId, string? role)
    {
        TakeRead read = bank.ReadTake(bank.ResolveTakeId(takeId));
        foreach (TakeDataRef d in role is not null ? read.Take.Data.Where(x => x.Role == role) : read.Take.Data)
        {
            using Stream st = bank.OpenData(d.DataId);
            byte[] head = new byte[4];
            if (st.Read(head, 0, 4) == 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
            {
                return d.DataId;
            }
        }

        return null;
    }

    public string? AudioOf(string id) => GetTask(id).AudioFile is { } f && File.Exists(f) ? f : null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (AiTask t in tasks.Values)
        {
            await t.Cancel.CancelAsync().ConfigureAwait(false);
        }

        if (device is not null)
        {
            await device.Rpc.DisposeAsync().ConfigureAwait(false);
        }

        if (browserContext is not null) await browserContext.CloseAsync().ConfigureAwait(false);
        browserPlaywright?.Dispose();
        browserGate.Dispose();
        browserActionGate.Dispose();
        browserPermissionGate.Dispose();

    }
}
