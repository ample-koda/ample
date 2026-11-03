using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ample.Audio.Engine;

/// <summary>
/// GN01, small deterministic generators for an assistant: euclidean rhythms, drum grooves with swing and ghost notes, arpeggios, basslines and fills. The same query (and seed) always
/// gives the same answer. Drum output uses the drums block grid (16 steps per bar; x hit, X accent, o ghost, . rest); note output uses the melody text of a midi-clip
/// (<c>name:beats</c>, for example <c>a3:0.5 c4:0.5 e4:1</c>). Nothing here calls a model.
/// </summary>
public static partial class PatternGenerators
{
    private static readonly Dictionary<string, int[]> Qualities = new(StringComparer.Ordinal)
    {
        [""] = [0, 4, 7], ["maj"] = [0, 4, 7], ["m"] = [0, 3, 7], ["min"] = [0, 3, 7], ["7"] = [0, 4, 7, 10], ["maj7"] = [0, 4, 7, 11], ["m7"] = [0, 3, 7, 10], ["dim"] = [0, 3, 6],
        ["sus4"] = [0, 5, 7], ["sus2"] = [0, 2, 7], ["5"] = [0, 7], ["6"] = [0, 4, 7, 9], ["m6"] = [0, 3, 7, 9], ["add9"] = [0, 4, 7, 14], ["9"] = [0, 4, 7, 10, 14],
    };

    private static readonly Dictionary<string, (string Kick, string Snare, string Hat, double Swing, string Say)> Styles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fourfloor"] = ("x...x...x...x...", "....x.......x...", "..x...x...x...x.", 0, "four on the floor, snare on 2 and 4, off-beat hat"),
        ["backbeat"] = ("x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", 0, "kick on 1 and 3, snare on 2 and 4, eighth hats"),
        ["halftime"] = ("x.......x.x.....", "........x.......", "x.x.x.x.x.x.x.x.", 0, "snare on beat 3 only, a slow heavy feel"),
        ["boombap"] = ("x..x......x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", 55, "swung boom bap"),
        ["shuffle"] = ("x.......x.......", "....x.......x...", "x.xx.xx.xx.xx.xx", 66, "triplet shuffle"),
        ["breakbeat"] = ("x.x.......x.....", "....x..o.o..x...", "x.x.x.x.x.x.x.x.", 0, "chopped break with ghost snares"),
        ["reggae"] = ("........x.......", "........x.......", "x.x.x.x.x.x.x.x.", 0, "one-drop"),
        ["funk"] = ("x..x..x...x.....", "....x..o.o..x..o", "x.x.x.x.x.x.x.x.", 10, "syncopated kick and ghost snares"),
        ["dembow"] = ("x...x...x...x...", "...x..x....x..x.", "x.x.x.x.x.x.x.x.", 0, "dembow"),
    };

    /// <summary>One request: <c>kind</c> is euclid, groove, arpeggio, bassline or fill (empty: what exists). Numbers may arrive as text.</summary>
    public static JsonObject Run(JsonObject q)
    {
        try
        {
            return Text(q, "kind").ToLowerInvariant() switch
            {
                "euclid" => Euclid(q),
                "groove" => Groove(q),
                "arpeggio" => Arpeggio(q),
                "bassline" => Bassline(q),
                "fill" => Fill(q),
                _ => Overview(),
            };
        }
        catch (FormatException ex)
        {
            return new JsonObject { ["error"] = ex.Message, ["see"] = Overview() };
        }
    }

    private static JsonObject Overview() => new()
    {
        ["kinds"] = new JsonObject
        {
            ["euclid"] = "steps (default 16), hits, rotate (default 0), symbol (default x): a euclidean rhythm as a grid, for example hits=3 steps=8 is the tresillo x..x..x.",
            ["groove"] = "style (" + string.Join(", ", Styles.Keys) + "), variation 0-1 (ghost notes and extra kicks), swing 0-60 (default of the style), seed: a kick, snare and hat grid and a drums block to paste",
            ["arpeggio"] = "chord or progression (Am F C G; qualities m, 7, maj7, m7, dim, sus4, sus2, 5, 6, add9, 9), pattern (up, down, updown, broken, thirds, random), octave (root octave, default 3), octaves (1-3), rate (notes per beat, default 2), barsPerChord (default 1), seed: melody text for a midi-clip",
            ["bassline"] = "progression, rhythm (a 16-step grid, default x..x..x...x..x..), style (root, rootfifth, octave, walking), octave (default 2), seed: melody text for a bass midi-clip, one bar per chord",
            ["fill"] = "style (snare-roll, tom-run, kick-snare, hat-roll), steps (4, 8 or 16, default 8: the last steps of the bar), voices (the voices the chosen kit has, default kick,snare,hat: tom-run uses snare when there is no tom; a missing sample is error AMP5350), seed: voice grids for the last bar of a section",
        },
        ["note"] = "Deterministic: the same query and seed give the same result. Drum grids are 16 steps per bar; melody text is name:beats (c4 is middle C).",
    };

    private static string Text(JsonObject q, string key) => q[key] is JsonValue v ? v.ToString().Trim() : string.Empty;

    private static int Whole(JsonObject q, string key, int fallback, int min, int max)
    {
        string text = Text(q, key);
        if (text.Length == 0) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) throw new FormatException($"{key} must be a whole number.");
        return Math.Clamp(n, min, max);
    }

    private static double Number(JsonObject q, string key, double fallback, double min, double max)
    {
        string text = Text(q, key);
        if (text.Length == 0) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) throw new FormatException($"{key} must be a number.");
        return Math.Clamp(n, min, max);
    }

    // a tiny deterministic generator (xorshift32): the same seed always gives the same numbers
    private sealed class Rng(uint seed)
    {
        private uint state = seed == 0 ? 2463534242u : seed;

        public double Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state / (double)uint.MaxValue;
        }

        public int Pick(int count) => Math.Min(count - 1, (int)(Next() * count));
    }

    private static Rng Seeded(JsonObject q) => new((uint)Whole(q, "seed", 1, 0, int.MaxValue));

    private static JsonObject Euclid(JsonObject q)
    {
        int steps = Whole(q, "steps", 16, 1, 64), hits = Whole(q, "hits", 4, 0, steps), rotate = Whole(q, "rotate", 0, 0, 63);
        string symbol = Text(q, "symbol").Length == 1 ? Text(q, "symbol") : "x";
        char[] grid = new char[steps];
        for (int i = 0; i < steps; i++) grid[(i + steps - (rotate % steps)) % steps] = (i * hits) % steps < hits ? symbol[0] : '.';
        string text = new(grid);
        JsonObject result = new() { ["steps"] = steps, ["hits"] = hits, ["rotate"] = rotate, ["grid"] = text };
        if (steps != 16) result["note"] = steps < 16 ? "A grid shorter than 16 repeats inside the bar (a drums block repeats it)." : "More than 16 steps spans more than one bar: split it into 16-step parts.";
        return result;
    }

    private static JsonObject Groove(JsonObject q)
    {
        string style = Text(q, "style").Length == 0 ? "backbeat" : Text(q, "style");
        if (!Styles.TryGetValue(style, out (string Kick, string Snare, string Hat, double Swing, string Say) basis)) throw new FormatException($"Unknown style '{style}'. Known: {string.Join(", ", Styles.Keys)}.");
        double variation = Number(q, "variation", 0.3, 0, 1);
        int swing = (int)Math.Round(Number(q, "swing", basis.Swing, 0, 60));
        Rng rng = Seeded(q);
        char[] kick = [.. basis.Kick], snare = [.. basis.Snare], hat = [.. basis.Hat];
        for (int step = 0; step < 16; step++)
        {
            bool offbeat = step % 2 == 1;
            if (kick[step] == '.' && offbeat && step % 4 != 0 && rng.Next() < variation * 0.25) kick[step] = 'x';
            if (snare[step] == '.' && offbeat && step % 4 == 3 && rng.Next() < variation * 0.35) snare[step] = 'o';
            if (hat[step] == '.' && rng.Next() < variation * 0.2) hat[step] = 'o';
        }

        string pattern = $"kick:{new string(kick)} snare:{new string(snare)} hat:{new string(hat)}";
        string block = $"drums beat at 1 for 4 {{\n  kit samples/<your-kit>\n  pattern {pattern}\n  swing {swing}\n  humanize 15\n}}";
        return new JsonObject { ["style"] = style, ["say"] = basis.Say, ["pattern"] = pattern, ["swing"] = swing, ["drumsBlock"] = block, ["note"] = "A ghost note (o) is soft; a hat marked o is a quiet hit. Change seed for another variation of the same style. In a song, `pattern` is a line inside a drums block (see drumsBlock), not a block type: do not look it up with ample_blocks." };
    }

    [GeneratedRegex("^([A-Ga-g])([#b]?)(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChordPattern();

    private static readonly Dictionary<char, int> Roots = new() { ['c'] = 0, ['d'] = 2, ['e'] = 4, ['f'] = 5, ['g'] = 7, ['a'] = 9, ['b'] = 11 };

    // chord symbol -> MIDI numbers of its tones with the root in the given octave (c4 = 60)
    private static (int Root, int[] Tones) Chord(string symbol, int octave)
    {
        Match match = ChordPattern().Match(symbol.Trim());
        if (!match.Success) throw new FormatException($"'{symbol}' is not a chord (examples: Am, C, F#m7, Gsus4).");
        int root = Roots[char.ToLowerInvariant(match.Groups[1].Value[0])] + (match.Groups[2].Value == "#" ? 1 : match.Groups[2].Value == "b" ? -1 : 0);
        string quality = match.Groups[3].Value;
        if (!Qualities.TryGetValue(quality, out int[]? intervals)) throw new FormatException($"Unknown chord quality '{quality}' in '{symbol}'. Known: m, 7, maj7, m7, dim, sus4, sus2, 5, 6, m6, add9, 9.");
        int baseNote = ((octave + 1) * 12) + root;
        return (baseNote, [.. intervals.Select(i => baseNote + i)]);
    }

    private static string[] Progression(JsonObject q, string fallback)
    {
        string text = Text(q, "progression").Length > 0 ? Text(q, "progression") : Text(q, "chord").Length > 0 ? Text(q, "chord") : fallback;
        return text.Split([' ', ',', '-', '|'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Beats(double beats) => beats.ToString("0.###", CultureInfo.InvariantCulture);

    private static JsonObject Arpeggio(JsonObject q)
    {
        string[] chords = Progression(q, "Am F C G");
        string pattern = Text(q, "pattern").Length == 0 ? "up" : Text(q, "pattern").ToLowerInvariant();
        int octave = Whole(q, "octave", 3, 1, 6), octaves = Whole(q, "octaves", 1, 1, 3), barsPerChord = Whole(q, "barsPerChord", 1, 1, 4);
        double rate = Number(q, "rate", 2, 0.5, 8);
        Rng rng = Seeded(q);
        double length = 1 / rate;
        int notesPerBar = (int)Math.Round(4 * rate);
        List<string> notes = [];
        foreach (string symbol in chords)
        {
            (_, int[] tones) = Chord(symbol, octave);
            List<int> pool = [];
            for (int o = 0; o < octaves; o++) pool.AddRange(tones.Select(t => t + (12 * o)));
            for (int bar = 0; bar < barsPerChord; bar++)
            {
                for (int n = 0; n < notesPerBar; n++)
                {
                    int index = pattern switch
                    {
                        "down" => pool.Count - 1 - (n % pool.Count),
                        "updown" => UpDown(n, pool.Count),
                        "broken" => new[] { 0, 2, 1, 3 }[n % 4] % pool.Count,
                        "thirds" => ((n * 2) % pool.Count),
                        "random" => rng.Pick(pool.Count),
                        _ => n % pool.Count,
                    };
                    notes.Add(AudioAnalyzer.NoteName(pool[index]) + ":" + Beats(length));
                }
            }
        }

        int bars = chords.Length * barsPerChord;
        string melody = string.Join(' ', notes);
        return new JsonObject
        {
            ["progression"] = string.Join(' ', chords),
            ["pattern"] = pattern,
            ["bars"] = bars,
            ["melody"] = melody,
            ["midiClip"] = $"midi-clip arp {{\n  melody \"{melody}\"\n  bars {bars}\n}}",
            ["note"] = "The melody text is name:beats; play it with a piano, guitar or synth block (from arp.notes -> notes). Octave 3 puts the chord root around the middle of the keyboard.",
        };
    }

    private static int UpDown(int n, int count)
    {
        if (count == 1) return 0;
        int cycle = (2 * count) - 2, p = n % cycle;
        return p < count ? p : cycle - p;
    }

    private static JsonObject Bassline(JsonObject q)
    {
        string[] chords = Progression(q, "Am F C G");
        string rhythm = Text(q, "rhythm").Length == 0 ? "x..x..x...x..x.." : Text(q, "rhythm");
        if (rhythm.Length != 16 || rhythm.Any(c => c is not ('x' or 'X' or '.' or 'o'))) throw new FormatException("rhythm must be 16 characters of x (note) and . (hold), for example x..x..x...x..x..");
        string style = Text(q, "style").Length == 0 ? "root" : Text(q, "style").ToLowerInvariant();
        int octave = Whole(q, "octave", 2, 0, 4);
        Rng rng = Seeded(q);
        char[] grid = [.. rhythm];
        grid[0] = 'x';
        List<int> starts = [.. Enumerable.Range(0, 16).Where(i => grid[i] != '.')];
        List<string> notes = [];
        for (int c = 0; c < chords.Length; c++)
        {
            (int root, int[] tones) = Chord(chords[c], octave);
            for (int s = 0; s < starts.Count; s++)
            {
                int pitch = style switch
                {
                    "rootfifth" => s % 2 == 0 ? root : root + 7,
                    "octave" => s % 2 == 0 ? root : root + 12,
                    "walking" => s == 0 ? root : tones[rng.Pick(tones.Length)],
                    _ => root,
                };
                int end = s + 1 < starts.Count ? starts[s + 1] : 16;
                notes.Add(AudioAnalyzer.NoteName(pitch) + ":" + Beats((end - starts[s]) * 0.25));
            }
        }

        string melody = string.Join(' ', notes);
        return new JsonObject
        {
            ["progression"] = string.Join(' ', chords),
            ["style"] = style,
            ["rhythm"] = new string(grid),
            ["bars"] = chords.Length,
            ["melody"] = melody,
            ["midiClip"] = $"midi-clip bassline {{\n  melody \"{melody}\"\n  bars {chords.Length}\n}}",
            ["note"] = "One chord per bar; each note lasts until the next one. Feed it to a bass block (from bassline.notes -> notes); the kick of the drums should land on the same steps.",
        };
    }

    private static JsonObject Fill(JsonObject q)
    {
        string style = Text(q, "style").Length == 0 ? "snare-roll" : Text(q, "style").ToLowerInvariant();
        int steps = Whole(q, "steps", 8, 4, 16);
        if (steps is not (4 or 8 or 16)) steps = 8;
        Rng rng = Seeded(q);
        string Tail(char symbol, double density, string? fixedFirst = null)
        {
            char[] g = new char[16];
            Array.Fill(g, '.');
            for (int i = 16 - steps; i < 16; i++) g[i] = rng.Next() < density ? symbol : '.';
            if (fixedFirst is not null) g[16 - steps] = fixedFirst[0];
            g[15] = symbol;
            return new string(g);
        }

        string[] voices = (Text(q, "voices").Length == 0 ? "kick,snare,hat" : Text(q, "voices")).ToLowerInvariant().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        string tomVoice = voices.Contains("tom") ? "tom" : voices.Contains("snare") ? "snare" : voices[0];
        string pattern = style switch
        {
            "tom-run" => $"kick:x............... {tomVoice}:{Tail('x', 0.9)}",
            "kick-snare" => $"kick:{Tail('x', 0.5)} snare:{Tail('x', 0.5)}",
            "hat-roll" => $"kick:x............... hat:{Tail('x', 0.95, "x")}",
            _ => $"kick:x............... snare:{Tail('x', 0.9)}",
        };
        return new JsonObject
        {
            ["style"] = style,
            ["steps"] = steps,
            ["pattern"] = pattern,
            ["use"] = "Put it in a named pattern (patterns fill=<this>) and play it on the last bar of a section: song verse*3 fill.",
        };
    }
}
