using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.Takes;

/// <summary>Per-project storage options, kept in <c>.ample/storage.json</c> (all optional; the defaults apply when the file or a key is missing).</summary>
/// <param name="SoftLimitGb">The size of the takebank above which a clean-up is suggested (never deleted automatically); 0 turns the suggestion off.</param>
/// <param name="KeepSuperseded">How many older takes of a block are kept besides the current one when a clean-up runs (0: only the current take).</param>
/// <param name="KeepMinutes">Takes younger than this are never cleaned up (a running build, an open preview).</param>
/// <param name="DataPath">Folder for the take data on another drive, or null for <c>.ample/data</c>.</param>
/// <param name="ArchivePath">Folder a clean-up moves reclaimable data to instead of deleting it, or null to delete.</param>
/// <param name="Intermediates"><c>wav</c> or <c>compressed</c>: how intermediate takes are stored (see the compression of take data).</param>
/// <param name="SilenceRuns">Whether runs of digital silence in intermediate takes are stored as a length.</param>
public sealed record StorageSettings(double SoftLimitGb, int KeepSuperseded, int KeepMinutes, string? DataPath, string? ArchivePath, string Intermediates, bool SilenceRuns)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Gets the defaults: a 10 GB soft limit, only the current takes kept, takes of the last hour protected, plain data folder, compressed intermediates with silence runs.</summary>
    public static StorageSettings Default { get; } = new(10, 0, 60, null, null, "compressed", true);

    /// <summary>Reads the settings of a project (the defaults when there is no file or it cannot be read).</summary>
    public static StorageSettings Load(string projectRoot)
    {
        string path = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "storage.json");
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            DateTime written = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out (DateTime Written, StorageSettings Settings) hit) && hit.Written == written)
            {
                return hit.Settings;
            }

            StorageSettings loaded = Read(path);
            Cache[path] = (written, loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, StorageSettings Settings)> Cache = new();

    private static StorageSettings Read(string path)
    {
        try
        {

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            string? Text(string name) => r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            double Number(string name, double fallback) => r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            return new StorageSettings(
                Math.Clamp(Number("softLimitGb", Default.SoftLimitGb), 0, 100_000),
                (int)Math.Clamp(Number("keepSuperseded", Default.KeepSuperseded), 0, 50),
                (int)Math.Clamp(Number("keepMinutes", Default.KeepMinutes), 0, 100_000),
                Text("dataPath"),
                Text("archivePath"),
                Text("intermediates") is "wav" ? "wav" : "compressed",
                !(r.TryGetProperty("silenceRuns", out JsonElement sr) && sr.ValueKind == JsonValueKind.False));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    /// <summary>
    /// Fails (AMP5606) when <c>storage.json</c> exists but cannot be read. A clean-up that guessed the defaults would ignore the keep rules and the
    /// archive folder the person chose (it would delete where the person wanted data archived), so it refuses instead.
    /// </summary>
    public static void EnsureReadable(string projectRoot)
    {
        string path = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "storage.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("not an object");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new AmpleException("AMP5606", "The storage options (.ample/storage.json) cannot be read, so the clean-up does not know your keep rules and archive folder. Nothing was removed. Repair or remove the file.");
        }
    }

    /// <summary>Writes the settings.</summary>
    public void Save(string projectRoot)
    {
        string path = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "storage.json");
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["softLimitGb"] = SoftLimitGb,
            ["keepSuperseded"] = KeepSuperseded,
            ["keepMinutes"] = KeepMinutes,
            ["dataPath"] = DataPath,
            ["archivePath"] = ArchivePath,
            ["intermediates"] = Intermediates,
            ["silenceRuns"] = SilenceRuns,
        }, Indented);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json + "\n");
        File.Move(temp, path, true);
    }
}

/// <summary>One take as the storage overview sees it.</summary>
/// <param name="TakeId">The take.</param>
/// <param name="Block">The block name at the time.</param>
/// <param name="Bytes">The data that only this take (among the takes that stay) would free.</param>
/// <param name="Protected">True when a clean-up never touches it.</param>
/// <param name="Reason">Why it is protected (<c>current</c>, <c>input</c>, <c>chosen</c>, <c>baked</c>, <c>history</c>, <c>recent</c>, <c>final</c>) or reclaimable (<c>superseded</c>, <c>missing</c>).</param>
/// <param name="Created">When it was made.</param>
public sealed record StorageTake(string TakeId, string Block, long Bytes, bool Protected, string Reason, DateTimeOffset Created);

/// <summary>A line of an impact table.</summary>
/// <param name="Name">The reason or block.</param>
/// <param name="Takes">How many takes.</param>
/// <param name="Bytes">How many bytes.</param>
public sealed record StorageLine(string Name, int Takes, long Bytes);

/// <summary>What the takebank and the project's working folders use, and what could be given back.</summary>
public sealed record StorageOverview(
    long DataBytes,
    int Takes,
    long ProtectedBytes,
    long ReclaimableBytes,
    long AiBytes,
    long TmpBytes,
    long HistoryBytes,
    long FreeBytes,
    long TotalBytes,
    double SoftLimitGb,
    bool OverLimit,
    bool LowDisk,
    IReadOnlyList<StorageLine> ByReason,
    IReadOnlyList<StorageLine> ByBlock,
    int DuplicateTakeRows,
    string DataFolder);

/// <summary>A clean-up as a dry run: exactly what would be done.</summary>
public sealed record StorageCleanPlan(
    IReadOnlyList<string> TakeIds,
    IReadOnlyList<string> DataIds,
    long Bytes,
    IReadOnlyList<StorageLine> ByReason,
    IReadOnlyList<StorageLine> ByBlock,
    long ProtectedBytes,
    long FreeBefore,
    long FreeAfter,
    IReadOnlyList<string> TempFolders,
    long TempBytes,
    string Fingerprint);

/// <summary>Progress of a running clean-up.</summary>
/// <param name="Done">Items done.</param>
/// <param name="Total">Items to do.</param>
/// <param name="Bytes">Bytes freed so far.</param>
/// <param name="TotalBytes">Bytes to free.</param>
public sealed record StorageProgress(int Done, int Total, long Bytes, long TotalBytes);

/// <summary>
/// The storage overview, the protection rules and the clean-up of a project's takebank. One function decides what is protected
/// (<see cref="Classify()"/>); the overview, the clean-up and the tests all use it, and nothing else decides.
/// </summary>
public sealed partial class StorageService(Takebank bank, string projectRoot, string databasePath, IClock clock)
{
    private static readonly string[] SourceExtensions = [".amplet", ".amplec", ".module", ".ampleseq", ".seq", ".lock", ".json", ".txt"];

    [GeneratedRegex("sha256:[0-9a-f]{64}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TakeIdPattern();

    /// <summary>The data of every live take, the size of every blob and whether it is on disk, read once.</summary>
    private sealed class DataMap(Dictionary<string, List<string>> takeData, Dictionary<string, long> sizes, HashSet<string> present)
    {
        public List<string> DataOf(string takeId) => takeData.TryGetValue(takeId, out List<string>? list) ? list : [];

        public Dictionary<string, long>.KeyCollection AllIds() => sizes.Keys;

        public bool Exists(string dataId) => present.Contains(dataId);

        public long Size(string dataId) => sizes.GetValueOrDefault(dataId);
    }

    private DataMap LoadData()
    {
        Dictionary<string, List<string>> takeData = new(StringComparer.Ordinal);
        Dictionary<string, long> sizes = new(StringComparer.Ordinal);
        using SqliteConnection c = TakeDatabase.Open(databasePath);
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT take_id, data_id FROM take_data";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!takeData.TryGetValue(r.GetString(0), out List<string>? list))
                {
                    takeData[r.GetString(0)] = list = [];
                }

                list.Add(r.GetString(1));
            }
        }

        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT data_id, size FROM data WHERE removed = 0";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                sizes[r.GetString(0)] = r.GetInt64(1);
            }
        }

        // what a blob takes on disk (a compressed take is smaller than the size of its content)
        HashSet<string> present = [];
        foreach (string id in sizes.Keys.ToList())
        {
            long onDisk = bank.DiskSizeOfData(id);
            if (onDisk > 0)
            {
                present.Add(id);
                sizes[id] = onDisk;
            }
        }

        return new DataMap(takeData, sizes, present);
    }

    /// <summary>Gets the settings of the project.</summary>
    public StorageSettings Settings => StorageSettings.Load(projectRoot);

    /// <summary>
    /// Decides for every live take whether it is protected, and why. Protected: chosen or variant takes, takes named in the current sources or in any
    /// history revision (baked and pinned takes), the current take of every block and what it was made from, finals, and everything younger than
    /// <see cref="StorageSettings.KeepMinutes"/>. Reclaimable: older takes of a block (beyond <see cref="StorageSettings.KeepSuperseded"/>) and takes whose data is gone.
    /// </summary>
    public IReadOnlyList<StorageTake> Classify()
    {
        return Classify(LoadData());
    }

    private List<StorageTake> Classify(DataMap data)
    {
        StorageSettings settings = Settings;
        DateTimeOffset now = clock.UtcNow;
        using SqliteConnection c = TakeDatabase.Open(databasePath);
        List<(string TakeId, string PlanId, string BlockUuid, string Block, DateTimeOffset Created)> takes = [];
        using (SqliteCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT t.take_id, t.plan_id, p.block_uuid, p.json, t.created_at FROM takes t JOIN plans p ON p.plan_id = t.plan_id WHERE t.tombstoned = 0 ORDER BY t.created_at DESC, t.take_id";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string block = r.GetString(2);
                try
                {
                    using JsonDocument plan = JsonDocument.Parse(r.GetString(3));
                    if (plan.RootElement.TryGetProperty("blockPath", out JsonElement bp) && bp.GetString() is { Length: > 0 } name)
                    {
                        block = name;
                    }
                }
                catch (JsonException)
                {
                    // the uuid is a good enough name
                }

                takes.Add((r.GetString(0), r.GetString(1), r.GetString(2), block, DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture)));
            }
        }

        Dictionary<string, string> protectedBy = new(StringComparer.Ordinal);
        void Protect(string takeId, string reason) => protectedBy.TryAdd(takeId, reason);

        // chosen takes, variants and regenerations in progress
        foreach (string sql in new[] { "SELECT take_id FROM c_selected", "SELECT take_id FROM c_variants", "SELECT old_take FROM c_regen UNION SELECT new_take FROM c_regen" })
        {
            try
            {
                using SqliteCommand cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    Protect(r.GetString(0), "chosen");
                }
            }
            catch (SqliteException ex) when (IsMissingTable(ex))
            {
                // the table does not exist (nothing was ever chosen); any other database error aborts the plan (fail safe)
            }
        }

        // finals: the master take each final was cut from
        foreach (string id in new Ample.Audio.Engine.FinalsStore(projectRoot).TakeIds())
        {
            Protect(id, "final");
        }

        // take ids named in the current sources (baked and pinned takes), in the project's own working files (finals, assistant proposals,
        // unsaved work) and in every history revision (also of dropped lines). Anything that cannot be read aborts the plan: nothing is deleted on a guess.
        HashSet<string> named = [];
        foreach (string file in SourceFiles())
        {
            ScanFile(file, named);
        }

        foreach (string file in AmpleWorkingFiles())
        {
            ScanFile(file, named);
        }

        try
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT node_id, files_json FROM h_nodes";
            using SqliteDataReader r = cmd.ExecuteReader();
            HashSet<string> blobs = [];
            while (r.Read())
            {
                try
                {
                    using JsonDocument files = JsonDocument.Parse(r.GetString(1));
                    foreach (JsonProperty p in files.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } id)
                        {
                            blobs.Add(id);
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    throw new AmpleException("AMP5606", $"The history revision {r.GetString(0)} cannot be read, so the clean-up cannot tell which takes it names. Nothing was removed.");
                }
            }

            foreach (string blob in blobs.Where(bank.DataExists))
            {
                using Stream blobStream = bank.OpenData(blob);
                ScanStream(blobStream, named);
            }

            // the safety copy of dropped lines (a clean-up removed their content from the takebank): the takes they name stay protected so Bring back finds them
            foreach (string archived in Ample.Storage.History.HistoryArchive.BlobFiles(projectRoot))
            {
                using FileStream archivedStream = File.OpenRead(archived);
                ScanStream(archivedStream, named);
            }
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            // no history yet
        }

        HashSet<string> live = [.. takes.Select(t => t.TakeId)];
        foreach (string id in named.Where(live.Contains))
        {
            Protect(id, "baked");
        }

        // the newest take of every block is current; what it was made from stays too
        Dictionary<string, string> planOf = takes.ToDictionary(t => t.TakeId, t => t.PlanId, StringComparer.Ordinal);
        Dictionary<string, string> planJson = new(StringComparer.Ordinal);
        using (SqliteCommand pj = c.CreateCommand())
        {
            pj.CommandText = "SELECT plan_id, json FROM plans";
            using SqliteDataReader pr = pj.ExecuteReader();
            while (pr.Read())
            {
                planJson[pr.GetString(0)] = pr.GetString(1);
            }
        }
        List<string> currents = [.. takes.GroupBy(t => t.BlockUuid, StringComparer.Ordinal).Select(g => g.First().TakeId)];
        Dictionary<string, int> rank = new(StringComparer.Ordinal);
        foreach (IGrouping<string, (string TakeId, string PlanId, string BlockUuid, string Block, DateTimeOffset Created)> g in takes.GroupBy(t => t.BlockUuid, StringComparer.Ordinal))
        {
            int i = 0;
            foreach ((string takeId, _, _, _, _) in g)
            {
                rank[takeId] = i++;
            }
        }

        Queue<string> pending = new(currents);
        HashSet<string> seen = [];
        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            if (!seen.Add(id) || !live.Contains(id))
            {
                continue;
            }

            Protect(id, currents.Contains(id) ? "current" : "input");
            foreach (string input in InputTakes(planJson[planOf[id]]))
            {
                pending.Enqueue(input);
            }
        }

        List<StorageTake> result = [];
        foreach ((string takeId, _, _, string block, DateTimeOffset created) in takes)
        {
            List<string> ids = data.DataOf(takeId);
            long bytes = ids.Distinct(StringComparer.Ordinal).Where(data.Exists).Sum(data.Size);
            if (protectedBy.TryGetValue(takeId, out string? why))
            {
                result.Add(new StorageTake(takeId, block, bytes, true, why, created));
            }
            else if (now - created < TimeSpan.FromMinutes(settings.KeepMinutes))
            {
                result.Add(new StorageTake(takeId, block, bytes, true, "recent", created));
            }
            else if (rank.GetValueOrDefault(takeId) <= settings.KeepSuperseded)
            {
                result.Add(new StorageTake(takeId, block, bytes, true, "kept", created));
            }
            else
            {
                result.Add(new StorageTake(takeId, block, bytes, false, ids.All(data.Exists) ? "superseded" : "missing", created));
            }
        }

        return result;
    }

    /// <summary>Builds the overview (sizes from the database and file lengths, never from reading audio).</summary>
    public StorageOverview Overview(Func<long>? aiBytes = null)
    {
        StorageSettings settings = Settings;
        DataMap map = LoadData();
        List<StorageTake> takes = Classify(map);
        HashSet<string> protectedData = [.. takes.Where(t => t.Protected).SelectMany(t => map.DataOf(t.TakeId))];
        long protectedBytes = protectedData.Where(map.Exists).Sum(map.Size);
        long reclaimable = takes.Where(t => !t.Protected).SelectMany(t => map.DataOf(t.TakeId)).Distinct(StringComparer.Ordinal)
            .Where(d => !protectedData.Contains(d) && map.Exists(d)).Sum(map.Size);
        long data = takes.SelectMany(t => map.DataOf(t.TakeId)).Distinct(StringComparer.Ordinal).Where(map.Exists).Sum(map.Size);
        string ample = Path.Combine(projectRoot, ProjectLayout.AmpleFolder);
        long free = 0, total = 0;
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(settings.DataPath ?? ample))!);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // unknown drive: no numbers
        }

        List<StorageLine> byReason = [.. takes.GroupBy(t => t.Reason).Select(g => new StorageLine(g.Key, g.Count(), g.Sum(t => t.Bytes))).OrderByDescending(l => l.Bytes)];
        List<StorageLine> byBlock = [.. takes.GroupBy(t => t.Block).Select(g => new StorageLine(g.Key, g.Count(), g.Sum(t => t.Bytes))).OrderByDescending(l => l.Bytes).Take(30)];
        int duplicates = takes.Count - takes.Select(t => string.Join('|', map.DataOf(t.TakeId).Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count();
        return new StorageOverview(
            data,
            takes.Count,
            protectedBytes,
            reclaimable,
            aiBytes?.Invoke() ?? FolderBytes(Path.Combine(ample, "ai")),
            FolderBytes(Path.Combine(ample, "tmp")),
            FileBytes(Path.Combine(ample, "history.db")),
            free,
            total,
            settings.SoftLimitGb,
            settings.SoftLimitGb > 0 && data + FolderBytes(Path.Combine(ample, "ai")) > settings.SoftLimitGb * 1024 * 1024 * 1024,
            free > 0 && free < 5L * 1024 * 1024 * 1024,
            byReason,
            byBlock,
            duplicates,
            settings.DataPath ?? Path.Combine(ample, "data"));
    }

    /// <summary>Plans a clean-up (a dry run): the takes to tombstone and the blobs that go with them, never anything protected.</summary>
    /// <param name="includeReasons">Which reclaimable reasons to include (null: all).</param>
    /// <param name="tempFolders">Temporary working folders (of finished AI tasks, older temporary files) that may go too, with their size.</param>
    public StorageCleanPlan Plan(IReadOnlySet<string>? includeReasons = null, IReadOnlyList<(string Path, long Bytes)>? tempFolders = null)
    {
        StorageSettings.EnsureReadable(projectRoot);
        DataMap map = LoadData();
        List<StorageTake> takes = Classify(map);
        HashSet<string> keepData = [.. takes.Where(t => t.Protected).SelectMany(t => map.DataOf(t.TakeId))];
        List<StorageTake> victims = [.. takes.Where(t => !t.Protected && (includeReasons is null || includeReasons.Contains(t.Reason)))];
        HashSet<string> victimIds = [.. victims.Select(v => v.TakeId)];
        // a blob that a take outside the plan (or a tombstoned-later take) still uses stays
        HashSet<string> stillUsed = [.. takes.Where(t => !victimIds.Contains(t.TakeId)).SelectMany(t => map.DataOf(t.TakeId)), .. keepData];
        List<string> dataIds = [.. victims.SelectMany(v => map.DataOf(v.TakeId)).Distinct(StringComparer.Ordinal).Where(d => !stillUsed.Contains(d) && map.Exists(d)).Order(StringComparer.Ordinal)];
        long bytes = dataIds.Sum(map.Size);
        long tempBytes = tempFolders?.Sum(f => f.Bytes) ?? 0;
        long free = Overview().FreeBytes;
        List<StorageLine> byReason = [.. victims.GroupBy(v => v.Reason).Select(g => new StorageLine(g.Key, g.Count(), g.Sum(v => v.Bytes))).OrderByDescending(l => l.Bytes)];
        List<StorageLine> byBlock = [.. victims.GroupBy(v => v.Block).Select(g => new StorageLine(g.Key, g.Count(), g.Sum(v => v.Bytes))).OrderByDescending(l => l.Bytes).Take(30)];
        string identity = string.Join(',', victimIds.Order(StringComparer.Ordinal)) + ";" + string.Join(',', dataIds) + ";" + string.Join(',', (tempFolders ?? []).Select(f => f.Path).Order(StringComparer.Ordinal));
        string fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16];
        return new StorageCleanPlan([.. victimIds.Order(StringComparer.Ordinal)], dataIds, bytes, byReason, byBlock, keepData.Where(map.Exists).Sum(map.Size), free, free + bytes + tempBytes, [.. tempFolders?.Select(f => f.Path) ?? []], tempBytes, fingerprint);
    }

    /// <summary>
    /// Runs a planned clean-up. The plan is checked against a fresh classification first, so nothing that became protected since the dry run is touched.
    /// Takes are tombstoned (their record and plan stay; the take can be made again), the blobs are deleted or moved to the archive folder.
    /// </summary>
    /// <param name="plan">The dry run.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation; stops between blobs and leaves a consistent store.</param>
    public async Task<StorageProgress> RunAsync(StorageCleanPlan plan, IProgress<StorageProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageSettings.EnsureReadable(projectRoot);
        StorageSettings settings = Settings;
        string? archive = CheckArchive(settings.ArchivePath);
        DataMap map = LoadData();
        List<StorageTake> fresh = Classify(map);
        HashSet<string> allowed = [.. fresh.Where(t => !t.Protected).Select(t => t.TakeId)];
        List<string> takeIds = [.. plan.TakeIds.Where(allowed.Contains)];
        HashSet<string> keep = [.. fresh.Where(t => t.Protected || !takeIds.Contains(t.TakeId)).SelectMany(t => map.DataOf(t.TakeId))];
        List<string> blobs = [.. plan.DataIds.Where(d => !keep.Contains(d) && bank.DataExists(d))];
        long total = blobs.Sum(bank.DiskSizeOfData);
        int done = 0;
        long freed = 0;
        int steps = takeIds.Count + blobs.Count;
        foreach (string id in takeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bank.Tombstone(id, "storage clean-up");
            done++;
            progress?.Report(new StorageProgress(done, steps, freed, total));
            await Task.Yield();
        }

        foreach (string blob in blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = bank.DiskSizeOfData(blob);
            bank.RemoveDataOfDroppedTakes(blob, archive);
            freed += size;
            done++;
            progress?.Report(new StorageProgress(done, steps, freed, total));
            if (done % 20 == 0)
            {
                await Task.Yield();
            }
        }

        return new StorageProgress(done, steps, freed, total);
    }

    /// <summary>
    /// Validates the archive folder before anything is tombstoned: it must be creatable and must not be, contain or lie inside a take data folder
    /// (a move onto the same file would leave the data flagged as removed while nothing was archived). Returns the full path, or null to delete.
    /// </summary>
    private string? CheckArchive(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(archivePath);
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AmpleException("AMP5603", $"The archive folder {archivePath} cannot be used: {ex.Message} Nothing was removed.");
        }

        foreach (string data in DataFolders())
        {
            if (Overlaps(full, data))
            {
                throw new AmpleException("AMP5603", "The archive folder must be a different folder than the take data (and not inside it or around it). Nothing was removed.");
            }
        }

        return full;
    }

    private IEnumerable<string> DataFolders() => new[] { Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "data"), Settings.DataPath }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(p!));

    private static bool Inside(string folder, string parent)
    {
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(f, p, StringComparison.OrdinalIgnoreCase) || f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string a, string b) => Inside(a, b) || Inside(b, a);

    private static readonly Regex DataFileName = new("^[0-9a-f]{64}$", RegexOptions.None, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Moves the take data to another folder (another drive), or back to <c>.ample/data</c> when <paramref name="target"/> is empty. The option is saved first, so
    /// new data goes to the new place at once and reads look in both while the move runs; a move that is stopped or interrupted simply continues when it is run again.
    /// Only take data files (a 64-digit hex name in a two-digit folder) are moved, whatever else lies in the old folder stays; a source file is deleted only after its copy
    /// is verified byte for byte, and never when the copy is the file itself.
    /// </summary>
    public async Task<StorageProgress> MoveAsync(string? target, IProgress<StorageProgress>? progress, CancellationToken cancellationToken)
    {
        StorageSettings.EnsureReadable(projectRoot);
        string own = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "data");
        string? full = string.IsNullOrWhiteSpace(target) ? null : Path.GetFullPath(target);
        string destination = Path.GetFullPath(full ?? own);
        string[] sources = [.. new[] { own, Settings.DataPath }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(p!)).Where(p => !string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(destination), StringComparison.OrdinalIgnoreCase) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (sources.Any(s => Overlaps(destination, s)))
        {
            throw new AmpleException("AMP5603", $"The folder {destination} lies inside the current take data folder or contains it. Choose a separate folder. Nothing was moved.");
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AmpleException("AMP5603", $"The folder {destination} cannot be used for the take data: {ex.Message}");
        }

        (Settings with { DataPath = full }).Save(projectRoot);
        List<string> files = [.. sources.SelectMany(s => Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories)).Where(f => DataFileName.IsMatch(Path.GetFileName(f)) && string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), Path.GetFileName(f)[..2], StringComparison.OrdinalIgnoreCase))];
        long total = files.Sum(f => new FileInfo(f).Length), moved = 0;
        int done = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            string target2 = Path.Combine(destination, name[..2], name);
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target2), StringComparison.OrdinalIgnoreCase))
            {
                continue;   // never delete a file that is its own copy
            }

            long length = new FileInfo(file).Length;
            Directory.CreateDirectory(Path.GetDirectoryName(target2)!);
            if (!File.Exists(target2) || new FileInfo(target2).Length != length)
            {
                string temp = target2 + ".moving";
                File.Copy(file, temp, true);
                if (new FileInfo(temp).Length != length || !SameContent(file, temp))
                {
                    File.Delete(temp);
                    throw new AmpleException("AMP5601", $"The copy of {name} to {destination} is incomplete (is the drive full?). Nothing was removed from the old place.");
                }

                File.Move(temp, target2, true);
            }

            File.Delete(file);
            moved += length;
            done++;
            progress?.Report(new StorageProgress(done, files.Count, moved, total));
            if (done % 10 == 0)
            {
                await Task.Yield();
            }
        }

        return new StorageProgress(done, files.Count, moved, total);
    }

    private static bool SameContent(string a, string b)
    {
        using FileStream x = new(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using FileStream y = new(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bx = new byte[1 << 16], by = new byte[1 << 16];
        while (true)
        {
            int rx = x.ReadAtLeast(bx, bx.Length, false);
            int ry = y.ReadAtLeast(by, by.Length, false);
            if (rx != ry || !bx.AsSpan(0, rx).SequenceEqual(by.AsSpan(0, ry)))
            {
                return false;
            }

            if (rx == 0)
            {
                return true;
            }
        }
    }

    /// <summary>The blobs that are still plain WAV and big enough to compress, with their size (a dry run of <see cref="CompressAsync"/>).</summary>
    public IReadOnlyList<(string DataId, long Bytes)> PlainBlobs()
    {
        DataMap map = LoadData();
        List<(string, long)> list = [];
        foreach (string id in map.AllIds())
        {
            long size = map.Size(id);
            if (map.Exists(id) && size >= TakeCodec.MinimumLength && bank.IsPlainWav(id))
            {
                list.Add((id, size));
            }
        }

        return list;
    }

    /// <summary>Compresses the plain WAV takes of the project in place (lossless; the takes and their ids do not change).</summary>
    public async Task<StorageProgress> CompressAsync(IProgress<StorageProgress>? progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string DataId, long Bytes)> blobs = PlainBlobs();
        long total = blobs.Sum(b => b.Bytes), freed = 0;
        int done = 0;
        foreach ((string id, _) in blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            freed += bank.CompressInPlace(id);
            done++;
            progress?.Report(new StorageProgress(done, blobs.Count, freed, total));
            await Task.Yield();
        }

        return new StorageProgress(done, blobs.Count, freed, total);
    }

    private IEnumerable<string> SourceFiles()
    {
        Stack<string> folders = new();
        folders.Push(projectRoot);
        while (folders.Count > 0)
        {
            string folder = folders.Pop();
            List<string> children;
            try
            {
                children = [.. Directory.EnumerateFileSystemEntries(folder)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AmpleException("AMP5606", $"The folder {Path.GetFileName(folder)} cannot be listed, so the clean-up cannot tell which takes it names. Nothing was removed.");
            }

            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (Directory.Exists(child))
                {
                    if (name is not (ProjectLayout.AmpleFolder or "node_modules" or "renders" or "exports" or "recordings" or "samples" or "assets" or ".git"))
                    {
                        folders.Push(child);
                    }
                }
                else if (SourceExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                {
                    yield return child;
                }
            }
        }
    }

    private static List<string> InputTakes(string planJson)
    {
        // every take id written anywhere in the plan counts as an input (a plan that cannot be parsed still names its inputs as text)
        HashSet<string> ids = [];
        AddTakeIds(ids, planJson);
        return [.. ids];
    }

    private static bool IsMissingTable(SqliteException ex) => ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    /// <summary>Scans a file for take ids in chunks (any size). A file that cannot be read aborts the plan: what it names would not be protected.</summary>
    private static void ScanFile(string file, HashSet<string> into)
    {
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ScanStream(stream, into);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AmpleException("AMP5606", $"The file {Path.GetFileName(file)} cannot be read, so the clean-up cannot tell which takes it names. Nothing was removed. Close it and try again.");
        }
    }

    private static void ScanStream(Stream stream, HashSet<string> into)
    {
        const int overlap = 70;   // "sha256:" + 64 hex digits is 71 characters
        using StreamReader reader = new(stream, System.Text.Encoding.UTF8, true, 1 << 16, leaveOpen: true);
        char[] buffer = new char[1 << 16];
        string tail = string.Empty;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            string text = tail + new string(buffer, 0, read);
            AddTakeIds(into, text);
            tail = text.Length > overlap ? text[^overlap..] : text;
        }
    }

    /// <summary>The text files in the project's own <c>.ample</c> folder that can name takes (finals, assistant proposals); the takebank itself, caches and nested projects are left out.</summary>
    private IEnumerable<string> AmpleWorkingFiles()
    {
        string ample = Path.Combine(projectRoot, ProjectLayout.AmpleFolder);
        string[] skip = ["data", "takes", "tmp", "peaks", "backups", "imports", "logs", "locks", "blackbox", "history-archive", ProjectLayout.AmpleFolder];
        Stack<string> folders = new();
        folders.Push(ample);
        while (folders.Count > 0)
        {
            string folder = folders.Pop();
            List<string> children;
            try
            {
                children = [.. Directory.EnumerateFileSystemEntries(folder)];
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AmpleException("AMP5606", $"The folder {Path.GetFileName(folder)} cannot be listed, so the clean-up cannot tell which takes it names. Nothing was removed.");
            }

            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (Directory.Exists(child))
                {
                    if (!skip.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        folders.Push(child);
                    }
                }
                else if (SourceExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                }
            }
        }
    }

    private static void AddTakeIds(HashSet<string> into, string text)
    {
        foreach (Match m in TakeIdPattern().Matches(text))
        {
            into.Add(m.Value);
        }
    }

    private static long FolderBytes(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long FileBytes(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;
}
