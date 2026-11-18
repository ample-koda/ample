using System.Globalization;

namespace Ample.Language;

/// <summary>
/// The shorthand of a midi-clip as notes: a melody (<c>c4:1 d4:1 e4:2 r:1</c>) and chord symbols (<c>Am F.G C/E</c>). The build (BuiltInRenderers, the midi-clip renderer) and the
/// note pipelines (a clip written with melody or chords read as a source, NoteFlow) use this one implementation, so a pipeline reads exactly the notes the clip plays.
/// Moved verbatim from the renderer (2026-11-17).
/// </summary>
public static class ClipShorthand
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Parses a chord name (<c>Am</c>, <c>F</c>, <c>C#m</c>, <c>Bb</c>) into a triad of MIDI notes around C3.</summary>
    public static int[] Triad(string chord)
    {
        string root = chord.Length > 1 && chord[1] is '#' or 'b' ? chord[..2] : chord[..1];
        bool minor = chord[root.Length..].StartsWith('m');
        int index = root.EndsWith('b') ? (Array.IndexOf(Names, root[..1]) + 11) % 12 : Array.IndexOf(Names, root);
        if (index < 0)
        {
            index = 0;
        }

        int baseNote = 48 + index;
        return [baseNote, baseNote + (minor ? 3 : 4), baseNote + 7];
    }

    /// <summary>
    /// The tones of one chord symbol: the root and quality of <see cref="NoteTheory.TryChord"/> (Am, F, G7, Cmaj7, Dsus4 ...), and a slash chord <c>C/E</c> puts the named bass note lowest
    /// (an inversion when it is a tone of the chord, otherwise an extra bass note below the root). A symbol that is not known falls back to <see cref="Triad"/>, so older songs sound the same.
    /// </summary>
    public static int[] ChordTones(string symbol)
    {
        string main = symbol, bass = string.Empty;
        int slash = symbol.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0) { main = symbol[..slash]; bass = symbol[(slash + 1)..]; }
        if (!NoteTheory.TryChord(main, out int pc, out int[] intervals)) return Triad(main);
        int root = 48 + pc;
        int[] tones = [.. intervals.Select(i => root + i)];
        if (bass.Length == 0) return tones;
        if (!NoteTheory.TryRoot(bass, out int bassClass)) return tones;
        int at = Array.FindIndex(tones, x => ((x % 12) + 12) % 12 == bassClass);
        if (at >= 0)
        {
            // inversion: the tones below the bass note move up an octave
            return [.. tones.Select((x, i) => i < at ? x + 12 : x).Order()];
        }

        int low = 48 + bassClass;
        while (low >= tones.Min()) low -= 12;
        return [low, .. tones];
    }

    /// <summary>The chords of one bar of the request (bar counted as the renderer counts it): the symbol of <c>chords[(bar - 1) % count]</c>, or several joined by <c>.</c> (<c>Am.F</c> is two chords of two beats) that share the bar evenly.</summary>
    public static IEnumerable<(int[] Notes, double Beat, double Beats)> ChordsOfBar(IReadOnlyList<string> chords, int bar, double beatsPerBar)
    {
        string[] parts = chords[(bar - 1) % chords.Count].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) parts = ["C"];
        double each = beatsPerBar / parts.Length;
        return parts.Select((p, i) => (ChordTones(p), i * each, each));
    }

    /// <summary>
    /// The notes a midi-clip written with <c>melody</c> or <c>chords</c> plays, as compact note text (<c>bar:beat:length:pitch:velocity</c>, bars of the clip from 1), the same notes
    /// the midi-clip renderer makes for a clip that is not placed: a melody wins over chords, a melody stops at the end of the clip, chords repeat bar by bar over the clip.
    /// <paramref name="bars"/> is the length of the clip in bars, or null when the clip does not say it (a melody then runs to its end, chords play their list once).
    /// Null when the clip has neither (a clip with notes is read as notes).
    /// </summary>
    public static string? ClipNotes(string? melody, string? chords, int? bars, double beatsPerBar = 4.0)
    {
        static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        List<string> entries = [];
        string tune = (melody ?? string.Empty).Trim().Trim('"');
        if (tune.Length > 0)
        {
            foreach ((int bar, int pitch, double beat, double beats, int velocity) in MelodyWithVelocity(tune, 1, bars ?? int.MaxValue, beatsPerBar))
            {
                entries.Add($"{bar}:{N(beat)}:{N(beats)}:{pitch}:{velocity}");
            }

            return string.Join(' ', entries);
        }

        List<string> list = [.. (chords ?? string.Empty).Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        if (list.Count == 0) return null;
        for (int bar = 1; bar <= (bars ?? list.Count); bar++)
        {
            foreach ((int[] notes, double beat, double beats) in ChordsOfBar(list, bar, beatsPerBar))
            {
                entries.AddRange(notes.Select(pitch => $"{bar}:{N(beat)}:{N(beats)}:{pitch}:96"));
            }
        }

        return string.Join(' ', entries);
    }

    /// <summary>Parses a melody such as <c>c4:1 d4:1 e4:2 r:1</c> (note name, optional # or b, octave, then beats; r is a rest, 1 beat when no length is given) into notes per bar. A note stops at the end of its bar.</summary>
    public static IEnumerable<(int Bar, int Pitch, double Beat, double Beats)> Melody(string text, int firstBar, int barCount, double beatsPerBar = 4.0) =>
        MelodyWithVelocity(text, firstBar, barCount, beatsPerBar).Select(n => (n.Bar, n.Pitch, n.Beat, n.Beats));

    /// <summary>
    /// The length of a melody in beats, rests included (a trailing rest is part of the melody: <c>c4:1 r:3</c> is 4 beats long). Dynamic markers and hairpins take no time. Not cut at a clip length.
    /// </summary>
    public static double MelodyBeats(string text)
    {
        double at = 0;
        foreach (string token in (text ?? string.Empty).Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            string name = parts[0].ToLowerInvariant();
            if (parts.Length == 1 && (DynamicMarkers.ContainsKey(name) || name is "<" or ">")) continue;
            at += parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 ? b : 1;
        }

        return at;
    }

    /// <summary>The velocity of a dynamic marker word in a melody: <c>pp p mp mf f ff</c> (32 to 112 in steps of 16; <c>f</c> is the default of 96).</summary>
    public static readonly IReadOnlyDictionary<string, int> DynamicMarkers = new Dictionary<string, int>(StringComparer.Ordinal) { ["pp"] = 32, ["p"] = 48, ["mp"] = 64, ["mf"] = 80, ["f"] = 96, ["ff"] = 112 };

    /// <summary>Like <see cref="Melody"/>, with a MIDI velocity (1 to 127, default 96). A note can carry its own as a third field (<c>c4:1:110</c>); a marker word (<c>pp p mp mf f ff</c>, see <see cref="DynamicMarkers"/>) sets the velocity of the notes after it; <c>&lt;</c> (crescendo) or <c>&gt;</c> (decrescendo) ramps the notes after it, by their place in time, to the next marker word (to <c>ff</c> or <c>pp</c> at the end of the melody when none follows). A note with its own velocity is never changed by a marker. Markers take no time.</summary>
    public static IEnumerable<(int Bar, int Pitch, double Beat, double Beats, int Velocity)> MelodyWithVelocity(string text, int firstBar, int barCount, double beatsPerBar = 4.0)
    {
        List<(int Bar, int Pitch, double Beat, double Beats, int Velocity)> result = [];
        List<double> starts = [];
        double at = 0;
        int current = 96;
        double hairAt = 0;
        int hairFrom = 0, hairDir = 0;
        List<int> hairNotes = [];

        void Resolve(int target, double endAt)
        {
            double span = endAt - hairAt;
            foreach (int index in hairNotes)
            {
                double t = span > 0 ? Math.Clamp((starts[index] - hairAt) / span, 0, 1) : 0;
                (int bar, int pitch, double beat, double beats, _) = result[index];
                result[index] = (bar, pitch, beat, beats, Math.Clamp((int)Math.Round(hairFrom + ((target - hairFrom) * t), MidpointRounding.AwayFromZero), 1, 127));
            }

            hairNotes = [];
            hairDir = 0;
        }

        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            string name = parts[0].ToLowerInvariant();
            if (parts.Length == 1 && DynamicMarkers.TryGetValue(name, out int marker))
            {
                if (hairDir != 0) Resolve(marker, at);
                current = marker;
                continue;
            }

            if (parts.Length == 1 && name is "<" or ">")
            {
                if (hairDir != 0) Resolve(hairDir > 0 ? 112 : 32, at);
                hairDir = name == "<" ? 1 : -1;
                hairAt = at;
                hairFrom = current;
                continue;
            }

            double beats = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 ? b : 1;
            int barIndex = (int)(at / beatsPerBar);
            if (barIndex >= barCount)
            {
                break;
            }

            if (name != "r" && name.Length >= 2 && "cdefgab".Contains(name[0]))
            {
                int semitone = name[0] switch { 'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, _ => 11 };
                int i = 1;
                if (name[i] == '#') { semitone++; i++; }
                else if (name[i] == 'b' && name.Length > i + 1) { semitone--; i++; }
                if (int.TryParse(name[i..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
                {
                    double beat = at - (barIndex * beatsPerBar);
                    bool own = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
                    int velocity = own ? Math.Clamp(int.Parse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture), 1, 127) : current;
                    if (hairDir != 0 && !own) hairNotes.Add(result.Count);
                    starts.Add(at);
                    result.Add((firstBar + barIndex, Math.Clamp(12 * (octave + 1) + semitone, 0, 127), beat, Math.Min(beats, beatsPerBar - beat), velocity));
                }
            }

            at += beats;
        }

        if (hairDir != 0) Resolve(hairDir > 0 ? 112 : 32, at);
        return result;
    }
}
