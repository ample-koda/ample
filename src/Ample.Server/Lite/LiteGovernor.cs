namespace Ample.Server.Lite;

/// <summary>
/// Abuse protection and a small queue for Lite. A client is known by two keys (a random cookie and the address, see <see cref="LiteClient"/>) and every limit has to hold for both,
/// so neither clearing cookies nor sharing an address gets around it. It keeps: sliding windows (a build and a download may happen only so many times a minute and an hour, and
/// only so many encoded bytes an hour), a first-come-first-served queue for builds (a few run, a few wait, the others are refused at once; a client has at most one build running
/// or waiting) and a slot for the encoder (one at a time, a few may wait). Nothing is stored on disk and a restart resets it.
/// </summary>
public sealed class LiteGovernor : IDisposable
{
    private readonly LiteLimits limits;
    private readonly object sync = new();
    private readonly Dictionary<string, Queue<(DateTime At, long Weight)>> hits = new(StringComparer.Ordinal);
    private readonly List<Guid> waiting = [];
    private readonly HashSet<string> active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim builds;
    private readonly SemaphoreSlim encodes;
    private readonly Dictionary<string, int> sockets = new(StringComparer.Ordinal);
    private int encodeWaiting;

    /// <summary>Initializes a new instance of the <see cref="LiteGovernor"/> class.</summary>
    /// <param name="limits">The limits.</param>
    public LiteGovernor(LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
        builds = new SemaphoreSlim(limits.ConcurrentBuilds, limits.ConcurrentBuilds);
        encodes = new SemaphoreSlim(limits.ConcurrentEncodes, limits.ConcurrentEncodes);
    }

    /// <summary>Frees the semaphores.</summary>
    public void Dispose()
    {
        builds.Dispose();
        encodes.Dispose();
    }

    /// <summary>Gets how many builds wait for their turn.</summary>
    public int Waiting
    {
        get
        {
            lock (sync) return waiting.Count;
        }
    }

    /// <summary>
    /// Counts one action (<paramref name="kind"/>) for every key and refuses it when any key has done it too often in the last minute or hour.
    /// </summary>
    /// <param name="kind">What is counted, for example <c>build</c> or <c>download</c>.</param>
    /// <param name="keys">The keys of the client.</param>
    /// <param name="perMinute">How many are allowed a minute.</param>
    /// <param name="perHour">How many are allowed an hour.</param>
    /// <exception cref="LiteRejectedException">Too many (status 429, with the seconds to wait).</exception>
    public void Spend(string kind, IReadOnlyList<string> keys, int perMinute, int perHour) =>
        Spend(kind, keys, [(TimeSpan.FromMinutes(1), perMinute, "a minute"), (TimeSpan.FromHours(1), perHour, "an hour")]);

    /// <summary>Counts one more of a kind for a client and refuses when any of the windows is full (a window of 0 or less is not used). The windows are at most an hour long.</summary>
    /// <param name="kind">What is counted (build, download ...).</param>
    /// <param name="keys">The keys of the client.</param>
    /// <param name="rules">The windows: how long, how many are allowed in it, and how to call it in the message ("a minute", "5 minutes").</param>
    /// <exception cref="LiteRejectedException">A window is full (status 429, with the seconds to wait).</exception>
    public void Spend(string kind, IReadOnlyList<string> keys, IReadOnlyList<(TimeSpan Window, int Max, string Label)> rules)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(rules);
        DateTime now = DateTime.UtcNow;
        lock (sync)
        {
            foreach (string key in keys)
            {
                Queue<(DateTime At, long Weight)> queue = Window(kind + "|" + key, now);
                foreach ((TimeSpan window, int max, string label) in rules)
                {
                    if (max <= 0) continue;
                    (int used, DateTime oldest) = Count(queue, now - window);
                    if (used >= max) throw Slow(kind, label, (int)Math.Ceiling((oldest + window - now).TotalSeconds));
                }
            }

            foreach (string key in keys) Window(kind + "|" + key, now).Enqueue((now, 1));
        }
    }

    /// <summary>Counts the bytes a client downloads (encoded audio) and refuses when the hour's budget is spent.</summary>
    /// <param name="keys">The keys of the client.</param>
    /// <param name="bytes">The size of this download.</param>
    /// <exception cref="LiteRejectedException">The budget is spent (status 429).</exception>
    public void SpendBytes(IReadOnlyList<string> keys, long bytes)
    {
        ArgumentNullException.ThrowIfNull(keys);
        DateTime now = DateTime.UtcNow;
        long budget = limits.EncodedMegabytesPerHour * 1024 * 1024;
        lock (sync)
        {
            foreach (string key in keys)
            {
                Queue<(DateTime At, long Weight)> queue = Window("bytes|" + key, now);
                long used = queue.Sum(h => h.Weight);
                if (used + bytes > budget) throw new LiteRejectedException("AMPL932", 429, $"You have downloaded {used / 1024 / 1024} MB of encoded audio in the last hour; the limit is {limits.EncodedMegabytesPerHour} MB an hour. Try again in {Math.Max(1, (int)Math.Ceiling((queue.Peek().At.AddHours(1) - now).TotalMinutes))} minutes.") { RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling((queue.Peek().At.AddHours(1) - now).TotalSeconds)) };
            }

            foreach (string key in keys) Window("bytes|" + key, now).Enqueue((now, bytes));
        }
    }

    /// <summary>
    /// Takes a place in the build queue and waits for a free build slot, first come first served. <paramref name="progress"/> hears the place in the queue (1 is next) and how many wait
    /// whenever the place changes. The returned object frees the slot when it is disposed.
    /// </summary>
    /// <param name="keys">The keys of the client (a client may have one build running or waiting).</param>
    /// <param name="progress">Told the place and the number waiting, or null.</param>
    /// <param name="cancellationToken">Cancelled when the page goes away: the place is given up.</param>
    /// <returns>The slot.</returns>
    /// <exception cref="LiteRejectedException">The client already has a build, the queue is full or the wait took too long (status 429).</exception>
    public async Task<IDisposable> EnterBuildAsync(IReadOnlyList<string> keys, Func<int, int, Task>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Guid me = Guid.NewGuid();
        lock (sync)
        {
            if (keys.Any(active.Contains)) throw new LiteRejectedException("AMPL933", 429, "You already have a build running or waiting: wait for it to finish (or cancel it) first.") { RetryAfterSeconds = 5 };
            if (waiting.Count >= limits.QueueWaiting) throw new LiteRejectedException("AMPL930", 429, $"Remix is busy: {waiting.Count} builds are waiting. Try again in a minute.") { RetryAfterSeconds = 30 };
            foreach (string key in keys) active.Add(key);
            waiting.Add(me);
        }

        DateTime until = DateTime.UtcNow.AddSeconds(limits.QueueWaitSeconds);
        int shown = -1;
        try
        {
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();
                int place, total;
                lock (sync)
                {
                    place = waiting.IndexOf(me) + 1;
                    total = waiting.Count;
                }

                if (place == 1 && await builds.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    lock (sync) waiting.Remove(me);
                    return new Releaser(() =>
                    {
                        builds.Release();
                        lock (sync)
                        {
                            foreach (string key in keys) active.Remove(key);
                        }
                    });
                }

                if (place != shown && progress is not null)
                {
                    shown = place;
                    await progress(place, total).ConfigureAwait(false);
                }

                if (DateTime.UtcNow > until) throw new LiteRejectedException("AMPL930", 429, $"Your build waited {limits.QueueWaitSeconds} seconds in the queue without a turn: Remix is busy, try again in a minute.") { RetryAfterSeconds = 30 };
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (sync)
            {
                waiting.Remove(me);
                foreach (string key in keys) active.Remove(key);
            }

            throw;
        }
    }

    /// <summary>Counts an open language-server connection for every key of a client and refuses one more than <paramref name="max"/>.</summary>
    /// <param name="keys">The keys of the client.</param>
    /// <param name="max">How many connections one client may hold.</param>
    /// <returns>The slot, freed when disposed (when the connection ends).</returns>
    /// <exception cref="LiteRejectedException">Too many connections (status 429).</exception>
    public IDisposable EnterSocket(IReadOnlyList<string> keys, int max)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (sync)
        {
            if (keys.Any(k => sockets.GetValueOrDefault(k) >= (k.StartsWith("ip:", StringComparison.Ordinal) ? max * 3 : max))) throw new LiteRejectedException("AMPL932", 429, $"Too many open editor connections ({max} at most): close another tab of Remix.") { RetryAfterSeconds = 10 };
            foreach (string key in keys) sockets[key] = sockets.GetValueOrDefault(key) + 1;
        }

        return new Releaser(() =>
        {
            lock (sync)
            {
                foreach (string key in keys)
                {
                    int left = sockets.GetValueOrDefault(key) - 1;
                    if (left <= 0) sockets.Remove(key); else sockets[key] = left;
                }
            }
        });
    }

    /// <summary>Takes the slot of the encoder (one at a time, a few may wait).</summary>
    /// <param name="cancellationToken">Cancelled when the page goes away.</param>
    /// <returns>The slot, freed when disposed.</returns>
    /// <exception cref="LiteRejectedException">The encoder is busy (status 429).</exception>
    public async Task<IDisposable> EnterEncodeAsync(CancellationToken cancellationToken)
    {
        if (await encodes.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new Releaser(() => encodes.Release());   // a free encoder: no waiting at all
        lock (sync)
        {
            if (encodeWaiting >= limits.EncodeWaiting) throw new LiteRejectedException("AMPL930", 429, "The encoder is busy with other downloads: try again in a moment.") { RetryAfterSeconds = 15 };
            encodeWaiting++;
        }

        try
        {
            if (!await encodes.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)) throw new LiteRejectedException("AMPL930", 429, "The encoder is busy with other downloads: try again in a moment.") { RetryAfterSeconds = 15 };
        }
        finally
        {
            lock (sync) encodeWaiting--;
        }

        return new Releaser(() => encodes.Release());
    }

    private static LiteRejectedException Slow(string kind, string per, int seconds) =>
        new("AMPL932", 429, $"Too many {kind}s: Remix allows only so many in {per}. Try again in {Math.Max(1, seconds)} seconds.") { RetryAfterSeconds = Math.Max(1, seconds) };

    private static (int Count, DateTime Oldest) Count(Queue<(DateTime At, long Weight)> queue, DateTime since)
    {
        int count = 0;
        DateTime oldest = DateTime.MaxValue;
        foreach ((DateTime at, long _) in queue)
        {
            if (at < since) continue;
            count++;
            if (at < oldest) oldest = at;
        }

        return (count, oldest == DateTime.MaxValue ? DateTime.UtcNow : oldest);
    }

    private Queue<(DateTime At, long Weight)> Window(string key, DateTime now)
    {
        if (!hits.TryGetValue(key, out Queue<(DateTime At, long Weight)>? queue)) hits[key] = queue = new Queue<(DateTime At, long Weight)>();
        while (queue.Count > 0 && queue.Peek().At < now.AddHours(-1)) queue.Dequeue();
        if (hits.Count > 5000)
        {
            foreach (string stale in hits.Where(h => h.Value.Count == 0 && h.Key != key).Select(h => h.Key).ToList()) hits.Remove(stale);
        }

        return queue;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) release();
        }
    }
}
