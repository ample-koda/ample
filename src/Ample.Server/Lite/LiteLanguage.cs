using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Engine.Modules;
using Ample.Language;
using Ample.Lsp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Ample.Server.Lite;

/// <summary>
/// The language tooling of Lite (plan/lite/PLAN.md, D5 and D6): the same <c>Ample.Lsp</c> server and the same component checks as Studio, over a project that the page holds and sends
/// (text files only), never over a folder of this machine.
/// </summary>
public static class LiteLanguage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How many chain clips one request of <c>/api/remix/notes/origins</c> works out (the page asks for the clips that are chains, a song has few).</summary>
    internal const int MaxOriginBlocks = 12;

    /// <summary>A reader for the files of a Lite project: relative paths as the language writes them (<c>./</c> and <c>\</c> tolerated), nothing outside the files that were given.</summary>
    /// <param name="files">The text files by path.</param>
    /// <returns>The reader.</returns>
    public static Func<string, string?> Reader(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return target =>
        {
            string path = (target ?? string.Empty).Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            return path.Length > 0 && !path.Contains("..", StringComparison.Ordinal) && !path.StartsWith('/') && files.TryGetValue(path, out string? text) ? text : null;
        };
    }

    /// <summary>Maps the language server of Lite on <c>/lsp/remix</c> (under <c>/lsp</c>, so the token check of the server covers it) and the component checks under <c>/api/remix/components</c>.</summary>
    /// <param name="app">The web application.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="limits">The limits, or null for the defaults.</param>
    /// <param name="governor">The abuse protection shared with the other Lite routes (connections and checks per client), or null for none.</param>
    public static void Map(IEndpointRouteBuilder app, SpecData spec, LiteLimits? limits = null, LiteGovernor? governor = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(spec);
        LiteLimits lim = limits ?? LiteLimits.Default;
        app.Map("/lsp/remix", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.Json(new { error = new { code = "AMP5285", message = "The language server is a WebSocket: connect with ws://." } }, Json, statusCode: 426);
            IDisposable? slot = null;
            if (governor is not null)
            {
                try
                {
                    slot = governor.EnterSocket(LiteClient.Keys(context), lim.MaxSockets);
                }
                catch (LiteRejectedException rejected)
                {
                    context.Response.Headers.RetryAfter = rejected.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(new { error = new { code = rejected.Code, message = rejected.Message, retryAfter = rejected.RetryAfterSeconds } }, Json, statusCode: rejected.Status);
                }
            }

            using IDisposable? held = slot;
            using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using WebSocketChannel channel = new(socket, lim.BytesAccepted + (1L * 1024 * 1024));
            ConcurrentDictionary<string, string> files = new(StringComparer.Ordinal);
            LiteChannel lite = new(channel, files, lim);
            Func<string, string?> read = Reader(files);
            await new LspServer(spec, "/help", null, read, null, text => ModuleService.ComponentsForEditors(text, spec, t => read(t))).RunAsync(lite, context.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        });
        app.MapPost("/api/remix/components/check", async (HttpContext context) =>
        {
            if (CountedResult(governor, lim, context) is { } refusedCheck) return refusedCheck;
            (Func<string, string?> read, JsonElement body) = await Files(context, lim).ConfigureAwait(false);
            return Results.Json(ComponentsApi.CheckText(spec, read, body.GetProperty("text").GetString() ?? string.Empty), Json);
        });
        app.MapPost("/api/remix/components/complete", async (HttpContext context) =>
        {
            if (CountedResult(governor, lim, context) is { } refusedComplete) return refusedComplete;
            (Func<string, string?> read, JsonElement body) = await Files(context, lim).ConfigureAwait(false);
            return Results.Json(ComponentsApi.Complete(spec, read, body.GetProperty("text").GetString() ?? string.Empty, body.GetProperty("line").GetInt32(), body.GetProperty("character").GetInt32()), Json);
        });
        app.MapPost("/api/remix/notes/origins", async (HttpContext context) =>
        {
            if (CountedResult(governor, lim, context) is { } refusedOrigins) return refusedOrigins;
            (Func<string, string?> read, JsonElement body) = await Files(context, lim).ConfigureAwait(false);
            string song = body.TryGetProperty("text", out JsonElement songText) && songText.ValueKind == JsonValueKind.String ? songText.GetString() ?? string.Empty : string.Empty;
            JsonObject origins = [];
            if (body.TryGetProperty("blocks", out JsonElement names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (string name in names.EnumerateArray().Select(n => n.GetString() ?? string.Empty).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxOriginBlocks))
                {
                    JsonArray list = [.. NoteBake.Origins(song, spec, name, read, NotePipeline.HardCap, null, context.RequestAborted)
                        .Select(n => (JsonNode?)new JsonObject { ["at"] = n.At, ["len"] = n.Len, ["pitch"] = n.Pitch, ["source"] = n.Origin?.Source, ["index"] = n.Origin?.Index, ["lap"] = n.Origin?.Lap })];
                    if (list.Count > 0) origins[name] = list;
                }
            }

            return Results.Json(new { origins }, Json);
        });
    }

    private static IResult? CountedResult(LiteGovernor? governor, LiteLimits lim, HttpContext context)
    {
        if (governor is null) return null;
        try
        {
            governor.Spend("check", LiteClient.Keys(context), lim.ChecksPerMinute, lim.ChecksPerMinute * 30);
            return null;
        }
        catch (LiteRejectedException rejected)
        {
            context.Response.Headers.RetryAfter = rejected.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = new { code = rejected.Code, message = rejected.Message, retryAfter = rejected.RetryAfterSeconds } }, Json, statusCode: rejected.Status);
        }
    }

    private static async Task<(Func<string, string?> Read, JsonElement Body)> Files(HttpContext context, LiteLimits limits)
    {
        // refuse a long body while it is read, not after it is all in memory (the server default is 30 MB per request)
        IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = limits.BytesAccepted + (1L * 1024 * 1024);
        using MemoryStream copy = new();
        await context.Request.Body.CopyToAsync(copy, context.RequestAborted).ConfigureAwait(false);
        if (copy.Length > limits.BytesAccepted) throw new BadHttpRequestException("The request is too large.", 413);
        JsonElement body = JsonDocument.Parse(copy.ToArray()).RootElement.Clone();
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        if (body.TryGetProperty("files", out JsonElement list) && list.ValueKind == JsonValueKind.Array) Apply(files, list.EnumerateArray().Select(f => (f.GetProperty("path").GetString() ?? string.Empty, f.GetProperty("text").GetString() ?? string.Empty)), limits);
        return (Reader(files), body);
    }

    /// <summary>Puts text files into a dictionary the way the language tooling accepts them: a valid path, a text size within the limit, no more files than allowed. The rest is left out without a word (the build says what is wrong).</summary>
    internal static void Apply(IDictionary<string, string> files, IEnumerable<(string Path, string Text)> incoming, LiteLimits limits)
    {
        foreach ((string path, string text) in incoming)
        {
            if (text.Length > limits.MaxTextBytes || (!files.ContainsKey(path) && files.Count >= limits.FilesAccepted)) continue;
            try
            {
                _ = LitePolicy.CleanPath(path, limits);
            }
            catch (LiteRejectedException)
            {
                continue;
            }

            files[path] = text;
        }
    }

    /// <summary>Sits between the socket and the language server and takes the two Lite notifications out of the stream: <c>ample/lite/files</c> (the whole snapshot of the text files) and <c>ample/lite/file</c> (one file).</summary>
    private sealed class LiteChannel(ILspChannel inner, ConcurrentDictionary<string, string> files, LiteLimits limits) : ILspChannel
    {
        public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
        {
            for (; ; )
            {
                string? message = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message is null || !message.Contains("\"ample/lite/", StringComparison.Ordinal)) return message;
                try
                {
                    JsonNode? node = JsonNode.Parse(message);
                    string? method = (string?)node?["method"];
                    JsonNode? parameters = node?["params"];
                    if (method == "ample/lite/files" && parameters?["files"] is JsonArray all)
                    {
                        files.Clear();
                        Apply(files, all.Select(f => ((string?)f?["path"] ?? string.Empty, (string?)f?["text"] ?? string.Empty)), limits);
                    }
                    else if (method == "ample/lite/file" && parameters is not null)
                    {
                        Apply(files, [((string?)parameters["path"] ?? string.Empty, (string?)parameters["text"] ?? string.Empty)], limits);
                        if (parameters["deleted"]?.GetValue<bool>() == true) files.TryRemove((string?)parameters["path"] ?? string.Empty, out _);
                    }
                }
                catch (JsonException)
                {
                    // a message that is not valid JSON is not ours: the language server would not make sense of it either
                }
            }
        }

        public ValueTask WriteAsync(string json, CancellationToken cancellationToken) => inner.WriteAsync(json, cancellationToken);
    }
}
