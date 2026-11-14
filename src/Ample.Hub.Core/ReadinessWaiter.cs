namespace Ample.Hub.Core;

/// <summary>Polls until something is ready or a deadline passes. A probe has its own limit (and never runs past the deadline); cancelling the token ends the wait.</summary>
public static class ReadinessWaiter
{
    /// <summary>Waits for a probe to give a value.</summary>
    /// <typeparam name="T">The kind of value a ready probe gives.</typeparam>
    /// <param name="probe">One look; null means not ready yet.</param>
    /// <param name="utcNow">The clock.</param>
    /// <param name="delay">How to pause between two looks.</param>
    /// <param name="timeout">The total time to wait.</param>
    /// <param name="probeLimit">The longest a single look may take.</param>
    /// <param name="pause">The time between two looks.</param>
    /// <param name="cancellationToken">Ends the wait (an <see cref="OperationCanceledException"/>).</param>
    /// <returns>The value, or null when the deadline passed.</returns>
    public static async Task<T?> WaitAsync<T>(Func<CancellationToken, Task<T?>> probe, Func<DateTime> utcNow, Func<TimeSpan, CancellationToken, Task> delay, TimeSpan timeout, TimeSpan probeLimit, TimeSpan pause, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delay);
        DateTime until = utcNow() + timeout;
        while (true)
        {
            TimeSpan left = until - utcNow();
            if (left <= TimeSpan.Zero) return null;
            using CancellationTokenSource look = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            look.CancelAfter(left < probeLimit ? left : probeLimit);
            try
            {
                T? found = await probe(look.Token).ConfigureAwait(false);
                if (found is not null) return found;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // one slow look: the deadline above still holds
            }

            TimeSpan rest = until - utcNow();
            if (rest <= TimeSpan.Zero) return null;
            await delay(rest < pause ? rest : pause, cancellationToken).ConfigureAwait(false);
        }
    }
}
