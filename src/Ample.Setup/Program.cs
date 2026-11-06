using System;
using System.Windows.Forms;

namespace Ample.Setup
{
    /// <summary>
    /// The setup wizard of AMPLE. Arguments: <c>--mode fresh|update|reconfigure</c> (default fresh; reconfigure is what the Start menu entry and the tray hub start)
    /// and <c>--runtime ok|missing</c> (whether the .NET 9 Desktop Runtime that the tray hub needs is installed; default: look for it).
    /// <c>--run-fix</c> is the elevated copy that runs one fix that needs administrator permission (it has no window).
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string fix = ValueOf(args, "--run-fix");
            if (fix != null) return FixRunner.RunChild(fix, ValueOf(args, "--progress-file") ?? string.Empty, ValueOf(args, "--ample") ?? "ample.exe", ValueOf(args, "--home") ?? string.Empty);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string mode = ValueOf(args, "--mode") ?? "fresh";
            if (mode != "update" && mode != "reconfigure") mode = "fresh";
            string runtime = ValueOf(args, "--runtime");
            Application.Run(new Wizard(mode, runtime != "missing" && (runtime != null || RuntimeLooksInstalled())));
            return 0;
        }

        // started from the Start menu there is no --runtime: look for the .NET 9 (or newer) desktop runtime the same way the installer does
        private static bool RuntimeLooksInstalled()
        {
            try
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string shared = System.IO.Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (!System.IO.Directory.Exists(shared)) return false;
                foreach (string dir in System.IO.Directory.GetDirectories(shared))
                {
                    Version v;
                    if (Version.TryParse(System.IO.Path.GetFileName(dir).Split('-')[0], out v) && v.Major >= 9) return true;
                }
            }
            catch (Exception) { /* unknown counts as missing */ }
            return false;
        }

        private static string ValueOf(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
