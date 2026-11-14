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

    private const double Song = 100;
    private static (string Type, long Bytes, double Seconds) Block(string type, double seconds) => (type, 1000, seconds);

    private static T WithRoot<T>(Func<string, T> body)
    {
        string root = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            return body(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Block_times_teach_a_pace_per_type_and_a_type_never_seen_falls_back_to_light_or_heavy()
    {
        WithRoot(root =>
        {
            BuildStats stats = BuildStats.Load(root);
            double lightGuess = stats.Estimate(["delay"], Song).Seconds / Song, heavyGuess = stats.Estimate(["gm"], Song).Seconds / Song;
            stats.Record([Block("reverb", 100), Block("reverb", 100), Block("eq", 10)], Song);   // reverb 1 s per s of song, eq 0.1 (both light)
            BuildStats learned = BuildStats.Load(root);
            double reverb = learned.Estimate(["reverb"], Song).Seconds / Song, eq = learned.Estimate(["eq"], Song).Seconds / Song;
            Assert.Equal(1.0, reverb, 6);
            Assert.Equal(0.1, eq, 6);
            Assert.NotEqual(reverb, eq);
            Assert.Equal("learned", learned.Estimate(["reverb"], Song).Basis);

            // a second build moves the pace by the sliding average (Alpha 0.3), per type
            learned.Record([Block("reverb", 200)], Song);
            Assert.Equal(1.0 + (0.3 * (2.0 - 1.0)), learned.Estimate(["reverb"], Song).Seconds / Song, 6);
            Assert.Equal(0.1, learned.Estimate(["eq"], Song).Seconds / Song, 6);   // eq did not render: unchanged

            // never seen: light types follow the (learned) light pace, heavy ones the heavy pace; neither is the first guess any more for the kind that was in the build
            double light = learned.Estimate(["delay"], Song).Seconds / Song, heavy = learned.Estimate(["gm"], Song).Seconds / Song;
            Assert.NotEqual(lightGuess, light);
            Assert.Equal(heavyGuess, heavy, 6);
            Assert.Equal(learned.Estimate(["chorus"], Song).Seconds, learned.Estimate(["delay"], Song).Seconds, 9);
            return 0;
        });
    }

    [Fact]
    public void Learned_paces_survive_a_reload_and_an_old_file_without_paces_still_loads()
    {
        WithRoot(root =>
        {
            BuildStats.Load(root).Record([Block("reverb", 50), Block("gm", 400)], Song);
            string file = Path.Combine(root, ".ample", "analysis", "build-stats.json");
            Assert.Contains("\"paces\"", File.ReadAllText(file), StringComparison.Ordinal);
            BuildStats again = BuildStats.Load(root);
            Assert.Equal(0.5, again.Estimate(["reverb"], Song).Seconds / Song, 6);
            Assert.Equal(4.0, again.Estimate(["gm"], Song).Seconds / Song, 6);
            Assert.Equal(1, again.Estimate(["reverb"], Song).Builds);

            File.WriteAllText(file, "{\"builds\":3,\"lightPace\":0.2,\"heavyPace\":2.0,\"bytesPerSecond\":{\"reverb\":1000}}");
            BuildStats old = BuildStats.Load(root);
            BuildEstimate estimate = old.Estimate(["reverb", "gm"], Song);
            Assert.Equal("learned", estimate.Basis);
            Assert.Equal(3, estimate.Builds);
            Assert.Equal(Song * (0.2 + 2.0), estimate.Seconds, 6);
            Assert.Equal(1000 * Song + (Song * 44100 * 4), estimate.Bytes);

            // a damaged paces entry is ignored instead of poisoning the estimate
            File.WriteAllText(file, "{\"builds\":1,\"paces\":{\"reverb\":-1,\"eq\":0}}");
            Assert.Equal(Song * 0.03, BuildStats.Load(root).Estimate(["reverb"], Song).Seconds, 6);
            return 0;
        });
    }

    [Fact]
    public void The_schedule_uses_the_learned_pace_of_each_type()
    {
        WithRoot(root =>
        {
            BuildStats stats = BuildStats.Load(root);
            stats.Record([Block("slow", 100), Block("fast", 10)], Song);   // slow 1.0, fast 0.1 per second of song
            BuildStats learned = BuildStats.Load(root);
            RenderStep[] chain = [Step("fast"), Step("slow", 0), Step("fast", 1)];             // a chain: the sum
            RenderStep[] beside = [Step("slow"), Step("fast"), Step("fast")];                  // the slow one beside two fast ones
            foreach (int workers in (int[])[1, 4])
            {
                Assert.Equal(1.2, learned.Wall(chain, workers), 6);
            }

            Assert.Equal(1.2, learned.Wall(beside, 1), 6);
            Assert.Equal(1.0, learned.Wall(beside, 4), 6);                                      // the slow one decides
            Assert.Equal(120, learned.Estimate(beside, Song, 1).Seconds, 4);
            Assert.Equal(100, learned.Estimate(beside, Song, 4).Seconds, 4);
            Assert.Equal(1.2 / 1.0, learned.Concurrency(beside, 4), 6);                         // all the work over the wall time
            Assert.Equal(1.0, learned.Concurrency(chain, 4), 6);
            return 0;
        });
    }

    [Fact]
    public void Without_block_times_the_old_record_path_learns_exactly_as_before()
    {
        WithRoot(root =>
        {
            BuildStats stats = BuildStats.Load(root);
            stats.Record([("reverb", 1000L), ("reverb", 1000L)], Song, 6, 2);   // 6 s on average two at once: 12 s of work for two light blocks
            BuildStats back = BuildStats.Load(root);
            double expectedLight = 0.03 + (0.3 * ((12.0 / (2 * Song)) - 0.03));
            Assert.Equal(expectedLight, back.Estimate(["reverb"], Song).Seconds / Song, 3);
            Assert.Equal(expectedLight, back.Estimate(["delay"], Song).Seconds / Song, 3);   // no per type pace: light and heavy are the only paces
            Assert.DoesNotContain("\"reverb\": 0", File.ReadAllText(Path.Combine(root, ".ample", "analysis", "build-stats.json")), StringComparison.Ordinal);

            // nothing to learn from an empty build, a song without length or a block time that is not a number
            BuildStats other = BuildStats.Load(root);
            other.Record(new List<(string Type, long Bytes, double Seconds)>(), Song);
            other.Record([Block("reverb", 5)], 0);
            other.Record([Block("reverb", double.NaN)], Song);
            Assert.Equal(1, BuildStats.Load(root).Estimate(["reverb"], Song).Builds);
            return 0;
        });
    }
}
