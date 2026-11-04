using System.CommandLine;
using System.Text.Json;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

/// <summary>The <c>ample check</c>, <c>ample lint</c> and <c>ample fmt</c> commands.</summary>
internal static class CheckCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Check(output), Lint(output), Fmt(output), Migrate(output), FollowSuggest(output)];

    private static Command Check(TextWriter output)
    {
        (Argument<string> file, Option<string> format, Option<string?> root) = Common();
        Command command = new("check", "Validates a song file; exit code 1 when there are errors.") { file, format, root };
        command.SetAction(parse => Report(output, parse.GetValue(file)!, parse.GetValue(root), parse.GetValue(format)!, onlyLint: false));
        QualityCreditsCommands.AddQuality(command, output);
        return command;
    }

    private static Command Lint(TextWriter output)
    {
        (Argument<string> file, Option<string> format, Option<string?> root) = Common();
        Command command = new("lint", "Shows only the lint rules (L001 and up) for a song file; exit code 1 when a rule is set to error.") { file, format, root };
        command.SetAction(parse => Report(output, parse.GetValue(file)!, parse.GetValue(root), parse.GetValue(format)!, onlyLint: true));
        return command;
    }

    private static Command Fmt(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "The .amplet file." };
        Option<bool> check = new("--check") { Description = "Do not write; exit code 1 when the file is not formatted." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command command = new("fmt", "Rewrites a song file into the canonical form.") { file, check, root };
        command.SetAction(parse =>
        {
            string path = parse.GetValue(file)!;
            if (!File.Exists(path))
            {
                output.WriteLine($"File not found: {path}");
                return 2;
            }

            SpecData spec = Load(parse.GetValue(root));
            string text = File.ReadAllText(path);
            string formatted = Formatter.Format(text, spec);
            bool same = string.Equals(text.Replace("\r\n", "\n", StringComparison.Ordinal), formatted, StringComparison.Ordinal);
            if (parse.GetValue(check))
            {
                output.WriteLine(same ? $"{path}: formatted" : $"{path}: needs formatting");
                return same ? 0 : 1;
            }

            if (!same)
            {
                File.WriteAllText(path, formatted);
            }

            output.WriteLine(same ? $"{path}: already formatted" : $"{path}: formatted");
            return 0;
        });
        return command;
    }

    private static Command FollowSuggest(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "A .wav file (a recording with passing cars, a drum loop, ...)." };
        Command command = new("follow-suggest", "Looks at a recording and suggests the settings of a follow block (band, baseline, attack, release); write them in the block to keep them.") { file };
        command.SetAction(parse =>
        {
            string path = parse.GetValue(file)!;
            if (!File.Exists(path))
            {
                output.WriteLine($"File not found: {path}");
                return 2;
            }

            Ample.Audio.Engine.FollowSuggestion s = Ample.Audio.Engine.FollowAnalysis.SuggestFromWav(File.ReadAllBytes(path));
            output.WriteLine($"{path}: the loud moments stand out most in the {s.Band} band ({s.Prominence / 10}% above the background).");
            output.Write(s.ToBlockLines());
            return 0;
        });
        return command;
    }

    private static Command Migrate(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "The .amplet file." };
        Option<bool> write = new("--write") { Description = "Write the migrated song (without it the command only shows what would change)." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command command = new("migrate", "Moves a song to the current AMPLE version: shows the steps and the changed lines first, writes only with --write.") { file, write, root };
        command.SetAction(parse =>
        {
            string path = parse.GetValue(file)!;
            if (!File.Exists(path))
            {
                output.WriteLine($"File not found: {path}");
                return 2;
            }

            // the components of the song are known, so the old port names of a component instance (in1, mix) are renamed too
            SpecData migrateSpec = Load(parse.GetValue(root));
            string migrateText = File.ReadAllText(path);
            using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(ProjectFolderOf(path), new Ample.Audio.IO.SystemClock());
            (IReadOnlyList<ComponentDefinition> imported, _) = modules.ComponentsForEditors(migrateText.Replace("\r\n", "\n", StringComparison.Ordinal), migrateSpec);
            SongMigrationPlan plan = AmpleMigration.Plan(migrateText, migrateSpec, null, imported);
            output.WriteLine($"{path}: written for AMPLE {plan.From}, current is AMPLE {plan.To}.");
            foreach (UpgradeStep step in plan.Steps) output.WriteLine($"  step {step.From} -> {step.From + 1}: {step.Title} (blocks: {string.Join(", ", step.TouchedTypes.Order())})");
            IReadOnlySet<string> stale = AmpleVersions.StaleTypes(Load(parse.GetValue(root)), plan.From);
            if (stale.Count > 0) output.WriteLine($"  blocks that changed since AMPLE {plan.From}: {string.Join(", ", stale.Order())}");
            if (!plan.Changed)
            {
                output.WriteLine("Nothing to change.");
                return 0;
            }

            foreach (LineChange change in plan.Changes)
            {
                if (change.Before.Length > 0) output.WriteLine($"  {change.Line,4} - {change.Before}");
                if (change.After.Length > 0) output.WriteLine($"  {change.Line,4} + {change.After}");
            }

            if (!parse.GetValue(write))
            {
                output.WriteLine("Not written: run again with --write to apply.");
                return 0;
            }

            File.WriteAllText(path, plan.Text);
            output.WriteLine("Written.");
            return 0;
        });
        return command;
    }

    private static (Argument<string>, Option<string>, Option<string?>) Common() =>
    (
        new Argument<string>("file") { Description = "The .amplet file." },
        new Option<string>("--format") { Description = "Output format: text or json.", DefaultValueFactory = _ => "text" },
        new Option<string?>("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." }
    );

    /// <summary>The project folder of a song: the folder above it that has .ample, or the folder of the song (project files such as a note bake are relative to it).</summary>
    private static string ProjectFolderOf(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        for (string? up = folder; up is not null; up = Path.GetDirectoryName(up))
        {
            if (Directory.Exists(Path.Combine(up, ".ample"))) return up;
        }

        return folder;
    }

    private static SpecData Load(string? root) => (root is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(root)).LoadSpecData();

    private static int Report(TextWriter output, string path, string? root, string format, bool onlyLint)
    {
        if (!File.Exists(path))
        {
            output.WriteLine($"File not found: {path}");
            return 2;
        }

        string folder = ProjectFolderOf(path);
        AmpleDocument doc;
        if (Ample.Language.ComponentRegistry.IsComponentFile(path))
        {
            // a file of components has no project line; its imports are found from the project folder, like the editor does
            doc = Parser.Parse(File.ReadAllText(path), Load(root), new ParseOptions { RequireProjectLine = false, ReadFile = target => { string full = Path.GetFullPath(Path.Combine(folder, target)); return File.Exists(full) ? File.ReadAllText(full) : null; } });
        }
        else
        {
            using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(folder, new Ample.Audio.IO.SystemClock());
            doc = modules.ParseExpanded(File.ReadAllText(path), Load(root));
        }

        List<Diagnostic> list = [.. doc.Diagnostics.Where(d => !onlyLint || d.Code.StartsWith('L'))];
        if (string.Equals(format, "json", StringComparison.Ordinal))
        {
            output.WriteLine(JsonSerializer.Serialize(list.Select(d => new { d.Code, Severity = d.Severity.ToString().ToLowerInvariant(), d.Line, d.Col, d.EndCol, d.Message, d.Hint }), JsonOptions));
        }
        else
        {
            foreach (Diagnostic d in list)
            {
                output.WriteLine($"{path}:{d.Line}:{d.Col}: {d.Severity.ToString().ToLowerInvariant()} {d.Code} {d.Message}");
                if (d.Hint.Length > 0)
                {
                    output.WriteLine($"  hint: {d.Hint}");
                }
            }

            output.WriteLine(list.Count == 0 ? $"{path}: no problems" : $"{path}: {list.Count(d => d.Severity == Severity.Error)} error(s), {list.Count(d => d.Severity != Severity.Error)} other");
        }

        return list.Any(d => d.Severity == Severity.Error) ? 1 : 0;
    }
}
