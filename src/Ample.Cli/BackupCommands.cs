using System.CommandLine;
using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Storage.Backup;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Cli;

/// <summary>The <c>ample backup</c>, <c>export</c>, <c>import</c> and <c>restore</c> commands.</summary>
internal static class BackupCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Backup(output), Export(output), Import(output), Restore(output)];

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static Option<string> TargetOption() => new("--target") { Description = "The folder that holds the backups.", Required = true };

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

    private static async Task<(OpenProject Project, string Root)> OpenAsync(string? folder, bool writer, CancellationToken token)
    {
        string root = Path.GetFullPath(folder ?? Directory.GetCurrentDirectory());
        return (await ProjectStore.OpenAsync(Fs, Clock, root, readOnly: !writer, token), root);
    }

    private static Command Backup(TextWriter output)
    {
        Command root = new("backup", "Incremental, verifiable backups of a project into a folder you choose.");

        Option<string> createTarget = TargetOption();
        Option<string?> createProject = ProjectOption();
        Option<bool> noTakes = new("--no-takes") { Description = "Leave out the takes and their data." };
        Command create = new("create", "Makes a backup (only what changed since the last one is stored).") { createTarget, createProject, noTakes };
        create.SetAction((parse, token) => Guard(output, async () =>
        {
            (OpenProject project, string path) = await OpenAsync(parse.GetValue(createProject), false, token);
            using (project)
            {
                BackupReport r = new BackupStore(parse.GetValue(createTarget)!, Clock).Create(path, project.Manifest.Id, project.Manifest.Name, !parse.GetValue(noTakes), token);
                BackupPolicy.Remember(path, Path.GetFullPath(parse.GetValue(createTarget)!), r.Id, Clock.UtcNow);
                output.WriteLine($"Backup {r.Id}: {r.Files} files, {r.TotalBytes.ToString(CultureInfo.InvariantCulture)} bytes, {r.NewObjects} new stored file(s) ({r.NewBytes.ToString(CultureInfo.InvariantCulture)} bytes), {r.Milliseconds} ms");
            }

            return 0;
        }));

        Option<string> listTarget = TargetOption();
        Command list = new("list", "Lists the backups, newest first.") { listTarget };
        list.SetAction((parse, _) => Guard(output, () =>
        {
            BackupStore store = new(parse.GetValue(listTarget)!, Clock);
            BackupManifest? good = store.LastGood();
            foreach (BackupManifest m in store.List())
            {
                output.WriteLine($"{m.Id}  {m.Created:u}  {m.ProjectName}  {m.Entries.Count} files  {m.Entries.Sum(e => e.Size).ToString(CultureInfo.InvariantCulture)} bytes{(m.IncludesTakes ? string.Empty : "  (no takes)")}{(good?.Id == m.Id ? "  last good" : string.Empty)}");
            }

            output.WriteLine($"{store.List().Count.ToString(CultureInfo.InvariantCulture)} backup(s)");
            return Task.FromResult(0);
        }));

        Option<string> verifyTarget = TargetOption();
        Argument<string> verifyId = new("backup") { Description = "A backup id (or a unique start of it); default: latest.", DefaultValueFactory = _ => "latest" };
        Command verify = new("verify", "Rehashes every stored file of a backup; exit code 1 when anything is missing or damaged.") { verifyId, verifyTarget };
        verify.SetAction((parse, token) => Guard(output, () =>
        {
            BackupVerifyReport r = new BackupStore(parse.GetValue(verifyTarget)!, Clock).Verify(parse.GetValue(verifyId)!, token);
            foreach (string p in r.Missing)
            {
                output.WriteLine($"MISSING  {p}");
            }

            foreach (string p in r.Corrupt)
            {
                output.WriteLine($"DAMAGED  {p}");
            }

            output.WriteLine(r.Ok ? $"Backup {r.Id} is intact ({r.Checked} stored file(s) checked); it is now the last good backup." : $"Backup {r.Id} has problems: {r.Missing.Count} missing, {r.Corrupt.Count} damaged.");
            return Task.FromResult(r.Ok ? 0 : 1);
        }));

        Option<string> pruneTarget = TargetOption();
        Option<int> keep = new("--keep") { Description = "Keep the newest N backups.", DefaultValueFactory = _ => 5 };
        Option<int> daily = new("--daily") { Description = "Also keep the newest of each of the last D days.", DefaultValueFactory = _ => 0 };
        Option<string?> pruneProject = ProjectOption();
        Option<string?> accept = new("--accept") { Description = "The words the dry run asked you to type; without it nothing is removed." };
        Option<string?> reason = new("--reason") { Description = "Why (kept in the black box)." };
        Command prune = new("prune", "Removes old backups (a dangerous act: run it without --accept for the dry run). The last good backup is always kept.") { pruneTarget, keep, daily, pruneProject, accept, reason };
        prune.SetAction((parse, token) => Guard(output, async () =>
        {
            PruneBackupsAct act = new(new BackupStore(parse.GetValue(pruneTarget)!, Clock), new PruneRules(parse.GetValue(keep), parse.GetValue(daily)));
            (OpenProject project, string path) = await OpenAsync(parse.GetValue(pruneProject), true, token);
            using (project)
            {
                using Journal journal = await Journal.OpenAsync(Fs, Clock, path, project.Manifest.Id.ToString("D"), cancellationToken: token);
                CommandContext context = new(Fs, path, Clock);
                DryRunResult dry = await act.DryRunAsync(context, token);
                foreach (string line in dry.Impact)
                {
                    output.WriteLine(line);
                }

                output.WriteLine(dry.BackupSuggestion);
                if (parse.GetValue(accept) is null)
                {
                    output.WriteLine($"Nothing was removed. To do it, run again with --accept \"{act.Acceptance}\" --reason \"...\".");
                    return 0;
                }

                await act.RunAsync(journal, context, "cli", parse.GetValue(accept)!, parse.GetValue(reason) ?? string.Empty, token);
                output.WriteLine("Pruned.");
            }

            return 0;
        }));

        Option<string> policyTarget = TargetOption();
        Option<string> auto = new("--auto") { Description = "on or off: make a backup before every dangerous act.", DefaultValueFactory = _ => "on" };
        Option<string?> policyProject = ProjectOption();
        Command policy = new("policy", "Chooses where backups go and whether one is made before every dangerous act.") { policyTarget, auto, policyProject };
        policy.SetAction((parse, token) => Guard(output, async () =>
        {
            (OpenProject project, string path) = await OpenAsync(parse.GetValue(policyProject), false, token);
            using (project)
            {
                BackupPolicy.SetAuto(path, Path.GetFullPath(parse.GetValue(policyTarget)!), string.Equals(parse.GetValue(auto), "on", StringComparison.OrdinalIgnoreCase));
                output.WriteLine($"Backup before dangerous acts: {parse.GetValue(auto)}; target {Path.GetFullPath(parse.GetValue(policyTarget)!)}");
            }

            return 0;
        }));

        foreach (Command c in new[] { create, list, verify, prune, policy })
        {
            root.Add(c);
        }

        return root;
    }

    private static Command Export(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "The archive to write (for example song.ample)." };
        Option<string?> project = ProjectOption();
        Option<bool> noTakes = new("--no-takes") { Description = "Leave out the takes and their data." };
        Command export = new("export", "Writes the project (without secrets) into one archive that any zip tool can read.") { file, project, noTakes };
        export.SetAction((parse, token) => Guard(output, async () =>
        {
            (OpenProject open, string path) = await OpenAsync(parse.GetValue(project), false, token);
            using (open)
            {
                try
                {
                    Ample.Language.SpecData spec = Ample.Docs.SpecRoot.Find(Directory.GetCurrentDirectory()).LoadSpecData();
                    Ample.Engine.Credits.CreditsService.Refresh(path, open.Manifest.Name, spec, Clock, null);
                }
                catch (Exception ex) when (ex is IOException or AmpleException or DirectoryNotFoundException or InvalidOperationException)
                {
                    output.WriteLine("Note: the credit sheet was not refreshed (" + ex.Message + ").");
                }

                BackupManifest m = Archive.Export(path, open.Manifest.Id, open.Manifest.Name, Path.GetFullPath(parse.GetValue(file)!), !parse.GetValue(noTakes), Clock);
                output.WriteLine($"Exported {Archive.Describe(m)}");
            }

            return 0;
        }));
        return export;
    }

    private static Command Import(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "The archive." };
        Argument<string> folder = new("folder") { Description = "A new folder to import into." };
        Option<bool> newId = new("--new-id") { Description = "Give the imported project a new id (default: it keeps its id)." };
        Command import = new("import", "Imports an archive into a new folder after checking every hash.") { file, folder, newId };
        import.SetAction((parse, _) => Guard(output, () =>
        {
            BackupManifest m = Archive.Import(parse.GetValue(file)!, parse.GetValue(folder)!, parse.GetValue(newId));
            output.WriteLine($"Imported {Archive.Describe(m)} into {Path.GetFullPath(parse.GetValue(folder)!)}");
            return Task.FromResult(0);
        }));
        return import;
    }

    private static Command Restore(TextWriter output)
    {
        Argument<string> id = new("backup") { Description = "A backup id (or a unique start of it), or latest." };
        Argument<string?> folder = new("folder") { Description = "A new folder to restore into (not with --in-place).", Arity = ArgumentArity.ZeroOrOne };
        Option<string> target = TargetOption();
        Option<bool> inPlace = new("--in-place") { Description = "Put the backup into an existing project (a dangerous act: run it without --accept for the dry run)." };
        Option<string?> project = ProjectOption();
        Option<string?> accept = new("--accept") { Description = "The words the dry run asked you to type." };
        Option<string?> reason = new("--reason") { Description = "Why (kept in the black box)." };
        Command restore = new("restore", "Restores a backup into a new folder, or in place as a dangerous act.") { id, folder, target, inPlace, project, accept, reason };
        restore.SetAction((parse, token) => Guard(output, async () =>
        {
            BackupStore store = new(parse.GetValue(target)!, Clock);
            if (!parse.GetValue(inPlace))
            {
                string dest = parse.GetValue(folder) ?? throw new AmpleException("AMP5301", "Say which new folder to restore into (or use --in-place).");
                store.RestoreToFolder(parse.GetValue(id)!, dest, token);
                output.WriteLine($"Restored {store.Get(parse.GetValue(id)!).Id} into {Path.GetFullPath(dest)}");
                return 0;
            }

            (OpenProject open, string path) = await OpenAsync(parse.GetValue(project), true, token);
            using (open)
            {
                using Journal journal = await Journal.OpenAsync(Fs, Clock, path, open.Manifest.Id.ToString("D"), cancellationToken: token);
                CommandContext context = new(Fs, path, Clock);
                RestoreInPlaceAct act = new(store, parse.GetValue(id)!, path);
                foreach (string line in (await act.DryRunAsync(context, token)).Impact)
                {
                    output.WriteLine(line);
                }

                if (parse.GetValue(accept) is null)
                {
                    output.WriteLine($"Nothing was changed. To do it, run again with --accept \"{act.Acceptance}\" --reason \"...\".");
                    return 0;
                }

                await act.RunAsync(journal, context, "cli", parse.GetValue(accept)!, parse.GetValue(reason) ?? string.Empty, token);
                output.WriteLine("Restored in place.");
            }

            return 0;
        }));
        return restore;
    }
}
