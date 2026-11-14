namespace Ample.Hub.Core.Tests;

public sealed class PortSwitchTests
{
    private sealed class FakeHost : IPortSwitchHost
    {
        public int StoredPort { get; set; } = 5000;

        public int? Running { get; set; } = 5000;

        public HashSet<int> Busy { get; } = [];

        public List<string> Calls { get; } = [];

        public Queue<string?> RestartResults { get; } = new();

        public Func<int, bool> ReadyOn { get; set; } = _ => true;

        public Task<int?> RunningPortAsync(CancellationToken cancellationToken) => Task.FromResult(Running);

        public bool IsPortFree(int port) => !Busy.Contains(port);

        public void StorePort(int port)
        {
            StoredPort = port;
            Calls.Add("store " + port);
        }

        public Task<string?> RestartAsync(CancellationToken cancellationToken)
        {
            Calls.Add("restart@" + StoredPort);
            return Task.FromResult(RestartResults.Count > 0 ? RestartResults.Dequeue() : null);
        }

        public Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("ready? " + port);
            return Task.FromResult(ReadyOn(port));
        }
    }

    [Fact]
    public async Task A_new_port_is_stored_and_kept_when_the_server_comes_up_on_it()
    {
        FakeHost host = new();

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(6000, host.StoredPort);
        Assert.Equal(["store 6000", "restart@6000", "ready? 6000"], host.Calls);
    }

    [Fact]
    public async Task A_busy_port_is_refused_before_anything_is_written_or_stopped()
    {
        FakeHost host = new();
        host.Busy.Add(6000);

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Contains("already in use", problem, StringComparison.Ordinal);
        Assert.Equal(5000, host.StoredPort);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task The_port_the_server_already_runs_on_is_not_probed_as_busy()
    {
        FakeHost host = new() { Running = 6000, StoredPort = 0 };
        host.Busy.Add(6000);

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(6000, host.StoredPort);
    }

    [Fact]
    public async Task When_the_server_never_gets_ready_on_the_new_port_the_old_port_is_restored_and_started_again()
    {
        FakeHost host = new() { ReadyOn = port => port == 5000 };

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Contains("did not start on port 6000", problem, StringComparison.Ordinal);
        Assert.Contains("old port 5000 was restored", problem, StringComparison.Ordinal);
        Assert.Equal(5000, host.StoredPort);
        Assert.Equal(["store 6000", "restart@6000", "ready? 6000", "store 5000", "restart@5000"], host.Calls);
    }

    [Fact]
    public async Task A_busy_port_that_only_fails_at_bind_time_takes_the_same_restore_path()
    {
        // the preflight said free, the server could not bind (taken in between): it never registers on the new port
        FakeHost host = new() { ReadyOn = port => port != 6000 };

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Equal(5000, host.StoredPort);
        Assert.Equal("restart@5000", host.Calls[^1]);
    }

    [Fact]
    public async Task A_failed_restart_of_the_old_port_is_reported()
    {
        FakeHost host = new() { ReadyOn = _ => false };
        host.RestartResults.Enqueue(null);
        host.RestartResults.Enqueue("cannot launch");

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Contains("could not be restarted: cannot launch", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_stop_fails_the_old_server_still_runs_so_the_port_is_restored_without_another_restart()
    {
        FakeHost host = new();
        host.RestartResults.Enqueue("it would not stop");

        string? problem = await PortSwitch.RunAsync(host, 6000, CancellationToken.None);

        Assert.Contains("it would not stop", problem, StringComparison.Ordinal);
        Assert.Contains("old port 5000 was kept", problem, StringComparison.Ordinal);
        Assert.Equal(5000, host.StoredPort);
        Assert.Equal(1, host.Calls.Count(c => c.StartsWith("restart", StringComparison.Ordinal)));
        Assert.DoesNotContain("ready? 6000", host.Calls);
    }

    [Fact]
    public async Task A_cancel_restores_the_old_port_before_it_propagates()
    {
        FakeHost host = new();
        using CancellationTokenSource cancel = new();
        host.ReadyOn = _ => throw new OperationCanceledException(cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortSwitch.RunAsync(host, 6000, cancel.Token));

        Assert.Equal(5000, host.StoredPort);
    }
}
