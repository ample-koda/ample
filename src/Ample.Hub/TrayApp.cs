using System.Diagnostics;
using Ample.Hub.Core;

namespace Ample.Hub;

/// <summary>The tray icon and its menu. The state is read in the background every few seconds; the menu shows the last reading.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly MainInstance main;
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private HubState state = new(null, [], new HubSettings());
    private string? pending;
    private int busy;
    private readonly CancellationTokenSource quit = new();
    // the installer asks a running hub to quit by setting this event (it cannot close a window: the hub has none); the timer sees it within a few seconds
    private readonly EventWaitHandle exitSignal = new(false, EventResetMode.AutoReset, @"Local\AMPLE-Hub-Exit");

    internal TrayApp(MainInstance main)
    {
        this.main = main;
        icon = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application, Visible = true, Text = "AMPLE", ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => OpenMain();
        menu.Opening += (_, _) => Rebuild();
        timer.Tick += async (_, _) => { if (exitSignal.WaitOne(0)) { ExitThread(); return; } await RefreshAsync(); };
        timer.Start();
        _ = RefreshAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            quit.Cancel();
            quit.Dispose();
            timer.Dispose();
            exitSignal.Dispose();
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            state = await main.StateAsync(CancellationToken.None);
            int others = state.Others.Count;
            string text = state.Main is null ? "AMPLE: stopped" : $"AMPLE: running on port {state.Main.Record.Port}";
            icon.Text = others == 0 ? text : $"{text} (+{others} more)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the registry is busy: the next tick tries again
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private void Rebuild()
    {
        menu.Items.Clear();
        if (state.Main is { } running)
        {
            Add(menu.Items, $"Main instance: running on port {running.Record.Port}", null, false);
            Add(menu.Items, "Open the studio", OpenMain);
            Add(menu.Items, "Copy the URL", () => Clipboard.SetText(main.LaunchUrl(running.Record) ?? string.Empty));
            Add(menu.Items, "Restart", () => Do("Restarting AMPLE", main.RestartAsync), pending is null);
            Add(menu.Items, "Stop", () => Do("Stopping AMPLE", main.StopAsync), pending is null);
        }
        else
        {
            Add(menu.Items, pending is null ? "Main instance: stopped" : "Main instance: " + pending.ToLowerInvariant() + "…", null, false);
            Add(menu.Items, "Start", () => Do("Starting AMPLE", async t => { await main.StartAsync(t); return null; }), pending is null);
        }

        Add(menu.Items, "Port…", ChoosePort);
        menu.Items.Add(new ToolStripSeparator());
        if (state.Others.Count == 0) Add(menu.Items, "No other instances", null, false);
        foreach (HubInstance other in state.Others)
        {
            ToolStripMenuItem sub = new($"{other.Record.Name} (port {other.Record.Port})" + (other.Record.Agent is null ? string.Empty : "  [AI]"));
            Add(sub.DropDownItems, "Open", () => Open(other));
            Add(sub.DropDownItems, "Copy the URL", () => Clipboard.SetText(main.LaunchUrl(other.Record) ?? string.Empty));
            Add(sub.DropDownItems, "Restart", () => Do("Restarting " + other.Record.Name, t => main.RestartOtherAsync(other.Record, t)), other.CanRestart);
            Add(sub.DropDownItems, "Shut down", () => Do("Shutting down " + other.Record.Name, t => main.ShutDownOtherAsync(other.Record, t)));
            Add(sub.DropDownItems, "Open the folder", () => Process.Start(new ProcessStartInfo(other.Record.Folder) { UseShellExecute = true })?.Dispose());
            menu.Items.Add(sub);
        }

        menu.Items.Add(new ToolStripSeparator());
        Add(menu.Items, "Overview report…", () => new ReportForm(main).Show());
        Add(menu.Items, "Settings…", ChooseSettings);
        Add(menu.Items, "Tune-up (sign-in, health check)…", OpenSetup);
        menu.Items.Add(new ToolStripSeparator());
        Add(menu.Items, "Quit the hub (servers keep running)", () => ExitThread());
    }

    private static void Add(ToolStripItemCollection items, string text, Action? click, bool enabled = true)
    {
        ToolStripMenuItem item = new(text) { Enabled = enabled && click is not null };
        if (click is not null) item.Click += (_, _) => click();
        items.Add(item);
    }

    private void Open(HubInstance instance)
    {
        if (main.LaunchUrl(instance.Record) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    private void OpenMain()
    {
        if (state.Main is not null) Open(state.Main);
        else Do("Starting AMPLE", async t => { await main.StartAsync(t); return await Program.OpenStudioAsync(main, quit.Token); });
    }

    private void Do(string what, Func<CancellationToken, Task<string?>> action, Action? then = null)
    {
        if (pending is not null) { icon.ShowBalloonTip(3000, "AMPLE", pending + " is still busy.", ToolTipIcon.Info); return; }
        pending = what;
        icon.Text = what + "…";
        _ = Task.Run(async () =>
        {
            string? problem;
            try { problem = await action(CancellationToken.None); }
            catch (Exception ex) { problem = ex.Message; }
            try { menu.BeginInvoke(() =>
            {
                pending = null;
                if (problem is not null) icon.ShowBalloonTip(5000, "AMPLE", problem, ToolTipIcon.Warning);
                else then?.Invoke();
                _ = RefreshAsync();
            }); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* the tray is closing */ }
        });
    }

    private static void OpenSetup()
    {
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Ample.Setup.exe"));
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe, "--mode reconfigure") { UseShellExecute = false })?.Dispose();
        else MessageBox.Show("The setup program was not found next to the hub.", "AMPLE", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ChoosePort()
    {
        HubSettings current = HubSettings.Load(main.Home);
        using PortDialog dialog = new(current.MainPort);
        if (dialog.ShowDialog() != DialogResult.OK || dialog.Port == current.MainPort) return;
        int port = dialog.Port;
        if (state.Main is not null && MessageBox.Show("Restart AMPLE now, so the new port applies? (If AMPLE cannot start on it, the old port is restored.)", "AMPLE", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            Do("Restarting AMPLE", t => main.RestartOnPortAsync(port, t));
            return;
        }

        (current with { MainPort = port }).Save(main.Home);
    }

    private void ChooseSettings()
    {
        HubSettings current = HubSettings.Load(main.Home);
        using SettingsForm dialog = new(current);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        dialog.Result.Save(main.Home);
        Program.SetStartAtLogin(dialog.Result.StartAtLogin);
        if (dialog.Result.MainLogin != current.MainLogin && state.Main is not null && MessageBox.Show("Restart AMPLE now, so the sign-in setting applies?", "AMPLE", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Do("Restarting AMPLE", main.RestartAsync);
        _ = RefreshAsync();
    }
}
