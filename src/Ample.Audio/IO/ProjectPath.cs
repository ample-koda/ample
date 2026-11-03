using Ample.Core.Abstractions;

namespace Ample.Audio.IO;

/// <summary>Turns user-supplied relative paths into safe absolute paths inside a project (or inside a registered link). Rejects <c>..</c> escapes, absolute paths, reserved Windows names, invalid characters and symbolic links that lead outside.</summary>
public static class ProjectPath
{
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>Resolves a relative path inside a root. Never touches the disk.</summary>
    /// <exception cref="AmpleException">AMP5201 (escapes or absolute) or AMP5202 (invalid or reserved name).</exception>
    public static string Resolve(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new AmpleException("AMP5202", "A project path must not be empty.");
        }

        if (relative.Any(c => c < ' ' || Invalid.Contains(c)))
        {
            throw new AmpleException("AMP5202", $"The path '{Shown(relative)}' contains characters that are not allowed.");
        }

        string normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            throw new AmpleException("AMP5201", $"The path '{Shown(relative)}' is absolute; project paths are relative.");
        }

        List<string> segments = [];
        foreach (string segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new AmpleException("AMP5201", $"The path '{Shown(relative)}' leaves the project.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new AmpleException("AMP5202", $"The path '{Shown(relative)}' has a name that ends with a dot or a space.");
            }

            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (Reserved.Contains(stem))
            {
                throw new AmpleException("AMP5202", $"'{segment}' is a reserved name on Windows.");
            }

            segments.Add(segment);
        }

        string rootFull = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine([rootFull, .. segments]));
        if (!IsInside(rootFull, full))
        {
            throw new AmpleException("AMP5201", $"The path '{Shown(relative)}' leaves the project.");
        }

        return full;
    }

    /// <summary>Resolves a relative path and also checks every existing part for symbolic links or junctions that lead outside the root (unless the target lies inside one of the allowed link roots).</summary>
    /// <exception cref="AmpleException">AMP5203 when a link leads outside.</exception>
    public static string Resolve(IFileSystem fs, string root, string relative, IReadOnlyCollection<string>? allowedLinkRoots = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        string full = Resolve(root, relative);
        string rootFull = Path.GetFullPath(root);
        string current = rootFull;
        foreach (string segment in Path.GetRelativePath(rootFull, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            string? target = fs.GetLinkTarget(current);
            if (target is null)
            {
                continue;
            }

            string targetFull = Path.GetFullPath(target);
            if (!IsInside(rootFull, targetFull) && !(allowedLinkRoots ?? []).Any(r => IsInside(Path.GetFullPath(r), targetFull)))
            {
                throw new AmpleException("AMP5203", $"'{Path.GetRelativePath(rootFull, current)}' is a link that leads outside the project.");
            }
        }

        return full;
    }

    /// <summary>Determines whether a full path lies inside a root folder (the root itself counts as inside).</summary>
    public static bool IsInside(string rootFull, string fullPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string path = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static string Shown(string relative) => relative.Length > 80 ? relative[..80] + "..." : relative.Replace('\0', '?');
}
