using System.Text;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// A control: one number over time that comes out of a block (follow, curve, map) and steers a parameter of another block. It lives on the timeline of the song
/// from the first bar of its source, with 1000 values per second, in fixed point (1000 is 1 for a general control, a typed control holds its unit times 1000), as whole numbers only,
/// so the same song gives the same bytes on every machine.
/// </summary>
/// <param name="FirstBar">The first bar (1-based) of the song that the first value belongs to.</param>
/// <param name="Unit">The unit of a typed control (Hz, dB, ...), or null for a general control.</param>
/// <param name="Values">The values, one per millisecond.</param>
public sealed record ControlCurve(int FirstBar, string? Unit, int[] Values)
{
    /// <summary>The number of values per second.</summary>
    public const int Rate = 1000;

    private static readonly byte[] Magic = "CTL1"u8.ToArray();

    /// <summary>Gets whether the bytes are an encoded control.</summary>
    public static bool IsControl(byte[]? data) => data is { Length: >= 17 } && data.AsSpan(0, 4).SequenceEqual(Magic);

    /// <summary>The bytes of the control (the content of a take).</summary>
    public byte[] Encode()
    {
        byte[] unit = Unit is null ? [] : Encoding.UTF8.GetBytes(Unit);
        byte[] bytes = new byte[4 + 4 + 4 + 1 + unit.Length + 4 + (Values.Length * 4)];
        Magic.CopyTo(bytes, 0);
        int at = 4;
        Write(bytes, ref at, Rate);
        Write(bytes, ref at, FirstBar);
        bytes[at++] = (byte)unit.Length;
        unit.CopyTo(bytes, at);
        at += unit.Length;
        Write(bytes, ref at, Values.Length);
        foreach (int v in Values) Write(bytes, ref at, v);
        return bytes;
    }

    /// <summary>Reads the bytes of a control.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a control.</exception>
    public static ControlCurve Decode(byte[] data)
    {
        if (!IsControl(data)) throw new InvalidDataException("This is not a control.");
        int at = 8;
        int firstBar = Read(data, ref at);
        int unitLength = data[at++];
        string? unit = unitLength == 0 ? null : Encoding.UTF8.GetString(data, at, unitLength);
        at += unitLength;
        int count = Read(data, ref at);
        if (count < 0 || at + (count * 4L) > data.Length) throw new InvalidDataException("The control is cut off.");
        int[] values = new int[count];
        for (int i = 0; i < count; i++) values[i] = Read(data, ref at);
        return new ControlCurve(firstBar, unit, values);
    }

    /// <summary>
    /// The value at an absolute sample of the song (integer, linear between two values; before the first value and after the last one the value is the first or the last).
    /// </summary>
    /// <param name="absoluteSample">The sample counted from the start of the song.</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="samplesPerBar">The samples in one bar.</param>
    public long At(long absoluteSample, int sampleRate, int samplesPerBar)
    {
        if (Values.Length == 0) return 0;
        long rel = absoluteSample - ((long)(FirstBar - 1) * samplesPerBar);
        if (rel <= 0) return Values[0];
        long scaled = rel * Rate;   // value index times sampleRate
        long index = scaled / sampleRate;
        if (index >= Values.Length - 1) return Values[^1];
        long frac = scaled - (index * sampleRate);
        long a = Values[index], b = Values[index + 1];
        return a + (((b - a) * frac) / sampleRate);
    }

    private static void Write(byte[] to, ref int at, int value)
    {
        to[at++] = (byte)value;
        to[at++] = (byte)(value >> 8);
        to[at++] = (byte)(value >> 16);
        to[at++] = (byte)(value >> 24);
    }

    private static int Read(byte[] from, ref int at)
    {
        int v = from[at] | (from[at + 1] << 8) | (from[at + 2] << 16) | (from[at + 3] << 24);
        at += 4;
        return v;
    }
}

/// <summary>
/// The controls that steer the parameters of the block that is being rendered. The engine turns each of them into the points of an automation curve (<c>ctl:cutoff</c> in the parameter text),
/// so every renderer that already follows <c>auto(...)</c> follows a control too without knowing about it.
/// </summary>
public static class ControlBinding
{
    /// <summary>The start of the text that stands for a control in a parameter.</summary>
    public const string Prefix = "ctl:";

    private static readonly AsyncLocal<IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>>?> Current = new();

    /// <summary>Gets whether the text stands for a control.</summary>
    public static bool Is(string text) => text.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The points of the control with this text, or an empty list when none is bound.</summary>
    public static IReadOnlyList<(long Sample, int Value)> Points(string text) =>
        Current.Value is { } map && map.TryGetValue(text.Trim(), out IReadOnlyList<(long Sample, int Value)>? points) ? points : [];

    /// <summary>Binds the controls for the render that follows (dispose to unbind).</summary>
    public static IDisposable Use(IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> points)
    {
        IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>>? before = Current.Value;
        Current.Value = points;
        return new Scope(before);
    }

    private sealed class Scope(IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>>? before) : IDisposable
    {
        public void Dispose() => Current.Value = before;
    }

    /// <summary>
    /// Works out what the parameters of <paramref name="block"/> that read controls come to over the span of the block, as points on the timeline of the song every millisecond:
    /// the expression of the parameter (<c>cutoff 600 + 3000 * traffic.level</c>), or the control that is connected to the parameter (<c>from traffic.level -> cutoff</c>, scaled to the range of the parameter when it is a general one).
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="template">Its template.</param>
    /// <param name="inputs">The content of its inputs (a connected control is under the name of the parameter, a control that an expression reads under <c>~block.output</c>).</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="samplesPerBar">The samples in one bar.</param>
    /// <param name="firstBar">The first bar the block renders (1-based).</param>
    /// <param name="barCount">The bars it renders.</param>
    /// <returns>The block with those parameters set to <c>ctl:name</c> and the points for each name; the block itself and no points when no parameter reads a control.</returns>
    public static (BlockNode Block, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> Points) Resolve(BlockNode block, TemplateSpec template, IReadOnlyDictionary<string, byte[]> inputs, int sampleRate, int samplesPerBar, int firstBar, int barCount)
    {
        Dictionary<string, IReadOnlyList<(long Sample, int Value)>> points = [];
        Dictionary<string, string> replaced = [];
        long start = (long)(firstBar - 1) * samplesPerBar;
        long end = start + ((long)barCount * samplesPerBar);
        foreach (TemplateParameterSpec spec in template.ParameterSpecs.Values.Where(p => p.Automatable))
        {
            string? text = block.Parameters.FirstOrDefault(p => p.Name == spec.Name)?.Value.Trim().Trim('"');
            List<(long Sample, int Value)>? curve = null;
            if (text is not null && ControlExpression.LooksLike(text) && ControlExpression.TryParse(text, out _) is { } expression)
            {
                ControlCurve?[] sources = [.. expression.References.Select(r => inputs.TryGetValue("~" + r.Block + "." + r.Output, out byte[]? bytes) && ControlCurve.IsControl(bytes) ? ControlCurve.Decode(bytes) : null)];
                curve = Sample(start, end, sampleRate, t => ControlExpression.ToInt(expression.Evaluate(i => sources[i]?.At(t, sampleRate, samplesPerBar) ?? 0)));
            }
            else if (inputs.TryGetValue(spec.Name, out byte[]? connected) && ControlCurve.IsControl(connected))
            {
                ControlCurve source = ControlCurve.Decode(connected);
                int min = (int)Math.Round(spec.Min ?? 0), max = (int)Math.Round(spec.Max ?? 1000);
                curve = Sample(start, end, sampleRate, t =>
                {
                    long value = source.At(t, sampleRate, samplesPerBar);
                    // a general control (0 to 1000) is scaled to the range of the parameter, a typed one is the value itself
                    return source.Unit is null ? min + (int)(((long)(max - min) * Math.Clamp(value, 0, 1000) + 500) / 1000) : ControlExpression.ToInt(value);
                });
            }

            if (curve is null) continue;
            string name = Prefix + spec.Name;
            points[name] = curve;
            replaced[spec.Name] = name;
        }

        if (points.Count == 0) return (block, points);
        BlockNode copy = new() { Type = block.Type, Name = block.Name, RawName = block.RawName, Class = block.Class, Version = block.Version, Freeze = block.Freeze, Line = block.Line, CloseLine = block.CloseLine, Skipped = block.Skipped };
        copy.Parts.AddRange(block.Parts);
        copy.Flags.AddRange(block.Flags);
        copy.Parameters.AddRange(block.Parameters.Select(p => replaced.TryGetValue(p.Name, out string? value) ? p with { Value = value } : p));
        foreach ((string name, string value) in replaced.Where(r => copy.Parameters.All(p => p.Name != r.Key))) copy.Parameters.Add(new Parameter(name, value, block.Line));
        copy.Froms.AddRange(block.Froms);
        copy.ControlRefs.AddRange(block.ControlRefs);
        return (copy, points);
    }

    private static List<(long Sample, int Value)> Sample(long start, long end, int sampleRate, Func<long, int> valueAt)
    {
        List<(long Sample, int Value)> points = [];
        long ms = 0;
        while (true)
        {
            long t = start + (ms * sampleRate / ControlCurve.Rate);
            points.Add((t, valueAt(t)));
            if (t >= end) break;
            ms++;
        }

        return points;
    }
}
