using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>One hand fix of the autotune window: from <paramref name="From"/> to <paramref name="To"/> (samples of the block) the voice goes to <paramref name="Note"/> (a MIDI number), or is left alone when it is null.</summary>
/// <param name="From">Start, in samples of the block.</param>
/// <param name="To">End, in samples of the block.</param>
/// <param name="Note">The note to sing there (60 is C4), or null: no correction there.</param>
/// <param name="Vibrato">How much of the vibrato stays there (0 to 100), or null: the setting of the block.</param>
public sealed record AutotuneFix(long From, long To, int? Note, int? Vibrato);

/// <summary>Reads the <c>fixes</c> text of an autotune block: <c>4.2s-4.7s E4; 7.9s-8.4s G4 vibrato 0%; 12s-13s off</c>.</summary>
public static partial class AutotuneFixes
{
    private const string Code = "AMP5397";

    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Reads the fixes. A time is seconds (<c>6s</c>), milliseconds (<c>500ms</c>) or decimal bars from the start of the block (<c>4.5</c>).</summary>
    /// <param name="text">The text of the parameter (empty: no fixes).</param>
    /// <param name="rate">Samples per second.</param>
    /// <param name="samplesPerBar">Samples in one bar.</param>
    /// <returns>The fixes in the order of their start.</returns>
    public static IReadOnlyList<AutotuneFix> Parse(string text, int rate, int samplesPerBar)
    {
        List<AutotuneFix> fixes = [];
        foreach (string chunk in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            string[] words = chunk.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string[] span = words[0].Split('-', 2);
            if (span.Length != 2 || words.Length < 2)
            {
                throw Bad($"'{chunk}' is not a fix: write a time span and a note, such as 4.2s-4.7s E4 (or off to leave it alone).");
            }

            long from = Time(span[0], rate, samplesPerBar, chunk), to = Time(span[1], rate, samplesPerBar, chunk);
            if (to <= from)
            {
                throw Bad($"The span of '{chunk}' ends before it starts.");
            }

            int? note = words[1].Equals("off", StringComparison.OrdinalIgnoreCase) ? null : Note(words[1]) ?? throw Bad($"'{words[1]}' in '{chunk}' is not a note: write a name and an octave, such as E4, F#3 or Bb2, or off.");
            int? vibrato = null;
            for (int i = 2; i < words.Length; i += 2)
            {
                if (!words[i].Equals("vibrato", StringComparison.OrdinalIgnoreCase) || i + 1 >= words.Length || !double.TryParse(words[i + 1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    throw Bad($"'{words[i]}' in '{chunk}' is not known: after the note only vibrato 0% to 100% may follow.");
                }

                vibrato = (int)Math.Clamp(Math.Round(v), 0, 100);
            }

            fixes.Add(new AutotuneFix(from, to, note, vibrato));
        }

        return [.. fixes.OrderBy(f => f.From)];
    }

    /// <summary>The MIDI number of a note name such as <c>E4</c>, <c>F#3</c> or <c>Bb2</c> (C4 is 60), or null.</summary>
    public static int? Note(string word)
    {
        Match m = NoteWord().Match(word);
        if (!m.Success)
        {
            return null;
        }

        int pc = char.ToUpperInvariant(m.Groups[1].Value[0]) switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
        pc += m.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 };
        int note = ((int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + 1) * 12) + pc;
        return note is >= 0 and <= 127 ? note : null;
    }

    /// <summary>The name of a MIDI number (60 is C4).</summary>
    public static string Name(int note) => Names[((note % 12) + 12) % 12] + ((note / 12) - 1).ToString(CultureInfo.InvariantCulture);

    private static long Time(string word, int rate, int samplesPerBar, string chunk)
    {
        Match m = TimeWord().Match(word);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0)
        {
            throw Bad($"'{word}' in '{chunk}' is not a time: write seconds (6s), milliseconds (500ms) or bars (4.5).");
        }

        return (long)Math.Round(m.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => number * rate,
            "ms" => number * rate / 1000,
            _ => number * samplesPerBar,
        });
    }

    private static AmpleException Bad(string message) => new(Code, message);

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(s|ms|bars?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimeWord();

    [GeneratedRegex(@"^([A-Ga-g])(#|b)?(-?[0-9])$")]
    private static partial Regex NoteWord();
}

/// <summary>
/// The autotune: the pitch of one voice is followed (YIN, every 10 ms), every moment gets the note it should be (a note of the key, the note a melody plays at that time, or a hand fix),
/// and the voice is moved there by a pitch shift that changes from period to period (PSOLA, so the vowels keep their colour). Speed is how fast the correction follows a new note
/// (0 is the hard robot sound), humanize leaves slides and the start of a note more alone, and vibrato says how much of the wobble of the voice stays.
/// </summary>
public static class Autotune
{
    /// <summary>The steps of the scales (semitones above the root).</summary>
    private static readonly Dictionary<string, int[]> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["major"] = [0, 2, 4, 5, 7, 9, 11],
        ["minor"] = [0, 2, 3, 5, 7, 8, 10],
        ["harmonic-minor"] = [0, 2, 3, 5, 7, 8, 11],
        ["dorian"] = [0, 2, 3, 5, 7, 9, 10],
        ["mixolydian"] = [0, 2, 4, 5, 7, 9, 10],
        ["pentatonic"] = [0, 2, 4, 7, 9],
        ["minor-pentatonic"] = [0, 3, 5, 7, 10],
        ["blues"] = [0, 3, 5, 6, 7, 10],
        ["chromatic"] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
    };

    // the key profiles of Krumhansl and Kessler, for the key auto setting
    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>The settings of one autotune.</summary>
    /// <param name="Scale">The 12 pitch classes that are allowed (C is 0).</param>
    /// <param name="SpeedMs">How fast the correction follows (0: at once).</param>
    /// <param name="Amount">How much of the correction is used (0 to 1).</param>
    /// <param name="Humanize">How much slides and note starts are left alone (0 to 1).</param>
    /// <param name="Vibrato">How much of the vibrato stays (0 to 1).</param>
    public sealed record Settings(bool[] Scale, double SpeedMs, double Amount, double Humanize, double Vibrato);

    /// <summary>The pitch of a sound every <paramref name="Hop"/> samples, as a MIDI number with decimals (NaN where it is not voiced).</summary>
    /// <param name="Hop">Samples between two values.</param>
    /// <param name="Midi">The pitch per step.</param>
    public sealed record Track(int Hop, double[] Midi);

    /// <summary>Follows the pitch of a mono sound with YIN on a copy at about 11 kHz, every 10 ms, from 65 Hz to 1000 Hz; the period is then refined at the full rate and octave errors are moved back.</summary>
    public static Track Detect(short[] x, int rate, CancellationToken cancellationToken = default)
    {
        int d = Math.Max(1, rate / 11025);
        int sr = rate / d;
        double[] s = new double[x.Length / d + 1];
        for (int k = 0; k < s.Length; k++)
        {
            double sum = 0;
            int n = 0;
            for (int j = k * d; j < (k * d) + d && j < x.Length; j++, n++)
            {
                sum += x[j];
            }

            s[k] = n > 0 ? sum / n / 32768.0 : 0;
        }

        int w = sr * 40 / 1000, minLag = Math.Max(2, sr / 1000), maxLag = sr / 65, hop = Math.Max(1, sr / 100);
        int frames = (s.Length / hop) + 1;
        double[] midi = new double[frames];
        double[] diff = new double[maxLag + 2];
        double floor = Math.Pow(10, -50.0 / 20);   // quieter than -50 dBFS is not voiced
        for (int f = 0; f < frames; f++)
        {
            if ((f & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            midi[f] = double.NaN;
            int start = (f * hop) - ((w + maxLag) / 2);
            double At(int i) => i >= 0 && i < s.Length ? s[i] : 0;
            double energy = 0;
            for (int j = 0; j < w; j++)
            {
                energy += At(start + j) * At(start + j);
            }

            if (Math.Sqrt(energy / w) < floor)
            {
                continue;
            }

            double running = 0;
            int best = -1;
            for (int tau = 1; tau <= maxLag; tau++)
            {
                double sum = 0;
                for (int j = 0; j < w; j++)
                {
                    double delta = At(start + j) - At(start + j + tau);
                    sum += delta * delta;
                }

                running += sum;
                diff[tau] = running > 0 ? sum * tau / running : 1;
            }

            // the first dip under the threshold, followed down to its lowest point; otherwise the lowest point overall when it is low enough
            for (int tau = minLag; tau <= maxLag; tau++)
            {
                if (diff[tau] < 0.15)
                {
                    while (tau + 1 <= maxLag && diff[tau + 1] < diff[tau])
                    {
                        tau++;
                    }

                    best = tau;
                    break;
                }
            }

            if (best < 0)
            {
                int lowest = minLag;
                for (int tau = minLag; tau <= maxLag; tau++)
                {
                    if (diff[tau] < diff[lowest])
                    {
                        lowest = tau;
                    }
                }

                best = diff[lowest] < 0.3 ? lowest : -1;
            }

            if (best < 0)
            {
                continue;
            }

            // a weak fundamental (a formant voice): when the period found is twice the true one, half of it is (nearly) as good a period: take it
            int half = (best + 1) / 2;
            if (half >= minLag)
            {
                int h = half;
                for (int k = Math.Max(minLag, half - 1); k <= Math.Min(maxLag, half + 1); k++)
                {
                    if (diff[k] < diff[h])
                    {
                        h = k;
                    }
                }

                if (diff[h] < 0.3 && diff[h] < (2.5 * diff[best]) + 0.08)
                {
                    best = h;
                }
            }

            double refined = best;
            if (best > 1 && best < maxLag)
            {
                double a = diff[best - 1], b = diff[best], c = diff[best + 1], bend = a - (2 * b) + c;
                if (bend > 0)
                {
                    refined = best + (0.5 * (a - c) / bend);
                }
            }

            midi[f] = 69 + (12 * Math.Log2(rate / Refine(x, (f * hop * d) - ((w * d) / 2), w * d, refined * d, d) / 440.0));
        }

        Clean(midi);
        FixOctaves(midi);
        return new Track(hop * d, midi);
    }

    // the period at the full rate: the difference of the window with itself one period later is lowest at the true period; only lags close to the coarse one are tried
    private static double Refine(short[] x, int start, int length, double coarse, int reach)
    {
        int from = Math.Max(2, (int)Math.Floor(coarse) - reach - 1), to = (int)Math.Ceiling(coarse) + reach + 1;
        double[] difference = new double[to - from + 1];
        for (int lag = from; lag <= to; lag++)
        {
            double sum = 0;
            for (int j = 0; j < length; j++)
            {
                int a = start + j, b = a + lag;
                double va = a >= 0 && a < x.Length ? x[a] : 0, vb = b >= 0 && b < x.Length ? x[b] : 0;
                sum += (va - vb) * (va - vb);
            }

            difference[lag - from] = sum;
        }

        int best = 0;
        for (int i = 1; i < difference.Length; i++)
        {
            if (difference[i] < difference[best])
            {
                best = i;
            }
        }

        if (best == 0 || best == difference.Length - 1)
        {
            return coarse;   // the lowest point is at the edge: keep the coarse period
        }

        double left = difference[best - 1], middle = difference[best], right = difference[best + 1], bend = left - (2 * middle) + right;
        return from + best + (bend > 0 ? 0.5 * (left - right) / bend : 0);
    }

    // a value an octave away from the voice around it (the median of the voiced steps within 100 ms) is an octave error of the tracking: it is moved back
    private static void FixOctaves(double[] midi)
    {
        double[] copy = (double[])midi.Clone();
        for (int f = 0; f < midi.Length; f++)
        {
            if (double.IsNaN(copy[f]))
            {
                continue;
            }

            List<double> around = [];
            for (int k = f - 10; k <= f + 10; k++)
            {
                if (k != f && k >= 0 && k < copy.Length && !double.IsNaN(copy[k]))
                {
                    around.Add(copy[k]);
                }
            }

            if (around.Count < 4)
            {
                continue;
            }

            around.Sort();
            double gap = copy[f] - around[around.Count / 2];
            if (Math.Abs(gap - 12) < 1)
            {
                midi[f] -= 12;
            }
            else if (Math.Abs(gap + 12) < 1)
            {
                midi[f] += 12;
            }
        }
    }

    // a median of three takes out single wrong values (an octave jump), and a voiced run shorter than 30 ms is not trusted
    private static void Clean(double[] midi)
    {
        double[] copy = (double[])midi.Clone();
        for (int f = 1; f + 1 < midi.Length; f++)
        {
            if (!double.IsNaN(copy[f - 1]) && !double.IsNaN(copy[f]) && !double.IsNaN(copy[f + 1]))
            {
                double[] three = [copy[f - 1], copy[f], copy[f + 1]];
                Array.Sort(three);
                midi[f] = three[1];
            }
        }

        for (int f = 0; f < midi.Length;)
        {
            if (double.IsNaN(midi[f]))
            {
                f++;
                continue;
            }

            int end = f;
            while (end < midi.Length && !double.IsNaN(midi[end]))
            {
                end++;
            }

            if (end - f < 3)
            {
                for (int i = f; i < end; i++)
                {
                    midi[i] = double.NaN;
                }
            }

            f = end;
        }
    }

    /// <summary>
    /// The scale of a key text: <c>chromatic</c>, <c>A minor</c>, <c>F# major</c>, <c>D dorian</c>, <c>C pentatonic</c>... <c>auto</c> (or empty) finds the key from the pitches that are sung
    /// (the key profiles of Krumhansl), and chromatic when too little is sung to tell.
    /// </summary>
    public static (bool[] Scale, string Name) Scale(string key, Track track)
    {
        string text = key.Trim();
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Guess(track);
        }

        if (text.Equals("chromatic", StringComparison.OrdinalIgnoreCase))
        {
            return (Mask(0, Modes["chromatic"]), "chromatic");
        }

        string[] words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? root = AutotuneFixes.Note(words[0] + "4");
        string mode = words.Length > 1 ? words[1].Replace(' ', '-') : "major";
        if (root is null || !Modes.TryGetValue(mode, out int[]? steps))
        {
            throw new AmpleException("AMP5397", $"'{key}' is not a key: write auto, chromatic, or a root and a scale such as A minor, F# major or D dorian (major, minor, harmonic-minor, dorian, mixolydian, pentatonic, minor-pentatonic, blues).");
        }

        return (Mask(root.Value % 12, steps), text);
    }

    private static bool[] Mask(int root, int[] steps)
    {
        bool[] mask = new bool[12];
        foreach (int step in steps)
        {
            mask[(root + step) % 12] = true;
        }

        return mask;
    }

    private static (bool[] Scale, string Name) Guess(Track track)
    {
        double[] weight = new double[12];
        int voiced = 0;
        foreach (double m in track.Midi.Where(m => !double.IsNaN(m)))
        {
            weight[(((int)Math.Round(m) % 12) + 12) % 12]++;
            voiced++;
        }

        if (voiced < 50)
        {
            return (Mask(0, Modes["chromatic"]), "chromatic");
        }

        double bestScore = double.MinValue;
        (int Root, bool Minor) best = (0, false);
        for (int root = 0; root < 12; root++)
        {
            foreach (bool minor in new[] { false, true })
            {
                double[] profile = minor ? MinorProfile : MajorProfile;
                double score = Enumerable.Range(0, 12).Sum(pc => weight[pc] * profile[((pc - root) % 12 + 12) % 12]);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (root, minor);
                }
            }
        }

        string name = AutotuneFixes.Name(best.Root + 60)[..^1] + (best.Minor ? " minor" : " major");
        return (Mask(best.Root, Modes[best.Minor ? "minor" : "major"]), name);
    }

    /// <summary>The notes of a melody (the midi input), as (from, to, note) in samples of the block.</summary>
    public static IReadOnlyList<(long From, long To, int Note)> Melody(byte[]? notes, int firstBar, int samplesPerBar, decimal beatsPerBar = 4m)
    {
        if (notes is null || JsonNode.Parse(notes) is not JsonObject root)
        {
            return [];
        }

        List<(long, long, int)> list = [];
        foreach (JsonObject note in (root["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int bar = note["bar"]?.GetValue<int>() ?? 1;
            decimal beat = note["beat"] is JsonValue b && b.TryGetValue(out decimal bd) ? bd : 0m;
            decimal beats = note["beats"] is JsonValue l && l.TryGetValue(out decimal ld) ? ld : 1m;
            long from = ((long)(bar - firstBar) * samplesPerBar) + (long)(beat * samplesPerBar / beatsPerBar);
            long to = from + Math.Max(1, (long)(beats * samplesPerBar / beatsPerBar));
            list.Add((from, to, note["pitch"]?.GetValue<int>() ?? 60));
        }

        return [.. list.OrderBy(n => n.Item1)];
    }

    /// <summary>The correction per step of the track, in semitones (0 where nothing is changed).</summary>
    public static double[] Corrections(Track track, Settings settings, IReadOnlyList<(long From, long To, int Note)> melody, IReadOnlyList<AutotuneFix> fixes)
    {
        double[] midi = track.Midi;
        int n = midi.Length;
        double[] target = new double[n];
        double[] vibrato = new double[n];
        for (int f = 0; f < n; f++)
        {
            target[f] = double.NaN;
            vibrato[f] = settings.Vibrato;
            if (double.IsNaN(midi[f]))
            {
                continue;
            }

            long at = (long)f * track.Hop;
            AutotuneFix? fix = fixes.LastOrDefault(x => x.From <= at && at < x.To);
            if (fix is not null)
            {
                vibrato[f] = fix.Vibrato is { } v ? v / 100.0 : settings.Vibrato;
                if (fix.Note is { } note)
                {
                    target[f] = note;
                }

                continue;   // off: left alone
            }

            int? sung = null;
            foreach ((long from, long to, int note) in melody)
            {
                if (from <= at && at < to)
                {
                    sung = note;
                }
            }

            if (sung is { } melodyNote)
            {
                // the note of the melody, in the octave that is closest to what is sung (a man sings the melody of the score an octave lower)
                target[f] = melodyNote + (12 * Math.Round((midi[f] - melodyNote) / 12));
                continue;
            }

            target[f] = Nearest(midi[f], settings.Scale, f > 0 ? target[f - 1] : double.NaN);
        }

        // the pitch the correction is measured from: the mean over 150 ms keeps the vibrato (it wobbles around the note), the pitch itself takes it away;
        // only steps that go to the same note count, so a short note is not pulled by the notes next to it
        double[] mean = new double[n];
        for (int f = 0; f < n; f++)
        {
            if (double.IsNaN(midi[f]))
            {
                continue;
            }

            double sum = 0;
            int count = 0;
            for (int k = f - 7; k <= f + 7; k++)
            {
                if (k >= 0 && k < n && !double.IsNaN(midi[k]) && (k == f || target[k].Equals(target[f])))
                {
                    sum += midi[k];
                    count++;
                }
            }

            mean[f] = sum / count;
        }

        double[] raw = new double[n];
        int run = 0;
        for (int f = 0; f < n; f++)
        {
            if (double.IsNaN(midi[f]) || double.IsNaN(target[f]))
            {
                run = 0;
                continue;
            }

            double from = (vibrato[f] * mean[f]) + ((1 - vibrato[f]) * midi[f]);
            double correction = target[f] - from;
            // humanize: a slide (the pitch moves fast) and the first 60 ms of a note keep more of what was sung
            double slope = f > 0 && !double.IsNaN(midi[f - 1]) ? Math.Abs(midi[f] - midi[f - 1]) : 0;
            double slide = Math.Clamp((slope - 0.25) / 0.5, 0, 1);
            double attack = run < 6 ? 1 - (run / 6.0) : 0;
            correction *= 1 - (settings.Humanize * Math.Max(slide, attack));
            raw[f] = Math.Clamp(correction * settings.Amount, -12, 12);
            run++;
        }

        // speed: the correction follows with a one-pole curve; where nothing is voiced it goes back to 0 in about 20 ms, so the joins never click
        const double hopMs = 10;   // the track has a value every 10 ms
        double follow = settings.SpeedMs <= 0 ? 1 : 1 - Math.Exp(-hopMs / settings.SpeedMs);
        double release = 1 - Math.Exp(-hopMs / 20.0);
        double[] smooth = new double[n];
        double state = 0;
        bool voicedBefore = false;
        for (int f = 0; f < n; f++)
        {
            bool voiced = !double.IsNaN(midi[f]) && !double.IsNaN(target[f]);
            if (voiced && !voicedBefore)
            {
                state = raw[f];   // a new note starts on its correction, so it never scoops in from the wrong note
            }

            state += (voiced ? follow : release) * ((voiced ? raw[f] : 0) - state);
            smooth[f] = state;
            voicedBefore = voiced;
        }

        return smooth;
    }

    // the closest note of the scale; the note of the step before stays while the voice is within 0.65 semitone of it, so a voice between two notes does not flip between them
    private static double Nearest(double m, bool[] scale, double previous)
    {
        if (!double.IsNaN(previous) && Math.Abs(m - previous) < 0.65 && scale[(((int)previous % 12) + 12) % 12])
        {
            return previous;
        }

        int center = (int)Math.Round(m);
        for (int distance = 0; distance <= 6; distance++)
        {
            int below = center - distance, above = center + distance;
            bool okBelow = scale[((below % 12) + 12) % 12], okAbove = scale[((above % 12) + 12) % 12];
            if (okBelow && okAbove)
            {
                return Math.Abs(m - below) <= Math.Abs(m - above) ? below : above;
            }

            if (okBelow)
            {
                return below;
            }

            if (okAbove)
            {
                return above;
            }
        }

        return center;
    }

    /// <summary>The correction at a sample: the values of the track steps are joined by straight lines, so the pitch never moves in steps of 10 ms.</summary>
    internal static double CorrectionAt(double[] values, int hop, double position)
    {
        double step = position / Math.Max(1, hop);
        int a = (int)Math.Clamp(Math.Floor(step), 0, values.Length - 1), b = Math.Min(a + 1, values.Length - 1);
        double t = Math.Clamp(step - a, 0, 1);
        return values[a] + ((values[b] - values[a]) * t);
    }

    /// <summary>How much of the tuned sound is used at every step (0 or 1): only where a voice is heard and corrected; breath, consonants and silence keep the original sound.</summary>
    internal static double[] Wet(Track track, double[] corrections)
    {
        double[] wet = new double[corrections.Length];
        for (int f = 0; f < wet.Length; f++)
        {
            wet[f] = !double.IsNaN(track.Midi[f]) && Math.Abs(corrections[f]) > 0.005 ? 1 : 0;
        }

        return wet;
    }

    /// <summary>
    /// Moves the pitch of every channel by the correction of its moment, with PSOLA (the default) or the spectral method, and blends the result with the original sound:
    /// where nothing is corrected (breath, consonants, silence) the original is heard, with 10 ms joins.
    /// </summary>
    public static short[][] Apply(short[][] channels, int rate, Track track, double[] corrections, bool spectral = false, CancellationToken cancellationToken = default)
    {
        if (corrections.All(c => Math.Abs(c) < 1e-4))
        {
            return channels;
        }

        double[][] tuned = spectral ? Spectral(channels, rate, position => CorrectionAt(corrections, track.Hop, position), true, cancellationToken) : Psola(channels, rate, track, corrections, cancellationToken);
        double[] wet = Wet(track, corrections);
        short[][] result = new short[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            result[c] = new short[channels[c].Length];
            for (int i = 0; i < result[c].Length; i++)
            {
                double w = CorrectionAt(wet, track.Hop, i);
                result[c][i] = (short)Math.Clamp(Math.Round((w * tuned[c][i]) + ((1 - w) * channels[c][i])), short.MinValue, short.MaxValue);
            }
        }

        return result;
    }

    /// <summary>
    /// PSOLA with a ratio per sample: the pitch marks come from the track (one period apart where it is voiced, 10 ms where not), a window of two periods around a mark is laid
    /// down one period divided by the ratio after the one before.
    /// </summary>
    private static double[][] Psola(short[][] channels, int rate, Track track, double[] corrections, CancellationToken cancellationToken)
    {
        int length = channels[0].Length;
        int fallback = Math.Max(2, rate / 100);
        int Step(long position) => (int)Math.Clamp(position / Math.Max(1, track.Hop), 0, track.Midi.Length - 1);
        // the period with its fraction: the windows are laid down that far apart (divided by the ratio), so a rounded period would put the pitch beside the note
        double ExactPeriodAt(long position)
        {
            double m = track.Midi[Step(position)];
            return double.IsNaN(m) ? fallback : Math.Max(2, rate / (440 * Math.Pow(2, (m - 69) / 12)));
        }

        int PeriodAt(long position) => Math.Max(2, (int)Math.Round(ExactPeriodAt(position)));

        // the marks on the sum of the channels: one period apart, each moved to the strongest sample close to where it should be
        int[] sum = new int[length];
        foreach (short[] channel in channels)
        {
            for (int i = 0; i < length; i++)
            {
                sum[i] += channel[i];
            }
        }

        List<int> marks = [];
        long mark = 0;
        while (mark < length)
        {
            int p = PeriodAt(mark), peak = (int)mark;
            if (!double.IsNaN(track.Midi[Step(mark)]))
            {
                int lo = (int)Math.Max(0, mark - (p / 4)), hi = (int)Math.Min(length - 1, mark + (p / 4));
                for (int i = lo; i <= hi; i++)
                {
                    if (Math.Abs(sum[i]) > Math.Abs(sum[peak]))
                    {
                        peak = i;
                    }
                }
            }

            if (marks.Count == 0 || peak > marks[^1])
            {
                marks.Add(peak);
            }

            mark = Math.Max(mark + 2, peak + PeriodAt(peak));
        }

        double[][] y = [.. channels.Select(_ => new double[length])];
        double[] weight = new double[length];
        double at = marks[0];
        int nearest = 0;
        while (at < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (nearest + 1 < marks.Count && Math.Abs(marks[nearest + 1] - at) <= Math.Abs(marks[nearest] - at))
            {
                nearest++;
            }

            int center = marks[nearest], p = PeriodAt(center);
            long place = (long)Math.Round(at);
            for (int i = -p; i < p; i++)
            {
                long o = place + i, src = center + i;
                if (o < 0 || o >= length || src < 0 || src >= length)
                {
                    continue;
                }

                double w = (p - Math.Abs(i)) / (double)p;   // a triangle of two periods
                for (int c = 0; c < channels.Length; c++)
                {
                    y[c][o] += channels[c][src] * w;
                }

                weight[o] += w;
            }

            // the ratio of this very sample (the correction joined by straight lines between the steps of the track)
            at += Math.Max(1, ExactPeriodAt(center) / Math.Pow(2, CorrectionAt(corrections, track.Hop, place) / 12));
        }

        for (int c = 0; c < channels.Length; c++)
        {
            for (int i = 0; i < length; i++)
            {
                // the windows are divided by how much of them landed here, so the level stays the same whatever the ratio
                y[c][i] = weight[i] > 1e-6 ? y[c][i] / Math.Max(weight[i], 0.5) : channels[c][i];
            }
        }

        return y;
    }

    /// <summary>
    /// The spectral method (a phase vocoder that keeps the formants): every frame of about 46 ms is split into its spectral envelope (the colour of the vowel, from the cepstrum)
    /// and its fine structure (the harmonics); only the fine structure is moved by the ratio, the envelope stays where it was, and the phases follow the true frequency of every
    /// harmonic. Larger corrections keep the voice more like itself than PSOLA does; small ones sound a little softer (phasier). Without <paramref name="keepFormants"/> the whole
    /// spectrum moves (like a tape or a sampler: the colour moves with the pitch). <paramref name="semitonesAt"/> gives the move at a sample.
    /// </summary>
    internal static double[][] Spectral(short[][] channels, int rate, Func<double, double> semitonesAt, bool keepFormants, CancellationToken cancellationToken)
    {
        int n = Dsp.NextPow2(Math.Max(512, rate / 22)), hop = n / 4, half = n / 2;
        int lifter = Math.Max(8, rate * 12 / 10000);   // 1.2 ms: below the period of any voice, so the envelope does not hold the harmonics
        double[] window = new double[n];
        for (int j = 0; j < n; j++)
        {
            window[j] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * j / n));
        }

        int length = channels[0].Length;
        double[][] result = new double[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
        {
            short[] x = channels[c];
            double[] y = new double[length], norm = new double[length];
            double[] lastPhase = new double[half + 1], sumPhase = new double[half + 1];
            double[] magnitude = new double[half + 1], trueBin = new double[half + 1], envelope = new double[half + 1];
            double[] outMagnitude = new double[half + 1], outBin = new double[half + 1];
            for (int start = -n + hop; start < length; start += hop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                System.Numerics.Complex[] frame = new System.Numerics.Complex[n];
                for (int j = 0; j < n; j++)
                {
                    int i = start + j;
                    frame[j] = i >= 0 && i < length ? x[i] * window[j] : 0;
                }

                Dsp.Fft(frame, false);
                System.Numerics.Complex[] cepstrum = new System.Numerics.Complex[n];
                for (int k = 0; k <= half; k++)
                {
                    magnitude[k] = frame[k].Magnitude;
                    double phase = frame[k].Phase;
                    double delta = phase - lastPhase[k] - (2 * Math.PI * hop * k / n);
                    delta -= 2 * Math.PI * Math.Round(delta / (2 * Math.PI));
                    trueBin[k] = k + (delta * n / (2 * Math.PI * hop));
                    lastPhase[k] = phase;
                    double log = Math.Log(magnitude[k] + 1e-3);
                    cepstrum[k] = log;
                    if (k > 0 && k < half)
                    {
                        cepstrum[n - k] = log;
                    }
                }

                // the envelope: the cepstrum with only its first quefrencies kept
                if (keepFormants)
                {
                    Dsp.Fft(cepstrum, true);
                    for (int q = lifter + 1; q < n - lifter; q++)
                    {
                        cepstrum[q] = 0;
                    }

                    Dsp.Fft(cepstrum, false);
                }

                for (int k = 0; k <= half; k++)
                {
                    envelope[k] = keepFormants ? Math.Exp(cepstrum[k].Real) : 1;
                }

                double ratio = Math.Pow(2, semitonesAt(start + half) / 12);
                Array.Clear(outMagnitude);
                Array.Clear(outBin);
                for (int k = 0; k <= half; k++)
                {
                    int to = (int)Math.Round(k * ratio);
                    if (to > half)
                    {
                        break;
                    }

                    outMagnitude[to] += magnitude[k] / envelope[k];
                    outBin[to] = trueBin[k] * ratio;
                }

                System.Numerics.Complex[] spectrum = new System.Numerics.Complex[n];
                for (int k = 0; k <= half; k++)
                {
                    sumPhase[k] += 2 * Math.PI * hop * outBin[k] / n;
                    spectrum[k] = System.Numerics.Complex.FromPolarCoordinates(outMagnitude[k] * envelope[k], sumPhase[k]);
                    if (k > 0 && k < half)
                    {
                        spectrum[n - k] = System.Numerics.Complex.Conjugate(spectrum[k]);
                    }
                }

                Dsp.Fft(spectrum, true);
                for (int j = 0; j < n; j++)
                {
                    int i = start + j;
                    if (i >= 0 && i < length)
                    {
                        y[i] += spectrum[j].Real * window[j];
                        norm[i] += window[j] * window[j];
                    }
                }
            }

            for (int i = 0; i < length; i++)
            {
                y[i] = norm[i] > 1e-6 ? y[i] / norm[i] : x[i];
            }

            result[c] = y;
        }

        return result;
    }
}

/// <summary>The autotune block: see <see cref="Autotune"/>. The melody input (target) says which note to sing at which time; the fixes of the window come first.</summary>
internal sealed class AutotuneRenderer : ExtraEffect
{
    public override string BlockType => "autotune";

    public override string Version => "2";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        short[] mono = new short[left.Length];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)((left[i] + right[i]) / 2);
        }

        Autotune.Track track = Autotune.Detect(mono, rate, cancellationToken);
        (bool[] scale, _) = Autotune.Scale(Text(r, "key"), track);
        Autotune.Settings settings = new(
            scale,
            Math.Clamp(Num(r, "speed", 20), 0, 1000),
            Math.Clamp(Num(r, "amount", 100), 0, 100) / 100,
            Math.Clamp(Num(r, "humanize", 30), 0, 100) / 100,
            Text(r, "vibrato") switch { "flatten" => 0, "keep" or "" => 1, _ => Math.Clamp(Num(r, "vibrato", 100), 0, 100) / 100 });
        IReadOnlyList<(long, long, int)> melody = Autotune.Melody(r.Inputs.GetValueOrDefault("target"), r.FirstBar, r.SamplesPerBar, r.BeatsPerBarDecimal);
        IReadOnlyList<AutotuneFix> fixes = AutotuneFixes.Parse(Text(r, "fixes"), rate, r.SamplesPerBar);
        double[] corrections = Autotune.Corrections(track, settings, melody, fixes);
        short[][] tuned = Autotune.Apply(stereo ? [left, right] : [left], rate, track, corrections, Text(r, "method") != "psola", cancellationToken);
        return Out(Encode(tuned[0], stereo ? tuned[1] : tuned[0], rate, stereo));
    }
}
