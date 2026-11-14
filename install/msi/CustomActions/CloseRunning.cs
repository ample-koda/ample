using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using WixToolset.Dtf.WindowsInstaller;

namespace Ample.Msi
{
    /// <summary>
    /// The required step between "Install" and the first file: AMPLE programs that run from the install folder (the tray hub, servers) would hold files, so the install looks first.
    /// With a wizard: it says what runs and asks (close them nicely and go on, or stop the installation); the hub and the servers are asked to stop themselves (a signal for the hub,
    /// POST /api/shutdown for a server) and the install waits for them; when something is still running after the wait it asks once more: close it forcibly, or stop the installation.
    /// Without a wizard (a silent install) it does the same without asking and closes forcibly what did not stop. Nothing outside the install folder is ever touched.
    /// </summary>
    public static class AmpleClose
    {
        private const int WaitSeconds = 25, OkCancel = 1, YesNo = 4, Warning = 0x30;   // the values of the Windows message box (no reference to Windows Forms needed)

        [CustomAction]
        public static ActionResult CloseRunningAmple(Session session)
        {
            try
            {
                string folder = (session["INSTALLFOLDER"] ?? string.Empty).TrimEnd('\\');
                if (folder.Length == 0) return ActionResult.Success;
                bool ui = int.TryParse(session["UILevel"], out int level) && level >= 3;
                List<Proc> running = Find(folder);
                session.Log("AmpleClose: running from " + folder + ": " + running.Count);
                if (running.Count == 0) return ActionResult.Success;

                if (ui && Ask(session, Describe(running) + "\r\n\r\nClose AMPLE now and go on with the installation? AMPLE is asked to stop by itself first. Cancel stops the installation.", OkCancel) != MessageResult.OK)
                    return ActionResult.UserExit;

                AskToStop(session, running);
                running = WaitUntilGone(folder, WaitSeconds);
                if (running.Count == 0) return ActionResult.Success;

                if (ui && Ask(session, Describe(running) + "\r\n\r\nAMPLE did not stop by itself within " + WaitSeconds + " seconds.\r\n\r\nYes closes it forcibly (a song that is open in a browser keeps its unsaved text there, but a running build is lost). No stops the installation.", YesNo) != MessageResult.Yes)
                    return ActionResult.UserExit;

                foreach (Proc p in running)
                {
                    try { using (Process process = Process.GetProcessById(p.Id)) { process.Kill(); session.Log("AmpleClose: forced " + p.Name + " " + p.Id); } }
                    catch (Exception ex) { session.Log("AmpleClose: could not close " + p.Id + ": " + ex.Message); }
                }

                running = WaitUntilGone(folder, 8);
                if (running.Count > 0) session.Log("AmpleClose: still running after the forced close: " + running.Count);
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("AmpleClose: " + ex.Message);   // a bug here must not stop an install: the standard files-in-use handling is still there
                return ActionResult.Success;
            }
        }

        private sealed class Proc { public int Id; public string Name; public string Path; }

        private static MessageResult Ask(Session session, string text, int buttons)
        {
            int kind = (int)InstallMessage.User | buttons | Warning;
            return session.Message((InstallMessage)kind, new Record(text.Replace("[", "(").Replace("]", ")")));
        }

        private static string Describe(List<Proc> running)
        {
            StringBuilder sb = new StringBuilder("AMPLE is still running:");
            foreach (Proc p in running) sb.Append("\r\n  - ").Append(p.Name.StartsWith("Ample.Hub", StringComparison.OrdinalIgnoreCase) ? "the tray hub" : "an AMPLE server or command (process " + p.Id + ")");
            return sb.ToString();
        }

        /// <summary>The AMPLE programs whose file is in the install folder (the hub, ample.exe).</summary>
        private static List<Proc> Find(string folder)
        {
            List<Proc> found = new List<Proc>();
            foreach (string name in new[] { "Ample.Hub", "ample" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = ImagePath(process.Id);
                        if (path != null && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) found.Add(new Proc { Id = process.Id, Name = name, Path = path });
                    }
                    finally { process.Dispose(); }
                }
            }
            return found;
        }

        private static List<Proc> WaitUntilGone(string folder, int seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            List<Proc> running = Find(folder);
            while (running.Count > 0 && DateTime.UtcNow < until) { Thread.Sleep(500); running = Find(folder); }
            return running;
        }

        /// <summary>The hub gets its exit signal; every server of the registry that runs from this installation (same process as the scan) is asked to stop itself (it kills nothing).</summary>
        private static void AskToStop(Session session, List<Proc> running)
        {
            try { using (EventWaitHandle exit = EventWaitHandle.OpenExisting(@"Local\AMPLE-Hub-Exit")) exit.Set(); session.Log("AmpleClose: the hub was signalled"); }
            catch (Exception) { session.Log("AmpleClose: no hub signal (an older hub, or none)"); }
            string home = session["AMPLEHOME"];
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("AMPLE_HOME");
            if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");
            string instances = Path.Combine(home, "instances");
            if (!Directory.Exists(instances)) return;
            foreach (string json in Directory.GetFiles(instances, "*.json"))
            {
                try
                {
                    string launch = Path.ChangeExtension(json, ".launch");
                    int port, pidValue; string token;
                    if (!TryReadInstance(File.ReadAllText(json), File.Exists(launch) ? File.ReadAllText(launch) : string.Empty, out port, out token, out pidValue))
                    {
                        session.Log("AmpleClose: left alone an instance file that does not name its port, token and pid: " + Path.GetFileName(json));   // a server that cannot be identified is never stopped
                        continue;
                    }

                    if (!running.Exists(p => p.Id == pidValue)) { session.Log("AmpleClose: skipped a server outside this installation (pid " + pidValue + ")"); continue; }
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/shutdown");
                    request.Method = "POST"; request.ContentLength = 0; request.Timeout = 4000; request.Headers["X-Ample-Token"] = token;
                    using (request.GetResponse()) { session.Log("AmpleClose: a server on port " + port + " was asked to stop"); }
                }
                catch (Exception ex) { session.Log("AmpleClose: a server did not answer: " + ex.Message); }
            }
        }

        /// <summary>What an instance file (camelCase JSON of the registry: port, pid) and its .launch file (the #token=) say; false when one of the three is missing.</summary>
        internal static bool TryReadInstance(string instanceJson, string launchText, out int port, out string token, out int pid)
        {
            port = 0; pid = 0; token = null;
            Match portMatch = Regex.Match(instanceJson ?? string.Empty, @"""port""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            Match pidMatch = Regex.Match(instanceJson ?? string.Empty, @"""pid""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            Match tokenMatch = Regex.Match(launchText ?? string.Empty, @"#token=([^\s""]+)");
            if (!portMatch.Success || !pidMatch.Success || !tokenMatch.Success) return false;
            if (!int.TryParse(portMatch.Groups[1].Value, out port) || !int.TryParse(pidMatch.Groups[1].Value, out pid)) return false;
            token = tokenMatch.Groups[1].Value;
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        /// <summary>The file a process runs from, whichever bitness (Process.MainModule fails across 32 and 64 bit).</summary>
        private static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try { StringBuilder name = new StringBuilder(1024); int size = name.Capacity; return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null; }
            finally { CloseHandle(handle); }
        }
    }
}
