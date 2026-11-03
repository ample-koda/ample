using System.Globalization;

namespace Ample.Audio.Engine;

/// <summary>
/// A value that changes over the song, written as <c>auto(bar:value, bar:value, ...)</c> (bars are whole numbers, values whole numbers). Between two points the value
/// moves in a straight line, before the first point it is the first value and after the last point the last value. All arithmetic is integer. Used by the blocks that
/// say so (the volume block's gain, in dB, is the first one; plan X06).
/// </summary>
public static class Automation
{
    /// <summary>Gets a value indicating whether the text is an automation expression.</summary>
    public static bool Is(string text) => text.TrimStart().StartsWith("auto(", StringComparison.Ordinal) || text.TrimStart().StartsWith("lfo(", StringComparison.Ordinal) || text.TrimStart().StartsWith("env(", StringComparison.Ordinal) || Fades.Is(text) || ControlBinding.Is(text);

    /// <summary>
    /// Reads the points of an expression as sample positions: <c>auto(31.5s:-6, 5.5:0, 9:-60)</c> may write a point in seconds (<c>12s</c>) or in bars with decimals
    /// (<c>5.5</c> is half way through bar 5); a whole bar is as before. <c>lfo</c> and <c>env</c> are the bar-based ones.
    /// </summary>
    public static IReadOnlyList<(long Sample, int Value)> ParseSamples(string text, int samplesPerBar, int sampleRate)
    {
        string t = text.Trim();
        if (ControlBinding.Is(t)) return ControlBinding.Points(t);   // a control: the points were worked out from the song by the engine
        if (t.StartsWith("auto(", StringComparison.Ordinal) && t.EndsWith(')') && !Fades.Is(t))
        {
            List<(long Sample, int Value)> timed = [];
            foreach (string part in t[5..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) throw Bad(text);
                string position = part[..colon].Trim();
                bool seconds = position.EndsWith('s') || position.EndsWith('S');
                if (seconds) position = position[..^1].Trim();
                if (!double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out double at) || at < 0 || (!seconds && at < 1)
                    || !int.TryParse(part[(colon + 1)..].Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                {
                    throw Bad(text);
                }

                timed.Add((seconds ? (long)Math.Round(at * sampleRate) : (long)Math.Round((at - 1) * samplesPerBar), value));
            }

            return timed.Count == 0 ? throw Bad(text) : [.. timed.OrderBy(p => p.Sample)];
        }

        return [.. Parse(text).Select(p => ((long)(p.Bar - 1) * samplesPerBar, p.Value))];
    }

    /// <summary>A gain per sample from points at sample positions (see <see cref="ParseSamples"/>): the values are joined by straight lines.</summary>
    public static int[] CurveAt(IReadOnlyList<(long Sample, int Value)> points, int firstBar, int samplesPerBar, int length, Func<int, int> gainOf)
    {
        int[] gains = [.. points.Select(p => gainOf(p.Value))];
        int[] curve = new int[length];
        int segment = 0;
        for (int i = 0; i < length; i++)
        {
            long absolute = ((long)(firstBar - 1) * samplesPerBar) + i;
            while (segment + 1 < points.Count && absolute >= points[segment + 1].Sample)
            {
                segment++;
            }

            if (absolute < points[0].Sample || segment + 1 >= points.Count)
            {
                curve[i] = absolute < points[0].Sample ? gains[0] : gains[^1];
                continue;
            }

            long from = points[segment].Sample, to = points[segment + 1].Sample;
            curve[i] = (int)(gains[segment] + ((gains[segment + 1] - (long)gains[segment]) * (absolute - from) / Math.Max(1, to - from)));
        }

        return curve;
    }

    /// <summary>Reads the points (bar, value) of an expression, in order of bar.</summary>
    /// <exception cref="Ample.Core.Abstractions.AmpleException">AMP5371 when the expression is malformed.</exception>
    public static IReadOnlyList<(int Bar, int Value)> Parse(string text)
    {
        string t = text.Trim();
        // a fade is not a list of points: the parameters that take one (a volume gain, an effect mix) read it with Fades; here it is refused with the sentence that says where it works
        if (Fades.Is(t)) { Fades.Parse(t); throw new Ample.Core.Abstractions.AmpleException("AMP5371", $"The automation '{text}' is a fade, and fadein and fadeout work on a volume gain and on an effect mix only."); }
        if (t.StartsWith("lfo(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            return Lfo(text, t);
        }

        if (t.StartsWith("env(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            return Envelope(text, t);
        }

        if (!t.StartsWith("auto(", StringComparison.Ordinal) || !t.EndsWith(')'))
        {
            throw Bad(text);
        }

        List<(int Bar, int Value)> points = [];
        foreach (string part in t[5..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !int.TryParse(part[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int bar) || bar < 1 || !int.TryParse(part[(colon + 1)..].Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            {
                throw Bad(text);
            }

            points.Add((bar, value));
        }

        return points.Count == 0 ? throw Bad(text) : [.. points.OrderBy(p => p.Bar)];
    }

    /// <summary>
    /// A Q15 gain per sample from points in dB: the gains of the points are joined by straight lines (so a fade never clicks). The block starts at bar <paramref name="firstBar"/>.
    /// </summary>
    public static int[] GainCurve(IReadOnlyList<(int Bar, int Value)> points, int firstBar, int samplesPerBar, int length, int minDb, int maxDb) =>
        Curve(points, firstBar, samplesPerBar, length, value => FixedAudio.GainFromDbWide(Math.Clamp(value, minDb, maxDb)));

    /// <summary>A Q15 gain per sample from points whose values <paramref name="gainOf"/> turns into a gain: the gains of the points are joined by straight lines.</summary>
    public static int[] Curve(IReadOnlyList<(int Bar, int Value)> points, int firstBar, int samplesPerBar, int length, Func<int, int> gainOf)
    {
        int[] gains = [.. points.Select(p => gainOf(p.Value))];
        int[] curve = new int[length];
        int segment = 0;
        for (int i = 0; i < length; i++)
        {
            long absolute = ((long)(firstBar - 1) * samplesPerBar) + i;
            while (segment + 1 < points.Count && absolute >= (long)(points[segment + 1].Bar - 1) * samplesPerBar)
            {
                segment++;
            }

            if (absolute < (long)(points[0].Bar - 1) * samplesPerBar || segment + 1 >= points.Count)
            {
                curve[i] = absolute < (long)(points[0].Bar - 1) * samplesPerBar ? gains[0] : gains[^1];
                continue;
            }

            long from = (long)(points[segment].Bar - 1) * samplesPerBar, to = (long)(points[segment + 1].Bar - 1) * samplesPerBar;
            curve[i] = (int)(gains[segment] + ((gains[segment + 1] - (long)gains[segment]) * (absolute - from) / Math.Max(1, to - from)));
        }

        return curve;
    }

    // env(start, rise, hold, fall, low, high): an envelope that starts at bar `start` at `low`, rises over `rise` bars to `high`, holds for `hold` bars and falls over `fall` bars back to `low`
    private static List<(int Bar, int Value)> Envelope(string text, string t)
    {
        string[] parts = t[4..^1].Split(',', StringSplitOptions.TrimEntries);
        int[] n = new int[6];
        if (parts.Length != 6 || parts.Select((x, i) => int.TryParse(x.Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), i < 4 ? NumberStyles.None : NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n[i])).Any(ok => !ok) || n[0] < 1 || n[1] < 1 || n[3] < 1)
        {
            throw new Ample.Core.Abstractions.AmpleException("AMP5371", $"The automation '{text}' must look like env(1, 2, 4, 2, -40, 0): the start bar, the bars to rise, the bars to hold, the bars to fall (at least 1 each, hold may be 0), the low and the high value.");
        }

        int rise = n[0] + n[1], hold = rise + n[2], fall = hold + n[3];
        List<(int Bar, int Value)> points = [(n[0], n[4]), (rise, n[5])];
        if (hold > rise)
        {
            points.Add((hold, n[5]));
        }

        points.Add((fall, n[4]));
        return points;
    }

    // lfo(period, low, high): a triangle between low and high with a period of an even number of bars, written out as points up to bar 257
    private static List<(int Bar, int Value)> Lfo(string text, string t)
    {
        string[] parts = t[4..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int period) || period < 2 || period % 2 != 0
            || !int.TryParse(parts[1].Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int low)
            || !int.TryParse(parts[2].Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int high))
        {
            throw new Ample.Core.Abstractions.AmpleException("AMP5371", $"The automation '{text}' must look like lfo(4, -12, 0): a period of an even number of bars, a low and a high value.");
        }

        List<(int Bar, int Value)> points = [];
        for (int bar = 1, n = 0; bar <= 257; bar += period / 2, n++)
        {
            points.Add((bar, n % 2 == 0 ? low : high));
        }

        return points;
    }

    private static Ample.Core.Abstractions.AmpleException Bad(string text) => new("AMP5371", $"The automation '{text}' must look like auto(1:-60, 5:0, 33:0) (a bar, a colon and a whole number per point).");
}
