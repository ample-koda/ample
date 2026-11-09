using System.CommandLine;
using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>The <c>ample new</c>, <c>open</c>, <c>doctor</c> and <c>project info</c> commands.</summary>
internal static class ProjectCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [New(output), Doctor(output), Project(output)];

    private static Argument<string> FolderArgument() => new("folder") { Description = "The project folder." };

    private static Argument<string> OptionalFolderArgument() => new("folder") { Description = "The project folder (default: the current folder).", Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "." };

    private static Command New(TextWriter output)
    {
        Argument<string> folder = FolderArgument();
        Option<string?> name = new("--name") { Description = "Project name (default: the folder name)." };
        Option<ulong?> seed = new("--seed") { Description = "Master seed (default: derived from the project id)." };
        Option<bool> noFirstRun = new("--no-first-run") { Description = "Mark the first-time setup wizard of the studio as already done (for scripts and tests).", Hidden = true };
        Command command = new("new", "Creates a project folder with the mandatory .ample structure.") { folder, name, seed, noFirstRun };
        command.SetAction(async (parse, token) => await Run(output, async () =>
        {
            string path = Path.GetFullPath(parse.GetValue(folder)!);
            using OpenProject project = await ProjectStore.CreateAsync(Fs, Clock, new GuidV7Generator(), path, parse.GetValue(name) ?? Path.GetFileName(path), parse.GetValue(seed), token);
            output.WriteLine($"Created project '{project.Manifest.Name}' ({project.Manifest.Id}) at {path}");
            if (parse.GetValue(noFirstRun))
            {
                string projectFile = Path.Combine(path, Ample.Storage.Projects.ProjectLayout.AmpleFolder, Ample.Storage.Projects.ProjectLayout.ProjectFile);
                Ample.Storage.Projects.ProjectManifest created = Ample.Storage.Projects.ProjectFile.Parse(await File.ReadAllTextAsync(projectFile, token));
                await File.WriteAllTextAsync(projectFile, Ample.Storage.Projects.ProjectFile.Serialize(created with { Settings = created.Settings with { FirstRun = created.Settings.FirstRun.Completed(Clock.UtcNow) } }), token);
            }
            else
            {
                output.WriteLine("The studio starts a first-time setup wizard when this project is opened (the project settings, then the demo song).");
            }
            // every new project starts with the demo song (the AMPLE folder is found like for check and build; without one there is no demo, and that is not an error)
            try
            {
                if (Ample.Server.DemoSong.SeedNew(path, Ample.Docs.SpecRoot.Find(Directory.GetCurrentDirectory()).Path)) output.WriteLine($"Added the demo song {Ample.Storage.Projects.DemoSongRules.FileName} (edit it as you like; it is not deleted for now).");
            }
            catch (DirectoryNotFoundException)
            {
                // no AMPLE folder with spec/ near the program: no demo
            }

            return 0;
        }));
        return command;
    }

    private static Command Open(TextWriter output)
    {
        Argument<string> folder = FolderArgument();
        Command command = new("open", "Opens a project for writing (takes the instance lock), prints a summary and releases it.") { folder };
        command.SetAction(async (parse, token) => await Run(output, async () =>
        {
            using OpenProject project = await ProjectStore.OpenAsync(Fs, Clock, Path.GetFullPath(parse.GetValue(folder)!), readOnly: false, token);
            Summary(output, project);
            return 0;
        }));
        return command;
    }

    private static Command Doctor(TextWriter output)
    {
        Argument<string> folder = OptionalFolderArgument();
        Option<bool> environment = new("--env") { Description = "Also check what the tools and the assistant need around the program (libraries, ffmpeg, Python, the sound font, disk space); what is missing goes to the project log with how to fix it." };
        Option<bool> assistant = new("--assistant") { Description = "Check whether Codex is usable for Koda (optional; never a failure): installed, signed in, and what that means." };
        Option<string?> sound = new("--sound") { Description = "Build this song (a path in the project) and report the sound of its blocks: silent, very quiet, clipping, not heard, and the loudness of the master, with suggested gains." };
        Option<bool> samplePacks = new("--packs") { Description = "With --json: also list the downloadable sample packs, each installed (with a way to remove it) or not (with a way to download it)." };
        Option<bool> soundJson = new("--json") { Description = "With --sound, or with --env and --assistant: write the findings as JSON (the setup wizard reads them; each fixable finding carries its fix)." };
        Option<bool> soundFix = new("--fix") { Description = "With --sound: put the suggested effect blocks (normalize for a very quiet block, a lower volume for a clipping one) into the song; the journal keeps the old text." };
        Option<string?> soundRoot = new("--root") { Description = "The AMPLE folder with spec/ (default: found from the current folder upwards); used with --sound." };
        Command command = new("doctor", "Checks a project folder without changing it; exit code 1 when there are errors.") { folder, environment, assistant, sound, soundRoot, soundJson, soundFix, samplePacks };
        command.SetAction(async (parse, token) => await Run(output, async () =>
        {
            if (parse.GetValue(sound) is { Length: > 0 } soundSong)
            {
                return await SoundCheck.RunAsync(output, soundSong, Path.GetFullPath(parse.GetValue(folder)!), parse.GetValue(soundRoot), parse.GetValue(soundJson), parse.GetValue(soundFix), token);
            }

            if (parse.GetValue(soundJson) && (parse.GetValue(environment) || parse.GetValue(assistant)))
            {
                output.WriteLine(DoctorJson.Build(Path.GetFullPath(parse.GetValue(folder)!), parse.GetValue(environment), parse.GetValue(assistant), parse.GetValue(samplePacks)));
                return 0;
            }

            if (parse.GetValue(assistant))
            {
                (string level, string text, string fix) = AssistantCheck.Run();
                output.WriteLine($"{level,-7} assistant: {text}");
                if (fix.Length > 0) output.WriteLine($"        how to fix: {fix}");
                (string browserLevel, string browserText, string browserFix) = AssistantCheck.Browser(AmpleHome.DefaultPath());
                output.WriteLine($"{browserLevel,-7} assistant browser: {browserText}");
                if (browserFix.Length > 0) output.WriteLine($"        how to fix: {browserFix}");
                if (!parse.GetValue(environment)) return 0;
            }

            if (parse.GetValue(environment))
            {
                string project = Path.GetFullPath(parse.GetValue(folder)!);
                InstallEnvironment installEnvironment = InstallEnvironmentDetector.Detect();
                IReadOnlyList<EnvironmentFinding> checks = EnvironmentCheck.Run(AppContext.BaseDirectory, project, installEnvironment);
                foreach (EnvironmentFinding c in checks)
                {
                    output.WriteLine($"{(c.Ok ? "ok     " : "MISSING")} {c.Name}: {c.Detail}");
                    if (!c.Ok)
                    {
                        // a tip is a sentence, then the commands of this machine (one per line, to copy and paste)
                        string[] hintLines = c.Hint.Split('\n');
                        output.WriteLine($"        how to fix: {hintLines[0]}");
                        foreach (string command in hintLines.Skip(1))
                        {
                            output.WriteLine($"          {command}");
                        }

                        if (Directory.Exists(Path.Combine(project, ".ample")))
                        {
                            ProjectLog.Append(project, "missing", c.Name, c.Hint);
                        }
                    }
                    else if (Directory.Exists(Path.Combine(project, ".ample")))
                    {
                        ProjectLog.Resolve(project, c.Name);
                    }
                }

                output.WriteLine(checks.All(c => c.Ok) ? "environment: nothing is missing" : $"environment: {checks.Count(c => !c.Ok)} thing(s) missing (also in the project log of the project)");
                if (EnvironmentCheck.InstallCommand(checks, installEnvironment) is { } oneCommand)
                {
                    output.WriteLine("to install what your package manager can supply in one go:");
                    output.WriteLine($"  {oneCommand}");
                }
                if (!Directory.Exists(Path.Combine(project, ".ample")))
                {
                    // not a project: this was a check of the installation only; only the required pieces decide the exit code
                    return checks.Any(c => !c.Ok && c.Name is "program libraries" or "General MIDI sound font") ? 1 : 0;
                }
            }

            IReadOnlyList<DoctorFinding> findings = await ProjectDoctor.CheckAsync(Fs, Clock, Path.GetFullPath(parse.GetValue(folder)!), token);
            foreach (DoctorFinding f in findings)
            {
                output.WriteLine($"{f.Severity} {f.Code} {f.Message}");
                if (f.Repair is not null)
                {
                    output.WriteLine($"  repair: {f.Repair}");
                }
            }

            output.WriteLine(findings.Count == 0 ? "doctor: no problems" : $"doctor: {findings.Count(f => f.Severity == "error")} error(s), {findings.Count(f => f.Severity == "warning")} warning(s), {findings.Count(f => f.Severity == "info")} note(s)");
            return findings.Any(f => f.Severity == "error") ? 1 : 0;
        }));
        return command;
    }

    private static Command Project(TextWriter output)
    {
        Argument<string> folder = FolderArgument();
        Command info = new("info", "Prints a project summary without taking the writer lock.") { folder };
        info.SetAction(async (parse, token) => await Run(output, async () =>
        {
            using OpenProject project = await ProjectStore.OpenAsync(Fs, Clock, Path.GetFullPath(parse.GetValue(folder)!), readOnly: true, token);
            Summary(output, project);
            return 0;
        }));
        return new Command("project", "Project commands.") { info, Open(output) };
    }

    private static void Summary(TextWriter output, OpenProject project)
    {
        output.WriteLine($"Project: {project.Manifest.Name}");
        output.WriteLine($"Id: {project.Manifest.Id}");
        output.WriteLine($"Folder: {project.Root}");
        output.WriteLine($"Created: {project.Manifest.Created:u}");
        output.WriteLine($"Format: {project.Manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)}{(project.Migrated ? " (upgraded; backup in .ample/backups)" : string.Empty)}");
        output.WriteLine($"Master seed: {project.Manifest.MasterSeed.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine(project.IsReadOnly ? "Opened read-only." : "Opened for writing (lock released when this command ends).");
    }

    private static async Task<int> Run(TextWriter output, Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }
}
