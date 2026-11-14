using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.History;

/// <summary>A snapshot of the sources.</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="ParentId">Previous node on the line, or null.</param>
/// <param name="Kind"><c>start</c>, <c>checkpoint</c>, <c>auto</c> or <c>promote</c>.</param>
/// <param name="Message">Message.</param>
/// <param name="At">When.</param>
/// <param name="Files">Path to content id (data id in the takebank).</param>
/// <param name="Reclaimed">True when cleanup removed the file content.</param>
/// <param name="ReduxSequence">Sequence of the exact durable command, when this revision has one.</param>
public sealed record NodeInfo(string NodeId, string? ParentId, string Kind, string Message, DateTimeOffset At, IReadOnlyDictionary<string, string> Files, bool Reclaimed, long? ReduxSequence);

/// <summary>A line of work. <c>studio</c> is the real version, <c>garage</c> is where work happens.</summary>
/// <param name="Name">Line name.</param>
/// <param name="Kind"><c>studio</c>, <c>garage</c> or <c>work</c>.</param>
/// <param name="Head">The newest node.</param>
/// <param name="Archived">True when dropped (recoverable).</param>
public sealed record LineInfo(string Name, string Kind, string Head, bool Archived);

/// <summary>A file difference between two states.</summary>
/// <param name="Path">Project-relative path.</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
public sealed record FileChange(string Path, string Change);

/// <summary>What a cleanup would remove.</summary>
/// <param name="Nodes">Unreachable nodes.</param>
/// <param name="Blobs">File contents used only by those nodes.</param>
/// <param name="Bytes">Bytes that would be freed.</param>
/// <param name="ArchivedLines">Dropped lines whose content is copied to <c>.ample/history-archive</c> first.</param>
/// <param name="ArchivedBlobs">The contents that are archived (a subset of <paramref name="Blobs"/>).</param>
/// <param name="ArchiveBytes">Size of the archive this clean-up would write.</param>
public sealed record CleanupPlan(IReadOnlyList<string> Nodes, IReadOnlyList<string> Blobs, long Bytes, IReadOnlyList<string>? ArchivedLines = null, IReadOnlyList<string>? ArchivedBlobs = null, long ArchiveBytes = 0);

/// <summary>History: lines, checkpoints, tags, fork, go, peek, promote, drop and cleanup. Snapshots store file contents in the takebank (deduplicated); every change goes through the command bus, so the black box sees it. Nothing reachable from a tag or an active line is ever removed.</summary>
public sealed class HistoryService
{
    private static readonly string[] SourcePatterns =
    [
        ".amplet",
        ".module",
        ".amplec",
        ".amplelrc",
        ".lrc",
        ".chordpro",
        ".lyr",
        ".chords",
        ".txt",
        ".md",
        ".csv",
        ".json",
        ".seq",
        ".ampleseq",
    ];
    private readonly IFileSystem fs;
    private readonly IClock clock;
    private readonly string root;
    private readonly Takebank bank;
    private readonly CommandBus bus;
    private readonly string dbPath;

    internal HistoryService(IFileSystem fs, IClock clock, string root, Takebank bank, CommandBus bus)
    {
        this.fs = fs;
        this.clock = clock;
        this.root = root;
        this.bank = bank;
        this.bus = bus;
        dbPath = Path.Combine(root, ProjectLayout.AmpleFolder, "history.db");
    }

    /// <summary>Opens the history of a project; the first time it takes the first snapshot and creates the lines <c>studio</c> and <c>garage</c> (work happens on garage).</summary>
    public static async ValueTask<HistoryService> OpenAsync(IFileSystem fs, IClock clock, string root, Takebank bank, CommandBus bus, CancellationToken cancellationToken = default)
    {
        HistoryService h = new(fs, clock, root, bank, bus);
        using (SqliteConnection c = h.Connect())
        {
            Exec(c, "CREATE TABLE IF NOT EXISTS h_nodes (node_id TEXT PRIMARY KEY, parent_id TEXT NULL, kind TEXT NOT NULL, message TEXT NOT NULL, at TEXT NOT NULL, files_json TEXT NOT NULL, reclaimed INTEGER NOT NULL DEFAULT 0);" +
                "CREATE TABLE IF NOT EXISTS h_node_redux (node_id TEXT PRIMARY KEY REFERENCES h_nodes(node_id), sequence INTEGER NOT NULL UNIQUE);" +
                "CREATE TABLE IF NOT EXISTS h_lines (name TEXT PRIMARY KEY, kind TEXT NOT NULL, head TEXT NOT NULL REFERENCES h_nodes(node_id), archived INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS h_tags (name TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES h_nodes(node_id), reason TEXT NOT NULL, created_at TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS h_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS h_tombstones (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, name TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, detail TEXT NOT NULL);" +
                "CREATE TRIGGER IF NOT EXISTS h_nodes_immutable BEFORE UPDATE OF node_id, parent_id, kind, message, at, files_json ON h_nodes BEGIN SELECT RAISE(ABORT, 'AMP5244: history nodes are immutable'); END;" +
                "CREATE TRIGGER IF NOT EXISTS h_nodes_no_delete BEFORE DELETE ON h_nodes BEGIN SELECT RAISE(ABORT, 'AMP5244: history nodes are never deleted'); END;" +
                "INSERT OR IGNORE INTO schema_parts (part, version) VALUES ('history', 1);");
        }

        if (h.Lines().Count == 0)
        {
            NodeInfo start = await h.SnapshotAsync("start", "first snapshot", null, cancellationToken).ConfigureAwait(false);
            h.Run(c =>
            {
                Exec(c, "INSERT INTO h_nodes (node_id, parent_id, kind, message, at, files_json) VALUES ($id, NULL, $k, $m, $at, $f)", ("$id", start.NodeId), ("$k", start.Kind), ("$m", start.Message), ("$at", h.Now()), ("$f", JsonSerializer.Serialize(start.Files)));
                Exec(c, "INSERT INTO h_lines (name, kind, head, created_at) VALUES ('studio', 'studio', $h, $at), ('garage', 'garage', $h, $at)", ("$h", start.NodeId), ("$at", h.Now()));
                Exec(c, "INSERT OR REPLACE INTO h_state (key, value) VALUES ('current', 'garage')");
            });
        }

        return h;
    }

    /// <summary>Gets the name of the line the working files belong to.</summary>
    public string CurrentLine => Run(c => (string)Scalar(c, "SELECT value FROM h_state WHERE key = 'current'")!);

    /// <summary>Lists the lines.</summary>
    public IReadOnlyList<LineInfo> Lines() => Run(c => Query(c, "SELECT name, kind, head, archived FROM h_lines ORDER BY name", r => new LineInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0)));

    /// <summary>Lists tags as name and node id.</summary>
    public IReadOnlyList<(string Name, string NodeId, string Reason)> Tags() => Run(c => Query(c, "SELECT name, node_id, reason FROM h_tags ORDER BY name", r => (r.GetString(0), r.GetString(1), r.GetString(2))));

    /// <summary>Gets a node.</summary>
    public NodeInfo Node(string nodeId) => Run(c => Query(c, "SELECT n.node_id, n.parent_id, n.kind, n.message, n.at, n.files_json, n.reclaimed, r.sequence FROM h_nodes n LEFT JOIN h_node_redux r ON r.node_id = n.node_id WHERE n.node_id = $id", ReadNode, ("$id", nodeId)).FirstOrDefault()) ?? throw new AmpleException("AMP5243", $"There is no history node {nodeId}.");

    /// <summary>Lists every node of every line, oldest first (the history view draws the graph from this).</summary>
    public IReadOnlyList<NodeInfo> AllNodes() => Run(c => Query(c, "SELECT n.node_id, n.parent_id, n.kind, n.message, n.at, n.files_json, n.reclaimed, r.sequence FROM h_nodes n LEFT JOIN h_node_redux r ON r.node_id = n.node_id ORDER BY n.rowid", ReadNode));

    /// <summary>Lists the newest nodes of a line, newest first.</summary>
    public IReadOnlyList<NodeInfo> Log(string line, int max = 20)
    {
        List<NodeInfo> list = [];
        string? id = LineOf(line).Head;
        while (id is not null && list.Count < max)
        {
            NodeInfo n = Node(id);
            list.Add(n);
            id = n.ParentId;
        }

        return list;
    }

    /// <summary>Resolves a line name, tag name, node id or unique node-id prefix to a node.</summary>
    public NodeInfo Resolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        LineInfo? line = Lines().FirstOrDefault(l => l.Name == reference);
        if (line is not null)
        {
            return Node(line.Head);
        }

        string? tag = Run(c => Scalar(c, "SELECT node_id FROM h_tags WHERE name = $n", ("$n", reference)) as string);
        if (tag is not null)
        {
            return Node(tag);
        }

        List<string> ids = Run(c => Query(c, "SELECT node_id FROM h_nodes WHERE node_id LIKE $p ORDER BY node_id", r => r.GetString(0), ("$p", reference.Replace("%", string.Empty, StringComparison.Ordinal) + "%")));
        return ids.Count == 1 ? Node(ids[0]) : throw new AmpleException("AMP5243", ids.Count == 0 ? $"'{reference}' is not a line, a tag or a node." : $"'{reference}' matches {ids.Count.ToString(CultureInfo.InvariantCulture)} nodes; give more characters.");
    }

    /// <summary>Determines whether the working files differ from the head of the current line.</summary>
    public async ValueTask<bool> IsDirtyAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string> head = Node(LineOf(CurrentLine).Head).Files;
        return (await WorkingFileIdsAsync(cancellationToken).ConfigureAwait(false)).Select(f => (f.Key, f.Value)).Order().SequenceEqual(head.Select(f => (f.Key, f.Value)).Order()) is false;
    }

    /// <summary>Takes a snapshot of the sources on the current line; returns null when nothing changed.</summary>
    public async ValueTask<NodeInfo?> CheckpointAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!await IsDirtyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        NodeInfo? made = null;
        await bus.RunWithStartRecordAsync("history.checkpoint", Json(new { message, line = CurrentLine }), async start => made = await AdvanceAsync(CurrentLine, "checkpoint", message, null, start.Seq, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return made;
    }

    /// <summary>Creates a new line at a node (default: the head of the current line). The working files stay as they are.</summary>
    public async ValueTask<LineInfo> ForkAsync(string name, string? from = null, CancellationToken cancellationToken = default)
    {
        if (Lines().Any(l => l.Name == name))
        {
            throw new AmpleException("AMP5241", $"There is already a line called '{name}'.");
        }

        NodeInfo at = Resolve(from ?? CurrentLine);
        await bus.RunAsync("history.fork", Json(new { name, from = at.NodeId }), () =>
        {
            Run(c => Exec(c, "INSERT INTO h_lines (name, kind, head, created_at) VALUES ($n, 'work', $h, $at)", ("$n", name), ("$h", at.NodeId), ("$at", Now())));
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return LineOf(name);
    }

    /// <summary>Switches the working files to a line. Unsaved work is checkpointed first (kind <c>auto</c>), so switching never loses anything.</summary>
    public async ValueTask GoAsync(string line, CancellationToken cancellationToken = default)
    {
        LineInfo target = LineOf(line);
        if (target.Archived)
        {
            throw new AmpleException("AMP5242", $"The line '{line}' is dropped. Restore it first.");
        }

        if (await IsDirtyAsync(cancellationToken).ConfigureAwait(false))
        {
            await bus.RunWithStartRecordAsync("history.checkpoint", Json(new { message = "automatic checkpoint before go " + line, line = CurrentLine, auto = true }), async start => await AdvanceAsync(CurrentLine, "auto", "automatic checkpoint before go " + line, null, start.Seq, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        string head = LineOf(line).Head;
        await bus.RunAsync("history.go", Json(new { line, head }), async () =>
        {
            await RestoreAsync(Node(head).Files, cancellationToken).ConfigureAwait(false);
            Run(c => Exec(c, "INSERT OR REPLACE INTO h_state (key, value) VALUES ('current', $l)", ("$l", line)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the files of a line, tag or node without changing anything (a read-only peek).</summary>
    public async ValueTask<IReadOnlyDictionary<string, string>> PeekAsync(string reference, CancellationToken cancellationToken = default)
    {
        NodeInfo n = Resolve(reference);
        Dictionary<string, string> texts = [];
        foreach ((string path, string id) in n.Files)
        {
            await using Stream s = bank.OpenData(id);
            using StreamReader reader = new(s, Encoding.UTF8);
            texts[path] = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return texts;
    }

    /// <summary>Lists the file differences from one state to another.</summary>
    public IReadOnlyList<FileChange> Diff(string from, string to)
    {
        IReadOnlyDictionary<string, string> a = Resolve(from).Files;
        IReadOnlyDictionary<string, string> b = Resolve(to).Files;
        return [.. a.Keys.Union(b.Keys).Order(StringComparer.Ordinal).Select(p => !a.TryGetValue(p, out string? x) ? new FileChange(p, "added") : !b.TryGetValue(p, out string? y) ? new FileChange(p, "removed") : x == y ? null : new FileChange(p, "changed")).OfType<FileChange>()];
    }

    /// <summary>Creates a locked tag. Moving or removing a tag is a dangerous act (see <see cref="RetagAct"/>).</summary>
    public async ValueTask TagAsync(string name, string reference, string reason, CancellationToken cancellationToken = default)
    {
        if (Tags().Any(t => t.Name == name))
        {
            throw new AmpleException("AMP5240", $"The tag '{name}' exists and is locked. Moving or removing it needs the dangerous-act contract.");
        }

        Quality.QualityService.Enforce(root, bank, clock, "tag");
        NodeInfo n = Resolve(reference);
        await bus.RunAsync("history.tag", Json(new { name, node = n.NodeId, reason }), () =>
        {
            Run(c => Exec(c, "INSERT INTO h_tags (name, node_id, reason, created_at) VALUES ($n, $id, $r, $at)", ("$n", name), ("$id", n.NodeId), ("$r", reason), ("$at", Now())));
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shows what promoting one line into another would change.</summary>
    public IReadOnlyList<FileChange> PreviewPromote(string from = "garage", string to = "studio") => Diff(to, from);

    /// <summary>Makes the head of <paramref name="from"/> the new head of <paramref name="to"/> (default garage into studio). The old head stays in the history. Promote is replace-with-preview, not a merge.</summary>
    public async ValueTask<NodeInfo> PromoteAsync(string from = "garage", string to = "studio", CancellationToken cancellationToken = default)
    {
        LineInfo source = LineOf(from);
        LineOf(to);
        Quality.QualityService.Enforce(root, bank, clock, "promote");
        NodeInfo? made = null;
        await bus.RunWithStartRecordAsync("history.promote", Json(new { from, to, changes = PreviewPromote(from, to).Count }), async start =>
        {
            made = await AdvanceAsync(to, "promote", $"promote {from}", Node(source.Head).Files, start.Seq, cancellationToken).ConfigureAwait(false);
            if (CurrentLine == to)
            {
                await RestoreAsync(made.Files, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        return made!;
    }

    /// <summary>Archives a line (recoverable). Used by <see cref="DropLineAct"/>.</summary>
    internal void Archive(string name, bool archived, string reason) => Run(c =>
    {
        Exec(c, "UPDATE h_lines SET archived = $a WHERE name = $n", ("$a", archived ? 1 : 0), ("$n", name));
        Exec(c, "INSERT INTO h_tombstones (kind, name, reason, at, detail) VALUES ($k, $n, $r, $at, $d)", ("$k", archived ? "line-dropped" : "line-restored"), ("$n", name), ("$r", reason), ("$at", Now()), ("$d", LineOf(name).Head));
    });

    /// <summary>Restores a dropped line.</summary>
    public async ValueTask RestoreLineAsync(string name, string reason, CancellationToken cancellationToken = default)
    {
        LineInfo line = LineOf(name);
        await bus.RunAsync("history.line.restore", Json(new { name, reason }), async () =>
        {
            await BringBackContentAsync(line, cancellationToken).ConfigureAwait(false);
            Archive(name, false, reason);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts the content of a dropped line back into the takebank from <c>.ample/history-archive</c> when a clean-up removed it; content that is not archived is left as it is (going there then fails with AMP5246).</summary>
    private async ValueTask BringBackContentAsync(LineInfo line, CancellationToken cancellationToken)
    {
        for (string? id = line.Head; id is not null; id = Node(id).ParentId)
        {
            NodeInfo node = Node(id);
            bool complete = true;
            foreach (string blob in node.Files.Values.Distinct())
            {
                if (bank.DataExists(blob))
                {
                    continue;
                }

                string? file = HistoryArchive.Find(root, blob);
                if (file is not null)
                {
                    await using FileStream s = File.OpenRead(file);
                    if (string.Equals(await bank.PutDataAsync(s, cancellationToken).ConfigureAwait(false), blob, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                complete = false;
            }

            if (complete && node.Reclaimed)
            {
                Run(c => Exec(c, "UPDATE h_nodes SET reclaimed = 0 WHERE node_id = $id", ("$id", id)));
            }
        }
    }

    /// <summary>Moves or removes a tag (dangerous act body).</summary>
    internal void MoveTag(string name, string? newNode, string reason) => Run(c =>
    {
        string old = (string?)Scalar(c, "SELECT node_id FROM h_tags WHERE name = $n", ("$n", name)) ?? throw new AmpleException("AMP5243", $"There is no tag '{name}'.");
        if (newNode is null)
        {
            Exec(c, "DELETE FROM h_tags WHERE name = $n", ("$n", name));
        }
        else
        {
            Exec(c, "UPDATE h_tags SET node_id = $id WHERE name = $n", ("$id", newNode), ("$n", name));
        }

        Exec(c, "INSERT INTO h_tombstones (kind, name, reason, at, detail) VALUES ('tag-changed', $n, $r, $at, $d)", ("$n", name), ("$r", reason), ("$at", Now()), ("$d", old));
    });

    /// <summary>Computes what a cleanup would remove: nodes that no active line and no tag can reach, and the file contents only they use (content used by a take is never touched).</summary>
    public CleanupPlan PlanCleanup()
    {
        HashSet<string> reachable = ReachableNodes();
        List<NodeInfo> unreachable = [.. Run(c => Query(c, "SELECT n.node_id, n.parent_id, n.kind, n.message, n.at, n.files_json, n.reclaimed, r.sequence FROM h_nodes n LEFT JOIN h_node_redux r ON r.node_id = n.node_id WHERE n.reclaimed = 0", ReadNode)).Where(n => !reachable.Contains(n.NodeId))];
        HashSet<string> keep = [.. reachable.SelectMany(id => Node(id).Files.Values)];
        List<string> blobs = [.. unreachable.SelectMany(n => n.Files.Values).Distinct().Where(b => !keep.Contains(b) && bank.DataExists(b) && !bank.IsUsedByTake(b)).Order(StringComparer.Ordinal)];
        List<string> droppedLines = [];
        HashSet<string> droppedBlobs = [];
        foreach (LineInfo line in Lines().Where(l => l.Archived))
        {
            List<NodeInfo> mine = DroppedLineRevisions(line, reachable, unreachable);
            if (mine.Count > 0)
            {
                droppedLines.Add(line.Name);
                droppedBlobs.UnionWith(mine.SelectMany(n => n.Files.Values));
            }
        }

        List<string> archived = [.. blobs.Where(droppedBlobs.Contains)];
        return new CleanupPlan([.. unreachable.Select(n => n.NodeId)], blobs, blobs.Sum(bank.SizeOfData), droppedLines, archived, archived.Sum(bank.SizeOfData));
    }

    private List<NodeInfo> DroppedLineRevisions(LineInfo line, HashSet<string> reachable, List<NodeInfo> unreachable)
    {
        HashSet<string> open = [.. unreachable.Select(n => n.NodeId)];
        List<NodeInfo> mine = [];
        for (string? id = line.Head; id is not null && open.Contains(id); id = Node(id).ParentId)
        {
            mine.Add(Node(id));
        }

        _ = reachable;
        return mine;
    }

    internal void ExecuteCleanup(CleanupPlan plan, string reason)
    {
        HashSet<string> reachable = ReachableNodes();
        foreach (string id in plan.Nodes.Where(reachable.Contains))
        {
            throw new AmpleException("AMP5245", $"Node {id} became reachable; the cleanup plan is out of date.");
        }

        HashSet<string> keep = [.. reachable.SelectMany(id => Node(id).Files.Values)];

        // dropped lines are copied to .ample/history-archive and read back BEFORE the first original is removed; a failure here removes nothing
        List<string> toArchive = [.. (plan.ArchivedBlobs ?? []).Where(b => !keep.Contains(b) && bank.DataExists(b) && HistoryArchive.Find(root, b) is null)];
        if (toArchive.Count > 0)
        {
            List<NodeInfo> openNodes = [.. plan.Nodes.Select(Node)];
            List<(string, string, IReadOnlyList<(string, string, IReadOnlyDictionary<string, string>)>)> lines = [];
            foreach (string name in plan.ArchivedLines ?? [])
            {
                LineInfo line = LineOf(name);
                List<(string, string, IReadOnlyDictionary<string, string>)> revisions = [.. DroppedLineRevisions(line, reachable, openNodes).Select(n => (n.NodeId, n.Message, n.Files))];
                lines.Add((name, line.Head, revisions));
            }

            HistoryArchive.Write(root, clock.UtcNow, reason, lines, toArchive, bank.OpenData);
        }

        foreach (string blob in plan.Blobs.Where(b => !keep.Contains(b)))
        {
            if (bank.IsUsedByTake(blob))
            {
                continue;   // a take started using it since the dry run: it stays
            }

            bank.RemoveData(blob);
        }

        Run(c =>
        {
            foreach (string id in plan.Nodes)
            {
                Exec(c, "UPDATE h_nodes SET reclaimed = 1 WHERE node_id = $id", ("$id", id));
            }

            Exec(c, "INSERT INTO h_tombstones (kind, name, reason, at, detail) VALUES ('cleanup', 'cleanup', $r, $at, $d)", ("$r", reason), ("$at", Now()), ("$d", Json(new { nodes = plan.Nodes.Count, blobs = plan.Blobs.Count, plan.Bytes })));
        });
    }

    /// <summary>Gets the tombstones (what was dropped, re-tagged or cleaned up, and why).</summary>
    public IReadOnlyList<(string Kind, string Name, string Reason, string Detail)> Tombstones() => Run(c => Query(c, "SELECT kind, name, reason, detail FROM h_tombstones ORDER BY id", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3))));

    /// <summary>Gets a line by name.</summary>
    public LineInfo LineOf(string name) => Lines().FirstOrDefault(l => l.Name == name) ?? throw new AmpleException("AMP5243", $"There is no line '{name}'.");

    private HashSet<string> ReachableNodes()
    {
        HashSet<string> seen = [];
        Queue<string> todo = new([.. Lines().Where(l => !l.Archived).Select(l => l.Head), .. Tags().Select(t => t.NodeId)]);
        while (todo.Count > 0)
        {
            string id = todo.Dequeue();
            if (seen.Add(id) && Node(id).ParentId is string parent)
            {
                todo.Enqueue(parent);
            }
        }

        return seen;
    }

    private async ValueTask<NodeInfo> AdvanceAsync(string line, string kind, string message, IReadOnlyDictionary<string, string>? files, long reduxSequence, CancellationToken cancellationToken)
    {
        LineInfo l = LineOf(line);
        NodeInfo node = await SnapshotAsync(kind, message, l.Head, cancellationToken, files).ConfigureAwait(false);
        Run(c =>
        {
            Exec(c, "INSERT OR IGNORE INTO h_nodes (node_id, parent_id, kind, message, at, files_json) VALUES ($id, $p, $k, $m, $at, $f)", ("$id", node.NodeId), ("$p", node.ParentId), ("$k", kind), ("$m", message), ("$at", Now()), ("$f", JsonSerializer.Serialize(node.Files)));
            Exec(c, "INSERT OR IGNORE INTO h_node_redux (node_id, sequence) VALUES ($id, $seq)", ("$id", node.NodeId), ("$seq", reduxSequence));
            Exec(c, "UPDATE h_lines SET head = $id WHERE name = $n", ("$id", node.NodeId), ("$n", line));
        });
        return node with { ReduxSequence = reduxSequence };
    }

    private async ValueTask<NodeInfo> SnapshotAsync(string kind, string message, string? parent, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? files = null)
    {
        Dictionary<string, string> map = [];
        if (files is null)
        {
            foreach ((string path, (string id, _)) in await WorkingFilesAsync(cancellationToken).ConfigureAwait(false))
            {
                map[path] = id;
            }
        }
        else
        {
            foreach ((string p, string id) in files)
            {
                map[p] = id;
            }
        }

        string text = "node\n" + parent + "\n" + string.Join('\n', map.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + "=" + m.Value)) + "\n" + kind + "\n" + message + "\n" + Now();
        string nodeId = "node:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        return new NodeInfo(nodeId, parent, kind, message, clock.UtcNow, map, false, null);
    }

    /// <summary>The data id of every source file, without storing anything: the id is the sha256 of the text, the same one that storing it gives. A check for unsaved changes used to write every source file into the takebank (a durable write each), which took seconds on a big project.</summary>
    private async ValueTask<Dictionary<string, string>> WorkingFileIdsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> result = [];
        foreach (string file in ListSources(root))
        {
            string text = await fs.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        return result;
    }

    private async ValueTask<Dictionary<string, (string Id, string Text)>> WorkingFilesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, (string, string)> result = [];
        foreach (string file in ListSources(root))
        {
            string text = await fs.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            string id = await bank.PutDataAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), cancellationToken).ConfigureAwait(false);
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = (id, text);
        }

        return result;
    }

    private IEnumerable<string> ListSources(string folder)
    {
        foreach (string file in fs.GetFiles(folder).Where(f => SourcePatterns.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            yield return file;
        }

        foreach (string dir in fs.GetDirectories(folder).Where(d => !string.Equals(Path.GetFileName(d), ProjectLayout.AmpleFolder, StringComparison.Ordinal)))
        {
            foreach (string file in ListSources(dir))
            {
                yield return file;
            }
        }
    }

    private async ValueTask RestoreAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken)
    {
        Dictionary<string, (string Id, string Text)> working = await WorkingFilesAsync(cancellationToken).ConfigureAwait(false);
        CommandContext context = new(fs, root, clock);

        // check that everything needed is still there before touching a single working file (a clean-up may have reclaimed the content of a dropped line)
        foreach ((string path, string id) in target)
        {
            if ((!working.TryGetValue(path, out (string Id, string Text) have) || have.Id != id) && !bank.DataExists(id))
            {
                throw new AmpleException("AMP5246", $"The saved content of {path} is gone (a history clean-up removed it), so this version cannot be restored. Nothing in the project was changed.");
            }
        }

        foreach ((string path, string id) in target)
        {
            if (!working.TryGetValue(path, out (string Id, string Text) w) || w.Id != id)
            {
                await using Stream s = bank.OpenData(id);
                using StreamReader reader = new(s, Encoding.UTF8);
                await bus.ExecuteAsync(new WriteFileCommand(path, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
            }
        }

        // a module is never deleted by going back: a revision made before modules were part of the history
        // cannot tell "no module" from "not recorded"
        IEnumerable<string> gone = working.Keys.Where(
            p => !target.ContainsKey(p) && !p.EndsWith(".module", StringComparison.OrdinalIgnoreCase));
        foreach (string path in gone)
        {
            await bus.ExecuteAsync(new DeleteFileCommand(path), cancellationToken).ConfigureAwait(false);
        }

        _ = context;
    }

    private SqliteConnection Connect() => TakeDatabase.Open(dbPath);

    private T Run<T>(Func<SqliteConnection, T> action)
    {
        try
        {
            using SqliteConnection c = Connect();
            return action(c);
        }
        catch (SqliteException ex)
        {
            int at = ex.Message.IndexOf("AMP52", StringComparison.Ordinal);
            throw at >= 0 ? new AmpleException(ex.Message.Substring(at, 7), ex.Message[(at + 9)..]) : ex;
        }
    }

    private void Run(Action<SqliteConnection> action) => Run<int>(c =>
    {
        action(c);
        return 0;
    });

    private string Now() => clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static NodeInfo ReadNode(SqliteDataReader r) => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5))!, r.GetInt64(6) != 0, r.IsDBNull(7) ? null : r.GetInt64(7));

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using SqliteDataReader r = cmd.ExecuteReader();
        List<T> list = [];
        while (r.Read())
        {
            list.Add(read(r));
        }

        return list;
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }
}
