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

    [Fact]
    public void A_serial_chain_is_not_multiplied_by_workers()
    {
        (string, long)[] four = [("reverb", 1000), ("reverb", 1000), ("reverb", 1000), ("reverb", 1000)];
        string a = Directory.CreateTempSubdirectory("ample-stats-").FullName, b = Directory.CreateTempSubdirectory("ample-stats-").FullName;
        try
        {
            BuildStats.Load(a).Record(four, 100, 8, 4, 4);   // four blocks one after the other
            BuildStats.Load(b).Record(four, 100, 8, 4, 1);   // four independent blocks
            Assert.True(BuildStats.Load(a).Estimate(["reverb"], 100).Seconds < BuildStats.Load(b).Estimate(["reverb"], 100).Seconds);
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }
}
