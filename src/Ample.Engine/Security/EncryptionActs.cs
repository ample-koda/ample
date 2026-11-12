using System.Globalization;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Security;

namespace Ample.Engine.Security;

/// <summary>Switching on the encryption of the black box and of later backups (dangerous: a lost passphrase loses the data for good).</summary>
public sealed class EnableEncryptionAct(string passphrase, DateTimeOffset now, int iterations = Crypto.DefaultIterations) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => "Encrypt the black box and the backups";

    /// <inheritdoc />
    public override string Acceptance => "encrypt this project";

    /// <inheritdoc />
    protected override string Kind => "project.encryption.enable";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ProjectEncryption.IsEnabled(context.ProjectRoot))
        {
            throw new AmpleException("AMP5314", "This project is already encrypted.");
        }

        PassphraseVault.CheckStrength(passphrase);
        int segments = context.Fs.GetFiles(Segments.Folder(context.ProjectRoot)).Count(f => f.EndsWith(".seg", StringComparison.Ordinal));
        return ValueTask.FromResult(new DryRunResult(
            [
                $"{segments.ToString(CultureInfo.InvariantCulture)} black box segment(s) are rewritten with every record sealed (AES-256-GCM); new records are sealed as they are written.",
                "Backups made from now on are encrypted too, and the backup folder gets a copy of the wrapped key (never the passphrase).",
                "Without the passphrase the black box and those backups cannot be opened by anyone, not even by AMPLE. A recovery kit (ample encrypt recovery-kit) holds the key itself: keep it offline.",
                "Not encrypted: the song, the lyrics, the history database and the takes in the project folder (use the disk encryption of your system for those), and the hourly summaries that thinning leaves of old black box segments.",
            ],
            "Make a backup first and print the recovery kit right after: both need the passphrase you are about to set."));
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        (EncryptionConfig config, DataCipher cipher) = ProjectEncryption.Create(passphrase, now, iterations);
        ProjectEncryption.WriteFile(ProjectEncryption.FileOf(context.ProjectRoot), config);
        ProjectEncryption.Remember(context.ProjectRoot, cipher);
        await Segments.RewriteAsync(context, cipher, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the act through the journal.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default) => ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
}

/// <summary>Switching the encryption off again (the black box is written in the clear; backups made earlier stay encrypted and keep needing the key).</summary>
public sealed class DisableEncryptionAct : DangerousAct
{
    /// <inheritdoc />
    public override string Title => "Stop encrypting the black box";

    /// <inheritdoc />
    public override string Acceptance => "decrypt this project";

    /// <inheritdoc />
    protected override string Kind => "project.encryption.disable";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ProjectEncryption.IsEnabled(context.ProjectRoot))
        {
            throw new AmpleException("AMP5314", "This project is not encrypted.");
        }

        _ = ProjectEncryption.CipherFor(context.ProjectRoot);
        int segments = context.Fs.GetFiles(Segments.Folder(context.ProjectRoot)).Count(f => f.EndsWith(".seg", StringComparison.Ordinal));
        return ValueTask.FromResult(new DryRunResult(
            [
                $"{segments.ToString(CultureInfo.InvariantCulture)} black box segment(s) are rewritten in the clear; the key file of the project is removed.",
                "Backups that were made while it was encrypted stay encrypted. Their backup folders keep a copy of the wrapped key, so the passphrase still opens them: do not forget it, and keep the recovery kit.",
            ],
            "Make a backup first."));
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        DataCipher cipher = ProjectEncryption.CipherFor(context.ProjectRoot) ?? throw new AmpleException("AMP5314", "This project is not encrypted.");
        await Segments.RewriteAsync(context, null, cancellationToken, cipher).ConfigureAwait(false);
        File.Delete(ProjectEncryption.FileOf(context.ProjectRoot));
        ProjectEncryption.Forget(context.ProjectRoot);
    }

    /// <summary>Runs the act through the journal.</summary>
    public ValueTask RunAsync(Journal journal, CommandContext context, string actor, string typed, string why, CancellationToken cancellationToken = default) => ExecuteAsync(journal, context, actor, typed, why, cancellationToken);
}

internal static class Segments
{
    public static string Folder(string projectRoot) => Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "blackbox");

    /// <summary>Rewrites every segment: sealed with <paramref name="seal"/> (or in the clear when null). Lines are read through <paramref name="open"/> (or as they are).</summary>
    public static async ValueTask RewriteAsync(CommandContext context, DataCipher? seal, CancellationToken cancellationToken, DataCipher? open = null)
    {
        foreach (string seg in context.Fs.GetFiles(Folder(context.ProjectRoot)).Where(f => f.EndsWith(".seg", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            string text = await context.Fs.ReadAllTextAsync(seg, cancellationToken).ConfigureAwait(false);
            bool endsClean = text.Length == 0 || text.EndsWith('\n');
            string[] lines = [.. text.Split('\n').Where(l => l.Length > 0)];
            string[] rewritten = [.. lines.Select((l, i) => Convert(l, seal, open, !endsClean && i == lines.Length - 1))];
            await AtomicFile.WriteTextAsync(context.Fs, seg, string.Concat(rewritten.Select(l => l + "\n")), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Convert(string line, DataCipher? seal, DataCipher? open, bool torn)
    {
        if (torn)
        {
            // An incomplete last line is left out of the rewrite by the journal on its next open; keep it as it is.
            return line;
        }

        string plain = open is not null && DataCipher.IsSealedLine(line) ? open.OpenLine(line) : line;
        return seal is not null && !DataCipher.IsSealedLine(plain) ? seal.SealLine(plain) : plain;
    }
}
