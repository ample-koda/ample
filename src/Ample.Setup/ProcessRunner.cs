using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Ample.Setup
{
    /// <summary>Runs a child process with both output streams drained at the same time and a real deadline.</summary>
    internal static class ProcessRunner
    {
        /// <summary>Returns null when the child finished in time, else the reason (a timeout kills the child). The exit code is in <paramref name="code"/>.</summary>
        internal static string Capture(ProcessStartInfo start, int timeoutMs, out string stdout, out string stderr, out int code)
        {
            stdout = string.Empty; stderr = string.Empty; code = -1;
            using (Process process = Process.Start(start))
            {
                Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch (Exception) { /* it may have just ended */ }
                    return "it did not finish within " + (timeoutMs / 1000) + " seconds and was stopped";
                }

                process.WaitForExit();
                Task.WaitAll(new Task[] { outTask, errTask }, 5000);
                stdout = outTask.IsCompleted ? outTask.Result : string.Empty;
                stderr = errTask.IsCompleted ? errTask.Result : string.Empty;
                code = process.ExitCode;
                return null;
            }
        }
    }
}
