using System.Text.Json;
using System.Text.Json.Serialization;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>
/// <c>ample doctor --env --assistant --json</c>: what the environment check and the assistant check found, as JSON, for the setup wizard. Every finding that
/// has a fix AMPLE can do for the person carries it: <c>pack</c> (ample fetches and checks it itself, no administrator rights) or <c>winget</c>
/// (a package for the current user; <c>admin</c> says whether it needs administrator permission).
/// </summary>
internal static class DoctorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>One finding.</summary>
    internal sealed record Item(string Level, string Name, string Detail, string Hint, Fix? Fix);

    /// <summary>A fix the setup wizard can run.</summary>
    internal sealed record Fix(string Kind, string Id, string Title, int SizeMb, bool Admin, string? Scope = null);

    /// <summary>Builds the findings as JSON.</summary>
    internal static string Build(string project, bool environment, bool assistant)
    {
        List<Item> items = [];
        if (assistant)
        {
            (string level, string text, string fix) = AssistantCheck.Run();
            items.Add(new Item(Level(level), "assistant (Codex)", text, fix, null));
            (string browserLevel, string browserText, string browserFix) = AssistantCheck.Browser(AmpleHome.DefaultPath());
            items.Add(new Item(Level(browserLevel), "assistant browser (koda-browser)", browserText, browserFix, browserLevel == "ok" ? null : new Fix("pack", "koda-browser", "Download koda-browser (the browser driver and Chromium, a few hundred MB)", 400, false)));
        }

        if (environment)
        {
            InstallEnvironment install = InstallEnvironmentDetector.Detect();
            foreach (EnvironmentFinding c in EnvironmentCheck.Run(AppContext.BaseDirectory, project, install))
            {
                items.Add(new Item(c.Ok ? "ok" : "missing", c.Name, c.Detail, c.Hint.Split('\n')[0], c.Ok ? null : FixFor(c.Name, install)));
            }
        }

        return JsonSerializer.Serialize(items, Options);
    }

    private static string Level(string level) => level is "ok" or "note" ? level : "missing";

    private static Fix? FixFor(string name, InstallEnvironment install)
    {
        if (name.StartsWith("General MIDI sound font", StringComparison.Ordinal)) return new Fix("pack", "soundfont", "Download the General MIDI SoundFont (about 205 MB, checked against a fixed SHA-256)", 205, false);
        if (install != InstallEnvironment.Windows) return null;
        if (name.StartsWith("ffmpeg", StringComparison.Ordinal)) return new Fix("winget", "Gyan.FFmpeg", "Install FFmpeg for your user with winget (mp3 export)", 100, false, "user");
        if (name.StartsWith("python", StringComparison.Ordinal)) return new Fix("winget", "Python.Python.3.12", "Install Python 3.12 for your user with winget (the voice worker)", 100, false, "user");
        return null;
    }
}
