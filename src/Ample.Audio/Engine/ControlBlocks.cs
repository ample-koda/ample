using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The blocks that make and use controls (plan: control lanes): <c>follow</c> (a control from how loud a sound is), <c>curve</c> (a control you write), <c>map</c> (rescale, convert, smooth) and <c>fire</c> (notes from a control).
/// Everything is integer arithmetic (no floating point, no library functions), so the control is the same on every machine.
/// </summary>
public static partial class ControlBlocks
{
    /// <summary>Gets the renderers of the control blocks.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = [new FollowRenderer(), new CurveRenderer(), new MapRenderer(), new FireRenderer()];

    /// <summary>The block types of <see cref="All"/>.</summary>
    public static IReadOnlySet<string> Types { get; } = new HashSet<string>(StringComparer.Ordinal) { "follow", "curve", "map", "fire" };

    private static readonly string[] AutoNames = ["band", "attack", "release", "baseline_window"];

    internal static string Text(RenderRequest r, string name) =>
        r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;

    private static string? UnitOf(RenderRequest r) => ControlTypes.UnitOfBlock(r.Block);

    /// <summary>Reads a number with a unit word (<c>600 Hz</c>, <c>30 %</c>, <c>0.5</c>) as fixed point with three decimals (1000 is 1).</summary>
    internal static long Fixed(string text, long fallback)
    {
        Match m = NumberPattern().Match(text.Trim());
        if (!m.Success) return fallback;
        long whole = long.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        long fraction = 0;
        if (m.Groups[3].Success)
        {
            string digits = (m.Groups[3].Value + "000")[..3];
            fraction = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        long value = (whole * 1000) + fraction;
        string unit = m.Groups[4].Value;
        if (unit == "%") value /= 100;   // a percentage is a part of one (30 % is 0.3)
        return m.Groups[1].Value == "-" ? -value : value;
    }

    /// <summary>Reads a time (<c>80 ms</c>, <c>1.5 s</c>) in milliseconds.</summary>
    internal static int Milliseconds(string text, int fallback)
    {
        Match m = NumberPattern().Match(text.Trim());
        if (!m.Success) return fallback;
        long fixedValue = Fixed(text, fallback * 1000L);
        return (int)(m.Groups[4].Value.Equals("s", StringComparison.OrdinalIgnoreCase) ? fixedValue : (fixedValue + 500) / 1000);
    }

    /// <summary>The two numbers of <c>a .. b</c> as fixed point.</summary>
    internal static (long Low, long High) Range(string text, long low, long high)
    {
        string[] parts = text.Split("..", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (Fixed(parts[0], low), Fixed(parts[1], high)) : (low, high);
    }

    private static int IntegerSqrt(long value)
    {
        if (value <= 0) return 0;
        long x = (long)Math.Sqrt(value);   // only a starting point: the loops below make the result exact, whatever the library gives
        while (x * x > value) x--;
        while ((x + 1) * (x + 1) <= value) x++;
        return (int)x;
    }

    [GeneratedRegex("^(-?)(\\d+)(?:\\.(\\d+))?\\s*([A-Za-z%]*)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("^(\\d+(?:\\.\\d+)?)\\s*(?:Hz)?\\s*(?:\\.\\.|-|to)\\s*(\\d+(?:\\.\\d+)?)\\s*(?:Hz)?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BandPattern();

    private static int[] LowPass(int[] x, int rate, int hz)
    {
        // a one-pole low-pass with an integer coefficient: a = 2*pi*fc / (fs + 2*pi*fc) in 16 bit fixed point
        long a = 6283185L * hz * 65536 / ((1000000L * rate) + (6283185L * hz));
        a = Math.Clamp(a, 1, 65536);
        int[] y = new int[x.Length];
        long state = 0;
        for (int i = 0; i < x.Length; i++)
        {
            y[i] = (int)(state >> 8);
            state += ((((long)x[i] << 8) - state) * a) >> 16;
        }

        return y;
    }

    internal static int[] BandOf(int[] x, int rate, string band) => Band(x, rate, band);

    /// <summary>The envelope of a band at 1000 values per second (rectified, one time to rise and one to fall; 8 extra bits of precision).</summary>
    internal static long[] Envelope(int[] band, int rate, int attackMs, int releaseMs, int count, CancellationToken cancellationToken)
    {
        long attack = Math.Max(1, 65536L / ((attackMs * (long)rate / 1000) + 1));
        long release = Math.Max(1, 65536L / ((releaseMs * (long)rate / 1000) + 1));
        long[] series = new long[count];
        long envelope = 0;
        long next = 0;
        for (int i = 0; i < band.Length && next < count; i++)
        {
            if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            long v = (long)Math.Abs(band[i]) << 8;
            envelope += ((v - envelope) * (v > envelope ? attack : release)) >> 16;
            while (next < count && next * rate / ControlCurve.Rate <= i) series[next++] = envelope;
        }

        while (next < count) series[next++] = envelope;
        return series;
    }

    private static int[] Band(int[] x, int rate, string band)
    {
        string b = band.Trim().ToLowerInvariant();
        int Hz(double v) => (int)Math.Clamp(Math.Round(v), 10, (rate / 2) - 100);
        switch (b)
        {
            case "low":
                return LowPass(x, rate, 200);
            case "mid":
                {
                    int[] a = LowPass(x, rate, 2000), c = LowPass(x, rate, 200);
                    return [.. a.Select((v, i) => v - c[i])];
                }

            case "high":
                {
                    int[] a = LowPass(x, rate, 2000);
                    return [.. x.Select((v, i) => v - a[i])];
                }
        }

        Match m = BandPattern().Match(b);
        if (m.Success)
        {
            int lo = Hz(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)), hi = Hz(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            if (hi > lo)
            {
                int[] top = LowPass(x, rate, hi), bottom = LowPass(x, rate, lo);
                return [.. top.Select((v, i) => v - bottom[i])];
            }
        }

        return x;
    }

    private static (int[] Mono, int Rate) Mono(RenderRequest r)
    {
        if (!r.Inputs.TryGetValue("in", out byte[]? input)) return (new int[r.BarCount * r.SamplesPerBar], r.SampleRate);
        (StereoBuffer both, int rate) = ChannelAudio.Decode(input);
        int[] mono = new int[both.Left.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = (both.Left[i] + both.Right[i]) / 2;
        return (mono, rate);
    }

    private static ControlCurve? Source(RenderRequest r) => r.Inputs.TryGetValue("in", out byte[]? bytes) && ControlCurve.IsControl(bytes) ? ControlCurve.Decode(bytes) : null;

    private static IReadOnlyList<RenderOutput> Outputs(RenderRequest r, int[] level, int[]? gate)
    {
        ControlCurve curve = new(r.FirstBar, UnitOf(r), level);
        return gate is null ? [new RenderOutput("level", curve.Encode())] : [new RenderOutput("level", curve.Encode()), new RenderOutput("gate", new ControlCurve(r.FirstBar, null, gate).Encode())];
    }

    private abstract class ControlRenderer : IBlockRenderer
    {
        public abstract string BlockType { get; }

        public string Id => "builtin." + BlockType;

        public string Version => "1";

        public string Determinism => "exact";

        public abstract ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
    }

    private sealed class FollowRenderer : ControlRenderer
    {
        public override string BlockType => "follow";

        public override ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            (int[] mono, int rate) = Mono(r);
            int count = (int)(((long)mono.Length * ControlCurve.Rate / rate) + 1);
            if (Text(r, "enabled") == "off") return ValueTask.FromResult(Outputs(r, new int[count], new int[count]));

            // "auto": the settings come from the recording itself (the same recording gives the same answer); the take says what they were
            bool auto = AutoNames.Any(n => Text(r, n).Equals("auto", StringComparison.OrdinalIgnoreCase));
            FollowSuggestion? suggestion = auto ? FollowAnalysis.Suggest(mono, rate, cancellationToken) : null;
            string bandText = Text(r, "band").Equals("auto", StringComparison.OrdinalIgnoreCase) ? suggestion!.Band : Text(r, "band");
            int attackMs = Text(r, "attack").Equals("auto", StringComparison.OrdinalIgnoreCase) ? suggestion!.AttackMilliseconds : Milliseconds(Text(r, "attack"), 80);
            int releaseMs = Text(r, "release").Equals("auto", StringComparison.OrdinalIgnoreCase) ? suggestion!.ReleaseMilliseconds : Milliseconds(Text(r, "release"), 600);
            long windowMs = Text(r, "baseline_window").Equals("auto", StringComparison.OrdinalIgnoreCase) ? suggestion!.BaselineSeconds * 1000L : Math.Max(1, Milliseconds(Text(r, "baseline_window"), 10000));
            int[] band = Band(mono, rate, bandText);
            long[] series = Envelope(band, rate, attackMs, releaseMs, count, cancellationToken);

            // the baseline: what stays of the level above its own slow average (steady rain goes, a car that passes stays)
            long[] level = series;
            if (Text(r, "baseline") != "off")
            {
                long slow = Math.Max(1, 65536L / (windowMs + 1));
                long average = series.Length > 0 ? series[0] : 0;
                level = new long[count];
                for (int k = 0; k < count; k++)
                {
                    level[k] = Math.Max(0, series[k] - average);
                    average += ((series[k] - average) * slow) >> 16;
                }
            }

            // to 0..1000: against the loudest moment (normalize) or against full scale
            long reference = Text(r, "normalize") == "off" ? 32768L << 8 : Math.Max(1, level.Max());
            bool soft = Text(r, "curve") == "soft";
            (long low, long high) = Range(Text(r, "range"), 0, 1000);
            int[] values = new int[count];
            int[] fractions = new int[count];
            for (int k = 0; k < count; k++)
            {
                int f = (int)Math.Min(1000, level[k] * 1000 / reference);
                if (soft) f = IntegerSqrt((long)f * 1000);
                fractions[k] = f;
                values[k] = (int)(low + (f * (high - low) / 1000));
            }

            // the gate: on at the threshold, and on for the hold time after the level has dropped below it
            int threshold = (int)Math.Clamp(Fixed(Text(r, "threshold"), 300), 0, 1000);
            int hold = Milliseconds(Text(r, "hold"), 500);
            int[] gate = new int[count];
            int lastAbove = int.MinValue;
            for (int k = 0; k < count; k++)
            {
                if (fractions[k] >= threshold) lastAbove = k;
                gate[k] = lastAbove != int.MinValue && k - lastAbove <= hold ? 1000 : 0;
            }

            IReadOnlyList<RenderOutput> made = Outputs(r, values, gate);
            if (suggestion is not null) made = [.. made.Select((o, i) => i == 0 ? new RenderOutput(o.Role, o.Data) { MetadataJson = JsonSerializer.Serialize(new { followAuto = new { band = bandText, attackMs, releaseMs, baselineSeconds = windowMs / 1000, prominence = suggestion.Prominence } }) } : o)];
            return ValueTask.FromResult(made);
        }
    }

    private sealed class CurveRenderer : ControlRenderer
    {
        public override string BlockType => "curve";

        public override ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            int length = r.BarCount * r.SamplesPerBar;
            int count = (int)(((long)length * ControlCurve.Rate / r.SampleRate) + 1);
            bool typed = UnitOf(r) is not null;
            int[] values = new int[count];
            string text = Text(r, "value");
            if (Automation.Is(text) && Text(r, "enabled") != "off")
            {
                int[] perSample = Automation.CurveAt(Automation.ParseSamples(text, r.SamplesPerBar, r.SampleRate), r.FirstBar, r.SamplesPerBar, length, v => v);
                for (int k = 0; k < count; k++)
                {
                    int at = (int)Math.Min(perSample.Length - 1, (long)k * r.SampleRate / ControlCurve.Rate);
                    values[k] = perSample.Length == 0 ? 0 : typed ? perSample[at] * 1000 : perSample[at];   // a general control is per mille already, a typed one holds its unit times 1000
                }
            }
            else
            {
                Array.Fill(values, (int)Fixed(text, 0));   // a plain number is the value of the curve all the time
            }

            return ValueTask.FromResult(Outputs(r, values, null));
        }
    }

    private sealed class MapRenderer : ControlRenderer
    {
        public override string BlockType => "map";

        public override ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            ControlCurve source = Source(r) ?? new ControlCurve(r.FirstBar, null, new int[(int)((r.BarCount * (long)r.SamplesPerBar * ControlCurve.Rate / r.SampleRate) + 1)]);
            long inLow = Fixed(Text(r, "in_low"), 0), inHigh = Fixed(Text(r, "in_high"), 1000);
            long outLow = Fixed(Text(r, "out_low"), 0), outHigh = Fixed(Text(r, "out_high"), 1000);
            bool invert = Text(r, "invert") == "on", limit = Text(r, "limit") != "off";
            int smooth = Milliseconds(Text(r, "smooth"), 0);
            if (invert) (outLow, outHigh) = (outHigh, outLow);
            long span = inHigh - inLow == 0 ? 1 : inHigh - inLow;
            long lo = Math.Min(outLow, outHigh), hi = Math.Max(outLow, outHigh);
            long a = smooth <= 0 ? 65536 : Math.Max(1, 65536L / (smooth + 1));
            int[] values = new int[source.Values.Length];
            long state = 0;
            for (int k = 0; k < values.Length; k++)
            {
                long mapped = outLow + (((long)source.Values[k] - inLow) * (outHigh - outLow) / span);
                if (limit) mapped = Math.Clamp(mapped, lo, hi);
                state = k == 0 || smooth <= 0 ? mapped : state + (((mapped - state) * a) >> 16);
                values[k] = (int)state;
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("level", new ControlCurve(source.FirstBar, UnitOf(r), values).Encode())]);
        }
    }

    private sealed class FireRenderer : ControlRenderer
    {
        public override string BlockType => "fire";

        public override ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            ControlCurve? source = Source(r);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("ppq", StructuredMidiNotes.MicrobeatsPerBeat);
                writer.WriteStartArray("notes");
                if (source is not null && Text(r, "enabled") != "off")
                {
                    long threshold = Fixed(Text(r, "threshold"), 500);
                    bool rising = Text(r, "direction") != "falling";
                    int pitch = (int)Math.Clamp(Fixed(Text(r, "note"), 60000) / 1000, 0, 127);
                    string velocityText = Text(r, "velocity");
                    int fixedVelocity = (int)Math.Clamp(Fixed(velocityText, 100000) / 1000, 1, 127);
                    int gap = Milliseconds(Text(r, "gap"), 600);
                    int lengthMs = Math.Max(10, Milliseconds(Text(r, "length"), 250));
                    long firstSample = (long)(source.FirstBar - 1) * r.SamplesPerBar;
                    long lengthBeats1000 = (long)lengthMs * r.SampleRate / 1000 * 4000 / r.SamplesPerBar;
                    int last = int.MinValue;
                    for (int k = 1; k < source.Values.Length; k++)
                    {
                        bool crossed = rising ? source.Values[k - 1] < threshold && source.Values[k] >= threshold : source.Values[k - 1] > threshold && source.Values[k] <= threshold;
                        if (!crossed || (last != int.MinValue && k - last < gap)) continue;
                        last = k;
                        long t = firstSample + ((long)k * r.SampleRate / ControlCurve.Rate);
                        int bar = (int)(t / r.SamplesPerBar) + 1;
                        if (bar < r.FirstBar || bar >= r.FirstBar + r.BarCount) continue;
                        long beat1000 = t % r.SamplesPerBar * 4000 / r.SamplesPerBar;
                        int velocity = velocityText.Equals("level", StringComparison.OrdinalIgnoreCase) ? (int)Math.Clamp(1 + (source.Values[k] * 126L / 1000), 1, 127) : fixedVelocity;
                        writer.WriteStartObject();
                        writer.WriteNumber("bar", bar);
                        writer.WriteNumber("pitch", pitch);
                        writer.WriteNumber("beat", beat1000 / 1000m);
                        writer.WriteNumber("beats", lengthBeats1000 / 1000m);
                        writer.WriteNumber("velocity", velocity);
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("notes", stream.ToArray())]);
        }
    }
}
