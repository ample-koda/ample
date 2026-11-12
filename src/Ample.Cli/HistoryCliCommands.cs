using System.CommandLine;
using System.Globalization;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Cli;

/// <summary>The history commands: history, checkpoint, tag, fork, go, peek, promote, drop, cleanup.</summary>
internal static class HistoryCliCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<string> reason = new("--reason") { Description = "Why (recorded in the black box).", DefaultValueFactory = _ => string.Empty };
        Option<string> accept = new("--accept") { Description = "The exact acceptance text for a dangerous act.", DefaultValueFactory = _ => string.Empty };
        Option<string> message = new("-m") { Description = "Message.", DefaultValueFactory = _ => "checkpoint" };
        Option<string?> from = new("--from") { Description = "A line, tag or node." };
        Option<bool> yes = new("--yes") { Description = "Do it (without it you only see the preview)." };
        Argument<string> name = new("name") { Description = "A line, tag or node name." };

        Command history = new("history", "Shows the lines and the newest snapshots of the current line.") { project };
        history.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            output.WriteLine($"Working on: {h.CurrentLine}{(await h.IsDirtyAsync(t) ? " (unsaved changes)" : string.Empty)}");
            foreach (LineInfo l in h.Lines())
            {
                output.WriteLine($"  {l.Name,-12} {l.Kind,-7} head {Short(l.Head)}{(l.Archived ? "  (dropped)" : string.Empty)}");
            }

            foreach ((string tag, string node, string why) in h.Tags())
            {
                output.WriteLine($"  tag {tag} -> {Short(node)}  {why}");
            }

            foreach (NodeInfo n in h.Log(h.CurrentLine, 10))
            {
                output.WriteLine($"  {Short(n.NodeId)}  {n.At:u}  {n.Kind,-10} {n.Message}");
            }

            return 0;
        }));

        Command checkpoint = new("checkpoint", "Takes a snapshot of the sources (nothing happens when nothing changed).") { project, message };
        checkpoint.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            NodeInfo? n = await h.CheckpointAsync(p.GetValue(message)!, t);
            output.WriteLine(n is null ? "Nothing changed since the last checkpoint." : $"Checkpoint {Short(n.NodeId)} on {h.CurrentLine}.");
            return 0;
        }));

        Command tag = new("tag", "Creates a locked tag.") { name, project, from, reason };
        tag.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            await h.TagAsync(p.GetValue(name)!, p.GetValue(from) ?? h.CurrentLine, p.GetValue(reason)!, t);
            output.WriteLine($"Tagged {p.GetValue(name)}.");
            return 0;
        }));

        Command fork = new("fork", "Creates a new line (default: at the current line's head).") { name, project, from };
        fork.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            LineInfo l = await h.ForkAsync(p.GetValue(name)!, p.GetValue(from), t);
            output.WriteLine($"Line {l.Name} created at {Short(l.Head)}. Use 'ample go {l.Name}' to work on it.");
            return 0;
        }));

        Command go = new("go", "Switches the working files to a line (unsaved work is checkpointed first).") { name, project };
        go.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            await h.GoAsync(p.GetValue(name)!, t);
            output.WriteLine($"Now on {p.GetValue(name)}.");
            return 0;
        }));

        Option<string?> file = new("--file") { Description = "Print this file of the peeked state." };
        Command peek = new("peek", "Looks at a line, tag or node without changing anything.") { name, project, file };
        peek.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            IReadOnlyDictionary<string, string> files = await h.PeekAsync(p.GetValue(name)!, t);
            string? want = p.GetValue(file);
            if (want is null)
            {
                foreach ((string path, string text) in files)
                {
                    output.WriteLine($"{path}  ({text.Length.ToString(CultureInfo.InvariantCulture)} chars)");
                }
            }
            else
            {
                output.WriteLine(files.TryGetValue(want, out string? text) ? text : $"AMP5243: {want} is not in that state.");
            }

            return want is not null && !files.ContainsKey(want) ? 1 : 0;
        }));

        Option<string> to = new("--to") { Description = "Target line.", DefaultValueFactory = _ => "studio" };
        Option<string> source = new("--source") { Description = "Source line.", DefaultValueFactory = _ => "garage" };
        Command promote = new("promote", "Shows what promoting a line into another would change; --yes does it.") { project, source, to, yes };
        promote.SetAction(async (p, t) => await Run(output, p.GetValue(project), async h =>
        {
            IReadOnlyList<FileChange> changes = h.PreviewPromote(p.GetValue(source)!, p.GetValue(to)!);
            foreach (FileChange c in changes)
            {
                output.WriteLine($"  {c.Change,-8} {c.Path}");
            }

            output.WriteLine($"{changes.Count.ToString(CultureInfo.InvariantCulture)} change(s) if {p.GetValue(source)} replaces {p.GetValue(to)}.");
            if (p.GetValue(yes))
            {
                NodeInfo n = await h.PromoteAsync(p.GetValue(source)!, p.GetValue(to)!, t);
                output.WriteLine($"Promoted: {p.GetValue(to)} is now {Short(n.NodeId)}. The old state stays in the history.");
            }

            return 0;
        }));

        Command drop = new("drop", "Drops (archives) a line. Without --accept you see the dry run.") { name, project, accept, reason };
        drop.SetAction(async (p, t) => await RunAct(output, p.GetValue(project), p.GetValue(accept)!, p.GetValue(reason)!, (h, journal, context) =>
        {
            DropLineAct act = new(h, p.GetValue(name)!);
            return (act.Acceptance, act.DryRunAsync(context, t).AsTask(), () => act.RunAsync(journal, context, "cli", p.GetValue(accept)!, p.GetValue(reason)!, t).AsTask());
        }));

        Command cleanup = new("cleanup", "Removes unreachable history content. Without --accept you see the dry run.") { project, accept, reason };
        cleanup.SetAction(async (p, t) => await RunAct(output, p.GetValue(project), p.GetValue(accept)!, p.GetValue(reason)!, (h, journal, context) =>
        {
            CleanupAct act = new(h);
            return (act.Acceptance, act.DryRunAsync(context, t).AsTask(), () => act.RunAsync(journal, context, "cli", p.GetValue(accept)!, p.GetValue(reason)!, t).AsTask());
        }));

        return [history, checkpoint, tag, fork, go, peek, promote, drop, cleanup];
    }

    private static string Short(string id) => id.Length > 14 ? id[..14] : id;

    private static async Task<int> Run(TextWriter output, string? projectFolder, Func<HistoryService, Task<int>> action) =>
        await WithHistory(output, projectFolder, (h, journal, context) => action(h));

    private static async Task<int> RunAct(TextWriter output, string? projectFolder, string accept, string reason, Func<HistoryService, Journal, CommandContext, (string Acceptance, Task<DryRunResult> Dry, Func<Task> Run)> make) =>
        await WithHistory(output, projectFolder, async (h, journal, context) =>
        {
            (string acceptance, Task<DryRunResult> dry, Func<Task> run) = make(h, journal, context);
            DryRunResult result = await dry;
            foreach (string line in result.Impact)
            {
                output.WriteLine("  " + line);
            }

            output.WriteLine(result.BackupSuggestion);
            if (accept.Length == 0)
            {
                output.WriteLine($"Dry run only. To do it, add: --accept \"{acceptance}\" --reason \"why\"");
                return 0;
            }

            await run();
            output.WriteLine("Done. A tombstone was written to the black box.");
            return 0;
        });

    private static async Task<int> WithHistory(TextWriter output, string? projectFolder, Func<HistoryService, Journal, CommandContext, Task<int>> action)
    {
        try
        {
            string root = Path.GetFullPath(projectFolder ?? Directory.GetCurrentDirectory());
            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, root);
            using Journal journal = await Journal.OpenAsync(Fs, Clock, root, open.Manifest.Id.ToString("D"));
            CommandContext context = new(Fs, root, Clock);
            CommandBus bus = new(journal, context, "cli");
            HistoryService history = await HistoryService.OpenAsync(Fs, Clock, root, Takebank.Open(Fs, Clock, root), bus);
            return await action(history, journal, context);
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }
}
