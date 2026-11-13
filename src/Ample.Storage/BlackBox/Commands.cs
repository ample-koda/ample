using System.Text.Json;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Storage.BlackBox;

/// <summary>What a command may use.</summary>
/// <param name="Fs">The file system.</param>
/// <param name="ProjectRoot">The project folder.</param>
/// <param name="Clock">The clock.</param>
public sealed record CommandContext(IFileSystem Fs, string ProjectRoot, IClock Clock);

/// <summary>A state change. Every change of project state is a command, so the black box sees it before it happens.</summary>
public interface ICommand
{
    /// <summary>Gets the kind, for example <c>file.write</c>.</summary>
    string Kind { get; }

    /// <summary>Gets the full payload as JSON text.</summary>
    string PayloadJson { get; }

    /// <summary>Captures what the command is about to overwrite (JSON text), or null.</summary>
    ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken);

    /// <summary>Applies the change.</summary>
    ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}

/// <summary>The one path for state changes: validate, write the journal record (durable), apply, write the outcome. If the journal cannot be written the command is not run.</summary>
public sealed class CommandBus(Journal journal, CommandContext context, string actor)
{
    /// <summary>Runs a command through the journal.</summary>
    public async ValueTask ExecuteAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string correlation = Guid.NewGuid().ToString("N");
        string? pre = await command.PreImageAsync(context, cancellationToken).ConfigureAwait(false);
        await journal.AppendAsync(actor, command.Kind, "start", correlation, command.PayloadJson, pre, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await command.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await journal.AppendAsync(actor, command.Kind, "failed", correlation, command.PayloadJson, null, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await journal.AppendAsync(actor, command.Kind, "ok", correlation, command.PayloadJson, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an arbitrary change as a journaled command (used for database operations such as history).</summary>
    public async ValueTask RunAsync(string kind, string payloadJson, Func<ValueTask> action, CancellationToken cancellationToken = default) =>
        _ = await RunWithStartRecordAsync(kind, payloadJson, _ => action(), cancellationToken).ConfigureAwait(false);

    /// <summary>Runs a database change and exposes its durable start record to the action.</summary>
    public async ValueTask<JournalRecord> RunWithStartRecordAsync(string kind, string payloadJson, Func<JournalRecord, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        string correlation = Guid.NewGuid().ToString("N");
        JournalRecord start = await journal.AppendAsync(actor, kind, "start", correlation, payloadJson, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await action(start).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await journal.AppendAsync(actor, kind, "failed", correlation, payloadJson, null, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await journal.AppendAsync(actor, kind, "ok", correlation, payloadJson, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks an interrupted command as dealt with (finished or abandoned by the user).</summary>
    public async ValueTask ResolveAsync(InterruptedCommand interrupted, string note, CancellationToken cancellationToken = default) =>
        await journal.AppendAsync(actor, interrupted.Start.Kind, "resolved", interrupted.Start.Correlation, JsonSerializer.Serialize(new { note }), null, null, cancellationToken).ConfigureAwait(false);
}

/// <summary>Writes a text file inside the project; the old content is the pre-image.</summary>
public sealed class WriteFileCommand(string relativePath, string text) : ICommand
{
    private const int InlineLimit = 1024 * 1024;

    /// <inheritdoc />
    public string Kind => "file.write";

    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { path = relativePath, length = text.Length });

    /// <inheritdoc />
    public async ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(context.ProjectRoot, relativePath);
        if (!context.Fs.FileExists(full))
        {
            return JsonSerializer.Serialize(new { path = relativePath, existed = false });
        }

        string old = await context.Fs.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        return old.Length <= InlineLimit
            ? JsonSerializer.Serialize(new { path = relativePath, existed = true, text = old })
            : JsonSerializer.Serialize(new { path = relativePath, existed = true, tooLarge = true, length = old.Length });
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(context.ProjectRoot, relativePath);
        context.Fs.CreateDirectory(Path.GetDirectoryName(full)!);
        await AtomicFile.WriteTextAsync(context.Fs, full, text, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Imports binary project data through the command bus and keeps an inline pre-image for recovery.</summary>
public sealed class ImportFileCommand(string relativePath, byte[] contents) : ICommand
{
    /// <inheritdoc />
    public string Kind => "file.import";

    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { path = relativePath, length = contents.Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contents)) });

    /// <inheritdoc />
    public async ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(context.Fs, context.ProjectRoot, relativePath);
        if (!context.Fs.FileExists(full)) return JsonSerializer.Serialize(new { path = relativePath, existed = false });
        byte[] old = await context.Fs.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path = relativePath, existed = true, bytes = Convert.ToBase64String(old) });
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(context.Fs, context.ProjectRoot, relativePath);
        string directory = Path.GetDirectoryName(full)!;
        context.Fs.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + AtomicFile.TempSuffix);
        try
        {
            await context.Fs.WriteAllBytesDurableAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
            context.Fs.Move(temporary, full, overwrite: true);
            context.Fs.FlushDirectory(directory);
        }
        catch
        {
            context.Fs.DeleteFile(temporary);
            throw;
        }
    }
}

/// <summary>Restores a file from the pre-image of an earlier record, as a new command (history is never rewritten).</summary>
public sealed class RestorePreImageCommand(JournalRecord source) : ICommand
{
    /// <inheritdoc />
    public string Kind => "file.restore";

    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { fromSeq = source.Seq });

    /// <inheritdoc />
    public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(source.PreImageJson ?? "{}");
        JsonElement r = doc.RootElement;
        if (!r.TryGetProperty("operation", out JsonElement operation)) return Inner().PreImageAsync(context, cancellationToken);
        string kind = operation.GetString()!;
        if (kind == "folder.create") return ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "folder.delete", path = r.GetProperty("path").GetString() }));
        if (kind == "folder.delete") return ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "folder.create", path = r.GetProperty("path").GetString() }));
        if (kind == "path.move") return ValueTask.FromResult<string?>(JsonSerializer.Serialize(new { operation = "path.move", source = r.GetProperty("destination").GetString(), destination = r.GetProperty("source").GetString(), directory = r.GetProperty("directory").GetBoolean() }));
        return Inner().PreImageAsync(context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using JsonDocument doc = JsonDocument.Parse(source.PreImageJson ?? throw new AmpleException("AMP5231", $"Record {source.Seq} has no pre-image to restore."));
        JsonElement r = doc.RootElement;
        if (r.TryGetProperty("operation", out JsonElement operation))
        {
            if (operation.GetString() == "folder.create")
            {
                string folder = ProjectPath.Resolve(context.ProjectRoot, r.GetProperty("path").GetString()!);
                if (context.Fs.GetFiles(folder).Count != 0 || context.Fs.GetDirectories(folder).Count != 0) throw new AmpleException("AMP5231", "The created folder is no longer empty and cannot be restored safely.");
                context.Fs.DeleteDirectory(folder, recursive: false);
                return ValueTask.CompletedTask;
            }

            if (operation.GetString() == "folder.delete")
            {
                context.Fs.CreateDirectory(ProjectPath.Resolve(context.ProjectRoot, r.GetProperty("path").GetString()!));
                return ValueTask.CompletedTask;
            }

            if (operation.GetString() == "path.move")
            {
                return new MoveProjectPathCommand(r.GetProperty("destination").GetString()!, r.GetProperty("source").GetString()!, r.GetProperty("directory").GetBoolean()).ExecuteAsync(context, cancellationToken);
            }
        }

        if (!r.GetProperty("existed").GetBoolean())
        {
            context.Fs.DeleteFile(ProjectPath.Resolve(context.ProjectRoot, r.GetProperty("path").GetString()!));
            return ValueTask.CompletedTask;
        }

        if (r.TryGetProperty("text", out JsonElement text)) return new WriteFileCommand(r.GetProperty("path").GetString()!, text.GetString()!).ExecuteAsync(context, cancellationToken);
        if (r.TryGetProperty("bytes", out JsonElement bytes)) return new ImportFileCommand(r.GetProperty("path").GetString()!, Convert.FromBase64String(bytes.GetString()!)).ExecuteAsync(context, cancellationToken);
        return ValueTask.FromException(new AmpleException("AMP5231", $"The pre-image of record {source.Seq} was too large to keep inline."));
    }

    private WriteFileCommand Inner()
    {
        using JsonDocument doc = JsonDocument.Parse(source.PreImageJson ?? JsonSerializer.Serialize(new { path = "?" }));
        return new WriteFileCommand(doc.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString()! : "unknown", string.Empty);
    }
}

/// <summary>Changes the project settings in <c>project.json</c>; the old file is the pre-image.</summary>
public sealed class SetSettingsCommand(ProjectSettings settings, ProjectSettings? basis = null) : ICommand
{
    /// <inheritdoc />
    public string Kind => "project.settings";

    /// <inheritdoc />
    public string PayloadJson => JsonSerializer.Serialize(new { blackBoxBudget = settings.BlackBox.BudgetBytes, fullDetailDays = settings.BlackBox.FullDetailDays, thinnedDays = settings.BlackBox.ThinnedDays });

    private static string FilePath(CommandContext c) => Path.Combine(c.ProjectRoot, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);

    /// <summary>With a basis (the settings the request started from), only the settings groups this request changed are applied onto the current file, so two saves of unrelated groups compose.</summary>
    private ProjectSettings Merge(ProjectSettings current)
    {
        ProjectSettings was = basis!;
        return current with
        {
            BlackBox = Equals(settings.BlackBox, was.BlackBox) ? current.BlackBox : settings.BlackBox,
            DefaultEngine = settings.DefaultEngine == was.DefaultEngine ? current.DefaultEngine : settings.DefaultEngine,
            ProjectPanel = Equals(settings.ProjectPanel, was.ProjectPanel) ? current.ProjectPanel : settings.ProjectPanel,
            Faer = Equals(settings.Faer, was.Faer) ? current.Faer : settings.Faer,
            Koda = Equals(settings.Koda, was.Koda) ? current.Koda : settings.Koda,
            Ai = Equals(settings.Ai, was.Ai) ? current.Ai : settings.Ai,
            BuildParallelism = settings.BuildParallelism == was.BuildParallelism ? current.BuildParallelism : settings.BuildParallelism,
        };
    }

    /// <inheritdoc />
    public async ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(new { path = ProjectLayout.AmpleFolder + "/" + ProjectLayout.ProjectFile, existed = true, text = await context.Fs.ReadAllTextAsync(FilePath(context), cancellationToken).ConfigureAwait(false) });

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ProjectManifest manifest = ProjectFile.Parse(await context.Fs.ReadAllTextAsync(FilePath(context), cancellationToken).ConfigureAwait(false));
        await AtomicFile.WriteTextAsync(context.Fs, FilePath(context), ProjectFile.Serialize(manifest with { Settings = basis is null ? settings : Merge(manifest.Settings) }), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The result of a dry run.</summary>
/// <param name="Impact">What would change, one line each.</param>
/// <param name="BackupSuggestion">Why and when a backup is advisable.</param>
public sealed record DryRunResult(IReadOnlyList<string> Impact, string BackupSuggestion);

/// <summary>The shared contract of every dangerous act (delete, purge, cleanup, re-tag, replace, master-seed change, lock take-over): a dry run, a backup suggestion, a typed acceptance, a reason, and a tombstone in the journal. There is no shortcut.</summary>
public abstract class DangerousAct
{
    /// <summary>
    /// Runs before an act executes (after the typed acceptance and the reason were checked): the place for the opt-in backup
    /// before dangerous acts. When it throws, the act does not run.
    /// </summary>
    public static Func<CommandContext, CancellationToken, ValueTask>? BeforeExecute { get; set; }

    /// <summary>Gets the title shown to the user.</summary>
    public abstract string Title { get; }

    /// <summary>Gets the exact text the user must type to accept (for example the name of the line).</summary>
    public abstract string Acceptance { get; }

    /// <summary>Gets the command kind.</summary>
    protected abstract string Kind { get; }

    /// <summary>Computes what would happen without doing it.</summary>
    public abstract ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken);

    /// <summary>Does it.</summary>
    protected abstract ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken);

    /// <summary>Runs the act through the journal; refused unless the typed acceptance matches exactly and a reason is given.</summary>
    /// <exception cref="AmpleException">AMP5232 when the acceptance or the reason is missing.</exception>
    public async ValueTask ExecuteAsync(Journal journal, CommandContext context, string actor, string typedAcceptance, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!string.Equals(typedAcceptance, Acceptance, StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5232", $"To accept, type exactly: {Acceptance}");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new AmpleException("AMP5232", "A dangerous act needs a reason.");
        }

        DryRunResult dry = await DryRunAsync(context, cancellationToken).ConfigureAwait(false);
        if (BeforeExecute is not null)
        {
            await BeforeExecute(context, cancellationToken).ConfigureAwait(false);
        }

        string payload = JsonSerializer.Serialize(new { title = Title, reason, impact = dry.Impact });
        string correlation = Guid.NewGuid().ToString("N");
        await journal.AppendAsync(actor, "dangerous:" + Kind, "start", correlation, payload, null, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteCoreAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await journal.AppendAsync(actor, "dangerous:" + Kind, "failed", correlation, payload, null, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await journal.AppendAsync(actor, "dangerous:" + Kind, "ok", correlation, payload, null, null, cancellationToken).ConfigureAwait(false);
        await journal.AppendAsync(actor, "dangerous:" + Kind, "tombstone", correlation, payload, null, null, cancellationToken).ConfigureAwait(false);
    }
}
