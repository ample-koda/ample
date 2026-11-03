using System.Diagnostics;
using System.Text.Json;

namespace Ample.Storage.Projects;

/// <summary>The result of one environment check.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">Whether it is fine.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Hint">How to put it right (when it is not fine).</param>
public sealed record EnvironmentFinding(string Name, bool Ok, string Detail, string Hint);

/// <summary>
/// Checks what the assistant and the tools of a project need around the program: every library of the build is present, ffmpeg for mp3, a modern Python for the
/// voice worker, the General MIDI sound font and some free disk space. Used by <c>ample doctor --env</c>, which writes what is missing to the project log.
/// </summary>
public static class EnvironmentCheck
{
    /// <summary>Runs the checks.</summary>
    /// <param name="programFolder">The folder of the running program (for the library check).</param>
    /// <param name="projectRoot">The project (for the disk check), or null.</param>
    public static IReadOnlyList<EnvironmentFinding> Run(string programFolder, string? projectRoot)
    {
        List<EnvironmentFinding> list = [];

        // 1. every runtime library the build lists is next to the program (a copy that missed a few files fails only when a tool needs them)
        List<string> missing = [];
        foreach (string deps in Directory.EnumerateFiles(programFolder, "*.deps.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(deps));
                if (!doc.RootElement.TryGetProperty("targets", out JsonElement targets))
                {
                    continue;
                }

                foreach (JsonProperty target in targets.EnumerateObject())
                {
                    foreach (JsonProperty library in target.Value.EnumerateObject())
                    {
                        if (library.Value.TryGetProperty("runtime", out JsonElement runtime))
                        {
                            foreach (JsonProperty asset in runtime.EnumerateObject())
                            {
                                string file = Path.GetFileName(asset.Name);
                                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(programFolder, file)))
                                {
                                    missing.Add(file);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // an unreadable manifest is not a finding
            }
        }

        missing = [.. missing.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        list.Add(missing.Count == 0
            ? new EnvironmentFinding("program libraries", true, "every library of the build is next to the program", string.Empty)
            : new EnvironmentFinding("program libraries", false, $"{missing.Count} file(s) missing: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : string.Empty)}", "Copy the whole build folder (all files of bin/Debug/net9.0), not a part of it, or reinstall AMPLE. Without them reading PDF text and other tools fail only when they are used."));

        // 2. ffmpeg (mp3 export and the play of other formats)
        list.Add(Probe("ffmpeg (mp3 export)", "ffmpeg", "-version", "ffmpeg is not found on the PATH. Install ffmpeg (for example winget install Gyan.FFmpeg) so that mp3 export works."));

        // 2b. Audiveris (optional: PDF/image score import via optical music recognition)
        list.Add(Probe("Audiveris (score PDF/image import)", Environment.GetEnvironmentVariable("AMPLE_AUDIVERIS") is { Length: > 0 } configured ? configured : "Audiveris", "-version", "Audiveris (audiveris.org) is not found on the PATH. This is only needed for ample_import_score on a PDF or image (a .mid or .musicxml score needs it): install Audiveris and set AMPLE_AUDIVERIS to its executable, or put it on the PATH."));

        // 2c. MuseScore (optional: MuseScore/Guitar Pro/Capella/Overture score import)
        list.Add(Probe("MuseScore (score file conversion)", Environment.GetEnvironmentVariable("AMPLE_MUSESCORE") is { Length: > 0 } configuredMuseScore ? configuredMuseScore : "MuseScore4", "-v", "MuseScore (musescore.org) is not found on the PATH. This is only needed for ample_import_score on a .mscz, Guitar Pro, Capella or Overture file: install MuseScore and set AMPLE_MUSESCORE to its executable, or put it on the PATH."));

        // 3. python for the voice worker
        EnvironmentFinding python = Probe("python (voice worker)", "python", "--version", "Python is not found on the PATH. The voice clone and AI voice blocks need Python 3.10 or newer: install it and open a new terminal.");
        if (python.Ok && python.Detail.StartsWith("Python 2", StringComparison.OrdinalIgnoreCase))
        {
            python = new EnvironmentFinding(python.Name, false, python.Detail, "The python on the PATH is version 2. Install Python 3.10 or newer, or set the interpreter of the engine in the plugin settings.");
        }

        list.Add(python);

        // 4. the General MIDI sound font
        string[] roots = [Path.Combine(programFolder, "audio-packs"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "audio-packs"), Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty, "ample", "audio-packs")];
        bool font = roots.Any(r => Directory.Exists(Path.Combine(r, "licensed", "musescore-general")) || Directory.Exists(Path.Combine(r, "free", "musescore-general")));
        list.Add(font
            ? new EnvironmentFinding("General MIDI sound font", true, "pack musescore-general found", string.Empty)
            : new EnvironmentFinding("General MIDI sound font", false, "pack musescore-general was not found", "gm blocks need it. Fetch it with the pack tools (ample pack fetch musescore-general) or set AMPLE_PACKS to the folder that holds it."));

        // 5. free disk space where the project lives
        if (projectRoot is not null && Directory.Exists(projectRoot))
        {
            try
            {
                DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(projectRoot))!);
                double gb = drive.AvailableFreeSpace / 1073741824.0;
                list.Add(new EnvironmentFinding("free disk space", gb >= 5, $"{gb:0.0} GB free on {drive.Name}", gb >= 5 ? string.Empty : "Under 5 GB free: open Projects, Storage to give space back, or move the takebank to another drive (ample storage move)."));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // unknown drive
            }
        }

        return list;
    }

    private static EnvironmentFinding Probe(string name, string program, string argument, string hint)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(program, argument) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null)
            {
                return new EnvironmentFinding(name, false, "could not start", hint);
            }

            string text = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Split('\n')[0].Trim();
            if (!p.WaitForExit(8000))
            {
                p.Kill();
                return new EnvironmentFinding(name, false, "did not answer", hint);
            }

            return new EnvironmentFinding(name, true, text.Length > 80 ? text[..80] : text, string.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new EnvironmentFinding(name, false, "not found", hint);
        }
    }
}
