using System.Globalization;
using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>A channel of a sequencer: a name, a type (drum, loop, notes, bass, piano, guitar, gm) and its settings (sample, file, set, wave, vol, pan, mute, color, bars...).</summary>
/// <param name="Name">Channel name (letters, digits, - and _).</param>
/// <param name="Type">drum, loop, notes, bass, piano, guitar or gm.</param>
/// <param name="Settings">The key=value settings, in the order they were written.</param>
public sealed record SeqChannel(string Name, string Type, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>Gets a setting or a fallback.</summary>
    public string Get(string key, string fallback = "") => Settings.TryGetValue(key, out string? v) ? v : fallback;
}

/// <summary>A pattern: a name, its length in bars, an optional colour and one row of text per channel (steps for a drum, notes for a notes channel).</summary>
/// <param name="Name">Pattern name.</param>
/// <param name="Bars">Length in bars.</param>
/// <param name="Color">A colour such as #e8505b, or empty.</param>
/// <param name="Rows">Channel name to the text of its row.</param>
/// <param name="From">The pattern this one is a variation of (plan sequencer-next SQ04), or empty: it starts with those rows and its own rows replace them.</param>
public sealed record SeqPattern(string Name, int Bars, string Color, IReadOnlyDictionary<string, string> Rows, string From = "")
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>Gets the comment lines above each row, by channel name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? RowNotes { get; init; }
}

/// <summary>A pattern placed on the playlist: from bar <c>At</c> for <c>Bars</c> bars (the pattern repeats when the clip is longer than the pattern).</summary>
/// <param name="Pattern">Pattern name.</param>
/// <param name="Track">The playlist track (1 and up); tracks only order the clips on the screen.</param>
/// <param name="At">First bar (1 is the first bar of the song).</param>
/// <param name="Bars">Length in bars.</param>
public sealed record SeqClip(string Pattern, int Track, int At, int Bars)
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>A named stretch of the song (verse, chorus), shown as a band in the workspace.</summary>
/// <param name="Name">Section name.</param>
/// <param name="At">First bar.</param>
/// <param name="Bars">Length in bars.</param>
/// <param name="Color">A colour, or empty.</param>
public sealed record SeqSection(string Name, int At, int Bars, string Color)
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>A named point of the playlist, shown as a flag in the workspace (it does not change the sound).</summary>
/// <param name="Name">Marker name.</param>
/// <param name="At">The bar.</param>
public sealed record SeqMarker(string Name, int At)
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>The loop range: from bar <c>At</c> on, the song bars <c>At</c> to <c>At + Bars - 1</c> play again and again.</summary>
/// <param name="At">First bar of the range.</param>
/// <param name="Bars">Length of the range in bars.</param>
public sealed record SeqLoop(int At, int Bars)
{
    /// <summary>Gets the comment lines (starting with #) that stood right above this line; they are written again in the same place.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>
/// The text of a sequencer (plan fl-block FL01): <c>sequencer 1</c>, <c>channel</c> lines, <c>pattern</c> blocks with a row per channel, and a <c>playlist</c> of clips and
/// sections. Lines starting with # are comments: a comment stays above the line that followed it when the file is written again (comments at the very top and the very end stay there; blank lines are not kept). Twin of web/lib/seq-model.mjs.
/// </summary>
public sealed class SequenceFile
{
    /// <summary>Gets the lanes the sequencer block offers.</summary>
    public const int LaneCount = 8;

    /// <summary>Gets the channel types.</summary>
    public static IReadOnlyList<string> Types { get; } = ["drum", "loop", "notes", "bass", "piano", "guitar", "gm"];

    /// <summary>Gets the channels in order.</summary>
    public IReadOnlyList<SeqChannel> Channels { get; init; } = [];

    /// <summary>Gets the patterns in order.</summary>
    public IReadOnlyList<SeqPattern> Patterns { get; init; } = [];

    /// <summary>Gets the clips of the playlist.</summary>
    public IReadOnlyList<SeqClip> Clips { get; init; } = [];

    /// <summary>Gets the sections of the playlist.</summary>
    public IReadOnlyList<SeqSection> Sections { get; init; } = [];

    /// <summary>Gets the comment lines before the <c>sequencer 1</c> line.</summary>
    public IReadOnlyList<string> Intro { get; init; } = [];

    /// <summary>Gets the comment lines above the <c>playlist</c> line.</summary>
    public IReadOnlyList<string> PlaylistNotes { get; init; } = [];

    /// <summary>Gets the comment lines at the end of the file, after the last line.</summary>
    public IReadOnlyList<string> Footer { get; init; } = [];

    /// <summary>Gets the markers of the playlist.</summary>
    public IReadOnlyList<SeqMarker> Markers { get; init; } = [];

    /// <summary>Gets the loop range, or null.</summary>
    public SeqLoop? Loop { get; init; }

    /// <summary>The rows a pattern really plays: those of the pattern it is a variation of, with its own rows on top (a row that is just - silences the channel).</summary>
    public IReadOnlyDictionary<string, string> RowsOf(SeqPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Dictionary<string, string> rows = [];
        if (pattern.From.Length > 0 && Patterns.FirstOrDefault(p => p.Name == pattern.From) is { } baseline) foreach ((string channel, string row) in baseline.Rows) rows[channel] = row;
        foreach ((string channel, string row) in pattern.Rows)
        {
            if (pattern.From.Length > 0 && row.Trim() == "-") rows.Remove(channel);
            else rows[channel] = row;
        }

        return rows;
    }

    /// <summary>Maps an output bar to the song bar that plays there: inside or after the loop range the range repeats.</summary>
    public int SourceBar(int bar) => Loop is { } loop && bar >= loop.At ? loop.At + ((bar - loop.At) % loop.Bars) : bar;

    /// <summary>Reads a sequence file.</summary>
    /// <exception cref="AmpleException">AMP5380 for a line that cannot be read, with its line number.</exception>
    public static SequenceFile Parse(string text)
    {
        List<SeqChannel> channels = [];
        List<(string Name, int Bars, string Color, Dictionary<string, string> Rows, string From, IReadOnlyList<string>? Notes, Dictionary<string, IReadOnlyList<string>> RowNotes)> patterns = [];
        List<string> pending = [];
        IReadOnlyList<string> intro = [], playlistNotes = [];
        IReadOnlyList<string>? Take()
        {
            if (pending.Count == 0) return null;
            IReadOnlyList<string> taken = [.. pending];
            pending.Clear();
            return taken;
        }

        List<SeqClip> clips = [];
        List<SeqSection> sections = [];
        List<SeqMarker> markers = [];
        SeqLoop? loopRange = null;
        string mode = "top";
        bool header = false;
        int number = 0;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            number++;
            string line = raw.TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#')) { pending.Add(trimmed); continue; }
            if (!header)
            {
                if (!trimmed.StartsWith("sequencer ", StringComparison.Ordinal) || trimmed != "sequencer 1") throw Bad(number, "the file must start with the line: sequencer 1");
                header = true;
                intro = Take() ?? [];
                continue;
            }

            bool indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            string[] words = Words(trimmed);
            if (!indented)
            {
                switch (words[0])
                {
                    case "channel":
                        if (words.Length < 3 || !Name(words[1]) || !Types.Contains(words[2])) throw Bad(number, $"a channel looks like: channel kick drum sample=samples/kick.wav (the types are {string.Join(", ", Types)})");
                        if (channels.Any(c => c.Name == words[1])) throw Bad(number, $"the channel '{words[1]}' is defined twice");
                        channels.Add(new SeqChannel(words[1], words[2], Settings(words.Skip(3), number)) { Notes = Take() });
                        mode = "top";
                        break;
                    case "pattern":
                        if (words.Length < 2 || !Name(words[1])) throw Bad(number, "a pattern looks like: pattern verse bars=4");
                        if (patterns.Any(p => p.Name == words[1])) throw Bad(number, $"the pattern '{words[1]}' is defined twice");
                        Dictionary<string, string> s = Settings(words.Skip(2), number);
                        string from = s.GetValueOrDefault("from", string.Empty);
                        (string Name, int Bars, string Color, Dictionary<string, string> Rows, string From, IReadOnlyList<string>? Notes, Dictionary<string, IReadOnlyList<string>> RowNotes) baseline = default;
                        if (from.Length > 0)
                        {
                            baseline = patterns.FirstOrDefault(x => x.Name == from);
                            if (baseline.Name is null) throw Bad(number, $"the pattern '{words[1]}' is a variation of '{from}', which must be defined before it");
                            if (baseline.From.Length > 0) throw Bad(number, $"'{from}' is itself a variation: a variation starts from a plain pattern");
                        }

                        // the bar count of a variation defaults to its baseline's (already found and validated above, if `from` was given) -
                        // reusing it here instead of re-querying `patterns` avoids a second lookup ever being able to diverge from the first
                        int bars = s.TryGetValue("bars", out string? b) && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 999 ? n : s.ContainsKey("bars") ? throw Bad(number, "bars must be a whole number from 1 to 999") : from.Length > 0 ? baseline.Bars : 1;
                        patterns.Add((words[1], bars, s.GetValueOrDefault("color", string.Empty), [], from, Take(), []));
                        mode = "pattern";
                        break;
                    case "playlist":
                        playlistNotes = Take() ?? [];
                        mode = "playlist";
                        break;
                    default:
                        throw Bad(number, $"unknown line '{words[0]}': channel, pattern or playlist");
                }

                continue;
            }

            if (mode == "pattern")
            {
                int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
                string channel = space < 0 ? trimmed : trimmed[..space];
                string row = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
                patterns[^1].Rows[channel] = row;
                if (Take() is { } rowNotes) patterns[^1].RowNotes[channel] = rowNotes;
            }
            else if (mode == "playlist")
            {
                if (words[0] == "clip")
                {
                    Dictionary<string, string> s = Settings(words.Skip(2), number);
                    if (words.Length < 2 || !Name(words[1]) || !Int(s, "at", out int at) || !Int(s, "for", out int length)) throw Bad(number, "a clip looks like: clip verse track=1 at=1 for=4");
                    clips.Add(new SeqClip(words[1], Int(s, "track", out int track) ? track : 1, at, length) { Notes = Take() });
                }
                else if (words[0] == "marker")
                {
                    Dictionary<string, string> s = Settings(words.Skip(2), number);
                    if (words.Length < 2 || !Int(s, "at", out int at)) throw Bad(number, "a marker looks like: marker Drop at=9");
                    markers.Add(new SeqMarker(words[1], at) { Notes = Take() });
                }
                else if (words[0] == "loop")
                {
                    Dictionary<string, string> s = Settings(words.Skip(1), number);
                    if (!Int(s, "at", out int at) || !Int(s, "for", out int length)) throw Bad(number, "the loop looks like: loop at=1 for=4");
                    if (loopRange is not null) throw Bad(number, "there can be only one loop line");
                    loopRange = new SeqLoop(at, length) { Notes = Take() };
                }
                else if (words[0] == "section")
                {
                    Dictionary<string, string> s = Settings(words.Skip(2), number);
                    if (words.Length < 2 || !Int(s, "at", out int at) || !Int(s, "for", out int length)) throw Bad(number, "a section looks like: section Verse at=1 for=8 color=#4a90d9");
                    sections.Add(new SeqSection(words[1], at, length, s.GetValueOrDefault("color", string.Empty)) { Notes = Take() });
                }
                else
                {
                    throw Bad(number, $"unknown playlist line '{words[0]}': clip, section, marker or loop");
                }
            }
            else
            {
                throw Bad(number, "an indented line belongs under a pattern or the playlist");
            }
        }

        if (!header) throw Bad(1, "the file must start with the line: sequencer 1");
        return new SequenceFile
        {
            Channels = channels,
            Patterns = [.. patterns.Select(p => new SeqPattern(p.Name, p.Bars, p.Color, p.Rows, p.From) { Notes = p.Notes, RowNotes = p.RowNotes.Count == 0 ? null : p.RowNotes })],
            Intro = intro,
            PlaylistNotes = playlistNotes,
            Footer = [.. pending],
            Clips = clips,
            Sections = sections,
            Markers = markers,
            Loop = loopRange,
        };
    }

    /// <summary>Writes the file in its canonical form.</summary>
    public string Format()
    {
        StringBuilder sb = new();
        static void Notes(StringBuilder to, IReadOnlyList<string>? notes, string indent)
        {
            if (notes is null) return;
            foreach (string note in notes) to.Append(indent).Append(note).Append('\n');
        }

        Notes(sb, Intro, string.Empty);
        sb.Append("sequencer 1\n");
        foreach (SeqChannel c in Channels)
        {
            Notes(sb, c.Notes, string.Empty);
            sb.Append("channel ").Append(c.Name).Append(' ').Append(c.Type).Append(SettingsText(c.Settings)).Append('\n');
        }

        foreach (SeqPattern p in Patterns)
        {
            Notes(sb, p.Notes, string.Empty);
            sb.Append("pattern ").Append(p.Name).Append(" bars=").Append(p.Bars.ToString(CultureInfo.InvariantCulture));
            if (p.From.Length > 0) sb.Append(" from=").Append(p.From);
            if (p.Color.Length > 0) sb.Append(" color=").Append(p.Color);
            sb.Append('\n');
            foreach ((string channel, string row) in p.Rows)
            {
                if (p.RowNotes is not null && p.RowNotes.TryGetValue(channel, out IReadOnlyList<string>? rowNotes)) Notes(sb, rowNotes, "  ");
                sb.Append("  ").Append(channel).Append(' ').Append(row).Append('\n');
            }
        }

        if (Clips.Count > 0 || Sections.Count > 0 || Markers.Count > 0 || Loop is not null)
        {
            Notes(sb, PlaylistNotes, string.Empty);
            sb.Append("playlist\n");
            foreach (SeqClip c in Clips)
            {
                Notes(sb, c.Notes, "  ");
                sb.Append("  clip ").Append(c.Pattern).Append(" track=").Append(c.Track.ToString(CultureInfo.InvariantCulture)).Append(" at=").Append(c.At.ToString(CultureInfo.InvariantCulture)).Append(" for=").Append(c.Bars.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            foreach (SeqSection s in Sections)
            {
                Notes(sb, s.Notes, "  ");
                sb.Append("  section ").Append(s.Name).Append(" at=").Append(s.At.ToString(CultureInfo.InvariantCulture)).Append(" for=").Append(s.Bars.ToString(CultureInfo.InvariantCulture)).Append(s.Color.Length > 0 ? " color=" + s.Color : string.Empty).Append('\n');
            }

            foreach (SeqMarker m in Markers)
            {
                Notes(sb, m.Notes, "  ");
                sb.Append("  marker ").Append(m.Name).Append(" at=").Append(m.At.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            if (Loop is { } l)
            {
                Notes(sb, l.Notes, "  ");
                sb.Append("  loop at=").Append(l.At.ToString(CultureInfo.InvariantCulture)).Append(" for=").Append(l.Bars.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        Notes(sb, Footer, string.Empty);
        return sb.ToString();
    }

    /// <summary>The lanes of the block: <c>lane1=kick lane2=hat+snare lane3=all</c>; a lane takes channels joined by + or the word all.</summary>
    /// <exception cref="AmpleException">AMP5380 when a lane or a channel in it is unknown.</exception>
    public IReadOnlyDictionary<int, string[]> ParseLanes(string text)
    {
        Dictionary<int, string[]> lanes = [];
        foreach (string part in text.Trim().Trim('"').Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string key = eq > 0 ? part[..eq] : string.Empty;
            if (!key.StartsWith("lane", StringComparison.Ordinal) || !int.TryParse(key[4..], NumberStyles.None, CultureInfo.InvariantCulture, out int lane) || lane is < 1 or > LaneCount)
                throw new AmpleException("AMP5380", $"The lanes '{text}' must look like lane1=kick lane2=hat+snare lane3=all (lanes 1 to {LaneCount}; channels joined by +; all for every channel).");
            string[] names = part[(eq + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries);
            foreach (string name in names)
            {
                if (name != "all" && Channels.All(c => c.Name != name)) throw new AmpleException("AMP5380", $"The lane {lane} names '{name}', which is not a channel. The channels are: {string.Join(", ", Channels.Select(c => c.Name))}.");
            }

            lanes[lane] = names.Contains("all") ? [.. Channels.Select(c => c.Name)] : names;
        }

        return lanes;
    }

    private static string SettingsText(IReadOnlyDictionary<string, string> settings) => string.Concat(settings.Select(kv => " " + kv.Key + "=" + (kv.Value.Any(char.IsWhiteSpace) || kv.Value.Length == 0 ? "\"" + kv.Value + "\"" : kv.Value)));

    private static bool Name(string text) => text.Length is >= 1 and <= 60 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool Int(Dictionary<string, string> settings, string key, out int value)
    {
        value = 0;
        return settings.TryGetValue(key, out string? text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 99999;
    }

    private static Dictionary<string, string> Settings(IEnumerable<string> words, int number)
    {
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        foreach (string word in words)
        {
            int eq = word.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw Bad(number, $"'{word}' must look like key=value");
            settings[word[..eq]] = word[(eq + 1)..].Trim('"');
        }

        return settings;
    }

    // words split on white space; a quoted value (key="a b") keeps its spaces
    private static string[] Words(string line)
    {
        List<string> words = [];
        StringBuilder current = new();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); }
            else if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } }
            else current.Append(c);
        }

        if (current.Length > 0) words.Add(current.ToString());
        return [.. words];
    }

    private static AmpleException Bad(int line, string message) => new("AMP5380", $"Sequence file, line {line}: {message}.");
}
