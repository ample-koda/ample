using System.Security.Cryptography;

namespace Ample.Server;

/// <summary>What is needed to undo an accepted proposal safely. Pure where possible, so it is tested without a server.</summary>
internal static class AcceptRecovery
{
    /// <summary>The SHA-256 of a file as it is now, or null when the file does not exist.</summary>
    internal static string? HashOfFile(string full) => File.Exists(full) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) : null;

    /// <summary>
    /// The files that are not the way Accept left them: the recorded hash (null: the file was not there) differs from the current one.
    /// Undoing the proposal would overwrite the work that was done in them after Accept.
    /// </summary>
    /// <param name="recorded">Project path to the hash that Accept left (null when it left no file).</param>
    /// <param name="current">The hash of a path now (null when the file does not exist).</param>
    /// <returns>The changed paths in order.</returns>
    internal static IReadOnlyList<string> ChangedSince(IReadOnlyDictionary<string, string?> recorded, Func<string, string?> current) =>
        [.. recorded.Where(pair => !string.Equals(pair.Value, current(pair.Key), StringComparison.Ordinal)).Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Whether an applied proposal has lost what it needs to be undone: it changed files other than the song, but nothing was kept
    /// to restore them (a proposal accepted before this data was saved, and the server restarted since).
    /// </summary>
    internal static bool RecoveryMissing(bool applied, int undoCount, int includedCount, int otherChanges) =>
        applied && otherChanges > 0 && undoCount == 0 && includedCount == 0;
}
