using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>What the user wants from a hummed melody: the tempo to count in, how fine the notes snap in time, whether they snap to a key, and where the clip starts.</summary>
/// <param name="Tempo">Beats per minute of the song (the recording starts on the downbeat of bar <paramref name="StartBar"/>).</param>
/// <param name="Grid">The smallest step in beats that the notes snap to (0.25 = a sixteenth, 0.5 = an eighth, 1 = a beat, 0 = no snapping).</param>
/// <param name="Key">A key to snap the notes to ("C major", "A minor"), "auto" to snap to the key that fits best, or empty to leave the notes as they were sung.</param>
/// <param name="StartBar">The bar the first beat of the recording is placed at.</param>
internal sealed record HumOptions(double Tempo = 120, double Grid = 0.25, string Key = "", int StartBar = 1);

/// <summary>One note found in a hummed line.</summary>
/// <param name="Start">Seconds from the start of the recording.</param>
/// <param name="Duration">Seconds.</param>
/// <param name="Pitch">MIDI note number (60 = middle C).</param>
/// <param name="Cents">How far the note was from that pitch, in cents (-50 to 50).</param>
/// <param name="Strength">0 to 1: how clear the pitch was.</param>
internal sealed record HumNote(double Start, double Duration, int Pitch, int Cents, double Strength);

/// <summary>What was found in a hummed line.</summary>
internal sealed record HumResult(double Seconds, IReadOnlyList<HumNote> Notes, string Key, double KeyScore, string NotesText, int Voiced, string Quality);

/// <summary>
/// Finds the melody in a hummed (or sung or whistled) single line: the pitch of every moment (the YIN method), cut into notes, snapped in time to the tempo and optionally to a key, and written as the
/// <c>notes</c> text of a <c>midi-clip</c>. It is an attempt, not a transcription: a hummed line is never exactly in tune or in time, so the result is meant to be listened to and corrected.
/// </summary>
internal static class HumTranscriber
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly double[] Major = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] Minor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>What a sample is: length, sample rate, peak and average level, silence at the start and end, and the pitch when it has one clear note.</summary>
    public static JsonObject Facts(byte[] wav)
    {
        (float[] samples, int rate) = ReadWav(wav);
        double seconds = samples.Length / (double)rate;
        float peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        double rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Length);
        float floor = (float)Math.Pow(10, -60.0 / 20);
        int first = Array.FindIndex(samples, v => Math.Abs(v) > floor), last = Array.FindLastIndex(samples, v => Math.Abs(v) > floor);
        JsonObject facts = new()
        {
            ["seconds"] = Math.Round(seconds, 3),
            ["sampleRate"] = rate,
            ["peakDbfs"] = peak == 0 ? null : Math.Round(20 * Math.Log10(peak), 1),
            ["rmsDbfs"] = rms == 0 ? null : Math.Round(20 * Math.Log10(rms), 1),
            ["silenceAtStartSeconds"] = first < 0 ? null : Math.Round(first / (double)rate, 3),
            ["silenceAtEndSeconds"] = last < 0 ? null : Math.Round((samples.Length - 1 - last) / (double)rate, 3),
        };
        if (seconds is > 0.1 and < 30)
        {
            try
            {
                HumResult r = Transcribe(wav, new HumOptions());
                if (r.Notes.Count > 0)
                {
                    HumNote main = r.Notes.OrderByDescending(n => n.Duration).First();
                    facts["pitch"] = NoteName(main.Pitch);
                    facts["pitchedNotes"] = r.Notes.Count;
                }
            }
            catch (AmpleException)
            {
                // a sample without a clear pitch (a drum hit, noise) just has no pitch
            }
        }

        return facts;
    }

    public static string NoteName(int pitch) => Names[((pitch % 12) + 12) % 12] + (pitch / 12 - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a WAV (PCM 16 bit, mono or stereo) into mono samples between -1 and 1.</summary>
    public static (float[] Samples, int Rate) ReadWav(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            throw new AmpleException("AMP5470", "This is not a WAV file.");
        }

        int channels = 1, rate = 0, bits = 16, format = 1;
        for (int i = 12; i + 8 <= wav.Length;)
        {
            string id = Encoding.ASCII.GetString(wav, i, 4);
            int size = BitConverter.ToInt32(wav, i + 4);
            int body = i + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16 || channels is < 1 or > 2 || rate < 8000)
                {
                    throw new AmpleException("AMP5470", "Only 16-bit PCM WAV files (mono or stereo) can be turned into a melody.");
                }

                int length = Math.Min(size < 0 ? int.MaxValue : size, wav.Length - body) / (2 * channels);
                float[] samples = new float[length];
                for (int n = 0; n < length; n++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BitConverter.ToInt16(wav, body + ((n * channels) + c) * 2) / 32768f;
                    }

                    samples[n] = sum / channels;
                }

                return (samples, rate);
            }

            i = body + size + (size & 1);
        }

        throw new AmpleException("AMP5470", "The WAV file has no audio data.");
    }

    public static HumResult Transcribe(byte[] wav, HumOptions options)
    {
        (float[] input, int inputRate) = ReadWav(wav);
        if (input.Length > inputRate * 120)
        {
            throw new AmpleException("AMP5470", "A hummed line can be at most two minutes.");
        }

        // work at about 16 kHz: a voice needs nothing more for its pitch, and it is much faster
        int factor = Math.Max(1, (int)Math.Round(inputRate / 16000.0));
        int rate = inputRate / factor;
        float[] x = new float[input.Length / factor];
        for (int i = 0; i < x.Length; i++)
        {
            float sum = 0;
            for (int j = 0; j < factor; j++)
            {
                sum += input[(i * factor) + j];
            }

            x[i] = sum / factor;
        }

        double seconds = input.Length / (double)inputRate;
        int hop = rate / 100, window = (int)(rate * 0.032), tauMin = rate / 900, tauMax = Math.Min(rate / 65, window);
        float peak = x.Length == 0 ? 0 : x.Max(Math.Abs);
        double gate = Math.Max(peak * 0.04, 0.003);
        int frames = x.Length > window + tauMax ? (x.Length - window - tauMax) / hop : 0;
        double[] midi = new double[frames];
        double[] clarity = new double[frames];
        double[] diff = new double[tauMax + 1];
        for (int f = 0; f < frames; f++)
        {
            int at = f * hop;
            double energy = 0;
            for (int j = 0; j < window; j++)
            {
                energy += x[at + j] * x[at + j];
            }

            midi[f] = double.NaN;
            if (Math.Sqrt(energy / window) < gate)
            {
                continue;
            }

            for (int tau = 1; tau <= tauMax; tau++)
            {
                double d = 0;
                for (int j = 0; j < window; j++)
                {
                    double delta = x[at + j] - x[at + j + tau];
                    d += delta * delta;
                }

                diff[tau] = d;
            }

            double running = 0;
            int best = -1;
            double bestValue = 1;
            double[] cmnd = new double[tauMax + 1];
            cmnd[0] = 1;
            for (int tau = 1; tau <= tauMax; tau++)
            {
                running += diff[tau];
                cmnd[tau] = running == 0 ? 1 : diff[tau] * tau / running;
            }

            for (int tau = tauMin; tau < tauMax; tau++)
            {
                if (cmnd[tau] < 0.18)
                {
                    while (tau + 1 < tauMax && cmnd[tau + 1] < cmnd[tau])
                    {
                        tau++;
                    }

                    best = tau;
                    bestValue = cmnd[tau];
                    break;
                }
            }

            if (best < 0)
            {
                // no clear dip under the threshold: take the lowest one, but only when it is clear enough
                for (int tau = tauMin; tau < tauMax; tau++)
                {
                    if (cmnd[tau] < bestValue)
                    {
                        bestValue = cmnd[tau];
                        best = tau;
                    }
                }

                if (best < 0 || bestValue > 0.3)
                {
                    continue;
                }
            }

            double refined = best;
            if (best > 1 && best < tauMax)
            {
                double a = cmnd[best - 1], b = cmnd[best], c = cmnd[best + 1];
                double denominator = a + c - (2 * b);
                if (Math.Abs(denominator) > 1e-9)
                {
                    refined = best + ((a - c) / (2 * denominator));
                }
            }

            midi[f] = 69 + (12 * Math.Log2(rate / refined / 440.0));
            clarity[f] = Math.Clamp(1 - bestValue, 0, 1);
        }

        // a median over five frames removes single jumps (an octave error, a click)
        double[] smooth = (double[])midi.Clone();
        for (int f = 2; f < frames - 2; f++)
        {
            double[] around = [.. midi.Skip(f - 2).Take(5).Where(v => !double.IsNaN(v)).Order()];
            smooth[f] = around.Length >= 3 ? around[around.Length / 2] : double.NaN;
        }

        List<HumNote> notes = Segment(smooth, clarity, hop / (double)rate);
        int voiced = smooth.Count(v => !double.IsNaN(v));
        string key = string.Empty;
        double keyScore = 0;
        if (notes.Count > 0)
        {
            (key, keyScore) = EstimateKey(notes);
        }

        string chosen = string.Equals(options.Key, "auto", StringComparison.OrdinalIgnoreCase) ? key : options.Key;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            notes = [.. notes.Select(n => n with { Pitch = SnapToKey(n.Pitch, chosen) })];
        }

        string quality = notes.Count == 0 ? "No melody was found: the recording is too quiet, or has no clear pitch (try humming louder, closer to the microphone, on one vowel)." : voiced / (double)Math.Max(1, frames) < 0.25 ? "Only a small part of the recording had a clear pitch: the notes are a rough guess." : "Found a melody; it is an attempt: listen to it and correct the notes.";
        return new HumResult(Math.Round(seconds, 3), notes, key, Math.Round(keyScore, 3), NotesText(notes, options), voiced, quality);
    }

    private static List<HumNote> Segment(double[] pitch, double[] clarity, double frame)
    {
        List<HumNote> notes = [];
        int f = 0, n = pitch.Length;
        while (f < n)
        {
            if (double.IsNaN(pitch[f]))
            {
                f++;
                continue;
            }

            int start = f, gap = 0, last = f;
            List<double> values = [pitch[f]];
            List<double> strengths = [clarity[f]];
            for (int k = f + 1; k < n; k++)
            {
                if (double.IsNaN(pitch[k]))
                {
                    if (++gap > 3)
                    {
                        break;
                    }

                    continue;
                }

                double typical = values.Skip(Math.Max(0, values.Count - 12)).Order().ElementAt(Math.Min(values.Count, 12) / 2);
                if (Math.Abs(pitch[k] - typical) > 0.8)
                {
                    // another pitch: it must hold for a few frames to be a new note (a slide goes through in between)
                    int hold = 0;
                    for (int m = k; m < Math.Min(n, k + 4); m++)
                    {
                        if (!double.IsNaN(pitch[m]) && Math.Abs(pitch[m] - pitch[k]) < 0.6)
                        {
                            hold++;
                        }
                    }

                    if (hold >= 3)
                    {
                        break;
                    }

                    continue;
                }

                gap = 0;
                last = k;
                values.Add(pitch[k]);
                strengths.Add(clarity[k]);
            }

            double duration = (last - start + 1) * frame;
            if (duration >= 0.07)
            {
                double median = values.Order().ElementAt(values.Count / 2);
                int midi = (int)Math.Round(median);
                notes.Add(new HumNote(Math.Round(start * frame, 3), Math.Round(duration, 3), midi, (int)Math.Round((median - midi) * 100), Math.Round(strengths.Average(), 2)));
            }

            f = Math.Max(last + 1, f + 1);
        }

        // two neighbours with the same pitch and a tiny gap are one note
        List<HumNote> merged = [];
        foreach (HumNote note in notes)
        {
            if (merged.Count > 0 && merged[^1].Pitch == note.Pitch && note.Start - (merged[^1].Start + merged[^1].Duration) < 0.06)
            {
                HumNote was = merged[^1];
                merged[^1] = was with { Duration = Math.Round(note.Start + note.Duration - was.Start, 3) };
            }
            else
            {
                merged.Add(note);
            }
        }

        return merged;
    }

    private static (string Key, double Score) EstimateKey(List<HumNote> notes)
    {
        double[] histogram = new double[12];
        foreach (HumNote n in notes)
        {
            histogram[((n.Pitch % 12) + 12) % 12] += n.Duration;
        }

        string best = string.Empty;
        double bestScore = double.NegativeInfinity;
        foreach ((double[] profile, string mode) in new[] { (Major, "major"), (Minor, "minor") })
        {
            for (int tonic = 0; tonic < 12; tonic++)
            {
                double[] shifted = [.. Enumerable.Range(0, 12).Select(i => profile[((i - tonic) + 12) % 12])];
                double score = Correlation(histogram, shifted);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = $"{Names[tonic]} {mode}";
                }
            }
        }

        return (best, bestScore);
    }

    private static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
        for (int i = 0; i < a.Length; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }

        return da == 0 || db == 0 ? 0 : num / Math.Sqrt(da * db);
    }

    private static int SnapToKey(int pitch, string key)
    {
        string[] parts = key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int tonic = Array.FindIndex(Names, n => n.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (tonic < 0)
        {
            return pitch;
        }

        int[] scale = parts.Length > 1 && parts[1].StartsWith("min", StringComparison.OrdinalIgnoreCase) ? [0, 2, 3, 5, 7, 8, 10] : [0, 2, 4, 5, 7, 9, 11];
        for (int distance = 0; distance <= 2; distance++)
        {
            foreach (int candidate in new[] { pitch - distance, pitch + distance })
            {
                if (scale.Contains((((candidate - tonic) % 12) + 12) % 12))
                {
                    return candidate;
                }
            }
        }

        return pitch;
    }

    /// <summary>The notes as the text of a midi-clip: bar:beat:length:pitch:velocity, snapped to the grid; a note over a bar line is written as two notes, one after the other.</summary>
    public static string NotesText(IReadOnlyList<HumNote> notes, HumOptions options)
    {
        double tempo = options.Tempo is > 20 and < 400 ? options.Tempo : 120;
        double grid = options.Grid > 0 ? options.Grid : 0.0625;
        StringBuilder text = new();
        double previousEnd = 0;
        foreach (HumNote n in notes)
        {
            double start = Math.Round(n.Start * tempo / 60 / grid) * grid;
            double end = Math.Round((n.Start + n.Duration) * tempo / 60 / grid) * grid;
            start = Math.Max(start, previousEnd);
            if (end <= start)
            {
                end = start + grid;
            }

            previousEnd = end;
            int velocity = (int)Math.Clamp(Math.Round(70 + (n.Strength * 40)), 50, 110);
            while (start < end - 1e-9)
            {
                int bar = (int)Math.Floor(start / 4);
                double inBar = start - (bar * 4), room = Math.Min(end - start, 4 - inBar);
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(options.StartBar + bar).Append(':').Append(inBar.ToString("0.######", CultureInfo.InvariantCulture)).Append(':').Append(room.ToString("0.######", CultureInfo.InvariantCulture)).Append(':').Append(n.Pitch).Append(':').Append(velocity);
                start += room;
            }
        }

        return text.ToString();
    }

    public static JsonObject ToJson(HumResult r) => new()
    {
        ["seconds"] = r.Seconds,
        ["noteCount"] = r.Notes.Count,
        ["key"] = r.Key,
        ["keyScore"] = r.KeyScore,
        ["quality"] = r.Quality,
        ["notes"] = new JsonArray([.. r.Notes.Select(n => (JsonNode)new JsonObject { ["start"] = n.Start, ["duration"] = n.Duration, ["pitch"] = n.Pitch, ["name"] = NoteName(n.Pitch), ["cents"] = n.Cents, ["strength"] = n.Strength })]),
        ["notesText"] = r.NotesText,
    };
}
