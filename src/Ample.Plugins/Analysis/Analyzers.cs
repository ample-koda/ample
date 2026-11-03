using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Ample.Storage.Quality;

namespace Ample.Plugins.Analysis;

/// <summary>What an analyser works on: mono samples of one take (the take itself is never changed).</summary>
/// <param name="Samples">Mono samples, -1..1.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="OptionsJson">Analyser options as JSON (for example <c>{"tempo":120}</c>), or <c>{}</c>.</param>
public sealed record AnalysisInput(float[] Samples, int SampleRate, string OptionsJson = "{}");

/// <summary>A reference analyser. It is pure given the input: the same samples and options always give the same JSON, and the result is checked against <c>spec/schema/analysis-&lt;kind&gt;.schema.json</c>.</summary>
public interface IAnalyzer
{
    /// <summary>Gets the analyser id (also its analysis kind): tempo, key, loudness, spectrum, pitch or melody.</summary>
    string Id { get; }

    /// <summary>Gets the analyser version; part of the cache key and of the toolprint.</summary>
    string Version { get; }

    /// <summary>Gets a plain sentence about what it does and how far it can be trusted.</summary>
    string Description { get; }

    /// <summary>Analyses and returns the result as JSON.</summary>
    string Analyze(AnalysisInput input);
}

/// <summary>Shared signal helpers: FFT and a few JSON writers.</summary>
internal static class Dsp
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static double Round(double v, int digits = 4) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    public static double? Round(double? v, int digits = 4) => v is { } x ? Round(x, digits) : null;

    /// <summary>In-place radix-2 FFT (length must be a power of two).</summary>
    public static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            Complex wlen = new(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k];
                    Complex v = a[i + k + (len / 2)] * w;
                    a[i + k] = u + v;
                    a[i + k + (len / 2)] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    /// <summary>Magnitude spectrum of one Hann-windowed frame (the first n/2+1 bins).</summary>
    public static double[] Magnitudes(float[] x, int start, int n)
    {
        Complex[] buffer = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double v = start + i < x.Length ? x[start + i] : 0;
            buffer[i] = v * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (n - 1))));
        }

        Fft(buffer);
        double[] mags = new double[(n / 2) + 1];
        for (int i = 0; i < mags.Length; i++)
        {
            mags[i] = buffer[i].Magnitude;
        }

        return mags;
    }

    public static double Option(string json, string name, double fallback)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return d.RootElement.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

/// <summary>Tempo and beat grid from the onset strength (positive energy change) by autocorrelation. Tolerance on clean synthetic click tracks: within 1 BPM for 70 to 180 BPM; real music is not claimed.</summary>
public sealed class TempoAnalyzer : IAnalyzer
{
    /// <inheritdoc />
    public string Id => "tempo";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "Tempo (70 to 180 BPM) and beat grid from onset autocorrelation.";

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        const int Hop = 256;
        const int Window = 1024;
        float[] x = input.Samples;
        int frames = (x.Length - Window) / Hop;
        if (frames < 8)
        {
            return JsonSerializer.Serialize(new { bpm = (double?)null, confidence = 0.0, beatSeconds = Array.Empty<double>(), firstBeatSeconds = (double?)null }, Dsp.Json);
        }

        double[] energy = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = 0; i < Window; i++)
            {
                double v = x[(f * Hop) + i];
                sum += v * v;
            }

            energy[f] = Math.Sqrt(sum / Window);
        }

        double[] flux = new double[frames];
        for (int f = 1; f < frames; f++)
        {
            flux[f] = Math.Max(0, energy[f] - energy[f - 1]);
        }

        double fps = input.SampleRate / (double)Hop;
        int minLag = (int)Math.Floor(fps * 60 / 180);
        int maxLag = Math.Min(frames - 1, (int)Math.Ceiling(fps * 60 / 70));
        double[] ac = new double[maxLag + 2];
        double zero = flux.Sum(v => v * v);
        for (int lag = minLag - 1; lag <= maxLag + 1 && lag < frames; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < frames; i++)
            {
                sum += flux[i] * flux[i + lag];
            }

            ac[lag] = sum / Math.Max(1, frames - lag);
        }

        // Prefer the shortest lag whose peak is close to the best one: the first level of the metrical hierarchy.
        int best = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (ac[lag] > ac[best])
            {
                best = lag;
            }
        }

        for (int lag = minLag; lag < best; lag++)
        {
            if (ac[lag] >= 0.9 * ac[best] && ac[lag] >= ac[lag - 1] && ac[lag] >= ac[lag + 1])
            {
                best = lag;
                break;
            }
        }

        double refined = best;
        double a = ac[best - 1];
        double b = ac[best];
        double c = ac[best + 1];
        double denom = a - (2 * b) + c;
        if (Math.Abs(denom) > 1e-12)
        {
            refined = best + (0.5 * (a - c) / denom);
        }

        double period = refined / fps;
        double bpm = 60 / period;
        double confidence = zero <= 0 ? 0 : Math.Clamp(b / (zero / frames), 0, 1);

        // Beat grid: the phase that collects the most onset strength.
        int periodFrames = Math.Max(1, (int)Math.Round(refined));
        int bestPhase = 0;
        double bestScore = -1;
        for (int phase = 0; phase < periodFrames; phase++)
        {
            double score = 0;
            for (double pos = phase; pos < frames; pos += refined)
            {
                int idx = (int)Math.Round(pos);
                if (idx < frames)
                {
                    score += flux[idx] + (idx + 1 < frames ? flux[idx + 1] : 0);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        double first = ((bestPhase + 1.0) * Hop / input.SampleRate) - (Window / 2.0 / input.SampleRate);
        first = Math.Max(0, first);
        List<double> beats = [];
        for (double t = first; t < x.Length / (double)input.SampleRate && beats.Count < 2000; t += period)
        {
            beats.Add(Dsp.Round(t, 3));
        }

        return JsonSerializer.Serialize(new { bpm = Dsp.Round(bpm, 2), confidence = Dsp.Round(confidence, 3), beatSeconds = beats, firstBeatSeconds = Dsp.Round(first, 3) }, Dsp.Json);
    }
}

/// <summary>Key from a chroma vector and the Krumhansl-Kessler profiles. Tolerance on synthetic chord sequences: the right tonic and mode, or the relative key at lower confidence; real music is not claimed.</summary>
public sealed class KeyAnalyzer : IAnalyzer
{
    private static readonly double[] Major = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] Minor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <inheritdoc />
    public string Id => "key";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "Key (tonic and major or minor) from a chroma profile.";

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        const int N = 8192;
        double[] chroma = new double[12];
        int hop = N / 2;
        for (int start = 0; start + N <= input.Samples.Length; start += hop)
        {
            double[] mags = Dsp.Magnitudes(input.Samples, start, N);
            for (int bin = 1; bin < mags.Length; bin++)
            {
                double hz = bin * input.SampleRate / (double)N;
                if (hz is < 55 or > 2000)
                {
                    continue;
                }

                double midi = 69 + (12 * Math.Log2(hz / 440));
                chroma[(((int)Math.Round(midi) % 12) + 12) % 12] += mags[bin] * mags[bin];
            }
        }

        double total = chroma.Sum();
        if (total <= 0)
        {
            return JsonSerializer.Serialize(new { key = (string?)null, tonic = (string?)null, mode = (string?)null, confidence = 0.0, chroma = new double[12], candidates = Array.Empty<object>() }, Dsp.Json);
        }

        double[] norm = [.. chroma.Select(v => v / total)];
        List<(string Name, string Tonic, string Mode, double Score)> scores = [];
        for (int tonic = 0; tonic < 12; tonic++)
        {
            scores.Add(($"{Names[tonic]} major", Names[tonic], "major", Correlate(norm, Major, tonic)));
            scores.Add(($"{Names[tonic]} minor", Names[tonic], "minor", Correlate(norm, Minor, tonic)));
        }

        List<(string Name, string Tonic, string Mode, double Score)> ranked = [.. scores.OrderByDescending(s => s.Score)];
        double confidence = Math.Clamp(ranked[0].Score - ranked[1].Score, 0, 1);
        return JsonSerializer.Serialize(new
        {
            key = ranked[0].Name,
            tonic = ranked[0].Tonic,
            mode = ranked[0].Mode,
            confidence = Dsp.Round(confidence, 3),
            chroma = norm.Select(v => Dsp.Round(v, 4)).ToArray(),
            candidates = ranked.Take(3).Select(s => new { key = s.Name, score = Dsp.Round(s.Score, 3) }).ToArray(),
        }, Dsp.Json);
    }

    private static double Correlate(double[] chroma, double[] profile, int tonic)
    {
        double[] rotated = new double[12];
        for (int i = 0; i < 12; i++)
        {
            rotated[i] = profile[((i - tonic) + 12) % 12];
        }

        double mx = chroma.Average();
        double my = rotated.Average();
        double sxy = 0;
        double sxx = 0;
        double syy = 0;
        for (int i = 0; i < 12; i++)
        {
            sxy += (chroma[i] - mx) * (rotated[i] - my);
            sxx += (chroma[i] - mx) * (chroma[i] - mx);
            syy += (rotated[i] - my) * (rotated[i] - my);
        }

        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
}

/// <summary>EBU R128 loudness: integrated, momentary and short-term maxima, loudness range, true peak. Tolerance on synthetic tones: 0.3 LU for integrated loudness.</summary>
public sealed class LoudnessAnalyzer : IAnalyzer
{
    /// <inheritdoc />
    public string Id => "loudness";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "EBU R128 loudness profile of mono audio.";

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        LoudnessProfile p = AudioAnalysis.Profile(input.Samples, input.SampleRate);
        return JsonSerializer.Serialize(new { integratedLufs = Dsp.Round(p.IntegratedLufs, 2), momentaryMaxLufs = Dsp.Round(p.MomentaryMaxLufs, 2), shortTermMaxLufs = Dsp.Round(p.ShortTermMaxLufs, 2), loudnessRange = Dsp.Round(p.LoudnessRange, 2), truePeakDb = Dsp.Round(p.TruePeakDb, 2), seconds = Dsp.Round(input.Samples.Length / (double)input.SampleRate, 3) }, Dsp.Json);
    }
}

/// <summary>Spectral summary: centroid, 85% roll-off, flatness and the share of energy in four bands.</summary>
public sealed class SpectrumAnalyzer : IAnalyzer
{
    /// <inheritdoc />
    public string Id => "spectrum";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "Spectral centroid, roll-off, flatness and band shares (below 250 Hz, 250 Hz to 2 kHz, 2 to 8 kHz, above 8 kHz).";

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        const int N = 2048;
        int frames = Math.Max(0, (input.Samples.Length - N) / (N / 2));
        double[] power = new double[(N / 2) + 1];
        int used = 0;
        int stride = Math.Max(1, frames / 600);
        for (int f = 0; f < frames; f += stride)
        {
            double[] mags = Dsp.Magnitudes(input.Samples, f * (N / 2), N);
            for (int i = 0; i < power.Length; i++)
            {
                power[i] += mags[i] * mags[i];
            }

            used++;
        }

        double total = power.Sum();
        if (used == 0 || total <= 0)
        {
            return JsonSerializer.Serialize(new { centroidHz = (double?)null, rolloffHz = (double?)null, flatness = (double?)null, bands = new { low = 0.0, mid = 0.0, high = 0.0, air = 0.0 } }, Dsp.Json);
        }

        double binHz = input.SampleRate / (double)N;
        double centroid = power.Select((p, i) => p * i * binHz).Sum() / total;
        double run = 0;
        double rolloff = 0;
        for (int i = 0; i < power.Length; i++)
        {
            run += power[i];
            if (run >= 0.85 * total)
            {
                rolloff = i * binHz;
                break;
            }
        }

        double[] active = [.. power.Skip(1).Where(p => p > 0)];
        double flatness = active.Length == 0 ? 0 : Math.Exp(active.Average(Math.Log)) / active.Average();
        double Band(double from, double to) => power.Select((p, i) => (p, hz: i * binHz)).Where(x => x.hz >= from && x.hz < to).Sum(x => x.p) / total;
        return JsonSerializer.Serialize(new { centroidHz = Dsp.Round(centroid, 1), rolloffHz = Dsp.Round(rolloff, 1), flatness = Dsp.Round(flatness, 5), bands = new { low = Dsp.Round(Band(0, 250), 4), mid = Dsp.Round(Band(250, 2000), 4), high = Dsp.Round(Band(2000, 8000), 4), air = Dsp.Round(Band(8000, double.MaxValue), 4) } }, Dsp.Json);
    }
}

/// <summary>Pitch contour by the YIN algorithm (de Cheveigne and Kawahara, 2002): difference function, cumulative mean normalisation, absolute threshold and parabolic interpolation. pYIN would add a probabilistic layer but needs a library; the choice is recorded in docs/analysis.md. Tolerance on synthetic tones: 10 cents.</summary>
public sealed class PitchAnalyzer : IAnalyzer
{
    /// <inheritdoc />
    public string Id => "pitch";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "Pitch contour (70 to 1000 Hz) every 10 ms with a confidence; unvoiced frames are left out.";

    /// <summary>Gets the voiced frames of a signal.</summary>
    public static IReadOnlyList<PitchFrame> Contour(float[] x, int rate)
    {
        ArgumentNullException.ThrowIfNull(x);
        int hop = rate / 100;
        int window = Math.Max(256, (int)(0.025 * rate));
        int tauMax = Math.Min(window, (int)(rate / 70.0));
        int tauMin = Math.Max(2, (int)(rate / 1000.0));
        List<PitchFrame> frames = [];
        double[] d = new double[tauMax + 1];
        double[] cmnd = new double[tauMax + 1];
        for (int start = 0; start + window + tauMax <= x.Length; start += hop)
        {
            double rms = 0;
            for (int i = 0; i < window; i++)
            {
                rms += x[start + i] * x[start + i];
            }

            if (AudioAnalysis.Db(Math.Sqrt(rms / window)) < -50)
            {
                continue;
            }

            for (int tau = 1; tau <= tauMax; tau++)
            {
                double sum = 0;
                for (int j = 0; j < window; j++)
                {
                    double diff = x[start + j] - x[start + j + tau];
                    sum += diff * diff;
                }

                d[tau] = sum;
            }

            cmnd[0] = 1;
            double running = 0;
            for (int tau = 1; tau <= tauMax; tau++)
            {
                running += d[tau];
                cmnd[tau] = running <= 0 ? 1 : d[tau] * tau / running;
            }

            int found = -1;
            for (int tau = tauMin; tau < tauMax; tau++)
            {
                if (cmnd[tau] < 0.15)
                {
                    while (tau + 1 < tauMax && cmnd[tau + 1] < cmnd[tau])
                    {
                        tau++;
                    }

                    found = tau;
                    break;
                }
            }

            if (found < 0)
            {
                continue;
            }

            double refined = found;
            if (found > 1 && found < tauMax)
            {
                double a = cmnd[found - 1];
                double b = cmnd[found];
                double c = cmnd[found + 1];
                double denom = a - (2 * b) + c;
                if (Math.Abs(denom) > 1e-12)
                {
                    refined = found + (0.5 * (a - c) / denom);
                }
            }

            double hz = rate / refined;
            frames.Add(new PitchFrame((start + (window / 2.0)) / rate, hz, 69 + (12 * Math.Log2(hz / 440)), Math.Clamp(1 - cmnd[found], 0, 1)));
        }

        return frames;
    }

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<PitchFrame> frames = Contour(input.Samples, input.SampleRate);
        return JsonSerializer.Serialize(new
        {
            hopSeconds = 0.01,
            voicedFrames = frames.Count,
            frames = frames.Select(f => new { t = Dsp.Round(f.Time, 3), hz = Dsp.Round(f.Hz, 2), midi = Dsp.Round(f.Midi, 3), confidence = Dsp.Round(f.Confidence, 3) }).ToArray(),
        }, Dsp.Json);
    }
}

/// <summary>One voiced frame of a pitch contour.</summary>
/// <param name="Time">Centre of the frame in seconds.</param>
/// <param name="Hz">Frequency.</param>
/// <param name="Midi">The same as a (fractional) MIDI note.</param>
/// <param name="Confidence">0 to 1.</param>
public sealed record PitchFrame(double Time, double Hz, double Midi, double Confidence);

/// <summary>A note of a melody plan.</summary>
/// <param name="Pitch">MIDI note number.</param>
/// <param name="StartSeconds">Start in seconds.</param>
/// <param name="LengthSeconds">Length in seconds.</param>
/// <param name="Start">Start in beats from the start of the take, on a quarter-beat grid.</param>
/// <param name="Length">Length in beats, on the same grid (at least a quarter).</param>
/// <param name="Bar">The 1-based bar (four beats).</param>
public sealed record PlannedNote(int Pitch, double StartSeconds, double LengthSeconds, double Start, double Length, int Bar);

/// <summary>Turns a pitch contour into the notes of a melody plan: the structure the freeze ladder's "melody" level stores and that a re-sing takes as its input.</summary>
public sealed class MelodyAnalyzer : IAnalyzer
{
    /// <inheritdoc />
    public string Id => "melody";

    /// <inheritdoc />
    public string Version => "1.0";

    /// <inheritdoc />
    public string Description => "Melody plan (notes on a quarter-beat grid) from the pitch contour. Option: {\"tempo\": BPM} (default: the tempo analyser, else 120).";

    /// <summary>Segments a contour into notes.</summary>
    public static IReadOnlyList<PlannedNote> Plan(IReadOnlyList<PitchFrame> contour, double bpm)
    {
        ArgumentNullException.ThrowIfNull(contour);
        List<List<PitchFrame>> runs = [];
        foreach (PitchFrame f in contour)
        {
            List<PitchFrame>? run = runs.Count > 0 ? runs[^1] : null;
            if (run is not null && f.Time - run[^1].Time <= 0.035 && Math.Abs(f.Midi - Median(run)) <= 0.7)
            {
                run.Add(f);
            }
            else
            {
                runs.Add([f]);
            }
        }

        // A short gap between two runs of the same note is one note (a consonant, a breath of the analyser).
        List<List<PitchFrame>> merged = [];
        foreach (List<PitchFrame> run in runs)
        {
            if (merged.Count > 0 && run[0].Time - merged[^1][^1].Time <= 0.08 && Math.Round(Median(run)) == Math.Round(Median(merged[^1])))
            {
                merged[^1].AddRange(run);
            }
            else
            {
                merged.Add(run);
            }
        }

        double secondsPerBeat = 60 / bpm;
        List<PlannedNote> notes = [];
        foreach (List<PitchFrame> run in merged.Where(r => r[^1].Time - r[0].Time + 0.01 >= 0.08))
        {
            double start = run[0].Time - 0.0125;
            double length = run[^1].Time - run[0].Time + 0.025;
            double beat = Math.Round(start / secondsPerBeat * 4) / 4;
            double beats = Math.Max(0.25, Math.Round(length / secondsPerBeat * 4) / 4);
            notes.Add(new PlannedNote((int)Math.Round(Median(run)), Math.Max(0, start), length, beat, beats, ((int)Math.Floor(beat) / 4) + 1));
        }

        return notes;
    }

    /// <inheritdoc />
    public string Analyze(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        double bpm = Dsp.Option(input.OptionsJson, "tempo", 0);
        if (bpm <= 0)
        {
            using JsonDocument t = JsonDocument.Parse(new TempoAnalyzer().Analyze(input));
            bpm = t.RootElement.TryGetProperty("bpm", out JsonElement b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 120;
        }

        IReadOnlyList<PlannedNote> notes = Plan(PitchAnalyzer.Contour(input.Samples, input.SampleRate), bpm);
        return ToJson(notes, bpm);
    }

    /// <summary>The plan as JSON (the same text a renderer gets as its <c>melody</c> input).</summary>
    public static string ToJson(IReadOnlyList<PlannedNote> notes, double bpm) => JsonSerializer.Serialize(new
    {
        schema = "ample-melody/1",
        tempo = Dsp.Round(bpm, 2),
        notes = notes.Select(n => new { pitch = n.Pitch, start = n.Start, length = n.Length, bar = n.Bar, startSeconds = Dsp.Round(n.StartSeconds, 3), lengthSeconds = Dsp.Round(n.LengthSeconds, 3) }).ToArray(),
    }, Dsp.Json);

    private static double Median(List<PitchFrame> run)
    {
        double[] sorted = [.. run.Select(f => f.Midi).Order()];
        return sorted[sorted.Length / 2];
    }
}

/// <summary>The analysers that ship with AMPLE (portable C#, no models, no network, no GPU).</summary>
public static class BuiltInAnalyzers
{
    /// <summary>Gets all reference analysers.</summary>
    public static IReadOnlyList<IAnalyzer> All { get; } = [new TempoAnalyzer(), new KeyAnalyzer(), new LoudnessAnalyzer(), new SpectrumAnalyzer(), new PitchAnalyzer(), new MelodyAnalyzer()];

    /// <summary>Finds an analyser by id.</summary>
    public static IAnalyzer? Find(string id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The text of a number as the analysers write it (for messages).</summary>
    public static string Number(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
