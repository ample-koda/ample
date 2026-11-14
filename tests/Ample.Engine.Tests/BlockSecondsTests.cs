using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>The seconds a block really works are in the build report: only for blocks that rendered, and without the time spent waiting.</summary>
[Trait("Category", "Engine")]
public sealed class BlockSecondsTests
{
    private sealed class SleepyRenderer(string type, TimeSpan time) : IBlockRenderer
    {
        public string BlockType => type;

        public string Id => "test.sleepy." + type;

        public string Version => "1";

        public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(time, cancellationToken);
            return [new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate))];
        }
    }

    [Fact]
    public async Task A_rendered_block_reports_its_seconds_and_a_cached_one_reports_none()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport first = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        Assert.All(first.Blocks.Where(b => b.Status == "rendered"), b => Assert.True(b.Seconds > 0, b.Block));
        Assert.Contains(first.Blocks, b => b.Status == "rendered");

        BuildReport second = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        Assert.All(second.Blocks, b => Assert.Null(b.Seconds));
        Assert.Contains(second.Blocks, b => b.Status == "cached");
    }

    [Fact]
    public async Task Blocks_that_run_at_once_report_their_own_work_and_not_the_wait_for_a_slow_one()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        TimeSpan slow = TimeSpan.FromMilliseconds(1500);
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions(Parallelism: 4, ExtraRenderers: [new SleepyRenderer("drums", slow)]));
        BlockResult drums = report.Blocks.Single(b => b.Block == "drums");
        Assert.Equal("rendered", drums.Status);
        Assert.True(drums.Seconds >= slow.TotalSeconds * 0.9, "drums worked " + drums.Seconds);
        foreach (BlockResult other in report.Blocks.Where(b => b.Status == "rendered" && b.Block != "drums"))
        {
            // a block that follows the slow one (the mixer) or ran beside it did not work for as long as the slow block took
            Assert.True(other.Seconds < slow.TotalSeconds * 0.7, other.Block + " worked " + other.Seconds);
        }
    }
}
