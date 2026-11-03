using Ample.Storage.Projects;

namespace Ample.Storage.Takes;

/// <summary>
/// The temporary working folders a storage clean-up may remove: entries of the project's own <c>.ample/tmp</c> and of the AI working folders
/// (<c>.ample/ai/work|validate|audio</c>) that have not been touched for a day. Nothing outside those folders is ever listed or removed, a folder
/// that is a link (junction, symbolic link) is never followed, and the files of an AI task that is still open (an unapplied proposal can be resumed) stay.
/// </summary>
public static class StorageTemp
{
    private static readonly string[] AiParts = ["work", "validate", "audio"];

    private static IEnumerable<string> Roots(string projectRoot)
    {
        string ample = Path.Combine(projectRoot, ProjectLayout.AmpleFolder);
        yield return Path.Combine(ample, "tmp");
        foreach (string part in AiParts)
        {
            yield return Path.Combine(ample, "ai", part);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // cannot tell: treat as a link
        }
    }

    /// <summary>True when the root and every folder between it and the project's .ample folder are real folders (not links to somewhere else).</summary>
    private static bool RootIsPlain(string projectRoot, string root)
    {
        string ample = Path.GetFullPath(Path.Combine(projectRoot, ProjectLayout.AmpleFolder));
        string current = Path.GetFullPath(root);
        while (true)
        {
            if (!Directory.Exists(current) || IsLink(current))
            {
                return false;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(ample), StringComparison.OrdinalIgnoreCase))
            {
                return !IsLink(ample);
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
            if (current.Length == 0)
            {
                return false;
            }
        }
    }

    private static DateTime Newest(string entry)
    {
        DateTime newest = File.GetLastWriteTimeUtc(entry);
        if (Directory.Exists(entry))
        {
            foreach (string f in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (t > newest)
                {
                    newest = t;
                }
            }
        }

        return newest;
    }

    /// <summary>Lists the removable entries with their size.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="aiTaskOpen">True for the name of an AI working entry (a folder or file named after its task) that belongs to a task that must be kept.</param>
    public static List<(string Path, long Bytes)> List(string projectRoot, DateTime nowUtc, Func<string, bool> aiTaskOpen)
    {
        ArgumentNullException.ThrowIfNull(aiTaskOpen);
        List<(string, long)> list = [];
        DateTime limit = nowUtc.AddDays(-1);
        bool first = true;
        foreach (string root in Roots(projectRoot))
        {
            bool ai = !first;
            first = false;
            if (!RootIsPlain(projectRoot, root))
            {
                continue;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (IsLink(entry) || (ai && aiTaskOpen(Path.GetFileName(entry))) || Newest(entry) >= limit)
                    {
                        continue;
                    }

                    long bytes = Directory.Exists(entry)
                        ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                        : new FileInfo(entry).Length;
                    list.Add((entry, bytes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // changing while we look: skip it
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Removes one listed entry, after checking again that it is a direct child of one of the temporary folders of this project and is not a link.
    /// Returns false when it was refused or is in use.
    /// </summary>
    public static bool Delete(string projectRoot, string path)
    {
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
        if (parent is null || !Roots(projectRoot).Any(r => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!RootIsPlain(projectRoot, parent) || (!Directory.Exists(full) && !File.Exists(full)) || IsLink(full))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, true);
            }
            else
            {
                File.Delete(full);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
