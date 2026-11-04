using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Ample.Server.Lite;

/// <summary>The routes of sharing: make a share, look at one, fetch its files, and manage it with the code of the maker (change the duration, take it down).</summary>
public static class LiteSharesApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the routes and starts the hourly upkeep (the ageing of what is kept about makers, and the log of what a cleanup would remove).</summary>
    /// <param name="app">The web application.</param>
    /// <param name="shares">The store.</param>
    /// <param name="blobs">The files that the server knows (demo files and kept uploads).</param>
    /// <param name="lim">The limits.</param>
    /// <param name="governor">The abuse protection that all Lite routes share.</param>
    public static void Map(IEndpointRouteBuilder app, LiteShares shares, LiteBlobs blobs, LiteLimits lim, LiteGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(lim);
        ArgumentNullException.ThrowIfNull(governor);

        app.MapPost("/api/remix/shares", async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType) return Problem("AMPL900", 400, "A share is a multipart form: a manifest and the files that the server does not have.");
            IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = lim.MaxProjectBytes + (4L * 1024 * 1024);
            IReadOnlyList<string> keys = LiteClient.Keys(context);
            try
            {
                governor.Spend("share", keys, lim.SharesPerMinute, lim.SharesPerHour);
                IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                if (!form.TryGetValue("manifest", out Microsoft.Extensions.Primitives.StringValues text) || text.Count != 1) throw new LiteRejectedException("AMPL900", 400, "The manifest is missing.");
                (ShareRequest request, List<(string Path, string Hash)> listed) = ParseShare(text.ToString(), lim);
                if (form.Files.Count > listed.Count) throw new LiteRejectedException("AMPL900", 400, "The manifest and the files do not match.");
                List<LiteFile> files = [];
                bool gone = false;
                for (int i = 0; i < listed.Count; i++)
                {
                    (string path, string hash) = listed[i];
                    IFormFile? part = form.Files[$"f{i}"];
                    if (part is null)
                    {
                        if (blobs.TryGet(hash, out byte[] known)) files.Add(new LiteFile(path, known)); else gone = true;
                        continue;
                    }

                    if (part.Length > Math.Max(lim.MaxAudioBytes, lim.MaxSoundFontBytes)) throw new LiteRejectedException("AMPL905", 413, $"\"{LitePolicy.CleanPath(path, lim)}\" is too large.");
                    using MemoryStream copy = new();
                    await part.CopyToAsync(copy, context.RequestAborted).ConfigureAwait(false);
                    byte[] data = copy.ToArray();
                    if (!string.Equals(LiteBlobs.Hash(data), hash, StringComparison.Ordinal)) throw new LiteRejectedException("AMPL943", 400, $"File {i} does not match its hash.");
                    blobs.Put(data);
                    files.Add(new LiteFile(path, data));
                }

                if (gone) throw new LiteRejectedException("AMPL942", 409, "Some files are no longer on the server (it keeps them for an hour): send the share again with all files.");
                LitePolicy.CheckFiles(request.Entry, files, lim);
                string agent = context.Request.Headers.UserAgent.ToString();
                ShareCreated made = shares.Create(request, [.. files.Select(f => (f.Path, f.Data, blobs.IsDemo(LiteBlobs.Hash(f.Data))))], AddressOf(context), agent.Length > 300 ? agent[..300] : agent, keys[0]);
                return Results.Json(new { id = made.Id, code = made.Code, expires = made.Expires, url = "/mix/" + made.Id }, Json);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message, rejected.RetryAfterSeconds, context);
            }
        });

        app.MapPost("/api/remix/admin/shares", async (HttpContext context) =>
        {
            // the owner's way to put in a permanent share: only when AMPLE_REMIX_ADMIN_KEY is set, only from this machine, only with the key; anything else looks like a route that does not exist. No rate limit (nginx answers 404 for /api/remix/admin/ from outside).
            if (!AdminAllowed(context)) return Problem("AMP5285", 404, "This address does not exist.");
            IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = lim.MaxProjectBytes + (1L * 1024 * 1024);
            try
            {
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                JsonElement root = body.RootElement;
                string Read(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
                List<LiteFile> files = [.. root.GetProperty("files").EnumerateArray().Select(f => new LiteFile(f.GetProperty("path").GetString() ?? string.Empty, System.Text.Encoding.UTF8.GetBytes(f.GetProperty("text").GetString() ?? string.Empty)))];
                if (files.Count == 0 || files.Count > lim.MaxFiles) throw new LiteRejectedException("AMPL900", 400, $"A share has 1 to {lim.MaxFiles} files.");
                string entry = Read("entry");
                LitePolicy.CheckFiles(entry, files, lim);
                ShareRecord share = shares.PutPermanent(Read("id"), Read("title"), Read("author"), Read("description"), entry, [.. files.Select(f => (f.Path, f.Data))]);
                return Results.Json(new { id = share.Id, permanent = true, expires = (DateTime?)null, url = "/mix/" + share.Id, files = share.Files.Select(f => new { path = f.Path, sha256 = f.Sha256, size = f.Size }) }, Json);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or BadHttpRequestException)
            {
                return Problem("AMPL900", 400, "The request is {\"id\", \"title\", \"author\", \"description\", \"entry\", \"files\": [{\"path\", \"text\"}]}.");
            }
        });

        app.MapGet("/api/remix/shares/{id}", (string id, HttpContext context) =>
        {
            (ShareRecord Share, string State)? found = shares.Find(id);
            if (found is null) return Problem("AMPL948", 404, "This share does not exist.");
            if (found.Value.State != "available") return Problem("AMPL948", 410, "This share is no longer available.");
            return Results.Json(Shape(found.Value.Share, found.Value.State), Json);
        });

        app.MapGet("/api/remix/shares/{id}/files/{hash}", (string id, string hash) =>
        {
            // a file that the share lists (its own, or one of a built-in demo: the server has those) of a share that is available
            (ShareRecord Share, string State)? found = shares.Find(id);
            if (!LiteBlobs.IsHash(hash) || found is not { State: "available" } || !found.Value.Share.Files.Any(f => f.Sha256 == hash) || !blobs.TryGet(hash, out byte[] data)) return Problem("AMPL948", 404, "This file is not available.");
            return Results.File(data, "application/octet-stream");
        });

        app.MapGet("/api/remix/shares/{id}/archive", (string id) =>
        {
            // every file of an available share in ONE answer (stored, not compressed): the page opens a share with one request instead of one per file (a proxy in front limits how many requests a visitor may make at once)
            (ShareRecord Share, string State)? found = shares.Find(id);
            if (found is null) return Problem("AMPL948", 404, "This share does not exist.");
            if (found.Value.State != "available") return Problem("AMPL948", 410, "This share is no longer available.");
            using MemoryStream output = new();
            using (ZipArchive zip = new(output, ZipArchiveMode.Create, true))
            {
                foreach (ShareFile file in found.Value.Share.Files)
                {
                    if (!blobs.TryGet(file.Sha256, out byte[] data)) return Problem("AMPL948", 404, $"The share is missing \"{file.Path}\".");
                    ZipArchiveEntry entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression);
                    using Stream stream = entry.Open();
                    stream.Write(data, 0, data.Length);
                }
            }

            return Results.File(output.ToArray(), "application/zip");
        });

        app.MapPost("/api/remix/shares/{id}/manage", async (string id, HttpContext context) =>
        {
            IHttpMaxRequestBodySizeFeature? small = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (small is { IsReadOnly: false }) small.MaxRequestBodySize = 16 * 1024;
            try
            {
                governor.Spend("sharecode", LiteClient.Keys(context), 10, 60);
                using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                string code = body.RootElement.GetProperty("code").GetString() ?? string.Empty;
                string action = body.RootElement.GetProperty("action").GetString() ?? string.Empty;
                string? expires = body.RootElement.TryGetProperty("expires", out JsonElement e) ? e.GetString() : null;
                (ShareRecord share, string state) = shares.Manage(id, code, action, expires);
                return Results.Json(Shape(share, state), Json);
            }
            catch (LiteRejectedException rejected)
            {
                return Problem(rejected.Code, rejected.Status, rejected.Message, rejected.RetryAfterSeconds, context);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or BadHttpRequestException)
            {
                return Problem("AMPL900", 400, "The request is {\"code\": ..., \"action\": \"info|expires|delete\", \"expires\": \"1h|24h|1w|1m|never\"}.");
            }
        });

        if (app is WebApplication web)
        {
            _ = Task.Run(async () =>
            {
                using PeriodicTimer timer = new(TimeSpan.FromHours(1));
                try
                {
                    Upkeep(shares);
                    while (await timer.WaitForNextTickAsync(web.Lifetime.ApplicationStopping).ConfigureAwait(false)) Upkeep(shares);
                }
                catch (OperationCanceledException)
                {
                    // the server is stopping
                }
            });
        }
    }

    private static void Upkeep(LiteShares shares)
    {
        try
        {
            shares.Maintain();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine("[AMPLE] Remix shares upkeep failed: " + ex.Message);
        }
    }

    /// <summary>The public shape of a share: what a visitor may see (never the address, the browser or the code of the maker).</summary>
    private static object Shape(ShareRecord share, string state) => new
    {
        id = share.Id,
        title = share.Title,
        author = share.Author,
        description = share.Description,
        created = share.Created,
        expires = share.Expires,
        permanent = share.Permanent,
        entry = share.Entry,
        state,
        files = share.Files.Select(f => new { path = f.Path, sha256 = f.Sha256, size = f.Size, demo = f.Demo }),
    };

    private static (ShareRequest Request, List<(string Path, string Hash)> Files) ParseShare(string text, LiteLimits lim)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string Read(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
            bool rights = root.TryGetProperty("rights", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            List<(string Path, string Hash)> files = [.. root.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("path").GetString() ?? string.Empty, f.GetProperty("sha256").GetString() ?? string.Empty))];
            if (files.Count == 0 || files.Count > lim.MaxFiles || files.Any(f => !LiteBlobs.IsHash(f.Hash))) throw new LiteRejectedException("AMPL900", 400, $"A share lists 1 to {lim.MaxFiles} files, each with a SHA-256.");
            return (new ShareRequest(Read("title"), Read("author"), Read("description"), Read("expires"), rights, Read("entry")), files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new LiteRejectedException("AMPL900", 400, "The manifest is not valid: title, author, description, expires, rights, entry and files [{path, sha256}].");
        }
    }

    /// <summary>True when the admin route may answer: the key is set, the request comes from this machine (loopback, so through the local proxy at most) and carries the key.</summary>
    private static bool AdminAllowed(HttpContext context)
    {
        string? key = Environment.GetEnvironmentVariable("AMPLE_REMIX_ADMIN_KEY");
        if (string.IsNullOrEmpty(key)) return false;
        if (context.Connection.RemoteIpAddress is not { } remote || !System.Net.IPAddress.IsLoopback(remote)) return false;
        string given = context.Request.Headers["X-Ample-Admin"].FirstOrDefault() ?? string.Empty;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(given)), System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }

    private static string AddressOf(HttpContext context)
    {
        string? forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        bool fromProxy = context.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote);
        string first = (forwarded ?? string.Empty).Split(',')[0].Trim();
        return fromProxy && first.Length > 0 ? first : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static IResult Problem(string code, int status, string message, int retryAfter = 0, HttpContext? context = null)
    {
        if (retryAfter > 0 && context is not null) context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new { error = new { code, message, retryAfter } }, Json, statusCode: status);
    }
}
