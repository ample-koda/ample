using System.Globalization;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>A section of a drum song: a pattern name and the number of bars it plays.</summary>
/// <param name="Name">A name of <c>patterns</c>, a built-in pattern (beat-A, beat-B) or the word <c>pattern</c> (the pattern parameter of the block).</param>
/// <param name="Bars">How many bars in a row (1 to 999).</param>
public sealed record DrumSongEntry(string Name, int Bars);

/// <summary>
/// The named patterns and the section sequence of a drums block (plan patterns P01): <c>patterns verse=kick:x... snare:..x. / fill=kick:xxxx</c> and
/// <c>song verse*4 fill chorus*8</c>. Without a song the block plays its <c>pattern</c> on every bar, as before.
/// </summary>
public sealed partial class DrumSong
{
    /// <summary>The word that stands for the <c>pattern</c> parameter of the block inside a song.</summary>
    public const string MainName = "pattern";

    private DrumSong(IReadOnlyDictionary<string, string> patterns, IReadOnlyList<DrumSongEntry> entries)
    {
        Patterns = patterns;
        Entries = entries;
    }

    /// <summary>Gets the named patterns (name to the grids of its voices), in the order they were written.</summary>
    public IReadOnlyDictionary<string, string> Patterns { get; }

    /// <summary>Gets the sections in order.</summary>
    public IReadOnlyList<DrumSongEntry> Entries { get; }

    /// <summary>Gets whether the block has a section sequence (otherwise it plays its pattern on every bar).</summary>
    public bool HasSong => Entries.Count > 0;

    /// <summary>Reads the two parameters. <paramref name="builtIn"/> are the names the engine knows without a definition (beat-A, beat-B).</summary>
    /// <exception cref="AmpleException">AMP5351 when a name, a repeat or a reference is wrong.</exception>
    public static DrumSong Parse(string patternsText, string songText, IEnumerable<string> builtIn)
    {
        Dictionary<string, string> patterns = new(StringComparer.Ordinal);
        string p = Unquote(patternsText);
        if (p.Length > 0)
        {
            foreach (string part in p.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                string name = eq > 0 ? part[..eq].Trim() : string.Empty;
                string grid = eq > 0 ? part[(eq + 1)..].Trim() : string.Empty;
                if (!NamePattern().IsMatch(name) || grid.Length == 0)
                {
                    throw new AmpleException("AMP5351", $"The patterns '{patternsText}' must look like verse=kick:x...x... snare:....x... / fill=kick:xxxxxxxx: a name of letters, digits, - and _, an =, then the voice grids, and patterns separated by /.");
                }

                if (name == MainName || builtIn.Contains(name, StringComparer.Ordinal)) throw new AmpleException("AMP5351", $"The pattern name '{name}' is taken by the engine; choose another name.");
                if (!patterns.TryAdd(name, grid)) throw new AmpleException("AMP5351", $"The pattern '{name}' is defined twice.");
            }
        }

        List<DrumSongEntry> entries = [];
        string s = Unquote(songText);
        if (s.Length > 0)
        {
            HashSet<string> known = new(patterns.Keys, StringComparer.Ordinal);
            foreach (string b in builtIn) known.Add(b);
            known.Add(MainName);
            foreach (string token in s.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                int star = token.IndexOf('*', StringComparison.Ordinal);
                string name = star < 0 ? token : token[..star];
                int bars = 1;
                if (star >= 0 && (!int.TryParse(token[(star + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out bars) || bars is < 1 or > 999))
                {
                    throw new AmpleException("AMP5351", $"The repeat in '{token}' must be a whole number of bars from 1 to 999, like {name}*4.");
                }

                if (!known.Contains(name)) throw new AmpleException("AMP5351", $"The song names '{name}', which is not a pattern. The patterns are: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
                entries.Add(new DrumSongEntry(name, bars));
            }

            if (entries.Sum(e => (long)e.Bars) > 10_000) throw new AmpleException("AMP5351", "The song is longer than 10000 bars.");
        }

        return new DrumSong(patterns, entries);
    }

    /// <summary>The pattern name of every bar of the sequence, one entry per bar (an empty list when there is no song).</summary>
    public IReadOnlyList<string> Expand() => [.. Entries.SelectMany(e => Enumerable.Repeat(e.Name, e.Bars))];

    /// <summary>The pattern name for a bar counted from the start of the block (0 is the first bar); the sequence repeats from the start.</summary>
    public static string NameAtBar(IReadOnlyList<string> expanded, int barIndex) => expanded.Count == 0 ? MainName : expanded[((barIndex % expanded.Count) + expanded.Count) % expanded.Count];

    private static string Unquote(string text) => text.Trim().Trim('"').Trim();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
