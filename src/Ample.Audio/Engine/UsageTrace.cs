using System.Collections.Concurrent;

namespace Ample.Audio.Engine;

/// <summary>
/// What a build actually used of the files it reads: which samples of a sample set were played and which presets of which SoundFont. A package export runs one traced build, so the
/// package can carry exactly what the build needs and nothing else. Only the engine and the renderers write to it.
/// </summary>
public sealed class UsageTrace
{
    private static readonly AsyncLocal<(UsageTrace? Trace, string Block)> Scope = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> samples = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> presets = new(StringComparer.Ordinal);

    /// <summary>Starts recording for the block that is about to render (flows with the async work of that block).</summary>
    internal static void Begin(UsageTrace? trace, string block) => Scope.Value = (trace, block);

    /// <summary>A sample of a set was played (the SHA-256 of its file, <c>sha256:</c>-prefixed).</summary>
    internal static void Sample(string hash)
    {
        (UsageTrace? trace, string block) = Scope.Value;
        if (trace is not null)
        {
            trace.samples.GetOrAdd(block, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[hash] = 1;
        }
    }

    /// <summary>A preset of a SoundFont file was resolved for playing.</summary>
    internal static void Preset(string? path, int bank, int program)
    {
        (UsageTrace? trace, string block) = Scope.Value;
        if (trace is not null && path is not null)
        {
            trace.presets.GetOrAdd(block, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[Path.GetFullPath(path) + "|" + bank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + program.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 1;
        }
    }

    /// <summary>The SHA-256 hashes of the sample files a block played (<c>sha256:</c>-prefixed).</summary>
    public IReadOnlySet<string> SamplesOf(string block) => samples.TryGetValue(block, out ConcurrentDictionary<string, byte>? set) ? set.Keys.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The presets a block used, as SoundFont path with bank and program.</summary>
    public IReadOnlyList<(string Path, int Bank, int Program)> PresetsOf(string block)
    {
        if (!presets.TryGetValue(block, out ConcurrentDictionary<string, byte>? set))
        {
            return [];
        }

        return [.. set.Keys.Select(k => { string[] p = k.Split('|'); return (p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)); }).OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ThenBy(x => x.Item3)];
    }
}
