using Ample.Audio.Engine;

namespace Ample.Server.Tests;

public sealed class EstimateMemoryTests
{
    private static BuildEstimateReport Report(int blocks) => new(new BuildEstimate(blocks, 1000, 2.5, "guess", 0), 5_000_000, 0, true, "text");

    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_estimate_is_found_again_for_the_same_key_within_its_lifetime()
    {
        EstimateMemory memory = new();
        memory.Put("song|1|bake|-|", Report(3), T0);

        Assert.True(memory.TryGet("song|1|bake|-|", T0.AddSeconds(19), out BuildEstimateReport? found));
        Assert.Equal(3, found!.Estimate.Blocks);
    }

    [Fact]
    public void An_older_estimate_is_not_used()
    {
        EstimateMemory memory = new();
        memory.Put("song|1|bake|-|", Report(3), T0);

        Assert.False(memory.TryGet("song|1|bake|-|", T0.AddSeconds(21), out _));
    }

    [Fact]
    public void Another_text_range_or_block_is_another_estimate()
    {
        EstimateMemory memory = new();
        memory.Put("song|1|bake|-|", Report(3), T0);

        Assert.False(memory.TryGet("song|2|bake|-|", T0, out _));
        Assert.False(memory.TryGet("song|1|preview|-|", T0, out _));
        Assert.False(memory.TryGet("song|1|bake|3-2|", T0, out _));
        Assert.False(memory.TryGet("song|1|bake|-|bass", T0, out _));
    }

    [Fact]
    public void A_clock_that_went_back_and_a_cleared_memory_find_nothing()
    {
        EstimateMemory memory = new();
        memory.Put("k", Report(1), T0);
        Assert.False(memory.TryGet("k", T0.AddSeconds(-5), out _));

        memory.Clear();
        Assert.False(memory.TryGet("k", T0, out _));
    }

    [Fact]
    public void A_new_estimate_replaces_the_old_one()
    {
        EstimateMemory memory = new();
        memory.Put("a", Report(1), T0);
        memory.Put("b", Report(2), T0);

        Assert.False(memory.TryGet("a", T0, out _));
        Assert.True(memory.TryGet("b", T0, out BuildEstimateReport? found));
        Assert.Equal(2, found!.Estimate.Blocks);
    }
}
