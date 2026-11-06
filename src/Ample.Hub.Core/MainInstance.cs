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

    /// <summary>Starts the main server when it is not running. Returns false when it already ran.</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if ((await StateAsync(cancellationToken).ConfigureAwait(false)).Main is not null) return false;
        await EnsureProjectAsync(cancellationToken).ConfigureAwait(false);
        InstanceControl.Launch(new InstanceRelaunch(ampleExe, Arguments(HubSettings.Load(Home))));
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
    public async Task<string?> StopAsync(CancellationToken cancellationToken)
    {
        HubState state = await StateAsync(cancellationToken).ConfigureAwait(false);
        return state.Main is null ? null : await InstanceControl.StopAsync(Registry, state.Main.Record, Http, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops, then starts the main server (so a new port or new program files apply).</summary>
    public async Task<string?> RestartAsync(CancellationToken cancellationToken)
    {
        string? problem = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (problem is not null) return problem;
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return null;
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
