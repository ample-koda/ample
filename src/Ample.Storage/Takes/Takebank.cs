using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.Takes;

/// <summary>The takebank: content-addressed, immutable data blobs and takes, and the plans they were made from. A take cannot exist without its plan (the Always rule): <see cref="BeginAsync"/> stores the plan first and the only way to create a take is <see cref="TakeTicket.CommitAsync"/>.</summary>
public sealed partial class Takebank
{
    private static readonly Regex IdPattern = HexId();
    private readonly IFileSystem fs;
    private readonly IClock clock;
    private readonly string root;
    private readonly string databasePath;
    private readonly bool readOnly;

    private Takebank(IFileSystem fs, IClock clock, string root, bool readOnly)
    {
        this.fs = fs;
        this.clock = clock;
        this.root = root;
        this.readOnly = readOnly;
        databasePath = Path.Combine(root, ProjectLayout.AmpleFolder, "history.db");
    }

    /// <summary>Opens (and creates when needed) the takebank of a project folder.</summary>
    public static Takebank Open(IFileSystem fs, IClock clock, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        Takebank bank = new(fs, clock, projectRoot, false);
        fs.CreateDirectory(Path.Combine(projectRoot, ProjectLayout.AmpleFolder));
        foreach (string folder in new[] { "data", "takes", "tmp" })
        {
            fs.CreateDirectory(bank.Ample(folder));
        }

        fs.CreateDirectory(bank.Ample("takes", "plans"));
        using SqliteConnection connection = TakeDatabase.Open(bank.databasePath);
        TakeDatabase.Initialize(connection);
        return bank;
    }

    /// <summary>Opens an existing project takebank without creating folders, initializing SQLite, or permitting writes.</summary>
    public static Takebank OpenReadOnly(IFileSystem fs, IClock clock, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        Takebank bank = new(fs, clock, projectRoot, true);
        if (!fs.FileExists(bank.databasePath)) throw new AmpleException("AMP5223", "The project takebank is not available for read-only cache lookup.");
        using SqliteConnection connection = TakeDatabase.OpenReadOnly(bank.databasePath);
        return bank;
    }

    /// <summary>Stores the plan (file and database row) and records a started attempt. Only after this returns can a take be made, through the returned ticket.</summary>
    public async ValueTask<TakeTicket> BeginAsync(PlanRecord plan, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        string canonical = PlanCanonical.Serialize(plan);
        string planId = PlanCanonical.IdOf(canonical);
        string hex = planId["sha256:".Length..];
        string planFile = Ample("takes", "plans", hex + ".plan.json");
        if (!fs.FileExists(planFile))
        {
            await AtomicFile.WriteTextAsync(fs, planFile, canonical + "\n", cancellationToken).ConfigureAwait(false);
        }

        Guid attempt = Guid.NewGuid();
        Run(c =>
        {
            if (plan.ParentPlanId is not null && !Exists(c, "plans", "plan_id", plan.ParentPlanId))
            {
                throw new AmpleException("AMP5223", $"The parent plan {plan.ParentPlanId} is not stored.");
            }

            using SqliteTransaction tx = c.BeginTransaction();
            Exec(c, tx, "INSERT OR IGNORE INTO plans (plan_id, block_uuid, parent_plan_id, json, created_at) VALUES ($id, $block, $parent, $json, $at)", ("$id", planId), ("$block", plan.BlockUuid), ("$parent", plan.ParentPlanId), ("$json", canonical), ("$at", Now()));
            Exec(c, tx, "INSERT INTO attempts (attempt_id, plan_id, started_at, outcome) VALUES ($a, $p, $at, 'started')", ("$a", attempt.ToString("D")), ("$p", planId), ("$at", Now()));
            tx.Commit();
        });
        return new TakeTicket(this, attempt, planId);
    }

    /// <summary>Stores a blob by content (streamed, hashed on the way, deduplicated) and returns its id <c>sha256:...</c>.</summary>
    public async ValueTask<string> PutDataAsync(Stream data, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(data);
        string temp = Ample("tmp", Guid.NewGuid().ToString("N") + AtomicFile.TempSuffix);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using (Stream output = fs.CreateDurableWriteStream(temp))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                }
            }

            string hex = Convert.ToHexStringLower(hash.GetHashAndReset());
            string dataId = "sha256:" + hex;
            string target = WritePath(dataId);
            if (fs.FileExists(DataPath(dataId)))
            {
                TryDelete(temp);
            }
            else
            {
                if (size >= TakeCodec.MinimumLength && StorageSettings.Load(root).Intermediates == "compressed")
                {
                    temp = Compress(temp);
                }

                fs.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    fs.Move(temp, target, overwrite: false);
                }
                catch (IOException) when (fs.FileExists(target))
                {
                    // Another writer stored the same content first; content addressing makes that harmless.
                    TryDelete(temp);
                }

                fs.FlushDirectory(Path.GetDirectoryName(target)!);
            }

            Run(c =>
            {
                Exec(c, null, "INSERT OR IGNORE INTO data (data_id, size, created_at, quarantined) VALUES ($id, $size, $at, 0)", ("$id", dataId), ("$size", size), ("$at", Now()));
                Exec(c, null, "UPDATE data SET removed = 0 WHERE data_id = $id", ("$id", dataId));
            });
            return dataId;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Opens a stored blob for reading (a compressed take is decoded: the bytes are exactly the bytes that were stored).</summary>
    /// <exception cref="AmpleException">AMP5221 when the file is missing.</exception>
    public Stream OpenData(string dataId)
    {
        string path = DataPath(dataId);
        if (!fs.FileExists(path))
        {
            throw new AmpleException("AMP5221", $"The data {dataId} is not on disk. Re-render the take from its plan or restore it from a backup.");
        }

        Stream raw = fs.OpenRead(path);
        byte[] start = new byte[4];
        int got = raw.Read(start, 0, 4);
        if (got == 4 && TakeCodec.IsContainer(start))
        {
            try
            {
                raw.Position = 0;
                return new MemoryStream(TakeCodec.Decode(raw), writable: false);
            }
            finally
            {
                raw.Dispose();
            }
        }

        raw.Dispose();
        return fs.OpenRead(path);
    }

    /// <summary>
    /// Compresses a plain WAV blob in place (the content address does not change). The packed file is decoded and rehashed before it replaces the plain one,
    /// so a failure never costs data. Returns the bytes given back (0 when it was not worth it or not plain WAV).
    /// </summary>
    public long CompressInPlace(string dataId)
    {
        EnsureWritable();
        string path = DataPath(dataId);
        if (!fs.FileExists(path))
        {
            return 0;
        }

        long before = new FileInfo(path).Length;
        if (before < TakeCodec.MinimumLength)
        {
            return 0;
        }

        byte[] plain = File.ReadAllBytes(path);
        if (!TakeCodec.IsWav(plain))
        {
            return 0;
        }

        string packed = path + ".z" + AtomicFile.TempSuffix;
        try
        {
            using (Stream output = fs.CreateDurableWriteStream(packed))
            {
                TakeCodec.Encode(plain, output);
            }

            long after = new FileInfo(packed).Length;
            if (after >= before)
            {
                return 0;
            }

            using (FileStream check = File.OpenRead(packed))
            {
                if (!SHA256.HashData(TakeCodec.Decode(check)).AsSpan().SequenceEqual(Convert.FromHexString(dataId["sha256:".Length..])))
                {
                    return 0;
                }
            }

            fs.Move(packed, path, overwrite: true);
            return before - after;
        }
        finally
        {
            TryDelete(packed);
        }
    }

    /// <summary>Determines whether a blob is stored as plain WAV (it could be compressed).</summary>
    public bool IsPlainWav(string dataId)
    {
        string path = DataPath(dataId);
        if (!fs.FileExists(path))
        {
            return false;
        }

        using Stream s = fs.OpenRead(path);
        byte[] start = new byte[12];
        return s.Read(start, 0, 12) == 12 && TakeCodec.IsWav(start);
    }

    /// <summary>Gets the bytes a blob takes on disk (what a clean-up gives back); 0 when it is not there.</summary>
    public long DiskSizeOfData(string dataId)
    {
        string path = DataPath(dataId);
        return fs.FileExists(path) ? new FileInfo(path).Length : 0;
    }

    private string Compress(string plainFile)
    {
        byte[] plain = File.ReadAllBytes(plainFile);
        if (!TakeCodec.IsWav(plain))
        {
            return plainFile;
        }

        string packed = plainFile + ".z";
        using (Stream output = fs.CreateDurableWriteStream(packed))
        {
            TakeCodec.Encode(plain, output);
        }

        TryDelete(plainFile);
        return packed;
    }

    /// <summary>Gets a stored plan.</summary>
    public StoredPlan GetPlan(string planId)
    {
        string? json = Run(c => Scalar(c, "SELECT json FROM plans WHERE plan_id = $id", ("$id", planId)) as string);
        return json is null ? throw new AmpleException("AMP5223", $"There is no plan {planId}.") : new StoredPlan(planId, json, PlanCanonical.Parse(json));
    }

    /// <summary>Gets a take.</summary>
    public TakeRecord GetTake(string takeId) => Run(c =>
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT take_id, plan_id, created_at, meta_json, tombstoned, tombstone_reason FROM takes WHERE take_id = $id";
        cmd.Parameters.AddWithValue("$id", takeId);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new AmpleException("AMP5223", $"There is no take {takeId}.");
        }

        return ReadTake(c, r);
    });

    /// <summary>Lists takes, newest first, optionally of one block.</summary>
    public IReadOnlyList<TakeRecord> ListTakes(string? blockUuid = null) => Run(c =>
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT t.take_id, t.plan_id, t.created_at, t.meta_json, t.tombstoned, t.tombstone_reason FROM takes t JOIN plans p ON p.plan_id = t.plan_id WHERE $block IS NULL OR p.block_uuid = $block ORDER BY t.created_at DESC, t.take_id";
        cmd.Parameters.AddWithValue("$block", (object?)blockUuid ?? DBNull.Value);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<(string, string, string, string, bool, string?)> rows = [];
        while (r.Read())
        {
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5)));
        }

        return (IReadOnlyList<TakeRecord>)[.. rows.Select(x => new TakeRecord(x.Item1, x.Item2, DateTimeOffset.Parse(x.Item3, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DataOf(c, x.Item1), x.Item4, x.Item5, x.Item6))];
    });

    /// <summary>Resolves an id or a unique prefix to a take id.</summary>
    public string ResolveTakeId(string idOrPrefix)
    {
        List<string> found = Run(c =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT take_id FROM takes WHERE take_id LIKE $p ESCAPE '\\' ORDER BY take_id";
            cmd.Parameters.AddWithValue("$p", idOrPrefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
            using SqliteDataReader r = cmd.ExecuteReader();
            List<string> ids = [];
            while (r.Read())
            {
                ids.Add(r.GetString(0));
            }

            return ids;
        });
        return found.Count switch
        {
            1 => found[0],
            0 => throw new AmpleException("AMP5223", $"There is no take starting with '{idOrPrefix}'."),
            _ => throw new AmpleException("AMP5223", $"'{idOrPrefix}' matches {found.Count.ToString(CultureInfo.InvariantCulture)} takes; give more characters."),
        };
    }

    /// <summary>Reads a take with its plan; blobs that are not on disk are listed in <see cref="TakeRead.Missing"/> instead of failing.</summary>
    public TakeRead ReadTake(string takeId)
    {
        TakeRecord take = GetTake(takeId);
        StoredPlan plan = GetPlan(take.PlanId);
        List<DataProblem> missing = [.. take.Data.Where(d => !fs.FileExists(DataPath(d.DataId))).Select(d => new DataProblem("missing", d.DataId, $"{d.Role} data is not on disk"))];
        return new TakeRead(take, plan, missing);
    }

    /// <summary>Rehashes every blob of a take and reports missing files, wrong sizes and bit rot.</summary>
    public async ValueTask<IReadOnlyList<DataProblem>> VerifyAsync(string takeId, CancellationToken cancellationToken = default)
    {
        List<DataProblem> problems = [];
        foreach (TakeDataRef d in GetTake(takeId).Data)
        {
            string path = DataPath(d.DataId);
            if (!fs.FileExists(path))
            {
                problems.Add(new DataProblem("missing", d.DataId, $"{d.Role} data is not on disk"));
                continue;
            }

            long expected = Run(c => (long)(Scalar(c, "SELECT size FROM data WHERE data_id = $id", ("$id", d.DataId)) ?? -1L));
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (Stream input = OpenData(d.DataId))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    size += read;
                }
            }

            if (expected >= 0 && size != expected)
            {
                problems.Add(new DataProblem("size", d.DataId, $"expected {expected.ToString(CultureInfo.InvariantCulture)} bytes, found {size.ToString(CultureInfo.InvariantCulture)}"));
            }

            string actual = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != d.DataId)
            {
                problems.Add(new DataProblem("hash", d.DataId, $"the content hashes to {actual}"));
            }
        }

        return problems;
    }

    /// <summary>Lists attempts, optionally only those with an outcome (for example <c>started</c> for interrupted renders).</summary>
    public IReadOnlyList<AttemptRecord> ListAttempts(string? outcome = null) => Run(c =>
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT attempt_id, plan_id, started_at, outcome, reason, take_id FROM attempts WHERE $o IS NULL OR outcome = $o ORDER BY started_at, rowid";
        cmd.Parameters.AddWithValue("$o", (object?)outcome ?? DBNull.Value);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<AttemptRecord> list = [];
        while (r.Read())
        {
            list.Add(new AttemptRecord(Guid.Parse(r.GetString(0)), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        }

        return list;
    });

    /// <summary>Marks a take as dropped. The take, its plan and its metadata stay; removing the audio is a separate dangerous act (chunk 50).</summary>
    public void Tombstone(string takeId, string reason) => Run(c =>
    {
        EnsureWritable();
        int rows = Exec(c, null, "UPDATE takes SET tombstoned = 1, tombstone_reason = $r, tombstone_at = $at WHERE take_id = $id", ("$r", reason), ("$at", Now()), ("$id", takeId));
        if (rows == 0)
        {
            throw new AmpleException("AMP5223", $"There is no take {takeId}.");
        }
    });

    /// <summary>Finds blobs that no take references: rows without takes and files on disk without a row.</summary>
    public IReadOnlyList<Orphan> FindOrphans()
    {
        HashSet<string> known = [];
        List<Orphan> orphans = Run(c =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT d.data_id, d.quarantined FROM data d WHERE d.removed = 0 AND NOT EXISTS (SELECT 1 FROM take_data t WHERE t.data_id = d.data_id) ORDER BY d.data_id";
            using SqliteDataReader r = cmd.ExecuteReader();
            List<Orphan> list = [];
            while (r.Read())
            {
                list.Add(new Orphan(r.GetString(0), true, r.GetInt64(1) != 0));
            }

            using SqliteCommand all = c.CreateCommand();
            all.CommandText = "SELECT data_id FROM data";
            using SqliteDataReader ar = all.ExecuteReader();
            while (ar.Read())
            {
                known.Add(ar.GetString(0));
            }

            return list;
        });
        List<string> dataFolders = [Ample("data")];
        if (StorageSettings.Load(root).DataPath is { Length: > 0 } custom && !string.Equals(Path.GetFullPath(custom), Path.GetFullPath(Ample("data")), StringComparison.OrdinalIgnoreCase) && fs.DirectoryExists(custom))
        {
            dataFolders.Add(custom);
        }

        foreach (string dir in dataFolders.Where(fs.DirectoryExists).SelectMany(fs.GetDirectories))
        {
            foreach (string file in fs.GetFiles(dir))
            {
                string id = "sha256:" + Path.GetFileName(file);
                if (IdPattern.IsMatch(id) && !known.Contains(id))
                {
                    orphans.Add(new Orphan(id, false, false));
                }
            }
        }

        return orphans;
    }

    /// <summary>Quarantines orphans: unreferenced rows are flagged, files without a row get a flagged row. Nothing is deleted.</summary>
    public int QuarantineOrphans()
    {
        EnsureWritable();
        IReadOnlyList<Orphan> orphans = FindOrphans();
        Run(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction();
            foreach (Orphan o in orphans.Where(x => !x.Quarantined))
            {
                if (o.InDatabase)
                {
                    Exec(c, tx, "UPDATE data SET quarantined = 1 WHERE data_id = $id", ("$id", o.DataId));
                }
                else
                {
                    long size = fs.OpenRead(DataPath(o.DataId)) is Stream s ? Length(s) : 0;
                    Exec(c, tx, "INSERT OR IGNORE INTO data (data_id, size, created_at, quarantined) VALUES ($id, $size, $at, 1)", ("$id", o.DataId), ("$size", size), ("$at", Now()));
                }
            }

            tx.Commit();
        });
        return orphans.Count(x => !x.Quarantined);
    }

    /// <summary>Finds the newest take of a plan whose data is still on disk (the build cache lookup), or null.</summary>
    public TakeRecord? FindTakeForPlan(string planId)
    {
        List<string> ids = Run(c =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT take_id FROM takes WHERE plan_id = $p AND tombstoned = 0 ORDER BY created_at DESC, take_id";
            cmd.Parameters.AddWithValue("$p", planId);
            using SqliteDataReader r = cmd.ExecuteReader();
            List<string> list = [];
            while (r.Read())
            {
                list.Add(r.GetString(0));
            }

            return list;
        });
        return ids.Select(GetTake).FirstOrDefault(t => t.Data.All(d => fs.FileExists(DataPath(d.DataId))));
    }

    internal async ValueTask CopyPlanLineageToAsync(Takebank destination, PlanRecord plan, CancellationToken cancellationToken)
    {
        if (plan.ParentPlanId is null || destination.HasPlan(plan.ParentPlanId)) return;
        PlanRecord parent = GetPlan(plan.ParentPlanId).Plan;
        await CopyPlanLineageToAsync(destination, parent, cancellationToken).ConfigureAwait(false);
        TakeTicket copied = await destination.BeginAsync(parent, cancellationToken).ConfigureAwait(false);
        await copied.CancelAsync("parent plan copied for cached proposal audio").ConfigureAwait(false);
    }

    private bool HasPlan(string planId) => Run(c => Scalar(c, "SELECT 1 FROM plans WHERE plan_id = $id", ("$id", planId)) is not null);

    /// <summary>Gets the size in bytes of one blob (0 when unknown).</summary>
    public long SizeOfData(string dataId) => Run(c => (long)(Scalar(c, "SELECT size FROM data WHERE data_id = $id", ("$id", dataId)) ?? 0L));

    /// <summary>Determines whether a take uses the blob.</summary>
    public bool IsUsedByTake(string dataId) => Run(c => Scalar(c, "SELECT 1 FROM take_data WHERE data_id = $id", ("$id", dataId)) is not null);

    /// <summary>Removes a blob from disk and flags it removed. Only the cleanup dangerous act calls this; blobs used by a take are refused (AMP5226).</summary>
    public void RemoveData(string dataId)
    {
        EnsureWritable();
        if (IsUsedByTake(dataId))
        {
            throw new AmpleException("AMP5226", $"The data {dataId} belongs to a take and is not removed here.");
        }

        fs.DeleteFile(DataPath(dataId));
        Run(c => Exec(c, null, "UPDATE data SET removed = 1 WHERE data_id = $id", ("$id", dataId)));
    }

    /// <summary>
    /// Removes the blob of takes that were dropped (tombstoned): deleted, or moved to the archive folder when one is given. Refused (AMP5226) while a live take uses it.
    /// Only the storage clean-up calls this.
    /// </summary>
    public void RemoveDataOfDroppedTakes(string dataId, string? archiveFolder)
    {
        EnsureWritable();
        bool live = Run(c => Scalar(c, "SELECT 1 FROM take_data td JOIN takes t ON t.take_id = td.take_id WHERE td.data_id = $id AND t.tombstoned = 0", ("$id", dataId)) is not null);
        if (live)
        {
            throw new AmpleException("AMP5226", $"The data {dataId} belongs to a live take and is not removed.");
        }

        string path = DataPath(dataId);
        if (archiveFolder is { Length: > 0 } && fs.FileExists(path))
        {
            string target = Path.Combine(archiveFolder, dataId["sha256:".Length..][..2], dataId["sha256:".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(path, target, true);
        }
        else
        {
            fs.DeleteFile(path);
        }

        Run(c => Exec(c, null, "UPDATE data SET removed = 1 WHERE data_id = $id", ("$id", dataId)));
    }

    /// <summary>Determines whether a blob is on disk.</summary>
    public bool DataExists(string dataId) => fs.FileExists(DataPath(dataId));

    /// <summary>Gets the size in bytes of the data of one take.</summary>
    public long SizeOfTake(string takeId) => Run(c => (long)(Scalar(c, "SELECT COALESCE(SUM(d.size), 0) FROM (SELECT DISTINCT data_id FROM take_data WHERE take_id = $id) x JOIN data d ON d.data_id = x.data_id", ("$id", takeId)) ?? 0L));

    /// <summary>Gets the size in bytes of all distinct data used by the takes of one block.</summary>
    public long SizeOfBlock(string blockUuid) => Run(c => (long)(Scalar(c, "SELECT COALESCE(SUM(d.size), 0) FROM (SELECT DISTINCT td.data_id FROM take_data td JOIN takes t ON t.take_id = td.take_id JOIN plans p ON p.plan_id = t.plan_id WHERE p.block_uuid = $b) x JOIN data d ON d.data_id = x.data_id", ("$b", blockUuid)) ?? 0L));

    /// <summary>Opens a raw connection to the database (for tests and for later chunks).</summary>
    internal SqliteConnection OpenConnection()
    {
        EnsureWritable();
        return TakeDatabase.Open(databasePath);
    }

    internal async ValueTask<TakeRecord> CommitAsync(Guid attempt, string planId, IReadOnlyList<(string Role, Stream Data)> outputs, string metaJson, CancellationToken cancellationToken)
    {
        EnsureWritable();
        List<TakeDataRef> refs = [];
        foreach ((string role, Stream data) in outputs)
        {
            refs.Add(new TakeDataRef(role, await PutDataAsync(data, cancellationToken).ConfigureAwait(false)));
        }

        string meta = CanonicalMeta(metaJson);
        string takeId = TakeIdOf(planId, refs);
        string now = Now();
        Run(c =>
        {
            using SqliteTransaction tx = c.BeginTransaction();
            Exec(c, tx, "INSERT OR IGNORE INTO takes (take_id, plan_id, created_at, meta_json) VALUES ($t, $p, $at, $m)", ("$t", takeId), ("$p", planId), ("$at", now), ("$m", meta));
            foreach (TakeDataRef r in refs)
            {
                Exec(c, tx, "INSERT OR IGNORE INTO take_data (take_id, role, data_id) VALUES ($t, $r, $d)", ("$t", takeId), ("$r", r.Role), ("$d", r.DataId));
            }

            Exec(c, tx, "UPDATE attempts SET outcome = 'completed', take_id = $t WHERE attempt_id = $a", ("$t", takeId), ("$a", attempt.ToString("D")));
            tx.Commit();
        });

        string takeFile = Ample("takes", takeId["sha256:".Length..][..2], takeId["sha256:".Length..] + ".take.json");
        if (!fs.FileExists(takeFile))
        {
            fs.CreateDirectory(Path.GetDirectoryName(takeFile)!);
            string json = JsonSerializer.Serialize(new { takeId, planId, created = now, data = refs.Select(r => new { role = r.Role, dataId = r.DataId }), meta = JsonDocument.Parse(meta).RootElement });
            await AtomicFile.WriteTextAsync(fs, takeFile, json + "\n", cancellationToken).ConfigureAwait(false);
        }

        return GetTake(takeId);
    }

    internal void Finish(Guid attempt, string outcome, string reason) => Run(c => Exec(c, null, "UPDATE attempts SET outcome = $o, reason = $r WHERE attempt_id = $a AND outcome = 'started'", ("$o", outcome), ("$r", reason), ("$a", attempt.ToString("D"))));

    private static string TakeIdOf(string planId, IEnumerable<TakeDataRef> refs)
    {
        string text = "take\n" + planId + "\n" + string.Join('\n', refs.OrderBy(r => r.Role, StringComparer.Ordinal).ThenBy(r => r.DataId, StringComparer.Ordinal).Select(r => r.Role + "=" + r.DataId));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string CanonicalMeta(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.GetRawText();
    }

    private static long Length(Stream s)
    {
        using (s)
        {
            return s.Length;
        }
    }

    private string Ample(params string[] parts) => Path.Combine([root, ProjectLayout.AmpleFolder, .. parts]);

    /// <summary>Gets where new data is written: the data path of the project's storage options, else <c>.ample/data</c>.</summary>
    private string WritePath(string dataId)
    {
        string hex = HexOf(dataId);
        string configured = StorageSettings.Load(root).DataPath ?? string.Empty;
        if (configured.Length == 0)
        {
            return Ample("data", hex[..2], hex);
        }

        string full = Path.GetFullPath(configured);
        string drive = Path.GetPathRoot(full) ?? full;
        if (!Directory.Exists(drive) || (!Directory.Exists(full) && !Directory.Exists(Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar)) ?? drive)))
        {
            throw new AmpleException("AMP5603", $"The folder for the take data ({configured}) is not reachable. Connect the drive or change the data path (ample storage move); what is missing renders again.");
        }

        return Path.Combine(full, hex[..2], hex);
    }

    private static string HexOf(string dataId)
    {
        if (!IdPattern.IsMatch(dataId))
        {
            throw new AmpleException("AMP5223", $"'{dataId}' is not a data id (sha256: and 64 hex digits).");
        }

        return dataId["sha256:".Length..];
    }

    /// <summary>Gets where a blob is read from: the configured data path, or the project's own <c>.ample/data</c> while data has not been moved yet.</summary>
    private string DataPath(string dataId)
    {
        string hex = HexOf(dataId);
        string configured = StorageSettings.Load(root).DataPath ?? string.Empty;
        if (configured.Length > 0)
        {
            string moved = Path.Combine(Path.GetFullPath(configured), hex[..2], hex);
            if (fs.FileExists(moved))
            {
                return moved;
            }
        }

        string own = Ample("data", hex[..2], hex);
        return configured.Length > 0 && !fs.FileExists(own) ? Path.Combine(Path.GetFullPath(configured), hex[..2], hex) : own;
    }

    private string Now() => clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private void TryDelete(string path)
    {
        try
        {
            fs.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is reported by the doctor.
        }
    }

    private static List<TakeDataRef> DataOf(SqliteConnection c, string takeId)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT role, data_id FROM take_data WHERE take_id = $id ORDER BY role, data_id";
        cmd.Parameters.AddWithValue("$id", takeId);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<TakeDataRef> refs = [];
        while (r.Read())
        {
            refs.Add(new TakeDataRef(r.GetString(0), r.GetString(1)));
        }

        return refs;
    }

    private static TakeRecord ReadTake(SqliteConnection c, SqliteDataReader r)
    {
        string id = r.GetString(0);
        TakeRecord partial = new(id, r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), [], r.GetString(3), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5));
        r.Close();
        return partial with { Data = DataOf(c, id) };
    }

    private T Run<T>(Func<SqliteConnection, T> action)
    {
        try
        {
            using SqliteConnection c = readOnly ? TakeDatabase.OpenReadOnly(databasePath) : TakeDatabase.Open(databasePath);
            return action(c);
        }
        catch (SqliteException ex)
        {
            int at = ex.Message.IndexOf("AMP52", StringComparison.Ordinal);
            if (at >= 0)
            {
                throw new AmpleException(ex.Message.Substring(at, 7), ex.Message[(at + 9)..]);
            }

            throw;
        }
    }

    private void EnsureWritable()
    {
        if (readOnly) throw new InvalidOperationException("This takebank was opened read-only.");
    }

    private void Run(Action<SqliteConnection> action) => Run<int>(c =>
    {
        action(c);
        return 0;
    });

    private static bool Exists(SqliteConnection c, string table, string column, string value) => Scalar(c, $"SELECT 1 FROM {table} WHERE {column} = $v", ("$v", value)) is not null;

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

    private static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HexId();
}

/// <summary>The right to make exactly one take for a stored plan. Obtained from <see cref="Takebank.BeginAsync"/>; finishing it (commit, fail or cancel) is one-shot.</summary>
public sealed class TakeTicket
{
    private readonly Takebank bank;
    private bool finished;
    private bool commitFailed;

    internal TakeTicket(Takebank bank, Guid attemptId, string planId)
    {
        this.bank = bank;
        AttemptId = attemptId;
        PlanId = planId;
    }

    /// <summary>Gets the attempt id.</summary>
    public Guid AttemptId { get; }

    /// <summary>Gets the id of the stored plan.</summary>
    public string PlanId { get; }

    /// <summary>Stores the outputs and creates the take. The only way to create a take.</summary>
    /// <param name="outputs">Role and content of each output (for example <c>audio</c>).</param>
    /// <param name="metaJson">Free metadata as JSON text.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<TakeRecord> CommitAsync(IReadOnlyList<(string Role, Stream Data)> outputs, string metaJson = "{}", CancellationToken cancellationToken = default)
    {
        Claim();
        try
        {
            return await bank.CommitAsync(AttemptId, PlanId, outputs, metaJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            commitFailed = true;
            bank.Finish(AttemptId, "failed", "the commit did not complete");

            // a full disk or an unwritable folder says so (AMP5600, AMP5601) instead of a bare IO error
            if (ex is IOException io && !(ex is FileNotFoundException))
            {
                const int DiskFull = 0x70, HandleDiskFull = 0x27;
                int code = io.HResult & 0xFFFF;
                throw new AmpleException(code is DiskFull or HandleDiskFull ? "AMP5600" : "AMP5601", code is DiskFull or HandleDiskFull
                    ? "There is not enough free space on the drive of the takebank to store this take. Free space or clean up takes (Storage panel)."
                    : "The take could not be stored: " + io.Message, io);
            }

            if (ex is UnauthorizedAccessException denied)
            {
                throw new AmpleException("AMP5601", "The take could not be stored: " + denied.Message, denied);
            }

            throw;
        }
    }

    /// <summary>Records that the render failed. The plan stays, with the reason.</summary>
    public ValueTask FailAsync(string reason)
    {
        if (commitFailed)
        {
            // the commit already recorded the failure; do not hide the real error behind "ticket already used"
            return ValueTask.CompletedTask;
        }

        Claim();
        bank.Finish(AttemptId, "failed", reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>Records that the render was cancelled. The plan stays, with the reason.</summary>
    public ValueTask CancelAsync(string reason)
    {
        if (commitFailed)
        {
            return ValueTask.CompletedTask;
        }

        Claim();
        bank.Finish(AttemptId, "cancelled", reason);
        return ValueTask.CompletedTask;
    }

    private void Claim()
    {
        if (finished)
        {
            throw new InvalidOperationException("This take ticket was already used.");
        }

        finished = true;
    }
}
