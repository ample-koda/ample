using Ample.Engine.Engine;

namespace Ample.Core.Tests;

[Trait("Category", "Engine")]
public sealed class BuildTimingsTests
{
    [Fact]
    public void The_summary_names_the_wall_time_the_total_block_work_and_the_slowest_blocks()
    {
        BuildReport report = new(
            [new BlockResult("drums", "rendered", "t1", null, 8.25), new BlockResult("bass", "rendered", "t2", null, 2.0), new BlockResult("piano", "rendered", "t3", null, 4.0), new BlockResult("mix", "cached", "t4", null)], "t4", 1, 3)
        { ElapsedSeconds = 9.0 };

        BuildTimingSummary summary = BuildTimings.Summarize(report, 2);

        Assert.Equal(9.0, summary.ElapsedSeconds);
        Assert.Equal(14.25, summary.BlockWorkSeconds);   // more than the wall time: the blocks overlap
        Assert.Equal(["drums", "piano"], summary.Slowest.Select(b => b.Block));
        Assert.Contains("9.0 s", summary.Describe(), StringComparison.Ordinal);
        Assert.Contains("overlap", summary.Describe(), StringComparison.Ordinal);
        Assert.Contains("drums 8.3 s", summary.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_that_only_read_the_cache_still_reports_its_time()
    {
        BuildReport report = new([new BlockResult("drums", "cached", "t1", null), new BlockResult("mix", "cached", "t2", null)], "t2", 2, 0) { ElapsedSeconds = 0.4 };

        string line = BuildTimings.Summarize(report).Describe();

        Assert.Contains("0.4 s", line, StringComparison.Ordinal);
        Assert.Contains("no block had to render", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_build_reports_a_wall_time_that_is_at_least_the_longest_block()
    {
        using EngineRig r = await EngineRig.CreateAsync();

        BuildReport first = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        BuildReport second = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));

        Assert.True(first.ElapsedSeconds > 0);
        Assert.True(first.ElapsedSeconds >= first.Blocks.Max(b => b.Seconds ?? 0));
        Assert.True(second.ElapsedSeconds > 0, "a build that only read the cache has a time too");
    }
}
