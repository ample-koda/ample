using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;

namespace Ample.Server;

/// <summary>
/// A client of the real <c>codex app-server</c> protocol: JSON-RPC messages, one per line, on the standard input and output of the Codex process. The Codex account and
/// configuration are chosen with <c>CODEX_HOME</c>: another folder is another identity, and its auth and configuration are not mixed with those of the user.
/// </summary>
internal sealed class CodexRpc : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly StringBuilder stderr = new();
    private readonly Task reading;
    private long nextId;

    private CodexRpc(Process process, Action<string>? raw)
    {
        this.process = process;
        Raw = raw;
        reading = Task.Run(ReadLoopAsync);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { if (stderr.Length < 8000) stderr.AppendLine(e.Data); } } };
        process.BeginErrorReadLine();
    }

    /// <summary>Gets or sets what to do with a request that Codex sends to the client (an approval, a question). The returned value is the result.</summary>
    public Func<string, JsonObject, Task<JsonNode?>>? OnServerRequest { get; set; }

    /// <summary>Gets or sets what to do with a notification of Codex.</summary>
    public Action<string, JsonObject>? OnNotification { get; set; }

    /// <summary>Gets or sets a recorder of the raw messages (for the task log), without any token.</summary>
    public Action<string>? Raw { get; set; }

    /// <summary>The first part of what Codex wrote on its error output.</summary>
    public string Stderr { get { lock (stderr) { return stderr.ToString(); } } }

    /// <summary>Whether the process has ended.</summary>
    public bool Exited => process.HasExited;

    /// <summary>Starts Codex as an app server. <paramref name="codexHome"/> null means the Codex home of the user the server runs as.</summary>
    public static CodexRpc Start(string? binary, string? codexHome, string workingDirectory, IEnumerable<string> extraArgs, Action<string>? raw = null)
    {
        ProcessStartInfo info = Command(binary, ["app-server", "--listen", "stdio://", .. extraArgs]);
        info.WorkingDirectory = workingDirectory;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.StandardOutputEncoding = info.StandardErrorEncoding = new UTF8Encoding(false);
        if (codexHome is not null)
        {
            Directory.CreateDirectory(codexHome);
            info.Environment["CODEX_HOME"] = codexHome;
        }

        Process process = Process.Start(info) ?? throw new AmpleException("AMP5453", "Codex could not be started.");
        return new CodexRpc(process, raw);
    }

    /// <summary>
    /// The command line to run Codex. Codex is started directly (never through a shell, so no argument is ever re-interpreted): the executable itself, or <c>node</c> with the
    /// <c>codex.js</c> of the npm install that the <c>codex.cmd</c> shim on the path belongs to.
    /// </summary>
    internal static ProcessStartInfo Command(string? binary, IEnumerable<string> args)
    {
        (string file, string[] prefix) = CodexLocator.Find(binary) ?? throw new AmpleException("AMP5453", "Codex was not found. Install it (npm install -g @openai/codex) or give its path in the server policy (codexPath).");
        ProcessStartInfo info = new(file);
        foreach (string a in prefix.Concat(args))
        {
            info.ArgumentList.Add(a);
        }

        return info;
    }

    /// <summary>Does the handshake of the protocol: initialize, then the notification that confirms it.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "ample", ["title"] = "AMPLE", ["version"] = "0.1.0" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        Notify("initialized", new JsonObject());
    }

    /// <summary>Sends a request and waits for its answer.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref nextId);
        TaskCompletionSource<JsonNode?> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = source;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            try
            {
                await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() }, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The deadline covers queueing and writing. A partly written line cannot be retried safely, so the connection is closed.
                StopNow();
                throw new TimeoutException($"Codex did not accept {method} in time.");
            }

            using (limit.Token.Register(() => source.TrySetException(new TimeoutException($"Codex did not answer {method} in time.{(Stderr.Length > 0 ? " " + Stderr[..Math.Min(300, Stderr.Length)] : string.Empty)}"))))
            {
                return await source.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification (no answer).</summary>
    public void Notify(string method, JsonObject parameters) => _ = WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    /// <summary>Immediately stops this task's Codex process and its descendants.</summary>
    public void StopNow()
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.NotSupportedException) { /* already stopped */ }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        string line = message.ToJsonString();
        Raw?.Invoke(">> " + (line.Length > 4000 ? line[..4000] + "…" : line));
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
                if (node is not JsonObject message)
                {
                    continue;
                }

                Raw?.Invoke("<< " + (line.Length > 4000 ? line[..4000] + "…" : line));
                string? method = message["method"]?.GetValue<string>();
                JsonNode? id = message["id"];
                if (method is null && id is not null)
                {
                    if (pending.TryGetValue(id.GetValue<long>(), out TaskCompletionSource<JsonNode?>? source))
                    {
                        if (message["error"] is JsonNode error)
                        {
                            source.TrySetException(new AmpleException("AMP5454", "Codex: " + (error["message"]?.GetValue<string>() ?? "the request failed")));
                        }
                        else
                        {
                            source.TrySetResult(message["result"]?.DeepClone());
                        }
                    }

                    continue;
                }

                if (method is null)
                {
                    continue;
                }

                JsonObject parameters = message["params"] as JsonObject ?? [];
                if (id is not null)
                {
                    JsonNode requestId = id.DeepClone();
                    _ = Task.Run(async () =>
                    {
                        JsonNode? result = null;
                        try { result = OnServerRequest is null ? null : await OnServerRequest(method, parameters).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { Raw?.Invoke("!! " + ex.Message); }
                        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId, ["result"] = result ?? new JsonObject() }).ConfigureAwait(false);
                    });
                }
                else
                {
                    OnNotification?.Invoke(method, parameters);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // the process ended
        }
        finally
        {
            foreach (TaskCompletionSource<JsonNode?> source in pending.Values)
            {
                source.TrySetException(new AmpleException("AMP5453", "Codex stopped." + (Stderr.Length > 0 ? " " + Stderr[..Math.Min(400, Stderr.Length)] : string.Empty)));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }

        try { await reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* the reader ends with the process */ }
        process.Dispose();
    }
}
