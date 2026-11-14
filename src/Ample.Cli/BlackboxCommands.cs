using System.CommandLine;
using System.Globalization;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>The <c>ample blackbox</c> and <c>ample recover</c> commands.</summary>
internal static class BlackboxCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<int> count = new("-n") { Description = "How many records.", DefaultValueFactory = _ => 20 };
        Argument<long> seq = new("seq") { Description = "A record number." };
        Argument<string> file = new("file") { Description = "Output file." };

        Command tail = new("tail", "Shows the last records of the black box.") { project, count };
        tail.SetAction(async (p, t) => await Run(output, p.GetValue(project), false, async (open, journal, bus) =>
        {
            IReadOnlyList<JournalRecord> all = await journal.ReadAllAsync(t);
            foreach (JournalRecord r in all.TakeLast(p.GetValue(count)))
            {
                output.WriteLine($"{r.Seq,6}  {r.At:u}  {r.Actor,-6} {r.Phase,-9} {r.Kind}  {r.PayloadJson}{(r.Error is null ? string.Empty : "  ERROR " + r.Error)}");
            }

            return 0;
        }));

        Command show = new("show", "Shows one record in full, including its pre-image.") { seq, project };
        show.SetAction(async (p, t) => await Run(output, p.GetValue(project), false, async (open, journal, bus) =>
        {
            JournalRecord? r = (await journal.ReadAllAsync(t)).FirstOrDefault(x => x.Seq == p.GetValue(seq));
            if (r is null)
            {
                output.WriteLine($"AMP5231: there is no record {p.GetValue(seq).ToString(CultureInfo.InvariantCulture)}.");
                return 1;
            }

            output.WriteLine($"{r.Seq} {r.At:u} {r.Actor} {r.Phase} {r.Kind} correlation {r.Correlation}\npayload: {r.PayloadJson}\npre-image: {r.PreImageJson ?? "none"}\nerror: {r.Error ?? "none"}");
            return 0;
        }));

        Command verify = new("verify", "Checks the checksums and the hash chain; exit code 1 when a record was changed or damaged.") { project };
        verify.SetAction(async (p, t) => await Run(output, p.GetValue(project), false, async (open, journal, bus) =>
        {
            VerifyReport report = await journal.VerifyAsync(t);
            output.WriteLine(report.BadSeq is null ? $"blackbox: {report.Records.ToString(CultureInfo.InvariantCulture)} record(s) ok{(report.TornTail ? " (the last line was torn by a crash and is ignored)" : string.Empty)}" : $"blackbox: record {report.BadSeq} is damaged or changed ({report.Records.ToString(CultureInfo.InvariantCulture)} good before it)");
            return report.BadSeq is null ? 0 : 1;
        }));

        Command export = new("export", "Writes all records to a file as text.") { file, project };
        export.SetAction(async (p, t) => await Run(output, p.GetValue(project), false, async (open, journal, bus) =>
        {
            IReadOnlyList<JournalRecord> all = await journal.ReadAllAsync(t);
            await File.WriteAllLinesAsync(p.GetValue(file)!, all.Select(r => $"{r.Seq}\t{r.At:O}\t{r.Actor}\t{r.Phase}\t{r.Kind}\t{r.PayloadJson}\t{r.PreImageJson}\t{r.Error}"), t);
            output.WriteLine($"Exported {all.Count.ToString(CultureInfo.InvariantCulture)} record(s) to {p.GetValue(file)}");
            return 0;
        }));

        Command restore = new("restore", "Restores a file from the pre-image of a record, as a new command (the history stays).") { seq, project };
        restore.SetAction(async (p, t) => await Run(output, p.GetValue(project), true, async (open, journal, bus) =>
        {
            JournalRecord? r = (await journal.ReadAllAsync(t)).FirstOrDefault(x => x.Seq == p.GetValue(seq) && x.Phase == "start");
            if (r is null)
            {
                output.WriteLine($"AMP5231: there is no start record {p.GetValue(seq).ToString(CultureInfo.InvariantCulture)}.");
                return 1;
            }

            await bus!.ExecuteAsync(new RestorePreImageCommand(r), t);
            output.WriteLine($"Restored from record {r.Seq.ToString(CultureInfo.InvariantCulture)}.");
            return 0;
        }));

        Command compact = new("compact", "Explicitly thins old AMPLE Redux records to hourly summaries and applies the size budget.") { project };
        compact.SetAction(async (p, t) => await Run(output, p.GetValue(project), true, async (open, journal, bus) =>
        {
            CompactionResult result = await journal.CompactAsync(open.Manifest.Settings.BlackBox, t);
            string thinned = result.Thinned.ToString(CultureInfo.InvariantCulture);
            string removed = result.Deleted.ToString(CultureInfo.InvariantCulture);
            string used = result.TotalBytes.ToString(CultureInfo.InvariantCulture);
            string warning = result.BudgetWarning ? " (20% of the budget or more)" : string.Empty;
            output.WriteLine($"compact: {thinned} thinned, {removed} summaries removed, {used} bytes used{warning}");
            return 0;
        }));

        Command recover = new("recover", "Lists commands that were interrupted (no outcome record) with what they were about to overwrite.") { project };
        recover.SetAction(async (p, t) => await Run(output, p.GetValue(project), false, async (open, journal, bus) =>
        {
            IReadOnlyList<InterruptedCommand> list = await journal.FindInterruptedAsync(t);
            foreach (InterruptedCommand i in list)
            {
                output.WriteLine($"interrupted: record {i.Start.Seq.ToString(CultureInfo.InvariantCulture)} {i.Start.At:u} {i.Start.Kind} {i.Start.PayloadJson}  (restore with: ample blackbox restore {i.Start.Seq.ToString(CultureInfo.InvariantCulture)})");
            }

            output.WriteLine(list.Count == 0 ? "recover: nothing was interrupted" : $"recover: {list.Count.ToString(CultureInfo.InvariantCulture)} interrupted command(s)");
            return list.Count == 0 ? 0 : 1;
        }));

        Argument<string> crashFile = new("file") { Description = "A project file (relative path)." };
        Command simulateCrash = new("simulate-crash", "For tests: writes the start of a file write to the black box and stops, as a crash would (the next health check finds it).") { crashFile, project };
        simulateCrash.Hidden = true;
        simulateCrash.SetAction(async (p, t) => await Run(output, p.GetValue(project), true, async (open, journal, bus) =>
        {
            string rel = p.GetValue(crashFile)!;
            string full = Ample.Audio.IO.ProjectPath.Resolve(open.Root, rel);
            string pre = System.Text.Json.JsonSerializer.Serialize(new { path = rel, existed = File.Exists(full), text = File.Exists(full) ? await File.ReadAllTextAsync(full, t) : null });
            await journal.AppendAsync("cli", "file.write", "start", Guid.NewGuid().ToString("N"), System.Text.Json.JsonSerializer.Serialize(new { path = rel }), pre, null, t);
            output.WriteLine("The black box now holds an unfinished command.");
            return 0;
        }));
        return [new Command("blackbox", "AMPLE Redux recovery archive (legacy command name).") { tail, show, verify, export, restore, compact, simulateCrash }, recover];
    }

    private static string Mirror => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "blackbox-mirror");

    private static async Task<int> Run(TextWriter output, string? projectFolder, bool writer, Func<OpenProject, Journal, CommandBus?, Task<int>> action)
    {
        try
        {
            string root = Path.GetFullPath(projectFolder ?? Directory.GetCurrentDirectory());
            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: !writer);
            using Journal journal = await Journal.OpenAsync(Fs, Clock, root, open.Manifest.Id.ToString("D"), new JournalOptions(MirrorRoot: Mirror));
            return await action(open, journal, writer ? new CommandBus(journal, new CommandContext(Fs, root, Clock), "cli") : null);
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }
}
