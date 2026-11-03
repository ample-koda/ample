namespace Ample.Storage.Backup;

/// <summary>One file of a backup or an archive.</summary>
/// <param name="Path">Project-relative path with <c>/</c>.</param>
/// <param name="Hash">Content hash (<c>sha256:</c>...).</param>
/// <param name="Size">Size in bytes.</param>
public sealed record BackupEntry(string Path, string Hash, long Size);

/// <summary>The manifest of one backup (immutable once written).</summary>
/// <param name="Format">Manifest format version (1).</param>
/// <param name="Id">Backup id: time and the start of the manifest hash.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="ProjectName">The project name when the backup was made.</param>
/// <param name="Created">When it was made.</param>
/// <param name="IncludesTakes">False when the takes and their data were left out.</param>
/// <param name="Entries">The files, sorted by path.</param>
public sealed record BackupManifest(int Format, string Id, Guid ProjectId, string ProjectName, DateTimeOffset Created, bool IncludesTakes, IReadOnlyList<BackupEntry> Entries);

/// <summary>What creating a backup did.</summary>
/// <param name="Id">The backup id.</param>
/// <param name="Files">Files in the backup.</param>
/// <param name="NewObjects">Objects that were not in the store yet (the rest is shared with earlier backups).</param>
/// <param name="NewBytes">Bytes written to the store.</param>
/// <param name="TotalBytes">Bytes of all files.</param>
/// <param name="Milliseconds">How long it took.</param>
public sealed record BackupReport(string Id, int Files, int NewObjects, long NewBytes, long TotalBytes, long Milliseconds);

/// <summary>The result of rehashing a backup.</summary>
/// <param name="Id">The backup.</param>
/// <param name="Checked">Objects checked.</param>
/// <param name="Missing">Paths whose object is not in the store.</param>
/// <param name="Corrupt">Paths whose object no longer has its hash.</param>
public sealed record BackupVerifyReport(string Id, int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Corrupt)
{
    /// <summary>Gets a value indicating whether everything is as it was.</summary>
    public bool Ok => Missing.Count == 0 && Corrupt.Count == 0;
}

/// <summary>Which backups to keep: the newest N, plus the newest of each of the last D days. The last good backup is always kept.</summary>
/// <param name="KeepLast">How many of the newest to keep.</param>
/// <param name="KeepDaily">How many days back to keep one per day (0 for none).</param>
public sealed record PruneRules(int KeepLast, int KeepDaily);

/// <summary>What a prune would remove.</summary>
/// <param name="Remove">Backup ids that go.</param>
/// <param name="Keep">Backup ids that stay.</param>
/// <param name="ObjectsFreed">Objects that nothing else uses.</param>
/// <param name="BytesFreed">Their size.</param>
public sealed record PrunePlan(IReadOnlyList<string> Remove, IReadOnlyList<string> Keep, int ObjectsFreed, long BytesFreed);

/// <summary>What an in-place restore would change.</summary>
/// <param name="Replace">Files that exist and differ.</param>
/// <param name="Add">Files that are missing now.</param>
/// <param name="Remove">Files that are not in the backup (moved to a trash folder, not deleted).</param>
public sealed record RestorePlan(IReadOnlyList<string> Replace, IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>What the project remembers about its backups (<c>.ample/backups/last.json</c>), for the health page.</summary>
/// <param name="Target">Where the backups are.</param>
/// <param name="Id">The last backup.</param>
/// <param name="Created">When.</param>
/// <param name="AgeDays">How old it is now.</param>
/// <param name="Auto">True when a backup is made before every dangerous act.</param>
public sealed record BackupStatus(string Target, string Id, DateTimeOffset Created, double AgeDays, bool Auto);
