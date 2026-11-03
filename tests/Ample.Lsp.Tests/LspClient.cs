using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ample.Language;

namespace Ample.Lsp.Tests;

internal static class Root
{
    private static readonly Lazy<string> Path = new(() =>
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "spec-tests")) && Directory.Exists(System.IO.Path.Combine(dir.FullName, "spec")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("AMPLE folder not found");
    });

    private static readonly Lazy<SpecData> SpecValue = new(() =>
    {
        string spec = System.IO.Path.Combine(Path.Value, "spec");
        return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => System.IO.Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
    });

    public static string Dir => Path.Value;

    public static SpecData Spec => SpecValue.Value;
}

/// <summary>A scripted editor: talks to the server over an in-memory channel.</summary>
internal sealed class LspClient : IAsyncDisposable
{
    private readonly MemoryChannel channel;
    private readonly CancellationTokenSource cts = new();
    private readonly Task<int> serverTask;
    private readonly Task pump;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    private readonly Channel<JsonObject> notifications = Channel.CreateUnbounded<JsonObject>();
    private long next;

    private LspClient(LspServer server)
    {
        (MemoryChannel client, MemoryChannel serverSide) = MemoryChannel.CreatePair();
        channel = client;
        serverTask = server.RunAsync(serverSide, cts.Token);
        pump = Task.Run(async () =>
        {
            for (; ; )
            {
                string? text = await client.ReadAsync(cts.Token);
                if (text is null)
                {
                    break;
                }

                JsonObject msg = (JsonObject)JsonNode.Parse(text)!;
                if (msg["id"] is JsonNode id && msg["method"] is null && pending.TryRemove((long)id, out TaskCompletionSource<JsonObject>? tcs))
                {
                    tcs.SetResult(msg);
                }
                else
                {
                    await notifications.Writer.WriteAsync(msg);
                }
            }
        });
    }

    public int? ExitCode { get; private set; }

    public static async Task<LspClient> StartAsync(string helpBase = "/help")
    {
        LspClient c = new(new LspServer(Root.Spec));
        await c.RequestAsync("initialize", new { processId = (object?)null, rootUri = (object?)null, capabilities = new { }, initializationOptions = new { helpBase } });
        await c.NotifyAsync("initialized", new { });
        return c;
    }

    public async Task<JsonObject> RawAsync(string method, object? parameters)
    {
        long id = Interlocked.Increment(ref next);
        TaskCompletionSource<JsonObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        await channel.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), cts.Token);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public async Task<JsonNode?> RequestAsync(string method, object? parameters)
    {
        JsonObject r = await RawAsync(method, parameters);
        if (r["error"] is JsonNode e)
        {
            throw new InvalidOperationException((string?)e["message"]);
        }

        return r["result"];
    }

    public async Task SendIdAsync(long id, string method, object? parameters, bool register)
    {
        if (register)
        {
            pending[id] = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await channel.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), cts.Token);
    }

    public Task<JsonObject> ResponseAsync(long id) => pending[id].Task.WaitAsync(TimeSpan.FromSeconds(20));

    public async Task NotifyAsync(string method, object? parameters) => await channel.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }), cts.Token);

    public async Task<JsonObject> NextAsync(string method)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        for (; ; )
        {
            JsonObject m = await notifications.Reader.ReadAsync(timeout.Token);
            if ((string?)m["method"] == method)
            {
                return m;
            }
        }
    }

    public Task OpenAsync(string uri, string text, int version = 1) => NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "amplet", version, text } });

    public async Task<JsonArray> DiagnosticsAfterOpenAsync(string uri, string text)
    {
        await OpenAsync(uri, text);
        JsonObject m = await NextAsync("textDocument/publishDiagnostics");
        return (JsonArray)m["params"]!["diagnostics"]!;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RawAsync("shutdown", null);
            await NotifyAsync("exit", null);
            ExitCode = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // The server was already gone.
        }

        await cts.CancelAsync();
        channel.Complete();
        cts.Dispose();
        _ = pump;
    }
}
