using System.Text;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>
/// The demo song that every project starts with: <c>spec/demo/demo.amplet</c> of the AMPLE folder, put in the project folder as <c>demo.amplet</c>.
/// It can be edited always, and deleted or moved once the first-time setup of the project is done (<see cref="DemoSongRules"/>), so a project always has a song to open at its start.
/// </summary>
public static class DemoSong
{
    /// <summary>Gets where the demo lives in an AMPLE folder.</summary>
    public static string SourcePath(string ampleRoot) => Path.Combine(ampleRoot, "spec", "demo", "demo.amplet");

    /// <summary>Reads the demo text (LF line ends), or null when this AMPLE folder has none.</summary>
    public static string? Text(string ampleRoot)
    {
        string path = SourcePath(ampleRoot);
        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
    }

    /// <summary>Puts the demo into a project that was just created, before anything else is in it. Returns false when there is no demo text or the file is already there.</summary>
    public static bool SeedNew(string projectRoot, string ampleRoot)
    {
        string? text = Text(ampleRoot);
        string target = Path.Combine(projectRoot, DemoSongRules.FileName);
        if (text is null || File.Exists(target)) return false;
        File.WriteAllText(target, text, new UTF8Encoding(false));
        return true;
    }

    /// <summary>Whether a project has no song at all (no <c>.amplet</c> file below its folder, the private <c>.ample</c> folder not counted).</summary>
    public static bool HasNoSong(string projectRoot)
    {
        string ample = Path.Combine(projectRoot, ".ample") + Path.DirectorySeparatorChar;
        EnumerationOptions options = new() { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true };
        return !Directory.EnumerateFiles(projectRoot, "*.amplet", options).Any(file => !file.StartsWith(ample, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gives a project that has no song the demo, through the command bus (journaled). Returns whether it was written.</summary>
    internal static async Task<bool> EnsureAsync(ProjectSession session, string ampleRoot, CancellationToken cancellationToken)
    {
        string? text = Text(ampleRoot);
        if (text is null || !HasNoSong(session.Root) || !DemoSongRules.IsProtected(session.Root)) return false;   // only while the first-time setup is pending: a demo that was deleted afterwards does not come back
        await session.Bus.ExecuteAsync(new WriteFileCommand(DemoSongRules.FileName, text), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
