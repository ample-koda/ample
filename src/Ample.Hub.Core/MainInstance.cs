using System.Globalization;
using Ample.Audio.IO;
using Ample.Storage.Projects;

namespace Ample.Hub.Core;

/// <summary>The main instance: the server the hub starts itself, for the person's own project in the AMPLE home.</summary>
public sealed class MainInstance
{
    private readonly string ampleExe;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Creates the manager.</summary>
    /// <param name="home">The AMPLE home folder.</param>
    /// <param name="ampleExe">The path of ample.exe.</param>
    public MainInstance(string home, string ampleExe)
    {
        Home = home;
        this.ampleExe = ampleExe;
        Registry = new InstanceRegistry(home, new SystemClock(), new SystemInstanceProbe());
    }

    /// <summary>Gets the registry the hub watches.</summary>
    public InstanceRegistry Registry { get; }

    /// <summary>Gets the AMPLE home folder.</summary>
    public string Home { get; }

    /// <summary>Gets the folder of the main project (it gets the demo song when it is new).</summary>
    public string Folder => Path.Combine(Home, "projects", "Welcome");

    /// <summary>Reads the hub state now.</summary>
    public async Task<HubState> StateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InstanceRecord> all = await Registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return HubState.Build(all, HubSettings.Load(Home), Folder);
    }

    /// <summary>Gets the arguments that start the main server.</summary>
    public IReadOnlyList<string> Arguments(HubSettings settings)
    {
        List<string> args = ["serve", "--project", Folder, "--home", Home, "--no-browser", "--no-token-output", "--started-by", "hub"];
        if (settings.MainPort > 0) args.AddRange(["--port", settings.MainPort.ToString(CultureInfo.InvariantCulture)]);
        if (settings.MainLogin) args.Add("--login");
        return args;
    }

    // one transition (start, stop, restart) of the main instance at a time, in this process (the hub itself runs once per user)
    private static readonly TransitionGate Gate = new();

    private const string BusyText = "AMPLE is busy starting or stopping.";

    /// <summary>Gets a value indicating whether a start, stop or restart of the main instance is under way.</summary>
    public static bool IsBusy => Gate.IsBusy;

    /// <summary>Waits until the main server is registered as running; null when it did not within the time.</summary>
    /// <param name="timeout">The total time to wait.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The record of the running server, or null.</returns>
    public Task<InstanceRecord?> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => WaitReadyAsync(0, timeout, cancellationToken);

    /// <summary>
    /// Waits until the main server is registered as running on a port (0: any port); null when it did not within the time.
    /// </summary>
    /// <param name="port">The port it must run on (0: any).</param>
    /// <param name="timeout">The total time to wait.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The record of the running server, or null.</returns>
    public Task<InstanceRecord?> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken) =>
        ReadinessWaiter.WaitAsync<InstanceRecord>(
            async probe =>
            {
                HubState state = await StateAsync(probe).ConfigureAwait(false);
                return state.Main is { } running && (port == 0 || running.Record.Port == port) ? running.Record : null;
            },
            () => DateTime.UtcNow,
            Task.Delay,
            timeout,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    /// <summary>
    /// Starts the main server when it is not running and waits (a bounded time) until it is registered. Returns false when
    /// it already ran (or a transition is under way).
    /// </summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>True when it was started.</returns>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        bool started = false;
        bool ran = await Gate.TryRunAsync(async () => started = await StartCoreAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!ran) return false;
        return started;
    }

    private async Task<bool> StartCoreAsync(CancellationToken cancellationToken)
    {
        if ((await StateAsync(cancellationToken).ConfigureAwait(false)).Main is not null) return false;
        await EnsureProjectAsync(cancellationToken).ConfigureAwait(false);
        HubSettings settings = HubSettings.Load(Home);
        InstanceControl.Launch(new InstanceRelaunch(ampleExe, Arguments(settings)));
        // the transition lasts until the server is registered: a second Start in between would otherwise launch a second server
        await WaitReadyAsync(settings.MainPort, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Creates the main project (with the demo song) the first time, with <c>ample new</c>.</summary>
    public async Task EnsureProjectAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(Folder, ".ample", "project.json"))) return;
        Directory.CreateDirectory(Folder);
        System.Diagnostics.ProcessStartInfo start = new(ampleExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "new", Folder, "--name", "Welcome" }) start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ample.exe did not start.");
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("ample new failed (exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");
    }

    /// <summary>Stops the main server; returns null when it stopped (or did not run), else the reason.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Null or the reason.</returns>
    public async Task<string?> StopAsync(CancellationToken cancellationToken)
    {
        string? result = null;
        bool ran = await Gate.TryRunAsync(async () => result = await StopCoreAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!ran) return BusyText;
        return result;
    }

    private async Task<string?> StopCoreAsync(CancellationToken cancellationToken)
    {
        HubState state = await StateAsync(cancellationToken).ConfigureAwait(false);
        return state.Main is null ? null : await InstanceControl.StopAsync(Registry, state.Main.Record, Http, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RestartCoreAsync(CancellationToken cancellationToken)
    {
        string? problem = await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        if (problem is not null) return problem;
        await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stops, then starts the main server (so a new port or new program files apply).</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Null or the reason.</returns>
    public async Task<string?> RestartAsync(CancellationToken cancellationToken)
    {
        string? result = null;
        bool ran = await Gate.TryRunAsync(async () => result = await RestartCoreAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!ran) return BusyText;
        return result;
    }

    /// <summary>Checks that a port can be bound now (0 always can).</summary>
    /// <param name="port">The port.</param>
    /// <returns>True when free.</returns>
    public static bool IsPortFree(int port)
    {
        if (port == 0) return true;
        try
        {
            System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    /// <summary>
    /// Restarts on a new port, as one transition: the new port is only kept when the server comes up on it; otherwise the old port is
    /// restored (only that field of hub.json) and started again. Null when done, else the reason.
    /// </summary>
    /// <param name="newPort">The new port.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>Null or the reason.</returns>
    public async Task<string?> RestartOnPortAsync(int newPort, CancellationToken cancellationToken)
    {
        string? result = null;
        bool ran = await Gate.TryRunAsync(
            async () => result = await PortSwitch.RunAsync(new PortHost(this), newPort, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (!ran) return BusyText;
        return result;
    }

    private sealed class PortHost(MainInstance owner) : IPortSwitchHost
    {
        public int StoredPort => HubSettings.Load(owner.Home).MainPort;

        public async Task<int?> RunningPortAsync(CancellationToken cancellationToken)
            => (await owner.StateAsync(cancellationToken).ConfigureAwait(false)).Main?.Record.Port;

        public bool IsPortFree(int port) => MainInstance.IsPortFree(port);

        public void StorePort(int port) => HubSettingsStore.Update(owner.Home, current => current with { MainPort = port });

        public Task<string?> RestartAsync(CancellationToken cancellationToken) => owner.RestartCoreAsync(cancellationToken);

        public async Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
            => await owner.WaitReadyAsync(port, timeout, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Stops an instance the hub did not start and starts it again from its own recipe; null when done, else the reason.</summary>
    public async Task<string?> RestartOtherAsync(InstanceRecord instance, CancellationToken cancellationToken)
    {
        if (instance.Relaunch is null) return "This instance did not tell the hub how to start it again.";
        string? problem = await InstanceControl.StopAsync(Registry, instance, Http, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (problem is not null) return problem;
        InstanceControl.Launch(instance.Relaunch);
        return null;
    }

    /// <summary>Shuts an instance the hub did not start down (no start afterwards); null when done, else the reason.</summary>
    public Task<string?> ShutDownOtherAsync(InstanceRecord instance, CancellationToken cancellationToken) =>
        InstanceControl.StopAsync(Registry, instance, Http, TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>Gets the URL that opens an instance in the browser (with its token), or null.</summary>
    public string? LaunchUrl(InstanceRecord instance) => Registry.ReadLaunchUrl(instance.ProjectId);
}
