using System.Diagnostics;
using Ample.Hub.Core;
using Ample.Storage.Projects;
using Microsoft.Win32;

namespace Ample.Hub;

/// <summary>
/// The AMPLE tray hub. Arguments (all optional): <c>--start-main</c>, <c>--restart-main</c>, <c>--stop-main</c>, <c>--open-studio</c>, <c>--main-login on|off</c>,
/// <c>--main-port N</c>, <c>--no-tray</c> (do the actions and leave). Whoever starts first owns the tray; a later start only does its actions.
/// </summary>
internal static class Program
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunName = "AMPLE Hub";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string home = Ample.Storage.Library.Library.DefaultHome();
        string ample = FindAmple();
        MainInstance main = new(home, ample);

        HubSettings settings = HubSettings.Load(home);
        // no hub.json yet: the installer's choice (the Run entry is there or not) is the preference, not the record default
        bool firstSettings = !File.Exists(HubSettings.PathIn(home));
        if (firstSettings) settings = settings with { StartAtLogin = RunEntryPolicy.InitialPreference(new RunEntryStore(), settings.StartAtLogin) };
        string? login = ValueOf(args, "--main-login");
        string? port = ValueOf(args, "--main-port");
        if (login is not null) settings = settings with { MainLogin = login == "on" };
        if (int.TryParse(port, out int p) && p is >= 0 and <= 65535) settings = settings with { MainPort = p };
        if (login is not null || port is not null || firstSettings)
        {
            try { settings.Save(home); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("The settings were not saved: " + ex.Message, "AMPLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        try
        {
            if (args.Contains("--stop-main")) Run(main.StopAsync);
            else if (args.Contains("--restart-main")) Run(main.RestartAsync);
            else if (args.Contains("--start-main")) Run(async t => { await main.StartAsync(t); return null; });
            if (args.Contains("--open-studio")) OpenStudio(main);
        }
        catch (Exception ex)
        {
            MessageBox.Show("AMPLE could not do that: " + ex.Message, "AMPLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (args.Contains("--no-tray")) return 0;
        using Mutex mutex = new(true, @"Local\AMPLE-Hub", out bool first);
        if (!first) return 0;
        SetStartAtLogin(settings.StartAtLogin);
        Application.Run(new TrayApp(main));
        return 0;
    }

    /// <summary>Makes the Run entry match the preference (no write when it already does); null when done, else why not.</summary>
    internal static string? SetStartAtLogin(bool on) => RunEntryPolicy.Apply(new RunEntryStore(), on, Environment.ProcessPath);

    private sealed class RunEntryStore : IRunEntryStore
    {
        public string? Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(RunName) as string;
        }

        public void Write(string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            key.SetValue(RunName, value);
        }

        public void Delete()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(RunName, false);
        }
    }

    internal static void OpenStudio(MainInstance main)
    {
        string? problem = Task.Run(() => OpenStudioAsync(main, CancellationToken.None)).GetAwaiter().GetResult();
        if (problem is not null) MessageBox.Show(problem, "AMPLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Waits (without blocking a UI thread, cancellable) until the main server is ready, then opens the browser; null when opened, else the problem.</summary>
    internal static async Task<string?> OpenStudioAsync(MainInstance main, CancellationToken cancellationToken)
    {
        InstanceRecord? record = await main.WaitReadyAsync(TimeSpan.FromSeconds(40), cancellationToken);
        if (record is null || main.LaunchUrl(record) is not { } url) return "The AMPLE server did not start in time. Use the tray menu to start it again.";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return null;
    }

    private static void Run(Func<CancellationToken, Task<string?>> action)
    {
        string? problem = Task.Run(() => action(CancellationToken.None)).GetAwaiter().GetResult();
        if (problem is not null) MessageBox.Show(problem, "AMPLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string? ValueOf(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static string FindAmple()
    {
        string here = AppContext.BaseDirectory;
        foreach (string candidate in new[] { Path.Combine(here, "..", "ample.exe"), Path.Combine(here, "ample.exe") })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return "ample.exe";
    }
}
