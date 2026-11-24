using Ample.Audio.IO;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>
/// One rule for a folder that a request names with an absolute path (the target of a backup, the folder of a new project, a folder to link into the library):
/// it must be a real absolute path, must not climb with <c>..</c>, must not be or lie in a private <c>.ample</c> folder, and must not be the root of a drive or
/// a system folder of the operating system (security finding 61).
/// </summary>
internal static class ServerPaths
{
    /// <summary>Returns the full path of a folder a request names, or throws AMP5285 when it is not one a person can choose.</summary>
    /// <param name="text">The path as sent.</param>
    /// <param name="what">What the folder is for, for the message ("backup target").</param>
    /// <exception cref="AmpleException">AMP5285 when the path is not allowed.</exception>
    internal static string Folder(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => c < ' '))
        {
            throw new AmpleException("AMP5285", $"The {what} is empty or has characters that are not allowed.");
        }

        if (!Path.IsPathRooted(text) || text.Replace('\\', '/').Split('/').Any(part => part == ".."))
        {
            throw new AmpleException("AMP5285", $"The {what} must be a full path without '..'.");
        }

        string full = Path.GetFullPath(text);
        string[] parts = full.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.TrimEnd(' ', '.').Equals(".ample", StringComparison.OrdinalIgnoreCase) || ProjectPath.IsShortAliasOfPrivate(part)))
        {
            throw new AmpleException("AMP5285", $"The {what} is in a private .ample folder.");
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        if (string.Equals(full.TrimEnd(separators), root.TrimEnd(separators), StringComparison.OrdinalIgnoreCase))
        {
            throw new AmpleException("AMP5285", $"The {what} is the root of a drive: choose a folder.");
        }

        foreach (string system in SystemFolders())
        {
            if (ProjectPath.IsInside(system, full))
            {
                throw new AmpleException("AMP5285", $"The {what} is in a system folder of the operating system.");
            }
        }

        return full;
    }

    private static IEnumerable<string> SystemFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SpecialFolder[] folders =
            [
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            ];
            foreach (Environment.SpecialFolder folder in folders)
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length > 0)
                {
                    yield return Path.GetFullPath(path);
                }
            }

            yield break;
        }

        foreach (string path in new[] { "/etc", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/boot", "/proc", "/sys", "/dev" })
        {
            yield return path;
        }
    }
}
