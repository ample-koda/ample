using System.Text.Json;

namespace Ample.Engine.Engine;

/// <summary>
/// How many blocks a build may render at the same time. The server has one limit, by default and at most the number of cores of the machine (one block per core);
/// <c>ample serve --parallelism N</c> (or the environment variable <c>AMPLE_PARALLELISM</c>) can lower it, never raise it above the cores. A project may ask for fewer
/// (<c>settings.build.parallelism</c> in project.json) and never for more than the server allows. A caller that asks for a number itself (Lite asks for 2, a test for 1) gets
/// that number or less: never more than the project or the server allows. The number changes how fast a build is, never what it makes.
/// </summary>
public static class BuildParallelism
{
    private static int serverLimit;

    /// <summary>Gets the number of cores of this machine (at least 1).</summary>
    public static int Cores => Math.Max(1, Environment.ProcessorCount);

    /// <summary>Gets what the server allows: the configured number, but between 1 and the cores; the cores when nothing is configured.</summary>
    public static int ServerLimit => Limit(serverLimit);

    /// <summary>The limit that a configured number gives: at least 1 and at most the cores; the cores when nothing is configured.</summary>
    /// <param name="configured">The configured number, or null or 0 for none.</param>
    /// <returns>The limit.</returns>
    public static int Limit(int? configured) => configured is > 0 ? Math.Min(configured.Value, Cores) : Cores;

    /// <summary>Sets the limit of this server (null or 0 for the default: the cores).</summary>
    /// <param name="requested">The wanted limit; more than the cores is cut back to the cores.</param>
    public static void ConfigureServer(int? requested) => serverLimit = requested is > 0 ? requested.Value : 0;

    /// <summary>The number of blocks a build may render at the same time.</summary>
    /// <param name="requested">What the caller asks for (0 or less: no wish, take the project's or the server's number).</param>
    /// <param name="project">What the project asks for, or null.</param>
    /// <returns>At least 1 and at most the limit of the server.</returns>
    public static int Effective(int requested, int? project) => Effective(requested, project, ServerLimit);

    /// <summary>The same for a given limit of the server (no global state).</summary>
    /// <param name="requested">What the caller asks for (0 or less: no wish).</param>
    /// <param name="project">What the project asks for, or null.</param>
    /// <param name="limit">The limit of the server.</param>
    /// <returns>At least 1 and at most the limit.</returns>
    public static int Effective(int requested, int? project, int limit)
    {
        int wanted = requested > 0 && project is > 0 ? Math.Min(requested, project.Value) : requested > 0 ? requested : project is > 0 ? project.Value : limit;
        return Math.Clamp(wanted, 1, limit);
    }

    /// <summary>Reads what a project asks for (<c>settings.build.parallelism</c> of its project.json), or null when it says nothing or the file cannot be read.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The number, or null.</returns>
    public static int? OfProject(string projectRoot)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, Ample.Storage.Projects.ProjectLayout.AmpleFolder, Ample.Storage.Projects.ProjectLayout.ProjectFile)));
            return doc.RootElement.TryGetProperty("settings", out JsonElement settings) && settings.TryGetProperty("build", out JsonElement build) && build.TryGetProperty("parallelism", out JsonElement number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int value) && value > 0 ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
