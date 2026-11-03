using System.Globalization;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>
/// The chop block: a recording (a spoken phrase) is cut into slices, and the slices are put back on the beat in the order of a pattern. This is how a spoken line becomes a rhythmic vocal: "work it", "harder",
/// "make it", "faster" are four slices; the pattern <c>1 2 3 4 3 4*2</c> plays them on the grid, and a <c>*2</c> repeats a slice twice inside its step (a stutter). The slices come from the words
/// themselves (the loud parts, found by following the level) or from a list of start times. A slice can be sped up or slowed down to fill its step (fit), which does not matter for a voice that
/// only modulates a vocoder. Doubles; no randomness: the same sound gives the same bytes.
/// </summary>
public static class Chop
{
    private const string Code = "AMP5399";

    /// <summary>The settings of one chop.</summary>
    /// <param name="Step">Beats of one pattern step.</param>
    /// <param name="Gate">Share of a step (or of a repeat inside it) that sounds before the fade-out, 0 to 1.</param>
    /// <param name="Fit">How much a slice is sped up or slowed down to fill its step, 0 (natural speed) to 1 (exactly the step).</param>
    /// <param name="Repeat">How many times the whole pattern is played.</param>
    public sealed record Settings(double Step = 1, double Gate = 0.9, double Fit = 0, int Repeat = 1);

    /// <summary>Reads a pattern: steps separated by spaces, a number is a slice (1 is the first), <c>-</c> or <c>.</c> is a rest, <c>3*2</c> plays slice 3 twice inside the step. An empty text plays all slices in order.</summary>
    /// <param name="text">The text of the parameter.</param>
    /// <param name="slices">Number of slices.</param>
    /// <returns>For every step the slice index (0 based, -1 for a rest) and how many times it is played in the step.</returns>
    public static IReadOnlyList<(int Slice, int Times)> ParsePattern(string text, int slices)
    {
        List<(int, int)> steps = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return [.. Enumerable.Range(0, slices).Select(i => (i, 1))];
        }

        foreach (string word in text.Split([' ', '\t', ',', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "-" or ".")
            {
                steps.Add((-1, 1));
                continue;
            }

            string[] parts = word.Split('*');
            if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > slices
                || (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int times) || times < 1 || times > 16)))
            {
                throw new AmpleException(Code, $"The chop pattern step '{word}' is not a slice number from 1 to {slices}, a rest ('-') or a repeat like 3*2.");
            }

            steps.Add((number - 1, parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1));
        }

        return steps;
    }

    /// <summary>Finds the words of a sound: the parts louder than a share of the peak, with short gaps closed. Returns the start of every slice in samples.</summary>
    /// <param name="mono">The sound.</param>
    /// <param name="rate">Samples per second.</param>
    /// <param name="sensitivity">0 to 100: a higher value also finds quiet words.</param>
    /// <param name="gapMs">A silence shorter than this does not split a word.</param>
    public static IReadOnlyList<int> AutoSlices(double[] mono, int rate, double sensitivity, double gapMs)
    {
        if (mono.Length == 0)
        {
            return [0];
        }

        double peak = Math.Max(1e-9, mono.Max(Math.Abs));
        double threshold = peak * (0.30 * Math.Pow(0.01 / 0.30, Math.Clamp(sensitivity, 0, 100) / 100));
        double smooth = 1 - Math.Exp(-1 / (rate * 0.008)), level = 0;
        int gap = Math.Max(1, (int)(gapMs * rate / 1000)), lead = (int)(0.006 * rate);
        List<int> starts = [];
        int quiet = gap + 1;
        for (int i = 0; i < mono.Length; i++)
        {
            level += (Math.Abs(mono[i]) - level) * smooth;
            if (level > threshold)
            {
                if (quiet > gap)
                {
                    starts.Add(Math.Max(0, i - lead - (int)(0.012 * rate)));
                }

                quiet = 0;
            }
            else
            {
                quiet++;
            }
        }

        return starts.Count == 0 ? [0] : starts;
    }

    /// <summary>Reads start times (seconds, separated by spaces or commas); the list must rise.</summary>
    public static IReadOnlyList<int> ParseStarts(string text, int rate, int length)
    {
        List<int> starts = [];
        foreach (string word in text.Split([' ', '\t', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(word.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
            {
                throw new AmpleException(Code, $"The slice start '{word}' is not a time in seconds.");
            }

            int at = (int)Math.Round(seconds * rate);
            if (at >= length || (starts.Count > 0 && at <= starts[^1]))
            {
                throw new AmpleException(Code, $"The slice start '{word}' is past the end of the sound or not later than the one before it.");
            }

            starts.Add(at);
        }

        return starts.Count == 0 ? [0] : starts;
    }

    /// <summary>Builds the chopped sound of one channel.</summary>
    public static double[] Process(double[] source, IReadOnlyList<int> starts, IReadOnlyList<(int Slice, int Times)> pattern, int rate, double samplesPerBeat, Settings s)
    {
        int stepLength = Math.Max(1, (int)Math.Round(samplesPerBeat * Math.Max(0.0625, s.Step)));
        int repeats = Math.Clamp(s.Repeat, 1, 64);
        double[] result = new double[checked(stepLength * pattern.Count * repeats)];
        int fade = Math.Max(1, (int)(0.004 * rate));
        for (int round = 0; round < repeats; round++)
        {
            for (int step = 0; step < pattern.Count; step++)
            {
                (int slice, int times) = pattern[step];
                if (slice < 0)
                {
                    continue;
                }

                int from = starts[Math.Min(slice, starts.Count - 1)], to = slice + 1 < starts.Count ? starts[slice + 1] : source.Length, natural = Math.Max(1, to - from);
                int part = stepLength / times, sounding = Math.Max(fade * 2, (int)(part * Math.Clamp(s.Gate, 0.05, 1)));
                for (int k = 0; k < times; k++)
                {
                    int begin = ((round * pattern.Count) + step) * stepLength + (k * part);
                    // the speed: 1 is natural; fit moves it towards the speed that makes the slice exactly as long as its part
                    double speed = Math.Pow(Math.Clamp(natural / (double)part, 0.5, 2), Math.Clamp(s.Fit, 0, 1));
                    for (int i = 0; i < sounding && begin + i < result.Length; i++)
                    {
                        double position = from + (i * speed);
                        int index = (int)position;
                        if (position >= to - 1 || index + 1 >= source.Length)
                        {
                            break;
                        }

                        double value = source[index] + ((source[index + 1] - source[index]) * (position - index));
                        double edge = Math.Min(1, Math.Min(i, sounding - 1 - i) / (double)fade);
                        result[begin + i] += value * edge;
                    }
                }
            }
        }

        return result;
    }
}

/// <summary>The chop block.</summary>
internal sealed class ChopRenderer : ExtraEffect
{
    public override string BlockType => "chop";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        double[] l = [.. left.Select(v => (double)v)], rr = [.. right.Select(v => (double)v)];
        double[] mono = stereo ? [.. l.Select((v, i) => (v + rr[i]) / 2)] : l;
        string startsText = Text(r, "slices");
        IReadOnlyList<int> starts = string.IsNullOrWhiteSpace(startsText) || startsText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Chop.AutoSlices(mono, rate, Num(r, "sensitivity", 50), Num(r, "gap", 150))
            : Chop.ParseStarts(startsText, rate, mono.Length);
        IReadOnlyList<(int Slice, int Times)> pattern = Chop.ParsePattern(Text(r, "pattern"), starts.Count);
        Chop.Settings settings = new(Step: Num(r, "step", 1), Gate: Num(r, "gate", 90) / 100, Fit: Num(r, "fit", 0) / 100, Repeat: (int)Math.Round(Num(r, "repeat", 1)));
        double beat = r.SamplesPerBar / r.BeatsPerBar;
        double[] a = Chop.Process(l, starts, pattern, rate, beat, settings), b = stereo ? Chop.Process(rr, starts, pattern, rate, beat, settings) : a;
        short[] ca = [.. a.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue))], cb = stereo ? [.. b.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue))] : ca;
        return Out(Encode(ca, cb, rate, stereo));
    }
}
