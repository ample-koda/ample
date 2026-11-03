using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;

namespace Ample.Storage.Backup;

/// <summary>Pruning the backups of a store (dangerous: backups are the way back). The last good backup is never removed.</summary>
public sealed class PruneBackupsAct(BackupStore store, PruneRules rules) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => "Prune the backups in " + store.Target;

    /// <inheritdoc />
    public override string Acceptance => "prune " + store.PlanPrune(rules).Remove.Count.ToString(CultureInfo.InvariantCulture) + " backups";

    /// <inheritdoc />
    protected override string Kind => "backup.prune";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        PrunePlan p = store.PlanPrune(rules);
        return ValueTask.FromResult(new DryRunResult([$"{p.Remove.Count.ToString(CultureInfo.InvariantCulture)} backup(s) are removed: {string.Join(", ", p.Remove)}", $"{p.Keep.Count.ToString(CultureInfo.InvariantCulture)} stay; {p.ObjectsFreed.ToString(CultureInfo.InvariantCulture)} stored file(s), {p.BytesFreed.ToString(CultureInfo.InvariantCulture)} bytes, are freed."], "A removed backup cannot be made again. The last good backup is always kept; verify one first if you have not."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        store.Prune(store.PlanPrune(rules));
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the act through the journal.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default) => ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
}

/// <summary>Putting a backup into the existing project (dangerous: files are replaced; files that are not in the backup are moved to a trash folder, never deleted).</summary>
public sealed class RestoreInPlaceAct(BackupStore store, string backupId, string projectRoot) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => $"Restore backup {store.Get(backupId).Id} into this project";

    /// <inheritdoc />
    public override string Acceptance => "restore " + store.Get(backupId).Id;

    /// <inheritdoc />
    protected override string Kind => "backup.restore";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        RestorePlan p = store.PlanRestoreInPlace(backupId, projectRoot);
        return ValueTask.FromResult(new DryRunResult([$"{p.Replace.Count.ToString(CultureInfo.InvariantCulture)} file(s) are replaced, {p.Add.Count.ToString(CultureInfo.InvariantCulture)} added.", $"{p.Remove.Count.ToString(CultureInfo.InvariantCulture)} file(s) that are not in the backup are moved to .ample/tmp/restore-trash-*, not deleted.", "The history and the black box come back as they were in the backup."], "Make a backup of the current state first; restoring into a new folder changes nothing here."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        store.RestoreInPlace(backupId, projectRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the act through the journal.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default) => ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
}

/// <summary>What the project remembers about its backups, and the opt-in to a backup before every dangerous act.</summary>
public static class BackupPolicy
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Folder(string projectRoot) => Path.Combine(projectRoot, ".ample", "backups");

    /// <summary>Remembers the last backup of a project.</summary>
    public static void Remember(string projectRoot, string target, string id, DateTimeOffset created)
    {
        Directory.CreateDirectory(Folder(projectRoot));
        bool auto = Read(projectRoot)?.Auto ?? false;
        Ample.Audio.IO.RawFiles.WriteText(Path.Combine(Folder(projectRoot), "last.json"), JsonSerializer.Serialize(new { target, id, created, auto }, Indented) + "\n");
    }

    /// <summary>Turns the backup before every dangerous act on or off (and remembers where to).</summary>
    public static void SetAuto(string projectRoot, string target, bool on)
    {
        Directory.CreateDirectory(Folder(projectRoot));
        BackupStatus? last = Read(projectRoot);
        Ample.Audio.IO.RawFiles.WriteText(Path.Combine(Folder(projectRoot), "last.json"), JsonSerializer.Serialize(new { target, id = last?.Id ?? string.Empty, created = last?.Created ?? DateTimeOffset.MinValue, auto = on }, Indented) + "\n");
    }

    /// <summary>Reads the status, or null when there is none.</summary>
    public static BackupStatus? Read(string projectRoot, IClock? clock = null)
    {
        string file = Path.Combine(Folder(projectRoot), "last.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement r = d.RootElement;
            DateTimeOffset created = r.GetProperty("created").GetDateTimeOffset();
            DateTimeOffset now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
            return new BackupStatus(r.GetProperty("target").GetString()!, r.GetProperty("id").GetString()!, created, created == DateTimeOffset.MinValue ? double.PositiveInfinity : (now - created).TotalDays, r.TryGetProperty("auto", out JsonElement a) && a.GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>One plain sentence for the dry run of a dangerous act: how old the last backup is.</summary>
    public static string Suggestion(string projectRoot, IClock? clock = null)
    {
        BackupStatus? s = Read(projectRoot, clock);
        return s is null || s.Id.Length == 0
            ? "There is no backup of this project yet: make one before you go on (ample backup create --target <folder>)."
            : s.AgeDays < 1 ? $"The last backup is from today ({s.Id})." : $"The last backup is {Math.Floor(s.AgeDays).ToString(CultureInfo.InvariantCulture)} day(s) old ({s.Id}); consider a new one.";
    }
}
