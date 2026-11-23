using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.VS.Debug;

namespace Ample.VisualStudio;

/// <summary>Launches AMPLE songs and maps the Visual Studio debug session to playback.</summary>
[ExportDebugger("Ample")]
[AppliesTo(AmpleUnconfiguredProject.UniqueCapability)]
public sealed class AmpleDebugLaunchProvider : DebugLaunchProviderBase
{
    private static AmpleRunSession? _session;
    private EnvDTE.DebuggerEvents? _debuggerEvents;

    /// <summary>Creates the AMPLE launch provider for one configured project.</summary>
    [ImportingConstructor]
    public AmpleDebugLaunchProvider(ConfiguredProject configuredProject) : base(configuredProject)
    {
        AmpleLog.Write("launch provider created for " + configuredProject.UnconfiguredProject.FullPath);
    }

    /// <summary>AMPLE projects can always be launched; runtime failures are reported by the launch.</summary>
    public override System.Threading.Tasks.Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        AmpleLog.Write("CanLaunchAsync " + launchOptions);
        return System.Threading.Tasks.Task.FromResult(true);
    }

    /// <summary>
    /// F5 and Ctrl+F5: saves this project's open files, builds the startup song through the instance (managed or the selected one), writes the
    /// master WAV to obj\Ample\ and returns the AMPLE player as the process to start. Visual Studio then runs the player like any program:
    /// Stop (Shift+F5) ends it, and when the song ends the player exits and Visual Studio returns to design mode, where an instance this launch
    /// started is shut down.
    /// </summary>
    public override async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
        DebugLaunchOptions launchOptions)
    {
        AmpleLog.Write("QueryDebugTargetsAsync " + launchOptions);
        await ThreadingService.JoinableTaskFactory.SwitchToMainThreadAsync();
        string projectFile = ConfiguredProject.UnconfiguredProject.FullPath;
        string projectRoot = System.IO.Path.GetDirectoryName(projectFile)!;
        string songPath = ProjectSetting(projectFile, "AmpleStartupSong") ?? "demo.amplet";
        string instanceSelection = UserSetting(projectFile, "AmpleInstanceSelection") ?? ProjectSetting(projectFile, "AmpleInstanceSelection") ?? "managed";
        if (string.IsNullOrWhiteSpace(instanceSelection)) instanceSelection = "managed";
        bool attachOnly = instanceSelection != "managed";
        SaveProjectDocuments(projectFile);
        StopPlayback();
        if (string.Equals(UserSetting(projectFile, "AmpleDebugMode") ?? ProjectSetting(projectFile, "AmpleDebugMode"), "build", System.StringComparison.OrdinalIgnoreCase))
        {
            string cli = AmpleCli.For(projectFile);
            if (!await Task.Run(() => AmpleCli.Supports(cli, "dap", "Debug Adapter")))
            {
                string message = $"Debugging the build needs a newer AMPLE: '{cli}' has no 'ample dap'. "
                    + "Set AMPLE_CLI or the AmpleExecutable property of the project to a newer ample.exe, "
                    + "or switch F5 back to play (Tools > AMPLE: Toggle F5 between play and debug build).";
                AmpleLog.Write("debug the build refused: " + message);
                throw new System.InvalidOperationException(message);
            }

            return [BuildDebugSettings(launchOptions, projectFile, projectRoot, songPath)];
        }

        try
        {
            string wavPath = System.IO.Path.Combine(projectRoot, "obj", "Ample", "play.wav");
            if (Ample.VisualStudio.Remote.RemoteSettings.Exists(projectRoot))
            {
                // a project on a server: send what changed here, build there, bring the master back
                await RemoteRun.BuildToFileAsync(projectRoot, songPath, wavPath);
            }
            else
            {
                if (attachOnly) await RequireSelectedInstanceAsync(instanceSelection, projectRoot);
                AmpleRunSession session = await Task.Run(() => AmpleRunSession.Open(projectRoot, attachOnly));
                _session = session;
                await Task.Run(() => session.BuildToFile(songPath, wavPath));
            }

            await ThreadingService.JoinableTaskFactory.SwitchToMainThreadAsync();
            SubscribeDebuggerEvents();
            DebugLaunchSettings player = new(launchOptions)
            {
                LaunchOperation = DebugLaunchOperation.CreateProcess,
                Executable = PlayerPath(),
                Arguments = "\"" + wavPath + "\" --song \"" + songPath + "\"",
                CurrentDirectory = projectRoot,
                LaunchDebugEngineGuid = DebuggerEngines.ManagedOnlyEngine,
            };
            AmpleLog.Write("start player " + player.Executable + " " + player.Arguments);
            return [player];
        }
        catch (System.Exception ex)
        {
            AmpleLog.Write("launch failed: " + ex.Message);
            StopPlayback();
            throw;
        }
    }

    /// <summary>The engine registered by DebugAdapter.pkgdef: the Debug Adapter Host running <c>ample dap</c>.</summary>
    private static readonly System.Guid BuildDebugEngine = new("B5A6156B-7DC9-45B0-8F40-5BBB5CD0CFD8");

    /// <summary>
    /// Debugs the build of the song instead of playing it (AmpleDebugMode = build): breakpoints in helper code, stepping, the call stack and
    /// the locals, through the Debug Adapter Protocol. The launch options are the JSON of a VS Code launch configuration plus the adapter.
    /// </summary>
    private static DebugLaunchSettings BuildDebugSettings(DebugLaunchOptions launchOptions, string projectFile, string projectRoot, string songPath)
    {
        string cli = AmpleCli.For(projectFile);
        string program = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, songPath));
        string options = "{\"type\":\"ample\",\"request\":\"launch\",\"name\":\"AMPLE build\","
            + "\"$adapter\":" + Json(cli) + ",\"$adapterArgs\":\"dap\","
            + "\"program\":" + Json(program) + ",\"project\":" + Json(projectRoot) + "}";
        AmpleLog.Write("debug the build: " + options);
        return new DebugLaunchSettings(launchOptions)
        {
            LaunchOperation = DebugLaunchOperation.CreateProcess,
            Executable = program,
            CurrentDirectory = projectRoot,
            LaunchDebugEngineGuid = BuildDebugEngine,
            Options = options,
        };
    }

    private static string Json(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The player that ships next to this assembly in the extension folder.</summary>
    private static string PlayerPath() =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(AmpleDebugLaunchProvider).Assembly.Location)!, "Ample.Echo.exe");

    private static async Task RequireSelectedInstanceAsync(string instanceSelection, string projectRoot)
    {
        if (!System.Guid.TryParse(instanceSelection, out System.Guid selectedId))
        {
            throw new System.InvalidOperationException(
                $"Unknown AMPLE instance selection '{instanceSelection}'. Choose a listed project instance or managed mode.");
        }
        System.Collections.Generic.IReadOnlyList<AmpleLocalInstance> instances = await AmpleInstanceDiscovery.ListAsync();
        if (!instances.Any(instance => IsSelectedInstance(instance, selectedId, projectRoot)))
        {
            throw new System.InvalidOperationException(
                "The selected AMPLE instance is unavailable for this project. Choose an active matching instance or switch to managed mode.");
        }
    }

    /// <summary>A property of the project file (the last one wins, as in MSBuild), or null.</summary>
    private static string? ProjectSetting(string projectFile, string name) =>
        System.Xml.Linq.XElement.Load(projectFile).Descendants().LastOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>A property of the machine-local .user file, or null when there is none.</summary>
    private static string? UserSetting(string projectFile, string name)
    {
        string userFile = projectFile + ".user";
        if (!System.IO.File.Exists(userFile)) return null;
        return System.Xml.Linq.XElement.Load(userFile).Descendants().LastOrDefault(e => e.Name.LocalName == name)?.Value;
    }

    private static bool IsSelectedInstance(AmpleLocalInstance instance, System.Guid selectedId, string projectRoot)
    {
        bool sameId = instance.ProjectId.Equals(selectedId.ToString("D"), System.StringComparison.OrdinalIgnoreCase);
        string folder = System.IO.Path.GetFullPath(instance.Folder).TrimEnd('\\');
        return sameId && string.Equals(folder, projectRoot.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stops the current playback and releases its audio and HTTP resources.</summary>
    public static void StopPlayback()
    {
        _session?.Dispose();
        _session = null;
    }

    private void DebuggerStopped(EnvDTE.dbgEventReason reason)
    {
        ThreadingService.VerifyOnUIThread();
        AmpleLog.Write("design mode again (" + reason + "): stop the session");
        StopPlayback();
        UnsubscribeDebuggerEvents();
    }

    private void SubscribeDebuggerEvents()
    {
        ThreadingService.VerifyOnUIThread();
        UnsubscribeDebuggerEvents();
        EnvDTE.DTE? dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        _debuggerEvents = dte?.Events.DebuggerEvents;
        if (_debuggerEvents is not null) _debuggerEvents.OnEnterDesignMode += DebuggerStopped;
    }

    private void UnsubscribeDebuggerEvents()
    {
        ThreadingService.VerifyOnUIThread();
        if (_debuggerEvents is not null) _debuggerEvents.OnEnterDesignMode -= DebuggerStopped;
        _debuggerEvents = null;
    }

    private void SaveProjectDocuments(string projectFile)
    {
        ThreadingService.VerifyOnUIThread();
        EnvDTE.DTE? dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte is null) return;
        foreach (EnvDTE.Document document in dte.Documents)
        {
            EnvDTE.Project? project = document.ProjectItem?.ContainingProject;
            if (!document.Saved && project is not null && string.Equals(project.FullName, projectFile, System.StringComparison.OrdinalIgnoreCase)) document.Save();
        }
    }
}
