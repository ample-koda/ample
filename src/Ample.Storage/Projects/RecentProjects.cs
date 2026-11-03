using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Storage.Projects;

/// <summary>One project the user opened before.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Name">The name when it was last opened.</param>
/// <param name="Id">The project id.</param>
/// <param name="LastOpened">When.</param>
public sealed record RecentProject(string Path, string Name, string Id, DateTimeOffset LastOpened)
{
    /// <summary>Gets the last port used by this project, when known.</summary>
    public int? LastPort { get; init; }
    /// <summary>Gets the last song opened in this project by the Studio, when known.</summary>
    public string? LastSong { get; init; }
}

/// <summary>The list of recent projects of a user, kept in the library home (<c>recent.json</c>).</summary>
public static class RecentProjects
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string FileOf(string home) => System.IO.Path.Combine(home, "recent.json");

    /// <summary>Lists the recent projects, newest first.</summary>
    public static IReadOnlyList<RecentProject> List(string home)
    {
        try
        {
            return File.Exists(FileOf(home)) ? [.. (JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(FileOf(home)), Options) ?? []).OrderByDescending(r => r.LastOpened)] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Notes that a project was opened.</summary>
    public static void Touch(string home, string path, string name, string id, IClock clock, int? lastPort)
    {
        ArgumentNullException.ThrowIfNull(clock);
        string full = System.IO.Path.GetFullPath(path);
        RecentProject? previous = List(home).FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        List<RecentProject> all = [.. List(home).Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)), new RecentProject(full, name, id, clock.UtcNow) { LastPort = lastPort ?? previous?.LastPort, LastSong = previous?.LastSong }];
        Write(home, all.OrderByDescending(r => r.LastOpened).Take(30));
    }

    /// <summary>Remembers the song selected in the Studio across reloads and server restarts.</summary>
    public static void RememberSong(string home, string path, string name, string id, string song, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        string full = System.IO.Path.GetFullPath(path);
        RecentProject? previous = List(home).FirstOrDefault(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        List<RecentProject> all = [.. List(home).Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)), new RecentProject(full, name, id, clock.UtcNow) { LastPort = previous?.LastPort, LastSong = song }];
        Write(home, all.OrderByDescending(r => r.LastOpened).Take(30));
    }

    /// <summary>Removes a project from the list (the folder is not touched).</summary>
    public static void Forget(string home, string path) => Write(home, List(home).Where(r => !string.Equals(r.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));

    private static void Write(string home, IEnumerable<RecentProject> list)
    {
        Directory.CreateDirectory(home);
        Ample.Audio.IO.RawFiles.WriteText(FileOf(home), JsonSerializer.Serialize(list, Options) + "\n");
    }
}
