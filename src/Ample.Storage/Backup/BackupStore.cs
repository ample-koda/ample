using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.Security;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.Backup;

/// <summary>
/// A backup store in a folder the user chose: content-addressed objects (<c>objects/ab/abcd...</c>, one per distinct file
/// content, so a second backup only stores what changed) and one manifest per backup (<c>backups/&lt;id&gt;.json</c>).
/// Backups are immutable. An interrupted backup leaves only objects, which the next run reuses; the manifest is written last.
/// The SQLite database is copied with the SQLite backup API, so the copy is consistent while the project is open. Secrets
/// (<c>.ample/vault*</c>, <c>*.secret</c>), locks, temp files, journals of the database and earlier backups are never included.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string target;
    private readonly IClock clock;
    private readonly Func<string, long> freeSpace;

    /// <summary>Opens (creating the folders) a store.</summary>
    /// <param name="targetFolder">The folder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="freeSpace">Gives the free bytes at a path (the default asks the drive; tests inject their own).</param>
    public BackupStore(string targetFolder, IClock clock, Func<string, long>? freeSpace = null)
    {
        target = Path.GetFullPath(targetFolder);
        this.clock = clock;
        this.freeSpace = freeSpace ?? (p => new DriveInfo(Path.GetPathRoot(p)!).AvailableFreeSpace);
        Directory.CreateDirectory(Path.Combine(target, "objects"));
        Directory.CreateDirectory(Path.Combine(target, "backups"));
    }

    /// <summary>Gets the folder of the store.</summary>
    public string Target => target;

    /// <summary>The files of a project that go into a backup or an export (relative path with <c>/</c>, full path).</summary>
    public static IEnumerable<(string Rel, string Full)> ProjectFiles(string projectRoot, bool includeTakes)
    {
        string root = Path.GetFullPath(projectRoot);
        foreach (string full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            string name = Path.GetFileName(rel);
            bool inAmple = rel.StartsWith(".ample/", StringComparison.Ordinal);
            if (inAmple && (rel.StartsWith(".ample/locks/", StringComparison.Ordinal) || rel.StartsWith(".ample/tmp/", StringComparison.Ordinal) || rel.StartsWith(".ample/backups/", StringComparison.Ordinal) || rel.StartsWith(".ample/vault", StringComparison.Ordinal)))
            {
                continue;
            }

            if (name.EndsWith(".db-wal", StringComparison.Ordinal) || name.EndsWith(".db-shm", StringComparison.Ordinal) || name.EndsWith(".secret", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            if (!includeTakes && inAmple && (rel.StartsWith(".ample/takes/", StringComparison.Ordinal) || rel.StartsWith(".ample/data/", StringComparison.Ordinal)))
            {
                continue;
            }

            yield return (rel, full);
        }
    }

    /// <summary>Makes a backup of a project (a consistent copy of the database, every other file as it is).</summary>
    /// <exception cref="AmpleException">AMP5300 when the target has not enough free space.</exception>
    public BackupReport Create(string projectRoot, Guid projectId, string projectName, bool includeTakes = true, CancellationToken cancellationToken = default) => CreateCore(projectRoot, projectId, projectName, includeTakes, true, cancellationToken);

    /// <summary>Makes a store copy that is never encrypted (the staging area of an export).</summary>
    internal BackupReport CreatePlain(string projectRoot, Guid projectId, string projectName, bool includeTakes, CancellationToken cancellationToken) => CreateCore(projectRoot, projectId, projectName, includeTakes, false, cancellationToken);

    private BackupReport CreateCore(string projectRoot, Guid projectId, string projectName, bool includeTakes, bool encrypt, CancellationToken cancellationToken)
    {
        DataCipher? cipher = encrypt ? ProjectEncryption.CipherFor(projectRoot) : null;
        if (cipher is not null)
        {
            EnsureTargetKey(projectRoot);
            ProjectEncryption.Remember(target, cipher);
        }

        Stopwatch sw = Stopwatch.StartNew();
        string temp = Path.Combine(target, "tmp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            List<(string Rel, string Path, long Size, string Hash)> files = [];
            foreach ((string rel, string full) in ProjectFiles(projectRoot, includeTakes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = full;
                if (rel.EndsWith(".db", StringComparison.Ordinal))
                {
                    source = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".db");
                    SnapshotDatabase(full, source);
                }

                files.Add((rel, source, new FileInfo(source).Length, HashFile(source)));
            }

            long needed = files.Where(f => !File.Exists(ObjectPath(f.Hash))).DistinctBy(f => f.Hash).Sum(f => f.Size);
            if (freeSpace(target) < needed + 1024 * 1024)
            {
                throw new AmpleException("AMP5300", $"The backup target has {freeSpace(target).ToString(CultureInfo.InvariantCulture)} bytes free but this backup needs about {needed.ToString(CultureInfo.InvariantCulture)} more. Free space or choose another target.");
            }

            int newObjects = 0;
            long newBytes = 0;
            foreach ((string _, string path, long size, string hash) in files.DistinctBy(f => f.Hash))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dest = ObjectPath(hash);
                if (File.Exists(dest))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string partial = dest + ".part";
                if (cipher is not null)
                {
                    cipher.SealFile(path, partial);
                }
                else
                {
                    File.Copy(path, partial, true);
                }

                File.Move(partial, dest, true);
                newObjects++;
                newBytes += size;
            }

            BackupEntry[] entries = [.. files.Select(f => new BackupEntry(f.Rel, f.Hash, f.Size)).OrderBy(e => e.Path, StringComparer.Ordinal)];
            DateTimeOffset now = clock.UtcNow;
            string digest = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', entries.Select(e => e.Path + "|" + e.Hash)))))[..6];
            string id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + digest;
            BackupManifest manifest = new(1, id, projectId, projectName, now, includeTakes, entries);
            string file = Path.Combine(target, "backups", id + ".json");
            Ample.Audio.IO.RawFiles.WriteText(file + ".part", JsonSerializer.Serialize(manifest, Indented) + "\n");
            File.Move(file + ".part", file, true);
            return new BackupReport(id, entries.Length, newObjects, newBytes, entries.Sum(e => e.Size), sw.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException)
            {
                // Left for the next run to clean.
            }

            foreach (string leftover in Directory.GetDirectories(target, "tmp-*").Where(d => d != temp && Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddHours(-1)))
            {
                try
                {
                    Directory.Delete(leftover, true);
                }
                catch (IOException)
                {
                    // Still in use.
                }
            }
        }
    }

    /// <summary>Lists the backups, newest first.</summary>
    public IReadOnlyList<BackupManifest> List() =>
    [
        .. Directory.GetFiles(Path.Combine(target, "backups"), "*.json").Select(f => Read(f)).OrderByDescending(m => m.Created).ThenByDescending(m => m.Id, StringComparer.Ordinal),
    ];

    /// <summary>Gets a backup by id or unique prefix (or the newest one with <c>latest</c>).</summary>
    /// <exception cref="AmpleException">AMP5303 when there is none.</exception>
    public BackupManifest Get(string idOrPrefix)
    {
        IReadOnlyList<BackupManifest> all = List();
        if (idOrPrefix == "latest" && all.Count > 0)
        {
            return all[0];
        }

        List<BackupManifest> hits = [.. all.Where(m => m.Id.StartsWith(idOrPrefix, StringComparison.Ordinal))];
        return hits.Count == 1 ? hits[0] : throw new AmpleException("AMP5303", hits.Count == 0 ? $"There is no backup '{idOrPrefix}' in {target}." : $"'{idOrPrefix}' matches {hits.Count.ToString(CultureInfo.InvariantCulture)} backups; give more of the id.");
    }

    /// <summary>Rehashes every object of a backup. A clean result is remembered as the "last good" backup.</summary>
    public BackupVerifyReport Verify(string idOrPrefix, CancellationToken cancellationToken = default)
    {
        BackupManifest m = Get(idOrPrefix);
        List<string> missing = [];
        List<string> corrupt = [];
        foreach (BackupEntry e in m.Entries.DistinctBy(e => e.Hash))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = ObjectPath(e.Hash);
            if (!File.Exists(path))
            {
                missing.AddRange(m.Entries.Where(x => x.Hash == e.Hash).Select(x => x.Path));
            }
            else if (HashObject(path) != e.Hash)
            {
                corrupt.AddRange(m.Entries.Where(x => x.Hash == e.Hash).Select(x => x.Path));
            }
        }

        BackupVerifyReport report = new(m.Id, m.Entries.Select(e => e.Hash).Distinct().Count(), missing, corrupt);
        if (report.Ok)
        {
            Dictionary<string, DateTimeOffset> good = Verified();
            good[m.Id] = clock.UtcNow;
            Ample.Audio.IO.RawFiles.WriteText(Path.Combine(target, "verified.json"), JsonSerializer.Serialize(good, Indented) + "\n");
        }

        return report;
    }

    /// <summary>The newest backup that passed a verify, or null.</summary>
    public BackupManifest? LastGood()
    {
        Dictionary<string, DateTimeOffset> good = Verified();
        return List().FirstOrDefault(m => good.ContainsKey(m.Id));
    }

    /// <summary>Works out what a prune would remove. The last good backup (or, when none was verified, the newest) is never removed.</summary>
    public PrunePlan PlanPrune(PruneRules rules)
    {
        IReadOnlyList<BackupManifest> all = List();
        HashSet<string> keep = [.. all.Take(Math.Max(1, rules.KeepLast)).Select(m => m.Id)];
        foreach (IGrouping<DateTime, BackupManifest> day in all.GroupBy(m => m.Created.UtcDateTime.Date).OrderByDescending(g => g.Key).Take(rules.KeepDaily))
        {
            keep.Add(day.OrderByDescending(m => m.Created).First().Id);
        }

        keep.Add((LastGood() ?? (all.Count > 0 ? all[0] : null))?.Id ?? string.Empty);
        List<string> remove = [.. all.Where(m => !keep.Contains(m.Id)).Select(m => m.Id)];
        HashSet<string> stillUsed = [.. all.Where(m => keep.Contains(m.Id)).SelectMany(m => m.Entries.Select(e => e.Hash))];
        List<BackupEntry> freed = [.. all.Where(m => remove.Contains(m.Id)).SelectMany(m => m.Entries).Where(e => !stillUsed.Contains(e.Hash)).DistinctBy(e => e.Hash)];
        return new PrunePlan(remove, [.. all.Where(m => keep.Contains(m.Id)).Select(m => m.Id)], freed.Count, freed.Sum(e => e.Size));
    }

    /// <summary>Removes the backups of a plan and the objects nothing else uses.</summary>
    public void Prune(PrunePlan plan)
    {
        foreach (string id in plan.Remove)
        {
            File.Delete(Path.Combine(target, "backups", id + ".json"));
        }

        HashSet<string> used = [.. List().SelectMany(m => m.Entries.Select(e => e.Hash))];
        foreach (string obj in Directory.EnumerateFiles(Path.Combine(target, "objects"), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".part", StringComparison.Ordinal)))
        {
            if (!used.Contains("sha256:" + Path.GetFileName(obj)))
            {
                File.Delete(obj);
            }
        }

        Dictionary<string, DateTimeOffset> good = Verified();
        foreach (string id in plan.Remove)
        {
            good.Remove(id);
        }

        Ample.Audio.IO.RawFiles.WriteText(Path.Combine(target, "verified.json"), JsonSerializer.Serialize(good, Indented) + "\n");
    }

    /// <summary>Restores a backup into a new (empty or missing) folder, checking each object's hash on the way.</summary>
    /// <exception cref="AmpleException">AMP5301 when the folder is not empty or an object is missing or damaged.</exception>
    public void RestoreToFolder(string idOrPrefix, string folder, CancellationToken cancellationToken = default)
    {
        BackupManifest m = Get(idOrPrefix);
        string root = Path.GetFullPath(folder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new AmpleException("AMP5301", $"The folder '{root}' is not empty. A restore goes into a new folder; use the in-place restore (a dangerous act) to change an existing project.");
        }

        foreach (BackupEntry e in m.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyVerified(e, Ample.Audio.IO.ProjectPath.Resolve(root, e.Path));
        }
    }

    /// <summary>Lists what restoring a backup into an existing project would change.</summary>
    public RestorePlan PlanRestoreInPlace(string idOrPrefix, string projectRoot)
    {
        BackupManifest m = Get(idOrPrefix);
        List<string> replace = [];
        List<string> add = [];
        foreach (BackupEntry e in m.Entries)
        {
            string path = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, e.Path);
            if (!File.Exists(path))
            {
                add.Add(e.Path);
            }
            else if (e.Path.EndsWith(".db", StringComparison.Ordinal) || HashFile(path) != e.Hash)
            {
                replace.Add(e.Path);
            }
        }

        HashSet<string> inBackup = [.. m.Entries.Select(e => e.Path)];
        List<string> remove = [.. ProjectFiles(projectRoot, m.IncludesTakes).Where(f => !inBackup.Contains(f.Rel)).Select(f => f.Rel)];
        return new RestorePlan(replace, add, remove);
    }

    /// <summary>Puts a backup into an existing project: files are replaced or added, files that are not in the backup are moved to <c>.ample/tmp/restore-trash-*</c>. Used only by <see cref="RestoreInPlaceAct"/>.</summary>
    internal void RestoreInPlace(string idOrPrefix, string projectRoot)
    {
        BackupManifest m = Get(idOrPrefix);
        RestorePlan plan = PlanRestoreInPlace(m.Id, projectRoot);
        string trash = Path.Combine(projectRoot, ".ample", "tmp", "restore-trash-" + clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        foreach (string rel in plan.Remove)
        {
            string from = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, rel);
            string to = Path.Combine(trash, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, true);
        }

        foreach (BackupEntry e in m.Entries.Where(e => plan.Replace.Contains(e.Path) || plan.Add.Contains(e.Path)))
        {
            CopyVerified(e, Ample.Audio.IO.ProjectPath.Resolve(projectRoot, e.Path));
        }
    }

    /// <summary>The full path of an object in the store (it need not exist).</summary>
    public string ObjectPath(string hash)
    {
        string hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash[7..] : hash;
        return Path.Combine(target, "objects", hex[..2], hex);
    }

    /// <summary>The sha256 of a file, as <c>sha256:</c>... text.</summary>
    public static string HashFile(string path)
    {
        using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>The plain content of an object in a file (a copy, or the decrypted object).</summary>
    internal void MaterializeTo(string objectPath, string destination)
    {
        if (DataCipher.IsSealedFile(objectPath))
        {
            (ProjectEncryption.CipherForTarget(target) ?? throw new AmpleException("AMP5310", $"{target} holds encrypted backups but no key file (encryption.json).")).OpenFile(objectPath, destination);
        }
        else
        {
            File.Copy(objectPath, destination, true);
        }
    }

    private string HashObject(string path)
    {
        if (!DataCipher.IsSealedFile(path))
        {
            return HashFile(path);
        }

        string temp = path + ".open-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            MaterializeTo(path, temp);
            return HashFile(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private void EnsureTargetKey(string projectRoot)
    {
        EncryptionConfig mine = ProjectEncryption.Read(projectRoot)!;
        string keyFile = Path.Combine(target, "encryption.json");
        EncryptionConfig? there = ProjectEncryption.ReadFile(keyFile);
        if (there is not null && there.KeyCheck != mine.KeyCheck)
        {
            throw new AmpleException("AMP5314", "This backup folder already holds backups that were encrypted with another key. Use another folder for this project.");
        }

        ProjectEncryption.WriteFile(keyFile, mine);
    }

    private void CopyVerified(BackupEntry e, string destination)
    {
        string obj = ObjectPath(e.Hash);
        if (!File.Exists(obj))
        {
            throw new AmpleException("AMP5301", $"The backup object for '{e.Path}' is missing from {target}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".restore";
        MaterializeTo(obj, partial);
        if (HashFile(partial) != e.Hash)
        {
            File.Delete(partial);
            throw new AmpleException("AMP5301", $"The backup object for '{e.Path}' is damaged (its hash is not the recorded one). Nothing was overwritten with it.");
        }

        File.Move(partial, destination, true);
    }

    private static void SnapshotDatabase(string source, string destination)
    {
        using SqliteConnection from = new(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using SqliteConnection to = new(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
        SqliteConnection.ClearPool(to);
    }

    private static BackupManifest Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), Indented) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5301", $"The backup manifest '{Path.GetFileName(file)}' is damaged: {ex.Message}");
        }
    }

    private Dictionary<string, DateTimeOffset> Verified()
    {
        string file = Path.Combine(target, "verified.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(file), Indented) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
