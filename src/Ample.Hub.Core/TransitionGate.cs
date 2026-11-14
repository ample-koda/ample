namespace Ample.Hub.Core;

/// <summary>
/// Lets one transition (start, stop, restart) run at a time; a second request is turned away, never queued, so nothing
/// waits on anything.
/// </summary>
public sealed class TransitionGate
{
    private int running;

    /// <summary>Gets a value indicating whether a transition is under way.</summary>
    public bool IsBusy => Volatile.Read(ref running) == 1;

    /// <summary>
    /// Runs <paramref name="work"/> when no transition is under way; the gate is released on every exit (done, failed,
    /// cancelled).
    /// </summary>
    /// <param name="work">The transition.</param>
    /// <returns>False when another transition was under way (the work did not run).</returns>
    public async Task<bool> TryRunAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Interlocked.Exchange(ref running, 1) == 1) return false;
        try
        {
            await work().ConfigureAwait(false);
            return true;
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }
}
