using System.Globalization;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// SA01, a deterministic look at one piece of audio for an assistant that must decide how to use it: its length, level, brightness, the pitch of a single note (YIN), the likely key
/// (chroma against Krumhansl profiles), the tempo (onset autocorrelation) and whether it is a loop of whole bars. Everything is an estimate and comes with a confidence; nothing here
/// uses AI or a GPU. Only the first 90 seconds are looked at.
/// </summary>
public static class AudioAnalyzer
{
    private const int MaxSeconds = 90;
    private static readonly string[] NoteNames = ["c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b"];
    private static readonly double[] Major = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] Minor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>The note name of a MIDI number the way sample files are named (c4 is middle C, 60).</summary>
    public static string NoteName(int midi) => NoteNames[((midi % 12) + 12) % 12] + (Math.DivRem(midi, 12, out _) - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Analyses mono 16-bit samples.</summary>
    public static JsonObject Analyze(short[] samples, int rate)
    {
        int count = Math.Min(samples.Length, rate * MaxSeconds);
        double[] x = new double[count];
        for (int i = 0; i < count; i++) x[i] = samples[i] / 32768.0;
        double seconds = samples.Length / (double)rate;
        JsonObject result = new() { ["seconds"] = Math.Round(seconds, 3), ["sampleRate"] = rate, ["analysedSeconds"] = Math.Round(count / (double)rate, 3) };
        if (count < rate / 10)
        {
            result["note"] = "Too short to analyse (under a tenth of a second).";
            return result;
        }

        double peak = x.Max(v => Math.Abs(v)), rms = Math.Sqrt(x.Sum(v => v * v) / count);
        result["peakDbfs"] = Db(peak);
        result["rmsDbfs"] = Db(rms);
        if (peak < 1e-4)
        {
            result["note"] = "Silent.";
            return result;
        }

        JsonObject tone = Tone(x, rate);
        result["brightness"] = tone;
        JsonObject? pitch = Pitch(x, rate);
        result["pitch"] = pitch ?? new JsonObject { ["pitched"] = false, ["note"] = "No clear single pitch (noise, drums, chords or a mix)." };
        JsonObject key = Key(x, rate);
        result["key"] = key;
        JsonObject tempo = Tempo(x, rate, seconds);
        result["tempo"] = tempo;
        result["kind"] = Kind(seconds, pitch, tempo);
        result["advice"] = Advice(seconds, pitch, key, tempo);
        result["confidenceNote"] = "All values are estimates. Trust pitch when confidence is above 0.85, tempo above 0.5 and the key above 0.3; below that, listen first.";
        return result;
    }

    private static double Db(double v) => Math.Round(20 * Math.Log10(Math.Max(v, 1e-9)), 1);

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + (len / 2);
                    double tr = (re[b] * cr) - (im[b] * ci), ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = ((cr * wr) - (ci * wi), (cr * wi) + (ci * wr));
                }
            }
        }
    }

    // magnitude spectrum of one Hann-windowed frame starting at 'start' (zero padded at the end)
    private static double[] Spectrum(double[] x, int start, int size)
    {
        double[] re = new double[size], im = new double[size];
        for (int i = 0; i < size && start + i < x.Length; i++) re[i] = x[start + i] * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (size - 1))));
        Fft(re, im);
        double[] mag = new double[size / 2];
        for (int i = 0; i < mag.Length; i++) mag[i] = Math.Sqrt((re[i] * re[i]) + (im[i] * im[i]));
        return mag;
    }

    private static JsonObject Tone(double[] x, int rate)
    {
        const int size = 2048;
        double weighted = 0, total = 0;
        int step = Math.Max(size, x.Length / 40);
        for (int start = 0; start + size <= x.Length; start += step)
        {
            double[] mag = Spectrum(x, start, size);
            double sum = 0, centroid = 0;
            for (int k = 1; k < mag.Length; k++) { sum += mag[k]; centroid += mag[k] * k * rate / size; }
            if (sum <= 0) continue;
            weighted += centroid;
            total += sum;
        }

        double hz = total > 0 ? weighted / total : 0;
        string label = hz < 800 ? "dark" : hz < 2000 ? "warm" : hz < 4500 ? "bright" : "very bright";
        return new JsonObject { ["centroidHz"] = Math.Round(hz), ["label"] = label };
    }

    // YIN on the loudest 4096-sample stretch of the first seconds
    private static JsonObject? Pitch(double[] x, int rate)
    {
        const int frame = 4096, window = 2048;
        if (x.Length < frame) return null;
        int limit = Math.Min(x.Length - frame, rate * 6);
        int best = 0;
        double bestEnergy = -1;
        for (int start = 0; start <= limit; start += 1024)
        {
            double e = 0;
            for (int i = 0; i < window; i++) e += x[start + i] * x[start + i];
            if (e > bestEnergy) { bestEnergy = e; best = start; }
        }

        int minLag = Math.Max(2, rate / 2000), maxLag = Math.Min(frame - window - 1, rate / 40);
        double[] d = new double[maxLag + 1];
        for (int tau = 1; tau <= maxLag; tau++)
        {
            double sum = 0;
            for (int i = 0; i < window; i++) { double diff = x[best + i] - x[best + i + tau]; sum += diff * diff; }
            d[tau] = sum;
        }

        double running = 0;
        double[] cmnd = new double[maxLag + 1];
        cmnd[0] = 1;
        for (int tau = 1; tau <= maxLag; tau++)
        {
            running += d[tau];
            cmnd[tau] = running > 0 ? d[tau] * tau / running : 1;
        }

        int found = -1;
        for (int tau = minLag; tau < maxLag; tau++)
        {
            if (cmnd[tau] < 0.15)
            {
                while (tau + 1 < maxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                found = tau;
                break;
            }
        }

        if (found < 0)
        {
            int argmin = minLag;
            for (int tau = minLag; tau < maxLag; tau++) if (cmnd[tau] < cmnd[argmin]) argmin = tau;
            if (cmnd[argmin] > 0.3) return null;
            found = argmin;
        }

        double refined = found;
        if (found > 1 && found < maxLag)
        {
            double a = cmnd[found - 1], b = cmnd[found], c = cmnd[found + 1], denom = a - (2 * b) + c;
            if (Math.Abs(denom) > 1e-12) refined = found + (0.5 * (a - c) / denom);
        }

        double hz = rate / refined;
        double confidence = Math.Clamp(1 - cmnd[found], 0, 1);
        if (confidence < 0.7) return null;
        double midiExact = 69 + (12 * Math.Log2(hz / 440.0));
        int midi = (int)Math.Round(midiExact);
        return new JsonObject { ["pitched"] = true, ["hz"] = Math.Round(hz, 2), ["note"] = NoteName(midi), ["midi"] = midi, ["cents"] = (int)Math.Round((midiExact - midi) * 100), ["confidence"] = Math.Round(confidence, 2) };
    }

    private static JsonObject Key(double[] x, int rate)
    {
        const int size = 8192;
        double[] chroma = new double[12];
        int step = Math.Max(size, x.Length / 24);
        int frames = 0;
        for (int start = 0; start + size <= x.Length; start += step)
        {
            double[] mag = Spectrum(x, start, size);
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = k * rate / (double)size;
                if (hz < 60 || hz > 2200) continue;
                int pc = (((int)Math.Round(69 + (12 * Math.Log2(hz / 440.0))) % 12) + 12) % 12;
                chroma[pc] += Math.Sqrt(mag[k]);
            }

            frames++;
        }

        if (frames == 0 || chroma.Sum() <= 0) return new JsonObject { ["found"] = false, ["note"] = "Too short for a key estimate." };
        List<(string Name, double R)> scores = [];
        for (int tonic = 0; tonic < 12; tonic++)
        {
            scores.Add((NoteNames[tonic].ToUpperInvariant() + " major", Correlate(chroma, Major, tonic)));
            scores.Add((NoteNames[tonic].ToUpperInvariant() + " minor", Correlate(chroma, Minor, tonic)));
        }

        List<(string Name, double R)> ranked = [.. scores.OrderByDescending(s => s.R)];
        double confidence = Math.Clamp((ranked[0].R - ranked[1].R) * 5, 0, 1);
        return new JsonObject
        {
            ["found"] = true,
            ["key"] = ranked[0].Name,
            ["alternatives"] = new JsonArray([.. ranked.Skip(1).Take(2).Select(r => (JsonNode)JsonValue.Create(r.Name)!)]),
            ["confidence"] = Math.Round(confidence, 2),
            ["note"] = "For a single note or a drum loop the key says little; use pitch or the project's key.",
        };
    }

    private static double Correlate(double[] chroma, double[] profile, int tonic)
    {
        double meanC = chroma.Average(), meanP = profile.Average(), num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[(tonic + i) % 12] - meanC, p = profile[i] - meanP;
            num += c * p;
            dc += c * c;
            dp += p * p;
        }

        return dc > 0 && dp > 0 ? num / Math.Sqrt(dc * dp) : 0;
    }

    private static JsonObject Tempo(double[] x, int rate, double seconds)
    {
        if (seconds < 2) return new JsonObject { ["found"] = false, ["note"] = "Too short for a tempo (under 2 seconds): a one-shot or a short hit." };
        int hop = 512;
        int frames = x.Length / hop;
        double[] energy = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double e = 0;
            for (int i = 0; i < hop; i++) { double v = x[(f * hop) + i]; e += v * v; }
            energy[f] = Math.Log(1 + (1000 * e / hop));
        }

        double[] onset = new double[frames];
        for (int f = 1; f < frames; f++) onset[f] = Math.Max(0, energy[f] - energy[f - 1]);
        double mean = onset.Average();
        for (int f = 0; f < frames; f++) onset[f] -= mean;
        double hopSeconds = hop / (double)rate;
        int minLag = (int)Math.Floor(60.0 / 200 / hopSeconds), maxLag = Math.Min(frames / 2, (int)Math.Ceiling(60.0 / 60 / hopSeconds));
        if (maxLag <= minLag + 2) return new JsonObject { ["found"] = false, ["note"] = "Too short for a tempo." };
        double[] acf = new double[maxLag + 2];
        double zero = 0;
        for (int f = 0; f < frames; f++) zero += onset[f] * onset[f];
        if (zero <= 0) return new JsonObject { ["found"] = false, ["note"] = "No clear beat." };
        for (int lag = minLag; lag <= maxLag + 1 && lag < frames; lag++)
        {
            double sum = 0;
            for (int f = lag; f < frames; f++) sum += onset[f] * onset[f - lag];
            acf[lag] = sum / zero;
        }

        int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            // the sum of a lag and its double favours the beat over a faster subdivision
            double score = acf[lag] + (lag * 2 <= maxLag + 1 && lag * 2 < frames ? 0.5 * acf[lag * 2] : 0);
            double bestScore = acf[bestLag] + (bestLag * 2 <= maxLag + 1 && bestLag * 2 < frames ? 0.5 * acf[bestLag * 2] : 0);
            if (score > bestScore) bestLag = lag;
        }

        double refined = bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double a = acf[bestLag - 1], b = acf[bestLag], c = acf[bestLag + 1], denom = a - (2 * b) + c;
            if (Math.Abs(denom) > 1e-12) refined = bestLag + (0.5 * (a - c) / denom);
        }

        double bpm = 60.0 / (refined * hopSeconds);
        while (bpm < 70) bpm *= 2;
        while (bpm > 180) bpm /= 2;
        double meanAcf = 0;
        int n = 0;
        for (int lag = minLag; lag <= maxLag; lag++) { meanAcf += Math.Abs(acf[lag]); n++; }
        meanAcf /= Math.Max(1, n);
        double confidence = Math.Clamp((acf[bestLag] - meanAcf) / Math.Max(0.05, acf[bestLag] + meanAcf), 0, 1);
        double bars = seconds * bpm / 60 / 4;
        int wholeBars = (int)Math.Round(bars);
        bool loopReady = wholeBars >= 1 && Math.Abs(bars - wholeBars) / wholeBars < 0.04;
        JsonObject tempo = new()
        {
            ["found"] = true,
            ["bpm"] = Math.Round(bpm, 1),
            ["alsoPlausible"] = new JsonArray([.. new[] { bpm * 2, bpm / 2 }.Where(b => b is >= 60 and <= 200).Select(b => (JsonNode)JsonValue.Create(Math.Round(b, 1))!)]),
            ["confidence"] = Math.Round(confidence, 2),
            ["bars4of4"] = Math.Round(bars, 2),
            ["wholeBars"] = wholeBars,
            ["loopReady"] = loopReady,
        };
        if (loopReady) tempo["loopNote"] = $"The length is very close to {wholeBars} whole bars at this tempo: it probably loops.";
        return tempo;
    }

    private static string Kind(double seconds, JsonObject? pitch, JsonObject tempo)
    {
        if (seconds < 1.2) return pitch is not null ? "one-shot note" : "one-shot hit";
        if (tempo["loopReady"]?.GetValue<bool>() == true && tempo["confidence"]?.GetValue<double>() > 0.3) return "loop";
        if (seconds < 4) return pitch is not null ? "sustained note or short phrase" : "short sound";
        return "phrase or longer recording";
    }

    private static JsonArray Advice(double seconds, JsonObject? pitch, JsonObject key, JsonObject tempo)
    {
        JsonArray advice = [];
        if (pitch is not null) advice.Add($"A single note: name the file {pitch["note"]!.GetValue<string>()}.wav in a sample set (cents off: {pitch["cents"]!.GetValue<int>()}; a few cents are fine).");
        if (tempo["found"]?.GetValue<bool>() == true)
        {
            double bpm = tempo["bpm"]!.GetValue<double>();
            advice.Add(tempo["loopReady"]!.GetValue<bool>()
                ? $"Looks like a loop of {tempo["wholeBars"]!.GetValue<int>()} bars at {bpm:0.#} bpm: set the song tempo to about {bpm:0.#} or time-stretch it to the song's tempo, and place it with at <bar> for {tempo["wholeBars"]!.GetValue<int>()}."
                : $"Tempo about {bpm:0.#} bpm (or {tempo["alsoPlausible"]!.AsArray().FirstOrDefault()?.ToString()}); the length is not a whole number of bars, so cut or stretch it before looping.");
        }

        if (key["found"]?.GetValue<bool>() == true && key["confidence"]!.GetValue<double>() > 0.3) advice.Add($"Probably in {key["key"]!.GetValue<string>()}: keep the chords in that key or transpose the sample.");
        if (seconds > 30) advice.Add("A long recording: cut the part you want before using it.");
        return advice;
    }
}
