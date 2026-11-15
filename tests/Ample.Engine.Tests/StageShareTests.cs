using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>Endpoints of one build that share a source or a prefix of effects render it once; anything that differs renders on its own (issue 298).</summary>
[Trait("Category", "Engine")]
public sealed class StageShareTests
{
    private static RenderOutput Out(params byte[] data) => new("audio", data);

    [Fact]
    public async Task Two_endpoints_that_ask_for_the_same_stage_at_once_render_it_once()
    {
        StageShare share = new(1024);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int rendered = 0;
        async Task<IReadOnlyList<RenderOutput>> Render()
        {
            Interlocked.Increment(ref rendered);
            await release.Task;
            return [Out(1, 2, 3)];
        }

        Task<IReadOnlyList<RenderOutput>> a = share.GetAsync("source", Render), b = share.GetAsync("source", Render);
        release.SetResult();
        IReadOnlyList<RenderOutput>[] both = await Task.WhenAll(a, b);

        Assert.Equal(1, rendered);
        Assert.Equal(1, share.Renders);
        Assert.Same(both[0], both[1]);
    }

    [Fact]
    public async Task A_later_endpoint_gets_the_stage_that_was_rendered_and_another_stage_renders_on_its_own()
    {
        StageShare share = new(1024);

        _ = await share.GetAsync("source", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(1)]));
        IReadOnlyList<RenderOutput> again = await share.GetAsync("source", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(9)]));
        IReadOnlyList<RenderOutput> other = await share.GetAsync("source>reverb", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(2)]));

        Assert.Equal([1], again[0].Data);
        Assert.Equal([2], other[0].Data);
        Assert.Equal(2, share.Renders);
    }

    [Fact]
    public async Task A_stage_over_the_budget_is_not_kept_and_a_failed_stage_is_not_kept()
    {
        StageShare small = new(2);
        _ = await small.GetAsync("big", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(1, 2, 3, 4)]));
        _ = await small.GetAsync("big", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(1, 2, 3, 4)]));
        Assert.Equal(2, small.Renders);

        StageShare share = new(1024);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await share.GetAsync("x", () => throw new InvalidOperationException("boom")));
        IReadOnlyList<RenderOutput> ok = await share.GetAsync("x", () => Task.FromResult<IReadOnlyList<RenderOutput>>([Out(7)]));
        Assert.Equal([7], ok[0].Data);
    }

    [Fact]
    public void The_key_changes_with_the_length_the_seed_the_settings_and_the_input_bytes()
    {
        BlockNode block = new() { Type = "reverb", Name = "fx" };
        RenderRequest Make(int bars = 2, ulong seed = 1, byte input = 5, string? setting = null) => new(block, EngineRig.Spec.Templates["reverb"], 16000, 16000, 1, bars, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]> { ["in"] = [input] }, seed, "p", Settings: setting is null ? null : new Dictionary<string, string> { ["k"] = setting });
        string baseKey = StageShare.RequestKey(Make());

        Assert.Equal(baseKey, StageShare.RequestKey(Make()));
        Assert.NotEqual(baseKey, StageShare.RequestKey(Make(bars: 3)));
        Assert.NotEqual(baseKey, StageShare.RequestKey(Make(input: 6)));
        Assert.NotEqual(baseKey, StageShare.RequestKey(Make(setting: "a")));
        Assert.True(StageShare.CanShare(Make()));
        Assert.False(StageShare.CanShare(Make() with { ForceRender = true }));
    }
    private sealed class CountingPiano(IBlockRenderer inner) : IBlockRenderer
    {
        public int Calls;

        public string BlockType => inner.BlockType;

        public string Id => inner.Id;

        public string Version => inner.Version;

        public string Determinism => inner.Determinism;

        public string ToolprintJson => inner.ToolprintJson;

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return inner.RenderAsync(request, cancellationToken);
        }
    }

    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 2 {\n  chords Am F\n}\n"
        + "piano keys at 1 for 2 {\n  from chords.notes -> notes\n}\n"
        + "reverb wet at 1 for 2 {\n  from keys.render -> render_in\n}\n"
        + "filter dark at 1 for 2 {\n  from keys.render -> render_in\n}\n"
        + "mixer mix {\n  from wet.audio -> audio1\n  from dark.audio -> audio2\n}\n"
        + "master master bake {\n  from mix.audio -> audio\n}\n";

    [Fact]
    public async Task A_build_with_one_instrument_feeding_two_effects_renders_the_instrument_once()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        IBlockRenderer real = BuiltInRenderers.All.First(x => x.BlockType == "piano");
        CountingPiano counting = new(real);

        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(Song), new BuildOptions(Parallelism: 4, ExtraRenderers: [counting]));

        Assert.DoesNotContain(report.Blocks, b => b.Status == "failed");
        Assert.Equal(1, counting.Calls);
    }
}
