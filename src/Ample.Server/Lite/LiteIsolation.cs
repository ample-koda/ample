using System.Diagnostics;
using System.Text.Json;

namespace Ample.Server.Lite;

/// <summary>
/// Runs a Lite build in a process of its own (<c>Ample.Cli lite-worker</c>, see <see cref="LiteWorker"/>) so that a song that makes the build take too long, too much memory or that
/// brings the runtime down can only end its own process: the server keeps serving, the process is killed (with everything it started) when the time is over, when it grows past the
/// memory limit or when the page goes away, and its private folder is deleted afterwards whatever happened. The managed heap of the worker is capped with <c>DOTNET_GCHeapHardLimit</c>.
/// </summary>
internal sealed class LiteIsolation(string workerPath, string ampleRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The program to start as a worker: this very program when it is Ample.Cli, else the one named by <c>AMPLE_LITE_WORKER</c>, else null (no isolation possible).</summary>
    /// <returns>The path, or null.</returns>
    public static string? FindWorker()
    {
        if (Environment.GetEnvironmentVariable("AMPLE_LITE_WORKER") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        string? current = Environment.ProcessPath;
        return current is not null && Path.GetFileNameWithoutExtension(current).Equals("Ample.Cli", StringComparison.OrdinalIgnoreCase) ? current : null;
    }

    /// <summary>Builds in a worker process.</summary>
    /// <param name="libraryHome">The AMPLE library home (the private folders live in it).</param>
    /// <param name="entry">The song.</param>
    /// <param name="files">The files.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="outputs">Where the audio is kept.</param>
    /// <param name="log">Told what the build does (text and how far, 0 to 1).</param>
    /// <param name="cancellationToken">Cancelled when the page goes away.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="LiteRejectedException">The project is refused, the worker needed too much memory or failed.</exception>
    public async Task<LiteBuildResult> BuildAsync(string libraryHome, string entry, IReadOnlyList<LiteFile> files, LiteLimits limits, LiteOutputs outputs, Action<string, double>? log, CancellationToken cancellationToken)
    {
        string work = Path.Combine(LiteWorkspace.WorkRoot(libraryHome), "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        ProcessStartInfo info = new(workerPath)
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "lite-worker", "--root", ampleRoot, "--work", work }) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_GCHeapHardLimit"] = "0x" + (limits.MaxBuildMemoryMegabytes * 1024L * 1024).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using Process process = new() { StartInfo = info };
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(limits.BuildSeconds + 25));
        bool memoryExceeded = false;
        LiteBuildResult? result = null;
        LiteRejectedException? refused = null;
        try
        {
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new LiteRejectedException("AMPL931", 500, "The build could not be started inside Remix.");
            }

            Task send = Task.Run(async () =>
            {
                try
                {
                    await LiteWorker.WriteRequestAsync(process.StandardInput.BaseStream, entry, files, limits, limit.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // the worker ended before it read everything: its own answer says why
                }
            }, limit.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(limit.Token);
            Task watch = Task.Run(async () =>
            {
                long cap = limits.MaxBuildMemoryMegabytes * 1024L * 1024;
                while (!process.HasExited && !limit.IsCancellationRequested)
                {
                    try
                    {
                        process.Refresh();
                        if (process.WorkingSet64 > cap) { memoryExceeded = true; Kill(process); return; }
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }

                    await Task.Delay(400, limit.Token).ConfigureAwait(false);
                }
            }, limit.Token);
            while (await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false) is { } line)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                switch (root.GetProperty("t").GetString())
                {
                    case "log":
                        log?.Invoke(root.GetProperty("text").GetString() ?? string.Empty, root.TryGetProperty("fraction", out JsonElement fraction) ? fraction.GetDouble() : -1);
                        break;
                    case "result":
                        result = root.GetProperty("result").Deserialize<LiteBuildResult>(Json);
                        break;
                    case "error":
                        refused = new LiteRejectedException(root.GetProperty("code").GetString() ?? "AMPL931", root.GetProperty("status").GetInt32(), root.GetProperty("message").GetString() ?? "The build was refused.");
                        break;
                }
            }

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAny(send, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            _ = watch;
            if (memoryExceeded) throw new LiteRejectedException("AMPL939", 413, $"The build needed more than {limits.MaxBuildMemoryMegabytes} MB of memory and was stopped. Make the song smaller.");
            if (refused is not null) throw refused;
            if (result is null)
            {
                string reference = Guid.NewGuid().ToString("N")[..8];
                await Console.Error.WriteLineAsync($"[AMPLE] lite worker {reference} gave no result (exit {(process.HasExited ? process.ExitCode : -1)}): {(await errors.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false)).Trim()}").ConfigureAwait(false);
                throw new LiteRejectedException("AMPL931", 500, $"The build failed inside Remix (reference {reference}).");
            }

            string? id = null;
            string wavFile = Path.Combine(work, "out.wav");
            if (result.Ok && File.Exists(wavFile)) id = outputs.Put(await File.ReadAllBytesAsync(wavFile, cancellationToken).ConfigureAwait(false), limits);
            return result with { OutputId = id };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new LiteBuildResult(false, [new LiteDiagnostic("AMPL923", "error", 0, 0, $"The build took longer than {limits.BuildSeconds} seconds and was stopped.")], null, limits.BuildSeconds, 0, 0);
        }
        finally
        {
            Kill(process);
            Cleanup(work);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // it ended by itself in the meantime
        }
    }

    private static void Cleanup(string work)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(work, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
