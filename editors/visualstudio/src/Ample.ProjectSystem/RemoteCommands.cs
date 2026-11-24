// menu handlers run on the UI thread
#pragma warning disable VSTHRD010, VSSDK007
using System.ComponentModel.Design;
using Ample.VisualStudio.Remote;
using Microsoft.VisualStudio.Shell;

namespace Ample.VisualStudio;

/// <summary>
/// The Tools commands for a project on a server's AMPLE (plan VS07): "Connect to a server" makes a local cache folder, fills it from the server and opens it as
/// a project; "Pull from server" and "Push to server" keep it in step. Everything runs on the background and reports in the status bar; a failure is a
/// message there (with the log), never a modal dialog.
/// </summary>
internal static class RemoteCommands
{
    internal static void Register(OleMenuCommandService commands)
    {
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(Connect), new CommandID(AmpleCommands.CommandSet, 0x0108)));
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(() => Sync(push: false)), new CommandID(AmpleCommands.CommandSet, 0x0109)));
        commands.AddCommand(new OleMenuCommand((_, _) => Guarded(() => Sync(push: true)), new CommandID(AmpleCommands.CommandSet, 0x010A)));
    }

    private static void Guarded(System.Action action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            AmpleLog.Write("remote command failed: " + ex);
            Say("AMPLE: " + ex.Message);
        }
    }

    private static void Say(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte) dte.StatusBar.Text = text;
    }

    private static void Connect()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        // the automated tests give the answers through AMPLE_REMOTE_TEST (url|mode|ssh|user|password|folder) instead of the dialog
        string? test = System.Environment.GetEnvironmentVariable("AMPLE_REMOTE_TEST");
        RemoteSettings settings = new();
        string? password;
        string folder;
        if (!string.IsNullOrEmpty(test))
        {
            string[] p = test!.Split('|');
            settings.Url = p[0];
            settings.Mode = p[1] == "tunnel" ? RemoteMode.Tunnel : RemoteMode.Direct;
            settings.SshHost = p[2];
            settings.User = p[3];
            password = p[4];
            folder = p[5];
        }
        else
        {
            using RemoteConnectDialog dialog = new();
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            settings = dialog.Settings;
            password = dialog.Password;
            folder = dialog.Folder;
        }

        CredentialStore.Write(settings.CredentialTarget, settings.User, password ?? string.Empty);
        System.IO.Directory.CreateDirectory(folder);
        settings.Save(folder);
        Say("AMPLE: connecting to " + settings.Url + " ...");
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string message;
            string? projectFile = null;
            try
            {
                using RemoteClient client = new(settings.ToOptions());
                client.Status += RemoteStatus.Show;
                await client.ConnectAsync();
                SyncResult pulled = await new RemoteSync(client, folder).PullAsync();
                projectFile = AmpleCli.ProjectFileIn(folder);
                if (projectFile is null || !System.IO.File.Exists(System.IO.Path.Combine(folder, ".ample", "project.json")))
                {
                    await System.Threading.Tasks.Task.Run(() => AmpleCommandsInit(folder));
                    projectFile = AmpleCli.ProjectFileIn(folder);
                }

                message = "AMPLE: connected, " + pulled.Fetched.Count + " files fetched" + (pulled.Skipped.Count > 0 ? ", " + pulled.Skipped.Count + " too big to fetch" : string.Empty) + ".";
            }
            catch (System.Exception ex)
            {
                AmpleLog.Write("connect failed: " + ex);
                message = "AMPLE: " + ex.Message;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte.StatusBar.Text = message;
            if (projectFile is not null) dte.Solution.AddFromFile(projectFile, false);
        }).FileAndForget("ample/remote/connect");
    }

    private static void AmpleCommandsInit(string folder)
    {
        // the cache is not an AMPLE project (the server holds that), so Visual Studio only gets the project file for it
        System.Diagnostics.ProcessStartInfo start = new(AmpleCli.For(null), "vs init " + AmpleProjectWizard.Quote(folder) + " --create-project")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new System.InvalidOperationException("The AMPLE command line did not start.");
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        if (process.ExitCode != 0) throw new System.InvalidOperationException("ample vs init failed: " + error.Trim());
    }

    private static string? CacheFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return null;
        foreach (EnvDTE.Project project in dte.Solution.Projects)
        {
            string? folder = System.IO.Path.GetDirectoryName(project.FullName);
            if (folder is not null && RemoteSettings.Exists(folder)) return folder;
        }

        return null;
    }

    private static void Sync(bool push)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) return;
        string? folder = CacheFolder();
        if (folder is null)
        {
            Say("AMPLE: the open project is not a project on a server (use \"Connect to a server\").");
            return;
        }

        foreach (EnvDTE.Document document in dte.Documents)
        {
            if (!document.Saved && document.FullName.StartsWith(folder, System.StringComparison.OrdinalIgnoreCase)) document.Save();
        }

        RemoteSettings settings = RemoteSettings.Load(folder)!;
        Say(push ? "AMPLE: sending changes to the server ..." : "AMPLE: fetching changes from the server ...");
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string message;
            try
            {
                using RemoteClient client = new(settings.ToOptions());
                client.Status += RemoteStatus.Show;
                RemoteSync sync = new(client, folder);
                SyncResult result = push ? await sync.PushAsync() : await sync.PullAsync();
                message = push
                    ? "AMPLE: " + result.Pushed.Count + " files sent" + (result.Conflicts.Count > 0 ? ", " + result.Conflicts.Count + " changed on the server too (pull first): " + string.Join(", ", result.Conflicts) : string.Empty) + "."
                    : "AMPLE: " + result.Fetched.Count + " files fetched" + (result.Conflicts.Count > 0 ? ", " + result.Conflicts.Count + " conflicts: your version is in _conflicts\\" + result.ConflictId + "\\" : string.Empty) + ".";
            }
            catch (System.Exception ex)
            {
                AmpleLog.Write("sync failed: " + ex);
                message = "AMPLE: " + ex.Message;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            dte.StatusBar.Text = message;
        }).FileAndForget("ample/remote/sync");
    }
}
