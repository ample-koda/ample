using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Plugins;

/// <summary>One running plugin process speaking JSON-RPC 2.0, one message per line on stdin and stdout. Crashes, timeouts, garbage and oversized output end the process and fail every pending request with a stable code.</summary>
internal sealed class JsonRpcProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> pending = [];
    private readonly long maxLine;
    private readonly IReadOnlyList<string> secrets;
    private readonly Task reader;
    private readonly Task errorReader;
    private long nextId;
    private AmpleException? fatal;

    private JsonRpcProcess(Process process, long maxLine, IReadOnlyList<string> secrets)
    {
        this.process = process;
        this.maxLine = maxLine;
        this.secrets = secrets;
        reader = Task.Run(ReadStdoutAsync);
        errorReader = Task.Run(ReadStderrAsync);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Fail(new AmpleException("AMP5275", $"The plugin stopped unexpectedly (exit code {SafeExit()})."));
    }

    public List<string> Logs { get; } = [];

    /// <summary>Whether the plugin process is still alive (a kept-warm process may have died while it was idle).</summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Forgets the log lines of an earlier job, so a reused process reports only its own.</summary>
    public void ClearLogs()
    {
        lock (Logs)
        {
            Logs.Clear();
        }
    }

    public Action<double>? OnProgress { get; set; }

    /// <summary>Gets or sets the listener for the whole progress report (fraction, sub-step and its fraction).</summary>
    public Action<Ample.Audio.Engine.ProgressReport>? OnReport { get; set; }

    public static JsonRpcProcess Start(string command, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string> environment, long maxLineBytes, IReadOnlyList<string> secrets)
    {
        ProcessStartInfo info = new(command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Clear();
        foreach ((string k, string v) in environment)
        {
            info.Environment[k] = v;
        }

        try
        {
            return new JsonRpcProcess(Process.Start(info) ?? throw new AmpleException("AMP5277", "The plugin could not be started."), maxLineBytes, secrets);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AmpleException("AMP5277", $"The plugin program '{command}' could not be started: {ex.Message}");
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (fatal is not null)
        {
            throw fatal;
        }

        long id = Interlocked.Increment(ref nextId);
        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending)
        {
            pending[id] = tcs;
        }

        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters })).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            Fail(new AmpleException("AMP5275", $"The plugin stopped unexpectedly (exit code {SafeExit()})."));
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill();
            throw new AmpleException("AMP5276", $"The plugin did not answer '{method}' within {timeout.TotalSeconds:F0} seconds and was stopped.");
        }
        catch (OperationCanceledException)
        {
            Notify("cancel", new { id });
            Kill();
            throw;
        }
    }

    public void Notify(string method, object? parameters)
    {
        try
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
            process.StandardInput.Flush();
        }
        catch (IOException)
        {
            // The process is already gone.
        }
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Notify("shutdown", null);
        if (!process.WaitForExit(2000))
        {
            Kill();
        }

        await Task.WhenAny(Task.WhenAll(reader, errorReader), Task.Delay(1000)).ConfigureAwait(false);
        process.Dispose();
    }

    private async Task ReadStdoutAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length > maxLine)
                {
                    Fail(new AmpleException("AMP5274", $"The plugin sent a message larger than {maxLine / 1024} KiB and was stopped."));
                    Kill();
                    return;
                }

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement m = doc.RootElement.Clone();
                    if (m.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long id))
                    {
                        TaskCompletionSource<JsonElement>? tcs;
                        lock (pending)
                        {
                            pending.Remove(id, out tcs);
                        }

                        if (m.TryGetProperty("error", out JsonElement error))
                        {
                            tcs?.TrySetException(new AmpleException("AMP5278", "The plugin reported an error: " + (error.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : error.GetRawText())));
                        }
                        else
                        {
                            tcs?.TrySetResult(m.TryGetProperty("result", out JsonElement result) ? result : default);
                        }
                    }
                    else if (m.TryGetProperty("method", out JsonElement method))
                    {
                        JsonElement p = m.TryGetProperty("params", out JsonElement pp) ? pp : default;
                        if (method.GetString() == "progress" && p.ValueKind == JsonValueKind.Object)
                        {
                            double? fraction = p.TryGetProperty("fraction", out JsonElement f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : null;
                            string? step = p.TryGetProperty("step", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                            double? stepFraction = p.TryGetProperty("stepFraction", out JsonElement sf) && sf.ValueKind == JsonValueKind.Number ? sf.GetDouble() : null;
                            if (fraction is { } whole)
                            {
                                OnProgress?.Invoke(whole);
                            }

                            OnReport?.Invoke(new Ample.Audio.Engine.ProgressReport(fraction, step, stepFraction));
                        }
                        else if (method.GetString() == "log" && p.ValueKind == JsonValueKind.Object)
                        {
                            AddLog((p.TryGetProperty("level", out JsonElement lv) ? lv.GetString() : "info") + ": " + (p.TryGetProperty("message", out JsonElement lm) ? lm.GetString() : string.Empty));
                        }
                    }
                }
                catch (JsonException)
                {
                    Fail(new AmpleException("AMP5273", "The plugin sent something that is not a JSON-RPC message and was stopped."));
                    Kill();
                    return;
                }
            }
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                AddLog("stderr: " + line);
            }
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }
    }

    private void AddLog(string text)
    {
        string redacted = secrets.Where(s => s.Length > 0).Aggregate(text, (t, s) => t.Replace(s, "[REDACTED]", StringComparison.Ordinal));
        lock (Logs)
        {
            if (Logs.Count < 500)
            {
                Logs.Add(redacted.Length > 2000 ? redacted[..2000] : redacted);
            }
        }
    }

    private void Fail(AmpleException ex)
    {
        fatal ??= ex;
        List<TaskCompletionSource<JsonElement>> all;
        lock (pending)
        {
            all = [.. pending.Values];
            pending.Clear();
        }

        foreach (TaskCompletionSource<JsonElement> t in all)
        {
            t.TrySetException(ex);
        }
    }

    private int SafeExit()
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
