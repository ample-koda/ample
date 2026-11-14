using Ample.Audio.Engine;

namespace Ample.Core.Tests;

/// <summary>The size and time estimate: a first guess, then what earlier builds really needed.</summary>
public sealed class BuildStatsTests
{
    [Fact]
    public void The_first_estimate_is_a_guess_from_the_length_of_the_song_and_later_ones_learn()
    {
        string root = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            BuildStats first = BuildStats.Load(root);
            BuildEstimate guess = first.Estimate(["sequencer", "reverb", "gm"], 100);
            Assert.Equal("default", guess.Basis);
            Assert.Equal(3 * 100 * 44100 * 4L, guess.Bytes);
            Assert.True(guess.Seconds > 0);

            // a build whose blocks only stored a tenth of that, and ran twice as fast as guessed
            first.Record([("sequencer", 100 * 44100 * 4L / 10), ("reverb", 100 * 44100 * 4L / 10), ("gm", 100 * 44100 * 4L / 10)], 100, 5);
            BuildEstimate learned = BuildStats.Load(root).Estimate(["sequencer", "reverb", "gm"], 100);
            Assert.Equal("learned", learned.Basis);
            Assert.Equal(1, learned.Builds);
            Assert.True(learned.Bytes < guess.Bytes);
            Assert.True(File.Exists(Path.Combine(root, ".ample", "analysis", "build-stats.json")));

            // unknown types keep the default
            Assert.Equal(100 * 44100 * 4L, BuildStats.Load(root).Estimate(["delay"], 100).Bytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static RenderStep Step(string type, params int[] after) => new(type, after);

    [Fact]
    public void Only_stale_and_waiting_blocks_render_in_a_plan()
    {
        Assert.True(BuildStats.WillRender("stale"));
        Assert.True(BuildStats.WillRender("waiting"));   // it follows a stale input
        foreach (string status in new[] { "cached", "rendered", "frozen", "adopted", "skipped", "failed", "cancelled", "deferred" })
        {
            Assert.False(BuildStats.WillRender(status), status);
        }
    }

    [Fact]
    public void Steps_wait_only_for_blocks_that_render()
    {
        // source -> reverb -> master, and a second input of master that is cached and does not render
        (string, string, IReadOnlyList<string>)[] blocks =
        [
            ("src", "click", []),
            ("cached", "click", []),
            ("fx", "reverb", ["src"]),
            ("master", "master", ["fx", "cached", "master", "ghost"]),   // itself and an unknown name are no waits
        ];
        IReadOnlyList<RenderStep> steps = BuildStats.StepsOf(blocks, new HashSet<string>(["src", "fx", "master"], StringComparer.Ordinal));
        Assert.Equal(["click", "reverb", "master"], steps.Select(s => s.Type));
        Assert.Empty(steps[0].After);
        Assert.Equal([0], steps[1].After);
        Assert.Equal([1], steps[2].After);

        // the same input listed twice is one wait
        Assert.Equal([0], BuildStats.StepsOf([("a", "click", []), ("b", "reverb", ["a", "a"])], new HashSet<string>(["a", "b"], StringComparer.Ordinal))[1].After);
    }

    [Fact]
    public void Wall_is_the_sum_of_a_chain_the_longest_of_independent_blocks_and_the_slowest_source_plus_the_mixer()
    {
        string root = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            BuildStats stats = BuildStats.Load(root);   // default paces: light 0.03, heavy 0.5 per second of song
            RenderStep[] chain = [Step("reverb"), Step("reverb", 0), Step("reverb", 1), Step("reverb", 2)];
            RenderStep[] independent = [Step("reverb"), Step("reverb"), Step("reverb"), Step("reverb")];
            Assert.Equal(4 * 0.03, stats.Wall(chain, 4), 9);
            Assert.Equal(4 * 0.03, stats.Wall(chain, 1), 9);
            Assert.Equal(0.03, stats.Wall(independent, 4), 9);
            Assert.Equal(4 * 0.03, stats.Wall(independent, 1), 9);
            Assert.Equal(2 * 0.03, stats.Wall(independent, 2), 9);

            // eight heavy sources into one light mixer
            RenderStep[] fanIn = [.. Enumerable.Range(0, 8).Select(_ => Step("gm")), Step("mixer", 0, 1, 2, 3, 4, 5, 6, 7)];
            Assert.Equal(0.5 + 0.03, stats.Wall(fanIn, 8), 9);
            Assert.Equal((2 * 0.5) + 0.03, stats.Wall(fanIn, 4), 9);
            Assert.Equal((8 * 0.5) + 0.03, stats.Wall(fanIn, 1), 9);

            // a wait that never ends is not a hang: what cannot start is counted one after the other
            Assert.Equal(2 * 0.03, stats.Wall([Step("reverb", 1), Step("reverb", 0)], 4), 9);
            Assert.Equal(0, stats.Wall([], 4));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Concurrency_is_how_many_blocks_run_at_once_on_average()
    {
        string root = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            BuildStats stats = BuildStats.Load(root);
            Assert.Equal(1, stats.Concurrency([Step("reverb"), Step("reverb", 0), Step("reverb", 1), Step("reverb", 2)], 4), 9);   // one after the other
            Assert.Equal(4, stats.Concurrency([Step("reverb"), Step("reverb"), Step("reverb"), Step("reverb")], 4), 9);
            Assert.Equal(2, stats.Concurrency([Step("reverb"), Step("reverb"), Step("reverb"), Step("reverb")], 2), 9);   // limited by the workers

            // a mixer of eight heavy sources runs nearly eight at once (the old rule, blocks divided by the longest chain, said 5 of 9)
            RenderStep[] fanIn = [.. Enumerable.Range(0, 8).Select(_ => Step("gm")), Step("mixer", 0, 1, 2, 3, 4, 5, 6, 7)];
            double wide = stats.Concurrency(fanIn, 8);
            Assert.InRange(wide, 7.5, 8.0);

            // one heavy block beside a chain of two light ones: the heavy one decides, almost nothing runs at once (the old rule said 2 of 3)
            double narrow = stats.Concurrency([Step("gm"), Step("reverb"), Step("reverb", 1)], 4);
            Assert.InRange(narrow, 1.0, 1.15);
            Assert.Equal(1, stats.Concurrency([], 4));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_serial_build_is_not_stretched_by_the_worker_limit_when_learning()
    {
        string a = Directory.CreateTempSubdirectory("ample-stats-").FullName, b = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            (string, long)[] four = [("reverb", 1000), ("reverb", 1000), ("reverb", 1000), ("reverb", 1000)];
            RenderStep[] chain = [Step("reverb"), Step("reverb", 0), Step("reverb", 1), Step("reverb", 2)];
            RenderStep[] independent = [Step("reverb"), Step("reverb"), Step("reverb"), Step("reverb")];
            BuildStats serialStats = BuildStats.Load(a), parallelStats = BuildStats.Load(b);
            serialStats.Record(four, 100, 8, serialStats.Concurrency(chain, 4));          // four blocks one after the other: 8 s of work
            parallelStats.Record(four, 100, 8, parallelStats.Concurrency(independent, 4));   // four blocks side by side: 32 s of work in the same 8 s
            double serial = BuildStats.Load(a).Estimate(["reverb"], 100).Seconds, parallel = BuildStats.Load(b).Estimate(["reverb"], 100).Seconds;
            Assert.True(serial < parallel);
            // the pace moves 0.3 of the way from the default 0.03 to what the build showed per second of song: 8 s of work / (4 blocks x 100 s) = 0.02 for the chain, 32 s / 400 s = 0.08 side by side
            Assert.Equal(0.03 + (0.3 * (0.02 - 0.03)), serial / 100, 9);
            Assert.Equal(0.03 + (0.3 * (0.08 - 0.03)), parallel / 100, 9);
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    public void The_estimate_of_a_wide_graph_is_the_time_of_the_schedule_and_not_the_sum()
    {
        string root = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            BuildStats stats = BuildStats.Load(root);
            RenderStep[] independent = [Step("reverb"), Step("reverb"), Step("reverb"), Step("reverb")];
            RenderStep[] chain = [Step("reverb"), Step("reverb", 0), Step("reverb", 1), Step("reverb", 2)];
            double sum = stats.Estimate(["reverb", "reverb", "reverb", "reverb"], 100).Seconds;
            Assert.Equal(sum, stats.Estimate(chain, 100, 4).Seconds, 6);                 // a chain takes the sum
            Assert.Equal(sum / 4, stats.Estimate(independent, 100, 4).Seconds, 6);       // four workers
            Assert.Equal(sum, stats.Estimate(independent, 100, 1).Seconds, 6);           // one worker
            Assert.Equal(4, stats.Estimate(independent, 100, 4).Blocks);                 // the space and the count do not change
            Assert.Equal(stats.Estimate(["reverb"], 100).Bytes * 4, stats.Estimate(independent, 100, 4).Bytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
