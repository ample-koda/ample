using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

public class HistoryArchiveTests
{
    private static async Task<(HistoryRig Rig, string Blob)> DroppedLineAsync()
    {
        HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "base"));
        await r.History.ForkAsync("experiment");
        await r.History.GoAsync("experiment");
        r.Write("song.amplet", "only on experiment");
        await r.History.CheckpointAsync("experiment work");
        await r.History.GoAsync("garage");
        DropLineAct drop = new(r.History, "experiment");
        await drop.RunAsync(r.Journal, r.Context, "user", drop.Acceptance, "done");
        return (r, r.History.PlanCleanup().Blobs.Single());
    }

    private static async Task CleanupAsync(HistoryRig r)
    {
        CleanupAct act = new(r.History);
        await act.RunAsync(r.Journal, r.Context, "user", act.Acceptance, "reclaim");
    }

    [Fact]
    public async Task Dry_run_reports_the_archive_and_cleanup_writes_it_before_removing_the_original()
    {
        (HistoryRig r, string blob) = await DroppedLineAsync();
        using (r)
        {
            CleanupPlan plan = r.History.PlanCleanup();
            Assert.Equal(["experiment"], plan.ArchivedLines);
            Assert.Equal([blob], plan.ArchivedBlobs);
            Assert.True(plan.ArchiveBytes > 0);
            DryRunResult dry = await new CleanupAct(r.History).DryRunAsync(r.Context, default);
            Assert.Contains(dry.Impact, e => e.Contains("history-archive") && e.Contains("experiment"));

            await CleanupAsync(r);

            Assert.False(r.Bank.DataExists(blob));
            string copy = HistoryArchive.Find(r.Root, blob)!;
            Assert.Equal("only on experiment", File.ReadAllText(copy));
            string manifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(copy)!)!, "manifest.json"));
            Assert.Contains("experiment", manifest);
            Assert.Contains("experiment work", manifest);
        }
    }

    [Fact]
    public async Task A_failing_archive_makes_the_cleanup_remove_nothing()
    {
        (HistoryRig r, string blob) = await DroppedLineAsync();
        using (r)
        {
            File.WriteAllText(HistoryArchive.Folder(r.Root), "in the way");
            AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await CleanupAsync(r));
            Assert.Equal("AMP5247", ex.DiagnosticCode);
            Assert.True(r.Bank.DataExists(blob));
            Assert.NotEmpty(r.History.PlanCleanup().Nodes);
        }
    }

    [Fact]
    public async Task Bringing_back_a_dropped_line_after_cleanup_restores_the_files_from_the_archive()
    {
        (HistoryRig r, string blob) = await DroppedLineAsync();
        using (r)
        {
            await CleanupAsync(r);
            Assert.False(r.Bank.DataExists(blob));

            await r.History.RestoreLineAsync("experiment", "need it");

            Assert.True(r.Bank.DataExists(blob));
            await r.History.GoAsync("experiment");
            Assert.Equal("only on experiment", r.Read("song.amplet"));
        }
    }
}
