using System.Diagnostics;

namespace Ample.Hub.Core.Tests;

public sealed class ReadinessWaiterTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan span, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Now += span;
            return Task.CompletedTask;
        }
    }

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task A_value_is_returned_as_soon_as_a_probe_gives_one()
    {
        FakeClock clock = new();
        int looks = 0;

        string? found = await ReadinessWaiter.WaitAsync<string>(
            _ => Task.FromResult(++looks == 3 ? "ready" : null), () => clock.Now, clock.Delay,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), Pause, CancellationToken.None);

        Assert.Equal("ready", found);
        Assert.Equal(3, looks);
    }

    [Fact]
    public async Task Null_comes_back_when_the_deadline_passes_and_the_looks_stop()
    {
        FakeClock clock = new();
        int looks = 0;

        string? found = await ReadinessWaiter.WaitAsync<string>(
            _ => { looks++; return Task.FromResult<string?>(null); }, () => clock.Now, clock.Delay,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), Pause, CancellationToken.None);

        Assert.Null(found);
        Assert.InRange(looks, 4, 5);
    }

    [Fact]
    public async Task One_slow_look_is_cut_off_and_the_wait_goes_on()
    {
        FakeClock clock = new();
        int looks = 0;

        string? found = await ReadinessWaiter.WaitAsync<string>(
            async token =>
            {
                if (++looks == 1) await Task.Delay(Timeout.Infinite, token);
                return "ready";
            },
            () => clock.Now,
            clock.Delay,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            Pause,
            CancellationToken.None);

        Assert.Equal("ready", found);
        Assert.Equal(2, looks);
    }

    [Fact]
    public async Task A_look_never_runs_past_the_deadline()
    {
        Stopwatch watch = Stopwatch.StartNew();

        string? found = await ReadinessWaiter.WaitAsync<string>(
            async token => { await Task.Delay(Timeout.Infinite, token); return "never"; }, () => DateTime.UtcNow, Task.Delay,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), Pause, CancellationToken.None);

        Assert.Null(found);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "the 30 s look limit must not outlast the 200 ms deadline");
    }

    [Fact]
    public async Task Cancelling_the_token_ends_the_wait_with_a_cancellation()
    {
        FakeClock clock = new();
        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadinessWaiter.WaitAsync<string>(
            _ =>
            {
                cancel.Cancel();
                return Task.FromResult<string?>(null);
            },
            () => clock.Now,
            clock.Delay,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5),
            Pause,
            cancel.Token));
    }

    [Fact]
    public async Task A_cancel_during_a_look_is_not_mistaken_for_a_slow_look()
    {
        FakeClock clock = new();
        using CancellationTokenSource cancel = new();
        Task<string?> wait = ReadinessWaiter.WaitAsync<string>(
            async token => { await Task.Delay(Timeout.Infinite, token); return "never"; }, () => clock.Now, clock.Delay,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), Pause, cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
