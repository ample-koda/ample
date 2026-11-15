using System.Globalization;

namespace Ample.Engine.Engine;

/// <summary>Where the time of a build went: the wall time from Build to done, the work of the blocks (which overlaps) and the slowest blocks.</summary>
/// <param name="ElapsedSeconds">Wall time of the build.</param>
/// <param name="BlockWorkSeconds">The seconds the blocks that rendered worked, added up; blocks run side by side, so this can be more than the wall time.</param>
/// <param name="Slowest">The blocks that worked longest, longest first.</param>
public sealed record BuildTimingSummary(double ElapsedSeconds, double BlockWorkSeconds, IReadOnlyList<(string Block, double Seconds)> Slowest)
{
    /// <summary>One line for the build log: also for a build that only read the cache.</summary>
    public string Describe()
    {
        static string S(double seconds) => seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (Slowest.Count == 0)
        {
            return $"Build took {S(ElapsedSeconds)} from start to done; no block had to render (planning, cache lookups and the take bank).";
        }

        string slowest = string.Join(", ", Slowest.Select(b => $"{b.Block} {S(b.Seconds)}"));
        return $"Build took {S(ElapsedSeconds)} from start to done. The blocks worked {S(BlockWorkSeconds)} in total (they overlap, so that can be more than the wall time). Slowest: {slowest}.";
    }
}

/// <summary>Makes the timing summary of a build report.</summary>
public static class BuildTimings
{
    /// <summary>The wall time of the report, the work of the blocks that rendered and the slowest ones.</summary>
    /// <param name="report">The report of a build.</param>
    /// <param name="slowest">How many of the slowest blocks to name.</param>
    public static BuildTimingSummary Summarize(BuildReport report, int slowest = 3)
    {
        ArgumentNullException.ThrowIfNull(report);
        (string Block, double Seconds)[] worked = [.. report.Blocks.Where(b => b.Seconds is > 0).Select(b => (b.Block, b.Seconds!.Value)).OrderByDescending(b => b.Item2).ThenBy(b => b.Block, StringComparer.Ordinal)];
        return new BuildTimingSummary(report.ElapsedSeconds, worked.Sum(b => b.Seconds), [.. worked.Take(Math.Max(0, slowest))]);
    }
}
