namespace Ample.Server;

/// <summary>Which generated assets an accepted selection needs. Pure, so it is tested without a server.</summary>
internal static class AcceptAssets
{
    /// <summary>
    /// The staged binary files (such as a sample the proposal made) that the given texts refer to, by project path or by file name.
    /// Accepting only some files of a proposal still has to bring the samples that those files use, or the accepted song would miss them.
    /// </summary>
    /// <param name="staged">Project-relative paths of the binaries the proposal made.</param>
    /// <param name="texts">The new text of the files that are accepted.</param>
    /// <returns>The paths to copy, in the order of <paramref name="staged"/>.</returns>
    internal static IReadOnlyList<string> RequiredBinaries(IEnumerable<string> staged, IEnumerable<string> texts)
    {
        string[] all = [.. texts.Where(t => !string.IsNullOrEmpty(t))];
        List<string> needed = [];
        foreach (string rel in staged)
        {
            string name = Path.GetFileName(rel);
            if (all.Any(t => t.Contains(rel, StringComparison.Ordinal) || (name.Length > 0 && t.Contains(name, StringComparison.Ordinal))))
            {
                needed.Add(rel);
            }
        }

        return needed;
    }
}
