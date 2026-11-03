using System.Globalization;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>Cutting a stretch out of audio: from a start to an end in seconds, or the first or last seconds, optionally repeated. Used by import-audio and the cut block; integer sample maths only.</summary>
internal static class AudioCut
{
    /// <summary>Reads seconds from a parameter value such as <c>10</c>, <c>10s</c> or <c>1.5 s</c>; empty or not a number gives null.</summary>
    internal static double? Seconds(string? text)
    {
        string value = (text ?? string.Empty).Trim().Trim('"').TrimEnd('s', 'S').Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0 ? seconds : null;
    }

    /// <summary>The samples that are kept: the first or the last seconds when asked for, else start to end (default: the whole).</summary>
    internal static (int Start, int End) Range(int total, int rate, string? start, string? end, string? first, string? last)
    {
        if (Seconds(first) is double head and > 0) return (0, (int)Math.Min(total, Math.Round(head * rate)));
        if (Seconds(last) is double tail and > 0) return ((int)Math.Max(0, total - Math.Round(tail * rate)), total);
        int from = (int)Math.Min(total, Math.Round((Seconds(start) ?? 0) * rate));
        int to = Seconds(end) is double stop && stop > 0 ? (int)Math.Min(total, Math.Round(stop * rate)) : total;
        return (from, Math.Max(from, to));
    }

    /// <summary>The kept stretch of a channel.</summary>
    internal static short[] Take(short[] source, (int Start, int End) range) => source[range.Start..range.End];

    /// <summary>How the two sides of a loop join are blended: equal-power keeps the level of unrelated material (rain), linear that of related material, cosine is an S-curve and log keeps both sides loud for most of the overlap.</summary>
    internal enum FadeCurve { EqualPower, Linear, Cosine, Log }

    /// <summary>The curve named in a parameter (empty: equal-power, the default).</summary>
    internal static FadeCurve CurveOf(string? text) => (text ?? string.Empty).Trim().Trim('"').ToLowerInvariant() switch
    {
        "linear" => FadeCurve.Linear,
        "cosine" => FadeCurve.Cosine,
        "log" => FadeCurve.Log,
        _ => FadeCurve.EqualPower,
    };

    /// <summary>The weight of the incoming side at fraction t (0..1) of the overlap; the outgoing side is the matching mirror (equal-power: sin and cos, the others: one minus).</summary>
    private static (double In, double Out) Weights(FadeCurve curve, double t) => curve switch
    {
        FadeCurve.Linear => (t, 1 - t),
        FadeCurve.Cosine => ((1 - Math.Cos(Math.PI * t)) / 2, (1 + Math.Cos(Math.PI * t)) / 2),
        FadeCurve.Log => (Math.Log10(1 + 9 * t), Math.Log10(1 + 9 * (1 - t))),
        _ => (Math.Sin(t * Math.PI / 2), Math.Cos(t * Math.PI / 2)),
    };

    /// <summary>A length as a count of samples: seconds (<c>3</c>, <c>3s</c>), beats (<c>2 beats</c>) or bars (<c>1 bar</c>); empty or not a length gives null.</summary>
    internal static int? SamplesOf(string? text, int rate, int samplesPerBar, double beatsPerBar = 4.0)
    {
        string value = (text ?? string.Empty).Trim().Trim('"').Trim();
        string number = new([.. value.TakeWhile(c => char.IsDigit(c) || c is '.')]);
        string unit = value[number.Length..].Trim().ToLowerInvariant();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n < 0) return null;
        double samples = unit switch
        {
            "bar" or "bars" => n * samplesPerBar,
            "beat" or "beats" => n * samplesPerBar / beatsPerBar,
            "" or "s" => n * rate,
            _ => -1,
        };
        return samples < 0 ? null : (int)Math.Round(samples);
    }

    private static short Blend(short outgoing, short incoming, (double In, double Out) w) => (short)Math.Clamp((int)Math.Round(outgoing * w.Out + incoming * w.In), short.MinValue, short.MaxValue);

    /// <summary>
    /// Samples at the places offset, offset + 1 and so on (relative to the start of the stretch): silence beyond its end, or repeating it when loop is set. Looping: the first pass plays plain (nothing came before it);
    /// from the second pass on the start of a pass is blended with the tail of the previous one over the crossfade. loopFrom and loopTo (in the stretch) cut an intro that plays once before the loop, and an outro
    /// that plays once at the end of the block; both join the loop with the same crossfade.
    /// </summary>
    internal static short[] Place(short[] stretch, long offset, int length, bool loop, int crossfade = 0, FadeCurve curve = FadeCurve.EqualPower, int loopFrom = 0, int loopTo = -1, bool[]? seam = null)
    {
        short[] result = new short[length];
        if (stretch.Length == 0) return result;
        if (!loop)
        {
            for (int i = 0; i < length; i++)
            {
                long at = offset + i;
                if (at >= 0 && at < stretch.Length) result[i] = stretch[at];
            }

            return result;
        }

        int from = Math.Clamp(loopFrom, 0, stretch.Length - 1);
        int to = loopTo < 0 ? stretch.Length : Math.Clamp(loopTo, from + 1, stretch.Length);
        ReadOnlySpan<short> intro = stretch.AsSpan(0, from), body = stretch.AsSpan(from, to - from), outro = stretch.AsSpan(to);
        int fade = Math.Clamp(crossfade, 0, body.Length / 2);
        int period = body.Length - fade;
        (double In, double Out)[] weights = [.. Enumerable.Range(0, fade).Select(j => Weights(curve, (j + 0.5) / fade))];
        int join = Math.Min(fade, intro.Length);
        long start = intro.Length - join;   // where the first pass of the loop begins: it overlaps the end of the intro
        for (int i = 0; i < length; i++)
        {
            long at = offset + i;
            if (at < 0) continue;
            if (at < start) { result[i] = intro[(int)at]; continue; }
            long x = at - start;
            if (at < intro.Length)
            {
                // the intro hands over to the first pass of the loop
                result[i] = join > 0 ? Blend(intro[(int)at], body[(int)x], weights[(int)(x * fade / join)]) : intro[(int)at];
                if (seam is not null && join > 0) seam[i] = true;
                continue;
            }

            long y = at - start;
            if (fade > 0 && y >= period && y % period < fade) { int p = (int)(y % period); result[i] = Blend(body[period + p], body[p], weights[p]); if (seam is not null) seam[i] = true; }
            else result[i] = body[(int)(y % period)];
        }

        if (outro.Length > 0)
        {
            int outroFade = Math.Min(fade, outro.Length);
            long from0 = length - outro.Length;
            for (long i = Math.Max(0, from0); i < length; i++)
            {
                int j = (int)(i - from0);
                result[i] = j < outroFade ? Blend(result[i], outro[j], weights[j * fade / Math.Max(1, outroFade)]) : outro[j];
                if (seam is not null && j < outroFade) seam[i] = true;
            }
        }

        return result;
    }
}

/// <summary>The cut block: keeps a stretch of its input (from a start to an end, or the first or last seconds) and plays it from the start of the block, once or repeated.</summary>
internal sealed class CutRenderer : IBlockRenderer
{
    public string BlockType => "cut";
    public string Id => "builtin.cut";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        byte[] input = request.Inputs.TryGetValue("in", out byte[]? connected) ? connected : Wav.Encode(new short[request.BarCount * request.SamplesPerBar], request.SampleRate);
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        if (Value("enabled") == "off") return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", input), new RenderOutput("out", input)]);
        (int Start, int End) range = AudioCut.Range(channels.Length, rate, Value("start"), Value("end"), Value("first"), Value("last"));
        bool loop = Value("loop") == "on";
        StereoBuffer cut = new(AudioCut.Place(AudioCut.Take(channels.Left, range), 0, channels.Length, loop), AudioCut.Place(AudioCut.Take(channels.Right, range), 0, channels.Length, loop));
        byte[] output = StereoWav.Encode(cut, rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", output), new RenderOutput("out", output)]);
    }
}

/// <summary>The shift block: moves its input later (or earlier) in time by some seconds or beats, so one block can start a little late without moving it on the timeline. What moves past the end of the block is cut off.</summary>
/// <summary>Puts the connected inputs one after the other with a gap between them (in1, then in2, ...): spoken phrases with pauses, hits in a row.</summary>
internal sealed class JoinRenderer : IBlockRenderer
{
    public string BlockType => "join";
    public string Id => "builtin.join";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        List<(StereoBuffer Buffer, int Rate)> parts = [];
        for (int i = 1; i <= 8; i++)
        {
            if (request.Inputs.TryGetValue("in" + i, out byte[]? input)) parts.Add(ChannelAudio.Decode(input));
        }

        int rate = parts.Count > 0 ? parts[0].Rate : request.SampleRate;
        if (Value("enabled") == "off" || parts.Count == 0)
        {
            byte[] first = parts.Count > 0 ? StereoWav.Encode(parts[0].Buffer, rate) : StereoWav.Encode(new StereoBuffer(new short[request.BarCount * request.SamplesPerBar], new short[request.BarCount * request.SamplesPerBar]), rate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", first), new RenderOutput("out", first)]);
        }

        // the gap is seconds plus beats (a beat is a quarter of a bar); it never goes below zero
        long gap = Math.Max(0, (long)Math.Round((Number(Value("seconds")) * rate) + (Number(Value("beats")) * request.SamplesPerBar / request.BeatsPerBar)));
        long total = parts.Sum(p => (long)p.Buffer.Length) + (gap * (parts.Count - 1));
        if (total > 600L * rate) throw new AmpleException("AMP5100", "The joined sound would be longer than ten minutes.");
        short[] left = new short[total], right = new short[total];
        long at = 0;
        foreach ((StereoBuffer buffer, int _) in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Copy(buffer.Left, 0, left, at, buffer.Length);
            Array.Copy(buffer.Right, 0, right, at, buffer.Length);
            at += buffer.Length + gap;
        }

        byte[] output = StereoWav.Encode(new StereoBuffer(left, right), rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", output), new RenderOutput("out", output)]);
    }

    private static double Number(string text)
    {
        string value = text.Trim().Trim('"').TrimEnd('s', 'S').Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }
}

internal sealed class ShiftRenderer : IBlockRenderer
{
    public string BlockType => "shift";
    public string Id => "builtin.shift";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        byte[] input = request.Inputs.TryGetValue("in", out byte[]? connected) ? connected : Wav.Encode(new short[request.BarCount * request.SamplesPerBar], request.SampleRate);
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        if (Value("enabled") == "off") return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", input), new RenderOutput("out", input)]);
        // a beat is a quarter of a bar; seconds and beats add up, and a minus moves the sound earlier
        double seconds = Signed(Value("seconds"));
        double beats = Signed(Value("beats"));
        long shift = (long)Math.Round((seconds * rate) + (beats * request.SamplesPerBar / request.BeatsPerBar));
        // the output is as long as the block itself, so a sound can be moved past the end of its input (a delay of 30 seconds)
        int outputLength = Math.Max(channels.Length, request.BarCount * request.SamplesPerBar);
        short[] Move(short[] source)
        {
            short[] moved = new short[outputLength];
            for (int i = 0; i < moved.Length; i++)
            {
                long from = i - shift;
                if (from >= 0 && from < source.Length) moved[i] = source[from];
            }

            return moved;
        }

        byte[] output = StereoWav.Encode(new StereoBuffer(Move(channels.Left), Move(channels.Right)), rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", output), new RenderOutput("out", output)]);
    }

    private static double Signed(string text)
    {
        string value = text.Trim().Trim('"').TrimEnd('s', 'S').Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }
}

/// <summary>The region-merge block: the one place where a region (the joins of a loop, after its effects) becomes audio again. With the body connected it is body plus region; with only the region it is the processed region alone.</summary>
internal sealed class RegionMergeRenderer : IBlockRenderer
{
    public string BlockType => "region-merge";
    public string Id => "builtin.region-merge";
    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        List<(StereoBuffer Buffer, int Rate)> parts = [];
        foreach (string name in new[] { "body", "region" })
        {
            if (request.Inputs.TryGetValue(name, out byte[]? input)) parts.Add(ChannelAudio.Decode(input));
        }

        int rate = parts.Count > 0 ? parts[0].Rate : request.SampleRate;
        int length = parts.Count > 0 ? parts.Max(p => p.Buffer.Length) : request.BarCount * request.SamplesPerBar;
        short[] left = new short[length], right = new short[length];
        foreach ((StereoBuffer buffer, _) in parts)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                left[i] = (short)Math.Clamp(left[i] + buffer.Left[i], short.MinValue, short.MaxValue);
                right[i] = (short)Math.Clamp(right[i] + buffer.Right[i], short.MinValue, short.MaxValue);
            }
        }

        byte[] output = StereoWav.Encode(new StereoBuffer(left, right), rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", output), new RenderOutput("out", output)]);
    }
}
