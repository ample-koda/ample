using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Ample.Setup
{
    /// <summary>A fix the wizard can run: a pack that ample fetches itself, or a package installed with winget.</summary>
    internal sealed class Fix
    {
        internal string Kind;      // "pack" (download), "pack-remove" (take an installed sound pack away) or "winget"
        internal string Id;        // the pack name or the winget package id
        internal string Title;
        internal string Scope;     // winget: "user" or "machine"
        internal int SizeMb;
        internal bool Admin;       // needs administrator permission (a UAC prompt)

        /// <summary>The form that is passed to the elevated copy of the wizard.</summary>
        internal string Key { get { return Kind + "|" + Id + "|" + (Scope ?? ""); } }

        internal static Fix FromKey(string key)
        {
            string[] p = key.Split('|');
            return new Fix { Kind = p[0], Id = p.Length > 1 ? p[1] : "", Scope = p.Length > 2 && p[2].Length > 0 ? p[2] : null, Admin = p.Length > 2 && p[2] == "machine" };
        }
    }

    /// <summary>What the doctor found.</summary>
    internal sealed class Finding
    {
        internal string Level;     // ok, note, missing
        internal string Name;
        internal string Detail;
        internal string Hint;
        internal Fix Fix;
    }

    /// <summary>Runs the fixes. Progress is reported as (percent or -1 when unknown, text).</summary>
    internal static class FixRunner
    {
        internal const int ErrorCancelledByUser = 1223;
        private const int WingetNothingToDo = -1978335189;   // 0x8A15002B: already installed and up to date

        /// <summary>Runs one fix in this process. Returns null when it worked, else the reason.</summary>
        internal static string Run(Fix fix, string ample, string home, Action<int, string> report, CancellationToken cancel = default(CancellationToken))
        {
            try
            {
                if (fix.Kind == "pack") return RunPack(fix, ample, home, report, cancel);
                if (fix.Kind == "pack-remove") return RunPackRemove(fix, ample, home);
                if (fix.Kind == "winget") return RunWinget(fix, report);
                return "Unknown kind of fix: " + fix.Kind;
            }
            catch (Win32Exception ex) when (fix.Kind == "winget") { return "winget was not found (it comes with the App Installer of Windows 10 and 11): " + ex.Message; }
            catch (Exception ex) { return ex.Message; }
        }

        private static string RunPack(Fix fix, string ample, string home, Action<int, string> report, CancellationToken cancel)
        {
            ProcessStartInfo start = new ProcessStartInfo(ample, "packs install " + fix.Id + " --progress --home \"" + home + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            };
            string last = null;
            using (Process process = Process.Start(start))
            using (cancel.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (Exception) { /* already gone */ } }))
            {
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) last = e.Data; };
                process.BeginErrorReadLine();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!line.StartsWith("progress ", StringComparison.Ordinal)) { if (line.Trim().Length > 0) last = line.Trim(); continue; }
                    string rest = line.Substring(9);
                    int space = rest.IndexOf(' ');
                    int percent;
                    if (space > 0 && int.TryParse(rest.Substring(0, space), out percent)) report(percent, rest.Substring(space + 1));
                }

                process.WaitForExit();
                if (cancel.IsCancellationRequested && process.ExitCode != 0) return "Cancelled by you.";   // a pack that finished before the Stop arrived is installed
                return process.ExitCode == 0 ? null : (last ?? "ample stopped with exit code " + process.ExitCode + ".");
            }
        }

        // removes an installed sound pack: ample deletes only that pack's folder in the audio-packs folder of this user
        private static string RunPackRemove(Fix fix, string ample, string home)
        {
            ProcessStartInfo start = new ProcessStartInfo(ample, "packs remove " + fix.Id + " --home \"" + home + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using (Process process = Process.Start(start))
            {
                string text = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 ? null : ((error + " " + text).Trim().Length > 0 ? (error + " " + text).Trim() : "ample stopped with exit code " + process.ExitCode + ".");
            }
        }

        private static string RunWinget(Fix fix, Action<int, string> report)
        {
            string scope = string.IsNullOrEmpty(fix.Scope) ? "user" : fix.Scope;
            ProcessStartInfo start = new ProcessStartInfo("winget", "install --id " + fix.Id + " --exact --scope " + scope + " --silent --disable-interactivity --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            };
            string last = null;
            using (Process process = Process.Start(start))
            {
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) last = e.Data.Trim(); };
                process.BeginErrorReadLine();
                StringBuilder current = new StringBuilder();
                int c;
                while ((c = process.StandardOutput.Read()) >= 0)
                {
                    if (c == '\r' || c == '\n')
                    {
                        string text = current.ToString().Trim();
                        current.Clear();
                        // winget draws its own bars and spinners: keep only the lines that say something
                        if (text.Length > 3 && !text.StartsWith("-", StringComparison.Ordinal) && !text.StartsWith("\\", StringComparison.Ordinal) && !text.StartsWith("|", StringComparison.Ordinal) && !text.StartsWith("/", StringComparison.Ordinal)) { last = text; report(-1, text); }
                    }
                    else current.Append((char)c);
                }

                process.WaitForExit();
                if (process.ExitCode == 0 || process.ExitCode == WingetNothingToDo) return null;
                return (last ?? "winget stopped") + " (winget exit code " + process.ExitCode + ")";
            }
        }

        /// <summary>
        /// Runs one fix in an elevated copy of this program (the Windows UAC prompt appears here) and follows its progress file.
        /// Returns null when it worked, else the reason (also when the person did not give permission).
        /// </summary>
        internal static string RunElevated(Fix fix, string ample, string home, Action<int, string> report)
        {
            string file = Path.Combine(Path.GetTempPath(), "ample-setup-" + Guid.NewGuid().ToString("N") + ".progress");
            File.WriteAllText(file, string.Empty);
            ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--run-fix \"" + fix.Key + "\" --progress-file \"" + file + "\" --ample \"" + ample + "\" --home \"" + home + "\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process process;
            try { process = Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser) { return "Administrator permission was not given, so this step was skipped."; }
            using (process)
            {
                long position = 0;
                string error = null;
                while (true)
                {
                    bool done = process.WaitForExit(400);
                    using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Seek(position, SeekOrigin.Begin);
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.StartsWith("error ", StringComparison.Ordinal)) error = line.Substring(6);
                                else if (line.StartsWith("progress ", StringComparison.Ordinal))
                                {
                                    string rest = line.Substring(9);
                                    int space = rest.IndexOf(' ');
                                    int percent;
                                    if (space > 0 && int.TryParse(rest.Substring(0, space), out percent)) report(percent, rest.Substring(space + 1));
                                }
                            }

                            position = stream.Position;
                        }
                    }

                    if (done) break;
                }

                try { File.Delete(file); } catch (IOException) { /* a leftover temporary file is harmless */ }
                return process.ExitCode == 0 ? null : (error ?? "The step did not finish (exit code " + process.ExitCode + ").");
            }
        }

        /// <summary>The elevated copy: runs one fix and writes its progress to the file the parent follows.</summary>
        internal static int RunChild(string key, string progressFile, string ample, string home)
        {
            Fix fix = Fix.FromKey(key);
            Action<int, string> write = (percent, text) =>
            {
                try { File.AppendAllText(progressFile, "progress " + percent + " " + text.Replace('\r', ' ').Replace('\n', ' ') + "\n", Encoding.UTF8); }
                catch (IOException) { /* the parent reads again next time */ }
            };
            string problem = Run(fix, ample, home, write);
            if (problem == null) return 0;
            try { File.AppendAllText(progressFile, "error " + problem.Replace('\r', ' ').Replace('\n', ' ') + "\n", Encoding.UTF8); } catch (IOException) { /* nothing more to do */ }
            Thread.Sleep(100);
            return 1;
        }
    }
}
