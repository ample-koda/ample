using System.CommandLine;
using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Credits;
using Ample.Engine.Modules;
using Ample.Storage.Projects;
using Ample.Storage.Quality;
using Ample.Storage.Takes;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

/// <summary>The <c>ample check quality</c> and <c>ample credits</c> commands.</summary>
internal static class QualityCreditsCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Credits(output)];

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static async Task<int> Guard(TextWriter output, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Adds <c>quality</c> as a subcommand of <c>check</c>.</summary>
    internal static void AddQuality(Command check, TextWriter output)
    {
        Option<string?> project = ProjectOption();
        Option<string?> take = new("--take") { Description = "A take id (or the start of one). Default: the newest take of every block that has audio." };
        Command quality = new("quality", "Measures takes (clipping, true peak, loudness, silence, DC offset, sample rate) against the rules of spec/quality-rules.json and the project policy. Advisory: the exit code is 1 only when a rule the policy requires fails.") { project, take };
        quality.SetAction((parse, token) => Guard(output, async () =>
        {
            await Task.CompletedTask;
            _ = token;
            string root = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: true, token);
            Takebank bank = Takebank.Open(Fs, Clock, root);
            QualityService service = new(bank, Clock, root);
            IReadOnlyList<QualityResult> results = parse.GetValue(take) is { } t ? [service.Check(t)] : service.CheckLatest();
            QualityPolicy policy = QualityPolicy.Read(root) ?? QualityPolicy.Default;
            bool requiredFailed = false;
            foreach (QualityResult r in results)
            {
                output.WriteLine($"{r.Block} (take {r.TakeId[..Math.Min(8, r.TakeId.Length)]}): {(r.Error ?? (r.Passed ? "no problems" : "problems found"))}");
                foreach (QualityFinding f in r.Findings)
                {
                    output.WriteLine($"  {f.RuleId} {f.Status,-7} {f.Message}");
                    requiredFailed |= f.Status == "failed" && policy.Require.Values.Any(ids => ids.Contains(f.RuleId));
                }
            }

            if (results.Count == 0)
            {
                output.WriteLine("No takes with audio yet. Build the song first.");
            }

            return requiredFailed ? 1 : 0;
        }));
        check.Subcommands.Add(quality);
    }

    private static Command Credits(TextWriter output)
    {
        Option<string?> project = ProjectOption();
        Option<string> format = new("--format") { Description = "md or json.", DefaultValueFactory = _ => "md" };
        Option<string?> outFile = new("--out") { Description = "Write the sheet to this file instead of the screen." };
        Option<bool> write = new("--write") { Description = "Also keep the sheet in .ample/credits/ so that backups and exports carry it." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Command credits = new("credits", "Writes the credit sheet of the project: what made each block (from the plans of the takes), the modules and library assets in use with their licences, and the people in .ample/credits.json. Advisory, not legal advice.") { project, format, outFile, write, root };
        credits.SetAction((parse, token) => Guard(output, async () =>
        {
            string projectRoot = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
            SpecData spec = (parse.GetValue(root) is { } r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory())).LoadSpecData();
            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, projectRoot, readOnly: true, token);
            using ModuleService modules = ModuleService.WithUserLibrary(projectRoot, Clock);
            List<AmpleDocument> songs = [];
            List<ModuleUsage> used = [];
            foreach (string f in Directory.EnumerateFiles(projectRoot, "*.amplet", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ample" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                ExpandedSong e = modules.Expand(await File.ReadAllTextAsync(f, token), spec);
                songs.Add(Parser.Parse(e.Text, spec));
                used.AddRange(e.Used);
            }

            using Ample.Storage.Library.Library library = Ample.Storage.Library.Library.Open(Ample.Storage.Library.Library.DefaultHome(), Clock);
            CreditSheet sheet = CreditsService.Build(projectRoot, open.Manifest.Name, Takebank.Open(Fs, Clock, projectRoot), library, songs, used, Clock);
            if (parse.GetValue(write))
            {
                CreditsService.WriteSheet(projectRoot, sheet);
            }

            string text = string.Equals(parse.GetValue(format), "json", StringComparison.OrdinalIgnoreCase) ? CreditsService.ToJson(sheet) : CreditsService.ToMarkdown(sheet);
            if (parse.GetValue(outFile) is { } path)
            {
                await File.WriteAllTextAsync(path, text, token);
                output.WriteLine($"Wrote {path} ({sheet.Blocks.Count.ToString(CultureInfo.InvariantCulture)} block(s), {sheet.Assets.Count.ToString(CultureInfo.InvariantCulture)} asset(s), {sheet.Warnings.Count.ToString(CultureInfo.InvariantCulture)} warning(s)).");
            }
            else
            {
                output.Write(text);
            }

            return 0;
        }));
        return credits;
    }
}
