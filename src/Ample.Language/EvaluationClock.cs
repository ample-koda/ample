using System.Diagnostics;

namespace Ample.Language;

/// <summary>Measures time spent in the musical evaluator, excluding time in isolated forks.</summary>
internal sealed class EvaluationClock(Func<long> now)
{
    private long started = now();
    private long elapsedTicks;
    private int pauseDepth;

    internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(0, elapsedTicks + (pauseDepth == 0 ? now() - started : 0));

    internal void Pause()
    {
        if (pauseDepth++ == 0) elapsedTicks += now() - started;
    }

    internal void Resume()
    {
        if (--pauseDepth == 0) started = now();
    }
}
