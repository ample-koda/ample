using System.Globalization;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>One rewind event, in samples of the block: where it happens, how long the scratch lasts and how far the music jumps back (negative: forward).</summary>
/// <param name="At">Output time of the stop.</param>
/// <param name="Scratch">Length of the scratch.</param>
/// <param name="Back">Jump back in the sample (negative: a skip forward).</param>
public sealed record RewindEvent(long At, long Scratch, long Back);

/// <summary>Reads the <c>events</c> text of a rewind block: <c>at 6s scratch 2s back 5s; at 14s scratch 1s back -3s</c>.</summary>
public static partial class RewindEvents
{
    private const string Code = "AMP5396";

    /// <summary>Reads the events text. A time is seconds (<c>6s</c>), milliseconds (<c>500ms</c>) or decimal bars from the start of the block (<c>4.5</c> or <c>4.5 bars</c>).</summary>
    /// <param name="text">The text of the parameter (empty: no events).</param>
    /// <param name="rate">Samples per second.</param>
    /// <param name="samplesPerBar">Samples in one bar.</param>
    /// <returns>The events in the order of their time.</returns>
    public static IReadOnlyList<RewindEvent> Parse(string text, int rate, int samplesPerBar)
    {
        List<RewindEvent> events = [];
        foreach (string chunk in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            string[] words = chunk.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            long? at = null;
            long scratch = rate;
            long back = 0;
            for (int i = 0; i < words.Length; i += 2)
            {
                if (i + 1 >= words.Length)
                {
                    throw Bad($"'{chunk}' ends after '{words[i]}': give a time after it.");
                }

                long value = Time(words[i + 1], rate, samplesPerBar, chunk);
                switch (words[i].ToLowerInvariant())
                {
                    case "at":
                        at = value;
                        break;
                    case "scratch":
                        scratch = value;
                        break;
                    case "back":
                        back = value;
                        break;
                    default:
                        throw Bad($"'{words[i]}' in '{chunk}' is not known: use at, scratch and back.");
                }
            }

            if (at is null || at < 0)
            {
                throw Bad($"'{chunk}' has no 'at' (a time from the start of the block).");
            }

            if (scratch is < 0 || scratch > 30L * rate)
            {
                throw Bad($"The scratch of '{chunk}' must last 0 to 30 seconds.");
            }

            events.Add(new RewindEvent(at.Value, scratch, back));
        }

        return [.. events.OrderBy(e => e.At)];
    }

    private static long Time(string word, int rate, int samplesPerBar, string chunk)
    {
        Match m = TimeWord().Match(word);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            throw Bad($"'{word}' in '{chunk}' is not a time: write seconds (6s), milliseconds (500ms) or bars (4.5).");
        }

        double samples = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => number * rate,
            "ms" => number * rate / 1000,
            _ => number * samplesPerBar,
        };
        if (Math.Abs(samples) > 600L * rate)
        {
            throw Bad($"'{word}' in '{chunk}' is further than 10 minutes.");
        }

        return (long)Math.Round(samples);
    }

    private static AmpleException Bad(string message) => new(Code, "The events of a rewind block cannot be read: " + message);

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)(s|ms|bar|bars)?$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TimeWord();
}

/// <summary>
/// Pulls the record back like a DJ: the music stops, only the scratch is heard, and the music goes on from an earlier (or later) place of the same sound. The times are those of the block (what you hear).
/// Built as a list of layers (music before the stop, the scratch, the music after it) that are added once each, so many events cost one pass. The scratch is made from the sound itself
/// (the record read backwards at a falling speed) or is the sample on the fx input. Start-up (smear) and brake follow a turntable motor. The scratches alone also leave on the region output.
/// </summary>
internal sealed class RewindRenderer : ExtraEffect
{
    public override string BlockType => "rewind";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        IReadOnlyList<RewindEvent> events = RewindEvents.Parse(Text(r, "events"), rate, r.SamplesPerBar);
        Settings s = new(
            Sound: Text(r, "sound"),
            Fade: (int)(Math.Clamp(Num(r, "fade", 5), 0, 50) * rate / 1000),
            Hardness: Math.Clamp(Num(r, "hardness", 100), 0, 100),
            Blend: Math.Clamp(Num(r, "blend", 120), 0, 500),
            Smear: (int)(Math.Clamp(Num(r, "smear", 0), 0, 2000) * rate / 1000),
            Brake: (int)(Math.Clamp(Num(r, "brake", 0), 0, 2000) * rate / 1000),
            Motor: Text(r, "curve") != "linear",
            Loop: Text(r, "end") == "loop");
        short[]? fxLeft = null, fxRight = null;
        if (r.Inputs.TryGetValue("fx", out byte[]? fx))
        {
            (StereoBuffer fxChannels, _) = ChannelAudio.Decode(fx);
            fxLeft = fxChannels.Left;
            fxRight = ChannelAudio.IsStereo(fx) ? fxChannels.Right : fxChannels.Left;
        }

        if (s.Sound == "fx" && fxLeft is null)
        {
            throw new AmpleException("AMP5396", "A rewind block with sound fx needs a sample on its fx input.");
        }

        (short[] outLeft, short[] regionLeft) = Perform(left, fxLeft, events, s, rate, cancellationToken);
        (short[] outRight, short[] regionRight) = stereo ? Perform(right, fxRight, events, s, rate, cancellationToken) : (outLeft, regionLeft);
        byte[] audio = Encode(outLeft, outRight, rate, stereo);
        return [new RenderOutput("out", audio), new RenderOutput("audio", audio), new RenderOutput("region", Encode(regionLeft, regionRight, rate, stereo))];
    }

    private sealed record Settings(string Sound, int Fade, double Hardness, double Blend, int Smear, int Brake, bool Motor, bool Loop);

    // how far the record has turned after u (0..1) of a start-up of n samples, in samples; the motor gets going fast and then settles, the straight one rises evenly
    private static double Up(double u, int n, bool motor) => n * (motor ? (u + ((Math.Exp(-5 * u) - 1) / 5)) / (1 - Math.Exp(-5)) : u * u / 2);

    // and for a brake: the motor slows down roughly quadratically
    private static double Down(double u, int n, bool motor) => n * (motor ? (1 - Math.Pow(1 - u, 3)) / 3 : u - (u * u / 2));

    private static (short[] Out, short[] Region) Perform(short[] src, short[]? fx, IReadOnlyList<RewindEvent> events, Settings s, int rate, CancellationToken ct)
    {
        int length = src.Length;
        double[] mix = new double[length];
        double[] region = new double[length];
        long half = (long)(s.Blend * (100 - s.Hardness) / 100 * rate / 1000 / 2);
        int overlap = (int)(half * 2);
        int fadeLength = overlap > 0 ? overlap : s.Fade;

        double Read(double position)
        {
            if (position < 0)
            {
                position = 0;
            }

            if (position >= length - 1)
            {
                if (!s.Loop || length == 0)
                {
                    return position >= length ? 0 : src[length - 1];
                }

                position %= length;
            }

            int index = (int)position;
            double fraction = position - index;
            return (src[index] * (1 - fraction)) + (src[Math.Min(length - 1, index + 1)] * fraction);
        }

        // how far the music has gone after i samples of a layer that starts with a start-up of `up` samples
        double Advance(long i, int up) => i < up ? Up((double)i / up, up, s.Motor) : (up > 0 ? Up(1, up, s.Motor) : 0) + (i - up);

        void Music(long start, long count, double from, int up, int brake, int fadeIn, int fadeOut)
        {
            count = Math.Min(count, length - start);
            for (long i = 0; i < count; i++)
            {
                if ((i & 8191) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                double advance = Advance(i, up);
                long brakeStart = count - brake;
                if (brake > 0 && i >= brakeStart)
                {
                    advance = Advance(brakeStart, up) + Down((double)(i - brakeStart) / brake, brake, s.Motor);
                }

                double gain = (fadeIn > 0 && i < fadeIn ? (double)i / fadeIn : 1) * (fadeOut > 0 && count - 1 - i < fadeOut ? (double)(count - 1 - i) / fadeOut : 1);
                mix[start + i] += Read(from + advance) * gain;
            }
        }

        long t = 0;
        double p = 0;
        int smear = 0;
        int fadeInNext = 0;
        foreach (RewindEvent e in events)
        {
            if (e.At < t + half || e.At >= length)
            {
                continue;
            }

            long musicEnd = Math.Min(length, e.At + half);
            Music(t, musicEnd - t, p, smear, (int)Math.Min(s.Brake, e.At - t), fadeInNext, fadeLength);
            double position = p + Advance(e.At - t, smear);
            long scratch = Math.Min(e.Scratch, length - e.At);
            for (long j = -half; j < scratch + half; j++)
            {
                long at = e.At + j;
                if (at < 0 || at >= length)
                {
                    continue;
                }

                double value;
                if (j < 0)
                {
                    value = Read(position + j);
                }
                else if (j >= scratch)
                {
                    value = 0;
                }
                else if (s.Sound == "fx" || (s.Sound == "auto" && fx is not null))
                {
                    int fade = (int)Math.Min(s.Fade, scratch / 4);
                    value = j < fx!.Length ? fx[j] * (fade > 0 && scratch - 1 - j < fade ? (double)(scratch - 1 - j) / fade : 1) : 0;
                }
                else if (s.Sound == "silence")
                {
                    value = 0;
                }
                else
                {
                    double travel = 4.0 * (j - ((double)j * j / (2.0 * Math.Max(1, scratch))));
                    value = Read(position - travel) * (scratch - j) / Math.Max(1, scratch);
                }

                double edge = 1;
                if (overlap > 0)
                {
                    edge = Math.Min(1, (double)(j + half + 1) / overlap) * Math.Min(1, (double)(scratch + half - j) / overlap);
                }
                else if (s.Fade > 0 && j >= 0 && j < scratch)
                {
                    edge = Math.Min(1, (double)(j + 1) / s.Fade) * Math.Min(1, (double)(scratch - j) / s.Fade);
                }

                mix[at] += value * edge;
                if (j >= 0 && j < scratch)
                {
                    region[at] += value * edge;
                }
            }

            p = Math.Max(0, position - e.Back);   // not before the start; past the end the end rule (stop or loop) decides
            t = Math.Max(0, e.At + scratch - half);
            smear = s.Smear;
            fadeInNext = fadeLength;
        }

        Music(t, length - t, p, smear, 0, fadeInNext, 0);
        short[] result = new short[length];
        short[] scratches = new short[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = FixedAudio.Clip((long)Math.Round(mix[i]));
            scratches[i] = FixedAudio.Clip((long)Math.Round(region[i]));
        }

        return (result, scratches);
    }
}
