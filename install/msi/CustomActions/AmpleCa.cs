using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WixToolset.Dtf.WindowsInstaller;

namespace Ample.Msi
{
    /// <summary>
    /// Deferred custom action: runs "ample.exe packs install &lt;pack&gt; --progress" as the person who installs and passes what ample reports
    /// ("progress &lt;percent or -1&gt; &lt;text&gt;") to the wizard: the text goes to the action-data line, the percent moves the progress bar.
    /// CustomActionData: "&lt;path of ample.exe&gt;|&lt;pack&gt;|&lt;AMPLE home or empty&gt;". A failure never fails the install (the action is scheduled with Return="ignore").
    /// </summary>
    public static class AmpleCa
    {
        // The download gets the whole bar to itself: it is reset to 0 at the start and fills to 100 percent.
        private const int Ticks = 1000;

        [CustomAction]
        public static ActionResult RunAmplePack(Session session)
        {
            string[] parts = session.CustomActionData.ToString().Split('|');
            if (parts.Length < 2) return ActionResult.Success;
            string exe = parts[0], pack = parts[1], home = parts.Length > 2 ? parts[2] : "";
            try
            {
                session.Log("AmpleCa: " + exe + " packs install " + pack);
                session.Message(InstallMessage.Progress, new Record(0, Ticks, 0, 0));   // reset: the bar starts again from zero for this download
                Say(session, "Starting " + pack + "...");
                StringBuilder arguments = new StringBuilder("packs install " + pack + " --progress");
                if (home.Length > 0) arguments.Append(" --home \"").Append(home).Append('"');
                ProcessStartInfo start = new ProcessStartInfo(exe, arguments.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                int done = 0;
                using (Process process = Process.Start(start))
                {
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) session.Log("AmpleCa stderr: " + e.Data); };
                    process.BeginErrorReadLine();
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (!line.StartsWith("progress ", StringComparison.Ordinal))
                        {
                            session.Log("AmpleCa: " + line);   // the normal lines of ample go to the log only
                            continue;
                        }

                        string rest = line.Substring(9);
                        int space = rest.IndexOf(' ');
                        int percent;
                        if (space <= 0 || !int.TryParse(rest.Substring(0, space), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent)) continue;
                        MessageResult result = Say(session, rest.Substring(space + 1));
                        if (percent >= 0)
                        {
                            int target = Math.Min(Ticks, percent * Ticks / 100);
                            if (target > done)
                            {
                                session.Message(InstallMessage.Progress, new Record(2, target - done, 0));
                                done = target;
                            }
                        }

                        if (result == MessageResult.Cancel)
                        {
                            try { process.Kill(); } catch (InvalidOperationException) { }
                            session.Log("AmpleCa: cancelled by the person");
                            return ActionResult.UserExit;
                        }
                    }

                    process.WaitForExit();
                    if (done < Ticks) session.Message(InstallMessage.Progress, new Record(2, Ticks - done, 0));
                    session.Log("AmpleCa: ample exited with " + process.ExitCode);
                    if (process.ExitCode != 0) Say(session, "Could not install " + pack + " (the install goes on; run ample doctor later).");
                }
            }
            catch (Exception ex)
            {
                session.Log("AmpleCa: " + ex.Message);   // never fail the install for an optional download
            }

            return ActionResult.Success;
        }

        private static MessageResult Say(Session session, string text)
        {
            // an action-start message replaces the status line of the standard progress screen
            return session.Message(InstallMessage.ActionStart, new Record("AmplePack", text, ""));
        }
    }
}
