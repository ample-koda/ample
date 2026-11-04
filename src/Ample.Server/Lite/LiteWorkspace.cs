using Ample.Audio.IO;

namespace Ample.Server.Lite;

/// <summary>
/// The private folder of one Lite build: made under the Lite work root (inside the library home, never in a project), filled with the files the page sent, deleted again when the
/// build is over, however it ended. Every path goes through <c>ProjectPath.Resolve</c> first.
/// </summary>
public sealed class LiteWorkspace : IDisposable
{
    private LiteWorkspace(string root) => Root = root;

    /// <summary>Gets the folder of the workspace.</summary>
    public string Root { get; }

    /// <summary>The folder that holds all workspaces.</summary>
    /// <param name="libraryHome">The AMPLE library home.</param>
    /// <returns>The work root.</returns>
    public static string WorkRoot(string libraryHome) => Path.GetFullPath(Path.Combine(libraryHome, "lite-work"));   // a full path: a home written with forward slashes must not make the containment check fail

    /// <summary>Makes a workspace and writes the files into it.</summary>
    /// <param name="libraryHome">The AMPLE library home.</param>
    /// <param name="files">The files, already checked by <c>LitePolicy.CheckFiles</c>.</param>
    /// <returns>The workspace.</returns>
    public static LiteWorkspace Create(string libraryHome, IReadOnlyList<LiteFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        string work = WorkRoot(libraryHome);
        Directory.CreateDirectory(work);
        string root = Path.Combine(work, Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        LiteWorkspace workspace = new(root);
        try
        {
            foreach (LiteFile file in files)
            {
                string full = ProjectPath.Resolve(root, file.Path);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new LiteRejectedException("AMPL909", 400, "A path leaves the project.");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, file.Data);
            }
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        return workspace;
    }

    /// <summary>Deletes the workspaces that were left behind (a crash, a killed server) and are older than a given age.</summary>
    /// <param name="libraryHome">The AMPLE library home.</param>
    /// <param name="olderThan">The age.</param>
    /// <returns>How many were deleted.</returns>
    public static int Sweep(string libraryHome, TimeSpan olderThan)
    {
        string work = WorkRoot(libraryHome);
        if (!Directory.Exists(work)) return 0;
        int deleted = 0;
        foreach (string folder in Directory.EnumerateDirectories(work))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < olderThan) continue;
            if (Delete(folder)) deleted++;
        }

        return deleted;
    }

    /// <summary>Deletes the workspace folder (it may take a few tries when a file is still closing).</summary>
    public void Dispose() => Delete(Root);

    private static bool Delete(string folder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(60 * (attempt + 1));
            }
        }

        return !Directory.Exists(folder);
    }
}
