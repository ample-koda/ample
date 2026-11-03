using System.Diagnostics;
using Ample.Audio.Engine;

namespace Ample.Engine.Engine;

/// <summary>Collects what a renderer reports about its progress and passes it on to the build's listener, at most about ten times a second (a change of sub-step always goes through).</summary>
internal sealed class ProgressTracker(string block, Action<BlockProgress>? listener)
{
    private readonly object gate = new();
    private readonly List<StepProgress> steps = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long lastSent = -1000;
    private double fraction;
    private string? lastStep;

    /// <summary>Takes one report from the renderer.</summary>
    public void Report(ProgressReport report)
    {
        if (listener is null)
        {
            return;
        }

        BlockProgress? send = null;
        lock (gate)
        {
            if (report.Fraction is { } f)
            {
                fraction = Math.Clamp(f, 0, 1);
            }

            bool newStep = false;
            if (report.Step is { Length: > 0 } step)
            {
                if (step != lastStep && lastStep is not null)
                {
                    int previous = steps.FindIndex(s => s.Name == lastStep);
                    if (previous >= 0 && steps[previous].Fraction != 1) steps[previous] = steps[previous] with { Fraction = 1 };
                }

                int i = steps.FindIndex(s => s.Name == step);
                StepProgress updated = new(step, report.StepFraction is { } sf ? Math.Clamp(sf, 0, 1) : null);
                if (i < 0)
                {
                    steps.Add(updated);
                }
                else
                {
                    steps[i] = updated;
                }

                newStep = step != lastStep || report.StepFraction is 1;
                lastStep = step;
            }

            long now = clock.ElapsedMilliseconds;
            if (newStep || now - lastSent >= 100)
            {
                lastSent = now;
                send = new BlockProgress(block, "running", fraction, [.. steps]);
            }
        }

        if (send is not null)
        {
            listener(send);
        }
    }
}
