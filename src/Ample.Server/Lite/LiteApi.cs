using System.Text.Json;
using System.Threading.Channels;
using Ample.Language;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Ample.Server.Lite;

/// <summary>
/// The routes of Lite (plan/lite/PLAN.md, D7): the limits, a build and the audio of a build. They live under <c>/api</c>, so the existing token or sign-in covers them; nothing here
/// reads the project of the server session. This is the one place that a standalone Lite would run apart.
/// </summary>
public static class LiteApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serves the files of the Lite page and its demos (<c>web/remix</c>) under <c>/lite</c>. Studio's static files refuse unknown types; the demos are <c>.amplet</c>, <c>.amplec</c> and <c>.sf2</c> files, so this knows them (as plain text or bytes, never as a page).</summary>
    /// <param name="app">The web application.</param>
    /// <param name="webRoot">The <c>web</c> folder of AMPLE.</param>
    public static void UseStaticFiles(IApplicationBuilder app, string webRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        string liteRoot = Path.Combine(webRoot, "remix");
        if (!Directory.Exists(liteRoot)) return;
        Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider types = new();
        foreach (string text in new[] { ".amplet", ".amplec", ".ampleseq", ".sfz" }) types.Mappings[text] = "text/plain; charset=utf-8";
        types.Mappings[".sf2"] = "application/octet-stream";
        types.Mappings[".sf3"] = "application/octet-stream";
        types.Mappings[".mid"] = "audio/midi";
        types.Mappings[".midi"] = "audio/midi";
        app.UseStaticFiles(new StaticFileOptions { RequestPath = "/remix", FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(liteRoot), ContentTypeProvider = types });
    }

    /// <summary>The Lite routes for the OpenAPI document (they are mapped here and in <see cref="LiteLanguage"/>, outside the Studio route table; none of them changes a project).</summary>
    public static IReadOnlyList<ApiOperation> Operations { get; } =
    [
        new("GET", "/api/remix/limits", "lite", "The limits of AMPLE Remix: files, sizes, build time, output length.", false, true),
        new("POST", "/api/remix/build", "lite", "Builds the text and sound files the page sends (multipart: a manifest and the parts f0..fn) in a private temporary workspace and returns the diagnostics and an output id.", false, true),
        new("GET", "/api/remix/output/{id}", "lite", "The audio of a Remix build, kept for a few minutes: WAV as it is, or MP3 / FLAC with ?format= (encoded on demand; counted per client, queued and size-limited, 429 with Retry-After when a limit is passed).", false, true),
        new("DELETE", "/api/remix/output/{id}", "lite", "Removes a Remix build output.", false, true),
        new("POST", "/api/remix/blobs/missing", "lite", "Which of these file hashes (SHA-256) the server does not have: it knows the files of the demos and keeps the files of earlier builds for an hour, so a build only has to send what is missing.", false, true),
        new("POST", "/api/remix/shares", "lite", "Shares a song of the visitor: a manifest (title, author, optional description, how long, the rights, the song and the files by SHA-256) and the files that the server does not have. Answers a short id and the management code (shown once).", false, true),
        new("GET", "/api/remix/shares/{id}", "lite", "A shared song: title, author, description, dates and its files. 410 when it is no longer available (expired, taken down or pushed out of the store).", false, true),
        new("GET", "/api/remix/shares/{id}/files/{hash}", "lite", "The bytes of a file that an available share lists, by its SHA-256 (its own files, and the files of built-in demos that it uses).", false, true),
        new("GET", "/api/remix/shares/{id}/archive", "lite", "All the files of an available share as one .zip (stored): the way to open a share with a single request.", false, true),
        new("POST", "/api/remix/shares/{id}/manage", "lite", "The maker's management with the code: info, change the duration (counted from now) or take the share down.", false, true),
        new("POST", "/api/remix/components/check", "lite", "Checks the text of a component file with the files the page sends.", false, true),
        new("POST", "/api/remix/components/complete", "lite", "Completion in a component file with the files the page sends.", false, true),
    ];

    /// <summary>Maps the Lite routes.</summary>
    /// <param name="app">The web application.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="libraryHome">The AMPLE library home (the work root lives in it).</param>
    /// <param name="limits">The limits, or null for the defaults (with the environment on top).</param>
    /// <param name="governor">The abuse protection that all Lite routes share, or null for one of its own.</param>
    /// <param name="publicMode">True for the public Lite (<c>--lite-only</c>): the limits endpoint says so and the page works without a sign-in.</param>
    /// <param name="ampleRoot">The AMPLE folder, needed to start isolated builds.</param>
    /// <param name="isolate">True to build in a process of its own (see <see cref="LiteIsolation"/>); it falls back to building in this process when no worker program is found.</param>
    public static void Map(IEndpointRouteBuilder app, SpecData spec, string libraryHome, LiteLimits? limits = null, LiteGovernor? governor = null, bool publicMode = false, string? ampleRoot = null, bool isolate = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(spec);
        LiteLimits lim = limits ?? LiteLimits.FromEnvironment();
        LiteOutputs outputs = new();
        LiteShares shares = new(Path.Combine(libraryHome, "lite-shares"), new ShareLimits(lim.ShareStoreMegabytes * 1024L * 1024, lim.ShareCeilingMegabytes * 1024L * 1024, lim.ShareMegabytesPerIpPerHour * 1024L * 1024));
        LiteBlobs blobs = new(ampleRoot is null ? null : Path.Combine(ampleRoot, "web"), lim.BlobCacheMegabytes * 1024L * 1024, TimeSpan.FromMinutes(lim.BlobCacheMinutes));
        if (governor is null)
        {
            governor = new LiteGovernor(lim);
            if (app is WebApplication web) web.Lifetime.ApplicationStopped.Register(governor.Dispose);
        }

        string? workerPath = isolate && ampleRoot is not null ? LiteIsolation.FindWorker() : null;
        LiteIsolation? isolation = workerPath is null ? null : new LiteIsolation(workerPath, ampleRoot!);
        if (isolate && isolation is null) Console.Error.WriteLine("[AMPLE] Remix: no worker program found (this is not Ample.Cli and AMPLE_LITE_WORKER is not set): builds run in this process.");
        try
        {
            LiteWorkspace.Sweep(libraryHome, TimeSpan.FromHours(1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a locked leftover is tried again at the next start
        }

        app.MapGet("/api/remix/limits", () =>
        {
            System.Text.Json.Nodes.JsonObject limitsJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(lim, Json))!.AsObject().WithPublic(publicMode);
            limitsJson["reportEmail"] = Environment.GetEnvironmentVariable("AMPLE_REMIX_REPORT_EMAIL") is { Length: > 0 } report ? report : string.Empty;   // where complaints about a share go: a setting of the operator, nothing in the code
            limitsJson["storeName"] = Environment.GetEnvironmentVariable("AMPLE_REMIX_STORE") is { Length: > 0 } store ? store : "ample-remix";   // the name of the browser store of this instance (the old host keeps ample-lite)
            return Results.Json(limitsJson, Json);
        });

        blobs.ExtraHas = shares.HasAvailableFile;
        blobs.ExtraGet = (hash) => shares.TryOpenAny(hash, out byte[] data) ? data : null;
        LiteSharesApi.Map(app, shares, blobs, lim, governor);
        app.MapPost("/api/remix/blobs/missing", async (HttpContext context) =>
        {
            IHttpMaxRequestBodySizeFeature? small = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (small is { IsReadOnly: false }) small.MaxRequestBodySize = 64 * 1024;
            try
            {
                governor.Spend("check", LiteClient.Keys(context), lim.ChecksPerMinute, lim.ChecksPerMinute * 30);
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                List<string> hashes = [.. body.RootElement.GetProperty("hashes").EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
                if (hashes.Count > lim.MaxFiles || hashes.Any(h => !LiteBlobs.IsHash(h))) return Problem("AMPL900", 400, "The hashes are at most as many as the files of a project, each a SHA-256 in lower case hexadecimal.");
                return Results.Json(new { missing = hashes.Where(h => !blobs.Has(h)).Distinct(StringComparer.Ordinal).ToArray() }, Json);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message, rejected.RetryAfterSeconds, context);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or BadHttpRequestException)
            {
                return Problem("AMPL900", 400, "The request is {\"hashes\": [sha256, ...]}.");
            }
        });
        app.MapPost("/api/remix/build", async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType) return Problem("AMPL900", 400, "A build is a multipart form: a manifest and the files.");
            IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = lim.MaxProjectBytes + (4L * 1024 * 1024);
            IReadOnlyList<string> keys = LiteClient.Keys(context);
            try
            {
                // the rate limits first (cheap, before the files are read), then the files, then the place in the queue
                governor.Spend("build", keys, lim.BuildsPerMinute, lim.BuildsPerHour);
                (string entry, List<LiteFile> files) = await Read(context, lim, blobs).ConfigureAwait(false);
                if (context.Request.Query["stream"] == "1") return await StreamBuild(context, spec, libraryHome, entry, files, lim, outputs, governor, keys, isolation).ConfigureAwait(false);
                using IDisposable slot = await governor.EnterBuildAsync(keys, null, context.RequestAborted).ConfigureAwait(false);
                LiteBuildResult result = isolation is not null
                    ? await isolation.BuildAsync(libraryHome, entry, files, lim, outputs, null, context.RequestAborted).ConfigureAwait(false)
                    : await LiteBuilder.BuildAsync(spec, libraryHome, entry, files, lim, outputs, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(Shape(result), Json);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message, rejected.RetryAfterSeconds, context);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // nothing of the exception goes to the page: a short id, the details stay in the server window
                string id = Guid.NewGuid().ToString("N")[..8];
                await Console.Error.WriteLineAsync($"[AMPLE] lite build {id} failed: {ex}").ConfigureAwait(false);
                return Problem("AMPL931", 500, $"The build failed inside Remix (reference {id}).");
            }
        });

        // the audio of a build: wav as it is (the page plays it), mp3 or flac encoded on demand (counted, queued, cached for a few minutes)
        app.MapGet("/api/remix/output/{id}", async (HttpContext context, string id) =>
        {
            string format = (context.Request.Query["format"].FirstOrDefault() ?? "wav").ToLowerInvariant();
            if (format != "wav" && !LiteEncoder.Formats.Contains(format)) return Problem("AMPL934", 400, "The format must be wav, mp3 or flac.");
            byte[]? wav = outputs.Get(id);
            if (wav is null) return Results.NotFound();
            if (format == "wav") return Results.File(wav, "audio/wav", "lite.wav");
            try
            {
                IReadOnlyList<string> keys = LiteClient.Keys(context);
                governor.Spend("download", keys, lim.DownloadsPerMinute, lim.DownloadsPerHour);
                byte[]? encoded = outputs.GetVariant(id, format);
                if (encoded is null)
                {
                    using IDisposable slot = await governor.EnterEncodeAsync(context.RequestAborted).ConfigureAwait(false);
                    encoded = outputs.GetVariant(id, format) ?? await LiteEncoder.EncodeAsync(wav, format, lim, context.RequestAborted).ConfigureAwait(false);
                    outputs.PutVariant(id, format, encoded, lim);
                }

                governor.SpendBytes(keys, encoded.LongLength);
                return Results.File(encoded, LiteEncoder.ContentType(format), "lite." + format);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message, rejected.RetryAfterSeconds, context);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
        });
        app.MapDelete("/api/remix/output/{id}", (string id) => outputs.Remove(id) ? Results.NoContent() : Results.NotFound());
    }

    private static object Shape(LiteBuildResult result) => new { ok = result.Ok, diagnostics = result.Diagnostics, outputId = result.OutputId, seconds = result.Seconds, audioSeconds = result.AudioSeconds, bytes = result.Bytes };

    /// <summary>A build that tells the page what it does: one JSON object per line (<c>log</c> with text and how far the build is, then <c>result</c>). The page cancels by closing the request.</summary>
    private static async Task<IResult> StreamBuild(HttpContext context, SpecData spec, string libraryHome, string entry, List<LiteFile> files, LiteLimits lim, LiteOutputs outputs, LiteGovernor governor, IReadOnlyList<string> keys, LiteIsolation? isolation)
    {
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        async Task Send(object message)
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(message, Json) + (char)10, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }

        // the place in the queue is told while the build waits for its turn; a refusal here is a line of the stream (the status is already 200)
        IDisposable? taken;
        try
        {
            taken = await governor.EnterBuildAsync(keys, (place, total) => Send(new { t = "queue", place, total }), context.RequestAborted).ConfigureAwait(false);
        }
        catch (LiteRejectedException rejected)
        {
            await Send(new { t = "error", code = rejected.Code, message = rejected.Message, retryAfter = rejected.RetryAfterSeconds }).ConfigureAwait(false);
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            return Results.Empty;
        }

        using IDisposable held = taken;
        Channel<string> lines = Channel.CreateUnbounded<string>();
        double lastFraction = 0;
        Task<LiteBuildResult> build = Task.Run(async () =>
        {
            try
            {
                void Told(string text, double fraction)
                {
                    if (fraction >= 0) lastFraction = Math.Max(lastFraction, fraction);
                    lines.Writer.TryWrite(JsonSerializer.Serialize(new { t = "log", text, fraction = lastFraction }, Json));
                }

                return isolation is not null
                    ? await isolation.BuildAsync(libraryHome, entry, files, lim, outputs, Told, context.RequestAborted).ConfigureAwait(false)
                    : await LiteBuilder.BuildAsync(spec, libraryHome, entry, files, lim, outputs, context.RequestAborted, Told).ConfigureAwait(false);
            }
            finally
            {
                lines.Writer.Complete();
            }
        });
        try
        {
            await foreach (string line in lines.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await context.Response.WriteAsync(line + '\n', context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }

            LiteBuildResult result = await build.ConfigureAwait(false);
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { t = "result", result = Shape(result) }, Json) + '\n', context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the page closed the request: the build is cancelled through the same token and cleans up its workspace
        }
        catch (LiteRejectedException rejected)
        {
            // a refusal of the isolated worker (too much memory, not startable) is a line of the stream: the status is already 200
            await Send(new { t = "error", code = rejected.Code, message = rejected.Message, retryAfter = rejected.RetryAfterSeconds }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string reference = Guid.NewGuid().ToString("N")[..8];
            await Console.Error.WriteLineAsync($"[AMPLE] lite build {reference} failed: {ex}").ConfigureAwait(false);
            await Send(new { t = "error", code = "AMPL931", message = $"The build failed inside Remix (reference {reference}).", retryAfter = 0 }).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static IResult Problem(string code, int status, string message, int retryAfter = 0, HttpContext? context = null)
    {
        if (retryAfter > 0 && context is not null) context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new { error = new { code, message, retryAfter } }, Json, statusCode: status);
    }

    private static async Task<(string Entry, List<LiteFile> Files)> Read(HttpContext context, LiteLimits limits, LiteBlobs blobs)
    {
        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        if (!form.TryGetValue("manifest", out Microsoft.Extensions.Primitives.StringValues text) || text.Count != 1) throw new LiteRejectedException("AMPL900", 400, "The manifest is missing.");
        string entry;
        List<(string Path, string? Hash)> names;
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(text.ToString());
            entry = manifest.RootElement.GetProperty("entry").GetString() ?? string.Empty;
            // a file is a path, or {"path", "sha256"}: with a hash its part may be left out when the server has that content already (a demo file, or a file of an earlier build)
            names = [.. manifest.RootElement.GetProperty("files").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                ? (e.GetString() ?? string.Empty, (string?)null)
                : (e.GetProperty("path").GetString() ?? string.Empty, e.TryGetProperty("sha256", out JsonElement hash) ? hash.GetString() : null))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new LiteRejectedException("AMPL900", 400, "The manifest is not valid: it holds {\"entry\": \"song.amplet\", \"files\": [paths or {path, sha256}]}.");
        }

        if (names.Count > limits.MaxFiles) throw new LiteRejectedException("AMPL902", 413, $"A Remix project has at most {limits.MaxFiles} files.");
        if (names.Any(n => n.Hash is not null && !LiteBlobs.IsHash(n.Hash))) throw new LiteRejectedException("AMPL900", 400, "A hash in the manifest is not a SHA-256 in lower case hexadecimal.");
        if (form.Files.Count > names.Count) throw new LiteRejectedException("AMPL900", 400, "The manifest and the files do not match.");
        List<LiteFile> files = [];
        bool anyGone = false;
        for (int i = 0; i < names.Count; i++)
        {
            (string path, string? hash) = names[i];
            IFormFile? part = form.Files[$"f{i}"];
            if (part is null)
            {
                if (hash is null) throw new LiteRejectedException("AMPL900", 400, $"File {i} is missing.");
                if (blobs.TryGet(hash, out byte[] known)) files.Add(new LiteFile(path, known)); else anyGone = true;
                continue;
            }

            if (part.Length > Math.Max(limits.MaxAudioBytes, limits.MaxSoundFontBytes)) throw new LiteRejectedException("AMPL905", 413, $"\"{LitePolicy.CleanPath(path, limits)}\" is too large.");
            using MemoryStream copy = new();
            await part.CopyToAsync(copy, context.RequestAborted).ConfigureAwait(false);
            byte[] data = copy.ToArray();
            if (hash is not null && !string.Equals(LiteBlobs.Hash(data), hash, StringComparison.Ordinal)) throw new LiteRejectedException("AMPL943", 400, $"File {i} does not match its hash.");
            blobs.Put(data);
            files.Add(new LiteFile(path, data));
        }

        // a hash that the server no longer knows (it keeps uploads for an hour): the page sends the build again with every file
        if (anyGone) throw new LiteRejectedException("AMPL942", 409, "Some files are no longer on the server (it keeps them for an hour): send the build again with all files.");
        return (entry, files);
    }

    private static System.Text.Json.Nodes.JsonObject WithPublic(this System.Text.Json.Nodes.JsonObject limits, bool publicMode)
    {
        limits["public"] = publicMode;
        return limits;
    }
}

