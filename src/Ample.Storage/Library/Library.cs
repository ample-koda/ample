using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.Library;

/// <summary>
/// The library of one user: linked folders (mounts) and the assets found in them, with stable asset uuids, incremental scans,
/// relocation, a resolver with a fixed order, packs, import into a project, and a usage index. A read-only mount is never
/// written to: every write goes through <see cref="ResolveWritable"/>. Nothing here uploads anything, scans only open files of
/// kinds that are meant to be hashed, and no folder is registered unless a person names it.
/// </summary>
public sealed partial class Library : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const string MarkerFile = ".ample-catalog.json";
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS mounts (
          id TEXT PRIMARY KEY, uuid TEXT NOT NULL, name TEXT NOT NULL, path TEXT NOT NULL, scope TEXT NOT NULL,
          project_id TEXT NULL, read_only INTEGER NOT NULL, fingerprint TEXT NOT NULL, added TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS assets (
          uuid TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, tags TEXT NOT NULL DEFAULT '', hash TEXT NULL, size INTEGER NOT NULL,
          mount_id TEXT NOT NULL REFERENCES mounts(id), rel_path TEXT NOT NULL, license TEXT NOT NULL DEFAULT '', credit TEXT NOT NULL DEFAULT '',
          added TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 1, missing INTEGER NOT NULL DEFAULT 0, mtime INTEGER NOT NULL DEFAULT 0);
        CREATE UNIQUE INDEX IF NOT EXISTS assets_path ON assets(mount_id, rel_path);
        CREATE INDEX IF NOT EXISTS assets_hash ON assets(hash);
        CREATE INDEX IF NOT EXISTS assets_name ON assets(name);
        """;

    private readonly SqliteConnection db;
    private readonly IClock clock;
    private readonly object gate = new();

    private Library(string home, IClock clock)
    {
        Home = home;
        this.clock = clock;
        Directory.CreateDirectory(home);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(home, "library.db"), Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec(Schema);
    }

    /// <summary>Gets the folder that holds the catalog database and the installed packs.</summary>
    public string Home { get; }

    /// <summary>The default home: <c>AMPLE_HOME</c>, else the local application data of the user.</summary>
    public static string DefaultHome() => Environment.GetEnvironmentVariable("AMPLE_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");

    /// <summary>Opens (and creates) the library in a home folder.</summary>
    public static Library Open(string home, IClock? clock = null) => new(Path.GetFullPath(home), clock ?? new Ample.Audio.IO.SystemClock());

    /// <inheritdoc />
    public void Dispose() => db.Dispose();

    /* ---------- mounts ---------- */

    /// <summary>Links a folder. A writable link gets a marker file with the catalog uuid; a read-only link is never written to (its identity is a fingerprint of its content).</summary>
    /// <exception cref="AmpleException">AMP5290 when the folder does not exist.</exception>
    public Mount LinkMount(string name, string path, string scope = "global", string? projectId = null, bool readOnly = true)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new AmpleException("AMP5290", $"The folder '{full}' is not reachable.");
        }

        lock (gate)
        {
            Mount? existing = MountsInternal().FirstOrDefault(m => string.Equals(m.Path, full, StringComparison.OrdinalIgnoreCase) && m.Scope == scope && m.ProjectId == projectId);
            if (existing is not null)
            {
                return existing;
            }

            string uuid = ReadMarker(full) ?? "cat-" + Guid.NewGuid().ToString("N")[..12];
            if (!readOnly)
            {
                WriteMarker(full, uuid, name);
            }

            Mount m = new(Guid.NewGuid().ToString("N")[..12], uuid, name, full, scope, projectId, readOnly, "online", Fingerprint(full), clock.UtcNow);
            Exec("INSERT INTO mounts (id, uuid, name, path, scope, project_id, read_only, fingerprint, added) VALUES ($i,$u,$n,$p,$s,$pr,$r,$f,$a)", ("$i", m.Id), ("$u", m.Uuid), ("$n", m.Name), ("$p", m.Path), ("$s", m.Scope), ("$pr", m.ProjectId), ("$r", m.ReadOnly ? 1 : 0), ("$f", m.Fingerprint), ("$a", m.Added.ToString("o", CultureInfo.InvariantCulture)));
            return m;
        }
    }

    /// <summary>Lists the global links and, when a project is given, its project links, with their live state.</summary>
    public IReadOnlyList<Mount> Mounts(string? projectId = null)
    {
        lock (gate)
        {
            return [.. MountsInternal().Where(m => m.Scope == "global" || (projectId is not null && m.ProjectId == projectId)).Select(WithState)];
        }
    }

    /// <summary>Gets one mount, with its live state.</summary>
    /// <exception cref="AmpleException">AMP5291 when there is no such mount.</exception>
    public Mount GetMount(string id)
    {
        lock (gate)
        {
            return WithState(MountsInternal().FirstOrDefault(m => m.Id == id) ?? throw new AmpleException("AMP5291", $"There is no linked folder with id '{id}'."));
        }
    }

    /// <summary>Makes a link read-only or read-write (a writable link gets its marker file).</summary>
    public Mount SetReadOnly(string id, bool readOnly)
    {
        lock (gate)
        {
            Mount m = GetMount(id);
            if (!readOnly && m.State == "online")
            {
                WriteMarker(m.Path, m.Uuid, m.Name);
            }

            Exec("UPDATE mounts SET read_only = $r WHERE id = $i", ("$r", readOnly ? 1 : 0), ("$i", id));
            return GetMount(id);
        }
    }

    /// <summary>Unlinks a folder: it and its assets leave the catalog. No file is touched.</summary>
    public void Unlink(string id)
    {
        lock (gate)
        {
            Exec("DELETE FROM assets WHERE mount_id = $i", ("$i", id));
            Exec("DELETE FROM mounts WHERE id = $i", ("$i", id));
        }
    }

    /// <summary>
    /// Points a link at its folder's new place. The identity is checked: the marker file must carry the same uuid, or the
    /// content fingerprint must be the same, or most of the known assets must be there with the same size; otherwise it is
    /// refused unless <paramref name="force"/> is set. Nothing in any song or lock file changes.
    /// </summary>
    /// <exception cref="AmpleException">AMP5292 when the identity check fails.</exception>
    public Mount Relocate(string id, string newPath, bool force = false)
    {
        string full = Path.GetFullPath(newPath);
        if (!Directory.Exists(full))
        {
            throw new AmpleException("AMP5290", $"The folder '{full}' is not reachable.");
        }

        lock (gate)
        {
            Mount m = GetMount(id);
            string? marker = ReadMarker(full);
            bool same = marker == m.Uuid || Fingerprint(full) == m.Fingerprint || MostAssetsThere(m, full);
            if (!same && !force)
            {
                throw new AmpleException("AMP5292", $"The folder '{full}' does not look like the catalog '{m.Name}' (its marker, fingerprint and files differ). Relocate with force to link it anyway.");
            }

            Exec("UPDATE mounts SET path = $p, fingerprint = $f WHERE id = $i", ("$p", full), ("$f", Fingerprint(full)), ("$i", id));
            Exec("UPDATE assets SET missing = 0 WHERE mount_id = $i AND missing = 1 AND hash IS NULL", ("$i", id));
            return GetMount(id);
        }
    }

    /// <summary>Looks for a moved catalog: folders (two levels deep) under the search roots that carry the marker of the mount.</summary>
    public string? FindMoved(string id, IEnumerable<string> searchRoots)
    {
        Mount m = GetMount(id);
        foreach (string root in searchRoots.Where(Directory.Exists))
        {
            foreach (string dir in new[] { root }.Concat(SafeDirs(root)).Concat(SafeDirs(root).SelectMany(SafeDirs)))
            {
                if (ReadMarker(dir) == m.Uuid)
                {
                    return dir;
                }
            }
        }

        return null;
    }

    /// <summary>Gives the full path for writing inside a mount. Refuses read-only mounts, offline mounts and paths that leave the mount.</summary>
    /// <exception cref="AmpleException">AMP5293 for a read-only mount, AMP5290 for an offline one, AMP5201 for an escaping path.</exception>
    public string ResolveWritable(string mountId, string relative)
    {
        Mount m = GetMount(mountId);
        if (m.ReadOnly)
        {
            throw new AmpleException("AMP5293", $"The linked folder '{m.Name}' is read-only: nothing may be written into it.");
        }

        if (m.State != "online")
        {
            throw new AmpleException("AMP5290", $"The linked folder '{m.Name}' is not reachable.");
        }

        return Ample.Audio.IO.ProjectPath.Resolve(m.Path, relative);
    }

    /* ---------- assets ---------- */

    /// <summary>Lists assets, optionally of one mount or kind, or whose name contains the text.</summary>
    public IReadOnlyList<AssetRecord> Assets(string? mountId = null, AssetKind? kind = null, string? search = null)
    {
        lock (gate)
        {
            return [.. AssetsInternal("SELECT * FROM assets ORDER BY name, rel_path").Where(a => (mountId is null || a.MountId == mountId) && (kind is null || a.Kind == kind) && (search is null || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || a.Tags.Contains(search, StringComparison.OrdinalIgnoreCase)))];
        }
    }

    /// <summary>Gets one asset by uuid, or null.</summary>
    public AssetRecord? Asset(string uuid)
    {
        lock (gate)
        {
            return AssetsInternal("SELECT * FROM assets WHERE uuid = $u", ("$u", uuid)).FirstOrDefault();
        }
    }

    /// <summary>Groups of assets that have the same content (by hash), in different places.</summary>
    public IReadOnlyList<IReadOnlyList<AssetRecord>> Duplicates()
    {
        lock (gate)
        {
            return [.. AssetsInternal("SELECT * FROM assets WHERE hash IS NOT NULL AND missing = 0 ORDER BY hash, rel_path").GroupBy(a => a.Hash).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<AssetRecord>)[.. g])];
        }
    }

    /// <summary>Sets tags, license and credit of an asset.</summary>
    public void Describe(string uuid, string? tags = null, string? license = null, string? credit = null)
    {
        lock (gate)
        {
            _ = Asset(uuid) ?? throw new AmpleException("AMP5291", $"There is no asset '{uuid}'.");
            if (tags is not null)
            {
                Exec("UPDATE assets SET tags = $v WHERE uuid = $u", ("$v", tags), ("$u", uuid));
            }

            if (license is not null)
            {
                Exec("UPDATE assets SET license = $v WHERE uuid = $u", ("$v", license), ("$u", uuid));
            }

            if (credit is not null)
            {
                Exec("UPDATE assets SET credit = $v WHERE uuid = $u", ("$v", credit), ("$u", uuid));
            }
        }
    }

    /// <summary>Kinds whose files are opened for hashing. Everything else is only listed.</summary>
    public static bool IsHashed(AssetKind kind) => kind != AssetKind.Other;

    /// <summary>Decides the kind from the file name.</summary>
    public static AssetKind KindOf(string relPath)
    {
        string file = Path.GetFileName(relPath).ToLowerInvariant();
        string ext = Path.GetExtension(file);
        return file switch
        {
            "pack.json" => AssetKind.Pack,
            "plugin.json" => AssetKind.Plugin,
            _ when file.EndsWith(".voice.json", StringComparison.Ordinal) => AssetKind.Voice,
            _ when file.EndsWith(".template.json", StringComparison.Ordinal) => AssetKind.Template,
            _ => ext switch
            {
                ".wav" or ".flac" or ".mp3" or ".ogg" or ".m4a" or ".aif" or ".aiff" => AssetKind.Sample,
                ".amplelrc" or ".lyr" or ".lrc" => AssetKind.Lyrics,
                ".chords" => AssetKind.Chords,
                ".module" => AssetKind.Module,
                ".json" or ".csv" => AssetKind.Data,
                _ => AssetKind.Other,
            },
        };
    }

    /// <summary>Reads the sha256 of a file in one streaming pass.</summary>
    public static string HashFile(string path)
    {
        using FileStream s = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /* ---------- plumbing ---------- */
    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.'));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string? ReadMarker(string folder)
    {
        string file = Path.Combine(folder, MarkerFile);
        try
        {
            if (File.Exists(file))
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                return d.RootElement.TryGetProperty("uuid", out JsonElement u) ? u.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable marker is no marker.
        }

        return null;
    }

    private static void WriteMarker(string folder, string uuid, string name)
    {
        try
        {
            Ample.Audio.IO.RawFiles.WriteText(Path.Combine(folder, MarkerFile), JsonSerializer.Serialize(new { uuid, name }, Indented) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only media: identity falls back to the fingerprint.
        }
    }

    /// <summary>Identity by content: a hash of the sorted relative paths and sizes of the first thousand files.</summary>
    private static string Fingerprint(string folder)
    {
        List<string> lines = [];
        try
        {
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('.')).Take(1000))
            {
                lines.Add(Path.GetRelativePath(folder, f).Replace('\\', '/') + "|" + new FileInfo(f).Length.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Partial fingerprint of what could be read.
        }

        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];
    }

    private bool MostAssetsThere(Mount m, string folder)
    {
        List<AssetRecord> known = [.. AssetsInternal("SELECT * FROM assets WHERE mount_id = $i AND missing = 0 LIMIT 50", ("$i", m.Id))];
        if (known.Count == 0)
        {
            return false;
        }

        int there = known.Count(a => File.Exists(Path.Combine(folder, a.RelPath)) && new FileInfo(Path.Combine(folder, a.RelPath)).Length == a.Size);
        return there * 10 >= known.Count * 8;
    }

    private Mount WithState(Mount m)
    {
        string state = !Directory.Exists(m.Path) ? "offline" : ReadMarker(m.Path) is { } marker && marker != m.Uuid ? "other" : "online";
        return m with { State = state };
    }

    private List<Mount> MountsInternal() => Query("SELECT * FROM mounts ORDER BY name", r => new Mount(r.GetString(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("uuid")), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("path")), r.GetString(r.GetOrdinal("scope")), r.IsDBNull(r.GetOrdinal("project_id")) ? null : r.GetString(r.GetOrdinal("project_id")), r.GetInt64(r.GetOrdinal("read_only")) != 0, "online", r.GetString(r.GetOrdinal("fingerprint")), DateTimeOffset.Parse(r.GetString(r.GetOrdinal("added")), CultureInfo.InvariantCulture)));

    private List<AssetRecord> AssetsInternal(string sql, params (string Name, object? Value)[] args) => Query(sql, r => new AssetRecord(
        r.GetString(r.GetOrdinal("uuid")), Enum.Parse<AssetKind>(r.GetString(r.GetOrdinal("kind")), true), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("tags")),
        r.IsDBNull(r.GetOrdinal("hash")) ? null : r.GetString(r.GetOrdinal("hash")), r.GetInt64(r.GetOrdinal("size")), r.GetString(r.GetOrdinal("mount_id")), r.GetString(r.GetOrdinal("rel_path")),
        r.GetString(r.GetOrdinal("license")), r.GetString(r.GetOrdinal("credit")), DateTimeOffset.Parse(r.GetString(r.GetOrdinal("added")), CultureInfo.InvariantCulture), (int)r.GetInt64(r.GetOrdinal("revision")), r.GetInt64(r.GetOrdinal("missing")) != 0), args);

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args)
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

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }
}
