namespace Ample.Hub.Core.Tests;

public sealed class TransitionGateTests
{
    [Fact]
    public async Task A_second_transition_is_turned_away_while_one_runs_and_allowed_after()
    {
        TransitionGate gate = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int second = 0;

        Task<bool> first = gate.TryRunAsync(() => release.Task);
        Assert.True(gate.IsBusy);
        bool ranSecond = await gate.TryRunAsync(() => { second++; return Task.CompletedTask; });

        Assert.False(ranSecond);
        Assert.Equal(0, second);
        release.SetResult();
        Assert.True(await first);
        Assert.False(gate.IsBusy);
        Assert.True(await gate.TryRunAsync(() => { second++; return Task.CompletedTask; }));
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task The_gate_is_released_when_the_work_throws()
    {
        TransitionGate gate = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TryRunAsync(() => throw new InvalidOperationException("boom")));

        Assert.False(gate.IsBusy);
        Assert.True(await gate.TryRunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task The_gate_is_released_when_the_work_throws_after_an_await()
    {
        TransitionGate gate = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TryRunAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("late");
        }));

        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task The_gate_is_released_when_the_work_is_cancelled()
    {
        TransitionGate gate = new();
        using CancellationTokenSource cancel = new();
        Task<bool> run = gate.TryRunAsync(() => Task.Delay(Timeout.Infinite, cancel.Token));

        Assert.True(gate.IsBusy);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task A_transition_started_from_inside_another_one_is_turned_away_and_does_not_deadlock()
    {
        TransitionGate gate = new();
        bool inner = true;

        bool outer = await gate.TryRunAsync(async () => inner = await gate.TryRunAsync(() => Task.CompletedTask));

        Assert.True(outer);
        Assert.False(inner);
        Assert.False(gate.IsBusy);
    }
}
