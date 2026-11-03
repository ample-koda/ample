using System.Globalization;
using Ample.Audio.IO;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Engine.Projects;

/// <summary>Changing the master seed of a project (dangerous: every seed derived from it changes, so the takes made from the old seeds cannot be reproduced from the new one).</summary>
public sealed class ChangeMasterSeedAct(ulong newSeed) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => "Change the master seed";

    /// <inheritdoc />
    public override string Acceptance => "change master seed";

    /// <inheritdoc />
    protected override string Kind => "project.master-seed";

    /// <inheritdoc />
    public override async ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProjectManifest m = ProjectFile.Parse(await context.Fs.ReadAllTextAsync(Path.Combine(context.ProjectRoot, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), cancellationToken).ConfigureAwait(false));
        return new DryRunResult([$"The master seed changes from {m.MasterSeed.ToString(CultureInfo.InvariantCulture)} to {newSeed.ToString(CultureInfo.InvariantCulture)}.", "Every seed that is derived from it (blocks without a seed of their own, variants) changes, so existing takes stay in the bank but can no longer be made again from the seeds. The change takes effect the next time the project is opened."], "Make a backup first; the old master seed is kept in the black box.");
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string file = Path.Combine(context.ProjectRoot, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
        ProjectManifest m = ProjectFile.Parse(await context.Fs.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        await AtomicFile.WriteTextAsync(context.Fs, file, ProjectFile.Serialize(m with { MasterSeed = newSeed }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the act through the journal.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default) => ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
}
