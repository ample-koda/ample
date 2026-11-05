using System.Text.Json;

namespace Ample.Audio.IO;

/// <summary>Per-user AMPLE folders, settings and ordered installed-pack lookup.</summary>
public static class AmpleHome
{
    private static readonly JsonSerializerOptions SettingsOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Gets the user-level AMPLE folder.</summary>
    public static string DefaultPath() => Environment.GetEnvironmentVariable("AMPLE_HOME") is { Length: > 0 } value ? Path.GetFullPath(value) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");

    /// <summary>
    /// Gets the machine-wide pack folder that a system install (the apt package or the system install script) fills: read-only for users,
    /// searched after the person's own folder, so a person can always override it. <c>/usr/share/ample/packs</c> on Linux and <c>%ProgramData%AMPLEpacks</c> on Windows.
    /// </summary>
    public static string SystemPackFolder() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AMPLE", "packs")
        : "/usr/share/ample/packs";

    /// <summary>Gets the settings file inside a user-level AMPLE folder.</summary>
    public static string SettingsPath(string home) => Path.Combine(Path.GetFullPath(home), "settings.json");

    /// <summary>Reads the user settings, treating a missing settings file as empty settings.</summary>
    public static AmpleSettings ReadSettings(string home)
    {
        string file = SettingsPath(home);
        return !File.Exists(file) ? new AmpleSettings() : JsonSerializer.Deserialize<AmpleSettings>(File.ReadAllText(file), SettingsOptions) ?? new AmpleSettings();
    }

    /// <summary>Writes the user settings atomically.</summary>
    public static void WriteSettings(string home, AmpleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(home);
        Ample.Audio.IO.RawFiles.WriteText(SettingsPath(home), JsonSerializer.Serialize(settings, SettingsOptions) + "\n");
    }

    /// <summary>Lists pack folders in the documented lookup order, without duplicate paths.</summary>
    public static IReadOnlyList<string> PackFolders(string home, string? ampleRoot, string projectRoot)
    {
        List<string> folders = [];
        foreach (string path in SplitEnvironmentFolders(Environment.GetEnvironmentVariable("AMPLE_PACKS"))) Add(folders, path);
        AmpleSettings settings = ReadSettings(home);
        if (!string.IsNullOrWhiteSpace(settings.PacksFolder)) Add(folders, settings.PacksFolder);
        Add(folders, Path.Combine(home, "audio-packs"));
        Add(folders, SystemPackFolder());
        if (!string.IsNullOrWhiteSpace(ampleRoot)) Add(folders, Path.Combine(ampleRoot, "audio-packs"));
        foreach (string root in AncestorRoots(AppContext.BaseDirectory)) Add(folders, Path.Combine(root, "audio-packs"));
        foreach (string root in AncestorRoots(projectRoot)) Add(folders, Path.Combine(root, "audio-packs"));
        return folders;
    }

    /// <summary>Finds a relative file in the configured pack folders and returns every location that was searched.</summary>
    public static PackLookup LookupPackFile(string home, string? ampleRoot, string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Any(part => part == "..")) throw new ArgumentException("The pack path must be relative.", nameof(relativePath));
        IReadOnlyList<string> folders = PackFolders(home, ampleRoot, projectRoot);
        IReadOnlyList<string> searched = [.. folders.Select(folder => Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar)))];
        return new PackLookup(searched.FirstOrDefault(File.Exists), searched);
    }

    private static string[] SplitEnvironmentFolders(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> AncestorRoots(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        for (int depth = 0; current is not null && depth < 12; depth++, current = current.Parent) yield return current.FullName;
    }

    private static void Add(List<string> folders, string path)
    {
        string full = Path.GetFullPath(path);
        if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
    }
}

/// <summary>The user-editable AMPLE settings.</summary>
public sealed record AmpleSettings
{
    /// <summary>Optional folder containing installed packs.</summary>
    public string? PacksFolder { get; init; }

    /// <summary>ACE-Step server settings.</summary>
    public AceSettings? Ace { get; init; }
}

/// <summary>The paths and port used by the local ACE-Step server helper.</summary>
public sealed record AceSettings
{
    /// <summary>Path to ace-server.exe.</summary>
    public string? Exe { get; init; }

    /// <summary>Folder containing ACE-Step models.</summary>
    public string? Models { get; init; }

    /// <summary>Local ACE-Step server port.</summary>
    public int? Port { get; init; }
}

/// <summary>The result of searching installed pack folders.</summary>
public sealed record PackLookup(string? Path, IReadOnlyList<string> Searched);
