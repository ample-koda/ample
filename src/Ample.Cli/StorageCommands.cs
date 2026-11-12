using System.CommandLine;
using System.Globalization;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Cli;

/// <summary>The <c>ample storage</c> commands: what the takebank uses, a dry run, the clean-up and the options.</summary>
internal static class StorageCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static Command Create(TextWriter output)
    {
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Command storage = new("storage", "What the takebank uses, what could be given back, the clean-up and the storage options.");

        Command show = new("show", "Shows the size of the takebank, what is protected and what could be given back.") { project };
        show.SetAction(async (parse, token) => await Run(output, parse.GetValue(project), async service =>
        {
            StorageOverview o = service.Overview();
            output.WriteLine($"Takebank: {Format(o.DataBytes)} in {o.Takes.ToString(CultureInfo.InvariantCulture)} take(s) at {o.DataFolder}");
            output.WriteLine($"  protected {Format(o.ProtectedBytes)}, can be given back {Format(o.ReclaimableBytes)}");
            output.WriteLine($"  AI working files {Format(o.AiBytes)}, temporary files {Format(o.TmpBytes)}, history {Format(o.HistoryBytes)}");
            output.WriteLine($"  free on the drive {Format(o.FreeBytes)} of {Format(o.TotalBytes)}");
            foreach (StorageLine line in o.ByReason)
            {
                output.WriteLine($"  {line.Name,-12} {line.Takes.ToString(CultureInfo.InvariantCulture),5} take(s) {Format(line.Bytes),10}");
            }

            if (o.OverLimit)
            {
                output.WriteLine($"Over the soft limit of {o.SoftLimitGb.ToString(CultureInfo.InvariantCulture)} GB: run 'ample storage clean' to see what a clean-up would give back.");
            }

            if (o.LowDisk)
            {
                output.WriteLine("The drive is low on free space.");
            }

            await Task.CompletedTask;
            return 0;
        }));

        Option<string?> accept = new("--accept") { Description = "The fingerprint printed by the dry run; with it the clean-up is run." };
        Option<string?> projectClean = new("--project") { Description = "The project folder (default: the current folder)." };
        Command clean = new("clean", "A dry run of a clean-up (what would go, what stays protected); with --accept <fingerprint> it runs.") { projectClean, accept };
        clean.SetAction(async (parse, token) => await Run(output, parse.GetValue(projectClean), async service =>
        {
            StorageCleanPlan plan = service.Plan();
            output.WriteLine($"A clean-up would remove {plan.TakeIds.Count.ToString(CultureInfo.InvariantCulture)} take(s) and give back {Format(plan.Bytes)} (free space {Format(plan.FreeBefore)} -> {Format(plan.FreeAfter)}).");
            foreach (StorageLine line in plan.ByReason)
            {
                output.WriteLine($"  {line.Name,-12} {line.Takes.ToString(CultureInfo.InvariantCulture),5} take(s) {Format(line.Bytes),10}");
            }

            output.WriteLine($"Protected and kept: {Format(plan.ProtectedBytes)}. Nothing protected is ever removed; removed takes render again when needed.");
            string? given = parse.GetValue(accept);
            if (given is null)
            {
                output.WriteLine($"To run it: ample storage clean --accept {plan.Fingerprint}");
                return 0;
            }

            if (!string.Equals(given, plan.Fingerprint, StringComparison.Ordinal))
            {
                output.WriteLine("AMP5602: what would be cleaned up changed since the dry run. Run the dry run again.");
                return 1;
            }

            int lastPercent = -1;
            StorageProgress result = await service.RunAsync(plan, new Progress<StorageProgress>(p =>
            {
                int percent = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
                if (percent != lastPercent && percent % 10 == 0)
                {
                    lastPercent = percent;
                    output.WriteLine($"  {percent.ToString(CultureInfo.InvariantCulture)}%  {Format(p.Bytes)} of {Format(p.TotalBytes)}");
                }
            }), token);
            output.WriteLine($"Done: {Format(result.Bytes)} given back.");
            return 0;
        }, readOnly: parse.GetValue(accept) is null));

        Option<string?> projectSet = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<double?> limit = new("--soft-limit-gb") { Description = "The size above which a clean-up is suggested (0: off)." };
        Option<int?> keep = new("--keep-superseded") { Description = "How many older takes of a block stay besides the current one." };
        Option<string?> archive = new("--archive-path") { Description = "Move reclaimed data here instead of deleting it (empty: delete)." };
        Command set = new("settings", "Shows or changes the storage options of the project.") { projectSet, limit, keep, archive };
        set.SetAction(async (parse, token) =>
        {
            string root = Path.GetFullPath(parse.GetValue(projectSet) ?? Directory.GetCurrentDirectory());
            StorageSettings s = StorageSettings.Load(root);
            double? newLimit = parse.GetValue(limit);
            int? newKeep = parse.GetValue(keep);
            string? newArchive = parse.GetValue(archive);
            if (newLimit is not null || newKeep is not null || newArchive is not null)
            {
                s = s with { SoftLimitGb = newLimit ?? s.SoftLimitGb, KeepSuperseded = newKeep ?? s.KeepSuperseded, ArchivePath = newArchive is null ? s.ArchivePath : newArchive.Length == 0 ? null : newArchive };
                s.Save(root);
            }

            output.WriteLine($"softLimitGb {s.SoftLimitGb.ToString(CultureInfo.InvariantCulture)}, keepSuperseded {s.KeepSuperseded.ToString(CultureInfo.InvariantCulture)}, keepMinutes {s.KeepMinutes.ToString(CultureInfo.InvariantCulture)}, dataPath {s.DataPath ?? "(default)"}, archivePath {s.ArchivePath ?? "(delete)"}, intermediates {s.Intermediates}, silence runs are part of the compression");
            await Task.CompletedTask;
            return 0;
        });

        Option<string?> projectPack = new("--project") { Description = "The project folder (default: the current folder)." };
        Command compress = new("compress", "Compresses the plain WAV takes of the project in place (lossless: the takes and their ids do not change).") { projectPack };
        compress.SetAction(async (parse, token) => await Run(output, parse.GetValue(projectPack), async service =>
        {
            IReadOnlyList<(string DataId, long Bytes)> blobs = service.PlainBlobs();
            output.WriteLine($"{blobs.Count.ToString(CultureInfo.InvariantCulture)} plain take file(s), {Format(blobs.Sum(b => b.Bytes))}. Compressing (this can take a few minutes)...");
            int last = -1;
            StorageProgress result = await service.CompressAsync(new Progress<StorageProgress>(p =>
            {
                int percent = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
                if (percent != last && percent % 10 == 0)
                {
                    last = percent;
                    output.WriteLine($"  {percent.ToString(CultureInfo.InvariantCulture)}%  {Format(p.Bytes)} given back");
                }
            }), token);
            output.WriteLine($"Done: {Format(result.Bytes)} given back.");
            return 0;
        }, readOnly: false));

        Option<string?> projectMove = new("--project") { Description = "The project folder (default: the current folder)." };
        Argument<string> where = new("path") { Description = "The folder for the take data (for example D:\\ample-takes\\song), or - to move it back into the project." };
        Command move = new("move", "Moves the take data to another folder or drive (progress is shown; a stopped move continues when run again).") { where, projectMove };
        move.SetAction(async (parse, token) => await Run(output, parse.GetValue(projectMove), async service =>
        {
            string to = parse.GetValue(where)!;
            int last = -1;
            StorageProgress result = await service.MoveAsync(to == "-" ? null : to, new Progress<StorageProgress>(p =>
            {
                int percent = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
                if (percent != last && percent % 10 == 0)
                {
                    last = percent;
                    output.WriteLine($"  {percent.ToString(CultureInfo.InvariantCulture)}%  {Format(p.Bytes)} of {Format(p.TotalBytes)}");
                }
            }), token);
            output.WriteLine($"Done: {result.Done.ToString(CultureInfo.InvariantCulture)} file(s), {Format(result.Bytes)} moved.");
            return 0;
        }, readOnly: false));

        storage.Add(show);
        storage.Add(move);
        storage.Add(compress);
        storage.Add(clean);
        storage.Add(set);
        return storage;
    }

    private static string Format(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    private static async Task<int> Run(TextWriter output, string? project, Func<StorageService, Task<int>> action, bool readOnly = true)
    {
        try
        {
            string root = Path.GetFullPath(project ?? Directory.GetCurrentDirectory());
            using OpenProject opened = await ProjectStore.OpenAsync(Fs, Clock, root, readOnly);
            Takebank bank = Takebank.Open(Fs, Clock, root);
            return await action(new StorageService(bank, root, Path.Combine(root, ".ample", "history.db"), Clock));
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }
}
