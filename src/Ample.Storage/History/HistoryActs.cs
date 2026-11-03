using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;

namespace Ample.Storage.History;

/// <summary>Drops (archives) a line. Studio and the line you are on cannot be dropped. Recoverable with <see cref="HistoryService.RestoreLineAsync"/>.</summary>
public sealed class DropLineAct(HistoryService history, string line) : DangerousAct
{
    private string reason = string.Empty;

    /// <inheritdoc />
    public override string Title => $"Drop the line '{line}'";

    /// <inheritdoc />
    public override string Acceptance => "drop " + line;

    /// <inheritdoc />
    protected override string Kind => "history.line.drop";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        LineInfo l = history.LineOf(line);
        if (l.Kind == "studio")
        {
            throw new AmpleException("AMP5241", "The studio line is the real version and cannot be dropped.");
        }

        if (history.CurrentLine == line)
        {
            throw new AmpleException("AMP5242", $"You are working on '{line}'. Go to another line first.");
        }

        int own = history.Log(line, 1000).Count(n => !history.Tags().Any(t => t.NodeId == n.NodeId));
        return ValueTask.FromResult(new DryRunResult([$"The line '{line}' is archived; nothing is deleted (cleanup is a separate act).", $"{own.ToString(CultureInfo.InvariantCulture)} node(s) are only reachable from it and become cleanup candidates."], "Nothing is lost by dropping; make a backup before the later cleanup."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        history.Archive(line, true, reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the act with the reason recorded.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default)
    {
        reason = why;
        return ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
    }
}

/// <summary>Moves or removes a locked tag.</summary>
public sealed class RetagAct(HistoryService history, string tag, string? newReference) : DangerousAct
{
    private string reason = string.Empty;

    /// <inheritdoc />
    public override string Title => newReference is null ? $"Remove the tag '{tag}'" : $"Move the tag '{tag}' to '{newReference}'";

    /// <inheritdoc />
    public override string Acceptance => (newReference is null ? "remove " : "move ") + tag;

    /// <inheritdoc />
    protected override string Kind => "history.tag.change";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        (string Name, string NodeId, string Reason) current = history.Tags().FirstOrDefault(t => t.Name == tag);
        return current.Name is null
            ? throw new AmpleException("AMP5243", $"There is no tag '{tag}'.")
            : ValueTask.FromResult(new DryRunResult([$"The tag '{tag}' now points at {current.NodeId} ({current.Reason})." + (newReference is null ? " It would be removed." : $" It would point at {history.Resolve(newReference).NodeId}.")], "A tag protects its snapshot from cleanup: after this the old snapshot may become a cleanup candidate."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        history.MoveTag(tag, newReference is null ? null : history.Resolve(newReference).NodeId, reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the act with the reason recorded.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default)
    {
        reason = why;
        return ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
    }
}

/// <summary>Removes the file contents of snapshots that no active line and no tag can reach. Tombstones remain; tags and active lines are never touched.</summary>
public sealed class CleanupAct(HistoryService history) : DangerousAct
{
    private string reason = string.Empty;
    private CleanupPlan? plan;

    /// <inheritdoc />
    public override string Title => "Clean up unreachable history";

    /// <inheritdoc />
    public override string Acceptance => "cleanup";

    /// <inheritdoc />
    protected override string Kind => "history.cleanup";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        plan = history.PlanCleanup();
        List<string> lines = [$"{plan.Nodes.Count.ToString(CultureInfo.InvariantCulture)} node(s) are unreachable from every active line and tag.", $"{plan.Blobs.Count.ToString(CultureInfo.InvariantCulture)} file content(s), {plan.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, would be removed from the history store."];
        if (plan.ArchivedLines is { Count: > 0 })
        {
            lines.Add($"Dropped line(s) {string.Join(", ", plan.ArchivedLines)} are archived first in .ample/history-archive ({(plan.ArchivedBlobs?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} file content(s), {plan.ArchiveBytes.ToString(CultureInfo.InvariantCulture)} bytes); Bring back restores them from there. The clean-up never deletes that archive.");
        }

        return ValueTask.FromResult(new DryRunResult(lines, plan.ArchivedLines is { Count: > 0 } ? "If the archive cannot be written and verified, nothing is removed. Other unreachable content (not from a dropped line) is removed for good: make a backup first." : "Removed content can only come back from a backup."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        history.ExecuteCleanup(plan ?? history.PlanCleanup(), reason);
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs the act with the reason recorded.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default)
    {
        reason = why;
        return ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
    }
}
