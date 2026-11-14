using Ample.Audio.Random;
using Ample.Core.Abstractions;
using Ample.Storage.History;

namespace Ample.Core.Tests;

[Trait("Category", "History")]
public sealed class HistoryTests
{
    [Fact]
    public async Task A_new_project_has_studio_and_garage_and_you_work_on_garage_and_checkpoints_only_happen_when_something_changed()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "one"));
        Assert.Equal("garage", r.History.CurrentLine);
        Assert.Equal(["garage", "studio"], r.History.Lines().Select(l => l.Name));
        Assert.Null(await r.History.CheckpointAsync("nothing"));

        r.Write("song.amplet", "two");
        Assert.True(await r.History.IsDirtyAsync());
        NodeInfo? n = await r.History.CheckpointAsync("changed");
        Assert.NotNull(n);
        Assert.Equal("two", (await r.History.PeekAsync(n.NodeId))["song.amplet"]);
        Assert.Equal(r.History.LineOf("garage").Head, n.NodeId);
        Assert.NotEqual(r.History.LineOf("studio").Head, n.NodeId);
        Assert.False(await r.History.IsDirtyAsync());
        Assert.Equal(["changed", "first snapshot"], r.History.Log("garage").Select(x => x.Message));
    }

    [Fact]
    public async Task Checkpoints_include_editable_text_files()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "song"), ("notes.txt", "before"));
        r.Write("notes.txt", "recovered notes");

        NodeInfo? checkpoint = await r.History.CheckpointAsync("drafts saved");

        Assert.NotNull(checkpoint);
        Assert.Equal("recovered notes", (await r.History.PeekAsync(checkpoint.NodeId))["notes.txt"]);
    }

    [Fact]
    public async Task Go_saves_unsaved_work_first_restores_the_target_and_peek_changes_nothing()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "studio text"));
        r.Write("song.amplet", "garage work");
        r.Write("lyrics/chorus.amplelrc", "la la");
        await r.History.CheckpointAsync("garage state");
        await r.History.ForkAsync("experiment");
        r.Write("song.amplet", "unsaved edit on garage");

        IReadOnlyDictionary<string, string> peek = await r.History.PeekAsync("studio");
        Assert.Equal("studio text", peek["song.amplet"]);
        Assert.Equal("unsaved edit on garage", r.Read("song.amplet"));
        Assert.Equal("garage", r.History.CurrentLine);

        await r.History.GoAsync("studio");
        Assert.Equal("studio", r.History.CurrentLine);
        Assert.Equal("studio text", r.Read("song.amplet"));
        Assert.Null(r.Read("lyrics/chorus.amplelrc"));

        NodeInfo auto = r.History.Log("garage")[0];
        Assert.Equal("auto", auto.Kind);
        Assert.Equal("unsaved edit on garage", (await r.History.PeekAsync(auto.NodeId))["song.amplet"]);

        await r.History.GoAsync("garage");
        Assert.Equal("unsaved edit on garage", r.Read("song.amplet"));
        Assert.Equal("la la", r.Read("lyrics/chorus.amplelrc"));
        Assert.Contains(await r.Journal.ReadAllAsync(), x => x.Kind == "history.go");
    }

    [Fact]
    public async Task Promote_previews_the_changes_replaces_studio_and_keeps_the_old_studio_in_the_history()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "v1"));
        string oldStudio = r.History.LineOf("studio").Head;
        r.Write("song.amplet", "v2");
        r.Write("new.amplet", "added");
        await r.History.CheckpointAsync("work");

        Assert.Equal([new FileChange("new.amplet", "added"), new FileChange("song.amplet", "changed")], r.History.PreviewPromote());
        NodeInfo promoted = await r.History.PromoteAsync();
        Assert.Equal(promoted.NodeId, r.History.LineOf("studio").Head);
        Assert.Equal(oldStudio, promoted.ParentId);
        Assert.Equal("v2", (await r.History.PeekAsync("studio"))["song.amplet"]);
        Assert.Empty(r.History.PreviewPromote());
        Assert.Contains(r.History.Log("studio"), n => n.NodeId == oldStudio);
        Assert.Equal("v1", (await r.History.PeekAsync(oldStudio))["song.amplet"]);
    }

    [Fact]
    public async Task A_switch_that_fails_halfway_puts_back_the_files_it_had_changed_and_stays_on_the_old_line()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "song"), ("a.txt", "A1"));
        string first = r.History.LineOf("garage").Head;
        r.Write("a.txt", "A2");
        r.Write("blocked/x.txt", "X");
        await r.History.CheckpointAsync("second state");
        await r.History.ForkAsync("older", first);
        await r.History.GoAsync("older");
        Assert.Equal("A1", r.Read("a.txt"));

        // the folder of a file of the second state is now a file that is not tracked, so writing that file fails
        Directory.Delete(Path.Combine(r.Root, "blocked"), true);
        File.WriteAllText(Path.Combine(r.Root, "blocked"), "in the way");

        await Assert.ThrowsAnyAsync<Exception>(async () => await r.History.GoAsync("garage"));

        Assert.Equal("older", r.History.CurrentLine);
        Assert.Equal("A1", r.Read("a.txt"));
        Assert.Equal("song", r.Read("song.amplet"));
        Assert.Equal("in the way", File.ReadAllText(Path.Combine(r.Root, "blocked")));
    }

    [Fact]
    public async Task Tags_are_locked_and_changing_them_is_a_dangerous_act_with_a_tombstone()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "v1"));
        await r.History.TagAsync("release-1", "studio", "first release");
        Assert.Equal("AMP5240", (await Assert.ThrowsAsync<AmpleException>(async () => await r.History.TagAsync("release-1", "garage", "again"))).DiagnosticCode);
        Assert.Equal("AMP5243", (await Assert.ThrowsAsync<AmpleException>(async () => await r.History.TagAsync("x", "no-such-thing", "r"))).DiagnosticCode);

        r.Write("song.amplet", "v2");
        await r.History.CheckpointAsync("v2");
        RetagAct act = new(r.History, "release-1", "garage");
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(r.Journal, r.Context, "user", "yes", "because"))).DiagnosticCode);
        await act.RunAsync(r.Journal, r.Context, "user", act.Acceptance, "the first release was wrong");
        Assert.Equal(r.History.LineOf("garage").Head, r.History.Tags().Single().NodeId);
        Assert.Equal("tag-changed", r.History.Tombstones().Single().Kind);

        RetagAct remove = new(r.History, "release-1", null);
        await remove.RunAsync(r.Journal, r.Context, "user", remove.Acceptance, "not needed");
        Assert.Empty(r.History.Tags());
    }

    [Fact]
    public async Task Dropping_a_line_follows_the_rules_is_recoverable_and_cannot_touch_studio_or_the_current_line()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "v1"));
        await r.History.ForkAsync("idea");
        Assert.Equal("AMP5241", (await Assert.ThrowsAsync<AmpleException>(async () => await new DropLineAct(r.History, "studio").DryRunAsync(r.Context, CancellationToken.None))).DiagnosticCode);
        Assert.Equal("AMP5242", (await Assert.ThrowsAsync<AmpleException>(async () => await new DropLineAct(r.History, "garage").DryRunAsync(r.Context, CancellationToken.None))).DiagnosticCode);

        DropLineAct act = new(r.History, "idea");
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(r.Journal, r.Context, "user", "idea", "x"))).DiagnosticCode);
        await act.RunAsync(r.Journal, r.Context, "user", act.Acceptance, "an idea that went nowhere");
        Assert.True(r.History.LineOf("idea").Archived);
        Assert.Equal("AMP5242", (await Assert.ThrowsAsync<AmpleException>(async () => await r.History.GoAsync("idea"))).DiagnosticCode);

        await r.History.RestoreLineAsync("idea", "I changed my mind");
        Assert.False(r.History.LineOf("idea").Archived);
        Assert.Equal(["line-dropped", "line-restored"], r.History.Tombstones().Select(t => t.Kind));
    }

    [Fact]
    public async Task Cleanup_removes_only_content_that_no_active_line_and_no_tag_can_reach()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "base"));
        await r.History.ForkAsync("experiment");
        await r.History.GoAsync("experiment");
        r.Write("song.amplet", "only on experiment");
        await r.History.CheckpointAsync("experiment work");
        NodeInfo experimentNode = r.History.Log("experiment")[0];
        r.Write("song.amplet", "tagged work");
        NodeInfo tagged = (await r.History.CheckpointAsync("tagged"))!;
        await r.History.TagAsync("keep", tagged.NodeId, "protected");
        r.Write("song.amplet", "orphan work");
        NodeInfo orphan = (await r.History.CheckpointAsync("orphan"))!;
        await r.History.GoAsync("garage");
        r.Write("song.amplet", "garage moves on");
        await r.History.CheckpointAsync("garage work");

        Assert.Empty(r.History.PlanCleanup().Blobs);
        DropLineAct drop = new(r.History, "experiment");
        await drop.RunAsync(r.Journal, r.Context, "user", drop.Acceptance, "done with it");

        CleanupPlan plan = r.History.PlanCleanup();
        Assert.Contains(orphan.NodeId, plan.Nodes);
        Assert.DoesNotContain(tagged.NodeId, plan.Nodes);
        Assert.DoesNotContain(experimentNode.NodeId, plan.Nodes);
        Assert.Single(plan.Blobs);
        Assert.True(plan.Bytes > 0);

        CleanupAct act = new(r.History);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(r.Journal, r.Context, "user", "ok", "r"))).DiagnosticCode);
        await act.RunAsync(r.Journal, r.Context, "user", act.Acceptance, "reclaim space");

        Assert.Equal("tagged work", (await r.History.PeekAsync("keep"))["song.amplet"]);
        Assert.Equal("base", (await r.History.PeekAsync("studio"))["song.amplet"]);
        Assert.False(r.Bank.DataExists(plan.Blobs[0]));
        Assert.Empty(r.History.PlanCleanup().Nodes);
        Assert.Equal("cleanup", r.History.Tombstones()[^1].Kind);
        Assert.Contains(await r.Journal.ReadAllAsync(), x => x.Kind == "dangerous:history.cleanup" && x.Phase == "tombstone");
    }

    [Fact]
    public async Task Whatever_the_order_of_operations_tagged_history_survives_and_going_never_loses_work()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "start"));
        StableRng rng = new(50);
        List<string> lines = ["garage", "studio"];
        for (int i = 0; i < 60; i++)
        {
            switch (rng.NextUInt(8))
            {
                case 0:
                case 1:
                    r.Write(rng.NextUInt(2) == 0 ? "song.amplet" : "lyrics/l.amplelrc", "text " + i);
                    break;
                case 2:
                    await r.History.CheckpointAsync("cp " + i);
                    break;
                case 3 when lines.Count < 6:
                    string name = "line" + i;
                    await r.History.ForkAsync(name, lines[(int)rng.NextUInt((ulong)lines.Count)]);
                    lines.Add(name);
                    break;
                case 4:
                    string target = lines[(int)rng.NextUInt((ulong)lines.Count)];
                    if (!r.History.LineOf(target).Archived)
                    {
                        bool dirty = await r.History.IsDirtyAsync();
                        string? before = r.Read("song.amplet");
                        await r.History.GoAsync(target);
                        Assert.False(await r.History.IsDirtyAsync());
                        if (dirty)
                        {
                            Assert.Contains(await Task.WhenAll(r.History.Lines().SelectMany(l => r.History.Log(l.Name, 200)).DistinctBy(n => n.NodeId).Select(async n => (await r.History.PeekAsync(n.NodeId)).GetValueOrDefault("song.amplet"))), t => t == before);
                        }
                    }

                    break;
                case 5:
                    string tagName = "tag" + i;
                    await r.History.TagAsync(tagName, lines[(int)rng.NextUInt((ulong)lines.Count)], "random");
                    break;
                case 6 when lines.Count > 2:
                    string drop = lines[2 + (int)rng.NextUInt((ulong)lines.Count - 2)];
                    if (r.History.CurrentLine != drop && !r.History.LineOf(drop).Archived)
                    {
                        DropLineAct act = new(r.History, drop);
                        await act.RunAsync(r.Journal, r.Context, "user", act.Acceptance, "random");
                    }

                    break;
                default:
                    CleanupAct cleanup = new(r.History);
                    await cleanup.RunAsync(r.Journal, r.Context, "user", cleanup.Acceptance, "random");
                    break;
            }

            foreach ((string tag, string nodeId, _) in r.History.Tags())
            {
                for (string? id = nodeId; id is not null; id = r.History.Node(id).ParentId)
                {
                    Assert.All(r.History.Node(id).Files.Values, b => Assert.True(r.Bank.DataExists(b), $"after step {i}: content of a node reachable from tag {tag} is gone"));
                }
            }
        }
    }
}
