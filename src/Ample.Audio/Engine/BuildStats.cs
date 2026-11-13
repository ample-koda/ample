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

/// <summary>
/// The size and time estimate of a build. A block of type X is expected to store <c>bytes per second of song</c> and to render at a pace per
/// second of song; both start as educated guesses (a stereo 16 bit WAV of the whole song; light blocks fast, GM, plugin and AI blocks slow) and are
/// replaced by a sliding average of what earlier builds of this project really needed (<c>.ample/analysis/build-stats.json</c>).
/// </summary>
public sealed class BuildStats
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const double Alpha = 0.3;
    private const int StereoBytesPerSecond = 44100 * 4;
    private static readonly HashSet<string> Heavy = new(StringComparer.Ordinal) { "gm", "midi-render", "voice-clone", "ai-voice", "ace-music", "choir", "lyrics", "import-audio", "stretch", "pitch" };

    private readonly string path;
    private readonly Dictionary<string, double> bytesPerSecond = new(StringComparer.Ordinal);
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
            seconds += songSeconds * (Heavy.Contains(type) ? heavyPace : lightPace);
        }

        return new BuildEstimate(blockTypes.Count, (long)bytes, seconds, builds > 0 ? "learned" : "default", builds);
    }

    /// <summary>Records a finished build: the bytes of every rendered block (exact, per type) and the elapsed time (shared out over the light and heavy blocks).</summary>
    /// <param name="rendered">The type and the stored size of every block that rendered.</param>
    /// <param name="songSeconds">The length of the song.</param>
    /// <param name="elapsedSeconds">The wall-clock time of the build.</param>
    /// <param name="parallelism">How many blocks may render at once (a build of a chain of light blocks runs one after the other).</param>
    /// <param name="chainLength">The longest run of rendered blocks that each waited for the previous one; only the blocks beside that run could share workers.</param>
    public void Record(IReadOnlyList<(string Type, long Bytes)> rendered, double songSeconds, double elapsedSeconds, int parallelism = 1, int chainLength = 1)
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
        double effective = elapsedSeconds * Math.Max(1, Math.Min(parallelism, (rendered.Count + Math.Max(1, chainLength) - 1) / Math.Max(1, chainLength)));
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

            JsonObject root = new()
            {
                ["builds"] = builds,
                ["lightPace"] = Math.Round(lightPace, 4),
                ["heavyPace"] = Math.Round(heavyPace, 4),
                ["bytesPerSecond"] = per,
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
