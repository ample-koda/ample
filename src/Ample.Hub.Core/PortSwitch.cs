using System.Globalization;

namespace Ample.Hub.Core;

/// <summary>What <see cref="PortSwitch"/> needs from the main instance (a small seam so the decision logic runs without a server).</summary>
public interface IPortSwitchHost
{
    /// <summary>Gets the port stored in the settings.</summary>
    int StoredPort { get; }

    /// <summary>Gets the port the main server runs on now, or null when it does not run.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The port or null.</returns>
    Task<int?> RunningPortAsync(CancellationToken cancellationToken);

    /// <summary>Checks that a port can be bound now.</summary>
    /// <param name="port">The port (0 is always free).</param>
    /// <returns>True when free.</returns>
    bool IsPortFree(int port);

    /// <summary>Stores the port in the settings (only that field).</summary>
    /// <param name="port">The port.</param>
    void StorePort(int port);

    /// <summary>Stops, then starts the main server; null when done, else the reason (then the old server may still run).</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Null or the reason.</returns>
    Task<string?> RestartAsync(CancellationToken cancellationToken);

    /// <summary>Waits until the main server runs on the port (0: any port).</summary>
    /// <param name="port">The port.</param>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>True when it runs there.</returns>
    Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Changes the port of the main instance: the new port is only kept when the server comes up on it.</summary>
public static class PortSwitch
{
    /// <summary>The longest wait for the server on the new port.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Restarts on a new port, or restores the old port.</summary>
    /// <param name="host">The main instance.</param>
    /// <param name="newPort">The new port.</param>
    /// <param name="cancellationToken">Cancels (the old port is restored first).</param>
    /// <returns>Null when done, else the reason.</returns>
    public static async Task<string?> RunAsync(IPortSwitchHost host, int newPort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        int old = host.StoredPort;
        int? running = await host.RunningPortAsync(cancellationToken).ConfigureAwait(false);
        if (running != newPort && !host.IsPortFree(newPort))
            return "Port " + Text(newPort) + " is already in use by another program. The port was not changed.";

        host.StorePort(newPort);
        string? problem;
        try
        {
            problem = await host.RestartAsync(cancellationToken).ConfigureAwait(false);
            if (problem is null && await host.WaitReadyAsync(newPort, ReadyTimeout, cancellationToken).ConfigureAwait(false)) return null;
        }
        catch (OperationCanceledException)
        {
            host.StorePort(old);
            throw;
        }

        host.StorePort(old);
        // a stop that failed leaves the old server running on the old port: there is nothing to start again
        if (problem is not null) return problem + " The old port " + Text(old) + " was kept.";
        string? again = await host.RestartAsync(cancellationToken).ConfigureAwait(false);
        return "AMPLE did not start on port " + Text(newPort) + ". The old port " + Text(old) + " was restored" + (again is null ? "." : " but AMPLE could not be restarted: " + again);
    }

    private static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);
}
