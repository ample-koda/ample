namespace Ample.Speech;

/// <summary>A segment and its calibrated Humanico acoustic target.</summary>
public sealed record HumanicoTargetSegment(SpeechSegment Segment, IReadOnlyDictionary<string, double> Target);

/// <summary>Plans 5 ms target frames using the pinned Humanico transition equation.</summary>
public static class HumanicoTargetFrames
{
    /// <summary>Plans transitions without driving omitted filter parameters toward zero.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, double>> PlanStable(
        IReadOnlyList<HumanicoTargetSegment> segments, double rate, int maxFrames, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(rate) || rate is < 0.1 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        token.ThrowIfCancellationRequested();
        Dictionary<string, double> effective = new(StringComparer.Ordinal)
        {
            ["av"] = 0,
            ["ah"] = 0,
            ["af"] = 0,
            ["ab"] = 0,
            ["a2"] = 0,
            ["a3"] = 0,
            ["a4"] = 0,
            ["a5"] = 0,
            ["a6"] = 0,
            ["f1"] = 500,
            ["f2"] = 1500,
            ["f3"] = 2500,
            ["f4"] = 3900,
            ["f5"] = 4500,
            ["f6"] = 4800,
            ["b1"] = 60,
            ["b2"] = 90,
            ["b3"] = 150,
            ["b4"] = 250,
            ["b5"] = 300,
            ["fnp"] = 270,
            ["bnp"] = 270,
            ["fnz"] = 270,
            ["bnz"] = 270
        };
        List<IReadOnlyDictionary<string, double>> frames = [];
        bool first = true;
        foreach (HumanicoTargetSegment item in segments)
        {
            token.ThrowIfCancellationRequested();
            double duration = item.Segment.DurationMilliseconds / rate;
            int count = Math.Max(1, checked((int)Math.Round(duration / 5, MidpointRounding.ToEven)));
            int transition = Math.Max(0, checked((int)Math.Round(
                Math.Min(item.Segment.TransitionMilliseconds, duration * 0.6) / 5, MidpointRounding.ToEven)));
            if (maxFrames > 0 && (long)frames.Count + count > maxFrames)
                throw new SpeechResourceLimitException("Humanico frame plan exceeds the configured frame limit.");
            Dictionary<string, double> next = new(effective, StringComparer.Ordinal);
            foreach ((string key, double value) in item.Target) next[key] = value;
            for (int i = 0; i < count; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                Dictionary<string, double> values = new(next, StringComparer.Ordinal);
                if (!first && i < transition)
                {
                    double mix = (double)(i + 1) / (transition + 1);
                    foreach ((string key, double end) in item.Target)
                    {
                        double start = effective.GetValueOrDefault(key, end);
                        values[key] = start + (end - start) * mix;
                    }
                }
                frames.Add(values);
            }
            effective = next;
            first = false;
        }
        return frames;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, double>> Plan(
        IReadOnlyList<HumanicoTargetSegment> segments, double rate, int maxFrames, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(rate) || rate is < 0.1 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        token.ThrowIfCancellationRequested();
        List<IReadOnlyDictionary<string, double>> frames = [];
        IReadOnlyDictionary<string, double>? previous = null;
        foreach (HumanicoTargetSegment item in segments)
        {
            token.ThrowIfCancellationRequested();
            double duration = item.Segment.DurationMilliseconds / rate;
            int count = Math.Max(1, checked((int)Math.Round(duration / 5, MidpointRounding.ToEven)));
            int transition = Math.Max(0, checked((int)Math.Round(
                Math.Min(item.Segment.TransitionMilliseconds, duration * 0.6) / 5, MidpointRounding.ToEven)));
            if (maxFrames > 0 && (long)frames.Count + count > maxFrames)
                throw new SpeechResourceLimitException("Humanico frame plan exceeds the configured frame limit.");
            previous ??= item.Target;
            string[] keys = [.. previous.Keys.Union(item.Target.Keys, StringComparer.Ordinal)];
            for (int i = 0; i < count; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                Dictionary<string, double> values = new(item.Target, StringComparer.Ordinal);
                if (i < transition && !ReferenceEquals(previous, item.Target))
                {
                    double mix = (double)(i + 1) / (transition + 1);
                    foreach (string key in keys)
                    {
                        double start = previous.GetValueOrDefault(key);
                        double end = item.Target.GetValueOrDefault(key);
                        values[key] = start + (end - start) * mix;
                    }
                }
                frames.Add(values);
            }
            previous = item.Target;
        }
        return frames;
    }
}
