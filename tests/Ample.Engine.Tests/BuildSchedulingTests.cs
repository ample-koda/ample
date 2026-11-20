using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>A block starts as soon as its own inputs are ready: a slow block on one branch does not hold back an independent branch (issue 288).</summary>
[Trait("Category", "Engine")]
public sealed class BuildSchedulingTests
{
    private sealed class HeldRenderer(string type, Task release, TaskCompletionSource? started = null) : IBlockRenderer
    {
        public string BlockType => type;

        public string Id => "test.held." + type;

        public string Version => "1";

        public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            started?.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return [new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate, cancellationToken))];
        }
    }

    private sealed class NoticingRenderer(string type, TaskCompletionSource started) : IBlockRenderer
    {
        public string BlockType => type;

        public string Id => "test.noticing." + type;

        public string Version => "1";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            IReadOnlyList<RenderOutput> output = [new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate, cancellationToken))];
            return ValueTask.FromResult(output);
        }
    }

    [Fact]
    public async Task An_independent_block_starts_while_a_slow_block_of_the_same_level_is_still_running()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        TaskCompletionSource drumsRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource drumsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bassStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BuildOptions options = new(Parallelism: 4, ExtraRenderers: [new HeldRenderer("drums", drumsRelease.Task, drumsStarted), new NoticingRenderer("bass", bassStarted)]);
        Task<BuildReport> build = r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), options);
        try
        {
            await drumsStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // drums is held: bass only depends on the chords, so it must start now and not after the drums have finished
            await bassStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(build.IsCompleted);
        }
        finally
        {
            drumsRelease.TrySetResult();
        }

        BuildReport report = await build;

        Assert.Equal(["chords", "drums", "bass", "piano", "mix", "master"], report.Blocks.Select(b => b.Block));   // the report keeps the order of the levels
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "mix").Status);   // a mixer still waits for all of its inputs
    }

    [Fact]
    public async Task A_cancelled_build_still_reports_every_block_and_the_blocks_after_the_stopped_one_do_not_run()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource drumsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stop = new();
        BuildOptions options = new(Parallelism: 4, ExtraRenderers: [new HeldRenderer("drums", never.Task, drumsStarted)]);
        Task<BuildReport> build = r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), options, stop.Token);

        await drumsStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        BuildReport report = await build;

        Assert.Equal(["chords", "drums", "bass", "piano", "mix", "master"], report.Blocks.Select(b => b.Block));
        Assert.Equal("cancelled", report.Blocks.Single(b => b.Block == "drums").Status);
        Assert.DoesNotContain(report.Blocks.Where(b => b.Block is "mix" or "master"), b => b.Status == "rendered");
    }
}
