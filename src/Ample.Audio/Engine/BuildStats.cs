using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>What a build is expected to need, and how sure that is.</summary>
/// <param name="Blocks">How many blocks have to render.</param>
/// <param name="Bytes">Space for the new takes.</param>
/// <param name="Seconds">Time to render them.</param>
/// <param name="Basis"><c>default</c> (an educated guess from the length of the song) or <c>learned</c> (from earlier builds of this project).</param>
/// <param name="Builds">How many earlier builds the numbers rest on.</param>
public sealed record BuildEstimate(int Blocks, long Bytes, double Seconds, string Basis, int Builds);

/// <summary>A block that renders in a build: its type and the blocks of the same list (by index) it has to wait for.</summary>
/// <param name="Type">The block type (it decides the pace).</param>
/// <param name="After">The indexes of the blocks that must be finished before this one starts.</param>
public sealed record RenderStep(string Type, IReadOnlyList<int> After);

/// <summary>
/// The size and time estimate of a build. A block of type X is expected to store <c>bytes per second of song</c> and to render at a pace per
/// second of song; both start as educated guesses (a stereo 16 bit WAV of the whole song; light blocks fast, GM, plugin and AI blocks slow) and are
/// replaced by a sliding average of what earlier builds of this project really needed (<c>.ample/analysis/build-stats.json</c>).
/// </summary>
public sealed class BuildStats
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const double Alpha = 0.3;
    private const double MinimumPace = 0.0001;   // a pace of nothing would make every later estimate nothing
    private const int StereoBytesPerSecond = 44100 * 4;
    private static readonly HashSet<string> Heavy = new(StringComparer.Ordinal) { "gm", "midi-render", "voice-clone", "ai-voice", "ace-music", "choir", "lyrics", "import-audio", "stretch", "pitch" };

    private readonly string path;
    private readonly Dictionary<string, double> bytesPerSecond = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> paces = new(StringComparer.Ordinal);   // learned per block type from measured block times; a type not in here takes the light or heavy pace
    private double lightPace = 0.03;
    private double heavyPace = 0.5;
    private int builds;

    private BuildStats(string path) => this.path = path;

    /// <summary>Loads the statistics of a project (defaults when there are none).</summary>
    public static BuildStats Load(string projectRoot)
    {
        BuildStats stats = new(Path.Combine(projectRoot, ".ample", "analysis", "build-stats.json"));
        try
        {
            if (File.Exists(stats.path) && JsonNode.Parse(File.ReadAllText(stats.path)) is JsonObject root)
            {
                stats.builds = (int?)root["builds"] ?? 0;
                stats.lightPace = (double?)root["lightPace"] ?? stats.lightPace;
                stats.heavyPace = (double?)root["heavyPace"] ?? stats.heavyPace;
                if (root["paces"] is JsonObject learned)
                {
                    foreach (KeyValuePair<string, JsonNode?> p in learned)
                    {
                        if ((double?)p.Value is double pace && double.IsFinite(pace) && pace > 0)
                        {
                            stats.paces[p.Key] = pace;
                        }
                    }
                }

                if (root["bytesPerSecond"] is JsonObject per)
                {
                    foreach (KeyValuePair<string, JsonNode?> p in per)
                    {
                        stats.bytesPerSecond[p.Key] = (double?)p.Value ?? StereoBytesPerSecond;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            // statistics are only a help: start again from the defaults
        }

        return stats;
    }

    /// <summary>Estimates a build of the given blocks (their types) in a song of the given length.</summary>
    public BuildEstimate Estimate(IReadOnlyCollection<string> blockTypes, double songSeconds)
    {
        double bytes = 0, seconds = 0;
        foreach (string type in blockTypes)
        {
            bytes += songSeconds * bytesPerSecond.GetValueOrDefault(type, StereoBytesPerSecond);
            seconds += songSeconds * Pace(type);
        }

        return new BuildEstimate(blockTypes.Count, (long)bytes, seconds, builds > 0 ? "learned" : "default", builds);
    }

    /// <summary>Records a finished build: the bytes of every rendered block (exact, per type) and the elapsed time (shared out over the light and heavy blocks).</summary>
    /// <param name="rendered">The type and the stored size of every block that rendered.</param>
    /// <param name="songSeconds">The length of the song.</param>
    /// <param name="elapsedSeconds">The wall-clock time of the build.</param>
    /// <param name="concurrency">How many blocks ran at once on average, from <see cref="Concurrency"/> (1: one after the other). The elapsed time times this is the work that was done.</param>
    public void Record(IReadOnlyList<(string Type, long Bytes)> rendered, double songSeconds, double elapsedSeconds, double concurrency = 1)
    {
        if (rendered.Count == 0 || songSeconds <= 0)
        {
            return;
        }

        foreach (IGrouping<string, (string Type, long Bytes)> g in rendered.GroupBy(r => r.Type, StringComparer.Ordinal))
        {
            double observed = g.Average(r => r.Bytes) / songSeconds;
            bytesPerSecond[g.Key] = bytesPerSecond.TryGetValue(g.Key, out double old) ? old + (Alpha * (observed - old)) : observed;
        }

        int heavy = rendered.Count(r => Heavy.Contains(r.Type)), light = rendered.Count - heavy;
        double lightShare = light * songSeconds * lightPace;
        double effective = elapsedSeconds * Math.Max(1, concurrency);
        if (heavy > 0)
        {
            double observed = Math.Max(0, effective - lightShare) / (heavy * songSeconds);
            heavyPace += Alpha * (observed - heavyPace);
        }
        else if (light > 0)
        {
            double observed = effective / (light * songSeconds);
            lightPace += Alpha * (observed - lightPace);
        }

        builds++;
        Save();
    }

    private double Pace(string type) => paces.TryGetValue(type, out double learned) ? learned : Heavy.Contains(type) ? heavyPace : lightPace;

    /// <summary>
    /// Records a finished build from the measured time of every block that rendered: the bytes (as in the other overload) and a pace per block type, the seconds the block really worked per second
    /// of song, as a sliding average. The light and heavy pace, which a type not seen yet falls back to, learn from the blocks of their kind too.
    /// </summary>
    /// <param name="rendered">The type, the stored size and the working seconds (without waiting) of every block that rendered.</param>
    /// <param name="songSeconds">The length of the song.</param>
    public void Record(IReadOnlyList<(string Type, long Bytes, double Seconds)> rendered, double songSeconds)
    {
        if (rendered.Count == 0 || songSeconds <= 0 || !double.IsFinite(songSeconds) || rendered.Any(r => !double.IsFinite(r.Seconds) || r.Seconds < 0))
        {
            return;
        }

        foreach (IGrouping<string, (string Type, long Bytes, double Seconds)> g in rendered.GroupBy(r => r.Type, StringComparer.Ordinal))
        {
            double observed = g.Average(r => r.Bytes) / songSeconds;
            bytesPerSecond[g.Key] = bytesPerSecond.TryGetValue(g.Key, out double old) ? old + (Alpha * (observed - old)) : observed;
            double pace = Math.Max(MinimumPace, g.Average(r => r.Seconds) / songSeconds);
            paces[g.Key] = paces.TryGetValue(g.Key, out double before) ? before + (Alpha * (pace - before)) : pace;
        }

        double[] heavy = [.. rendered.Where(r => Heavy.Contains(r.Type)).Select(r => r.Seconds / songSeconds)];
        double[] light = [.. rendered.Where(r => !Heavy.Contains(r.Type)).Select(r => r.Seconds / songSeconds)];
        if (heavy.Length > 0)
        {
            heavyPace += Alpha * (Math.Max(MinimumPace, heavy.Average()) - heavyPace);
        }

        if (light.Length > 0)
        {
            lightPace += Alpha * (Math.Max(MinimumPace, light.Average()) - lightPace);
        }

        builds++;
        Save();
    }

    /// <summary>Whether a block with this status in a plan-only pass renders in the build: a stale block does, and so does a waiting one, which follows a stale input (a plan cannot know that input comes out the same, so it counts the worst case).</summary>
    public static bool WillRender(string status) => status is "stale" or "waiting";

    /// <summary>The blocks that render, each with the rendering blocks it takes its input from (an input that does not render, because it is cached or skipped, does not make it wait).</summary>
    /// <param name="blocks">Every block of the song: name, type and the names of the blocks it takes input from.</param>
    /// <param name="rendered">The names of the blocks that render.</param>
    public static IReadOnlyList<RenderStep> StepsOf(IEnumerable<(string Name, string Type, IReadOnlyList<string> Inputs)> blocks, IReadOnlySet<string> rendered)
    {
        List<(string Name, string Type, IReadOnlyList<string> Inputs)> mine = [.. blocks.Where(b => rendered.Contains(b.Name))];
        Dictionary<string, int> at = new(StringComparer.Ordinal);
        for (int i = 0; i < mine.Count; i++)
        {
            at.TryAdd(mine[i].Name, i);
        }

        return [.. mine.Select((b, i) => new RenderStep(b.Type, [.. b.Inputs.Where(n => at.TryGetValue(n, out int j) && j != i).Select(n => at[n]).Distinct()]))];
    }

    /// <summary>
    /// How long, in seconds per second of song, the steps take on this many workers. A block starts as soon as the blocks it waits for are finished and a worker is free (the earlier
    /// ones in the list first), and takes the pace of its kind. A chain takes the sum of its blocks, independent blocks the longest of them, a mixer of many sources the slowest source
    /// plus the mixer.
    /// </summary>
    public double Wall(IReadOnlyList<RenderStep> steps, int workers)
    {
        int n = steps.Count, limit = Math.Max(1, workers);
        int[] waiting = [.. steps.Select(s => s.After.Count)];
        List<int>[] followers = [.. steps.Select(_ => new List<int>())];
        for (int i = 0; i < n; i++)
        {
            foreach (int before in steps[i].After)
            {
                followers[before].Add(i);
            }
        }

        List<int> ready = [.. Enumerable.Range(0, n).Where(i => waiting[i] == 0)];
        List<(double At, int Index)> running = [];
        double now = 0;
        int finished = 0;
        while (finished < n)
        {
            while (ready.Count > 0 && running.Count < limit)
            {
                int next = ready[0];
                ready.RemoveAt(0);
                running.Add((now + Pace(steps[next].Type), next));
            }

            if (running.Count == 0)
            {
                // a wait that never ends (not a song that can build): the rest is counted one after the other
                return now + steps.Where((_, i) => waiting[i] > 0).Sum(s => Pace(s.Type));
            }

            (double at, int done) = running.MinBy(r => r.At);
            running.Remove((at, done));
            now = at;
            finished++;
            foreach (int after in followers[done])
            {
                if (--waiting[after] == 0)
                {
                    ready.Add(after);
                    ready.Sort();
                }
            }
        }

        return now;
    }

    /// <summary>How many blocks run at once on average on this many workers: all the work divided by the wall time (1: one after the other).</summary>
    public double Concurrency(IReadOnlyList<RenderStep> steps, int workers)
    {
        double wall = Wall(steps, workers);
        return wall > 0 ? Math.Max(1, steps.Sum(s => Pace(s.Type)) / wall) : 1;
    }

    /// <summary>Estimates a build of these steps on this many workers: the same space as the sum of the blocks, and the time of the schedule (<see cref="Wall"/>) instead of the sum.</summary>
    public BuildEstimate Estimate(IReadOnlyList<RenderStep> steps, double songSeconds, int workers) =>
        Estimate([.. steps.Select(s => s.Type)], songSeconds) with { Seconds = songSeconds * Wall(steps, workers) };

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonObject per = [];
            foreach (KeyValuePair<string, double> p in bytesPerSecond.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                per[p.Key] = Math.Round(p.Value, 1);
            }

            JsonObject learned = [];
            foreach (KeyValuePair<string, double> p in paces.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                learned[p.Key] = Math.Round(p.Value, 6);
            }

            JsonObject root = new()
            {
                ["builds"] = builds,
                ["lightPace"] = Math.Round(lightPace, 4),
                ["heavyPace"] = Math.Round(heavyPace, 4),
                ["bytesPerSecond"] = per,
                ["paces"] = learned,
            };
            string temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the statistics are a help; failing to save them never fails a build
        }
    }

    /// <summary>Formats seconds as a short human text (about 4 min).</summary>
    public static string Pretty(double seconds) => seconds < 90 ? "about " + Math.Max(1, (int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + " s" : "about " + Math.Round(seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
}
