#pragma warning disable VSSDK007   // fire and forget on purpose: a status line
using Microsoft.VisualStudio.Shell;

namespace Ample.VisualStudio;

/// <summary>Shows what a remote connection is doing (signing in, a lost connection and the next attempt) in the status bar and writes it to the extension log; never a dialog.</summary>
internal static class RemoteStatus
{
    internal static void Show(string message)
    {
        AmpleLog.Write("remote: " + message);
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte) dte.StatusBar.Text = "AMPLE: " + message;
        }).FileAndForget("ample/remote/status");
    }
}
