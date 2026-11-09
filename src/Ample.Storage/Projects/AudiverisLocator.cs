using System.Text.Json;

namespace Ample.Storage.Projects;

/// <summary>
/// Where the Audiveris that <c>ample packs install audiveris</c> put is: <c>&lt;AMPLE home&gt;/audiveris/installed.json</c> names the program. Used by the score import (optical music recognition),
/// by <c>ample doctor</c> and by the installer's checks, so they all find the same one. The environment variable AMPLE_AUDIVERIS still wins over this.
/// </summary>
public static class AudiverisLocator
{
    /// <summary>The AMPLE user folder as the program finds it: AMPLE_HOME, else <c>ample</c> in the local application data.</summary>
    public static string DefaultHome() => Environment.GetEnvironmentVariable("AMPLE_HOME") is { Length: > 0 } value
        ? Path.GetFullPath(value)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");

    /// <summary>The Audiveris program of the pack in this AMPLE folder, or null when it is not installed (or its file is gone).</summary>
    public static string? FindExe(string? home = null)
    {
        try
        {
            string folder = Path.Combine(home ?? DefaultHome(), "audiveris");
            string marker = Path.Combine(folder, "installed.json");
            if (!File.Exists(marker))
            {
                return null;
            }

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.TryGetProperty("exe", out JsonElement exe) && exe.ValueKind == JsonValueKind.String && exe.GetString() is { Length: > 0 } relative)
            {
                string full = Path.GetFullPath(Path.Combine(folder, relative));
                return full.StartsWith(Path.GetFullPath(folder), StringComparison.Ordinal) && File.Exists(full) ? full : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // a damaged marker counts as not installed
        }

        return null;
    }
}
