using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace Ample.Storage.Library;

public sealed partial class Library
{
    /// <summary>
    /// Scans a mount incrementally: a file whose size and time are unchanged is not read; a new file is hashed (when its kind
    /// is meant to be) and, when the same content was just lost from another path, it is the same asset moved (uuid kept);
    /// a changed file gets a new revision under the same uuid; a file that is gone is marked missing, never deleted.
    /// </summary>
    /// <exception cref="AmpleException">AMP5290 when the folder is not reachable.</exception>
    public ScanReport Scan(string mountId, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        lock (gate)
        {
            Mount mount = GetMount(mountId);
            if (mount.State == "offline")
            {
                throw new AmpleException("AMP5290", $"The linked folder '{mount.Name}' is not reachable.");
            }

            Dictionary<string, (AssetRecord Asset, long Mtime)> rows = Query("SELECT *, mtime AS mt FROM assets WHERE mount_id = $i", r => (Asset: new AssetRecord(r.GetString(r.GetOrdinal("uuid")), Enum.Parse<AssetKind>(r.GetString(r.GetOrdinal("kind")), true), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("tags")), r.IsDBNull(r.GetOrdinal("hash")) ? null : r.GetString(r.GetOrdinal("hash")), r.GetInt64(r.GetOrdinal("size")), mountId, r.GetString(r.GetOrdinal("rel_path")), r.GetString(r.GetOrdinal("license")), r.GetString(r.GetOrdinal("credit")), DateTimeOffset.Parse(r.GetString(r.GetOrdinal("added")), CultureInfo.InvariantCulture), (int)r.GetInt64(r.GetOrdinal("revision")), r.GetInt64(r.GetOrdinal("missing")) != 0), Mtime: r.GetInt64(r.GetOrdinal("mt"))), ("$i", mountId)).ToDictionary(x => x.Asset.RelPath, x => x);
            HashSet<string> seen = [];
            List<(string Rel, long Size, long Mtime, AssetKind Kind, string? Hash)> fresh = [];
            int added = 0, changed = 0, moved = 0, unchanged = 0, hashed = 0;
            using SqliteTransaction tx = db.BeginTransaction();
            foreach (string file in Walk(mount.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(mount.Path, file).Replace('\\', '/');
                FileInfo info = new(file);
                long mtime = info.LastWriteTimeUtc.Ticks;
                seen.Add(rel);
                if (rows.TryGetValue(rel, out (AssetRecord Asset, long Mtime) row))
                {
                    if (row.Mtime == mtime && row.Asset.Size == info.Length && !row.Asset.Missing)
                    {
                        unchanged++;
                        continue;
                    }

                    string? newHash = IsHashed(row.Asset.Kind) ? HashFile(file) : null;
                    hashed += newHash is null ? 0 : 1;
                    bool differs = newHash != row.Asset.Hash || info.Length != row.Asset.Size;
                    Exec("UPDATE assets SET size = $s, hash = $h, mtime = $m, missing = 0, revision = revision + $r WHERE uuid = $u", ("$s", info.Length), ("$h", newHash), ("$m", mtime), ("$r", differs ? 1 : 0), ("$u", row.Asset.Uuid));
                    if (differs)
                    {
                        changed++;
                    }
                    else
                    {
                        unchanged++;
                    }

                    continue;
                }

                AssetKind kind = KindOf(rel);
                string? hash = IsHashed(kind) ? HashFile(file) : null;
                hashed += hash is null ? 0 : 1;
                fresh.Add((rel, info.Length, mtime, kind, hash));
            }

            List<AssetRecord> lost = [.. rows.Values.Where(r => !seen.Contains(r.Asset.RelPath)).Select(r => r.Asset)];
            foreach ((string rel, long size, long mtime, AssetKind kind, string? hash) in fresh)
            {
                AssetRecord? same = hash is null ? null : lost.FirstOrDefault(l => l.Hash == hash && l.Size == size);
                string name = NameOf(mount.Path, rel, kind);
                if (same is not null)
                {
                    lost.Remove(same);
                    Exec("UPDATE assets SET rel_path = $p, name = $n, mtime = $m, missing = 0 WHERE uuid = $u", ("$p", rel), ("$n", name), ("$m", mtime), ("$u", same.Uuid));
                    moved++;
                    continue;
                }

                Exec("INSERT INTO assets (uuid, kind, name, hash, size, mount_id, rel_path, added, mtime) VALUES ($u,$k,$n,$h,$s,$m,$p,$a,$t)", ("$u", "ast-" + Guid.NewGuid().ToString("N")[..12]), ("$k", kind.ToString().ToLowerInvariant()), ("$n", name), ("$h", hash), ("$s", size), ("$m", mountId), ("$p", rel), ("$a", clock.UtcNow.ToString("o", CultureInfo.InvariantCulture)), ("$t", mtime));
                added++;
            }

            foreach (AssetRecord gone in lost)
            {
                Exec("UPDATE assets SET missing = 1 WHERE uuid = $u", ("$u", gone.Uuid));
            }

            tx.Commit();
            return new ScanReport(added, changed, moved, lost.Count, unchanged, hashed, sw.ElapsedMilliseconds);
        }
    }

    private static string NameOf(string root, string rel, AssetKind kind)
    {
        if (kind is AssetKind.Pack or AssetKind.Plugin)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, rel)));
                string? name = d.RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : d.RootElement.TryGetProperty("id", out JsonElement i) ? i.GetString() : null;
                string? version = d.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
                if (name is not null)
                {
                    return version is null ? name : $"{name}@{version}";
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Falls back to the path.
            }
        }

        string noExt = kind == AssetKind.Other ? rel : rel[..^Path.GetExtension(rel).Length];
        return noExt;
    }

    private static IEnumerable<string> Walk(string root)
    {
        Stack<string> dirs = new([root]);
        while (dirs.Count > 0)
        {
            string dir = dirs.Pop();
            IEnumerable<string> files;
            IEnumerable<string> sub;
            try
            {
                files = Directory.EnumerateFiles(dir);
                sub = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string f in files.Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal))
            {
                yield return f;
            }

            foreach (string d in sub.Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal))
            {
                dirs.Push(d);
            }
        }
    }
}
