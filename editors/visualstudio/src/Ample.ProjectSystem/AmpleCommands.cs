// menu handlers and BeforeQueryStatus run on the UI thread
#pragma warning disable VSTHRD010, VSSDK007
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;

namespace Ample.VisualStudio;

/// <summary>"Build current AMPLET" and "Build and play current AMPLET": the song open in the editor, not the project's startup song.
/// Build renders to obj\Ample\&lt;song&gt;.wav; Build and play also plays it. Both are in the Tools menu and the editor's context menu.</summary>
internal static class AmpleCommands
{
    internal static readonly System.Guid CommandSet = new("5C0C3F0E-7B1B-4B7C-9D5B-3A1E4C6E2A10");
    private static AmpleRunSession? _session;

    internal static void Register(OleMenuCommandService commands)
    {
        foreach ((int id, bool play, bool context) in new[] { (0x0100, false, false), (0x0101, true, false), (0x0102, false, true), (0x0103, true, true) })
        {
            OleMenuCommand command = new((_, _) => Guarded(() => Run(play)), new CommandID(CommandSet, id));
            command.BeforeQueryStatus += (sender, _) =>
            {
                OleMenuCommand self = (OleMenuCommand)sender;
                bool song = CurrentSong() is not null;
                self.Enabled = song;
                self.Visible = !context || song;
            };
            commands.AddCommand(command);
        }

        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(OpenAsProject), new CommandID(CommandSet, 0x0104)));
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(ToggleDebugMode), new CommandID(CommandSet, 0x0105)));
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(ConnectFolder), new CommandID(CommandSet, 0x0106)));
        RemoteCommands.Register(commands);
        WindowShot.Register(commands);
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(TogglePalette), new CommandID(CommandSet, 0x0107)));
    }

    /// <summary>Writes the Visual Studio project file into an existing AMPLE folder (`ample vs init`) and opens it: Visual Studio becomes
    /// another view of the same project. The folder is the one of the open song when it is in an AMPLE project, else the person picks one.</summary>
    private static void OpenAsProject()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        string? song = CurrentSong();
        string? root = song is null ? null : ProjectRootOf(song);
        if (root is null)
        {
            using System.Windows.Forms.FolderBrowserDialog dialog = new() { Description = "Choose the AMPLE project folder (it has a .ample folder)" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            root = dialog.SelectedPath;
        }

        if (!System.IO.Directory.Exists(System.IO.Path.Combine(root, ".ample")))
        {
            dte.StatusBar.Text = "AMPLE: " + root + " is not an AMPLE project (no .ample folder).";
            return;
        }

        string folder = root;
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string message;
            try
            {
                string? existing = AmpleCli.ProjectFileIn(folder);
                if (existing is null)
                {
                    await System.Threading.Tasks.Task.Run(() => RunInit(folder));
                    existing = AmpleCli.ProjectFileIn(folder);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (existing is null) throw new System.InvalidOperationException("No .ampleproj was written in " + folder);
                dte.Solution.AddFromFile(existing, false);
                message = "AMPLE: opened " + existing;
            }
            catch (System.Exception ex)
            {
                AmpleLog.Write("open as project failed: " + ex.Message);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                message = "AMPLE: " + ex.Message;
            }

            dte.StatusBar.Text = message;
        }).FileAndForget("ample/openAsProject");
    }

    /// <summary>Switches what F5 does for the startup project: play the song (default) or debug its build (breakpoints, stepping, locals).
    /// The choice is kept in the machine-local .user file next to the project (AmpleDebugMode), not in the shared project file.</summary>
    private static void ToggleDebugMode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        string? projectFile = null;
        foreach (EnvDTE.Project project in dte.Solution.Projects)
        {
            if (project.FullName.EndsWith(".ampleproj", System.StringComparison.OrdinalIgnoreCase)) { projectFile = project.FullName; break; }
        }

        if (projectFile is null)
        {
            dte.StatusBar.Text = "AMPLE: no AMPLE project is open.";
            return;
        }

        string userFile = projectFile + ".user";
        System.Xml.Linq.XElement root = System.IO.File.Exists(userFile) ? System.Xml.Linq.XElement.Load(userFile) : new System.Xml.Linq.XElement("Project");
        System.Xml.Linq.XElement? setting = root.Descendants().LastOrDefault(e => e.Name.LocalName == "AmpleDebugMode");
        bool toBuild = !string.Equals(setting?.Value, "build", System.StringComparison.OrdinalIgnoreCase);
        if (setting is null)
        {
            root.Add(new System.Xml.Linq.XElement("PropertyGroup", new System.Xml.Linq.XElement("AmpleDebugMode", toBuild ? "build" : "play")));
        }
        else
        {
            setting.Value = toBuild ? "build" : "play";
        }

        root.Save(userFile);
        dte.StatusBar.Text = toBuild ? "AMPLE: F5 now debugs the build (breakpoints in helper code)." : "AMPLE: F5 now plays the song.";
    }

    /// <summary>Connects the folder of the open project to an AMPLE project that already has files (`ample vs connect`): identical files are fine,
    /// differing local files are moved to _conflicts/&lt;id&gt;/ and listed here, nothing is deleted. The AMPLE project folder is asked for (or taken
    /// from AMPLE_CONNECT_PROJECT, which the automated tests use instead of the dialog).</summary>
    private static void ConnectFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        string? projectFile = null;
        foreach (EnvDTE.Project project in dte.Solution.Projects)
        {
            if (project.FullName.EndsWith(".ampleproj", System.StringComparison.OrdinalIgnoreCase)) { projectFile = project.FullName; break; }
        }

        if (projectFile is null)
        {
            dte.StatusBar.Text = "AMPLE: open the project of the folder you want to connect first.";
            return;
        }

        string? ample = System.Environment.GetEnvironmentVariable("AMPLE_CONNECT_PROJECT");
        if (string.IsNullOrEmpty(ample))
        {
            using System.Windows.Forms.FolderBrowserDialog dialog = new() { Description = "Choose the AMPLE project folder to connect to (it has a .ample folder)" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            ample = dialog.SelectedPath;
        }

        string local = System.IO.Path.GetDirectoryName(projectFile)!;
        string chosen = ample!;
        dte.StatusBar.Text = "AMPLE: connecting " + local + " ...";
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string report = await System.Threading.Tasks.Task.Run(() => RunCli("vs connect \"" + local + "\" --project \"" + chosen + "\"", projectFile));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte.StatusBar.Text = "AMPLE: " + report.Trim().Split('\n').Last().Trim();
            if (report.Contains("_conflicts") || report.Contains("unsaved"))
            {
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, report, "AMPLE: connect", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
                    Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }).FileAndForget("ample/connect");
    }

    private static string RunCli(string arguments, string projectFile)
    {
        System.Diagnostics.ProcessStartInfo start = new(AmpleCli.For(projectFile), arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new System.InvalidOperationException("The AMPLE CLI did not start.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return output;
    }

    /// <summary>Switches the colours of .amplet files between the AMPLE palette (default) and Visual Studio's own colours.</summary>
    private static void TogglePalette()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        bool toAmple = !AmplePalette.Enabled;
        AmplePalette.SetEnabled(toAmple);
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
        {
            dte.StatusBar.Text = toAmple ? "AMPLE: colours now follow the AMPLE palette." : "AMPLE: colours now follow Visual Studio.";
        }
    }

    private static void RunInit(string folder)
    {
        System.Diagnostics.ProcessStartInfo start = new(AmpleCli.For(null), "vs init " + AmpleProjectWizard.Quote(folder))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new System.InvalidOperationException("The AMPLE CLI did not start.");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new System.InvalidOperationException("ample vs init took longer than 30 seconds."); }
        if (process.ExitCode != 0) throw new System.InvalidOperationException("ample vs init failed: " + error.Trim());
    }

    /// <summary>Runs a command and reports a failure in the status bar and the extension log instead of a modal "extension exception" dialog.</summary>
    private static void Guarded(System.Action action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            AmpleLog.Write("command failed: " + ex);
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte) dte.StatusBar.Text = "AMPLE: " + ex.Message;
        }
    }

    private static string? CurrentSong()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EnvDTE.DTE? dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        string? path = dte?.ActiveDocument?.FullName;
        return path is not null && path.EndsWith(".amplet", System.StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string? ProjectRootOf(string file)
    {
        for (string? folder = System.IO.Path.GetDirectoryName(file); folder is not null; folder = System.IO.Path.GetDirectoryName(folder))
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(folder, ".ample"))) return folder;
        }

        return null;
    }

    private static void Run(bool play)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        string? file = CurrentSong();
        if (file is null) return;
        if (dte.ActiveDocument is { Saved: false } document) document.Save();
        string? root = ProjectRootOf(file);
        if (root is null)
        {
            dte.StatusBar.Text = "AMPLE: this song is not inside an AMPLE project (no .ample folder above it).";
            return;
        }

        string song = System.IO.Path.GetFileName(file);
        string relative = file.Substring(root.Length).TrimStart('\\').Replace('\\', '/');
        dte.StatusBar.Text = (play ? "AMPLE: building and playing " : "AMPLE: building ") + relative + "...";
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string message;
            try
            {
                message = await System.Threading.Tasks.Task.Run(() => Execute(root, relative, song, play));
            }
            catch (System.Exception ex)
            {
                AmpleLog.Write("command failed: " + ex.Message);
                message = "AMPLE: " + ex.Message;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte.StatusBar.Text = message;
        }).FileAndForget("ample/command");
    }

    private static string Execute(string root, string relative, string song, bool play)
    {
        AmpleDebugLaunchProvider.StopPlayback();
        _session?.Dispose();
        _session = null;
        if (Ample.VisualStudio.Remote.RemoteSettings.Exists(root))
        {
            string remoteWav = System.IO.Path.Combine(root, "obj", "Ample", System.IO.Path.GetFileNameWithoutExtension(song) + ".wav");
#pragma warning disable VSTHRD002   // this runs on a background thread (Task.Run), nothing waits for the UI thread
            RemoteRun.BuildToFileAsync(root, relative, remoteWav).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            if (!play) return "AMPLE: built " + song + " on the server -> " + remoteWav;
            using System.Media.SoundPlayer remote = new(remoteWav);
            remote.Play();
            return "AMPLE: playing " + song + " (built on the server)";
        }

        AmpleRunSession session = AmpleRunSession.Open(root, false);
        if (play)
        {
            _session = session;   // stays open while it plays; the next command ends it
            session.BuildAndPlay(relative);
            return "AMPLE: playing " + song;
        }

        try
        {
            string wav = System.IO.Path.Combine(root, "obj", "Ample", System.IO.Path.GetFileNameWithoutExtension(song) + ".wav");
            session.BuildToFile(relative, wav);
            return "AMPLE: built " + song + " -> " + wav;
        }
        finally
        {
            session.Dispose();
        }
    }
}
