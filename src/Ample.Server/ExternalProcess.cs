using System.Diagnostics;
using System.Text;

namespace Ample.Server;

/// <summary>What came out of running an external tool: whether it exited, its exit code (when it did) and its captured stderr.</summary>
internal readonly record struct ExternalProcessResult(bool Exited, int ExitCode, string Stderr);

/// <summary>
/// Runs an external tool (Audiveris, MuseScore, ffmpeg-like helpers) the deadlock-free way.
/// </summary>
internal static class ExternalProcess
{
    /// <summary>Runs <paramref name="info"/>, which must redirect both standard streams; both are drained asynchronously (BeginOutputReadLine/BeginErrorReadLine) instead of read to completion one after the other, which can deadlock once a chatty tool's stdout fills the OS pipe buffer while something is still blocked reading stderr.</summary>
    internal static ExternalProcessResult Run(ProcessStartInfo info, int timeoutMs)
    {
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start");
        StringBuilder stderr = new();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            return new ExternalProcessResult(false, -1, string.Empty);
        }

        process.WaitForExit(); // lets the async output/error readers finish delivering what they already have queued
        string errorText;
        lock (stderr) errorText = stderr.ToString();
        return new ExternalProcessResult(true, process.ExitCode, errorText);
    }
}
