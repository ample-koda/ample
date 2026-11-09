using System.Diagnostics;
using System.Text.Json;

namespace Ample.Storage.Projects;

/// <summary>The result of one environment check.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Ok">Whether it is fine.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Hint">How to put it right (when it is not fine).</param>
public sealed record EnvironmentFinding(string Name, bool Ok, string Detail, string Hint);

/// <summary>The kind of machine, so that a tip names the command that belongs to it.</summary>
public enum InstallEnvironment
{
    /// <summary>Windows (winget).</summary>
    Windows,

    /// <summary>Debian, Ubuntu and what is built on them, WSL included (apt).</summary>
    Debian,

    /// <summary>macOS (Homebrew).</summary>
    MacOs,

    /// <summary>Another Linux.</summary>
    OtherLinux,
}

/// <summary>Finds the <see cref="InstallEnvironment"/> of this machine.</summary>
public static class InstallEnvironmentDetector
{
    /// <summary>Detects the environment; <paramref name="osRelease"/> is the text of /etc/os-release (tests pass their own).</summary>
    public static InstallEnvironment Detect(string? osRelease = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return InstallEnvironment.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return InstallEnvironment.MacOs;
        }

        return IsDebianFamily(osRelease ?? ReadOsRelease()) ? InstallEnvironment.Debian : InstallEnvironment.OtherLinux;
    }

    /// <summary>True when the ID or ID_LIKE of an os-release text names debian or ubuntu.</summary>
    public static bool IsDebianFamily(string osRelease)
    {
        foreach (string line in osRelease.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("ID=", StringComparison.Ordinal) || trimmed.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            {
                string[] words = trimmed[(trimmed.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Any(w => w is "debian" or "ubuntu"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ReadOsRelease()
    {
        try
        {
            return File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Checks what the assistant and the tools of a project need around the program: every library of the build is present, ffmpeg for mp3, a modern Python for the
/// voice worker, the General MIDI sound font and some free disk space. Used by <c>ample doctor --env</c>, which writes what is missing to the project log.
/// </summary>
public static class EnvironmentCheck
{
    /// <summary>Runs the checks.</summary>
    /// <param name="programFolder">The folder of the running program (for the library check).</param>
    /// <param name="projectRoot">The project (for the disk check), or null.</param>
    /// <param name="environment">The kind of machine, for the tips; detected when null.</param>
    public static IReadOnlyList<EnvironmentFinding> Run(string programFolder, string? projectRoot, InstallEnvironment? environment = null)
    {
        InstallEnvironment env = environment ?? InstallEnvironmentDetector.Detect();
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
        list.Add(Probe("ffmpeg (mp3 export)", "ffmpeg", "-version", FfmpegHint(env)));

        // 2b. Audiveris (optional: PDF/image score import via optical music recognition)
        list.Add(Probe("Audiveris (score PDF/image import)", Environment.GetEnvironmentVariable("AMPLE_AUDIVERIS") is { Length: > 0 } configured ? configured : AudiverisLocator.FindExe() ?? "Audiveris", "-version", "Audiveris (audiveris.org, the reader of sheet music in a PDF or a picture) is not installed. It is only needed for ample_import_score on a PDF or image: run `ample packs install audiveris` (the setup wizard and the installer offer it), or install it yourself and set AMPLE_AUDIVERIS to its executable."));

        // 2c. MuseScore (optional: MuseScore/Guitar Pro/Capella/Overture score import)
        list.Add(Probe("MuseScore (score file conversion)", Environment.GetEnvironmentVariable("AMPLE_MUSESCORE") is { Length: > 0 } configuredMuseScore ? configuredMuseScore : "MuseScore4", "-v", "MuseScore (musescore.org) is not found on the PATH. This is only needed for ample_import_score on a .mscz, Guitar Pro, Capella or Overture file: install MuseScore and set AMPLE_MUSESCORE to its executable, or put it on the PATH."));

        // 3. python for the voice worker
        // Windows calls it python; Linux and macOS call it python3 (a plain "python" is often not there at all)
        string pythonHint = PythonHint(env);
        EnvironmentFinding python = Probe("python (voice worker)", OperatingSystem.IsWindows() ? "python" : "python3", "--version", pythonHint);
        if (!python.Ok && !OperatingSystem.IsWindows())
        {
            EnvironmentFinding plain = Probe("python (voice worker)", "python", "--version", pythonHint);
            python = plain.Ok ? plain : python;
        }

        if (python.Ok && python.Detail.StartsWith("Python 2", StringComparison.OrdinalIgnoreCase))
        {
            python = new EnvironmentFinding(python.Name, false, python.Detail, "The python on the PATH is version 2. Install Python 3.10 or newer, or set the interpreter of the engine in the plugin settings.\n" + PythonCommands(env));
        }

        list.Add(python);

        // 4. the General MIDI sound font
        string[] roots = [Path.Combine(programFolder, "audio-packs"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "audio-packs"), Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty, "ample", "audio-packs")];
        bool font = Ample.Audio.IO.AmpleHome.LookupPackFile(Ample.Audio.IO.AmpleHome.DefaultPath(), null, projectRoot ?? Directory.GetCurrentDirectory(), "licensed/musescore-general/MuseScore_General.sf2").Path is not null
            || roots.Any(r => Directory.Exists(Path.Combine(r, "licensed", "musescore-general")) || Directory.Exists(Path.Combine(r, "free", "musescore-general")));
        list.Add(font
            ? new EnvironmentFinding("General MIDI sound font", true, "pack musescore-general found", string.Empty)
            : new EnvironmentFinding("General MIDI sound font", false, "pack musescore-general was not found", "gm blocks need it. Fetch it with: ample packs install soundfont   (or set AMPLE_PACKS to the folder that holds it)."));

        // 5. free disk space where the project lives
        if (projectRoot is not null && Directory.Exists(projectRoot))
        {
            try
            {
                DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(projectRoot))!);
                double gb = drive.AvailableFreeSpace / 1073741824.0;
                list.Add(DiskFinding(gb, drive.Name, HostDriveFreeGb(env)));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // unknown drive
            }
        }

        return list;
    }

    /// <summary>The hint for a missing ffmpeg: a sentence, then the commands of this machine, one per line.</summary>
    public static string FfmpegHint(InstallEnvironment env) => "ffmpeg is not found on the PATH, so mp3 export does not work.\n" + env switch
    {
        InstallEnvironment.Windows => "winget install Gyan.FFmpeg\nthen open a new terminal",
        InstallEnvironment.Debian => "sudo apt install ffmpeg\nor: sudo snap install ffmpeg",
        InstallEnvironment.MacOs => "brew install ffmpeg",
        _ => "install ffmpeg with the package manager of your system (for example: sudo dnf install ffmpeg, or sudo pacman -S ffmpeg)",
    };

    /// <summary>The hint for a missing Python.</summary>
    public static string PythonHint(InstallEnvironment env) => "Python 3.10 or newer is not found on the PATH. The voice clone and AI voice blocks need it.\n" + PythonCommands(env);

    private static string PythonCommands(InstallEnvironment env) => env switch
    {
        InstallEnvironment.Windows => "winget install Python.Python.3.12\nthen open a new terminal",
        InstallEnvironment.Debian => "sudo apt install python3 python3-venv",
        InstallEnvironment.MacOs => "brew install python",
        _ => "install Python 3.10 or newer with the package manager of your system",
    };

    /// <summary>
    /// One command that installs everything that is missing and can be installed by the package manager of this machine, or null.
    /// </summary>
    public static string? InstallCommand(IReadOnlyList<EnvironmentFinding> findings, InstallEnvironment env)
    {
        bool ffmpeg = findings.Any(f => !f.Ok && f.Name.StartsWith("ffmpeg", StringComparison.Ordinal));
        bool python = findings.Any(f => !f.Ok && f.Name.StartsWith("python", StringComparison.Ordinal));
        List<string> packages = [];
        switch (env)
        {
            case InstallEnvironment.Debian:
                if (ffmpeg) packages.Add("ffmpeg");
                if (python) packages.AddRange(["python3", "python3-venv"]);
                return packages.Count == 0 ? null : "sudo apt install " + string.Join(' ', packages);
            case InstallEnvironment.MacOs:
                if (ffmpeg) packages.Add("ffmpeg");
                if (python) packages.Add("python");
                return packages.Count == 0 ? null : "brew install " + string.Join(' ', packages);
            default:
                return null;
        }
    }

    /// <summary>
    /// The disk check. Inside WSL the root is a virtual disk that is huge on paper (about 1 TB) but grows on a Windows drive, so what is really free is the smaller of
    /// the two numbers: <paramref name="hostGb"/> is the free space of that Windows drive (null when it is not known or this is not WSL).
    /// </summary>
    public static EnvironmentFinding DiskFinding(double rootGb, string rootName, double? hostGb)
    {
        double real = hostGb is { } host ? Math.Min(rootGb, host) : rootGb;
        // the numbers are written the same way on every machine (no decimal comma)
        string detail = hostGb is { } h
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{real:0.0} GB really free: {rootGb:0.0} GB free on {rootName} (the virtual disk of WSL), but only {h:0.0} GB on the Windows drive C: that it grows on")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rootGb:0.0} GB free on {rootName}");
        string hint = real >= 5
            ? string.Empty
            : hostGb is not null
                ? "Under 5 GB free on the Windows drive that holds WSL: free up space on C:, or move the takebank to another drive (ample storage move)."
                : "Under 5 GB free: open Projects, Storage to give space back, or move the takebank to another drive (ample storage move).";
        return new EnvironmentFinding("free disk space", real >= 5, detail, hint);
    }

    // the free space of the Windows drive C: as seen from WSL (/mnt/c), or null when this is not WSL or it cannot be read
    private static double? HostDriveFreeGb(InstallEnvironment env)
    {
        if (OperatingSystem.IsWindows() || (env is not InstallEnvironment.Debian && env is not InstallEnvironment.OtherLinux))
        {
            return null;
        }

        bool wsl = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 };
        if (!wsl)
        {
            try
            {
                wsl = File.Exists("/proc/version") && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return null;
            }
        }

        if (!wsl || !Directory.Exists("/mnt/c"))
        {
            return null;
        }

        try
        {
            return new DriveInfo("/mnt/c").AvailableFreeSpace / 1073741824.0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
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
