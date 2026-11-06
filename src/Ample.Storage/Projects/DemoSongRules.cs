namespace Ample.Storage.Projects;

/// <summary>
/// The demo song every project starts with (<c>demo.amplet</c> in the project folder). It can be edited always, but it can be neither deleted nor moved away
/// until the first-time setup wizard of the project has been walked through (the first opening of the demo): until then a project always has a song to open.
/// After that it is an ordinary song. The rule lives here, where every way of deleting or moving a file (the studio, the command line, the assistant) goes through the command bus.
/// </summary>
public static class DemoSongRules
{
    /// <summary>The file name of the demo song, in the project folder itself.</summary>
    public const string FileName = "demo.amplet";

    /// <summary>The explanation given when something tries to delete or move it.</summary>
    public const string Refusal = "The demo song stays until you have walked through the first-time setup: edit it as much as you like, and make your own songs next to it.";

    /// <summary>Whether the demo song of the project in a folder is protected: only while the first-time setup wizard of the project is still to be walked through. A project file that cannot be read protects nothing.</summary>
    public static bool IsProtected(string projectRoot)
    {
        try
        {
            string file = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
            return ProjectFile.Parse(File.ReadAllText(file)).Settings.FirstRun.Pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Ample.Core.Abstractions.AmpleException)
        {
            return false;
        }
    }

    /// <summary>Whether a project-relative path is the demo song (only the file in the project folder itself counts, whatever the case or slashes).</summary>
    public static bool IsDemo(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        return string.Equals(normalized, FileName, StringComparison.OrdinalIgnoreCase);
    }
}
