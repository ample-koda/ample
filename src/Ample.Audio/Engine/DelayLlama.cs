using System.Globalization;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// Delay Llama: a vocal synthesizer with FOF synthesis (formant wave functions, after Rodet; the principle, vowel table and timing follow MonkSynth by Jonathan Taylor,
/// MIT, itself inspired by the Delay Lama of AudioNerdz). Every pitch period a grain of 20 ms is laid down: three damped sines at the formants F1 to F3 of the vowel
/// plus a breath of two inharmonic damped sines, under a short window. The vowel (a e i o u, 0 to 1) moves the formants along a spline, the voice scales them (a big or a
/// small singer), the mouth opens F1 and the level and closes the breath. Monophonic with the last note winning, glide between notes, vibrato with a seeded jitter,
/// unison with detune and stereo spread, an ADSR and an optional stereo echo. Pitch bend of the notes (the curve of the clip, the bend of a note) is sung natively.
/// Doubles and a seeded LCG: the same notes and settings give the same bytes on the same machine.
/// </summary>
public static class DelayLlama
{
    // the formants at the five vowel points [a, e, i, o, u] (Hz) and their bandwidths (Hz); the values are those of MonkSynth
    private static readonly double[][] Formants =
    [
        // a e i o u (MonkSynth lists its points in the order u o a e i, so its first point is an oo: the table here is put in the order of the vowel axis)
        [800, 350, 270, 450, 280],
        [1150, 2000, 2140, 800, 600],
        [2900, 2800, 2950, 2830, 2240],
    ];

    private static readonly double[] Bandwidths = [32.5, 47.5, 62.5];

    /// <summary>The settings of one Delay Llama (percent values 0..1, times in ms).</summary>
    public sealed record Settings(double Vowel = 0.5, double Voice = 0.5, double Mouth = 0.6, double GlideMs = 60, double Vibrato = 0.3, double VibratoHz = 5.5,
        double Breath = 0.5, int Unison = 1, double DetuneCents = 12, double Spread = 0.6, double AttackMs = 20, double DecayMs = 200, double Sustain = 0.85,
        double ReleaseMs = 180, double GainDb = -6, double Echo = 0, double Leveler = 0.5);

    /// <summary>One note, in samples of the block.</summary>
    public sealed record Note(long Start, long Length, int Pitch, int Velocity, int Fine = 0);

    /// <summary>The frequency of a formant (0..2) at a vowel (0..1): a Catmull-Rom spline through the five vowel points.</summary>
    public static double Formant(int formant, double vowel)
    {
        double[] p = Formants[formant];
        double[] padded = [p[0], p[0], p[1], p[2], p[3], p[4], p[4]];
        double position = Math.Clamp(vowel, 0, 1) * 4;
        int segment = Math.Min(3, (int)position);
        double t = position - segment, t2 = t * t, t3 = t2 * t;
        double p0 = padded[segment], p1 = padded[segment + 1], p2 = padded[segment + 2], p3 = padded[segment + 3];
        return 0.5 * ((2 * p1) + ((-p0 + p2) * t) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * t2) + ((-p0 + (3 * p1) - (3 * p2) + p3) * t3));
    }

    /// <summary>
    /// Sings the notes. <paramref name="vowel"/>, <paramref name="voice"/> and <paramref name="mouth"/> are curves per sample (0..1) or null for the setting;
    /// <paramref name="bend"/> is a pitch bend in semitones per sample (or null). Returns left and right (equal for one voice without echo).
    /// </summary>
    public static (double[] Left, double[] Right) Render(IReadOnlyList<Note> notes, Settings s, int rate, int length, ulong seed, double[]? vowel = null, double[]? voice = null,
        double[]? mouth = null, double[]? bend = null, CancellationToken cancellationToken = default)
    {
        double[] left = new double[length], right = new double[length];
        int voices = Math.Clamp(s.Unison, 1, 7);
        List<Note> ordered = [.. notes.Where(n => n.Length > 0).OrderBy(n => n.Start)];
        for (int v = 0; v < voices; v++)
        {
            // unison: the voices spread evenly over the detune (cents) and over the stereo field; each its own vibrato phase and jitter
            double spread = voices == 1 ? 0 : (v / (double)(voices - 1) * 2) - 1;
            double cents = spread * s.DetuneCents;
            double pan = spread * Math.Clamp(s.Spread, 0, 1);
            double[] one = Voice(ordered, s, rate, length, seed + ((ulong)v * 7919), cents, vowel, voice, mouth, bend, cancellationToken);
            double gl = Math.Cos((pan + 1) * Math.PI / 4), gr = Math.Sin((pan + 1) * Math.PI / 4);
            double share = 1 / Math.Sqrt(voices);
            for (int i = 0; i < length; i++)
            {
                left[i] += one[i] * gl * share * Math.Sqrt(2);
                right[i] += one[i] * gr * share * Math.Sqrt(2);
            }
        }

        if (s.Echo > 0)
        {
            Echo(left, right, rate, Math.Clamp(s.Echo, 0, 1));
        }

        double gain = Math.Pow(10, Math.Clamp(s.GainDb, -48, 12) / 20);
        // the leveler: what gets louder than about -10 dBFS is pulled back, slowly (it follows the loudness over 10 ms up and 250 ms down), so a low note, an open mouth
        // or three llamas in unison do not jump out; it only turns down, never up. leveler 0 is off, 1 holds everything to that level.
        double envelope = 0, attackCoefficient = 1 - Math.Exp(-1 / (rate * 0.01)), releaseCoefficient = 1 - Math.Exp(-1 / (rate * 0.25)), level = Math.Clamp(s.Leveler, 0, 1);
        for (int i = 0; i < length; i++)
        {
            double l = left[i] * gain, r = right[i] * gain, peak = Math.Max(Math.Abs(l), Math.Abs(r));
            envelope += (peak > envelope ? attackCoefficient : releaseCoefficient) * (peak - envelope);
            double turn = envelope > 0.3 ? 0.3 / envelope : 1;
            double g = 1 - (level * (1 - turn));
            left[i] = Soft(l * g);
            right[i] = Soft(r * g);
        }

        return (left, right);
    }

    // a gentle limit near full scale, so a loud unison never clips hard
    private static double Soft(double x) => Math.Abs(x) < 0.8 ? x : Math.Sign(x) * (0.8 + (0.2 * Math.Tanh((Math.Abs(x) - 0.8) / 0.2)));

    // the classic stereo echo: two taps (about 310 and 398 ms), each feeding back into itself, added to the dry sound
    private static void Echo(double[] left, double[] right, int rate, double amount)
    {
        int tapLeft = (int)(rate * 0.3096), tapRight = (int)(rate * 0.3984);
        double feedback = 0.45;
        double[] lineLeft = new double[left.Length], lineRight = new double[right.Length];
        for (int i = 0; i < left.Length; i++)
        {
            lineLeft[i] = (left[i] * amount) + (i >= tapLeft ? lineLeft[i - tapLeft] * feedback : 0);
            lineRight[i] = (right[i] * amount) + (i >= tapRight ? lineRight[i - tapRight] * feedback : 0);
        }

        for (int i = 0; i < left.Length; i++)
        {
            left[i] += i >= tapLeft ? lineLeft[i - tapLeft] : 0;
            right[i] += i >= tapRight ? lineRight[i - tapRight] : 0;
        }
    }

    private static double[] Voice(List<Note> notes, Settings s, int rate, int length, ulong seed, double cents, double[]? vowelCurve, double[]? voiceCurve, double[]? mouthCurve,
        double[]? bend, CancellationToken cancellationToken)
    {
        double[] output = new double[length];
        int grainLength = Math.Max(16, (int)(rate * 0.02));
        double[] window = new double[grainLength], breath = new double[grainLength];
        int attack = Math.Max(1, (int)(rate * 0.0018)), releaseStart = (int)(rate * 0.013), releaseLength = Math.Max(1, (int)(rate * 0.007));
        double period1 = rate * 0.000202, period2 = rate * 0.000263;
        for (int i = 0; i < grainLength; i++)
        {
            window[i] = i < attack ? 0.5 * (1 - Math.Cos(Math.PI * i / attack)) : i >= releaseStart ? 0.5 * (1 - Math.Cos(Math.PI * (releaseLength + i - releaseStart) / releaseLength)) : 1;
            breath[i] = (Math.Sin(2 * Math.PI * i / period1) * Math.Exp(-50 * Math.PI * 3 * i / rate)) + (Math.Sin(2 * Math.PI * i / period2) * Math.Exp(-50 * Math.PI * 3.5 * i / rate));
        }

        // the note that sounds at every moment: the last one that started and has not ended (a monophonic singer)
        int[] owner = new int[length];
        Array.Fill(owner, -1);
        for (int n = 0; n < notes.Count; n++)
        {
            for (long i = Math.Max(0, notes[n].Start); i < Math.Min(length, notes[n].Start + notes[n].Length); i++)
            {
                owner[i] = n;
            }
        }

        uint rng = (uint)(seed ^ (seed >> 32)) | 1;
        double Random() { rng = (rng * 1103515245u) + 12345u; return ((rng >> 16) & 0x7FFF) / 32768.0; }
        double vibratoPhase = Random() * 2 * Math.PI, jitter = 1, glideFrom = double.NaN, glideTo = double.NaN, pitch = 60;
        long glideStart = 0;
        int glideLength = Math.Max(1, (int)(s.GlideMs * rate / 1000));
        int jitterCounter = 0, jitterPeriod = Math.Max(1, (int)(rate * 0.104));
        double envelope = 0, level = 0;
        int stage = 0;   // 0 idle, 1 attack, 2 decay, 3 sustain, 4 release
        long noteSince = 0;
        int current = -1;
        double sinceGrain = double.MaxValue;
        double attackStep = 1 / Math.Max(1, s.AttackMs * rate / 1000), decayStep = 1 / Math.Max(1, s.DecayMs * rate / 1000), releaseStep = 1 / Math.Max(1, s.ReleaseMs * rate / 1000);
        double[] grain = new double[grainLength];
        for (int i = 0; i < length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int now = owner[i];
            if (now != current)
            {
                if (now >= 0)
                {
                    Note note = notes[now];
                    double target = note.Pitch + (note.Fine / 100.0);
                    bool legato = current >= 0 && stage is 1 or 2 or 3;
                    if (legato && s.GlideMs > 0)
                    {
                        // glide: from where the voice is to the new note in the glide time, without a new attack
                        glideFrom = pitch;
                        glideTo = target;
                        glideStart = i;
                    }
                    else
                    {
                        pitch = target;
                        glideFrom = glideTo = double.NaN;
                        stage = 1;
                        noteSince = i;
                    }

                    if (!legato)
                    {
                        noteSince = i;
                    }

                    level = 0.35 + (0.65 * note.Velocity / 127.0);
                }
                else
                {
                    stage = stage == 0 ? 0 : 4;
                }

                current = now;
            }

            if (!double.IsNaN(glideTo))
            {
                double t = Math.Min(1, (i - glideStart) / (double)glideLength);
                pitch = glideFrom + ((glideTo - glideFrom) * (t * t * (3 - (2 * t))));   // an eased slide
                if (t >= 1)
                {
                    glideFrom = glideTo = double.NaN;
                }
            }

            switch (stage)
            {
                case 1:
                    envelope += attackStep;
                    if (envelope >= 1) { envelope = 1; stage = 2; }
                    break;
                case 2:
                    envelope -= decayStep * (1 - s.Sustain);
                    if (envelope <= s.Sustain) { envelope = s.Sustain; stage = 3; }
                    break;
                case 4:
                    envelope -= releaseStep;
                    if (envelope <= 0) { envelope = 0; stage = 0; }
                    break;
            }

            // vibrato: it comes in over the first 300 ms of a note, its rate wanders a little (a living voice), and a small wobble is always there
            if (++jitterCounter >= jitterPeriod)
            {
                jitterCounter = 0;
                jitter = 0.9 + (Random() * 0.2);
            }

            vibratoPhase += 2 * Math.PI * s.VibratoHz * jitter / rate;
            double onset = Math.Min(1, (i - noteSince) / (rate * 0.3));
            double vibrato = ((0.04 + (s.Vibrato * 0.6)) * onset) * Math.Sin(vibratoPhase);
            if (stage == 0)
            {
                sinceGrain = double.MaxValue;
                continue;
            }

            double sung = pitch + (cents / 100) + vibrato + (bend is null ? 0 : bend[i]);
            // the pitch in steps of 1/32 semitone, as the original: part of its character
            double quantised = Math.Floor((sung - 12) * 32) / 32 + 12;
            double frequency = 440 * Math.Pow(2, (quantised - 69) / 12);
            double periodSamples = Math.Max(1, rate / frequency);
            sinceGrain++;
            if (sinceGrain < periodSamples)
            {
                continue;
            }

            // the grain starts where the period really ends (between two samples): the rest is carried, so the pitch is exact and not rounded to whole samples
            double overshoot = Math.Min(1, sinceGrain - periodSamples);
            sinceGrain = overshoot;
            double vowel = vowelCurve?[i] ?? s.Vowel, size = voiceCurve?[i] ?? s.Voice, open = Math.Clamp(mouthCurve?[i] ?? s.Mouth, 0, 1);
            double scale = (size * 0.5) + 0.75;
            // the mouth: open lifts F1 and the level and quiets the breath; closed is a hum
            double f1 = Formant(0, vowel) * scale * (0.75 + (0.45 * open)), f2 = Formant(1, vowel) * scale, f3 = Formant(2, vowel) * scale;
            double breathAmount = s.Breath * (1.2 - (0.7 * open));
            // fewer grains overlap at a high pitch: louder low, quieter high
            double compensate = Math.Clamp(((sung - 12) * (-1.0 / 72)) + 2, 0.1, 3);
            double amplitude = 0.5 * compensate * envelope * level * (0.45 + (0.55 * open));
            for (int k = 0; k < grainLength && i + k < length; k++)
            {
                double t = (k + overshoot) / rate;   // sample i lies overshoot after the true start of the grain
                double sample = (Math.Sin(2 * Math.PI * f1 * t) * Math.Exp(-Math.PI * Bandwidths[0] * t))
                    + (Math.Sin(2 * Math.PI * f2 * t) * Math.Exp(-Math.PI * Bandwidths[1] * t) * (0.6 + (0.4 * open)))
                    + (Math.Sin(2 * Math.PI * f3 * t) * Math.Exp(-Math.PI * Bandwidths[2] * t) * 0.8)
                    + (breath[k] * breathAmount);
                output[i + k] += sample * window[k] * amplitude;
            }
        }

        return output;
    }

    /// <summary>A percent setting or curve of a block: a number (0..100, or 0..1 with a decimal point below 1, or a vowel letter for vowel) or auto(bar:percent, ...).</summary>
    internal static (double Value, double[]? Curve) Shape(string text, double fallback, RenderRequest r, int length, bool vowel = false)
    {
        string t = text.Trim().Trim('"');
        if (t.Length == 0)
        {
            return (fallback, null);
        }

        if (Automation.Is(t))
        {
            int[] curve = Automation.CurveAt(Automation.ParseSamples(t, r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, length, v => Math.Clamp(v, 0, 100) * 100);
            return (fallback, [.. curve.Select(v => v / 10000.0)]);
        }

        if (vowel && t.Length == 1 && "aeiou".Contains(char.ToLowerInvariant(t[0]), StringComparison.Ordinal))
        {
            return ("aeiou".IndexOf(char.ToLowerInvariant(t[0]), StringComparison.Ordinal) / 4.0, null);
        }

        string number = new([.. t.TakeWhile(c => char.IsDigit(c) || c is '.' or '-')]);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? (Math.Clamp(v / 100, 0, 1), null) : (fallback, null);
    }

    /// <summary>The settings of a block or a sequencer channel from its words (<paramref name="get"/> gives the text of a setting, empty when not set).</summary>
    internal static Settings Read(Func<string, string> get)
    {
        double Number(string name, double fallback, double min, double max)
        {
            string t = get(name).Trim().Trim('"');
            string number = new([.. t.TakeWhile(c => char.IsDigit(c) || c is '.' or '-')]);
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, min, max) : fallback;
        }

        Settings d = new();
        return new Settings(
            Vowel: d.Vowel, Voice: d.Voice, Mouth: d.Mouth,
            GlideMs: Number("glide", d.GlideMs, 0, 2000),
            Vibrato: Number("vibrato", d.Vibrato * 100, 0, 100) / 100,
            VibratoHz: Number("vibrato_rate", d.VibratoHz, 0.5, 12),
            Breath: Number("breath", d.Breath * 100, 0, 100) / 100,
            Unison: (int)Number("unison", d.Unison, 1, 7),
            DetuneCents: Number("detune", d.DetuneCents, 0, 100),
            Spread: Number("spread", d.Spread * 100, 0, 100) / 100,
            AttackMs: Number("attack", d.AttackMs, 0, 5000),
            DecayMs: Number("decay", d.DecayMs, 0, 5000),
            Sustain: Number("sustain", d.Sustain * 100, 0, 100) / 100,
            ReleaseMs: Number("release", d.ReleaseMs, 0, 8000),
            GainDb: Number("gain", d.GainDb, -48, 12),
            Echo: Number("echo", d.Echo * 100, 0, 100) / 100,
            Leveler: Number("leveler", d.Leveler * 100, 0, 100) / 100);
    }

    /// <summary>The notes of a midi input for a block (in samples of the block).</summary>
    internal static List<Note> NotesOf(byte[]? bytes, RenderRequest r)
    {
        List<Note> notes = [];
        if (bytes is null || JsonNode.Parse(bytes) is not JsonObject root)
        {
            return notes;
        }

        foreach (JsonObject note in (root["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int bar = note["bar"]?.GetValue<int>() ?? 1;
            if (bar < r.FirstBar || bar >= r.FirstBar + r.BarCount)
            {
                continue;
            }

            decimal beat = note["beat"] is JsonValue b && b.TryGetValue(out decimal bd) ? bd : 0m;
            decimal beats = note["beats"] is JsonValue l && l.TryGetValue(out decimal ld) ? ld : 1m;
            long start = ((long)(bar - r.FirstBar) * r.SamplesPerBar) + (long)(beat * r.SamplesPerBar / r.BeatsPerBarDecimal);
            notes.Add(new Note(start, Math.Max(1, (long)(beats * r.SamplesPerBar / r.BeatsPerBarDecimal)), Math.Clamp(note["pitch"]?.GetValue<int>() ?? 60, 0, 127),
                Math.Clamp(note["velocity"]?.GetValue<int>() ?? 96, 1, 127), note["fine"]?.GetValue<int>() ?? 0));
        }

        return notes;
    }
}

/// <summary>The delay-llama block: the notes on its input sung by <see cref="DelayLlama"/>; vowel, voice and mouth may be curves.</summary>
internal sealed class DelayLlamaRenderer : IBlockRenderer
{
    public string BlockType => "delay-llama";

    public string Id => "builtin.delay-llama";

    public string Version => "3";   // 2: the vowels follow the axis a e i o u; 3: the leveler

    public string Determinism => "machine-exact";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        int length = r.BarCount * r.SamplesPerBar;
        if (Text("enabled") == "off")
        {
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(new short[length], r.SampleRate))]);
        }

        DelayLlama.Settings defaults = new();
        DelayLlama.Settings settings = DelayLlama.Read(Text);
        (double vowel, double[]? vowelCurve) = DelayLlama.Shape(Text("vowel"), defaults.Vowel, r, length, vowel: true);
        (double voice, double[]? voiceCurve) = DelayLlama.Shape(Text("voice"), defaults.Voice, r, length);
        (double mouth, double[]? mouthCurve) = DelayLlama.Shape(Text("mouth"), defaults.Mouth, r, length);
        settings = settings with { Vowel = vowel, Voice = voice, Mouth = mouth };
        byte[]? notes = r.Inputs.GetValueOrDefault("notes");
        // the bend of the notes (the curve of the clip, the bend of a note) is sung by the voice itself, so the vowels stay where they are
        double[]? bend = notes is null ? null : PitchBend.Curve(notes, r.FirstBar, r.SamplesPerBar, r.SampleRate, length, r.BeatsPerBarDecimal);
        (double[] left, double[] right) = DelayLlama.Render(DelayLlama.NotesOf(notes, r), settings, r.SampleRate, length, r.Seed, vowelCurve, voiceCurve, mouthCurve, bend, cancellationToken);
        short[] l = [.. left.Select(v => (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue))];
        short[] rr = [.. right.Select(v => (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue))];
        bool stereo = settings.Unison > 1 || settings.Echo > 0;
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", stereo ? StereoWav.Encode(new StereoBuffer(l, rr), r.SampleRate) : Wav.Encode(l, r.SampleRate))]);
    }
}
