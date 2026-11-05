namespace Ample.Storage.Projects;

/// <summary>
/// The demo song every project starts with (<c>demo.amplet</c> in the project folder). For now it can be edited but never deleted or moved away:
/// a project always has a song to open, and Koda and the studio always have something to look at. The rule lives here, where every way of deleting
/// or moving a file (the studio, the command line, the assistant) goes through the command bus.
/// </summary>
public static class DemoSongRules
{
    /// <summary>The file name of the demo song, in the project folder itself.</summary>
    public const string FileName = "demo.amplet";

    /// <summary>The explanation given when something tries to delete or move it.</summary>
    public const string Refusal = "The demo song stays for now: edit it as much as you like, and make your own songs next to it.";

    /// <summary>Whether a project-relative path is the demo song (only the file in the project folder itself counts, whatever the case or slashes).</summary>
    public static bool IsDemo(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        return string.Equals(normalized, FileName, StringComparison.OrdinalIgnoreCase);
    }
}
