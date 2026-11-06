using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Audio.Midi;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Engine.Midi;
using Ample.Engine.Projects;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;
using Ample.Plugins;
using Ample.Plugins.Analysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Ample.Server;

/// <summary>All API routes. Every route is recorded in <see cref="Catalog"/>, which is the source of the OpenAPI document; every state change is journaled (a command) and the dangerous acts are two-step.</summary>
public static class Routes
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UploadGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte[]> EffectPreviewCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> EffectPreviewCacheOrder = new();
    private static readonly ConcurrentDictionary<string, object> ProjectPreviewCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> ProjectPreviewCacheOrder = new();
    private static readonly SemaphoreSlim EffectPreviewGate = new(2, 2);
    private static long EffectPreviewCacheBytes;
    private static readonly string[] UploadExtensions = [".wav", ".mp3", ".flac", ".ogg", ".mid", ".midi", ".lyr", ".lrc", ".srt", ".txt", ".json", ".amplet", ".module", ".amplec", ".sf2", ".sf3", ".sfz"];
    /// <summary>The value of a file parameter as it is written in the song: a path with a space or another character that cannot stand bare is put in double quotes (with \" and \ escaped), so it stays one value.</summary>
/// <summary>One graph edit of the document route, as an edit of the text. Shared by the single edit and by the atomic list of edits.</summary>
    private static EditResult RunDocEdit(ProjectSession s, AmpleDocument parsed, JsonElement b, JsonElement op)
    {
        if (s.PreviewBuilds.TryGetValue(Str(b, "path"), out bool previewBuild) && previewBuild && (Str(op, "type") == "setFreeze" || (Str(op, "type") == "addFlag" && Str(op, "flag") == "lock") || Str(op, "type") == "bakeCallSite" || Str(op, "type") == "bakeDynamicHelpers"))
        {
            return new EditResult([], "The last build of this song used unsaved text or left out blocks with errors, so its takes are preview takes: save and fix, build again, then freeze or lock.");
        }

        return Str(op, "type") switch
        {
            "setParameter" => Edits.SetParameter(parsed, Str(op, "block"), Str(op, "name"), FileParameterValue(s, parsed, Str(op, "block"), Str(op, "name"), Str(op, "value"))),
            "bakeCallSite" => Edits.BakeCallSite(parsed, op.GetProperty("line").GetInt32(), op.GetProperty("col").GetInt32(), s.LastBuildAt.TryGetValue(Str(b, "path"), out DateTime callAt) ? callAt : parsed.ParsedAt),
            "bakeDynamicHelpers" => Edits.BakeDynamicHelpers(parsed, s.LastBuildAt.TryGetValue(Str(b, "path"), out DateTime builtAt) ? builtAt : parsed.ParsedAt, op.TryGetProperty("block", out JsonElement bakeBlock) && bakeBlock.ValueKind == JsonValueKind.String ? bakeBlock.GetString() : null),
            "setPropertyValue" => Edits.SetPropertyValue(parsed, Str(op, "block"), Str(op, "name"), op.TryGetProperty("value", out JsonElement pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()! : string.Empty, Str(op, "mode")),
            "removeParameter" => Edits.RemoveParameter(parsed, Str(op, "block"), Str(op, "name")),
            "setSetting" => Edits.SetSetting(parsed, Str(op, "key"), Str(op, "value")),
            "removeSetting" => Edits.RemoveSetting(parsed, Str(op, "key")),
            "addBlock" => Edits.AddBlock(parsed, s.Spec, Str(op, "blockType"), Str(op, "name"), Opt(op, "version")),
            "removeBlock" => Edits.RemoveBlock(parsed, Str(op, "block")),
            "connect" => Edits.Connect(parsed, s.Spec, Str(op, "from"), Str(op, "output"), Str(op, "to"), Str(op, "input")),
            "disconnect" => Edits.Disconnect(parsed, Str(op, "to"), Str(op, "input")),
            "setConnectionDisabled" => Edits.SetConnectionDisabled(parsed, Str(op, "to"), Str(op, "input"), op.GetProperty("disabled").GetBoolean()),
            "rename" => Edits.RenameBlock(parsed, Str(op, "block"), Str(op, "newName")),
            "addFlag" => Edits.AddFlag(parsed, Str(op, "block"), Str(op, "flag")),
            "setFreeze" => Edits.SetFreeze(parsed, Str(op, "block"), Str(op, "level")),
            "removeFlag" => Edits.RemoveFlag(parsed, Str(op, "block"), Str(op, "flag")),
            "setLayout" => Edits.SetLayout(parsed, Str(op, "block"), op.GetProperty("x").GetInt32(), op.GetProperty("y").GetInt32()),
            "setParts" => Edits.SetParts(parsed, Str(op, "block"), op.GetProperty("parts").EnumerateArray().Select(p => new Part(p.GetProperty("at").GetInt32(), p.GetProperty("length").GetInt32(), p.TryGetProperty("name", out JsonElement pn) ? pn.GetString() ?? string.Empty : string.Empty)).ToArray()),

            "copyBlock" => Edits.CopyBlock(parsed, Str(op, "block"), Str(op, "newName")),
            "insertEffect" => InsertEffectEdit(s, parsed, op),
            "bypassEffect" => BypassEffectEdit(s, parsed, op),
            _ => new EditResult([], "Unknown edit type."),
        };
    }

    private static EditResult InsertEffectEdit(ProjectSession s, AmpleDocument parsed, JsonElement op)
    {
        List<(string Name, string Value)> parameters = [];
        if (op.TryGetProperty("parameters", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            parameters = [.. list.EnumerateArray().Select(p => (Str(p, "name"), Str(p, "value")))];
        }

        string? text = Edits.InsertEffectAfter(parsed.Text, s.Spec, Str(op, "block"), Str(op, "blockType"), Str(op, "name"), parameters, out string? error);
        return text is null ? new EditResult([], error ?? "The effect could not be put in.") : Edits.ReplaceText(parsed, text);
    }

    private static EditResult BypassEffectEdit(ProjectSession s, AmpleDocument parsed, JsonElement op)
    {
        string? text = Edits.BypassEffect(parsed.Text, s.Spec, Str(op, "block"), out string? error);
        return text is null ? new EditResult([], error ?? "The effect could not be taken out.") : Edits.ReplaceText(parsed, text);
    }

    private static string FileParameterValue(ProjectSession s, AmpleDocument doc, string block, string name, string value)
    {
        string? type = doc.Blocks.FirstOrDefault(b => b.Name == block)?.Type;
        string kind = type is not null && s.Spec.Templates.TryGetValue(type, out TemplateSpec? template) ? template.ParameterSpecs.GetValueOrDefault(name)?.Kind ?? "text" : "text";
        string trimmed = value.Trim();
        if (kind != "file" || trimmed.Length == 0 || trimmed[0] == '"' || trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '/' or ':' or '@' or '+' or '-')) return value;
        return "\"" + trimmed.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string[] VariantFieldNames(JsonElement body, BlockNode block, TemplateSpec template)
    {
        if (body.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
            return [.. fields.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Distinct(StringComparer.Ordinal)];
        return [.. VariantFieldPlanner.Fields(block, template).Where(field => field.Kind is "seed" or "renderer-seed").Select(field => field.Name)];
    }

    private static readonly string[] CompressedAudioExtensions = [".mp3", ".ogg", ".flac"];

    /// <summary>Decodes a compressed audio file (MP3, Ogg, FLAC) to 16-bit PCM WAV with ffmpeg, keeping the sample rate and channels. FLAC is lossless; MP3 and Ogg were lossy already, so the WAV holds exactly what they decode to and nothing is lost on top of that.</summary>
    private static async Task<byte[]> DecodeToWavAsync(byte[] data, string extension, CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-decode-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input" + extension);
        string output = Path.Combine(root, "output.wav");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(input, data, cancellationToken).ConfigureAwait(false);
            string decoder = Environment.GetEnvironmentVariable("AMPLE_FFMPEG") is { Length: > 0 } configured ? configured : "ffmpeg";
            ProcessStartInfo info = new(decoder, $"-y -hide_banner -loglevel error -i \"{input}\" -vn -map_metadata -1 -fflags +bitexact -flags:a +bitexact -c:a pcm_s16le \"{output}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            using Process process = new() { StartInfo = info };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new AmpleException("AMP5290", "Converting this file to WAV needs ffmpeg. Install ffmpeg or set AMPLE_FFMPEG to its executable path.");
            }

            _ = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new AmpleException("AMP5290", "ffmpeg could not decode this audio file.");
            }

            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<(byte[] Data, string Encoder)> EncodeMp3Async(Stream wav, CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-mp3-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input.wav");
        string output = Path.Combine(root, "output.mp3");
        Directory.CreateDirectory(root);
        try
        {
            await using (FileStream file = File.Create(input))
            {
                await wav.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            string encoder = Environment.GetEnvironmentVariable("AMPLE_FFMPEG") is { Length: > 0 } configured ? configured : "ffmpeg";
            ProcessStartInfo versionInfo = new(encoder, "-version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using Process version = new() { StartInfo = versionInfo };
            try
            {
                version.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new AmpleException("AMP5290", "MP3 export needs ffmpeg. Install ffmpeg or set AMPLE_FFMPEG to its executable path.");
            }

            string encoderVersion = await version.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? "ffmpeg";
            await version.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (version.ExitCode != 0)
            {
                throw new AmpleException("AMP5290", "ffmpeg could not report its version for MP3 export.");
            }

            ProcessStartInfo info = new(encoder, $"-y -hide_banner -loglevel error -i \"{input}\" -codec:a libmp3lame \"{output}\"") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            using Process process = new() { StartInfo = info };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new AmpleException("AMP5290", "MP3 export needs ffmpeg. Install ffmpeg or set AMPLE_FFMPEG to its executable path.");
            }

            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new AmpleException("AMP5290", "ffmpeg could not encode this WAV as MP3" + (string.IsNullOrWhiteSpace(error) ? "." : ". Check the installed encoder."));
            }

            return (await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false), encoderVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static readonly Lazy<IReadOnlyList<ApiOperation>> Recorded = new(() =>
    {
        List<ApiOperation> list = [];
        Declare((method, path, tag, summary, mutating, _) => list.Add(new ApiOperation(method, path, tag, summary, mutating, path != "/api/ping")), 512L * 1024 * 1024);
        list.AddRange(Lite.LiteApi.Operations);
        return list;
    });

    /// <summary>Gets the operations of the API (available without a running server).</summary>
    public static IReadOnlyList<ApiOperation> Catalog => Recorded.Value;

    internal static void Map(WebApplication app, ProjectSession s, string token, EventBus events, long maxProjectUploadBytes) => Declare((method, path, tag, summary, mutating, handler) =>
    {
        _ = token;
        if (handler is not null)
        {
            app.MapMethods(path, [method], handler);
        }
    }, maxProjectUploadBytes, app, s, events);

    // The address the assistant's tools call back on. A task started through the public site (nginx and the tunnel) must not send its tool calls that way: nginx answers 405 to them.
    // The connection itself knows where this server listens, so use that (a loopback or the server's own address), over plain http.
    internal static string AssistantBase(HttpContext c)
    {
        System.Net.IPAddress? ip = c.Connection.LocalIpAddress;
        int port = c.Connection.LocalPort;
        if (ip is null || port == 0) return $"{c.Request.Scheme}://{c.Request.Host}";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        string host = ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any) ? "127.0.0.1" : ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        return $"http://{host}:{port}";
    }

    private static JsonElement Obj(JsonElement? e) => e ?? JsonDocument.Parse("{}").RootElement;

    private static string? Fail(string message, out string? failure)
    {
        failure = message;
        return null;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new AmpleException("AMP5285", $"The field '{name}' is required.");

    private static string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool HasProjectLink(PhysicalFileSystem fs, string root, string relative)
    {
        string current = Path.GetFullPath(root);
        foreach (string segment in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (fs.GetLinkTarget(current) is not null) return true;
        }

        return false;
    }

    private static async Task<JsonElement> Body(HttpContext c) => await c.Request.ReadFromJsonAsync<JsonElement>(c.RequestAborted);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new UInt64AsStringConverter() } };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PeakCache = new();

    private static IResult Ok(object value) => Results.Json(value, Web);

    private static HashSet<string>? StorageReasons(JsonElement body) => body.TryGetProperty("reasons", out JsonElement r) && r.ValueKind == JsonValueKind.Array ? [.. r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : null;

    private static TxImportRequest TxImportRequestOf(JsonElement b) => new(Str(b, "path"), Opt(b, "mode") ?? "anyway", Opt(b, "targetFolder"), Opt(b, "newFolder"), Opt(b, "projectName"), Opt(b, "songName"), b.TryGetProperty("preferEmbedded", out JsonElement pe) && pe.ValueKind == JsonValueKind.True, b.TryGetProperty("overrides", out JsonElement ov) && ov.ValueKind == JsonValueKind.Object ? ov.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal) : null);

    private static Task<JsonObject> AcceptWith(ProjectSession s, string id, JsonElement body, CancellationToken ct) => s.Ai.AcceptAsync(id, body.TryGetProperty("anyway", out JsonElement an) && an.ValueKind == JsonValueKind.True, body.TryGetProperty("include", out JsonElement inc) && inc.ValueKind == JsonValueKind.Array ? [.. inc.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null, ct, body.TryGetProperty("moves", out JsonElement ticked) && ticked.ValueKind == JsonValueKind.Array ? [.. ticked.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null);

    private static double Num(HttpContext c, string name) => double.TryParse(c.Request.Query[name].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static ReduxArchiveQuery ReduxQuery(HttpContext c, string defaultScope)
    {
        string scope = c.Request.Query["scope"].ToString();
        if (string.IsNullOrWhiteSpace(scope)) scope = defaultScope;
        DateTimeOffset? from = QueryDate(c.Request.Query["from"].ToString(), "from");
        DateTimeOffset? to = QueryDate(c.Request.Query["to"].ToString(), "to");
        int limit = int.TryParse(c.Request.Query["limit"], out int requestedLimit) ? requestedLimit : 50;
        return new ReduxArchiveQuery(scope, c.Request.Query["ar"].ToString(), c.Request.Query["task"].ToString(), c.Request.Query["session"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["block"].ToString(), c.Request.Query["field"].ToString(), from, to, c.Request.Query["q"].ToString(), limit, c.Request.Query["cursor"].ToString());
    }

    private static async Task<object> ReduxSearchView(ProjectSession s, HttpContext c, ReduxArchiveQuery query, bool taskContext)
    {
        bool includePayload = string.Equals(c.Request.Query["includePayload"], "true", StringComparison.OrdinalIgnoreCase);
        if (!taskContext && query.Scope != "project") throw new AmpleException("AMP5285", "Use a task-scoped AMPLE Redux search for task or session scope.");
        ReduxArchivePage page = taskContext
            ? await s.Ai.ReduxSearch(c.Request.Headers["X-Ample-Context"].ToString(), query, c.RequestAborted).ConfigureAwait(false)
            : await ReduxArchive.SearchAsync(s.Journal, query, c.RequestAborted).ConfigureAwait(false);
        if (includePayload && (string.IsNullOrWhiteSpace(query.Reference) || page.Records.Any(r => r.PayloadJson.Length + (r.PreImageJson?.Length ?? 0) > 2_000_000))) throw new AmpleException("AMP5285", "Payload detail requires one AR reference and is limited to 2 MB.");
        return new { through = page.ThroughSequence, nextCursor = page.NextCursor, records = page.Records.Select(r => includePayload ? (object)new { r.Seq, r.ArId, r.CompactArId, r.At, r.Actor, r.Kind, r.Phase, r.Correlation, r.PayloadJson, r.PreImageJson, r.Error } : new { r.Seq, r.ArId, r.CompactArId, r.At, r.Actor, r.Kind, r.Phase, r.Correlation }) };
    }

    private static DateTimeOffset? QueryDate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)) throw new AmpleException("AMP5285", $"The AMPLE Redux {name} timestamp is invalid.");
        return parsed;
    }

    private static HumOptions HumOptionsOf(HttpContext c) => new(Num(c, "tempo") > 0 ? Num(c, "tempo") : 120, c.Request.Query.ContainsKey("grid") ? Num(c, "grid") : 0.25, c.Request.Query["key"].ToString(), Math.Max(1, (int)Num(c, "bar")));

    private static async Task<byte[]> ReadBodyAsync(HttpContext c, int max)
    {
        Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? limit = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = max;
        }

        await using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await c.Request.Body.ReadAsync(chunk, c.RequestAborted);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > max)
            {
                throw new AmpleException("AMP5470", "The recording is too large.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), c.RequestAborted);
        }

        return buffer.ToArray();
    }

    private static TxExportRequest TxRequest(string path, string mode, bool includeMaster, bool compress, string? folder, string? name, bool whole = false, bool flac = false, string? approval = null) => new(path, mode.Length == 0 ? "full" : mode, includeMaster, compress, folder, name, whole, flac, approval);

    private const long MaxTranscriptionAudioBytes = 128L * 1024 * 1024;
    private const int MaxTranscriptionSeconds = 600;
    private sealed record StoredTranscriptionFile(string Path, string ContentType);
    private sealed record StoredTranscription(string Id, string ResultJson, SortedDictionary<string, StoredTranscriptionFile> Files);
    private sealed record ProjectTreeItem(string Name, string Path, string Kind, long SizeBytes, DateTimeOffset Modified, bool IsDir, IReadOnlyList<ProjectTreeItem>? Children);

    private static StoredTranscription ReadTranscription(string projectRoot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _) || id.Length != 32) throw new AmpleException("AMP5344", "The transcription id is invalid.");
        string folder = Path.Combine(projectRoot, ".ample", "transcriptions", id);
        string index = Path.Combine(folder, "result.json");
        if (!File.Exists(index) || new FileInfo(index).Length > 16L * 1024 * 1024) throw new AmpleException("AMP5344", "There is no completed audio transcription with that id.");
        StoredTranscription? result;
        try { result = JsonSerializer.Deserialize<StoredTranscription>(File.ReadAllText(index), Web); }
        catch (JsonException) { throw new AmpleException("AMP5344", "The stored audio transcription is invalid."); }
        if (result is null || result.Id != id) throw new AmpleException("AMP5344", "The stored audio transcription is invalid.");
        return result;
    }

    private static object TranscriptionView(StoredTranscription result)
    {
        using JsonDocument source = JsonDocument.Parse(result.ResultJson);
        return new { result.Id, result = source.RootElement.Clone(), stems = new[] { new { role = "source", url = $"/api/audio/transcriptions/{result.Id}/source", contentType = "audio/wav" } }.Concat(result.Files.Select(item => new { role = item.Key, url = $"/api/audio/transcriptions/{result.Id}/{item.Key}", contentType = item.Value.ContentType })) };
    }

    /// <summary>The data of a take that is WAV audio (the role is the output port name, so it is found by content).</summary>
    internal static string? AudioDataId(ProjectSession s, TakeRecord take)
    {
        // The main output first (a sequencer block also carries lane outputs, which are not the mix and may be silent).
        string[] preferred = ["master", "mix", "audio", "out"];
        foreach (TakeDataRef d in take.Data.OrderBy(x => { int i = Array.IndexOf(preferred, x.Role); return i < 0 ? preferred.Length : i; }))
        {
            try
            {
                using Stream st = s.Bank.OpenData(d.DataId);
                byte[] head = new byte[4];
                if (st.Read(head, 0, 4) == 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
                {
                    return d.DataId;
                }
            }
            catch (AmpleException)
            {
                // Missing data: not audio we can show.
            }
        }

        return null;
    }

    /// <summary>The seeds a block has: its lm_seed and seed parameters, or just "seed".</summary>
    private static string[] SeedNames(ProjectSession s, BlockNode block)
    {
        string[] names = s.Spec.Templates.TryGetValue(block.Type, out TemplateSpec? t) ? [.. t.Parameters.Keys.Where(n => n is "lm_seed" or "seed")] : [];
        return names.Length > 0 ? names : ["seed"];
    }

    /// <summary>The <c>*.amplet</c> files of a project folder. The <c>.ample</c> folder (the takebank, with thousands of files) is never entered: listing it only to throw its files away took about a second on a big project.</summary>
    internal static IEnumerable<string> SongFiles(string root)
    {
        Stack<string> pending = new([root]);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(folder, "*.amplet"))
            {
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(folder))
            {
                if (!string.Equals(Path.GetFileName(child), ".ample", StringComparison.Ordinal))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>The waveform summary of a take's audio: read from the small file next to the takebank when it was made before, else computed from the (large) WAV and written there. The audio is content-addressed, so the summary can never be out of date; a file that cannot be read or written only costs the computing.</summary>
    private static object PeaksFromDiskOrCompute(ProjectSession s, string audio, int points)
    {
        string name = new([.. audio.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]);
        string file = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "peaks", $"{name}-{points}.json");
        try
        {
            if (File.Exists(file))
            {
                using JsonDocument cached = JsonDocument.Parse(File.ReadAllText(file));
                return cached.RootElement.Clone();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a damaged or locked summary is simply computed again
        }

        object peaks = Peaks(s.Bank.OpenData(audio), points);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(peaks));
            File.Move(temp, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the summary just is not remembered
        }

        return peaks;
    }

    /// <summary>Computes the waveform summary of 16-bit WAV data in one streaming pass (the file is never held in memory).</summary>
    internal static object Peaks(Stream stream, int points)
    {
        using BinaryReader r = new(stream);
        if (stream.Length < 44 || new string(r.ReadChars(4)) != "RIFF")
        {
            throw new AmpleException("AMP5285", "The data is not a WAV file.");
        }

        r.ReadInt32();
        r.ReadChars(4);
        int format = 0, rate = 0, channels = 1, bits = 16;
        long dataStart = -1, dataLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                if (size < 16 || size > stream.Length - stream.Position) throw new AmpleException("AMP5285", "The WAV format chunk is invalid.");
                format = r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                stream.Position += Math.Max(0, size - 16);
            }
            else if (id == "data")
            {
                dataStart = stream.Position;
                if (size < 0 || size > stream.Length - dataStart) throw new AmpleException("AMP5285", "The WAV data chunk is invalid.");
                dataLength = size;
                break;
            }
            else
            {
                stream.Position += size + (size & 1);
            }
        }

        if (dataStart < 0 || format != 1 || channels is < 1 or > 32 || bits != 16 || rate is < 8000 or > 192000)
        {
            throw new AmpleException("AMP5285", "Only 16-bit WAV data can be shown as a waveform.");
        }

        long frames = dataLength / (2 * channels);
        int n = (int)Math.Clamp(points, 1, Math.Max(1, frames));
        double[] pairs = new double[n * 2];
        stream.Position = dataStart;
        int frameBytes = 2 * channels;
        byte[] buffer = new byte[frameBytes * 8192];
        long perBucket = Math.Max(1, frames / n);
        short min = short.MaxValue, max = short.MinValue;
        int bucket = 0;
        long inBucket = 0;
        double sum = 0;
        double top = 0;
        long done = 0;
        while (done < frames)
        {
            int want = (int)Math.Min(buffer.Length / frameBytes, frames - done) * frameBytes;
            int got = stream.Read(buffer, 0, want);
            if (got < frameBytes)
            {
                break;
            }

            for (int i = 0; i + frameBytes <= got; i += frameBytes)
            {
                short v = (short)(buffer[i] | (buffer[i + 1] << 8));
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                double x = v / 32768.0;
                sum += x * x;
                top = Math.Max(top, Math.Abs(x));
                done++;
                if (++inBucket == perBucket && bucket < n)
                {
                    pairs[bucket * 2] = Math.Round(min / 32768.0, 3);
                    pairs[bucket * 2 + 1] = Math.Round(max / 32768.0, 3);
                    bucket++;
                    inBucket = 0;
                    min = short.MaxValue;
                    max = short.MinValue;
                }
            }
        }

        double rms = done == 0 ? 0 : Math.Sqrt(sum / done);
        return new { rate, channels, seconds = frames / (double)rate, points = n, rms = Math.Round(rms, 5), peak = Math.Round(top, 5), peaks = pairs };
    }

    private static T CachedProjectPreview<T>(string key, Func<T> create) where T : notnull
    {
        if (ProjectPreviewCache.TryGetValue(key, out object? saved)) return (T)saved;
        T value = create();
        if (ProjectPreviewCache.TryAdd(key, value))
        {
            ProjectPreviewCacheOrder.Enqueue(key);
            while (ProjectPreviewCache.Count > 64 && ProjectPreviewCacheOrder.TryDequeue(out string? oldest)) ProjectPreviewCache.TryRemove(oldest, out _);
            return value;
        }
        return (T)ProjectPreviewCache[key];
    }

    private static byte[] ReadProjectPreviewBytes(Stream stream, int maxBytes)
    {
        using (stream)
        {
            if (stream.Length > maxBytes) throw new AmpleException("AMP5285", "This file is too large for a project preview.");
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new AmpleException("AMP5285", "This file is too large for a project preview.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    private static object MidiProjectPreview(byte[] bytes)
    {
        SmfFile midi;
        try { midi = SmfReader.Parse(bytes, SmfLimits.Standard); }
        catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
        List<object> notes = [];
        long totalNotes = 0;
        foreach ((SmfTrack track, int index) in midi.Tracks.Select((track, index) => (track, index)))
        {
            IReadOnlyList<MidiNoteSpan> paired = MidiNotePairer.Pair(track, index).Notes;
            totalNotes += paired.Count;
            foreach (MidiNoteSpan note in paired.Take(Math.Max(0, 512 - notes.Count))) notes.Add(new { track = index, start = note.StartTick, end = note.EndTick, pitch = note.Pitch, velocity = note.Velocity });
        }
        long endTick = midi.Tracks.SelectMany(track => track.Events).Select(item => item.Tick).DefaultIfEmpty(0).Max();
        long barTicks = 4L * midi.TicksPerQuarter;
        return new { kind = "midi", midi.TicksPerQuarter, tracks = midi.Tracks.Count, bars = Math.Max(1, (endTick + barTicks - 1) / barTicks), noteCount = totalNotes, truncated = totalNotes > notes.Count, notes };
    }

    /// <summary>A project path for a working copy: relative, inside the project, never in the private .ample folder.</summary>
    private static string WorkingPath(string path)
    {
        string clean = path.Replace('\\', '/').Trim('/');
        if (clean.Length == 0 || clean.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase) || part == "..")) throw new AmpleException("AMP5285", "That path cannot have a working copy.");
        return clean;
    }

    /// <summary>The note that goes with a build error when files with unsaved changes were used: the error may be in them (plan/editor-tabs.md, decision 1).</summary>
    private static string UnsavedNote(ProjectSession s) => s.Texts.Dirty() is { Count: > 0 } dirty ? $" (Built with the unsaved changes of {string.Join(", ", dirty)}, so the error may be in there.)" : string.Empty;

    private static object Diags(AmpleDocument d) => d.Diagnostics.Select(x => new { x.Code, severity = x.Severity.ToString().ToLowerInvariant(), x.Line, x.Col, x.EndCol, x.Message, x.Hint, breaksGraph = DiagnosticKinds.BreaksGraph(x.Code), block = DiagnosticKinds.BlockOf(d, x) });

    private static object DocView(DocSession d, AmpleDocument parsed, ProjectSession session, IReadOnlyList<string>? unsupported = null) => new { d.Path, d.Version, d.Text, dirty = d.Text != d.Saved, diagnostics = Diags(parsed), graphBroken = DiagnosticKinds.GraphBroken(parsed), blocks = parsed.Blocks.Where(b => !b.Skipped).Select(b => new { b.Name, b.Type, b.Class, b.Version, b.Line, flags = b.Flags, b.Freeze, parts = b.Parts, parameters = b.Parameters.Select(p => new { p.Name, p.Value, p.Line }), froms = b.Froms.Select(f => new { f.Block, f.Output, f.Input, f.Disabled }), controlRefs = b.ControlRefs.Select(c => new { c.Block, c.Output, c.Parameter }), layout = parsed.Layout.TryGetValue(b.Name, out (int X, int Y) pos) ? new { x = pos.X, y = pos.Y } : null }), uses = ModuleUsesView(parsed, session), project = parsed.Project is null ? null : new { parsed.Project.Name, parsed.Project.Tempo, parsed.Project.MasterSeed, parsed.Project.MeterNumerator, parsed.Project.MeterDenominator, parsed.Project.BeatsPerBar }, settings = parsed.Settings, helpers = parsed.Helpers.Select(h => new { h.Name, h.Kind, h.Dynamic, h.Baked, h.Effect, h.Returns, h.Line, parameters = h.Params, dependencies = h.Dependencies }), properties = parsed.Properties, stability = parsed.Stability, stabilityNotes = parsed.StabilityNotes, capabilities = parsed.Capabilities, clockAt = (session.LastBuildAt.TryGetValue(d.Path, out DateTime builtClock) ? builtClock : parsed.ParsedAt).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), builtClock = session.LastBuildAt.ContainsKey(d.Path), bakeableClock = parsed.Stability == "dynamic" && Edits.BakeDynamicHelpers(parsed, parsed.ParsedAt).Ok, environmentAllow = parsed.CapabilityPolicy.AllowLists("environment"), limits = parsed.Limits.Select(l => new { l.Key, l.Effective, l.Source, l.Cap }), unsupported = unsupported ?? [] };

    private static object ModuleDocView(ModuleDocState state, ProjectSession session) => DocView(state.Document, session.ParseModule(state.Document), session, Ample.Audio.Midi.MidiModuleFacts.FromSource(state.Source).UnsupportedFeatures);

    private static SmfFile SelectTrackWithGlobalMeta(SmfFile source, int trackIndex)
    {
        List<SmfEvent> events = [.. source.Tracks[trackIndex].Events.Where(e => !(e.Kind == SmfEventKind.Meta && e.MetaType == 0x2F))];
        foreach (SmfEvent item in source.Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.Meta && (e.MetaType is 0x51 or 0x58 or 0x59)))
        {
            if (events.Any(e => e.Tick == item.Tick && e.MetaType == item.MetaType && e.Payload.AsSpan().SequenceEqual(item.Payload))) continue;
            events.Add(item);
        }
        long endTick = events.Select(e => e.Tick).DefaultIfEmpty(0).Max();
        events.Add(new SmfEvent(endTick, SmfEventKind.Meta, 0, 0, 0, 0x2F, []));
        return source with { Tracks = [new SmfTrack([.. events.OrderBy(e => e.Tick)])] };
    }

    private static object[] ModuleUsesView(AmpleDocument parsed, ProjectSession session)
    {
        using Ample.Engine.Modules.ModuleService modules = new(session.Root, session.Library, session.Manifest.Id.ToString("D"));
        return [.. parsed.Uses.Select(u =>
        {
            Ample.Language.ModuleSource? source = modules.Find(u.Module);
            Ample.Language.ModuleDefinition? definition = source is null ? null : Ample.Language.ModuleDefinition.Parse(source.Text);
            AmpleDocument? body = definition is null || definition.Problems.Count > 0 ? null : Parser.Parse(definition.Body, session.Spec);
            object[] inputs = definition is null || body is null ? [] : [.. definition.Inputs.Select(name =>
            {
                string type = body.Blocks.SelectMany(b => b.Froms.Select(f => (Block: b, From: f))).Where(x => x.From.Block == "in" && x.From.Output == name).Select(x => session.Spec.Templates.GetValueOrDefault(x.Block.Type)?.Inputs.FirstOrDefault(i => i.Name == x.From.Input)?.Type).FirstOrDefault(t => t is not null) ?? "data";
                return new { name, type };
            })];
            object[] outputs = definition is null || body is null ? [] : [.. definition.Outputs.Select(o =>
            {
                string type = body.Blocks.FirstOrDefault(b => b.Name == o.Block) is { } block ? session.Spec.Templates.GetValueOrDefault(block.Type)?.Outputs.FirstOrDefault(p => p.Name == o.Port)?.Type ?? "data" : "data";
                return new { name = o.Name, type };
            })];
            return new { u.Module, u.Version, u.Instance, u.Line, isMidi = source?.Text.Contains("# midi-source version=1", StringComparison.Ordinal) ?? false, inputs, outputs, parameters = u.Parameters.Select(p => new { p.Name, p.Value }), froms = u.Froms.Select(f => new { f.Block, f.Output, f.Input, f.Disabled }) };
        })];
    }

    /// <summary>
    /// Opens a song for a request that works from its text (a build) and first brings the working copy in line with its file on disk, like opening it in the page does:
    /// a copy without unsaved changes follows the file (and the page is told through doc.changed), a copy with changes is merged when both sides changed different places;
    /// a real conflict or a deleted file leaves the working copy as it is. Without this a song changed by a script, git or an agent was built as it was when it was first opened.
    /// </summary>
    private static async Task<DocSession> OpenFreshAsync(ProjectSession s, EventBus events, string path, CancellationToken cancellationToken)
    {
        DocSession d = await s.DocAsync(path, cancellationToken).ConfigureAwait(false);
        ProjectSession.DiskSync sync = await s.SyncDiskAsync(d, cancellationToken).ConfigureAwait(false);
        if (sync.State is "reloaded" or "merged")
        {
            events.Publish("doc.changed", new { d.Path, d.Version });
        }

        return d;
    }

    private static void Declare(Action<string, string, string, string, bool, Delegate?> add, long maxProjectUploadBytes, WebApplication? app = null, ProjectSession? s = null, EventBus? events = null)
    {
        // --- project and health ---
        add("GET", "/api/ping", "health", "Minimal unauthenticated instance liveness check.", false, s is null ? null : () => Ok(new { ok = true, id = s.Manifest.Id.ToString("D") }));
        add("GET", "/api/about", "health", "About this AMPLE: version, build time, runtime and when this server started (the About dialog shows it).", false, s is null ? null : () => Ok(new { name = "AMPLE", version = typeof(Routes).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0", builtAt = File.GetLastWriteTimeUtc(File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(Routes).Assembly.GetName().Name + ".dll")) ? Path.Combine(AppContext.BaseDirectory, typeof(Routes).Assembly.GetName().Name + ".dll") : Environment.ProcessPath ?? string.Empty), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, spec = s.Spec.Version, startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime() }));
        add("GET", "/api/project", "project", "The open project: id, name, folder, master seed and last Studio song.", false, s is null ? null : () => Ok(new { s.Manifest.Id, s.Manifest.Name, folder = s.Root, masterSeed = s.Manifest.MasterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture), format = s.Manifest.FormatVersion, readOnly = false, firstRunPending = ReadManifestSync(s).Settings.FirstRun.Pending, lastSong = RecentProjects.List(s.Library.Home).FirstOrDefault(project => string.Equals(project.Path, Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase))?.LastSong }));
        add("POST", "/api/project/last-song", "project", "Remembers the last Studio song in the user's recent-project record.", true, s is null ? null : async (HttpContext c) =>
        {
            string path = Str(await Body(c), "path").Replace('\\', '/');
            if (!path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5243", "Choose a song in this project.");
            await s.DocAsync(path, c.RequestAborted).ConfigureAwait(false);
            RecentProjects.RememberSong(s.Library.Home, s.Root, s.Manifest.Name, s.Manifest.Id.ToString("D"), path, s.Clock);
            return Ok(new { path });
        });
        add("GET", "/api/health", "health", "Project health: doctor findings, AMPLE Redux check, interrupted commands, open regeneration decisions.", false, s is null ? null : async (HttpContext c) =>
        {
            IReadOnlyList<DoctorFinding> findings = await ProjectDoctor.CheckAsync(s.Fs, s.Clock, s.Root, c.RequestAborted);
            VerifyReport report = await s.Journal.VerifyAsync(c.RequestAborted);
            IReadOnlyList<InterruptedCommand> interrupted = await s.Journal.FindInterruptedAsync(c.RequestAborted);
            return Ok(new { backup = Ample.Storage.Backup.BackupPolicy.Read(s.Root, s.Clock), findings, blackBox = new { report.Records, report.BadSeq, report.TornTail }, interrupted = interrupted.Select(i => new { i.Start.Seq, i.Start.Kind, i.Start.PayloadJson }), pendingRegens = s.Control.PendingRegens().Select(p => new { p.Id, p.Block, p.OldTake, p.NewTake, p.Differences }) });
        });
        add("GET", "/api/redux/status", "redux", "Aggregate AMPLE Redux storage use and budget warning thresholds; no record contents are returned.", false, s is null ? null : async (HttpContext c) =>
        {
            ProjectManifest manifest = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            long used = await JournalRetention.StorageBytesAsync(s.Context.Fs, s.Root, c.RequestAborted);
            long budget = manifest.Settings.BlackBox.BudgetBytes;
            return Ok(new { usedBytes = used, warnAtBytes = budget / 5, budgetBytes = budget, state = used >= budget ? "limit" : used >= budget / 5 ? "warning" : "ok" });
        });
        add("GET", "/api/redux/compact-preview", "redux", "Previews the exact records and bytes an explicit AMPLE Redux compaction would affect.", false, s is null ? null : async (HttpContext c) =>
        {
            ProjectManifest manifest = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            return Ok(await s.Journal.PreviewCompactionAsync(manifest.Settings.BlackBox, c.RequestAborted));
        });
        add("POST", "/api/redux/compact", "redux", "Explicitly compacts AMPLE Redux after the caller has reviewed the impact preview.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = Obj(await Body(c));
            ProjectManifest manifest = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            CompactionResult result = await s.Journal.CompactAsync(manifest.Settings.BlackBox, b.GetProperty("expectedSequence").GetInt64(), c.RequestAborted);
            events!.Publish("redux.changed", new { kind = "compact" });
            return Ok(result);
        });
        add("GET", "/api/blackbox/status", "health", "AMPLE Redux journal check and the last records (legacy route name retained for compatibility).", false, s is null ? null : async (HttpContext c) =>
        {
            VerifyReport report = await s.Journal.VerifyAsync(c.RequestAborted);
            IReadOnlyList<JournalRecord> all = await s.Journal.ReadAllAsync(c.RequestAborted);
            return Ok(new { report.Records, report.BadSeq, report.TornTail, last = all.TakeLast(20).Select(r => new { r.Seq, r.ArId, r.CompactArId, r.At, r.Actor, r.Phase, r.Kind }) });
        });
        add("GET", "/api/redux/records", "redux", "Read-only, bounded search through AMPLE Redux records. Cursor pages stay pinned to the first page's high-water sequence.", false, s is null ? null : async (HttpContext c) => Ok(await ReduxSearchView(s, c, ReduxQuery(c, "project"), false).ConfigureAwait(false)));
        add("GET", "/api/redux/edit-history", "redux", "Read-only exact-hash lookup of saved song or module states in AMPLE Redux, without exposing journal contents.", false, s is null ? null : async (HttpContext c) =>
        {
            string path = c.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal) || path.Replace('\\', '/').StartsWith(".ample", StringComparison.Ordinal) || !(path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".module", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Edit history is available for project song and module files only.");
            HashSet<string> wanted = new(StringComparer.Ordinal);
            foreach (string? value in c.Request.Query["hash"])
            {
                string hash = value ?? throw new AmpleException("AMP5285", "Each edit-history hash must be a SHA-256 value.");
                if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))) throw new AmpleException("AMP5285", "Each edit-history hash must be a SHA-256 value.");
                wanted.Add(hash.ToLowerInvariant());
            }

            if (wanted.Count is < 1 or > 100) throw new AmpleException("AMP5285", "Request between 1 and 100 edit-history hashes at a time.");
            Dictionary<string, (string Ar, DateTimeOffset At)> matches = new(StringComparer.Ordinal);
            long through = s.Journal.Head.Seq, before = through + 1;
            while (wanted.Count > 0)
            {
                IReadOnlyList<JournalRecord> batch = await s.Journal.ReadPageAsync(through, before, record => record.Kind == "file.write" && record.Phase == "start" && ReduxRecordPath(record) == path, 200, c.RequestAborted).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (JournalRecord record in batch)
                {
                    foreach (string hash in ReduxSongHashes(record, path))
                    {
                        if (wanted.Remove(hash)) matches[hash] = (record.CompactArId, record.At);
                    }
                }

                before = batch[^1].Seq;
                if (batch.Count < 200) break;
            }

            return Ok(new { records = matches.Select(match => new { ar = match.Value.Ar, at = match.Value.At, hash = match.Key }) });
        });
        add("GET", "/api/ai/context/redux-search", "ai", "For an AI task: read-only search of the task's AMPLE Redux archive; scope=project expands to this project's journal.", false, s is null ? null : async (HttpContext c) => Ok(await ReduxSearchView(s, c, ReduxQuery(c, "task"), true).ConfigureAwait(false)));
        add("GET", "/api/plugins", "plugins", "The plugins that are found and any problems with them.", false, s is null ? null : () =>
        {
            (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> plugins, IReadOnlyList<string> problems) = Ample.Plugins.PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins"), Path.Combine(s.Root, "plugins"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
            return Ok(new { plugins = plugins.Select(p => new { p.Manifest.Id, p.Manifest.Title, p.Manifest.Version, p.Manifest.Kind, p.Manifest.Determinism, p.Manifest.Blocks, analyses = p.Manifest.Analyses, p.Manifest.Needs, settingsSchema = JsonDocument.Parse(p.Manifest.SettingsSchemaJson).RootElement }), problems });
        });
        add("GET", "/api/packs/files", "project", "The WAV files of the installed audio packs as pack:<pack>/<name> values for a file field.", false, s is null ? null : () =>
        {
            SortedSet<string> found = new(StringComparer.Ordinal);
            foreach (string folder in Ample.Audio.IO.AmpleHome.PackFolders(Ample.Audio.IO.AmpleHome.DefaultPath(), s.AmpleRoot, s.Root))
            {
                foreach (string tier in (string[])["free", "licensed"])
                {
                    string tierFolder = Path.Combine(folder, tier);
                    if (!Directory.Exists(tierFolder)) continue;
                    foreach (string file in Directory.EnumerateFiles(tierFolder, "*.wav", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                    {
                        string relative = Path.GetRelativePath(tierFolder, file).Replace(Path.DirectorySeparatorChar, '/');
                        if (relative.Contains('/', StringComparison.Ordinal) && found.Count < 500) found.Add("pack:" + relative[..^4]);
                    }
                }
            }

            return Ok(new { files = found.ToArray() });
        });
        add("GET", "/api/project/files", "project", "The files of the project (without .ample), for the file tree.", false, s is null ? null : () => Ok(Directory.EnumerateFiles(s.Root, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(s.Root, f).StartsWith(".ample", StringComparison.Ordinal)).Select(f => new { path = Path.GetRelativePath(s.Root, f).Replace('\\', '/'), size = new FileInfo(f).Length }).OrderBy(f => f.path, StringComparer.Ordinal)));
        add("GET", "/api/project/export", "project", "Downloads an original project file without converting it.", false, s is null ? null : (HttpContext c) =>
        {
            string file = ProjectExports.Source(s.Root, c.Request.Query["path"].ToString());
            if (!File.Exists(file)) throw new AmpleException("AMP5243", "Select a file; folders export as ZIP.");
            return Results.File(file, "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: true);
        });
        add("POST", "/api/project/export", "project", "Starts a cancellable audio conversion or folder ZIP export in temporary storage.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            return Ok(ProjectExports.Start(s.Root, Str(body, "path"), Str(body, "format")));
        });
        add("GET", "/api/project/export/status", "project", "Progress of a temporary export job.", false, s is null ? null : (HttpContext c) => Ok(ProjectExports.Status(s.Root, c.Request.Query["id"].ToString())));
        add("POST", "/api/project/export/cancel", "project", "Cancels an export job without changing the source files.", true, s is null ? null : async (HttpContext c) => Ok(ProjectExports.Cancel(s.Root, Str(await Body(c), "id"))));
        add("GET", "/api/project/export/download", "project", "Downloads a completed temporary export (available for 30 minutes).", false, s is null ? null : (HttpContext c) => ProjectExports.Download(s.Root, c.Request.Query["id"].ToString()));
        add("GET", "/api/project/audio", "project", "Streams a project audio file with range support for the shared player.", false, s is null ? null : (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            string contentType = extension switch { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".aif" or ".aiff" => "audio/aiff", _ => string.Empty };
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || contentType.Length == 0 || !s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5243", "The project audio file does not exist or cannot be played.");
            return Results.File(s.Fs.OpenRead(full), contentType, enableRangeProcessing: true);
        });
        add("GET", "/api/project/tree", "project", "Lists one project folder and its immediate contents without following links or entering .ample; names that start with a dot (such as .git) are left out unless hidden=1.", false, s is null ? null : (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString();
            int depth = int.TryParse(c.Request.Query["depth"], out int requested) ? Math.Clamp(requested, 0, 8) : 0;
            string full = relative.Length == 0 ? s.Root : ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (!Directory.Exists(full) || Path.GetRelativePath(s.Root, full).Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5243", "The project folder does not exist.");
            return Ok(new { path = relative.Replace('\\', '/'), children = ProjectTree(s, full, depth, c.Request.Query["hidden"].ToString() == "1") });
        });
        add("GET", "/api/project/hash", "project", "Returns the SHA-256 digest of one project file without entering .ample or following links.", false, s is null ? null : async (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || !s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5243", "The project file does not exist or cannot be read.");
            await using Stream stream = s.Fs.OpenRead(full);
            byte[] digest = await System.Security.Cryptography.SHA256.HashDataAsync(stream, c.RequestAborted).ConfigureAwait(false);
            return Ok(new { path = relative, sha256 = Convert.ToHexStringLower(digest) });
        });
        add("GET", "/api/project/peaks", "project", "Returns a bounded, cached waveform summary for a project audio file (WAV as it is; mp3, flac, ogg and aiff are decoded by ffmpeg first).", false, s is null ? null : async (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            FileInfo info = new(full);
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || !s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative) || Path.GetExtension(relative).ToLowerInvariant() is not (".wav" or ".mp3" or ".flac" or ".ogg" or ".aif" or ".aiff")) throw new AmpleException("AMP5285", "The file cannot be previewed.");
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            if (info.Length > (extension == ".wav" ? 128L : 64L) * 1024 * 1024) throw new AmpleException("AMP5285", "This audio file is too large for a preview.");
            // the latest known summary of this file is kept on disk (one per file: a changed file replaces it); a compressed file is decoded once with ffmpeg
            return Ok(await ProjectPreviewStore.For(s.Root).GetAsync(relative, full, "peaks", async () =>
            {
                if (extension == ".wav") return Peaks(s.Fs.OpenRead(full), 160);
                byte[] decoded = await DecodeToWavAsync(await File.ReadAllBytesAsync(full, c.RequestAborted).ConfigureAwait(false), extension, c.RequestAborted).ConfigureAwait(false);
                return Peaks(new MemoryStream(decoded), 160);
            }).ConfigureAwait(false));
        });
        add("GET", "/api/project/preview", "project", "Returns bounded cached song, lyric or MIDI metadata for project-file thumbnails.", false, s is null ? null : (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            FileInfo info = new(full);
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || !s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5285", "The file cannot be previewed.");
            if (info.Length > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5285", "This file is too large for a project preview.");
            JsonNode preview = ProjectPreviewStore.For(s.Root).Get(relative, full, "preview", () =>
            {
                if (extension is ".mid" or ".midi") return MidiProjectPreview(ReadProjectPreviewBytes(s.Fs.OpenRead(full), SmfLimits.Standard.MaxFileBytes));
                if (extension == ".amplet")
                {
                    string text = System.Text.Encoding.UTF8.GetString(ReadProjectPreviewBytes(s.Fs.OpenRead(full), 1024 * 1024));
                    AmpleDocument song = Parser.Parse(text, s.Spec);
                    return new { kind = "song", tempo = song.Project?.Tempo ?? 0, blocks = song.Blocks.Count(block => !block.Skipped) };
                }
                if (extension == ".module") return new { kind = "module" };
                if (extension == ".amplec") return new { kind = "component" };
                if (extension is ".lyr" or ".lrc" or ".srt" or ".txt")
                {
                    string text = System.Text.Encoding.UTF8.GetString(ReadProjectPreviewBytes(s.Fs.OpenRead(full), 256 * 1024));
                    string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Take(5).Select(line => line.Length > 96 ? line[..96] : line).ToArray();
                    return new { kind = "lyrics", lines };
                }
                return new { kind = "file", extension };
            });
            return Ok(preview);
        });
        add("GET", "/api/project/usage", "project", "Finds song and module parameters that reference a project file or a file under a project folder.", false, s is null ? null : async (HttpContext c) =>
        {
            string requested = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            string full = ProjectPath.Resolve(s.Fs, s.Root, requested);
            if (requested.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || (!s.Fs.FileExists(full) && !s.Fs.DirectoryExists(full))) throw new AmpleException("AMP5243", "The project file or folder does not exist.");
            bool isDirectory = s.Fs.DirectoryExists(full);
            string prefix = isDirectory ? requested + "/" : requested;
            List<object> users = [];
            foreach (string source in ProjectSourceFiles(s, s.Root))
            {
                string extension = Path.GetExtension(source);
                if (!extension.Equals(".amplet", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".module", StringComparison.OrdinalIgnoreCase)) continue;
                string relative = Path.GetRelativePath(s.Root, source).Replace('\\', '/');
                AmpleDocument document;
                if (extension.Equals(".module", StringComparison.OrdinalIgnoreCase)) document = s.ParseModule((await s.ModuleDocAsync(relative, c.RequestAborted).ConfigureAwait(false)).Document);
                else document = s.Parse(await s.DocAsync(relative, c.RequestAborted).ConfigureAwait(false));
                foreach (BlockNode block in document.Blocks.Where(block => !block.Skipped))
                    foreach (Parameter parameter in block.Parameters)
                    {
                        string value = parameter.Value.Replace('\\', '/').Trim();
                        bool matches = isDirectory ? value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : string.Equals(value, requested, StringComparison.OrdinalIgnoreCase);
                        if (matches) users.Add(new { path = relative, block = block.Name, type = block.Type, block.Line, parameter = parameter.Name });
                    }
            }

            return Ok(new { path = requested, users });
        });
        add("POST", "/api/project/upload", "project", "Imports a project file up to the configured byte limit through the command bus with explicit conflict handling.", true, s is null ? null : async (HttpContext c) =>
        {
            long maxBytes = maxProjectUploadBytes;
            string maxSize = maxBytes % (1024 * 1024) == 0 ? (maxBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MiB" : maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes";
            bool multipart = c.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;
            IHttpMaxRequestBodySizeFeature? requestLimit = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (requestLimit is { IsReadOnly: false }) requestLimit.MaxRequestBodySize = maxBytes + (multipart ? 64 * 1024 : 0);
            string name = c.Request.Query["name"].ToString();
            string directory = c.Request.Query["dir"].ToString().Replace('\\', '/').Trim('/');
            string conflict = c.Request.Query["onConflict"].ToString();
            byte[] bytes;
            if (multipart)
            {
                if (c.Request.ContentLength > maxBytes + 64 * 1024) throw new AmpleException("AMP5285", "The uploaded file exceeds the " + maxSize + " limit.");
                c.Features.Set<IFormFeature>(new FormFeature(c.Request, new FormOptions { MultipartBodyLengthLimit = maxBytes + 64 * 1024, ValueCountLimit = 4, KeyLengthLimit = 128, ValueLengthLimit = 256 }));
                IFormCollection form = await c.Request.ReadFormAsync(c.RequestAborted).ConfigureAwait(false);
                if (form.Files.Count != 1) throw new AmpleException("AMP5285", "Upload exactly one project file at a time.");
                IFormFile file = form.Files[0];
                if (name.Length == 0) name = form["name"].ToString();
                if (name.Length == 0) name = file.FileName.Replace('\\', '/').Split('/').Last();
                if (directory.Length == 0) directory = form["dir"].ToString().Replace('\\', '/').Trim('/');
                if (conflict.Length == 0) conflict = form["onConflict"].ToString();
                if (file.Length > maxBytes) throw new AmpleException("AMP5285", "The uploaded file exceeds the " + maxSize + " limit.");
                await using MemoryStream buffer = new();
                await file.CopyToAsync(buffer, c.RequestAborted).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }
            else
            {
                if (c.Request.ContentLength > maxBytes) throw new AmpleException("AMP5285", "The uploaded file exceeds the " + maxSize + " limit.");
                await using MemoryStream buffer = new();
                byte[] readBuffer = new byte[64 * 1024];
                int read;
                while ((read = await c.Request.Body.ReadAsync(readBuffer, c.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes) throw new AmpleException("AMP5285", "The uploaded file exceeds the " + maxSize + " limit.");
                    await buffer.WriteAsync(readBuffer.AsMemory(0, read), c.RequestAborted).ConfigureAwait(false);
                }

                bytes = buffer.ToArray();
            }

            if (conflict.Length == 0) conflict = "refuse";
            if (conflict is not ("refuse" or "overwrite" or "rename" or "overwrite-record") || name.Length is 0 or > 128 || name != Path.GetFileName(name) || name is "." or "..") throw new AmpleException("AMP5285", "The upload name or conflict mode is invalid.");
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (!UploadExtensions.Contains(extension, StringComparer.Ordinal)) throw new AmpleException("AMP5285", "This file extension is not supported by project upload.");
            string relative = (directory.Length == 0 ? name : directory + "/" + name);
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Files cannot be uploaded into the private project folder.");
            if (HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5243", "Project files inside links cannot be changed.");
            string parent = Path.GetDirectoryName(full)!;
            SemaphoreSlim gate = UploadGates.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(c.RequestAborted).ConfigureAwait(false);
            try
            {
                string? existing = s.Fs.DirectoryExists(parent) ? s.Fs.GetFiles(parent).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) : null;
                if (existing is not null && conflict == "refuse") return Results.Conflict(new { sizeBytes = new FileInfo(existing).Length, modified = File.GetLastWriteTimeUtc(existing) });
                if (existing is not null && conflict == "rename")
                {
                    string stem = Path.GetFileNameWithoutExtension(name) + "-" + s.Clock.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    string suffix = Path.GetExtension(name);
                    string candidate = stem + suffix;
                    for (int number = 2; s.Fs.FileExists(Path.Combine(parent, candidate)); number++) candidate = stem + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
                    relative = (directory.Length == 0 ? candidate : directory + "/" + candidate);
                    full = ProjectPath.Resolve(s.Fs, s.Root, relative);
                }
                else if (existing is not null) { relative = Path.GetRelativePath(s.Root, existing).Replace('\\', '/'); full = existing; }
                if (existing is not null && conflict == "overwrite-record") await s.History.CheckpointAsync("replaced " + relative, c.RequestAborted).ConfigureAwait(false);
                await s.Bus.ExecuteAsync(new ImportFileCommand(relative, bytes), c.RequestAborted).ConfigureAwait(false);
                string? converted = null, convertError = null;
                if (CompressedAudioExtensions.Contains(extension, StringComparer.Ordinal) && c.Request.Query["toWav"].ToString() != "0")
                {
                    // Blocks play WAV files, so a compressed file also gets a WAV next to it (the original stays).
                    try
                    {
                        byte[] wav = await DecodeToWavAsync(bytes, extension, c.RequestAborted).ConfigureAwait(false);
                        string folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                        string stem = Path.GetFileNameWithoutExtension(relative);
                        string wavRelative = (folder.Length == 0 ? "" : folder + "/") + stem + ".wav";
                        for (int number = 2; s.Fs.FileExists(ProjectPath.Resolve(s.Fs, s.Root, wavRelative)); number++) wavRelative = (folder.Length == 0 ? "" : folder + "/") + stem + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wav";
                        await s.Bus.ExecuteAsync(new ImportFileCommand(wavRelative, wav), c.RequestAborted).ConfigureAwait(false);
                        converted = wavRelative;
                    }
                    catch (AmpleException ex) { convertError = ex.Message; }
                }

                events!.Publish("files.changed", new { paths = converted is null ? new[] { relative } : new[] { relative, converted } });
                return Ok(new { path = relative, sizeBytes = bytes.Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), converted, convertError });
            }
            finally { gate.Release(); }
        });
        add("DELETE", "/api/project/file", "project", "Deletes one project file through the command bus with a recoverable pre-image.", true, s is null ? null : async (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Files cannot be deleted from the private project folder.");
            if (Ample.Storage.Projects.DemoSongRules.IsDemo(relative) && Ample.Storage.Projects.DemoSongRules.IsProtected(s.Root)) throw new AmpleException("AMP5285", Ample.Storage.Projects.DemoSongRules.Refusal);
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (!s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5243", "The project file does not exist or cannot be changed.");
            await s.Bus.ExecuteAsync(new DeleteFileCommand(relative), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { relative } });
            return Ok(new { path = relative, deleted = true });
        });
        add("DELETE", "/api/project/folder", "project", "Deletes one empty project folder through the command bus with a recoverable pre-image.", true, s is null ? null : async (HttpContext c) =>
        {
            string relative = c.Request.Query["path"].ToString().Replace('\\', '/').Trim('/');
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Private project folders cannot be deleted.");
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (!s.Fs.DirectoryExists(full) || HasProjectLink(s.Fs, s.Root, relative) || s.Fs.GetFiles(full).Count != 0 || s.Fs.GetDirectories(full).Count != 0) throw new AmpleException("AMP5243", "Only an empty project folder can be deleted.");
            await s.Bus.ExecuteAsync(new DeleteFolderCommand(relative), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { relative } });
            return Ok(new { path = relative, deleted = true });
        });
        add("POST", "/api/project/folder", "project", "Creates a project folder through the command bus.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string relative = Str(body, "path").Replace('\\', '/').Trim('/');
            if (relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(relative).Length is 0 or > 128) throw new AmpleException("AMP5285", "The folder path is invalid or private.");
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            string parent = Path.GetDirectoryName(full)!;
            if (!s.Fs.DirectoryExists(parent) || HasProjectLink(s.Fs, s.Root, relative) || s.Fs.DirectoryExists(full) || s.Fs.FileExists(full)) throw new AmpleException("AMP5243", "The parent folder must exist and the destination must be unused.");
            await s.Bus.ExecuteAsync(new CreateFolderCommand(relative), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { relative } });
            return Ok(new { path = relative, created = true });
        });
        add("POST", "/api/project/convert-audio", "project", "Decodes an MP3, Ogg or FLAC file of the project to a WAV next to it (ffmpeg, 16-bit PCM, same sample rate) so blocks can play it; an existing WAV of the same name is reused.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string relative = Str(body, "path").Replace('\\', '/').Trim('/');
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            if (!CompressedAudioExtensions.Contains(extension, StringComparer.Ordinal) || relative.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Only an MP3, Ogg or FLAC file of the project can be converted.");
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (!s.Fs.FileExists(full) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5243", "The audio file does not exist or cannot be used.");
            string wavRelative = relative[..^extension.Length] + ".wav";
            if (s.Fs.FileExists(ProjectPath.Resolve(s.Fs, s.Root, wavRelative))) return Ok(new { path = wavRelative, created = false });
            byte[] wav = await DecodeToWavAsync(await File.ReadAllBytesAsync(full, c.RequestAborted).ConfigureAwait(false), extension, c.RequestAborted).ConfigureAwait(false);
            await s.Bus.ExecuteAsync(new ImportFileCommand(wavRelative, wav), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { wavRelative } });
            return Ok(new { path = wavRelative, created = true });
        });
        add("POST", "/api/project/copy", "project", "Copies a project file or folder (with everything in it) to a new path through the command bus; the destination must not exist yet.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string source = Str(body, "source").Replace('\\', '/').Trim('/');
            string destination = Str(body, "destination").Replace('\\', '/').Trim('/');
            bool Private(string path) => path.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase));
            if (source.Length == 0 || destination.Length == 0 || Private(source) || Private(destination) || Path.GetFileName(destination).Length is 0 or > 128) throw new AmpleException("AMP5285", "The source or the destination of the copy is not valid.");
            string from = ProjectPath.Resolve(s.Fs, s.Root, source), to = ProjectPath.Resolve(s.Fs, s.Root, destination);
            bool isFile = s.Fs.FileExists(from), isFolder = s.Fs.DirectoryExists(from);
            if ((!isFile && !isFolder) || HasProjectLink(s.Fs, s.Root, source) || HasProjectLink(s.Fs, s.Root, destination)) throw new AmpleException("AMP5243", "The source does not exist or cannot be copied.");
            if (s.Fs.FileExists(to) || s.Fs.DirectoryExists(to)) throw new AmpleException("AMP5243", $"There is already something called {destination}.");
            if (isFolder && (destination + "/").StartsWith(source + "/", StringComparison.Ordinal)) throw new AmpleException("AMP5285", "A folder cannot be copied into itself.");
            if (!s.Fs.DirectoryExists(Path.GetDirectoryName(to)!)) throw new AmpleException("AMP5243", "The folder of the destination does not exist.");
            async Task CopyAsync(string fromPath, string toRelative)
            {
                if (s.Fs.DirectoryExists(fromPath))
                {
                    await s.Bus.ExecuteAsync(new CreateFolderCommand(toRelative), c.RequestAborted).ConfigureAwait(false);
                    foreach (string entry in s.Fs.GetDirectories(fromPath).Concat(s.Fs.GetFiles(fromPath)))
                    {
                        string name = Path.GetFileName(entry);
                        if (name.Equals(".ample", StringComparison.OrdinalIgnoreCase) || s.Fs.GetLinkTarget(entry) is not null) continue;
                        await CopyAsync(entry, toRelative + "/" + name).ConfigureAwait(false);
                    }

                    return;
                }

                await s.Bus.ExecuteAsync(new ImportFileCommand(toRelative, await File.ReadAllBytesAsync(fromPath, c.RequestAborted).ConfigureAwait(false)), c.RequestAborted).ConfigureAwait(false);
            }

            await CopyAsync(from, destination).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { destination } });
            return Ok(new { path = destination });
        });
        add("GET", "/api/project/kinds", "project", "The kinds of file and folder AMPLE can create in a project (the Add menu of the project panel).", false, s is null ? null : () => Ok(NewFileKinds.All.Select(k => new { k.Id, k.Label, k.Folder, k.Extension, k.IsFolder, k.Open })));
        add("POST", "/api/project/new", "project", "Creates a file or folder of a kind (song, lyrics, chords, MIDI clip, module, sample folder, note, folder) with a valid skeleton through the command bus; the name is checked and an existing path is refused.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string kindId = Str(body, "kind");
            string name = Str(body, "name");
            string? folder = Opt(body, "folder");
            if (kindId == "song" && string.IsNullOrWhiteSpace(folder))
            {
                IResult song = await CreateSongAsync(s, name.Replace(' ', '-'), c.RequestAborted).ConfigureAwait(false);
                return song;
            }

            NewFile item = NewFileKinds.Build(kindId, name, folder, 100, s.Manifest.MasterSeed);
            if (item.Path.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || HasProjectLink(s.Fs, s.Root, item.Path)) throw new AmpleException("AMP5285", "Nothing can be created in the private project folder or inside a link.");
            string full = ProjectPath.Resolve(s.Fs, s.Root, item.Path);
            if (s.Fs.FileExists(full) || s.Fs.DirectoryExists(full)) throw new AmpleException("AMP5243", $"There is already something called {item.Path}.");
            // the default folders (lyrics, assets/midi...) are made when they are not there yet
            string[] parts = item.Path.Split('/');
            string built = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                built = built.Length == 0 ? parts[i] : built + "/" + parts[i];
                if (!s.Fs.DirectoryExists(ProjectPath.Resolve(s.Fs, s.Root, built))) await s.Bus.ExecuteAsync(new CreateFolderCommand(built), c.RequestAborted).ConfigureAwait(false);
            }

            if (item.IsFolder) await s.Bus.ExecuteAsync(new CreateFolderCommand(item.Path), c.RequestAborted).ConfigureAwait(false);
            else await s.Bus.ExecuteAsync(new ImportFileCommand(item.Path, item.Bytes!), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { item.Path } });
            return Ok(new { path = item.Path, kind = kindId, isFolder = item.IsFolder, open = NewFileKinds.Find(kindId)!.Open });
        });
        foreach (string route in new[] { "/api/project/rename", "/api/project/move" })
        {
            add("POST", route, "project", "Moves a project file or folder to an unused path through the command bus; by default every path in the songs and modules that points at it follows (refactor), refactor false moves only the file, dryRun true returns the edits without moving.", true, s is null ? null : async (HttpContext c) =>
            {
                JsonElement body = await Body(c);
                string source = Str(body, "source").Replace('\\', '/').Trim('/');
                string destination = Str(body, "destination").Replace('\\', '/').Trim('/');
                if (source.Split('/').Concat(destination.Split('/')).Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(destination).Length is 0 or > 128) throw new AmpleException("AMP5285", "The source or destination path is invalid or private.");
                string from = ProjectPath.Resolve(s.Fs, s.Root, source), to = ProjectPath.Resolve(s.Fs, s.Root, destination);
                bool directory = s.Fs.DirectoryExists(from);
                if ((!directory && !s.Fs.FileExists(from)) || HasProjectLink(s.Fs, s.Root, source) || HasProjectLink(s.Fs, s.Root, destination)) throw new AmpleException("AMP5243", "The source does not exist or contains a link that cannot be moved.");
                string parent = Path.GetDirectoryName(to)!;
                bool occupied = s.Fs.FileExists(to) || s.Fs.DirectoryExists(to) || (s.Fs.DirectoryExists(parent) && s.Fs.GetFiles(parent).Concat(s.Fs.GetDirectories(parent)).Any(path => string.Equals(Path.GetFileName(path), Path.GetFileName(to), StringComparison.OrdinalIgnoreCase)));
                if (!s.Fs.DirectoryExists(parent) || occupied || (directory && ProjectPath.IsInside(from, to))) throw new AmpleException("AMP5243", "The destination parent must exist and the destination must be unused and outside the source folder.");
                // refactor (the default): every path in the songs and modules that points at it follows; the Advanced way (refactor false) moves only the file
                bool refactor = !body.TryGetProperty("refactor", out JsonElement refactorFlag) || refactorFlag.ValueKind != JsonValueKind.False;
                bool dryRun = body.TryGetProperty("dryRun", out JsonElement dryFlag) && dryFlag.ValueKind == JsonValueKind.True;
                RefactorPlan plan = new([], new Dictionary<string, string>());
                if (refactor)
                {
                    List<(string Path, string Text)> sources = [];
                    foreach (string file in ProjectSourceFiles(s, s.Root))
                    {
                        string extension = Path.GetExtension(file);
                        if (!extension.Equals(".amplet", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".module", StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = Path.GetRelativePath(s.Root, file).Replace('\\', '/');
                        sources.Add((relative, File.ReadAllText(file)));
                    }

                    plan = ProjectRefactor.Plan(sources, source, destination, directory);
                    string? unsaved = plan.Texts.Keys.FirstOrDefault(s.IsDocDirty);
                    if (unsaved is not null && !dryRun) throw new AmpleException("AMP5243", $"{unsaved} has changes that are not saved. Save or revert it before renaming, so the references can follow.");
                }

                object edits = plan.Edits.Select(e => new { file = e.File, line = e.Line, old = e.Old, @new = e.New });
                if (dryRun) return Ok(new { source, destination, directory, refactor, dryRun = true, edits });
                await s.Bus.ExecuteAsync(new MoveProjectPathCommand(source, destination, directory), c.RequestAborted).ConfigureAwait(false);
                foreach ((string path, string text) in plan.Texts)
                {
                    string target = ProjectRefactor.PathAfterMove(path, source, destination, directory);
                    await s.Bus.ExecuteAsync(new WriteFileCommand(target, text), c.RequestAborted).ConfigureAwait(false);
                    s.ForgetDoc(path);
                    s.ForgetDoc(target);
                }

                events!.Publish("files.changed", new { paths = new[] { source, destination }.Concat(plan.Texts.Keys).ToArray() });
                return Ok(new { source, destination, directory, refactor, edits });
            });
        }
        add("POST", "/api/effects/impulse", "effects", "Returns the bounded WAV impulse response selected by a convolution block for approximate local preview.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            AmpleDocument document = s.Parse(await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false));
            BlockNode block = document.Blocks.FirstOrDefault(candidate => candidate.Name == Str(body, "block") && candidate.Type == "convolution") ?? throw new AmpleException("AMP5530", "The convolution block is not in the selected song.");
            if (!body.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object) throw new AmpleException("AMP5530", "Convolution preview parameters must be an object.");
            foreach (JsonProperty parameter in parameters.EnumerateObject())
            {
                if (parameter.Name is not ("file" or "ir")) continue;
                string value = parameter.Value.ValueKind == JsonValueKind.String ? parameter.Value.GetString()! : parameter.Value.ToString();
                block.Parameters.RemoveAll(existing => existing.Name == parameter.Name);
                block.Parameters.Add(new Parameter(parameter.Name, value, block.Line));
            }

            string file = BlockFiles.ConvolutionImpulse(s.Root, s.AmpleRoot, block) ?? throw new AmpleException("AMP5350", "The selected impulse response is unavailable.");
            if (new FileInfo(file).Length > 32L * 1024 * 1024) throw new AmpleException("AMP5530", "The impulse response exceeds the local preview limit (32 MB).");
            return Results.File(file, "audio/wav");
        });
        add("POST", "/api/effects/preview", "effects", "Applies a built-in integer effect to a bounded input range without creating a take or changing project state.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string songPath = Str(body, "path");
            string blockName = Str(body, "block");
            string takeId = Str(body, "takeId");
            int fromMs = body.TryGetProperty("fromMs", out JsonElement from) && from.TryGetInt32(out int fromValue) ? fromValue : 0;
            int toMs = body.TryGetProperty("toMs", out JsonElement to) && to.TryGetInt32(out int toValue) ? toValue : 30_000;
            if (fromMs < 0 || toMs <= fromMs || toMs - fromMs > 30_000) throw new AmpleException("AMP5530", "Effect previews must be a valid range of at most 30 seconds.");
            AmpleDocument document = s.Parse(await s.DocAsync(songPath, c.RequestAborted).ConfigureAwait(false));
            BlockNode block = document.Blocks.FirstOrDefault(candidate => candidate.Name == blockName) ?? throw new AmpleException("AMP5530", "The effect block is not in the selected song.");
            IBlockRenderer renderer = AudioEffects.All.Concat(ExtraEffects.All).FirstOrDefault(candidate => candidate.BlockType == block.Type) ?? throw new AmpleException("AMP5530", "This block does not have a built-in audio effect preview.");
            // the take the page shows for the input (the last build, a cache hit or the one chosen) is fine; it only has to exist
            if (block.Froms.Count == 0) throw new AmpleException("AMP5530", "This effect block has no input to preview.");
            takeId = s.Bank.ResolveTakeId(takeId);
            if (!body.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object) throw new AmpleException("AMP5530", "Effect preview parameters must be an object.");
            foreach (JsonProperty parameter in parameters.EnumerateObject())
            {
                if (!s.Spec.Templates[block.Type].Parameters.ContainsKey(parameter.Name)) throw new AmpleException("AMP5530", "An effect preview parameter is not supported by this block.");
                string value = parameter.Value.ValueKind == JsonValueKind.String ? parameter.Value.GetString()! : parameter.Value.ToString();
                block.Parameters.RemoveAll(existing => existing.Name == parameter.Name);
                block.Parameters.Add(new Parameter(parameter.Name, value, block.Line));
            }

            Ample.Storage.Takes.TakeRecord takeRecord = s.Bank.GetTake(takeId);
            FromLine selectedInput = block.Froms.FirstOrDefault(input => !input.Disabled && input.Input is "in" or "render_in") ?? throw new AmpleException("AMP5530", "This effect block has no active audio or render input.");
            string inputRole = selectedInput.Output;
            if (inputRole == "render")
            {
                BlockNode sourceBlock = document.Blocks.FirstOrDefault(source => source.Name == selectedInput.Block) ?? throw new AmpleException("AMP5530", "The selected input source is no longer in the song.");
                inputRole = s.Spec.Templates[sourceBlock.Type].Outputs.FirstOrDefault(port => port.Type == "audio")?.Name ?? throw new AmpleException("AMP5530", "The selected render input has no audio output.");
            }
            Ample.Storage.Takes.TakeDataRef audio = takeRecord.Data.FirstOrDefault(data => data.Role == inputRole)
                ?? takeRecord.Data.FirstOrDefault(data => data.Role == "audio" || data.Role == "out")
                ?? throw new AmpleException("AMP5530", "The selected input take has no audio data.");
            await using Stream stream = s.Bank.OpenData(audio.DataId);
            using MemoryStream inputBuffer = new();
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, c.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (inputBuffer.Length + read > 128L * 1024 * 1024) throw new AmpleException("AMP5530", "The source audio exceeds the preview input limit.");
                await inputBuffer.WriteAsync(buffer.AsMemory(0, read), c.RequestAborted).ConfigureAwait(false);
            }

            short[] inputSamples;
            int sampleRate;
            try { (inputSamples, sampleRate) = Wav.Decode(inputBuffer.ToArray()); }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException) { throw new AmpleException("AMP5530", "The source take is not a supported mono WAV file."); }
            int firstSample = Math.Min(inputSamples.Length, checked((int)((long)fromMs * sampleRate / 1000)));
            int lastSample = Math.Min(inputSamples.Length, checked((int)((long)toMs * sampleRate / 1000)));
            if (lastSample <= firstSample) throw new AmpleException("AMP5530", "The requested preview range contains no audio.");
            short[] rangeSamples = inputSamples[firstSample..lastSample];
            byte[] rangeBytes = Wav.Encode(rangeSamples, sampleRate);
            byte[] originalBytes = inputBuffer.ToArray();
            if (originalBytes.Length >= 44 && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(originalBytes.AsSpan(22)) == 2)
            {
                (StereoBuffer channels, _) = StereoWav.Decode(originalBytes);
                rangeBytes = StereoWav.Encode(new StereoBuffer(channels.Left[firstSample..lastSample], channels.Right[firstSample..lastSample]), sampleRate);
            }
            string cacheKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(block.Type + "\n" + blockName + "\n" + parameters.GetRawText() + "\n" + takeId + "\n" + fromMs + "\n" + toMs + "\n" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rangeBytes)))));
            if (!EffectPreviewCache.TryGetValue(cacheKey, out byte[]? preview))
            {
                await EffectPreviewGate.WaitAsync(c.RequestAborted).ConfigureAwait(false);
                try
                {
                    if (!EffectPreviewCache.TryGetValue(cacheKey, out preview))
                    {
                        long tempoMilli = (long)Math.Round((document.Project?.Tempo ?? 100) * 1000, MidpointRounding.AwayFromZero);
                        int samplesPerBar = FixedAudio.SamplesPerBar(sampleRate, tempoMilli);
                        int bars = Math.Max(1, (rangeSamples.Length + samplesPerBar - 1) / samplesPerBar);
                        RenderRequest request = new(block, s.Spec.Templates[block.Type], sampleRate, samplesPerBar, 1, bars, Enumerable.Range(1, bars).ToHashSet(), new Dictionary<string, byte[]> { ["in"] = rangeBytes }, 0, s.Root);
                        IReadOnlyList<RenderOutput> outputs = await renderer.RenderAsync(request, c.RequestAborted).ConfigureAwait(false);
                        byte[] rendered = outputs.FirstOrDefault(output => output.Role == "out")?.Data ?? throw new AmpleException("AMP5530", "The effect renderer returned no audio preview.");
                        short[] renderedSamples;
                        int renderedRate;
                        try { (renderedSamples, renderedRate) = Wav.Decode(rendered); }
                        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException) { throw new AmpleException("AMP5530", "The effect renderer returned an unsupported WAV preview."); }
                        int previewSamples = (int)Math.Min(renderedSamples.Length, (long)renderedRate * 30);
                        preview = Wav.Encode(renderedSamples.AsSpan(0, previewSamples), renderedRate);
                        if (rendered.Length >= 44 && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(rendered.AsSpan(22)) == 2)
                        {
                            (StereoBuffer channels, _) = StereoWav.Decode(rendered);
                            preview = StereoWav.Encode(new StereoBuffer(channels.Left[..previewSamples], channels.Right[..previewSamples]), renderedRate);
                        }
                        if (preview.Length <= 64 * 1024 * 1024)
                        {
                            EffectPreviewCache[cacheKey] = preview;
                            EffectPreviewCacheOrder.Enqueue(cacheKey);
                            Interlocked.Add(ref EffectPreviewCacheBytes, preview.Length);
                            while ((EffectPreviewCache.Count > 24 || Interlocked.Read(ref EffectPreviewCacheBytes) > 64 * 1024 * 1024) && EffectPreviewCacheOrder.TryDequeue(out string? oldest))
                                if (EffectPreviewCache.TryRemove(oldest, out byte[]? removed)) Interlocked.Add(ref EffectPreviewCacheBytes, -removed.Length);
                        }
                    }
                }
                finally { EffectPreviewGate.Release(); }
            }

            return Results.File(preview, "audio/wav");
        });
        add("GET", "/api/samples", "project", "The sample sets of the project: folders (up to three levels deep, outside .ample) that hold WAV files, with the base names (lower case, without .wav and without a layer or round robin suffix), how many layers and round robins are on disk (layers, roundRobins, and per voice in voices), the set.json numbers when present (offered), and how many names are notes (notes).", false, s is null ? null : () =>
        {
            List<object> sets = [];
            foreach (string dir in Directory.EnumerateDirectories(s.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(s.Root, dir).Replace('\\', '/');
                if (rel.StartsWith(".ample", StringComparison.Ordinal) || rel.Split('/').Length > 3 || rel.Split('/').Any(p => p.StartsWith('.')))
                {
                    continue;
                }

                var voices = Directory.EnumerateFiles(dir, "*.wav").Select(f => Ample.Audio.Engine.SampleName.Parse(Path.GetFileNameWithoutExtension(f))).GroupBy(x => x.Base, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { name = g.Key, layers = g.Select(x => x.Layer).Distinct().Count(), roundRobins = g.GroupBy(x => x.Layer).Max(l => l.Count()) }).ToArray();
                if (voices.Length > 0)
                {
                    string[] files = [.. voices.Select(v => v.name)];
                    sets.Add(new { folder = rel, files, notes = files.Select(f => Ample.Audio.Engine.SampleSet.NoteOf(f)).Where(n => n is not null).Count(), layers = voices.Max(v => v.layers), roundRobins = voices.Max(v => v.roundRobins), offered = SetMeta(dir), voices });
                }
            }

            return Ok(new { sets });
        });
        add("GET", "/api/file", "project", "Reads a text file of the project (lyrics, chords, notes, data); other files are refused.", false, s is null ? null : (HttpContext c) =>
        {
            string rel = c.Request.Query["path"].ToString();
            string full = ProjectPath.Resolve(s.Root, rel);
            bool allowed = TextFile(rel) || Path.GetFileName(rel) == "ample.lock";
            if (allowed && !File.Exists(full) && c.Request.Query["optional"] == "1")
            {
                return Ok(new { path = rel, text = (string?)null });   // a sidecar the page may or may not have (notes, navigation): absent is an answer, not an error in the browser console
            }

            if (!allowed || !File.Exists(full))
            {
                throw new AmpleException("AMP5243", $"There is no editable text file '{rel}' in the project.");
            }

            return Ok(new { path = rel, text = File.ReadAllText(full) });
        });
        add("PUT", "/api/file", "project", "Writes a text file of the project through the command bus (AMPLE Redux keeps the previous text).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string rel = Str(b, "path");
            if (!TextFile(rel))
            {
                throw new AmpleException("AMP5285", "Only text files of the project (lyrics, chords, notes, data) can be written this way; songs go through the document API.");
            }

            await s.Bus.ExecuteAsync(new WriteFileCommand(rel, Str(b, "text")), c.RequestAborted);
            return Ok(new { path = rel, saved = true });
        });
        add("POST", "/api/songs", "project", "Creates a new song in the project with a settings line holding the project defaults of the plugins (the project-level plugin settings are only the starting point for new songs).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return await CreateSongAsync(s, Str(b, "name"), c.RequestAborted);
        });
        add("POST", "/api/midi/module", "modules", "Converts a project MIDI file into a generated module through the project command bus.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string file = Str(b, "file").Replace('\\', '/');
            BlockNode midi = new() { Type = "midi", Name = "convert" };
            midi.Parameters.Add(new Parameter("file", file, 0));
            string full = Ample.Audio.Engine.BuiltInRenderers.MidiFile(s.Root, midi) ?? throw new AmpleException("AMP5501", "The MIDI file must be a project-contained .mid or .midi file.");
            SmfFile parsed;
            try { parsed = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(full), SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string track = Opt(b, "track") ?? "all";
            if (track != "all" && (!int.TryParse(track, out int trackIndex) || trackIndex < 0 || trackIndex >= parsed.Tracks.Count)) throw new AmpleException("AMP5503", "The MIDI track selector must be 'all' or a valid zero-based track number.");
            SmfFile selected = track == "all" ? parsed : SelectTrackWithGlobalMeta(parsed, int.Parse(track, System.Globalization.CultureInfo.InvariantCulture));
            MidiModuleConversion result = await MidiModuleConverter.WriteAsync(selected, s.Root, Str(b, "name"), s.Bus, c.RequestAborted);
            int tempo = Math.Clamp((60_000_000 + parsed.TempoMap[0].MicrosecondsPerQuarter / 2) / parsed.TempoMap[0].MicrosecondsPerQuarter, 20, 300);
            return Ok(new { path = "modules/" + result.Name + ".module", result.Name, tempo, ppq = parsed.TicksPerQuarter, warnings = result.Warnings });
        });
        add("POST", "/api/midi/upload", "documents", "Validates and imports bounded MIDI bytes into assets/midi through the project command bus.", true, s is null ? null : async (HttpContext c) =>
        {
            Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? limit = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = SmfLimits.Standard.MaxFileBytes;
            string extension = c.Request.Query["extension"].ToString().ToLowerInvariant();
            if (extension is not (".mid" or ".midi")) throw new AmpleException("AMP5501", "MIDI uploads must use .mid or .midi.");
            if (c.Request.ContentLength.HasValue && c.Request.ContentLength.Value > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5501", "The MIDI file exceeds the supported size limit.");
            await using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            while (true)
            {
                int read = await c.Request.Body.ReadAsync(chunk, c.RequestAborted);
                if (read == 0) break;
                if (buffer.Length + read > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5501", "The MIDI file exceeds the supported size limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), c.RequestAborted);
            }
            byte[] bytes = buffer.ToArray();
            if (bytes.Length == 0) throw new AmpleException("AMP5501", "The MIDI file is empty.");
            SmfFile parsed;
            try { parsed = SmfReader.Parse(bytes, SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string selectedTrack = c.Request.Query["track"].ToString();
            if (selectedTrack != "all" && (!int.TryParse(selectedTrack, out int trackIndex) || trackIndex < 0 || trackIndex >= parsed.Tracks.Count)) throw new AmpleException("AMP5501", "The selected MIDI track does not exist.");
            IEnumerable<SmfTrack> selectedTracks = selectedTrack == "all" ? parsed.Tracks : [parsed.Tracks[int.Parse(selectedTrack, System.Globalization.CultureInfo.InvariantCulture)]];
            long lastTick = selectedTracks.SelectMany(t => t.Events).Select(e => e.Tick).DefaultIfEmpty(0).Max();
            int bars = checked((int)Math.Max(1, (lastTick + 4L * parsed.TicksPerQuarter - 1) / (4L * parsed.TicksPerQuarter)));
            if (bars > 100_000) throw new AmpleException("AMP5501", "The MIDI file exceeds the supported 100,000-bar song limit.");
            string path = "assets/midi/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() + extension;
            string full = ProjectPath.Resolve(s.Fs, s.Root, path);
            if (!File.Exists(full)) await s.Bus.RunAsync("midi.upload", JsonSerializer.Serialize(new { path, bytes = bytes.Length }), async () =>
            {
                string directory = Path.GetDirectoryName(full)!;
                s.Fs.CreateDirectory(directory);
                string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await s.Fs.WriteAllBytesDurableAsync(temp, bytes, c.RequestAborted); s.Fs.Move(temp, full, overwrite: false); s.Fs.FlushDirectory(directory); }
                catch { s.Fs.DeleteFile(temp); throw; }
            }, c.RequestAborted);
            return Ok(new { path, tracks = parsed.Tracks.Count, bars });
        });
        add("POST", "/api/audio/transcriptions", "analysis", "Runs a local analysis plugin on bounded WAV audio and keeps its aligned stems and transcription artifacts in the project analysis cache.", true, s is null ? null : async (HttpContext c) =>
        {
            Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? limit = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxTranscriptionAudioBytes + 64 * 1024;
            if (c.Request.ContentLength > MaxTranscriptionAudioBytes + 64 * 1024) throw new AmpleException("AMP5344", "The audio upload exceeds its size limit.");
            IFormCollection form = await c.Request.ReadFormAsync(c.RequestAborted);
            IFormFile audio = form.Files.GetFile("audio") ?? throw new AmpleException("AMP5344", "Choose a WAV audio file to analyse.");
            if (audio.Length is < 44 or > MaxTranscriptionAudioBytes) throw new AmpleException("AMP5344", "The audio file is empty or exceeds its size limit.");
            string pluginId = form["plugin"].ToString();
            if (pluginId.Length is < 1 or > 64 || pluginId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))) throw new AmpleException("AMP5344", "The local analysis plugin id is invalid.");
            string options = string.IsNullOrWhiteSpace(form["options"]) ? "{}" : form["options"].ToString();
            if (options.Length > 16 * 1024) throw new AmpleException("AMP5344", "The analysis options exceed their size limit.");
            using (JsonDocument parsedOptions = JsonDocument.Parse(options))
                if (parsedOptions.RootElement.ValueKind != JsonValueKind.Object) throw new AmpleException("AMP5344", "The analysis options must be a JSON object.");
            bool allowGpu = bool.TryParse(form["allowGpu"], out bool gpuConsent) && gpuConsent;
            bool remote = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted)).Settings.Faer.Separation == "faer";
            if (remote != (pluginId == "faer")) throw new AmpleException("AMP5344", "Choose the analysis route configured for this project.");
            if (remote)
            {
                using JsonDocument remoteOptions = JsonDocument.Parse(options);
                if (remoteOptions.RootElement.EnumerateObject().Any()) throw new AmpleException("AMP5344", "Faer separation has no plugin options.");
            }
            DiscoveredPlugin? plugin = null;
            if (!remote)
            {
                (IReadOnlyList<DiscoveredPlugin> plugins, _) = PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins"), Path.Combine(s.Root, "plugins"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
                plugin = plugins.FirstOrDefault(item => item.Manifest.Id == pluginId && item.Manifest.Kind == "analysis" && item.Manifest.Analyses?.Contains("transcription", StringComparer.Ordinal) == true)
                    ?? throw new AmpleException("AMP5344", "There is no local analysis plugin offering transcription.");
                if (plugin.Manifest.Needs.Network) throw new AmpleException("AMP5340", "Audio transcription plugins that need network access are not allowed.");
            }

            string requestedId = form["job"].ToString();
            string id = string.IsNullOrEmpty(requestedId) ? Guid.NewGuid().ToString("N") : requestedId;
            if (!Guid.TryParseExact(id, "N", out _)) throw new AmpleException("AMP5344", "The transcription job id is invalid.");
            string transcriptionRoot = Path.Combine(s.Root, ".ample", "transcriptions");
            Directory.CreateDirectory(transcriptionRoot);
            string jobFolder = Path.Combine(transcriptionRoot, id);
            string leasePath = Path.Combine(transcriptionRoot, id + ".running");
            string inputFolder = Path.Combine(jobFolder, "in");
            bool ownsJob = false;
            bool ownsLease = false;
            try
            {
                using FileStream lease = new(leasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ownsLease = true;
                if (Directory.Exists(jobFolder)) throw new AmpleException("AMP5344", "The transcription job id is already in use.");
                Directory.CreateDirectory(jobFolder);
                ownsJob = true;
                Directory.CreateDirectory(inputFolder);
                string inputPath = Path.Combine(inputFolder, "audio.wav");
                await using (FileStream destination = new(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    await audio.CopyToAsync(destination, c.RequestAborted);
                if (new FileInfo(inputPath).Length != audio.Length) throw new AmpleException("AMP5344", "The uploaded audio length changed while it was being received.");
                AudioInputInfo sourceInfo;
                try { sourceInfo = AudioInput.ValidateWav(inputPath, MaxTranscriptionAudioBytes, MaxTranscriptionSeconds); }
                catch (InvalidDataException ex) { throw new AmpleException("AMP5344", ex.Message); }
                AnalysisJob analysisJob = new(jobFolder, "transcription", options, TimeSpan.FromMinutes(30), progress => s.Events.Publish("analysis.progress", new { job = id, progress.Fraction, progress.Step, progress.StepFraction }));
                AnalysisPluginResult analysis = remote ? await FaerSeparation.RunAsync(analysisJob, inputPath, sourceInfo, c.RequestAborted) : await s.AnalyzeTranscriptionAsync(plugin!, analysisJob, allowGpu, c.RequestAborted);
                SortedDictionary<string, StoredTranscriptionFile> files = new(StringComparer.Ordinal);
                string outputFolder = Path.Combine(jobFolder, "out");
                foreach ((string role, string path) in analysis.Outputs)
                {
                    if (!ProjectPath.IsInside(outputFolder, path)) throw new AmpleException("AMP5272", "Transcription artifacts must be written under the job output folder.");
                    string extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension == ".wav")
                    {
                        AudioInputInfo stem;
                        try { stem = AudioInput.ValidateWav(path, 512L * 1024 * 1024, MaxTranscriptionSeconds); }
                        catch (InvalidDataException ex) { throw new AmpleException("AMP5344", "An analysis stem is not a valid bounded WAV: " + ex.Message); }
                        if (stem.SampleRate != sourceInfo.SampleRate || stem.Frames != sourceInfo.Frames) throw new AmpleException("AMP5344", "An analysis stem is not aligned to the source audio timeline.");
                    }
                    else if (extension is ".mid" or ".midi")
                    {
                        FileInfo midiInfo = new(path);
                        if (midiInfo.Length > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5344", "A transcription MIDI output exceeds its size limit.");
                        try { _ = SmfReader.Parse(await File.ReadAllBytesAsync(path, c.RequestAborted), SmfLimits.Standard); }
                        catch (SmfFormatException ex) { throw new AmpleException("AMP5344", "A transcription MIDI output is invalid: " + ex.Message); }
                    }
                    else throw new AmpleException("AMP5344", "Analysis artifacts must be WAV audio or Standard MIDI files.");
                    string relative = Path.GetRelativePath(outputFolder, path).Replace('\\', '/');
                    if (files.Values.Any(existing => string.Equals(existing.Path, relative, StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5344", "Analysis artifact paths must be unique.");
                    files.Add(role, new StoredTranscriptionFile(relative, extension is ".mid" or ".midi" ? "audio/midi" : "audio/wav"));
                }
                if (!files.TryGetValue("vocals", out StoredTranscriptionFile? vocals) || vocals.ContentType != "audio/wav" || !files.TryGetValue("backing", out StoredTranscriptionFile? backing) || backing.ContentType != "audio/wav")
                    throw new AmpleException("AMP5344", "The analysis route must return aligned vocals and backing WAV stems.");
                if (System.Text.Encoding.UTF8.GetByteCount(analysis.Json) > 8 * 1024 * 1024) throw new AmpleException("AMP5343", "The analysis metadata exceeds its size limit.");
                StoredTranscription stored = new(id, analysis.Json, files);
                string storedJson = JsonSerializer.Serialize(stored, Web);
                if (System.Text.Encoding.UTF8.GetByteCount(storedJson) > 16 * 1024 * 1024) throw new AmpleException("AMP5343", "The completed transcription index exceeds its size limit.");
                await AtomicFile.WriteTextAsync(s.Fs, Path.Combine(jobFolder, "result.json"), storedJson, c.RequestAborted);
                return Ok(TranscriptionView(stored));
            }
            catch
            {
                if (ownsJob && Directory.Exists(jobFolder)) Directory.Delete(jobFolder, recursive: true);
                throw;
            }
            finally { if (ownsLease && File.Exists(leasePath)) File.Delete(leasePath); }
        });
        add("GET", "/api/audio/transcriptions/{id}", "analysis", "Returns local transcription provenance and URLs for its aligned audio and MIDI artifacts.", false, s is null ? null : (string id) => Ok(TranscriptionView(ReadTranscription(s.Root, id))));
        add("GET", "/api/audio/transcriptions/{id}/peaks", "analysis", "Returns a bounded waveform summary for a 16-bit source or stem WAV.", false, s is null ? null : (string id, HttpContext c) =>
        {
            StoredTranscription result = ReadTranscription(s.Root, id);
            string role = string.IsNullOrEmpty(c.Request.Query["role"]) ? "source" : c.Request.Query["role"].ToString();
            string folder = Path.Combine(s.Root, ".ample", "transcriptions", id);
            string path;
            if (role == "source") path = Path.Combine(folder, "in", "audio.wav");
            else if (result.Files.TryGetValue(role, out StoredTranscriptionFile? artifact) && artifact.ContentType == "audio/wav")
            {
                string[] parts = artifact.Path.Split(['/', '\\']);
                if (Path.IsPathRooted(artifact.Path) || parts.Any(part => part is "" or "." or "..")) throw new AmpleException("AMP5344", "The stored transcription path is invalid.");
                path = Path.GetFullPath(Path.Combine(folder, "out", artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            else throw new AmpleException("AMP5344", "There is no WAV artifact with that role.");
            string contentRoot = Path.Combine(folder, role == "source" ? "in" : "out");
            if (!ProjectPath.IsInside(contentRoot, path) || !File.Exists(path)) throw new AmpleException("AMP5344", "The transcription audio is missing.");
            for (string? current = path; current is not null && current != contentRoot; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AmpleException("AMP5344", "The transcription audio follows a link.");
            int points = int.TryParse(c.Request.Query["points"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, 16, 4000) : 800;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Ok(Peaks(stream, points));
        });
        add("GET", "/api/audio/transcriptions/{id}/{role}", "analysis", "Streams one completed local transcription source, stem or MIDI artifact with byte ranges.", false, s is null ? null : (string id, string role) =>
        {
            StoredTranscription result = ReadTranscription(s.Root, id);
            string folder = Path.Combine(s.Root, ".ample", "transcriptions", id);
            string path, contentType, contentRoot;
            if (role == "source") (path, contentType, contentRoot) = (Path.Combine(folder, "in", "audio.wav"), "audio/wav", Path.Combine(folder, "in"));
            else if (result.Files.TryGetValue(role, out StoredTranscriptionFile? artifact))
            {
                contentRoot = Path.Combine(folder, "out");
                string[] parts = artifact.Path.Split(['/', '\\']);
                if (Path.IsPathRooted(artifact.Path) || parts.Any(part => part is "" or "." or "..")) throw new AmpleException("AMP5344", "The stored transcription path is invalid.");
                path = Path.GetFullPath(Path.Combine(contentRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar)));
                contentType = artifact.ContentType;
            }
            else throw new AmpleException("AMP5344", "There is no transcription artifact with that role.");
            if (!ProjectPath.IsInside(contentRoot, path) || !File.Exists(path)) throw new AmpleException("AMP5344", "The stored transcription artifact is missing.");
            for (string? current = path; current is not null && current != contentRoot; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AmpleException("AMP5344", "The stored transcription artifact follows a link.");
            return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), contentType, enableRangeProcessing: true);
        });
        add("GET", "/api/midi/info", "documents", "Returns a bounded, payload-free summary of a project MIDI file.", false, s is null ? null : (HttpContext c) =>
        {
            BlockNode midi = new() { Type = "midi", Name = "info" };
            midi.Parameters.Add(new Parameter("file", c.Request.Query["file"].ToString(), 0));
            string full = BuiltInRenderers.MidiFile(s.Root, midi) ?? throw new AmpleException("AMP5501", "The MIDI file must be a project-contained .mid or .midi file.");
            SmfFile file;
            try { file = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(full), SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            long ticks = file.Tracks.SelectMany(t => t.Events).Select(e => e.Tick).DefaultIfEmpty(0).Max();
            SmfTempo tempo = file.TempoMap[0];
            return Ok(new { tracks = file.Tracks.Count, file.Format, ppq = file.TicksPerQuarter, tempo.MicrosecondsPerQuarter, lengthTicks = ticks, bars = (ticks + 4L * file.TicksPerQuarter - 1) / (4L * file.TicksPerQuarter) });
        });
        add("GET", "/api/midi/notes", "documents", "The paired notes of a project MIDI file (all tracks or one), as ticks of the file, for the read-only piano roll and for making an editable copy; bounded.", false, s is null ? null : (HttpContext c) =>
        {
            BlockNode midi = new() { Type = "midi", Name = "notes" };
            midi.Parameters.Add(new Parameter("file", c.Request.Query["file"].ToString(), 0));
            string full = BuiltInRenderers.MidiFile(s.Root, midi) ?? throw new AmpleException("AMP5501", "The MIDI file must be a project-contained .mid or .midi file.");
            SmfFile file;
            try { file = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(full), SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string selected = c.Request.Query["track"].ToString();
            int[] tracks = string.IsNullOrEmpty(selected) || selected == "all" ? [.. Enumerable.Range(0, file.Tracks.Count)] : int.TryParse(selected, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int one) && one >= 0 && one < file.Tracks.Count ? [one] : throw new AmpleException("AMP5501", "The track must be all or a zero-based track number.");
            List<object> notes = [];
            bool truncated = false;
            foreach (int index in tracks)
            {
                foreach (MidiNoteSpan note in MidiNotePairer.Pair(file.Tracks[index], index).Notes)
                {
                    if (notes.Count >= 100_000) { truncated = true; break; }
                    notes.Add(new { start = note.StartTick, end = note.EndTick, pitch = note.Pitch, velocity = note.Velocity, channel = note.Channel, bank = note.Bank, program = note.Program, track = index });
                }
            }

            return Ok(new { ppq = file.TicksPerQuarter, microsecondsPerQuarter = file.TempoMap[0].MicrosecondsPerQuarter, tracks = file.Tracks.Count, notes, truncated });
        });
        add("POST", "/api/midi/clip-notes", "documents", "The notes a midi-clip block plays (from its chords, melody or notes), written as compact notes text, so the piano roll can make a clip with chords or a melody editable; nothing is changed or built.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            AmpleDocument document = s.ParseExpanded(await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false));
            BlockNode block = document.Blocks.FirstOrDefault(candidate => candidate.Name == Str(body, "block") && candidate.Type == "midi-clip") ?? throw new AmpleException("AMP5501", "The block is not a midi-clip of this song.");
            int bars = int.TryParse(block.Parameters.FirstOrDefault(p => p.Name == "bars")?.Value.Trim().Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedBars) && parsedBars is > 0 and <= 512 ? parsedBars : 16;
            long tempoMilli = (long)((document.Project?.Tempo ?? 120) * 1000 + 0.5);
            RenderRequest request = new(block, s.Spec.Templates["midi-clip"], 44_100, FixedAudio.SamplesPerBar(44_100, tempoMilli), 1, bars, new HashSet<int>(Enumerable.Range(1, bars)), new Dictionary<string, byte[]>(), 0, s.Root);
            IReadOnlyList<RenderOutput> rendered = await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "midi-clip").RenderAsync(request, c.RequestAborted);
            using JsonDocument notes = JsonDocument.Parse(rendered.Single(output => output.Role == "notes").Data);
            string Text(JsonElement note, string name, decimal fallback) => (note.TryGetProperty(name, out JsonElement value) && value.TryGetDecimal(out decimal number) ? number : fallback).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            List<string> entries = [];
            foreach (JsonElement note in notes.RootElement.GetProperty("notes").EnumerateArray().OrderBy(n => n.GetProperty("bar").GetInt32()).ThenBy(n => n.TryGetProperty("beat", out JsonElement beat) && beat.TryGetDecimal(out decimal value) ? value : 0m).ThenBy(n => n.GetProperty("pitch").GetInt32()))
            {
                entries.Add($"{note.GetProperty("bar").GetInt32()}:{Text(note, "beat", 0)}:{Text(note, "beats", 1)}:{note.GetProperty("pitch").GetInt32()}:{(note.TryGetProperty("velocity", out JsonElement velocity) ? velocity.GetInt32() : 96)}");
            }

            return Ok(new { text = string.Join(' ', entries), count = entries.Count, bars });
        });
        add("POST", "/api/notes/bake", "documents", "Bakes the note pipeline of a midi-clip up to a stage (or detaches it into plain notes) and returns the new text of the song and, for a bake into a file, that file; nothing is written.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            DocSession doc = await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false);
            string block = Str(body, "block");
            string? stage = body.TryGetProperty("stage", out JsonElement stageValue) && stageValue.ValueKind == JsonValueKind.String && stageValue.GetString()!.Length > 0 ? stageValue.GetString() : null;
            string? file = body.TryGetProperty("file", out JsonElement fileValue) && fileValue.ValueKind == JsonValueKind.String && fileValue.GetString()!.Length > 0 ? fileValue.GetString() : null;
            bool detach = body.TryGetProperty("detach", out JsonElement detachValue) && detachValue.ValueKind == JsonValueKind.True;
            if (file is not null) ProjectPath.Resolve(s.Root, file);
            Ample.Engine.Modules.ModuleService midi = new(s.Root, () => s.Library, s.Manifest.Id.ToString("D"));
            string? Read(string relative)
            {
                try
                {
                    string full = ProjectPath.Resolve(s.Root, relative);
                    return File.Exists(full) ? File.ReadAllText(full) : null;
                }
                catch (AmpleException)
                {
                    return null;
                }
            }

            try
            {
                BakeOutcome outcome = detach
                    ? NoteBake.Detach(doc.Text, s.Spec, block, Read, NotePipeline.HardCap, midi.ReadMidiNotes, c.RequestAborted)
                    : NoteBake.Bake(doc.Text, s.Spec, block, stage, file, Read, NotePipeline.HardCap, midi.ReadMidiNotes, c.RequestAborted);
                return Ok(new { text = outcome.Text, file = outcome.FilePath, fileContent = outcome.FileContent, notes = outcome.Notes, stage = outcome.Stage, notesHash = outcome.NotesHash, inputHash = outcome.InputHash });
            }
            catch (NoteBakeException e)
            {
                throw new AmpleException(e.Code, e.Message);
            }
        });
        add("POST", "/api/notes/editable", "documents", "Rewrites one hand-written editable piece of a midi-clip's note pipeline (the roll writes here) and returns the new text of the song; nothing is written.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            DocSession doc = await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false);
            int index = body.TryGetProperty("index", out JsonElement indexValue) && indexValue.TryGetInt32(out int parsed) ? parsed : 0;
            try
            {
                return Ok(new { text = NoteBake.SetEditable(doc.Text, s.Spec, Str(body, "block"), index, Str(body, "notes").Trim().Trim('"')) });
            }
            catch (NoteBakeException e)
            {
                throw new AmpleException(e.Code, e.Message);
            }
        });
        add("POST", "/api/notes/random-seed", "documents", "Replaces every random-seed() of the song by a whole number chosen once from OS randomness (so the song stays reproducible) and returns the new text; nothing is written.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            DocSession doc = await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false);
            (string text, int count) = NoteBake.PickSeeds(doc.Text, () => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue) * 1_048_576L + System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_048_576));
            return Ok(new { text, count });
        });
        add("POST", "/api/notes/duplicate", "documents", "Copies a midi-clip under a new name and lets the copy reuse, fork or leave the stable random namespaces of the original (mode reuse, fork or independent); returns the new text of the song, nothing is written.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            DocSession doc = await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false);
            NoteBake.SeedCopyMode mode = Str(body, "mode") switch { "reuse" => NoteBake.SeedCopyMode.Reuse, "fork" => NoteBake.SeedCopyMode.Fork, "independent" => NoteBake.SeedCopyMode.Independent, _ => throw new AmpleException("AMP5285", "The mode must be reuse, fork or independent.") };
            try
            {
                return Ok(new { text = NoteBake.DuplicateClip(doc.Text, s.Spec, Str(body, "block"), Str(body, "name"), mode) });
            }
            catch (NoteBakeException e)
            {
                throw new AmpleException(e.Code, e.Message);
            }
        });
        add("POST", "/api/notes/provenance", "documents", "Where the randomness of a midi-clip comes from: every use of a seed with its derived key and the values drawn, its random-streams, and the stable names of the song with their use counts.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            DocSession doc = await s.DocAsync(Str(body, "path"), c.RequestAborted).ConfigureAwait(false);
            Ample.Engine.Modules.ModuleService modules = new(s.Root, () => s.Library, s.Manifest.Id.ToString("D"));
            string? Read(string relative)
            {
                try
                {
                    string full = ProjectPath.Resolve(s.Root, relative);
                    return File.Exists(full) ? File.ReadAllText(full) : null;
                }
                catch (AmpleException)
                {
                    return null;
                }
            }

            (IReadOnlyList<SeedUseInfo> seeds, IReadOnlyList<StreamUseInfo> streams, IReadOnlyList<(string Name, int Count)> names) = NoteBake.Provenance(doc.Text, s.Spec, Str(body, "block"), Read, modules.ReadMidiNotes);
            return Ok(new { seeds, streams, names = names.Select(n => new { name = n.Name, count = n.Count }) });
        });
        add("POST", "/api/gm/audition", "documents", "Renders a bounded temporary GM note preview without creating a take; channel -1 auditions all note channels.", false, s is null ? null : async (HttpContext c) =>
        {
            Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? limit = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 64 * 1024;
            if (c.Request.ContentLength is > 64 * 1024) throw new AmpleException("AMP5502", "The GM audition request exceeds its size limit.");
            JsonElement body = await Body(c);
            int ReadInt(JsonElement value, string name, int min, int max) => value.TryGetProperty(name, out JsonElement field) && field.TryGetInt32(out int parsed) && parsed >= min && parsed <= max ? parsed : throw new AmpleException("AMP5311", $"The audition field '{name}' is outside its supported range.");
            string file = Str(body, "file");
            int bank = ReadInt(body, "bank", 0, 16383), program = ReadInt(body, "program", 0, 127), channel = ReadInt(body, "channel", -1, 15), gain = ReadInt(body, "gain", -24, 12);
            string limiter = Str(body, "limiter");
            if (limiter is not ("soft" or "off")) throw new AmpleException("AMP5352", "The limiter must be 'soft' or 'off'.");
            if (!body.TryGetProperty("notes", out JsonElement noteList) || noteList.ValueKind != JsonValueKind.Array || noteList.GetArrayLength() is < 1 or > 128) throw new AmpleException("AMP5502", "A GM audition must contain 1 to 128 notes.");
            List<object> notes = [];
            foreach (JsonElement note in noteList.EnumerateArray())
            {
                int bar = ReadInt(note, "bar", 1, 2), tick = ReadInt(note, "tick", 0, 3_999_999), ticks = ReadInt(note, "ticks", 1, 4_000_000), ppq = ReadInt(note, "ppq", 1, 1_000_000), pitch = ReadInt(note, "pitch", 0, 127), velocity = ReadInt(note, "velocity", 1, 127);
                if (tick >= 4L * ppq || ticks > 4L * ppq - tick) throw new AmpleException("AMP5502", "A GM audition note exceeds the supported bar length.");
                Dictionary<string, object> item = new() { ["bar"] = bar, ["tick"] = tick, ["ticks"] = ticks, ["ppq"] = ppq, ["pitch"] = pitch, ["velocity"] = velocity };
                if (note.TryGetProperty("channel", out _)) { int noteChannel = ReadInt(note, "channel", -1, 15); if (noteChannel >= 0) item["channel"] = noteChannel; }
                if (note.TryGetProperty("bank", out _)) { int noteBank = ReadInt(note, "bank", -1, 16383); if (noteBank >= 0) item["bank"] = noteBank; }
                if (note.TryGetProperty("program", out _)) { int noteProgram = ReadInt(note, "program", -1, 127); if (noteProgram >= 0) item["program"] = noteProgram; }
                notes.Add(item);
            }
            BlockNode block = new() { Type = "gm", Name = "gm-audition" };
            foreach ((string name, string value) in new[] { ("file", file), ("bank", bank.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("program", program.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("channel", channel.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("gain", gain.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("limiter", limiter) }) block.Parameters.Add(new Parameter(name, value, 0));
            byte[] input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { notes });
            RenderRequest request = new(block, s.Spec.Templates["gm"], 44_100, 88_200, 1, 2,   // the full rate: at 22.05 kHz a 44.1 kHz sample was read twice as fast and its highs folded back as hiss
                new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]> { ["notes"] = input }, 0, s.Root);
            IReadOnlyList<RenderOutput> rendered = await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "gm").RenderAsync(request, c.RequestAborted);
            string warnings = System.Text.Encoding.UTF8.GetString(rendered.FirstOrDefault(output => output.Role == "warnings")?.Data ?? []);
            if (warnings.Length > 0) c.Response.Headers["X-Ample-GM-Warnings"] = new string([.. string.Join("; ", warnings.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)).Where(ch => ch is >= ' ' and <= '~')]);   // a header is one line of plain ASCII
            return Results.File(rendered.Single(output => output.Role == "audio").Data, "audio/wav");
        });
        add("GET", "/api/midi/download", "documents", "Re-encodes a project MIDI source as a downloadable Standard MIDI File while retaining its parsed events.", false, s is null ? null : (HttpContext c) =>
        {
            BlockNode midi = new() { Type = "midi", Name = "download" };
            midi.Parameters.Add(new Parameter("file", c.Request.Query["file"].ToString(), 0));
            string full = BuiltInRenderers.MidiFile(s.Root, midi) ?? throw new AmpleException("AMP5501", "The MIDI file must be a project-contained .mid or .midi file.");
            SmfFile file;
            try { file = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(full), SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            byte[] bytes;
            try { bytes = SmfWriter.Write(file, SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string name = Path.GetFileNameWithoutExtension(full) + "-roundtrip.mid";
            return Results.File(bytes, "audio/midi", name);
        });
        add("POST", "/api/midi/module-activate", "modules", "Saves an edited MIDI module, exports its active MIDI variant through the command bus and updates the associated MIDI block working copy.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string modulePath = Str(body, "path").Replace('\\', '/');
            if (!modulePath.StartsWith("modules/", StringComparison.Ordinal) || !modulePath.EndsWith(".module", StringComparison.Ordinal)) throw new AmpleException("AMP5504", "The MIDI variant module path is invalid.");
            string moduleName = modulePath[8..^7];
            if (moduleName.Length is < 1 or > 64 || moduleName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))) throw new AmpleException("AMP5504", "The MIDI module name is invalid.");
            string songPath = Str(body, "songPath");
            string blockName = Str(body, "block");
            DocSession document = await s.DocAsync(songPath, c.RequestAborted);
            Check(document, body);
            AmpleDocument parsed = s.Parse(document);
            BlockNode midi = parsed.Blocks.FirstOrDefault(block => block.Name == blockName && block.Type == "midi") ?? throw new AmpleException("AMP5504", "The associated MIDI block no longer exists.");
            if (midi.Parameters.FirstOrDefault(parameter => parameter.Name == "variant-module")?.Value != moduleName || string.IsNullOrWhiteSpace(midi.Parameters.FirstOrDefault(parameter => parameter.Name == "source-file")?.Value)) throw new AmpleException("AMP5504", "The module is not registered as this MIDI block's editable variant.");
            ModuleDocState state = await s.ModuleDocAsync(modulePath, c.RequestAborted);
            string moduleSource;
            lock (state) moduleSource = state.Source;
            MidiModuleExport export = MidiModuleExporter.Export(moduleSource, s.Spec);
            string outputPath = "assets/midi/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(export.Bytes)) + ".mid";
            string outputFullPath = ProjectPath.Resolve(s.Fs, s.Root, outputPath);
            if (!File.Exists(outputFullPath)) await s.Bus.RunAsync("midi.variant-export", JsonSerializer.Serialize(new { path = outputPath, bytes = export.Bytes.Length }), async () =>
            {
                string directory = Path.GetDirectoryName(outputFullPath)!;
                s.Fs.CreateDirectory(directory);
                string temporary = outputFullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await s.Fs.WriteAllBytesDurableAsync(temporary, export.Bytes, c.RequestAborted); s.Fs.Move(temporary, outputFullPath, overwrite: false); s.Fs.FlushDirectory(directory); }
                catch { s.Fs.DeleteFile(temporary); throw; }
            }, c.RequestAborted);
            await s.Bus.ExecuteAsync(new WriteFileCommand(modulePath, moduleSource), c.RequestAborted);
            EditResult fileEdit = Edits.SetParameter(parsed, blockName, "file", outputPath);
            if (!fileEdit.Ok) throw new AmpleException("AMP5504", "The MIDI block cannot activate its exported variant.");
            DocSession nextDocument = new(songPath, Edits.Apply(document.Text, fileEdit.Edits));
            AmpleDocument nextParsed = s.Parse(nextDocument);
            EditResult variantEdit = Edits.SetParameter(nextParsed, blockName, "variant-file", outputPath);
            if (!variantEdit.Ok) throw new AmpleException("AMP5504", "The MIDI block cannot retain its exported variant path.");
            string nextText = Edits.Apply(nextDocument.Text, variantEdit.Edits);
            lock (document)
            {
                Check(document, body);
                document.Text = nextText;
                document.Version++;
            }
            events!.Publish("doc.changed", new { document.Path, document.Version });
            return Ok(new { midi = DocView(document, s.Parse(document), s), path = outputPath, warnings = export.Warnings });
        });
        add("POST", "/api/midi/module-download", "documents", "Exports edited notes from a generated MIDI module and merges them with retained source events.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string moduleName = Str(body, "name");
            if (moduleName.Length is < 1 or > 64 || moduleName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))) throw new AmpleException("AMP5504", "The MIDI module name is invalid.");
            string path = ProjectPath.Resolve(s.Root, "modules/" + moduleName + ".module");
            FileInfo info = new(path);
            if (!info.Exists || info.Length > 64L * 1024 * 1024) throw new AmpleException("AMP5504", "The generated MIDI module is missing or exceeds its export size limit.");
            ModuleDocState state = await s.ModuleDocAsync("modules/" + moduleName + ".module", c.RequestAborted);
            string moduleSource;
            lock (state) moduleSource = state.Source;
            MidiModuleExport export = MidiModuleExporter.Export(moduleSource, s.Spec);
            if (export.Warnings.Count > 0) c.Response.Headers["X-Ample-Midi-Warnings"] = string.Join("; ", export.Warnings);
            return Results.File(export.Bytes, "audio/midi", moduleName + "-edited.mid");
        });
        add("GET", "/api/gpu", "plugins", "The free and total GPU memory (totals only) and the plugin processes that are kept warm.", false, s is null ? null : () => Ok(s.GpuStatus()));
        add("POST", "/api/gpu/release", "plugins", "Stops the kept-warm plugin processes now so their GPU memory is given back.", true, s is null ? null : () => Ok(new { released = s.ReleaseGpu() }));
        add("GET", "/api/plugins/settings", "plugins", "The settings schema of a plugin and the values stored in this project (defaults filled in).", false, s is null ? null : (HttpContext c) =>
        {
            string id = c.Request.Query["id"].ToString();
            (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> plugins, _) = Ample.Plugins.PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins")]);
            Ample.Plugins.DiscoveredPlugin p = plugins.FirstOrDefault(x => x.Manifest.Id == id) ?? throw new AmpleException("AMP5243", $"There is no plugin '{id}'.");
            (IReadOnlyList<string> errors, string filled) = Ample.Plugins.SettingsValidator.Validate(p.Manifest.SettingsSchemaJson, s.PluginSettingsJson(id));
            return Ok(new { id, schema = JsonDocument.Parse(p.Manifest.SettingsSchemaJson).RootElement, values = JsonDocument.Parse(filled).RootElement, errors });
        });
        add("PUT", "/api/plugins/settings", "plugins", "Validates and stores the settings of a plugin in the project (secrets only by reference).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string id = Str(b, "id");
            (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> plugins, _) = Ample.Plugins.PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins")]);
            Ample.Plugins.DiscoveredPlugin p = plugins.FirstOrDefault(x => x.Manifest.Id == id) ?? throw new AmpleException("AMP5243", $"There is no plugin '{id}'.");
            string values = b.GetProperty("values").GetRawText();
            (IReadOnlyList<string> errors, string filled) = Ample.Plugins.SettingsValidator.Validate(p.Manifest.SettingsSchemaJson, values);
            if (errors.Count > 0)
            {
                throw new AmpleException("AMP5279", "The settings are not valid: " + string.Join("; ", errors));
            }

            string file = Path.Combine(s.Root, ".ample", "plugin-settings.json");
            JsonObject all = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing ? existing : [];
            all[id] = JsonNode.Parse(filled);
            await s.Bus.ExecuteAsync(new WriteFileCommand(ProjectSession.PluginSettingsFile, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"), c.RequestAborted);
            s.ForgetRenderers();
            events!.Publish("plugins.changed", new { id });
            return Ok(new { id, values = JsonDocument.Parse(filled).RootElement });
        });
        add("GET", "/api/projects", "projects", "The recent projects with a short health summary each (folder reachable, findings, age of the last backup).", false, s is null ? null : async (HttpContext c) =>
        {
            Ample.Storage.Projects.RecentProjects.Touch(s.Library.Home, s.Root, s.Manifest.Name, s.Manifest.Id.ToString("D"), s.Clock, null);
            List<object> list = [];
            foreach (Ample.Storage.Projects.RecentProject r in Ample.Storage.Projects.RecentProjects.List(s.Library.Home))
            {
                bool there = Directory.Exists(Path.Combine(r.Path, ".ample"));
                IReadOnlyList<DoctorFinding> findings = there && !string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase) ? await ProjectDoctor.CheckAsync(s.Fs, s.Clock, r.Path, c.RequestAborted) : [];
                Ample.Storage.Backup.BackupStatus? backup = there ? Ample.Storage.Backup.BackupPolicy.Read(r.Path, s.Clock) : null;
                list.Add(new { r.Path, r.Name, r.Id, r.LastOpened, reachable = there, current = string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase), findings = findings.Count(f => f.Severity == "error"), warnings = findings.Count(f => f.Severity == "warning"), backupAgeDays = backup is null || backup.Id.Length == 0 ? (double?)null : Math.Round(backup.AgeDays, 1) });
            }

            return Ok(new { projects = list });
        });
        add("POST", "/api/projects/new", "projects", "Creates a new project folder (it is not opened).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string folder = Path.GetFullPath(Str(b, "folder"));
            using OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new Ample.Audio.IO.GuidV7Generator(), folder, Opt(b, "name") ?? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), null, c.RequestAborted);
            DemoSong.SeedNew(folder, s.AmpleRoot);   // every new project starts with the demo song
            Ample.Storage.Projects.RecentProjects.Touch(s.Library.Home, folder, created.Manifest.Name, created.Manifest.Id.ToString("D"), s.Clock, null);
            return Ok(new { folder, name = created.Manifest.Name, id = created.Manifest.Id });
        });
        add("POST", "/api/projects/open", "projects", "Opens another project in a server of its own and returns its address (this server keeps its project).", true, s is null ? null : async (HttpContext c) =>
        {
            string folder = Path.GetFullPath(Str(await Body(c), "folder"));
            if (string.Equals(folder, Path.GetFullPath(s.Root), StringComparison.OrdinalIgnoreCase))
            {
                throw new AmpleException("AMP5285", "That project is the one open here.");
            }

            return Ok(new { url = await s.OpenChildAsync(folder, c.RequestAborted) });
        });
        add("POST", "/api/projects/forget", "projects", "Removes a project from the recent list (the folder is not touched).", true, s is null ? null : async (HttpContext c) =>
        {
            Ample.Storage.Projects.RecentProjects.Forget(s.Library.Home, Str(await Body(c), "folder"));
            return Ok(new { forgotten = true });
        });
        add("GET", "/api/project/settings", "project", "The project settings: AMPLE Redux budget and retention, default engine, Faer routes, linked folders, master seed (as text).", false, s is null ? null : () =>
        {
            ProjectManifest m = ProjectFile.Parse(File.ReadAllText(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)));
            return Ok(new { m.Name, masterSeed = m.MasterSeed, m.Settings.BlackBox, m.Settings.Mounts, m.Settings.DefaultEngine, m.Settings.ProjectPanel, m.Settings.Faer, koda = m.Settings.Koda, buildParallelism = m.Settings.BuildParallelism, parallelismLimit = Ample.Engine.Engine.BuildParallelism.ServerLimit, cores = Ample.Engine.Engine.BuildParallelism.Cores, ai = m.Settings.Ai, braveSearchKeyStoreAvailable = OperatingSystem.IsWindows(), braveSearchKeyConfigured = AiProjectSecretStore.IsConfigured(s.Library.Home, m.Id) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY_" + m.Id.ToString("N").ToUpperInvariant())) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMPLE_BRAVE_SEARCH_API_KEY")) });
        });
        add("GET", "/api/project/first-run", "project", "The first-time setup wizard of the project: whether it still has to be walked through (pending), where the project came from (origin: new or existing), the wizard version of this program and the version that was done.", false, s is null ? null : async (HttpContext c) =>
        {
            FirstRunSettings first = (await ReadManifest(s, c.RequestAborted)).Settings.FirstRun;
            return Ok(new { pending = first.Pending, origin = first.Origin, wizardVersion = FirstRunSettings.WizardVersion, doneVersion = first.DoneVersion, doneAt = first.DoneAt });
        });
        add("POST", "/api/project/first-run/complete", "project", "Marks the first-time setup wizard as walked through (the same again does nothing more) and says what the studio opens: the demo song of a new project (play: true), otherwise the song that was open (play: false).", false, s is null ? null : async (HttpContext c) =>
        {
            ProjectManifest m = await ReadManifest(s, c.RequestAborted);
            FirstRunSettings first = m.Settings.FirstRun;
            if (first.Pending) await s.Bus.ExecuteAsync(new SetSettingsCommand(m.Settings with { FirstRun = first.Completed(DateTimeOffset.UtcNow) }), c.RequestAborted);
            bool demo = first.Origin == "new" && File.Exists(Path.Combine(s.Root, Ample.Storage.Projects.DemoSongRules.FileName));
            return Ok(new { pending = false, origin = first.Origin, open = demo ? Ample.Storage.Projects.DemoSongRules.FileName : null, play = demo });
        });
        add("GET", "/api/beats", "project", "The built-in beats catalogue (no AI): search by text, genre, mood, kind, bpm, energy and novelty (the query names of the ample_beats tool), overview=1 for the lists, id=<id> for one entry (parts=<parts>), similar=<id> for the entries that look like it.", false, (HttpContext c) =>
        {
            JsonObject query = [];
            foreach ((string key, Microsoft.Extensions.Primitives.StringValues value) in c.Request.Query)
            {
                query[key] = value.ToString();
            }

            return Ok(Ample.Audio.Engine.BeatsCatalog.Run(query));
        });
        add("GET", "/api/sample-sources", "project", "Where real, legal samples can be found (a DJ scratch, a vinyl crackle, a foley sound) and how they come into a project correctly: the rules (CC0 or public domain by default, the source written down next to the file, nothing bought without the owner) and the places with their licence. text=words puts the places that fit first.", false, (HttpContext c) => Ok(Ample.Audio.Engine.SampleSources.Run(new JsonObject { ["text"] = c.Request.Query["text"].ToString() })));
        add("GET", "/api/ace/models", "plugins", "The ACE-Step model files on the local ACE server and on the Faer server, which file is on which, and whether the size matches (the same name and size count as the same file).", false, s is null ? null : async (HttpContext c) => Ok(await AceModels.ListAsync(s, c.RequestAborted)));
        add("GET", "/api/faer/status", "project", "Machine-local Faer connection, aggregate capability readiness and installed ACE model filenames, roles and sizes, without secrets or job content.", false, s is null ? null : async (HttpContext c) =>
        {
            Ample.Plugins.FaerProfile? profile = Ample.Plugins.FaerProfile.LoadLocal();
            if (profile is null) return Ok(new { configured = false, connected = false });
            try
            {
                using Ample.Plugins.FaerClient client = profile.CreateClient();
                JsonElement status = await client.StatusAsync(c.RequestAborted);
                return Ok(new { configured = true, connected = true, status });
            }
            catch (Exception ex) when (ex is HttpRequestException or CryptographicException)
            {
                return Ok(new { configured = true, connected = false });
            }
        });
        add("POST", "/api/project/settings", "project", "Changes AMPLE Redux retention, the default engine, independent Faer capability routes and buildParallelism (how many blocks a build renders at once: 1 to the limit of the server, or null for the server's number; the master seed is a dangerous act of its own).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            ProjectSettings old = m.Settings;
            BlackBoxSettings bb = new(b.TryGetProperty("blackBoxBudget", out JsonElement budget) ? budget.GetInt64() : old.BlackBox.BudgetBytes, b.TryGetProperty("fullDetailDays", out JsonElement full) ? full.GetInt32() : old.BlackBox.FullDetailDays, b.TryGetProperty("thinnedDays", out JsonElement thin) ? thin.GetInt32() : old.BlackBox.ThinnedDays);
            if (bb.BudgetBytes < 1024 * 1024 || bb.FullDetailDays < 1 || bb.ThinnedDays < bb.FullDetailDays)
            {
                throw new AmpleException("AMP5285", "The AMPLE Redux budget must be at least 1 MiB and the thinned period at least as long as the full detail period.");
            }

            ProjectPanelSettings panel = old.ProjectPanel;
            if (b.TryGetProperty("projectPanel", out JsonElement panelJson))
            {
                string[] allowed = ["name", "kind", "size", "length", "modified", "usedBy", "path", "sha256"];
                ProjectPanelColumn[] columns = panelJson.GetProperty("columns").EnumerateArray().Select(column => new ProjectPanelColumn(column.GetProperty("id").GetString()!, column.GetProperty("width").GetInt32())).ToArray();
                panel = new ProjectPanelSettings(panelJson.GetProperty("view").GetString()!, panelJson.GetProperty("tileSize").GetInt32(), panelJson.GetProperty("sortColumn").GetString()!, panelJson.GetProperty("sortDescending").GetBoolean(), columns);
                if (panel.View is not ("tiles" or "details") || panel.TileSize is < 0 or > 4 || !allowed.Contains(panel.SortColumn, StringComparer.Ordinal) || panel.Columns.Count is < 1 or > 8 || panel.Columns.Any(column => !allowed.Contains(column.Id, StringComparer.Ordinal) || column.Width is < 72 or > 640) || panel.Columns.Select(column => column.Id).Distinct(StringComparer.Ordinal).Count() != panel.Columns.Count)
                    throw new AmpleException("AMP5285", "The Project panel layout contains an unsupported view, column, width or size.");
            }

            FaerRoutes faer = old.Faer;
            if (b.TryGetProperty("faer", out JsonElement faerJson))
            {
                // the default models are kept when the request does not mention them, cleared when they are empty; a model that is not a family or a .gguf name is refused (AMP5383)
                faer = new FaerRoutes(faerJson.GetProperty("song").GetString()!, faerJson.GetProperty("voice").GetString()!, faerJson.GetProperty("separation").GetString()!) { SongModel = DefaultModel(faerJson, "songModel", "ace-music", old.Faer.SongModel), VoiceModel = DefaultModel(faerJson, "voiceModel", "ai-voice", old.Faer.VoiceModel) };
                if (new[] { faer.Song, faer.Voice }.Any(route => route is not ("local" or "faer" or "faer-else-local")) || faer.Separation is not ("local" or "faer")) throw new AmpleException("AMP5285", "Faer routes must be local or faer (the song and the voice may also be faer-else-local).");
            }

            KodaSettings koda = old.Koda;
            if (b.TryGetProperty("koda", out JsonElement kodaJson))
            {
                if (kodaJson.TryGetProperty("language", out JsonElement kodaLanguage))
                {
                    if (!KodaSettings.IsKnown(kodaLanguage.GetString())) throw new AmpleException("AMP5285", "Koda speaks en or nl.");
                    koda = koda with { Language = kodaLanguage.GetString()! };
                }

                // the model and the thinking level Koda starts with (explicitly chosen in its menu; empty: the setting of the assistant)
                if (kodaJson.TryGetProperty("model", out JsonElement kodaModel) && kodaModel.ValueKind == JsonValueKind.String)
                {
                    string model = kodaModel.GetString()!.Trim();
                    if (model.Length > 100 || model.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/'))) throw new AmpleException("AMP5285", "That is not a model name.");
                    koda = koda with { Model = model };
                }

                if (kodaJson.TryGetProperty("effort", out JsonElement kodaEffort) && kodaEffort.ValueKind == JsonValueKind.String)
                {
                    string effort = kodaEffort.GetString()!.Trim();
                    if (effort is not ("" or "default" or "minimal" or "low" or "medium" or "high" or "xhigh")) throw new AmpleException("AMP5285", "The thinking level is default, low, medium, high or xhigh.");
                    koda = koda with { Effort = effort };
                }
            }

            if (b.TryGetProperty("braveSearchApiKey", out JsonElement braveKey) && braveKey.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(braveKey.GetString())) AiProjectSecretStore.Save(s.Library.Home, m.Id, braveKey.GetString()!.Trim());
            if (b.TryGetProperty("clearBraveSearchApiKey", out JsonElement clearBraveKey) && clearBraveKey.ValueKind == JsonValueKind.True) AiProjectSecretStore.Clear(s.Library.Home, m.Id);
            AiAssistantSettings ai = old.Ai with { ExposeTakebankCache = b.TryGetProperty("exposeTakebankCache", out JsonElement exposeCache) ? exposeCache.GetBoolean() : old.Ai.ExposeTakebankCache };
            int? parallelism = old.BuildParallelism;
            if (b.TryGetProperty("buildParallelism", out JsonElement parallelismJson))
            {
                if (parallelismJson.ValueKind == JsonValueKind.Null) parallelism = null;
                else if (parallelismJson.ValueKind == JsonValueKind.Number && parallelismJson.TryGetInt32(out int asked) && asked >= 1 && asked <= Ample.Engine.Engine.BuildParallelism.ServerLimit) parallelism = asked;
                else throw new AmpleException("AMP5941", $"The build parallelism must be a whole number from 1 to {Ample.Engine.Engine.BuildParallelism.ServerLimit} (what this server allows), or null for the number of the server.");
            }

            await s.Bus.ExecuteAsync(new SetSettingsCommand(old with { BuildParallelism = parallelism, BlackBox = bb, DefaultEngine = b.TryGetProperty("defaultEngine", out JsonElement e) && e.ValueKind == JsonValueKind.String && e.GetString()!.Length > 0 ? e.GetString() : old.DefaultEngine, ProjectPanel = panel, Faer = faer, Koda = koda, Ai = ai }), c.RequestAborted);
            return Ok(new { saved = true });
        });
        add("POST", "/api/acts/master-seed", "acts", "Dry run of changing the master seed; returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            ChangeMasterSeedAct act = new(ulong.Parse(Str(await Body(c), "seed"), System.Globalization.CultureInfo.InvariantCulture));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/health/recover", "health", "Deals with an interrupted command: restore what it was about to overwrite (from its pre-image), or accept the file as it is.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            long seq = b.GetProperty("seq").GetInt64();
            InterruptedCommand item = (await s.Journal.FindInterruptedAsync(c.RequestAborted)).FirstOrDefault(i => i.Start.Seq == seq) ?? throw new AmpleException("AMP5231", $"There is no interrupted command with record {seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            if (Str(b, "action") == "restore")
            {
                await s.Bus.ExecuteAsync(new RestorePreImageCommand(item.Start), c.RequestAborted);
            }

            await s.Bus.ResolveAsync(item, Str(b, "action") == "restore" ? "restored from the pre-image in the UI" : "accepted as it is in the UI", c.RequestAborted);
            return Ok(new { resolved = seq });
        });
        add("POST", "/api/history/restore-line", "history", "Brings back a line that was dropped (it was only archived).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            await s.History.RestoreLineAsync(Str(b, "line"), Opt(b, "reason") ?? "restored in the UI", c.RequestAborted);
            events!.Publish("history.changed", new { kind = "restore-line" });
            return Ok(new { restored = Str(b, "line") });
        });
        add("GET", "/api/backup", "backup", "What the project remembers about its backups (target, last backup, age, whether one is made before dangerous acts) and the backups in that target.", false, s is null ? null : () =>
        {
            Ample.Storage.Backup.BackupStatus? status = Ample.Storage.Backup.BackupPolicy.Read(s.Root, s.Clock);
            IReadOnlyList<Ample.Storage.Backup.BackupManifest> list = status is not null && Directory.Exists(status.Target) ? new Ample.Storage.Backup.BackupStore(status.Target, s.Clock).List() : [];
            return Ok(new { status, suggestion = Ample.Storage.Backup.BackupPolicy.Suggestion(s.Root, s.Clock), backups = list.Select(m => new { m.Id, m.Created, m.ProjectName, files = m.Entries.Count, bytes = m.Entries.Sum(e => e.Size), m.IncludesTakes }) });
        });
        add("POST", "/api/backup/create", "backup", "Makes a backup (into the remembered target, or the target given).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = Obj(await Body(c));
            string target = Opt(b, "target") ?? Ample.Storage.Backup.BackupPolicy.Read(s.Root, s.Clock)?.Target ?? throw new AmpleException("AMP5285", "Say where the backups go (target).");
            Ample.Storage.Backup.BackupReport r = new Ample.Storage.Backup.BackupStore(target, s.Clock).Create(s.Root, s.Manifest.Id, s.Manifest.Name, !(b.TryGetProperty("includeTakes", out JsonElement t) && t.ValueKind == JsonValueKind.False), c.RequestAborted);
            Ample.Storage.Backup.BackupPolicy.Remember(s.Root, Path.GetFullPath(target), r.Id, s.Clock.UtcNow);
            return Ok(r);
        });
        add("POST", "/api/backup/verify", "backup", "Rehashes a backup (default: the latest in the remembered target).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = Obj(await Body(c));
            string target = Opt(b, "target") ?? Ample.Storage.Backup.BackupPolicy.Read(s.Root, s.Clock)?.Target ?? throw new AmpleException("AMP5285", "Say where the backups are (target).");
            return Ok(new Ample.Storage.Backup.BackupStore(target, s.Clock).Verify(Opt(b, "id") ?? "latest", c.RequestAborted));
        });
        add("POST", "/api/backup/policy", "backup", "Chooses the target and whether a backup is made before every dangerous act (takes effect the next time the server starts).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            Ample.Storage.Backup.BackupPolicy.SetAuto(s.Root, Path.GetFullPath(Str(b, "target")), !(b.TryGetProperty("auto", out JsonElement a) && a.ValueKind == JsonValueKind.False));
            return Ok(Ample.Storage.Backup.BackupPolicy.Read(s.Root, s.Clock)!);
        });
        add("POST", "/api/export", "backup", "Writes the project into an archive file (no secrets, a consistent copy of the database).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            try
            {
                Ample.Engine.Credits.CreditsService.Refresh(s.Root, s.Manifest.Name, s.Spec, s.Clock, s.Library);
            }
            catch (Exception ex) when (ex is IOException or AmpleException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The export goes on without a fresh credit sheet.
            }

            Ample.Storage.Backup.BackupManifest m = Ample.Storage.Backup.Archive.Export(s.Root, s.Manifest.Id, s.Manifest.Name, Path.GetFullPath(Str(b, "file")), !(b.TryGetProperty("includeTakes", out JsonElement t) && t.ValueKind == JsonValueKind.False), s.Clock);
            return Ok(new { file = Str(b, "file"), files = m.Entries.Count, bytes = m.Entries.Sum(e => e.Size) });
        });
        add("GET", "/api/library", "library", "The user's library: linked folders with their live state, the assets (up to 5000) and groups of duplicates.", false, s is null ? null : () => Ok(LibraryView(s)));
        add("POST", "/api/library/mounts", "library", "Links a folder and scans it (read-only unless readOnly is false; scope project makes it visible to this project only).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            bool project = (Opt(b, "scope") ?? "global") == "project";
            string path = Str(b, "path");
            Ample.Storage.Library.Mount m = s.Library.LinkMount(Opt(b, "name") ?? Path.GetFileName(path.TrimEnd('/', '\\')), path, project ? "project" : "global", project ? s.Manifest.Id.ToString("D") : null, !(b.TryGetProperty("readOnly", out JsonElement ro) && ro.ValueKind == JsonValueKind.False));
            if (project)
            {
                await AddProjectMount(s, m.Id, c.RequestAborted);
            }

            Ample.Storage.Library.ScanReport report = s.Library.Scan(m.Id, c.RequestAborted);
            events!.Publish("library.changed", new { kind = "link" });
            return Ok(new { mount = m, report, library = LibraryView(s) });
        });
        add("POST", "/api/library/scan", "library", "Scans a linked folder incrementally.", true, s is null ? null : async (HttpContext c) =>
        {
            Ample.Storage.Library.ScanReport report = s.Library.Scan(Str(await Body(c), "mount"), c.RequestAborted);
            events!.Publish("library.changed", new { kind = "scan" });
            return Ok(new { report, library = LibraryView(s) });
        });
        add("POST", "/api/library/relocate", "library", "Points a linked folder at its new place after an identity check (force skips the check). No song changes.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            Ample.Storage.Library.Mount m = s.Library.Relocate(Str(b, "mount"), Str(b, "path"), b.TryGetProperty("force", out JsonElement f) && f.ValueKind == JsonValueKind.True);
            Ample.Storage.Library.ScanReport report = s.Library.Scan(m.Id, c.RequestAborted);
            events!.Publish("library.changed", new { kind = "relocate" });
            return Ok(new { mount = m, report, library = LibraryView(s) });
        });
        add("POST", "/api/library/access", "library", "Makes a linked folder read-only or read-write.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            s.Library.SetReadOnly(Str(b, "mount"), !(b.TryGetProperty("readOnly", out JsonElement ro) && ro.ValueKind == JsonValueKind.False));
            events!.Publish("library.changed", new { kind = "access" });
            return Ok(LibraryView(s));
        });
        add("POST", "/api/library/describe", "library", "Sets tags, license and credit of an asset.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            s.Library.Describe(Str(b, "asset"), Opt(b, "tags"), Opt(b, "license"), Opt(b, "credit"));
            events!.Publish("library.changed", new { kind = "describe" });
            return Ok(LibraryView(s));
        });
        add("GET", "/api/library/audio", "library", "Streams the audio file of a library asset (a sample or loop in a linked folder) with range support, so the Library view can play it before it is used.", false, s is null ? null : (HttpContext c) =>
        {
            Ample.Storage.Library.AssetRecord asset = s.Library.Asset(c.Request.Query["asset"].ToString()) ?? throw new AmpleException("AMP5291", "There is no such asset.");
            Ample.Storage.Library.Mount mount = s.Library.GetMount(asset.MountId);
            string extension = Path.GetExtension(asset.RelPath).ToLowerInvariant();
            string contentType = extension switch { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".flac" => "audio/flac", ".aif" or ".aiff" => "audio/aiff", _ => string.Empty };
            string root = Path.GetFullPath(mount.Path), full = Path.GetFullPath(Path.Combine(root, asset.RelPath));
            if (mount.State != "online" || contentType.Length == 0 || !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                throw new AmpleException("AMP5243", "This asset has no audio that can be played.");
            }

            return Results.File(File.OpenRead(full), contentType, enableRangeProcessing: true);
        });
        add("POST", "/api/library/import", "library", "Copies an asset into the project (assets/) with a provenance file; the write is journaled in AMPLE Redux.", true, s is null ? null : async (HttpContext c) =>
        {
            string relative = await s.Library.ImportAsync(Str(await Body(c), "asset"), s.Root, s.Bus, c.RequestAborted);
            events!.Publish("library.changed", new { kind = "import" });
            return Ok(new { path = relative });
        });
        add("GET", "/api/bundles", "library", "The library bundles: blocks saved with their files, seeds and audio, with the slots a user may swap.", false, s is null ? null : () => Ok(new { bundles = Ample.Engine.Library.Bundles.List(s.Library.Home) }));
        add("POST", "/api/bundles/save", "library", "Saves blocks of the open song as a library bundle: their text with the seeds they have, the files they read and the audio of their takes.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            AmpleDocument parsed = s.ParseExpanded(d);
            List<string> names = [.. b.GetProperty("blocks").EnumerateArray().Select(x => x.GetString() ?? string.Empty)];
            if (b.TryGetProperty("includeInputs", out JsonElement closure) && closure.ValueKind == JsonValueKind.True)
            {
                for (int i = 0; i < names.Count; i++) foreach (FromLine from in parsed.Blocks.FirstOrDefault(x => x.Name == names[i])?.Froms ?? []) if (!names.Contains(from.Block)) names.Add(from.Block);
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            BuildReport status = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, control, null, null) { PlanOnly = true }, c.RequestAborted);
            ulong master = (ulong)(parsed.Project?.MasterSeed ?? 0);
            Ample.Engine.Library.BundleInfo info = Ample.Engine.Library.Bundles.Save(s.Library.Home, s.Root, s.AmpleRoot, parsed, s.Spec, names, Str(b, "name"), Opt(b, "description") ?? string.Empty, block => control.SeedFor(block) ?? Ample.Audio.Random.SeedDeriver.Derive(master, "block", block, "seed", "seed"), block =>
            {
                string? takeId = status.Blocks.FirstOrDefault(r => r.Block == block && r.Status is "cached" or "frozen" or "adopted")?.TakeId;
                if (takeId is null) return null;
                TakeRecord take = s.Bank.GetTake(takeId);
                List<(string Role, byte[] Data)> outputs = [];
                foreach (TakeDataRef data in take.Data)
                {
                    using Stream stream = s.Bank.OpenData(data.DataId);
                    using MemoryStream copy = new();
                    stream.CopyTo(copy);
                    outputs.Add((data.Role, copy.ToArray()));
                }

                return outputs;
            }, s.Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            events!.Publish("library.changed", new { kind = "bundle" });
            return Ok(new { bundle = info, withAudio = info.Takes.Select(t => t.Block).Distinct().Count(), without = info.Blocks.Count - info.Takes.Select(t => t.Block).Distinct().Count() });
        });
        add("POST", "/api/bundles/place", "library", "Adds a library bundle to the open song under new names (a prefix or a name per block), copies the files it needs and gives the blocks the audio they were saved with.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            AmpleDocument parsed = s.Parse(d);
            Dictionary<string, string> Map(string name) => b.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty) : [];
            string id = Str(b, "id");
            Ample.Engine.Library.BundlePlacement placement = Ample.Engine.Library.Bundles.Place(s.Library.Home, id, s.Root, s.Spec, [.. parsed.Blocks.Select(x => x.Name)], Opt(b, "prefix") ?? string.Empty, Map("names"), Map("slots"), b.TryGetProperty("useExisting", out JsonElement keep) && keep.ValueKind == JsonValueKind.True);
            EditResult result = Edits.InsertBlocks(parsed, placement.Text);
            if (!result.Ok) throw new AmpleException("AMP5286", result.Error!);
            foreach (Ample.Engine.Library.BundleFileWrite file in placement.Files) await s.Bus.ExecuteAsync(new ImportFileCommand(file.Path, file.Data), c.RequestAborted).ConfigureAwait(false);
            if (placement.Files.Count > 0) events!.Publish("files.changed", new { paths = placement.Files.Select(f => f.Path).ToArray() });
            lock (d)
            {
                d.Text = Edits.Apply(d.Text, result.Edits);
                d.Version++;
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            // the saved audio becomes the take of each block that still is what was saved
            List<string> adopted = [];
            AmpleDocument after = s.ParseExpanded(d);
            if (placement.Seeds.Count > 0)
            {
                // the seeds the blocks had when they were saved, so the new names sound the same
                IReadOnlyList<IBlockRenderer> seedRenderers = await s.RenderersAsync(c.RequestAborted);
                ControlService seedControl = s.NewControl(after, seedRenderers);
                foreach ((string block, ulong value) in placement.Seeds)
                {
                    seedControl.GetSeed(block, "seed");   // makes the row; a new seed starts locked
                    await seedControl.UnlockSeedAsync(block, "seed");
                    await seedControl.SetSeedAsync(block, "seed", value, "placed from the bundle " + id);
                    await seedControl.LockSeedAsync(block, "seed");
                }
            }

            if (placement.Adoptable.Count > 0 && !after.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                IReadOnlyDictionary<string, IReadOnlyList<RenderOutput>> saved = Ample.Engine.Library.Bundles.LoadTakes(s.Library.Home, id, placement.Names);
                IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
                ControlService control = s.NewControl(after, renderers);
                foreach (string name in after.Blocks.Select(x => x.Name).Where(placement.Adoptable.Contains))
                {
                    BuildReport report = await s.Engine.BuildAsync(after, new BuildOptions("bake", null, name, 0, renderers, null, control, null, null) { Adopt = block => saved.GetValueOrDefault(block) }, c.RequestAborted);
                    if (report.Blocks.Any(r => r.Block == name && r.Status is "adopted" or "cached")) adopted.Add(name);
                }

            }

            return Ok(new { names = placement.Names, filesAdded = placement.Files.Select(f => f.Path), conflicts = placement.Conflicts, adopted, openInputs = placement.OpenInputs, doc = DocView(d, s.Parse(d), s) });
        });
        add("DELETE", "/api/bundles/{id}", "library", "Removes a bundle from the library (the songs that used it keep their blocks).", true, s is null ? null : (string id) =>
        {
            Ample.Engine.Library.Bundles.Delete(s.Library.Home, id);
            events!.Publish("library.changed", new { kind = "bundle" });
            return Ok(new { id, deleted = true });
        });
        add("GET", "/api/library/uses", "library", "Where the open song uses catalog assets (block and parameter).", false, s is null ? null : async (HttpContext c) =>
        {
            AmpleDocument doc = s.Parse(await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted));
            return Ok(s.Library.Uses(doc).Select(u => new { u.AssetUuid, u.Block, u.Parameter, u.Line, asset = s.Library.Asset(u.AssetUuid) }));
        });
        add("GET", "/api/lock", "library", "ample.lock checked against the catalog: ok, changed, missing or offline per entry.", false, s is null ? null : () =>
        {
            string file = Path.Combine(s.Root, Ample.Engine.Library.LockFile.FileName);
            return Ok(new { exists = File.Exists(file), checks = File.Exists(file) ? Ample.Engine.Library.LockFile.Verify(s.Root, s.Library, Path.Combine(s.Root, "plugins")) : [] });
        });
        add("POST", "/api/lock/update", "library", "Works out what the songs use and writes ample.lock (accepts changed assets).", true, s is null ? null : async (HttpContext c) =>
        {
            List<AmpleDocument> songs = [];
            List<Ample.Language.ModuleUsage> modules = [];
            Ample.Engine.Modules.ModuleService service = new(s.Root, s.Library, s.Manifest.Id.ToString("D"));
            foreach (string f in Directory.EnumerateFiles(s.Root, "*.amplet", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ample" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                Ample.Language.ExpandedSong expanded = service.Expand((await s.DocAsync(Path.GetRelativePath(s.Root, f).Replace('\\', '/'), c.RequestAborted)).Text, s.Spec);
                songs.Add(Ample.Language.Parser.Parse(expanded.Text, s.Spec));
                modules.AddRange(expanded.LockUsages);
            }

            string text = await Ample.Engine.Library.LockFile.UpdateAsync(s.Root, s.Library, songs, Path.Combine(s.Root, "plugins"), s.Bus, modules, c.RequestAborted);
            events!.Publish("library.changed", new { kind = "lock" });
            return Ok(new { entries = Ample.Engine.Library.LockFile.Parse(text) });
        });

        // --- components ---
        add("GET", "/api/components", "components", "The components a song can see (its own and the imported ones) with every version, its seal and signature, the problems of putting them together, and the .amplec files that can be imported.", false, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.ListAsync(s, c.Request.Query["path"].ToString(), c.RequestAborted)));
        add("GET", "/api/components/graph", "components", "The internal graph of an instance of an Audio component (path of the song, block): its processors with parameters and links, the blocks they become, the latency and whether the browser can preview it.", false, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.GraphAsync(s, c.Request.Query["path"].ToString(), c.Request.Query["block"].ToString(), c.RequestAborted)));
        add("POST", "/api/components/graph-values", "components", "The internal graph of an Audio instance with property values that are not in the song yet (path, block, values: name to text): the same answer as the graph route, for a knob that is being moved. Nothing is written or built; the live preview sets its real-time chain from it.", false, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); Dictionary<string, string> values = b.TryGetProperty("values", out JsonElement v) && v.ValueKind == JsonValueKind.Object ? v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? string.Empty, StringComparer.Ordinal) : []; return Ok(await ComponentsApi.GraphAsync(s, Str(b, "path"), Str(b, "block"), c.RequestAborted, values)); });
        add("GET", "/api/components/files", "components", "The .amplec files of the project.", false, s is null ? null : () => Ok(ComponentsApi.Files(s)));
        add("GET", "/api/components/library", "components", "The library of the project (path of the song, refresh=true to read every file again): every component of every .amplec file with friendly name, description, tags, kind, versions and seal, whether the song imports it, and how many files were read now and how many came from the cache.", false, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.LibraryAsync(s, c.Request.Query["path"].ToString(), c.Request.Query["refresh"].ToString() == "true", c.RequestAborted)));
        add("POST", "/api/components/import", "components", "What adding a component file to a song does (path of the song, file, name, add): the import line and optionally an instance, the new text and the conflicts (AMP2212 to AMP2214). Nothing is written; the client applies the text.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.ImportAsync(s, await Body(c), c.RequestAborted)));
        add("POST", "/api/components/complete", "components", "The suggestions at a place in the text of a component file (text, line and character, 0-based): attributes, statements, types, the words of the ui block, processors after |>. Nothing is read or written; used by Ctrl+Space in the editor of a .amplec file.", false, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(ComponentsApi.Complete(s, Str(b, "text"), b.TryGetProperty("line", out JsonElement l) ? l.GetInt32() : 0, b.TryGetProperty("character", out JsonElement ch) ? ch.GetInt32() : 0)); });
        add("POST", "/api/components/check-text", "components", "The problems of the text of a component file (text): the diagnostics of the parser as the language server gives them (0-based ranges). Nothing is read or written; used while a .amplec file is typed.", false, s is null ? null : async (HttpContext c) => Ok(ComponentsApi.CheckText(s, Str(await Body(c), "text"))));
        add("POST", "/api/components/seal", "components", "Seals a version of a component (path of the song or file of the project, name, version): the hashes are written as a seal block. A song gets the new text back (nothing is written); a .amplec file is written through the command bus.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.EditAsync(s, "seal", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/unseal", "components", "Removes the seal of a version of a component, so that it can be changed again.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.EditAsync(s, "unseal", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/new-version", "components", "Makes a working copy of a version with a new version number (newVersion), without its seal; the original stays as it is.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.EditAsync(s, "new-version", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/embed-preview", "components", "Says what embedding components in a song would do (path, blocks or components, whole): the closure of definitions that would be written, the ones that are there already and the ones with other code.", false, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "embed-preview", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/embed", "components", "Embeds the dependency closure of some instances (blocks), the named components (components) or whole source modules (whole) in the song, inside an embedded module; the song gets the new text back.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "embed", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/use-template", "components", "Uses the template source again for an embedded component, only when id, version and payload are exactly the same; otherwise it is refused.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "use-template", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/extract", "components", "Extracts an embedded component to a new .amplec file of the project (file) and makes the song import it; identity, version and seal are kept.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "extract", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/detach", "components", "Detaches a sealed embedded component for editing: its seal is removed and it builds as a development version.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "detach", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/migrate-preview", "components", "Says what migrating an instance (block) to a version (target: 4.0.0, or id@version for another component) would do: the chain, the impact, the changed properties and ports, the notes before and after. Nothing is changed.", false, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "migrate-preview", await Body(c), c.RequestAborted)));
        add("POST", "/api/components/migrate", "components", "Migrates an instance to a version. A migration with an impact above equivalent is applied only with acknowledge true; the song gets the new text back.", true, s is null ? null : async (HttpContext c) => Ok(await ComponentsApi.SourceAsync(s, "migrate", await Body(c), c.RequestAborted)));

        // --- documents ---
        add("GET", "/api/templates", "documents", "The block templates, port types and classes of the language (the palette is generated from this).", false, s is null ? null : () =>
        {
            Dictionary<string, string> categories = [];
            string dir = Path.Combine(s.AmpleRoot, "spec", "templates");
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    using JsonDocument t = JsonDocument.Parse(File.ReadAllText(file));
                    if (t.RootElement.TryGetProperty("id", out JsonElement id) && t.RootElement.TryGetProperty("category", out JsonElement cat))
                    {
                        categories[id.GetString()!] = cat.GetString()!;
                    }
                }
            }

            return Ok(new
            {
                version = s.Spec.Version,
                templates = s.Spec.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => new { t.Id, t.Title, category = categories.GetValueOrDefault(t.Id, "Other"), t.Class, t.Version, t.Dynamic, t.Plugin, t.Description, inputs = t.Inputs, controlInputs = t.ControlInputs, outputs = t.Outputs, parameters = t.ParameterSpecs.Values.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new { p.Name, p.Label, p.Type, @default = p.Default, documentation = p.Documentation, p.ModalEditor, p.Multiline, p.Kind, p.Options, p.Min, p.Max, p.Step, p.Unit, p.Placeholder, p.Accept, p.Count, p.Automatable }) }),
            });
        });
        add("POST", "/api/doc/parse", "documents", "Parses text that is not open (for example an older state read with peek) and returns the same view as a document, without opening or changing anything.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string text = Str(b, "text");
            DocSession temp = new(Opt(b, "path") ?? "peek", text);
            return Ok(DocView(temp, s.Parse(temp), s));
        });
        add("GET", "/api/docs", "documents", "The song files of the project.", false, s is null ? null : () => Ok(SongFiles(s.Root).Select(f => Path.GetRelativePath(s.Root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)));
        add("GET", "/api/doc", "documents", "Opens a song: text, version, diagnostics and blocks.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            ProjectSession.DiskSync sync = await s.SyncDiskAsync(d, c.RequestAborted);
            if (sync.State is "reloaded" or "merged")
            {
                events!.Publish("doc.changed", new { d.Path, d.Version });
            }

            return Ok(DocView(d, s.Parse(d), s));
        });
        add("GET", "/api/wc", "documents", "The working copy of a text file that is not a song (path): its text, the saved text, the version and whether it has unsaved changes. Without a path: the files with unsaved changes. A build, check or play of a song reads these working copies instead of the disk for the components and modules it uses.", false, s is null ? null : async (HttpContext c) =>
        {
            string path = c.Request.Query["path"].ToString();
            if (path.Length == 0) return Ok(new { dirty = s.Texts.Dirty() });
            (string text, string saved, long version, bool dirty) = s.Texts.Get(WorkingPath(path));
            return await Task.FromResult(Ok(new { path, text, saved, version, dirty }));
        });
        add("PUT", "/api/wc", "documents", "Sets the working copy of a text file that is not a song (path, text): it is not saved, but builds of songs that use the file read it.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string path = WorkingPath(Str(b, "path"));
            (long version, bool dirty, string saved) = s.Texts.Set(path, Str(b, "text"));
            return await Task.FromResult(Ok(new { path, version, dirty, saved }));
        });
        add("POST", "/api/wc/save", "documents", "Writes the working copy of a text file to the file through the command bus (path).", true, s is null ? null : async (HttpContext c) =>
        {
            string path = WorkingPath(Str(await Body(c), "path"));
            await s.Texts.SaveAsync(path, c.RequestAborted);
            (string text, string saved, long version, bool dirty) = s.Texts.Get(path);
            return Ok(new { path, version, dirty, saved });
        });
        add("POST", "/api/wc/revert", "documents", "Throws the unsaved changes of a text file away (path): the working copy is the file again.", true, s is null ? null : async (HttpContext c) =>
        {
            string path = WorkingPath(Str(await Body(c), "path"));
            (string text, long version) = s.Texts.Revert(path);
            return await Task.FromResult(Ok(new { path, text, version, dirty = false }));
        });
        add("GET", "/api/tabs/state", "documents", "The state of open files for the tab row (paths, comma separated): whether each exists, has unsaved changes, and for a song its errors, warnings and whether the text can be read into blocks.", false, s is null ? null : async (HttpContext c) =>
        {
            List<object> items = [];
            foreach (string raw in c.Request.Query["paths"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(200))
            {
                string path = WorkingPath(raw);
                bool exists = File.Exists(ProjectPath.Resolve(s.Fs, s.Root, path));
                if (path.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) && exists)
                {
                    DocSession d = await s.DocAsync(path, c.RequestAborted);
                    AmpleDocument parsed = s.Parse(d);
                    items.Add(new { path, exists, dirty = d.Text != d.Saved, errors = parsed.Diagnostics.Count(x => x.Severity == Severity.Error), warnings = parsed.Diagnostics.Count(x => x.Severity == Severity.Warning), graphBroken = DiagnosticKinds.GraphBroken(parsed) });
                }
                else items.Add(new { path, exists, dirty = s.Texts.Read(path) is not null, errors = 0, warnings = 0, graphBroken = false });
            }

            return Ok(new { items });
        });
        add("GET", "/api/doc/saved", "documents", "The saved text of a song (the last text written to its file or read from it), next to the working copy that GET /api/doc gives: what a person or Koda needs to see what changed without saving.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(WorkingPath(c.Request.Query["path"].ToString()), c.RequestAborted);
            return Ok(new { path = d.Path, saved = d.Saved, dirty = d.Text != d.Saved });
        });
        add("GET", "/api/doc/disk", "documents", "Compares the working copy of a song with its file on disk. state: same, reloaded or merged (already dealt with), conflict (regions lists the stretches both sides share and the ones to choose from: base, mine, theirs = the file) or gone.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            ProjectSession.DiskSync sync = await s.SyncDiskAsync(d, c.RequestAborted);
            if (sync.State is "reloaded" or "merged")
            {
                events!.Publish("doc.changed", new { d.Path, d.Version });
            }

            object[] regions = sync.State != "conflict" ? [] : [.. AiDiff.Regions(d.Saved, d.Text, sync.DiskText!).Select(r => r.Conflict ? (object)new { conflict = true, @base = r.Base, mine = r.Mine, theirs = r.Theirs } : new { conflict = false, common = r.Common })];
            return Ok(new { state = sync.State, version = d.Version, diskSha = sync.DiskText is null ? null : ProjectSession.DiskSha(sync.DiskText), regions });
        });
        add("POST", "/api/doc/resolve", "documents", "Answers a conflict between the working copy and the file on disk: text is the composed result (per region the file's lines, the working copy's, both, or your own), diskSha the one the conflict reported. The file becomes the saved base, the working copy the text; save it as usual.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            Check(d, b);
            await s.ResolveDiskAsync(d, Str(b, "text"), Str(b, "diskSha"), c.RequestAborted);
            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("GET", "/api/doc/expanded", "documents", "Returns the module-expanded graph for nested module navigation without changing the open song.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            return Ok(DocView(d, s.ParseExpanded(d), s));
        });
        add("GET", "/api/module/doc", "modules", "Opens the editable block body of a project module.", false, s is null ? null : async (HttpContext c) =>
        {
            ModuleDocState state = await s.ModuleDocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            return Ok(ModuleDocView(state, s));
        });
        add("GET", "/api/module/source", "modules", "Opens the complete source text of a project module.", false, s is null ? null : async (HttpContext c) =>
        {
            ModuleDocState state = await s.ModuleDocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            return Ok(new { path = state.Document.Path, version = state.Document.Version, text = state.Source, facts = Ample.Audio.Midi.MidiModuleFacts.FromSource(state.Source).UnsupportedFeatures });
        });
        add("PUT", "/api/module/source", "modules", "Updates complete module source in the working copy with optimistic concurrency.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ModuleDocState state = await s.ModuleDocAsync(Str(b, "path"), c.RequestAborted);
            Check(state.Document, b);
            Ample.Language.ModuleDefinition parsed = Ample.Language.ModuleDefinition.Parse(Str(b, "text"));
            if (parsed.Problems.Count > 0) throw new AmpleException("AMP5326", "The project module source is malformed.");
            lock (state)
            {
                Check(state.Document, b);
                state.Source = Str(b, "text");
                state.Document.Text = parsed.Body;
                state.Document.Version++;
            }
            events!.Publish("doc.changed", new { state.Document.Path, state.Document.Version });
            return Ok(ModuleDocView(state, s));
        });
        add("PUT", "/api/module/doc", "modules", "Updates a project module body in its working copy with optimistic concurrency.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ModuleDocState state = await s.ModuleDocAsync(Str(b, "path"), c.RequestAborted);
            Check(state.Document, b);
            lock (state)
            {
                Check(state.Document, b);
                ProjectSession.UpdateModuleDoc(state, Str(b, "text"));
            }
            events!.Publish("doc.changed", new { state.Document.Path, state.Document.Version });
            return Ok(ModuleDocView(state, s));
        });
        add("POST", "/api/module/doc/edit", "modules", "Applies one typed graph edit to a project module body.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ModuleDocState state = await s.ModuleDocAsync(Str(b, "path"), c.RequestAborted);
            Check(state.Document, b);
            AmpleDocument parsed = s.Parse(state.Document);
            JsonElement op = b.GetProperty("op");
            EditResult result = Str(op, "type") switch
            {
                "addBlock" => Edits.AddBlock(parsed, s.Spec, Str(op, "blockType"), Str(op, "name"), Opt(op, "version")),
                "removeBlock" => Edits.RemoveBlock(parsed, Str(op, "block")),
                "connect" => Edits.Connect(parsed, s.Spec, Str(op, "from"), Str(op, "output"), Str(op, "to"), Str(op, "input")),
                "disconnect" => Edits.Disconnect(parsed, Str(op, "to"), Str(op, "input")),
                "setConnectionDisabled" => Edits.SetConnectionDisabled(parsed, Str(op, "to"), Str(op, "input"), op.GetProperty("disabled").GetBoolean()),
                "rename" => Edits.RenameBlock(parsed, Str(op, "block"), Str(op, "newName")),
                "setParameter" => Edits.SetParameter(parsed, Str(op, "block"), Str(op, "name"), FileParameterValue(s, parsed, Str(op, "block"), Str(op, "name"), Str(op, "value"))),
                "bakeCallSite" => Edits.BakeCallSite(parsed, op.GetProperty("line").GetInt32(), op.GetProperty("col").GetInt32(), s.LastBuildAt.TryGetValue(Str(b, "path"), out DateTime callAt) ? callAt : parsed.ParsedAt),
                "bakeDynamicHelpers" => Edits.BakeDynamicHelpers(parsed, s.LastBuildAt.TryGetValue(Str(b, "path"), out DateTime builtAt) ? builtAt : parsed.ParsedAt, op.TryGetProperty("block", out JsonElement bakeBlock) && bakeBlock.ValueKind == JsonValueKind.String ? bakeBlock.GetString() : null),
                "setPropertyValue" => Edits.SetPropertyValue(parsed, Str(op, "block"), Str(op, "name"), op.TryGetProperty("value", out JsonElement pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()! : string.Empty, Str(op, "mode")),
                "removeParameter" => Edits.RemoveParameter(parsed, Str(op, "block"), Str(op, "name")),
                "addFlag" => Edits.AddFlag(parsed, Str(op, "block"), Str(op, "flag")),
                "removeFlag" => Edits.RemoveFlag(parsed, Str(op, "block"), Str(op, "flag")),
                "setFreeze" => Edits.SetFreeze(parsed, Str(op, "block"), Str(op, "level")),
                "setLayout" => Edits.SetLayout(parsed, Str(op, "block"), op.GetProperty("x").GetInt32(), op.GetProperty("y").GetInt32()),
                "setParts" => Edits.SetParts(parsed, Str(op, "block"), op.GetProperty("parts").EnumerateArray().Select(p => new Part(p.GetProperty("at").GetInt32(), p.GetProperty("length").GetInt32(), p.TryGetProperty("name", out JsonElement pn) ? pn.GetString() ?? string.Empty : string.Empty)).ToArray()),
                _ => new EditResult([], "Unknown module edit type."),
            };
            if (!result.Ok) throw new AmpleException("AMP5286", result.Error!);
            lock (state)
            {
                Check(state.Document, b);
                if (!string.Equals(state.Document.Text, parsed.Text, StringComparison.Ordinal)) throw new AmpleException("AMP5280", "The module changed while the edit was prepared; try again.");
                ProjectSession.UpdateModuleDoc(state, Edits.Apply(state.Document.Text, result.Edits));
            }
            events!.Publish("doc.changed", new { state.Document.Path, state.Document.Version });
            return Ok(ModuleDocView(state, s));
        });
        add("POST", "/api/module/doc/save", "modules", "Writes the module body through the project command bus.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ModuleDocState state = await s.ModuleDocAsync(Str(b, "path"), c.RequestAborted);
            await s.Bus.ExecuteAsync(new WriteFileCommand(state.Document.Path, state.Source), c.RequestAborted);
            state.Document.Saved = state.Document.Text;
            return Ok(ModuleDocView(state, s));
        });
        add("PUT", "/api/doc", "documents", "Applies new text to the working copy (not saved) with optimistic concurrency by version.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            Check(d, b);
            lock (d)
            {
                Check(d, b);
                d.Text = Str(b, "text");
                d.Version++;
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/edit", "documents", "Applies a graph edit (set parameter, add or remove block, connect, disconnect, rename, flag) as a text edit.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            Check(d, b);
            AmpleDocument parsed = s.Parse(d);
            JsonElement op = b.GetProperty("op");
            EditResult result = RunDocEdit(s, parsed, b, op);
            if (!result.Ok)
            {
                throw new AmpleException("AMP5286", result.Error!);
            }

            lock (d)
            {
                Check(d, b);
                if (!string.Equals(d.Text, parsed.Text, StringComparison.Ordinal))
                {
                    throw new AmpleException("AMP5280", "The document changed while the edit was prepared; try again.");
                }

                d.Text = Edits.Apply(d.Text, result.Edits);
                d.Version++;
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/edits", "documents", "Applies a list of graph edits to one version of the text, all of them or none: when one is refused nothing changes. One version step.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            Check(d, b);
            string text = d.Text;
            int index = 0;
            foreach (JsonElement op in b.GetProperty("ops").EnumerateArray())
            {
                index++;
                AmpleDocument step = Parser.Parse(text, s.Spec);
                EditResult result = RunDocEdit(s, step, b, op);
                if (!result.Ok)
                {
                    throw new AmpleException("AMP5286", $"Edit {index} of the list was refused, so nothing changed: {result.Error}");
                }

                text = Edits.Apply(text, result.Edits);
            }

            lock (d)
            {
                Check(d, b);
                if (!string.Equals(d.Text, text, StringComparison.Ordinal))
                {
                    d.Text = text;
                    d.Version++;
                }
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/layouts", "documents", "Sets the place of many blocks in one request (Auto format, align, distribute): items is a list of block, x, y.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            Check(d, b);
            AmpleDocument parsed = s.Parse(d);
            List<(string, int, int)> places = [.. b.GetProperty("items").EnumerateArray().Select(i => (Str(i, "block"), i.GetProperty("x").GetInt32(), i.GetProperty("y").GetInt32()))];
            EditResult result = Edits.SetLayouts(parsed, places);
            if (!result.Ok)
            {
                throw new AmpleException("AMP5286", result.Error!);
            }

            lock (d)
            {
                Check(d, b);
                if (!string.Equals(d.Text, parsed.Text, StringComparison.Ordinal))
                {
                    throw new AmpleException("AMP5280", "The document changed while the edit was prepared; try again.");
                }

                d.Text = Edits.Apply(d.Text, result.Edits);
                d.Version++;
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/save", "documents", "Writes the working copy to the file through the command bus (AMPLE Redux keeps the old text).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = Obj(await Body(c));
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            ProjectSession.DiskSync sync = await s.SyncDiskAsync(d, c.RequestAborted);
            if (sync.State == "conflict")
            {
                throw new AmpleException("AMP5307", "The file changed on disk and your working copy changed the same place: nothing was saved. Choose per difference (GET /api/doc/disk, then POST /api/doc/resolve), then save again.");
            }

            if (sync.State is "reloaded" or "merged")
            {
                events!.Publish("doc.changed", new { d.Path, d.Version });
            }

            await s.AutoCheckpoint!.SaveAsync(d, "Saved", b.TryGetProperty("suppressCheckpoint", out JsonElement suppress) && suppress.ValueKind == JsonValueKind.True, c.RequestAborted);
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("GET", "/api/project/auto-checkpoint", "project", "The autosave setting (in project.json): whether every save gets checkpoints, and the autosave interval in minutes (with the allowed steps).", false, s is null ? null : () =>
        {
            AutoCheckpointSettings config = AutoCheckpoint.Read(s.Root);
            return Ok(new { enabled = config.Enabled, minutes = config.Minutes, steps = AutoCheckpointSettings.Steps });
        });
        add("POST", "/api/project/auto-checkpoint", "project", "Turns the checkpoint-on-save and autosave setting on or off and sets its interval (one of the steps).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            AutoCheckpointSettings old = m.Settings.AutoCheckpoint;
            AutoCheckpointSettings next = new(b.TryGetProperty("enabled", out JsonElement e) ? e.GetBoolean() : old.Enabled, b.TryGetProperty("minutes", out JsonElement mi) ? mi.GetInt32() : old.Minutes);
            if (!AutoCheckpointSettings.Steps.Contains(next.Minutes))
            {
                throw new AmpleException("AMP5285", "The autosave interval must be one of: " + string.Join(", ", AutoCheckpointSettings.Steps) + " minutes.");
            }

            await s.Bus.ExecuteAsync(new SetSettingsCommand(m.Settings with { AutoCheckpoint = next }), c.RequestAborted);
            return Ok(new { enabled = next.Enabled, minutes = next.Minutes, steps = AutoCheckpointSettings.Steps });
        });
        add("GET", "/api/tx/export/preview", "package", "What an .ampletx export would do (Full or Derived): the files that go in with their hashes, the Derived boundaries and what stays editable, the warnings and the approximate size. Nothing is written. The song must be saved and its final result must build.", false, s is null ? null : async (HttpContext c) =>
        {
            TxPrepared prepared = await TxExport.PrepareAsync(s, TxRequest(c.Request.Query["path"].ToString(), c.Request.Query["mode"].ToString(), c.Request.Query["master"].ToString() != "false", c.Request.Query["compress"].ToString() == "true", null, null, c.Request.Query["whole"].ToString() == "true"), c.RequestAborted);
            try { return Ok(TxExport.Impact(s, prepared)); }
            finally { try { if (Directory.Exists(prepared.TempFolder)) Directory.Delete(prepared.TempFolder, true); } catch (IOException) { /* removed with .ample/tmp later */ } }
        });
        add("POST", "/api/tx/export", "package", "Writes an .ampletx package (Full or Derived) of a saved song into the project; an existing package is never overwritten. With compress the same file is also put in a zip.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            TxPrepared prepared = await TxExport.PrepareAsync(s, TxRequest(Str(b, "path"), Opt(b, "mode") ?? "full", !b.TryGetProperty("includeMaster", out JsonElement im) || im.ValueKind != JsonValueKind.False, b.TryGetProperty("compress", out JsonElement cm) && cm.ValueKind == JsonValueKind.True, Opt(b, "folder"), Opt(b, "name"), b.TryGetProperty("wholeSets", out JsonElement ws) && ws.ValueKind == JsonValueKind.True, b.TryGetProperty("flac", out JsonElement fl) && fl.ValueKind == JsonValueKind.True, Opt(b, "flacApproval")), c.RequestAborted);
            JsonObject written = await TxExport.WriteAsync(s, prepared, c.RequestAborted);
            written["impact"] = TxExport.Impact(s, prepared);
            return Ok(written);
        });
        add("POST", "/api/tx/flac/preview", "package", "The listening test of the FLAC option of a Derived export: the result built with the original audio inputs (before) and with the audio inputs that went through FLAC (after), and how each audio input came back. Nothing is written to the project.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            await TxFlac.EnsureAvailableAsync(c.RequestAborted);
            TxPrepared prepared = await TxExport.PrepareAsync(s, TxRequest(Str(b, "path"), "derived", true, false, null, null, b.TryGetProperty("wholeSets", out JsonElement w2) && w2.ValueKind == JsonValueKind.True), c.RequestAborted);
            FlacPreview test = await TxFlac.PreviewAsync(s, prepared, c.RequestAborted);
            return Ok(new { test.Id, test.MastersIdentical, stems = test.Stems.Select(x => new { x.File, x.Supported, x.IdenticalFile, x.IdenticalSamples, x.WavSize, x.FlacSize }) });
        });
        add("GET", "/api/tx/flac/audio", "package", "Streams one of the two masters of a FLAC listening test (which=before or after).", false, s is null ? null : (HttpContext c) =>
        {
            FlacPreview test = TxFlac.Get(c.Request.Query["id"].ToString());
            return Results.File(c.Request.Query["which"].ToString() == "after" ? test.After : test.Before, "audio/wav", enableRangeProcessing: true);
        });
        add("POST", "/api/tx/flac/approve", "package", "Records that the user listened to the FLAC test and accepts the result; the export with FLAC needs it.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(new { id = TxFlac.Approve(Str(b, "id")).Id, approved = true });
        });
        add("GET", "/api/tx/inspect", "package", "Opens an .ampletx package read-only (a plain package or one in a zip, in the project or at an absolute path): what it is, its settings and dependencies, its files with their hashes, the song text and whether a master preview is inside. Nothing is changed.", false, s is null ? null : (HttpContext c) => Ok(TxImport.Inspect(TxImport.Open(s, c.Request.Query["path"].ToString()))));
        add("GET", "/api/tx/master", "package", "Streams the master preview of a package (taken out of it into a temporary file, nothing is built).", false, s is null ? null : (HttpContext c) =>
        {
            Ample.Storage.Package.TxPackage package = TxImport.Open(s, c.Request.Query["path"].ToString());
            return Results.File(TxImport.MasterFile(s, package), "audio/wav", enableRangeProcessing: true);
        });
        add("POST", "/api/tx/import/plan", "package", "What importing a package would do, before anything is written: the differences with this environment and what they can mean, and for every file whether it is already there (same SHA-256), can be relinked, or is extracted (and where).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(TxImport.Plan(s, TxImport.Open(s, Str(b, "path")), TxImportRequestOf(b)));
        });
        add("POST", "/api/tx/import/apply", "package", "Makes an editable project of a package: into a project (anyway or convert) or as a new project with a new identity. The package is only read; nothing existing is overwritten.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(await TxImport.ApplyAsync(s, TxImport.Open(s, Str(b, "path")), TxImportRequestOf(b), c.RequestAborted));
        });
        add("GET", "/api/ai/context/project", "ai", "Read-only context for an AI task: what the project is, its linked sample-pack catalogs, and a searchable, filterable, paged list of its files.", false, s is null ? null : (HttpContext c) => Ok(AiService.WithAssistantInfo(c.Request.Headers["X-Ample-Context"].ToString(), AiContext.Project(s, c.Request.Query["filesQuery"].ToString(), c.Request.Query["filesKind"].ToString(), c.Request.Query["filesExt"].ToString(), (int)Num(c, "filesOffset"), string.IsNullOrEmpty(c.Request.Query["filesLimit"].ToString()) ? null : (int)Num(c, "filesLimit")))));
        add("GET", "/api/ai/context/faer", "ai", "Read-only context for an AI task: independent project Faer routes, aggregate capability readiness and installed ACE model filenames, roles and sizes, without credentials or job content.", false, s is null ? null : async (HttpContext c) =>
        {
            ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), c.RequestAborted));
            Ample.Plugins.FaerProfile? profile = Ample.Plugins.FaerProfile.LoadLocal();
            if (profile is null) return Ok(new { routes = m.Settings.Faer, configured = false, connected = false });
            try
            {
                using Ample.Plugins.FaerClient client = profile.CreateClient();
                JsonElement status = await client.StatusAsync(c.RequestAborted);
                return Ok(new { routes = m.Settings.Faer, configured = true, connected = true, status });
            }
            catch (Exception ex) when (ex is HttpRequestException or CryptographicException)
            {
                return Ok(new { routes = m.Settings.Faer, configured = true, connected = false });
            }
        });
        add("POST", "/api/ai/context/move", "ai", "For an AI task: proposes moving a project file or folder with the references following. With confirm ask the owner is asked now and the call waits for the answer; with propose the move is listed with the proposal and ticked at Accept. Nothing is moved before Accept.", true, s is null ? null : async (HttpContext c) => Ok(await s.Ai.ProposeMoveAsync(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c), c.RequestAborted)));
        add("GET", "/api/ai/context/memory", "ai", "For an AI task: the notes kept for later tasks outside the song, scope=project or scope=song (the song of the task).", false, s is null ? null : (HttpContext c) => Ok(s.Ai.MemoryRead(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["scope"].ToString())));
        add("POST", "/api/ai/context/memory", "ai", "For an AI task: adds a dated note to (mode=append, the default) or rewrites (mode=replace) the memory of the project or of the song; it is kept in the .ample folder, never in the song.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(s.Ai.MemoryWrite(c.Request.Headers["X-Ample-Context"].ToString(), Opt(b, "scope") ?? string.Empty, Opt(b, "text") ?? string.Empty, Opt(b, "mode")));
        });
        add("POST", "/api/ai/context/move-file", "ai", "Move a text file within an AI staged proposal without overwriting; workspace/filename recovers a misplaced root song.", true, s is null ? null : async (HttpContext c) => Ok(AiService.MoveFile(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c))));
        add("POST", "/api/ai/context/sample-set-add", "ai", "Add one audio file as a WAV to a sample set folder of an AI staged proposal, optionally with its loop points and the set's envelope; never overwrites.", true, s is null ? null : async (HttpContext c) => Ok(AiService.SampleSetAdd(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c), s.Root)));
        add("POST", "/api/ai/context/copy-file", "ai", "Copy a text file within an AI staged proposal to a new path, without overwriting; the source stays.", true, s is null ? null : async (HttpContext c) => Ok(AiService.CopyFile(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c), s.Root)));
        add("GET", "/api/ai/context/docs", "ai", "Read-only context for an AI task: the reference documents (list, one, or a search).", false, s is null ? null : (HttpContext c) => Ok(AiContext.Docs(s, c.Request.Query["name"].ToString(), c.Request.Query["query"].ToString())));
        add("GET", "/api/ai/context/blocktypes", "ai", "Read-only context for an AI task: the block types.", false, s is null ? null : (HttpContext c) => Ok(AiContext.BlockTypes(s, c.Request.Query["type"].ToString())));
        add("GET", "/api/ai/context/song", "ai", "Read-only context for an AI task: a song with its blocks and problems.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.CachedAsync(c.Request.Headers["X-Ample-Context"].ToString(), "song|" + c.Request.Query["location"] + "|" + c.Request.Query["path"], () => AiContext.SongAsync(s, c.Request.Query["path"].ToString(), c.RequestAborted, AiService.ContextText(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["location"].ToString() is { Length: > 0 } loc ? loc : "project")))));
        add("GET", "/api/ai/context/block", "ai", "Read-only context for an AI task: one block with what feeds it and what it feeds.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.CachedAsync(c.Request.Headers["X-Ample-Context"].ToString(), "block|" + c.Request.Query["location"] + "|" + c.Request.Query["path"] + "|" + c.Request.Query["name"], () => AiContext.BlockAsync(s, c.Request.Query["path"].ToString(), c.Request.Query["name"].ToString(), c.RequestAborted, AiService.ContextText(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["location"].ToString() is { Length: > 0 } loc ? loc : "project")))));
        add("GET", "/api/ai/context/analyze", "ai", "Read-only context for an AI task: the reference analysers (tempo, key, loudness, spectrum, pitch) on the built audio of a block.", false, s is null ? null : async (HttpContext c) => Ok(await AiContext.AnalyzeAsync(s, c.Request.Query["path"].ToString(), c.Request.Query["block"].ToString(), c.Request.Query["with"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/access", "ai", "For an AI task: asks the person to allow writing to a place outside the project (path, why, minutes, kind write or passthrough). The call waits for the answer: allow for that long, always for the project, or deny.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.RequestAccessAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["why"].ToString(), c.Request.Query["minutes"].ToString(), c.Request.Query["kind"].ToString(), c.RequestAborted)));
        add("POST", "/api/ai/context/write-outside", "ai", "For an AI task: AMPLE's own pass-through write to a place outside the project; it only works inside a permission the person gave (see access).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(s.Ai.WriteOutside(c.Request.Headers["X-Ample-Context"].ToString(), Str(b, "path"), Str(b, "text")));
        });
        add("POST", "/api/ai/context/findings", "ai", "For an AI task: writes the model's remarks (what is missing, what could be better) to notes/ai-findings/ inside the project; no permission is needed.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(s.Ai.Findings(c.Request.Headers["X-Ample-Context"].ToString(), Opt(b, "name") ?? string.Empty, Str(b, "text")));
        });
        add("POST", "/api/ai/context/import-score", "ai", "For an AI task: turns a MIDI or MusicXML score of the project into a new sequence file in the proposal (one channel per part, patterns of a number of bars, a playlist).", true, s is null ? null : async (HttpContext c) => Ok(s.Ai.ImportScore(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c))));
        add("GET", "/api/ai/context/project-log", "ai", "For an AI task: the project log, what is missing for the tools of the project and how to put it right.", false, s is null ? null : () => Ok(s.Ai.ProjectLogRead()));
        add("GET", "/api/ai/grants", "ai", "The permissions that last for the project (writing outside it and visible browser access) and the project log.", false, s is null ? null : () => Ok(new { grants = s.Ai.ProjectGrants().Select(g => new { path = g.Path, kind = g.Kind }), browserAccess = s.Ai.BrowserAccessForProject(), log = Ample.Storage.Projects.ProjectLog.List(s.Root, 100).Select(e => new { at = e.At, kind = e.Kind, what = e.What, hint = e.Hint }) }));
        add("POST", "/api/ai/grants/revoke", "ai", "Takes back a permission that lasts for the project.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            if (b.TryGetProperty("browserAccess", out JsonElement browserAccess) && browserAccess.ValueKind == JsonValueKind.True) s.Ai.RevokeBrowserAccessForProject();
            else s.Ai.RevokeGrant(Str(b, "path"));
            return Ok(new { revoked = true });
        });
        add("GET", "/api/ai/context/request-asset", "ai", "For an AI task: asks the user to let Codex read a large or non-text source asset; AMPLE copies it into the working folder of the task when it is allowed. The answer comes when the user has decided.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.RequestAssetAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["reason"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/fetch", "ai", "For an AI task: AMPLE downloads a public web address for Codex (which has no network). A link from the user's request is fetched at once, another link needs the user's approval. Public addresses only, images, text, audio, JSON, XML or PDF, at most 10 MB.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.FetchUrlAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["url"].ToString(), c.Request.Query["reason"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/install-pack", "ai", "For an AI task: downloads a .zip sample-pack archive from a public web address and installs it into the user's library (packs/<name>@<version>), available to every project afterward. Always needs the user's approval, even when the link was given in the request. At most 200 MB; the archive must contain a pack.json.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.InstallPackAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["url"].ToString(), c.Request.Query["reason"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-open", "ai", "For an AI task: after explicit task approval, opens a URL in the visible persistent Chromium window. The owner enters credentials manually. Purchase actions are unavailable.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserOpenAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["url"].ToString(), c.Request.Query["reason"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-show", "ai", "For an AI task: after explicit browser approval, makes its isolated headless Chromium window visible for owner inspection or manual interaction.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserShowAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["reason"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/browser/view", "ai", "The assistant's browser window as a picture (JPEG, base64) while it waits for a person (sign-in, a human check); active=false otherwise.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserViewAsync(c.RequestAborted)));
        add("POST", "/api/ai/browser/input", "ai", "A click, scroll, typing or key of the person into the assistant's browser window while it waits for a person (type click|move|scroll|type|key, x, y, dy, text).", true, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserInputAsync(await Body(c), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-read", "ai", "For an AI task: reads the visible page title and text, returning visible links, buttons and text fields with indexes for browser-click and browser-type.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserReadAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-click", "ai", "For an AI task: clicks a numbered element from the latest browser-read result; purchase actions are blocked.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserClickAsync(c.Request.Headers["X-Ample-Context"].ToString(), (int)Num(c, "index"), c.RequestAborted)));
        add("GET", "/api/ai/context/web-search", "ai", "For an AI task: calls the selected search provider after task approval; DuckDuckGo is its free official Instant Answer API (not full results), Brave is an API-key search plan. Brave usage is a local estimate and identical results have a brief in-memory cache. No search-result HTML scraping fallback.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.WebSearchAsync(c.Request.Headers["X-Ample-Context"].ToString(), s.Library.Home, c.Request.Query["query"].ToString(), c.Request.Query["limit"].ToString(), c.Request.Query["offset"].ToString(), c.Request.Query["provider"].ToString(), AiProjectSecretStore.Read(s.Library.Home, s.Manifest.Id), c.RequestAborted)));
        add("GET", "/api/ai/context/web-read", "ai", "For an AI task: reads a public HTML URL supplied by the user or returned by this task's approved search, without cookies or JavaScript and without another approval; redirects are public-address-only and response size is capped at 2 MB.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.WebReadAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["url"].ToString(), c.Request.Query["mode"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-type", "ai", "For an AI task: fills a numbered non-credential text field from the latest browser-read result; password, username and account fields are never filled.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserTypeAsync(c.Request.Headers["X-Ample-Context"].ToString(), (int)Num(c, "index"), c.Request.Query["text"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-mouse-move", "ai", "For an AI task: moves the pointer inside the visible page viewport for hovering; does not click.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserMouseMoveAsync(c.Request.Headers["X-Ample-Context"].ToString(), (int)Num(c, "x"), (int)Num(c, "y"), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-media-mute", "ai", "For an AI task: mutes or unmutes HTML audio and video elements on the current page; does not control browser tab/system audio.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserMediaMuteAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["muted"].ToString() == "true", c.RequestAborted)));
        add("GET", "/api/ai/context/browser-screenshot", "ai", "For an AI task: captures the visible page screenshot and returns it for visual inspection, without saving it as a download.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserScreenshotAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/browser-network-stats", "ai", "For an AI task: returns aggregate console error and network resource counts without message text, URLs, headers or bodies.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.BrowserNetworkStatsAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.RequestAborted)));
        add("POST", "/api/ai/context/browser-js", "ai", "For an AI task: executes owner-authorized JavaScript in the visible page; may change page state or trigger actions and can bypass the numbered-click purchase block.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(await s.Ai.BrowserRunJavaScriptAsync(c.Request.Headers["X-Ample-Context"].ToString(), Str(b, "script"), c.RequestAborted)); });
        add("POST", "/api/ai/context/edit", "ai", "For an AI task: structured edits (replace, insert, delete_lines, set_param, add_block, remove_block, connect, disconnect, create) of a file of the temporary copy, all or nothing; the answer lists the new errors and warnings and the diff.", true, s is null ? null : async (HttpContext c) => Ok(s.Ai.Edit(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c))));
        add("GET", "/api/ai/context/listen", "ai", "For an AI task: builds the proposal in a temporary project and measures a block (or the master) with analysers (default loudness and spectrum), next to the same block of the real project when it is built.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.ListenAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["block"].ToString(), c.Request.Query["with"].ToString(), c.Request.Query["path"].ToString(), null, c.Request.Query["forceRender"] == "true", c.RequestAborted)));
        add("GET", "/api/ai/context/lookup", "ai", "For an AI task: more about units, a parameter (unit, range, what a change reaches), the impact of a block (what is rendered again) or a word (block types, parameters and documents), for the song as the task has it now.", false, s is null ? null : (HttpContext c) => Ok(s.Ai.Lookup(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["topic"].ToString(), c.Request.Query["name"].ToString(), c.Request.Query["query"].ToString())));
        add("GET", "/api/ai/context/arrangement", "ai", "For an AI task: the arrangement of the song before the task and now (where the blocks sit in bars and seconds, the slice of the master), what changed, and with measure=true the real length, peak and silence of the built master of the proposal next to the current project.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.ArrangementAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["measure"].ToString() == "true", c.RequestAborted, c.Request.Query["path"].ToString())));
        add("GET", "/api/ai/context/hum", "ai", "For an AI task: the melody found in a hummed recording that the user attached (notes, key, the notes text of a midi-clip), at the tempo of the song or the one asked for.", false, s is null ? null : (HttpContext c) => Ok(s.Ai.Hum(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["id"].ToString(), Num(c, "tempo"), Num(c, "grid"), c.Request.Query["key"].ToString(), (int)Num(c, "bar"))));
        add("POST", "/api/hum/transcribe", "audio", "Finds the melody in a hummed or sung line (WAV bytes in the body, 16 bit PCM): the notes, the key and the notes text of a midi-clip, snapped to tempo (default 120), grid (beats, default 0.25), key (auto or like 'A minor') and start bar. An attempt: listen to it and correct it.", true, s is null ? null : async (HttpContext c) =>
        {
            byte[] wav = await ReadBodyAsync(c, 40_000_000);
            return Ok(HumTranscriber.ToJson(HumTranscriber.Transcribe(wav, HumOptionsOf(c))));
        });
        add("POST", "/api/hum/save-midi", "audio", "Writes the notes found in a hum (notes: start and duration in seconds, pitch, strength) as a .mid file in a folder of the project, at the tempo given; the name is made unique. The answer holds the path.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement body = await Body(c);
            string folder = (Opt(body, "folder") ?? string.Empty).Trim('/');
            string stem = new([.. (Opt(body, "name") ?? "hum").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')]);
            if (stem.Length == 0) stem = "hum";
            double tempo = body.TryGetProperty("tempo", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 120;
            byte[] bytes = HumMidi.Build(body.GetProperty("notes").EnumerateArray(), tempo);
            string path = string.Empty;
            for (int n = 1; n < 1000; n++)
            {
                path = (folder.Length > 0 ? folder + "/" : string.Empty) + (n == 1 ? stem : stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".mid";
                if (!s.Fs.FileExists(ProjectPath.Resolve(s.Fs, s.Root, path))) break;
            }

            if (path.Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || HasProjectLink(s.Fs, s.Root, path)) throw new AmpleException("AMP5285", "Nothing can be created in the private project folder or through a link.");
            if (folder.Length > 0 && !s.Fs.DirectoryExists(ProjectPath.Resolve(s.Fs, s.Root, folder))) await s.Bus.ExecuteAsync(new CreateFolderCommand(folder), c.RequestAborted).ConfigureAwait(false);
            await s.Bus.ExecuteAsync(new ImportFileCommand(path, bytes), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { path } });
            return Ok(new { path, notes = body.GetProperty("notes").GetArrayLength() });
        });
        add("POST", "/api/ai/hum", "ai", "Keeps a hummed recording (WAV bytes) so that it can be attached to an AI task or message (hums: [id]); the answer holds its id and the melody found in it (tempo, grid, key and bar as for /api/hum/transcribe).", true, s is null ? null : async (HttpContext c) =>
        {
            byte[] wav = await ReadBodyAsync(c, 40_000_000);
            string id = s.Ai.SaveHum(wav);
            JsonObject found = HumTranscriber.ToJson(HumTranscriber.Transcribe(wav, HumOptionsOf(c)));
            found["id"] = id;
            return Ok(found);
        });
        add("GET", "/api/ai/hum/{id}/audio", "ai", "The kept hummed recording, to play it again.", false, s is null ? null : (string id) => Results.File(s.Ai.HumFile(id), "audio/wav", enableRangeProcessing: true));
        add("GET", "/api/ai/context/offered", "ai", "For an AI task: the audio files the user offered (length, level, pitch, the path the song uses for them, whether the proposal uses them).", false, s is null ? null : (HttpContext c) => Ok(AiService.Offered(c.Request.Headers["X-Ample-Context"].ToString())));
        add("POST", "/api/ai/attachment", "ai", "Keeps an audio file the user offers to Codex (WAV bytes in the body, or mp3/ogg/flac with ?extension=; name= is the file name). Nothing of it is in the project: it is only offered to a task (offered: [id]) and comes along into the project only when the user chooses so at Accept.", true, s is null ? null : async (HttpContext c) =>
        {
            byte[] data = await ReadBodyAsync(c, 60_000_000);
            string extension = c.Request.Query["extension"].ToString().ToLowerInvariant();
            if (CompressedAudioExtensions.Contains(extension))
            {
                data = await DecodeToWavAsync(data, extension, c.RequestAborted);
            }

            return Ok(s.Ai.SaveAttachment(data, c.Request.Query["name"].ToString()));
        });
        add("GET", "/api/ai/attachment/{id}/audio", "ai", "An offered audio file, to listen to before it is given to Codex or taken along.", false, s is null ? null : (string id) => Results.File(s.Ai.AttachmentFile(id), "audio/wav", enableRangeProcessing: true));
        add("POST", "/api/ai/context/listen", "ai", "For an AI task: like GET listen, with a trial: edits (the format of the edit tool) that are applied only in the temporary project of this call, to hear a variant without changing the proposal.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            JsonArray? trial = b.TryGetProperty("edits", out JsonElement e) && e.ValueKind == JsonValueKind.Array ? JsonNode.Parse(e.GetRawText())!.AsArray() : null;
            return Ok(await s.Ai.ListenAsync(c.Request.Headers["X-Ample-Context"].ToString(), Opt(b, "block") ?? string.Empty, Opt(b, "with") ?? string.Empty, Opt(b, "path"), trial, b.TryGetProperty("forceRender", out JsonElement forceRender) && forceRender.ValueKind == JsonValueKind.True, c.RequestAborted));
        });
        add("POST", "/api/ai/context/answer", "ai", "For an AI task that answers a question from Koda: delivers the answer as a title, a short answer and the details (markdown) the user can unfold.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(AiService.DeliverAnswer(c.Request.Headers["X-Ample-Context"].ToString(), b)); });
        add("POST", "/api/ai/context/variants", "ai", "For an AI task: offers 2 to 6 alternatives that are all part of the proposal (name, summary, the blocks that belong to each, the block to listen to), so the user can compare, hear and keep one.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(s.Ai.OfferVariants(c.Request.Headers["X-Ample-Context"].ToString(), b)); });
        add("POST", "/api/ai/context/skip-build", "ai", "For an AI task: honor the user's explicit no-build or no-render request while still validating source changes.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(s.Ai.SkipBuild(c.Request.Headers["X-Ample-Context"].ToString(), Str(b, "reason"))); });
        add("GET", "/api/ai/context/history", "ai", "For an AI task: what the user asked before in this project and what came out (newest first; id= for one request in full with its changes).", false, s is null ? null : (HttpContext c) => Ok(s.Ai.HistoryTool(c.Request.Query["id"].ToString(), c.Request.Query["song"].ToString(), c.Request.Query["query"].ToString(), (int)Num(c, "limit"))));
        add("GET", "/api/ai/history", "ai", "The earlier AI requests of this project (newest first), optionally for one song (song=) or containing a word (query=), so a new request can continue one (continueFrom).", false, s is null ? null : (HttpContext c) => Ok(new JsonObject { ["requests"] = s.Ai.History(Num(c, "limit") > 0 ? (int)Num(c, "limit") : 12, c.Request.Query["song"].ToString(), c.Request.Query["query"].ToString()) }));
        add("POST", "/api/ai/context/present", "ai", "Presents a clip with optional temporary structured trial edits; reports rendered settings and identical audio.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c).ConfigureAwait(false);
            JsonArray? trial = b.TryGetProperty("edits", out JsonElement edits) && edits.ValueKind == JsonValueKind.Array ? JsonNode.Parse(edits.GetRawText())!.AsArray() : null;
            if (b.TryGetProperty("edits", out edits) && edits.ValueKind != JsonValueKind.Null && trial is null) throw new AmpleException("AMP5458", "edits must be an array.");
            double Number(string name) => b.TryGetProperty(name, out JsonElement number) && double.TryParse(number.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;
            return Ok(await s.Ai.PresentAsync(c.Request.Headers["X-Ample-Context"].ToString(), Opt(b, "block") ?? string.Empty, Number("from"), Number("to"), Opt(b, "unit") ?? string.Empty, Opt(b, "label") ?? string.Empty, Opt(b, "why") ?? string.Empty, Opt(b, "source") ?? string.Empty, Opt(b, "path"), trial, b.TryGetProperty("rebuildCurrent", out JsonElement rebuild) && rebuild.GetBoolean(), c.RequestAborted).ConfigureAwait(false));
        });
        add("POST", "/api/ai/context/obsolete", "ai", "For an AI task: marks one previously presented sample obsolete with a reason; it remains in the output until the user dismisses it.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(s.Ai.MarkClipObsoleteByToken(c.Request.Headers["X-Ample-Context"].ToString(), Str(b, "clipId"), Str(b, "reason"))); });
        add("GET", "/api/ai/context/present", "ai", "For an AI task: shows the user a piece of sound (the render of a block, whole or a slice, from the proposal or the current project) in the task window.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.PresentAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["block"].ToString(), Num(c, "from"), Num(c, "to"), c.Request.Query["unit"].ToString(), c.Request.Query["label"].ToString(), c.Request.Query["why"].ToString(), c.Request.Query["source"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/tasks/{id}/clip/{clip}", "ai", "A clip of sound that Codex presented to the user, to listen to; ?format=wav or ?format=mp3 (encoded by ffmpeg) downloads it as a file.", false, s is null ? null : async (string id, string clip, HttpContext c) =>
        {
            string path = s.Ai.ClipFile(id, clip);
            string mediaType = s.Ai.ClipMediaType(id, clip);
            if (c.Request.Query.ContainsKey("peaks")) return Ok(Peaks(File.OpenRead(path), Math.Clamp((int)Num(c, "peaks"), 1, 2000)));
            string format = c.Request.Query["format"].ToString().ToLowerInvariant();
            if (format.Length == 0 && mediaType == "audio/mpeg") return Results.File(path, mediaType, enableRangeProcessing: true);
            if (format == "mp3" && mediaType == "audio/mpeg") return Results.File(path, mediaType, clip + ".mp3", enableRangeProcessing: true);
            if (format == "mp3")
            {
                await using FileStream wav = File.OpenRead(path);
                (byte[] mp3, string encoder) = await EncodeMp3Async(wav, c.RequestAborted).ConfigureAwait(false);
                c.Response.Headers["X-Ample-Mp3-Encoder"] = encoder;
                return Results.File(mp3, "audio/mpeg", clip + ".mp3");
            }

            if (format is "wav") return Results.File(path, "audio/wav", clip + ".wav", enableRangeProcessing: true);
            if (format.Length > 0) throw new AmpleException("AMP5285", "Download format must be wav or mp3.");
            return Results.File(path, "audio/wav", enableRangeProcessing: true);
        });
        add("GET", "/api/ai/context/beats", "ai", "The beats catalogue (original CC0 grooves) for an AI task, small by default: overview=true, a search with genre, family, mood, form, bpm, novelty, energy, text, limit and offset, similar=<id>, id=<id> with parts, or queries=[...] for several searches in one call.", false, s is null ? null : (HttpContext c) => Ok(AiService.Beats(c.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()))));
        add("GET", "/api/ai/context/sample-sources", "ai", "For an AI task: where real, legal samples can be found (a DJ scratch, a vinyl crackle, a foley sound) and how they come into a project: the rules (CC0 or public domain by default, the source written down, nothing bought or downloaded without the owner) and the places with their licence. text=words puts the places that fit first. Read-only, no AI.", false, s is null ? null : (HttpContext c) => Ok(Ample.Audio.Engine.SampleSources.Run(new JsonObject { ["text"] = c.Request.Query["text"].ToString() })));
        add("GET", "/api/ai/context/find-samples", "ai", "For an AI task: finds real samples by itself. AMPLE reads the CC0 search of Freesound (and the public-domain search of the Internet Archive) without a sign-in, reads every candidate page and checks its licence, and answers with candidates ready for fetch-sample (page and file). text=words (for example dj scratch), source=freesound|archive|all, limit up to 8. Read-only; nothing is downloaded.", false, s is null ? null : async (HttpContext c) => Ok(await AiService.FindSamplesAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["text"].ToString(), c.Request.Query["source"].ToString(), int.TryParse(c.Request.Query["limit"].ToString(), out int found) ? found : 5, c.RequestAborted)));
        add("GET", "/api/ai/context/fetch-sample", "ai", "For an AI task: fetches a real sample for the proposal. AMPLE reads the page itself and only a CC0 or public-domain licence on freesound.org, archive.org or opengameart.org lets the audio file (wav, mp3, ogg, flac, aif) through, at most 25 MB, into assets/web of the working copy; the answer has a ready SOURCE.md text. no approval is asked because the licence is checked here. browser=true does the same with the signed-in browser of AMPLE for a sound that needs a sign-in (it needs the browser permission of the task).", false, s is null ? null : async (HttpContext c) => Ok(c.Request.Query["browser"].ToString() is "1" or "true" ? await s.Ai.FetchSampleViaBrowserAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["page"].ToString(), c.Request.Query["file"].ToString(), c.Request.Query["title"].ToString(), c.RequestAborted) : await AiService.FetchSampleAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["page"].ToString(), c.Request.Query["file"].ToString(), c.Request.Query["title"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/analyze-audio", "ai", "For an AI task: a deterministic look at one audio file (a project file or workspace/<path>): length, level, brightness, pitch of a single note, likely key, tempo and whether it loops in whole bars. Read-only, no AI.", false, s is null ? null : (HttpContext c) => Ok(AiService.AnalyzeAudio(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["source"].ToString(), s.Root)));
        add("GET", "/api/ai/context/generate", "ai", "For an AI task: deterministic pattern generators (euclid, groove, arpeggio, bassline, fill) from a small query with a seed. Read-only, no AI.", false, s is null ? null : (HttpContext c) => Ok(AiService.Generate(c.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()))));
        add("GET", "/api/ai/context/mix-hints", "ai", "For an AI task: mix starting points (volume, pan, filter, EQ, compression, sends) per instrument and genre as AMPLE block parameters. Read-only.", false, s is null ? null : (HttpContext c) => Ok(AiService.MixHint(c.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()))));
        add("POST", "/api/ai/context/resolve-notes", "ai", "For an AI task: the notes of a midi-clip, of a sequencer channel or of a note pipeline (expression) in one form (count, bars, signature, first notes), and with compare_block / compare_channel / compare_expression whether the two are exactly the same (every note, length and setting). Read-only.", false, s is null ? null : async (HttpContext c) => Ok(s.Ai.ResolveNotes(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c))));
        add("POST", "/api/ai/context/find-patterns", "ai", "For an AI task: finds a shorter way to write the notes of a midi-clip or a sequencer channel: a rhythm that repeats with pitches, velocities and other settings that cycle, as a note pipeline that was evaluated and compared with the original. Read-only.", false, s is null ? null : async (HttpContext c) => Ok(s.Ai.FindPatterns(c.Request.Headers["X-Ample-Context"].ToString(), await Body(c))));
        add("GET", "/api/ai/context/component-library", "ai", "For an AI task: searches the library of the proposal (query words, tag, imported=yes|no): every component of every .amplec file with description, tags, kind, seal, whether the song imports it, the import line to write and the block type to use (an alias when the name is taken).", false, s is null ? null : (HttpContext c) => Ok(s.Ai.ComponentLibraryTool(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["query"].ToString(), c.Request.Query["tag"].ToString(), c.Request.Query["imported"].ToString())));
        add("GET", "/api/ai/context/components", "ai", "For an AI task: the components a song of the proposal can see, every version with its seal, signature and deprecation, the contract of the default version and the problems of putting them together.", false, s is null ? null : (HttpContext c) => Ok(s.Ai.ComponentsTool(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString())));
        add("GET", "/api/ai/context/arrangement-lint", "ai", "For an AI task: which blocks of a song play in which bars, the density per bar and what looks wrong (silent bars, harmony without a bass, nothing that builds). Reads only the placements.", false, s is null ? null : (HttpContext c) => Ok(s.Ai.LintArrangement(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString())));
        add("GET", "/api/ai/context/check", "ai", "For an AI task: the errors and warnings that the proposal introduced (the ones the song did not have before); none means it can be handed in.", false, s is null ? null : (HttpContext c) => Ok(s.Ai.Check(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString())));
        add("POST", "/api/ai/tasks/{id}/message", "ai", "Goes on with a proposal before it is accepted: while Codex works the message steers it, afterwards it asks for a change on top of the proposal as it is now.", true, s is null ? null : async (string id, HttpContext c) => { JsonElement b = await Body(c); return Ok(AiService.TaskJson(s.Ai.SendMessage(id, Str(b, "text"), b.TryGetProperty("hums", out JsonElement hm) && hm.ValueKind == JsonValueKind.Array ? [.. hm.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null, b.TryGetProperty("offered", out JsonElement of) && of.ValueKind == JsonValueKind.Array ? [.. of.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null), false)); });
        add("GET", "/api/ai/context/thought", "ai", "For an AI task: shows the user what the AI is thinking about right now (a short note that replaces the previous one).", false, s is null ? null : (HttpContext c) => Ok(AiService.SetThought(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["text"].ToString())));
        add("GET", "/api/ai/context/revision", "ai", "For an AI task: what one revision of the project history changed against the one before it, or the diff of one file of it.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.RevisionToolAsync(c.Request.Query["id"].ToString(), c.Request.Query["path"].ToString(), c.RequestAborted)));
        add("GET", "/api/ai/context/changes-since", "ai", "For a resumed AI task: how the project changed since the working copy was based on it (overview), the diff of one file, or import=true to bring the project's version of a file into the copy.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.ChangesSinceAsync(c.Request.Headers["X-Ample-Context"].ToString(), c.Request.Query["path"].ToString(), c.Request.Query["import"].ToString() == "true")));
        add("GET", "/api/ai/context/file", "ai", "Read-only context for an AI task: a text file of the project (a PDF: its text), in a window; an offset reads on past a truncated one.", false, s is null ? null : async (HttpContext c) =>
        {
            string path = c.Request.Query["path"].ToString();
            int offset = int.TryParse(c.Request.Query["offset"].ToString(), out int o) ? o : 0;
            string location = c.Request.Query["location"].ToString() is { Length: > 0 } value ? value : "project";
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Ok(s.Ai.PdfText(c.Request.Headers["X-Ample-Context"].ToString(), path, location));
            return Ok(await AiService.CachedAsync(c.Request.Headers["X-Ample-Context"].ToString(), $"file|{location}|{path}|{offset}", () => Task.FromResult(AiContext.File_(s, path, AiService.ContextText(c.Request.Headers["X-Ample-Context"].ToString(), path, location), offset, location == "work" ? "task work folder (read-only)" : "project proposal"))));
        });
        add("GET", "/api/ai/status", "ai", "Where the AI assistant stands: what the server allows, what the project chose, whether Codex is installed and (with auth=true) whether it is signed in.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.StatusAsync(c.Request.Query["auth"].ToString() == "true", c.RequestAborted)));
        add("GET", "/api/ai/models", "ai", "The models Codex offers and the current account's usage limits.", false, s is null ? null : async (HttpContext c) => Ok(await s.Ai.ModelsAsync(c.RequestAborted)));
        add("GET", "/api/ai/policy", "ai", "What this server allows projects to use of Codex.", false, s is null ? null : () => Ok(AiPolicy.Read().ToJson()));
        add("POST", "/api/ai/policy", "ai", "Changes what this server allows projects to use of Codex (capabilities only: they say what a project MAY choose).", true, s is null ? null : async (HttpContext c) =>
        {
            AiPolicy current = AiPolicy.Read();
            AiPolicy next = current.With(await Body(c));
            if (current.Loosening(next) is { } widened)
            {
                throw new AmpleException("AMP5459", $"The setting {widened} cannot be widened over the API: a project may switch capabilities off, but allowing more (or choosing the Codex program) is done by the administrator of this machine in {AiPolicy.FilePath}.");
            }

            next.Write();
            return Ok(next.ToJson());
        });
        add("POST", "/api/ai/settings", "ai", "Changes the AI settings of the project: the assistant (none, codex-server, codex-device) and the execution mode (manual, auto, unsafe). What the server does not allow cannot be chosen; the first use and the first unsafe use need an acknowledgement.", true, s is null ? null : async (HttpContext c) => Ok(await s.Ai.SaveSettingsAsync(await Body(c), c.RequestAborted)));
        add("POST", "/api/ai/device/start", "ai", "Starts the Codex device flow for this project (its own Codex home) and returns the address and the code to enter.", true, s is null ? null : async (HttpContext c) => Ok(await s.Ai.StartDeviceLoginAsync(c.RequestAborted)));
        add("GET", "/api/ai/device/status", "ai", "The state of the device flow: waiting, linked, failed or cancelled.", false, s is null ? null : () => Ok(s.Ai.DeviceStatus()));
        add("POST", "/api/ai/device/cancel", "ai", "Stops a device flow that is waiting.", true, s is null ? null : async () => { await s.Ai.CancelDeviceLoginAsync(); return Ok(s.Ai.DeviceStatus()); });
        add("POST", "/api/ai/unlink", "ai", "Removes the Codex identity of this project (its login and its Codex home); the Codex of the user is not touched.", true, s is null ? null : async (HttpContext c) => { await s.Ai.UnlinkAsync(c.RequestAborted); return Ok(await s.Ai.StatusAsync(false, c.RequestAborted)); });
        add("GET", "/api/ai/sessions", "ai", "Lists the saved assistant sessions for this project.", false, s is null ? null : () => Ok(s.Ai.AssistantSessions()));
        add("POST", "/api/ai/sessions", "ai", "Creates a named assistant session; optional conversation archiving is off by default.", true, s is null ? null : async (HttpContext c) => { JsonElement b = await Body(c); return Ok(s.Ai.CreateAssistantSession(Str(b, "title"), b.TryGetProperty("archiveConversation", out JsonElement archive) && archive.GetBoolean())); });
        add("PATCH", "/api/ai/sessions/{id}", "ai", "Renames a saved assistant session or changes its optional conversation archive.", true, s is null ? null : async (string id, HttpContext c) => { JsonElement b = await Body(c); if (b.TryGetProperty("archiveConversation", out JsonElement archive)) return Ok(s.Ai.SetConversationArchive(id, archive.GetBoolean(), b.TryGetProperty("includeEarlier", out JsonElement earlier) && earlier.GetBoolean())); return Ok(s.Ai.RenameAssistantSession(id, Str(b, "title"))); });
        add("DELETE", "/api/ai/sessions/{id}", "ai", "Deletes an assistant session that has no active or unapplied proposal.", true, s is null ? null : (string id) => { s.Ai.DeleteAssistantSession(id); return Ok(new { deleted = true }); });
        add("GET", "/api/koda/status", "ai", "Koda, the helper: whether Koda has a login of their own (lane own) or shares the lane of the assistant (lane shared), and a device flow that is waiting.", false, s is null ? null : () => Ok(s.Ai.KodaStatus()));
        add("POST", "/api/koda/login/start", "ai", "Starts the Codex device flow for Koda's own home: an address and a code to enter. The same account as the assistant may be linked a second time.", true, s is null ? null : async (HttpContext c) => Ok(await s.Ai.StartKodaLoginAsync(c.RequestAborted)));
        add("POST", "/api/koda/login/cancel", "ai", "Stops the device flow of Koda that is waiting.", true, s is null ? null : async () => Ok(await s.Ai.CancelKodaLoginAsync()));
        add("POST", "/api/koda/unlink", "ai", "Removes Koda's own login; Koda then shares the lane of the assistant.", true, s is null ? null : async () => Ok(await s.Ai.UnlinkKodaAsync()));
        add("POST", "/api/ai/tasks", "ai", "Asks Codex for a proposal: it works in a temporary copy of the files in scope; the project is not changed.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string path = Str(b, "path");
            string? sessionId = Opt(b, "sessionId");
            if (sessionId is null)
            {
                string[] titleWords = Str(b, "prompt").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).ToArray();
                sessionId = s.Ai.CreateAssistantSession(titleWords.Length == 0 ? "Assistant session" : string.Join(' ', titleWords), archiveConversation: false).Id;
            }
            AiTaskRequest request = new(Opt(b, "kind") ?? "custom", Opt(b, "scope") ?? "file", Str(b, "prompt"), path, Opt(b, "song") ?? path, b.TryGetProperty("fromLine", out JsonElement fl) && fl.ValueKind == JsonValueKind.Number ? fl.GetInt32() : null, b.TryGetProperty("toLine", out JsonElement tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetInt32() : null, Opt(b, "block"), Opt(b, "model"), b.TryGetProperty("hums", out JsonElement hm) && hm.ValueKind == JsonValueKind.Array ? [.. hm.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null, b.TryGetProperty("offered", out JsonElement of) && of.ValueKind == JsonValueKind.Array ? [.. of.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null, Opt(b, "continueFrom"), Opt(b, "effort"), b.TryGetProperty("blocks", out JsonElement bl) && bl.ValueKind == JsonValueKind.Array ? [.. bl.EnumerateArray().Select(x => x.GetString() ?? string.Empty)] : null) { Strict = b.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind == JsonValueKind.True, Context = Opt(b, "context") == "saved" ? "saved" : "unsaved" };
            AiTask task = await s.Ai.StartTaskAsync(request, AssistantBase(c), sessionId, c.RequestAborted);
            return Ok(AiService.TaskJson(task, false));
        });
        add("POST", "/api/ai/tasks/{id}/resume", "ai", "Resumes an interrupted or ended assistant conversation; with fresh=true a new Codex session starts in the same working copy and is told what was asked and what is already changed.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement body = await Body(c);
            bool fresh = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("fresh", out JsonElement f) && f.ValueKind == JsonValueKind.True;
            return Ok(AiService.TaskJson(await s.Ai.ResumeTaskAsync(id, AssistantBase(c), fresh, c.RequestAborted), false));
        });
        add("GET", "/api/ai/tasks", "ai", "The AI tasks of this project, newest first.", false, s is null ? null : () => Ok(s.Ai.Tasks().Select(t => new { t.Id, t.SessionId, t.Status, prompt = t.Request.Prompt, t.Created })));
        add("GET", "/api/ai/tasks/{id}", "ai", "One AI task: the log (since=N: only the entries after number N; details=1 adds the input and output of every tool call, which is large), the approvals Codex waits for, and the proposal with its diff, diagnostics and build status.", false, s is null ? null : async (string id, HttpContext c) =>
        {
            AiTask task = s.Ai.GetTask(id);
            if (task.Status == "ready")
            {
                await s.Ai.CheckStaleAsync(task);
            }

            long? since = long.TryParse(c.Request.Query["since"], out long sinceNumber) ? sinceNumber : null;
            return Ok(AiService.TaskJson(task, c.Request.Query["diffs"] != "0", since, c.Request.Query["details"] == "1"));
        });
        add("POST", "/api/ai/tasks/{id}/answer", "ai", "Answers an approval request of Codex: accept, acceptForSession, accept30, acceptForProject, decline or cancel.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement b = await Body(c);
            s.Ai.Answer(id, Str(b, "approval"), Str(b, "decision"));
            return Ok(AiService.TaskJson(s.Ai.GetTask(id), false));
        });
        add("GET", "/api/ai/tasks/{id}/log/{seq}", "ai", "The input and output of one tool call in the log of a task (the log itself leaves them out because they are large).", false, s is null ? null : (string id, long seq) =>
        {
            AiLog entry = s.Ai.GetTask(id).Log.FirstOrDefault(l => l.Seq == seq) ?? throw new AmpleException("AMP5458", "This log entry is no longer kept.");
            return Ok(new JsonObject { ["seq"] = seq, ["details"] = entry.Details?.DeepClone() });
        });
        add("POST", "/api/ai/tasks/{id}/compact", "ai", "Asks Codex to compact its conversation now (between turns); the task shows that it is compacting while it happens.", true, s is null ? null : async (string id) => Ok(AiService.TaskJson(await s.Ai.CompactAsync(id), false)));
        add("POST", "/api/ai/tasks/{id}/assistant", "ai", "Changes the model and/or the reasoning effort of a running task (as Codex itself can); the next turn uses them and Codex is told.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(AiService.TaskJson(s.Ai.ChangeAssistant(id, Opt(b, "model"), Opt(b, "effort")), false));
        });
        add("POST", "/api/ai/tasks/{id}/cancel", "ai", "Stops a task. Nothing in the project changes.", true, s is null ? null : (string id) => { s.Ai.CancelTask(id); return Ok(AiService.TaskJson(s.Ai.GetTask(id), false)); });
        add("POST", "/api/ai/tasks/{id}/archive", "ai", "Archives the conversation of a task: its Codex session ends and nothing in the project changes. While the working copy and the thread are kept it can be resumed from the Assistant list (see resume in the session list). Stop (interrupt) only stops the turn.", true, s is null ? null : (string id) => Ok(AiService.TaskJson(s.Ai.ArchiveTask(id), false)));
        add("POST", "/api/ai/tasks/{id}/interrupt", "ai", "Stops the turn Codex is working on and keeps the conversation open: the next message goes on in the same session (unlike cancel, which ends the task).", true, s is null ? null : (string id) => Ok(AiService.TaskJson(s.Ai.InterruptTask(id), false)));
        add("POST", "/api/ai/tasks/{id}/accept", "ai", "Applies the proposal: a checkpoint first, then the other files; the new text of the song is returned for the editor. Refused when the project changed since the task began, and when it does not build unless anyway is true.", true, s is null ? null : async (string id, HttpContext c) => Ok(await AcceptWith(s, id, await Body(c), c.RequestAborted)));
        add("POST", "/api/ai/tasks/{id}/accept-selected-continue", "ai", "Accepts selected proposal files after checking them, then continues the same Codex conversation with the remaining proposal.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string[] paths = b.TryGetProperty("paths", out JsonElement selected) && selected.ValueKind == JsonValueKind.Array ? [.. selected.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            string[] include = b.TryGetProperty("include", out JsonElement offered) && offered.ValueKind == JsonValueKind.Array ? [.. offered.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            string[] moves = b.TryGetProperty("moves", out JsonElement ticked) && ticked.ValueKind == JsonValueKind.Array ? [.. ticked.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            return Ok(await s.Ai.AcceptSelectedAndContinueAsync(id, paths, Str(b, "message"), include, c.RequestAborted, moves));
        });
        add("POST", "/api/ai/tasks/{id}/accept-selected", "ai", "Accepts selected proposal files without starting another Codex turn.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string[] paths = b.TryGetProperty("paths", out JsonElement selected) && selected.ValueKind == JsonValueKind.Array ? [.. selected.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            string[] include = b.TryGetProperty("include", out JsonElement offered) && offered.ValueKind == JsonValueKind.Array ? [.. offered.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            string[] moves = b.TryGetProperty("moves", out JsonElement ticked) && ticked.ValueKind == JsonValueKind.Array ? [.. ticked.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            return Ok(await s.Ai.AcceptSelectedAsync(id, paths, string.Empty, include, false, c.RequestAborted, moves).ConfigureAwait(false));
        });
        add("POST", "/api/ai/tasks/{id}/continue", "ai", "Continues only unfinished work using delivered clips and original quantity limits; rejects duplicate running requests.", true, s is null ? null : async (string id, HttpContext c) => Ok(await s.Ai.ContinueRemainingAsync(id, Opt(await Body(c), "message") ?? string.Empty, c.RequestAborted).ConfigureAwait(false)));
        add("POST", "/api/ai/tasks/{id}/rebase", "ai", "Brings a proposal up to date with a project that changed in the meantime, where the changes do not overlap.", true, s is null ? null : async (string id) => Ok(await s.Ai.RebaseAsync(id)));
        add("POST", "/api/ai/tasks/{id}/reject", "ai", "Throws the proposal away; the project stays as it is.", true, s is null ? null : (string id) => { s.Ai.Reject(id); return Ok(AiService.TaskJson(s.Ai.GetTask(id), false)); });
        add("POST", "/api/ai/tasks/{id}/undo", "ai", "Puts back the files (other than the open song) that an accepted proposal changed.", true, s is null ? null : async (string id, HttpContext c) => Ok(await s.Ai.UndoAsync(id, c.RequestAborted)));
        add("POST", "/api/ai/tasks/{id}/build", "ai", "Builds the proposal in its temporary project so it can be listened to: the whole song, or with block one block and what it needs.", true, s is null ? null : async (string id, HttpContext c) => { JsonElement b = await Body(c); return Ok(AiService.TaskJson(s.Ai.RebuildTask(id, Opt(b, "block")), false)); });
        add("GET", "/api/ai/tasks/{id}/text", "ai", "The proposed text of a file of the proposal (as it would be after Accept).", false, s is null ? null : (string id, HttpContext c) => Ok(new { path = c.Request.Query["path"].ToString(), text = s.Ai.ProposedFile(id, c.Request.Query["path"].ToString()) }));
        add("GET", "/api/ai/tasks/{id}/audio", "ai", "Proposal audio with byte ranges; peaks=N returns a bounded waveform summary instead of audio.", false, s is null ? null : (string id, HttpContext c) =>
        {
            Stream? stream = c.Request.Query["block"].ToString() is { Length: > 0 } block ? s.Ai.BlockAudio(id, block) : s.Ai.AudioOf(id) is { } file ? File.OpenRead(file) : null;
            if (stream is null) throw new AmpleException("AMP5458", "The proposal has no audio (yet).");
            return c.Request.Query.ContainsKey("peaks") ? Ok(Peaks(stream, Math.Clamp((int)Num(c, "peaks"), 1, 2000))) : Results.File(stream, "audio/wav", enableRangeProcessing: true);
        });
        add("POST", "/api/ai/tasks/{id}/clips/{clipId}/obsolete", "ai", "Marks one presented sample obsolete with a reason. It stays visible until the user dismisses it.", true, s is null ? null : async (string id, string clipId, HttpContext c) => Ok(s.Ai.MarkClipObsolete(id, clipId, Str(await Body(c), "reason"))));
        add("POST", "/api/tx/mark", "package", "Sets or clears the Derived export marker of a block in the song text (bake: use the rendered output of this block as a boundary; as-is: always include this source as it is; none: clear). It changes the working copy of the song like any edit.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            string kind = Str(b, "kind");
            if (kind is not ("bake" or "as-is" or "none")) throw new AmpleException("AMP5285", "The kind must be bake, as-is or none.");
            string text = TxMarks.With(d.Text, Str(b, "block"), kind);
            d.Text = text;
            d.Version++;
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/revert", "documents", "Throws the changes of the working copy away: the song goes back to the text of the saved file.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            d.Text = d.Saved;
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("POST", "/api/doc/save-as", "documents", "Writes the working copy of a song to a new song file of the project (an existing name is refused); the open song stays as it is.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            string name = Str(b, "name").Trim();
            if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            {
                throw new AmpleException("AMP5285", "A song name is letters, digits, _ and - only.");
            }

            string folder = System.IO.Path.GetDirectoryName(d.Path)?.Replace('\\', '/') ?? string.Empty;
            string rel = (folder.Length == 0 ? name : folder + "/" + name) + ".amplet";
            if (File.Exists(ProjectPath.Resolve(s.Root, rel))) throw new AmpleException("AMP5285", $"There is already a song called {rel}.");
            await s.Bus.ExecuteAsync(new WriteFileCommand(rel, d.Text), c.RequestAborted);
            events!.Publish("files.changed", new { paths = new[] { rel } });
            return Ok(new { path = rel });
        });
        add("POST", "/api/doc/bake-clock-as", "documents", "Bakes the clock values of the last build of a song (or of one block: block) into a NEW song file of the project and leaves the open song as it is: the open song stays dynamic and can be the seed of several baked songs. An existing name is refused.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            string name = Str(b, "name").Trim();
            if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            {
                throw new AmpleException("AMP5285", "A song name is letters, digits, _ and - only.");
            }

            string folder = System.IO.Path.GetDirectoryName(d.Path)?.Replace('\\', '/') ?? string.Empty;
            string rel = (folder.Length == 0 ? name : folder + "/" + name) + ".amplet";
            if (File.Exists(ProjectPath.Resolve(s.Root, rel))) throw new AmpleException("AMP5285", $"There is already a song called {rel}.");
            AmpleDocument parsed = s.Parse(d);
            EditResult result = Edits.BakeDynamicHelpers(parsed, s.LastBuildAt.TryGetValue(Str(b, "path"), out DateTime builtAt) ? builtAt : parsed.ParsedAt, Opt(b, "block"));
            if (!result.Ok) throw new AmpleException("AMP5285", result.Error!);
            await s.Bus.ExecuteAsync(new WriteFileCommand(rel, Edits.Apply(d.Text, result.Edits)), c.RequestAborted);
            events!.Publish("files.changed", new { paths = new[] { rel } });
            return Ok(new { path = rel, summary = result.Summary });
        });
        add("GET", "/api/doc/inputs", "documents", "The files each block reads while it builds (sample folders, lyrics, MIDI and audio files, SoundFonts, modules) as one stamp per block, a hash of all of them, and the blocks whose seed follows the master seed. The web UI compares it to know that a build is out of date after such a file changed, although the song text did not.", false, s is null ? null : async (HttpContext c) =>
        {
            AmpleDocument doc = s.ParseExpanded(await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted));
            Dictionary<string, string> blocks = new(StringComparer.Ordinal);
            foreach (BlockNode b in doc.Blocks.Where(b => !b.Skipped))
            {
                IReadOnlyList<string> files = Ample.Audio.Engine.BlockFiles.Of(s.Root, null, doc, b);
                if (files.Count > 0) blocks[b.Name] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Ample.Audio.Engine.BlockFiles.Stamp(files))));
            }

            string all = string.Join('\n', blocks.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
            ControlService control = s.NewControl(doc, await s.RenderersAsync(c.RequestAborted));
            string[] followsMaster = [.. doc.Blocks.Where(b => !b.Skipped && Ample.Engine.Engine.BuildEngine.FollowsMasterSeed(b, control.SeedFor(b.Name) is not null)).Select(b => b.Name)];
            return Ok(new { hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all))), blocks, followsMaster });
        });
        add("GET", "/api/doc/symbols", "documents", "The outline of a song.", false, s is null ? null : async (HttpContext c) => Ok(Queries.DocumentSymbols(s.Parse(await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted)))));
        add("GET", "/api/doc/hover", "documents", "Hover text at a position.", false, s is null ? null : async (HttpContext c) => Ok(new { text = Queries.HoverAt(s.Parse(await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted)), s.Spec, Pos(c, "line"), Pos(c, "col")) }));
        add("GET", "/api/doc/complete", "documents", "Completions at a position.", false, s is null ? null : async (HttpContext c) => Ok(Queries.CompletionAt(s.Parse(await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted)), s.Spec, Pos(c, "line"), Pos(c, "col"), s.SettingHints())));

        // --- build and takes ---
        add("POST", "/api/build", "build", "Builds a song (the cache decides what is rendered); progress goes to the event stream. An optional override { block, name, value } builds with that one parameter replaced, without changing the song, and without events (a draft, for listening to an edit before it is applied).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
                        string? buildId = Opt(b, "buildId");   // the page names its build, so it can ignore the events of a build it stopped
            DocSession d = await OpenFreshAsync(s, events!, Str(b, "path"), c.RequestAborted);
            AmpleDocument parsed = s.ParseExpanded(d);
            IReadOnlyList<SkippedBlock> skippedBlocks = [];
            if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                // a block with an error does not stop the build: it is left out, and the answer says so loudly (plan/editor-tabs.md, decision 8). An error that belongs to no block still stops it.
                string? cleaned = BrokenBlocks.Skip(d.Text, s.Spec, out skippedBlocks);
                if (cleaned is null || skippedBlocks.Count == 0) throw new AmpleException("AMP5287", "The song has errors; fix them first." + UnsavedNote(s));
                parsed = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(cleaned, s.Spec);
                if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error)) throw new AmpleException("AMP5287", "The song has errors; fix them first." + UnsavedNote(s));
            }

            // the first time a song is built with a component, that component is pinned in ample.lock (a later change of its code is AMP2203); written through the command bus
            if (new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")) is { } pinning && pinning.LockWithNewComponents(pinning.Expand(d.Text, s.Spec)) is { } pinned)
            {
                await s.Bus.ExecuteAsync(new WriteFileCommand(Ample.Engine.Library.LockFile.FileName, pinned), c.RequestAborted);
            }

            // a draft: one parameter of the parsed copy is replaced, so the song and its working copy stay as they are
            bool draft = b.TryGetProperty("override", out JsonElement draftOverride) && draftOverride.ValueKind == JsonValueKind.Object;
            if (draft)
            {
                ApplyDraftParameter(parsed, Str(draftOverride, "block"), Str(draftOverride, "name"), Str(draftOverride, "value"));
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            BarRange? range = b.TryGetProperty("range", out JsonElement r) && r.ValueKind == JsonValueKind.Object ? new BarRange(r.GetProperty("firstBar").GetInt32(), r.GetProperty("bars").GetInt32()) : null;
            bool forceRender = b.TryGetProperty("forceRender", out JsonElement force) && force.ValueKind == JsonValueKind.True;
            if (forceRender && (string.IsNullOrWhiteSpace(Opt(b, "only")) || range is not null || parsed.Blocks.FirstOrDefault(block => block.Name == Opt(b, "only"))?.Type is not ("ace-music" or "ai-voice"))) throw new AmpleException("AMP5605", "Force render requires one complete ACE song or voice block.");
            BuildEstimateReport estimate = await StorageApi.EstimateAsync(s, parsed, renderers, control, Opt(b, "intent") ?? "bake", range, Opt(b, "only"), c.RequestAborted);
            if (!estimate.Fits)
            {
                throw new AmpleException("AMP5604", $"This build needs about {StorageApi.Size(estimate.Estimate.Bytes)} and the drive of the takebank has {StorageApi.Size(estimate.FreeBytes)} free. Free space (Projects, Storage can give back about {StorageApi.Size(estimate.ReclaimableBytes)}), move the takebank, or build a smaller part.");
            }

            if (!draft)
            {
                events!.Publish("build.estimate", new { estimate.Estimate.Blocks, estimate.Estimate.Bytes, estimate.Estimate.Seconds, estimate.Estimate.Basis, estimate.Estimate.Builds, estimate.FreeBytes, buildId });
            }

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions(Opt(b, "intent") ?? "bake", range, Opt(b, "only"), 0, renderers, (block, status) => { if (!draft) { events!.Publish("build.progress", new { block, status, buildId }); } }, control, forceRender ? new BlockOverride(Opt(b, "only")!, null, null, true, null) : null, detail => events!.Publish("build.block", new { buildId, block = detail.Block, state = detail.State, fraction = detail.Fraction, steps = detail.Steps.Select(x => new { name = x.Name, fraction = x.Fraction }) })) { ModuleSources = s.ModuleSources() }, c.RequestAborted);
            if (!draft)
            {
                StorageApi.RecordBuild(s, parsed, report, clock.Elapsed.TotalSeconds);
                s.LastBuildAt[Str(b, "path")] = parsed.ParsedAt;
                s.PreviewBuilds[Str(b, "path")] = skippedBlocks.Count > 0 || s.Texts.Dirty().Count > 0;
                events!.Publish("build.done", new { report.Hits, report.Misses, report.MasterTakeId, buildId });
            }

            object[] skippedReport = [.. skippedBlocks.Select(x => (object)new { block = x.Block, status = "skipped", message = $"has an error and was left out of this build: {x.Code} {x.Message} (line {x.Line})" })];
            return Ok(new { report.Hits, report.Misses, report.MasterTakeId, blocks = report.Blocks.Cast<object>().Concat(skippedReport).ToArray(), builtWithErrors = skippedBlocks.Count > 0, unsavedInputs = s.Texts.DirtyWithHashes().Select(x => new { path = x.Path, x.Sha256 }), previewTake = skippedBlocks.Count > 0 || s.Texts.Dirty().Count > 0, skippedBlocks = skippedBlocks.Select(x => new { x.Block, x.Code, x.Message, x.Line }), clockAt = parsed.ParsedAt.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) });
        });
        add("POST", "/api/build/text", "build", "Builds song text that is not a file of the project (the source of a template, to listen to it) as a draft: nothing is changed or announced. only builds up to one block and gives its take.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            AmpleDocument parsed = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(Str(b, "text"), s.Spec);
            if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                throw new AmpleException("AMP5287", "The text has errors; fix them first.");
            }

            string? only = Opt(b, "only");
            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("preview", null, string.IsNullOrWhiteSpace(only) ? null : only, 0, renderers, (block, status) => { }, control, null, detail => { }) { ModuleSources = s.ModuleSources() }, c.RequestAborted);
            return Ok(new { report.MasterTakeId, blocks = report.Blocks });
        });
        // --- storage: overview, clean-up with dry run and progress, soft limit and options ---
        add("GET", "/api/storage", "storage", "What the takebank and the working folders use, what is protected and what could be given back, the options and the state of a clean-up.", false, s is null ? null : () => Ok(StorageApi.Overview(s)));
        add("POST", "/api/storage/plan", "storage", "The dry run of a clean-up: takes and data that would go, per reason and per block, what stays protected, free space before and after.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(StorageApi.Plan(s, StorageReasons(b), !(b.TryGetProperty("includeTemp", out JsonElement t) && t.ValueKind == JsonValueKind.False)));
        });
        add("POST", "/api/storage/clean", "storage", "Starts the clean-up of a dry run in the background (its fingerprint must still match); progress goes to the event stream.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(StorageApi.Start(s, StorageReasons(b), !(b.TryGetProperty("includeTemp", out JsonElement t) && t.ValueKind == JsonValueKind.False), Str(b, "fingerprint"), (name, data) => events!.Publish(name, data)));
        });
        add("POST", "/api/storage/compress", "storage", "Compresses the plain WAV takes of the project in place, losslessly (the takes and their ids do not change), in the background with progress.", true, s is null ? null : () => Ok(StorageApi.StartCompress(s, (name, data) => events!.Publish(name, data))));
        add("POST", "/api/storage/move", "storage", "Moves the take data to another folder or drive in the background, with progress (an empty path moves it back into the project); a stopped move continues when run again.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(StorageApi.StartMove(s, Opt(b, "path"), (name, data) => events!.Publish(name, data)));
        });
        add("GET", "/api/storage/status", "storage", "The state and progress of the clean-up.", false, s is null ? null : () => Ok(StorageApi.Status(s)));
        add("POST", "/api/storage/cancel", "storage", "Stops a running clean-up between items.", false, s is null ? null : () => Ok(StorageApi.Cancel(s)));
        add("POST", "/api/storage/settings", "storage", "Changes the storage options of the project (soft limit, how many older takes stay, keep window, archive folder, intermediates).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            StorageSettings now = StorageSettings.Load(s.Root);
            double Num(string n, double d) => b.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
            string? Txt(string n, string? d) => b.TryGetProperty(n, out JsonElement v) ? (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } x ? x : null) : d;
            StorageSettings next = new(Math.Clamp(Num("softLimitGb", now.SoftLimitGb), 0, 100_000), (int)Math.Clamp(Num("keepSuperseded", now.KeepSuperseded), 0, 50), (int)Math.Clamp(Num("keepMinutes", now.KeepMinutes), 0, 100_000),
                Txt("dataPath", now.DataPath), Txt("archivePath", now.ArchivePath), Txt("intermediates", now.Intermediates) == "wav" ? "wav" : "compressed", !(b.TryGetProperty("silenceRuns", out JsonElement sr) && sr.ValueKind == JsonValueKind.False) && now.SilenceRuns);
            next.Save(s.Root);
            StorageApi.ForgetOverview(s);
            return Ok(new { settings = next });
        });
        add("GET", "/api/build/estimate", "build", "How big and how long the build of a song would be (an educated guess that learns from earlier builds), and whether it fits on the drive of the takebank. Nothing is rendered or stored.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            AmpleDocument parsed = s.ParseExpanded(d);
            if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                return Ok(new { ok = false, message = "The song has errors; fix them first." });
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            BuildEstimateReport e = await StorageApi.EstimateAsync(s, parsed, renderers, control, c.Request.Query["intent"].ToString() is { Length: > 0 } i ? i : "bake", null, null, c.RequestAborted);
            return Ok(new { ok = true, blocks = e.Estimate.Blocks, bytes = e.Estimate.Bytes, seconds = e.Estimate.Seconds, basis = e.Estimate.Basis, builds = e.Estimate.Builds, freeBytes = e.FreeBytes, reclaimableBytes = e.ReclaimableBytes, fits = e.Fits, text = e.Text });
        });
        add("GET", "/api/finals", "finals", "The finals of a song (a song without any has one implicit final, full) and what their last render made.", false, s is null ? null : (HttpContext c) => Ok(FinalsApi.List(s, c.Request.Query["song"].ToString())));
        add("POST", "/api/finals", "finals", "Adds a final to a song or changes its definition: the first bar, how many bars, a fade in (beats), a fade out (bars) and the format.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(FinalsApi.Upsert(s, Str(b, "song"), b));
        });
        add("POST", "/api/finals/delete", "finals", "Removes a final from a song (an exported file stays).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(FinalsApi.Remove(s, Str(b, "song"), Str(b, "name")));
        });
        add("POST", "/api/finals/render", "finals", "Renders one final on purpose: the estimate and the disk check, the build, then the kept bars with their fades exported to renders/.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            return Ok(await FinalsApi.RenderAsync(s, Str(b, "song"), Str(b, "name"), (name, data) => events!.Publish(name, data), EncodeMp3Async, c.RequestAborted));
        });
        add("GET", "/api/build/status", "build", "Which blocks of a song already have a take (a dry run of the build: nothing is rendered or stored). A block is cached (with its take), stale (no take yet), waiting (an input has none) or disabled.", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            AmpleDocument parsed = s.ParseExpanded(d);
            if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                return Ok(new { ok = false, message = "The song has errors; fix them first.", blocks = Array.Empty<object>() });
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            string intent = c.Request.Query["intent"].ToString() is { Length: > 0 } asked ? asked : "bake";
            BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions(intent, null, null, 0, renderers, null, control, null, null) { PlanOnly = true }, c.RequestAborted);
            return Ok(new { ok = true, report.MasterTakeId, blocks = report.Blocks.Select(r => new { r.Block, r.Status, r.TakeId, r.Message }) });
        });
        add("GET", "/api/sound-check", "build", "The sound of the built takes of a song: silent, very quiet, clipping and unheard blocks, the master loudness and values that are cut, each with a fix when there is one (nothing is rendered).", false, s is null ? null : async (HttpContext c) =>
        {
            DocSession d = await s.DocAsync(c.Request.Query["path"].ToString(), c.RequestAborted);
            AmpleDocument parsed = s.ParseExpanded(d);
            if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
            {
                return Ok(new { ok = false, message = "The song has errors; fix them first.", findings = Array.Empty<object>(), master = (object?)null });
            }

            IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(c.RequestAborted);
            ControlService control = s.NewControl(parsed, renderers);
            BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, control, null, null) { PlanOnly = true }, c.RequestAborted);
            (IReadOnlyList<SoundFinding> found, MasterLevels? master) = await SoundAnalysis.AnalyseAsync(parsed, s.Spec, s.Bank, report, c.RequestAborted);
            int notBuilt = report.Blocks.Count(r => r.TakeId is null);
            return Ok(new { ok = true, notBuilt, findings = found.Select(f => new { f.Severity, f.Code, f.Block, f.Message, f.Fix }), master = master is null ? null : new { master.PeakDbfs, master.RmsDbfs, master.Lufs } });
        });
        add("POST", "/api/sound-check/fix", "build", "Puts the fix of a sound finding into the song: a normalize block (fix normalize) or a volume block with a lower gain (fix volume:-3) after the block; what read the block now reads the effect.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            DocSession d = await s.DocAsync(Str(b, "path"), c.RequestAborted);
            string fix = Str(b, "fix"), block = Str(b, "block");
            string text;
            lock (d)
            {
                text = d.Text;
            }

            string? failure;
            string? next = fix == "normalize"
                ? Edits.InsertEffectAfter(text, s.Spec, block, "normalize", block + "-normalize", [("peak", "-3")], out failure)
                : fix.StartsWith("volume:", StringComparison.Ordinal)
                    ? Edits.InsertEffectAfter(text, s.Spec, block, "volume", block + "-lower", [("gain", fix["volume:".Length..])], out failure)
                    : Fail("Unknown fix.", out failure);
            if (next is null)
            {
                throw new AmpleException("AMP5286", failure ?? "The fix could not be put into the song.");
            }

            lock (d)
            {
                if (!string.Equals(d.Text, text, StringComparison.Ordinal))
                {
                    throw new AmpleException("AMP5280", "The document changed while the fix was prepared; try again.");
                }

                d.Text = next;
                d.Version++;
            }

            events!.Publish("doc.changed", new { d.Path, d.Version });
            return Ok(DocView(d, s.Parse(d), s));
        });
        add("GET", "/api/takes", "takes", "Lists takes, newest first (optionally of one block).", false, s is null ? null : () => Ok(s.Bank.ListTakes().Select(t => new { t.TakeId, t.PlanId, t.Created, t.Tombstoned, t.TombstoneReason, data = t.Data })));
        add("GET", "/api/takes/{id}", "takes", "One take with its plan and any missing data.", false, s is null ? null : (string id) =>
        {
            TakeRead r = s.Bank.ReadTake(s.Bank.ResolveTakeId(id));
            return Ok(new { r.Take, plan = JsonDocument.Parse(r.Plan.CanonicalJson).RootElement, missing = r.Missing, audioDataId = AudioDataId(s, r.Take) });
        });
        add("GET", "/api/takes/{id}/verify", "takes", "Rehashes the data of a take.", false, s is null ? null : async (string id, HttpContext c) => Ok(await s.Bank.VerifyAsync(s.Bank.ResolveTakeId(id), c.RequestAborted)));
        add("GET", "/api/data/{id}", "takes", "A stored blob (supports range requests for audio; WAV data is served as audio/wav).", false, s is null ? null : (string id) =>
        {
            Stream data = s.Bank.OpenData(id);
            byte[] head = new byte[4];
            int got = data.Read(head, 0, 4);
            data.Position = 0;
            return Results.File(data, got == 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' ? "audio/wav" : "application/octet-stream", enableRangeProcessing: true);
        });
        add("GET", "/api/takes/{id}/download", "takes", "Downloads a take as its original WAV or as a real MP3 encoded by ffmpeg (role=<output name> picks one output of the take, such as body or loop-region).", false, s is null ? null : async (string id, HttpContext c) =>
        {
            TakeRead take = s.Bank.ReadTake(s.Bank.ResolveTakeId(id));
            string role = c.Request.Query["role"].ToString();
            string audio = (role.Length > 0 ? take.Take.Data.FirstOrDefault(d => d.Role == role)?.DataId ?? throw new AmpleException("AMP5243", $"This take has no output named \"{role}\".") : AudioDataId(s, take.Take)) ?? throw new AmpleException("AMP5243", "This take has no audio.");
            string format = c.Request.Query["format"].ToString().ToLowerInvariant();
            if (format == "wav")
            {
                return Results.File(s.Bank.OpenData(audio), "audio/wav", take.Take.TakeId + ".wav", enableRangeProcessing: true);
            }

            if (format == "mp3")
            {
                (byte[] mp3, string encoder) = await EncodeMp3Async(s.Bank.OpenData(audio), c.RequestAborted).ConfigureAwait(false);
                c.Response.Headers["X-Ample-Mp3-Encoder"] = encoder;
                return Results.File(mp3, "audio/mpeg", take.Take.TakeId + ".mp3");
            }

            throw new AmpleException("AMP5285", "Download format must be wav or mp3.");
        });
        add("POST", "/api/takes/{id}/export", "takes", "Writes the audio of a take to a file in the project (WAV as it is, or MP3 encoded by ffmpeg) through the command bus; an existing name is refused. With download true nothing is written: the file comes back as a download.", true, s is null ? null : async (string id, HttpContext c) =>
        {
            JsonElement b = await Body(c);
            TakeRead take = s.Bank.ReadTake(s.Bank.ResolveTakeId(id));
            string? wanted = Opt(b, "role");
            string audio = (wanted is { Length: > 0 } ? take.Take.Data.FirstOrDefault(d => d.Role == wanted)?.DataId : AudioDataId(s, take.Take)) ?? throw new AmpleException("AMP5243", wanted is { Length: > 0 } ? $"This take has no '{wanted}' audio: name channels in the lanes of the block." : "This take has no audio.");
            string format = (Opt(b, "format") ?? "wav").ToLowerInvariant();
            if (format is not ("wav" or "mp3")) throw new AmpleException("AMP5285", "The format must be wav or mp3.");
            string name = (Opt(b, "name") ?? take.Take.TakeId).Trim();
            if (name.Length is 0 or > 100 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ' ' or '.' or '(' or ')'))) throw new AmpleException("AMP5285", "A file name is letters, digits, spaces, _, -, . and ( ) only.");
            if (b.TryGetProperty("download", out JsonElement downloadFlag) && downloadFlag.ValueKind == JsonValueKind.True)
            {
                byte[] file;
                if (format == "mp3")
                {
                    (file, _) = await EncodeMp3Async(s.Bank.OpenData(audio), c.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    await using Stream source = s.Bank.OpenData(audio);
                    using MemoryStream held = new();
                    await source.CopyToAsync(held, c.RequestAborted).ConfigureAwait(false);
                    file = held.ToArray();
                }

                return Results.File(file, format == "mp3" ? "audio/mpeg" : "audio/wav", name + "." + format);
            }

            string folder = (Opt(b, "folder") ?? "renders").Replace('\\', '/').Trim('/');
            string relative = (folder.Length == 0 ? name : folder + "/" + name) + "." + format;
            if (relative.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".ample", StringComparison.OrdinalIgnoreCase)) || HasProjectLink(s.Fs, s.Root, relative)) throw new AmpleException("AMP5285", "The target must be a normal project folder.");
            string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
            if (s.Fs.FileExists(full) || s.Fs.DirectoryExists(full)) throw new AmpleException("AMP5243", $"There is already a file called {relative}.");
            string built = string.Empty;
            string[] parts = relative.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                built = built.Length == 0 ? parts[i] : built + "/" + parts[i];
                if (!s.Fs.DirectoryExists(ProjectPath.Resolve(s.Fs, s.Root, built))) await s.Bus.ExecuteAsync(new CreateFolderCommand(built), c.RequestAborted).ConfigureAwait(false);
            }

            byte[] bytes;
            string encoder = "none";
            if (format == "mp3")
            {
                (bytes, encoder) = await EncodeMp3Async(s.Bank.OpenData(audio), c.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await using Stream data = s.Bank.OpenData(audio);
                using MemoryStream copy = new();
                await data.CopyToAsync(copy, c.RequestAborted).ConfigureAwait(false);
                bytes = copy.ToArray();
            }

            await s.Bus.ExecuteAsync(new ImportFileCommand(relative, bytes), c.RequestAborted).ConfigureAwait(false);
            events!.Publish("files.changed", new { paths = new[] { relative } });
            return Ok(new { path = relative, format, encoder, sizeBytes = bytes.Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) });
        });
        add("GET", "/api/takes/{id}/peaks", "takes", "Min and max pairs of the audio of a take (points pairs, values -1 to 1; role=<output name> picks one output, such as loop-region) for drawing a waveform; computed in one pass over the file and cached.", false, s is null ? null : (string id, HttpContext c) =>
        {
            TakeRead r = s.Bank.ReadTake(s.Bank.ResolveTakeId(id));
            string role = c.Request.Query["role"].ToString();
            string audio = (role.Length > 0 ? r.Take.Data.FirstOrDefault(d => d.Role == role)?.DataId ?? throw new AmpleException("AMP5243", $"This take has no output named \"{role}\".") : AudioDataId(s, r.Take)) ?? throw new AmpleException("AMP5243", "This take has no audio.");
            int points = int.TryParse(c.Request.Query["points"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) ? Math.Clamp(p, 16, 4000) : 800;
            object peaks = PeakCache.GetOrAdd($"{audio}:{points}", _ => PeaksFromDiskOrCompute(s, audio, points));
            return Ok(new { take = r.Take.TakeId, dataId = audio, peaks });
        });
        add("GET", "/api/takes/{id}/control", "takes", "The curve of a control take (a follow, curve or map block), cut down to at most points values for drawing a lane: the first bar it belongs to, its unit and the values (a general control from 0 to 1000, a typed one in its unit times 1000). role=<output name> picks one output (level or gate).", false, s is null ? null : (string id, HttpContext c) =>
        {
            TakeRead r = s.Bank.ReadTake(s.Bank.ResolveTakeId(id));
            string role = c.Request.Query["role"].ToString();
            TakeDataRef data = (role.Length > 0 ? r.Take.Data.FirstOrDefault(d => d.Role == role) : r.Take.Data.FirstOrDefault(d => d.Role == "level")) ?? throw new AmpleException("AMP5243", "This take has no control output.");
            using Stream stream = s.Bank.OpenData(data.DataId);
            using MemoryStream bytes = new();
            stream.CopyTo(bytes);
            if (!Ample.Audio.Engine.ControlCurve.IsControl(bytes.ToArray())) throw new AmpleException("AMP5243", "This take is not a control.");
            Ample.Audio.Engine.ControlCurve curve = Ample.Audio.Engine.ControlCurve.Decode(bytes.ToArray());
            int points = int.TryParse(c.Request.Query["points"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) ? Math.Clamp(p, 16, 4000) : 600;
            int step = Math.Max(1, (int)Math.Ceiling(curve.Values.Length / (double)points));
            int[] values = [.. Enumerable.Range(0, (curve.Values.Length + step - 1) / step).Select(i => curve.Values[Math.Min(curve.Values.Length - 1, i * step)])];
            return Ok(new { take = r.Take.TakeId, firstBar = curve.FirstBar, unit = curve.Unit, rate = Ample.Audio.Engine.ControlCurve.Rate / step, count = curve.Values.Length, values });
        });
        add("GET", "/api/plans/{id}", "takes", "A stored plan (the complete recipe).", false, s is null ? null : (string id) => Ok(JsonDocument.Parse(s.Bank.GetPlan(id).CanonicalJson).RootElement));

        // --- history ---
        add("GET", "/api/history", "history", "Lines, tags and the newest snapshots of the current line.", false, s is null ? null : async (HttpContext c) => Ok(new { current = s.History.CurrentLine, dirty = await s.History.IsDirtyAsync(c.RequestAborted), lines = s.History.Lines(), tags = s.History.Tags().Select(t => new { t.Name, t.NodeId, t.Reason }), log = s.History.Log(s.History.CurrentLine, 30).Select(n => new { n.NodeId, n.Kind, n.Message, n.At, files = n.Files.Count }) }));
        add("POST", "/api/history/checkpoint", "history", "Takes a snapshot (nothing happens when nothing changed).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = Obj(await Body(c));
            NodeInfo? n = await s.History.CheckpointAsync(Opt(b, "message") ?? "checkpoint", c.RequestAborted);
            events!.Publish("history.changed", new { kind = "checkpoint" });
            return Ok(new { created = n is not null, node = n?.NodeId });
        });
        add("POST", "/api/history/fork", "history", "Creates a new line.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            LineInfo l = await s.History.ForkAsync(Str(b, "name"), Opt(b, "from"), c.RequestAborted);
            events!.Publish("history.changed", new { kind = "fork" });
            return Ok(l);
        });
        add("POST", "/api/history/go", "history", "Switches the working files to a line (unsaved work is checkpointed first). Open documents are reloaded.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            await s.History.GoAsync(Str(b, "line"), c.RequestAborted);
            s.ForgetDocs();
            events!.Publish("history.changed", new { kind = "go" });
            return Ok(new { current = s.History.CurrentLine });
        });
        add("GET", "/api/history/graph", "history", "Every snapshot of every line with its parent, plus lines, tags and the current line (the history view draws this).", false, s is null ? null : async (HttpContext c) => Ok(new
        {
            current = s.History.CurrentLine,
            dirty = await s.History.IsDirtyAsync(c.RequestAborted),
            lines = s.History.Lines(),
            tags = s.History.Tags().Select(t => new { t.Name, t.NodeId, t.Reason }),
            nodes = s.History.AllNodes().Select(n => new { n.NodeId, n.ParentId, n.Kind, n.Message, n.At, files = n.Files.Count, reduxRef = n.ReduxSequence is long sequence ? ReduxRecordReference.Compact(sequence) : null }),
        }));
        add("GET", "/api/history/diff", "history", "Which files differ between two states (line, tag or node).", false, s is null ? null : (HttpContext c) => Ok(s.History.Diff(c.Request.Query["from"].ToString(), c.Request.Query["to"].ToString())));
        add("GET", "/api/history/peek", "history", "Reads another state without changing anything.", false, s is null ? null : async (HttpContext c) => Ok(await s.History.PeekAsync(c.Request.Query["ref"].ToString(), c.RequestAborted)));
        add("POST", "/api/history/tag", "history", "Creates a locked tag.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            await s.History.TagAsync(Str(b, "name"), Opt(b, "ref") ?? s.History.CurrentLine, Opt(b, "reason") ?? string.Empty, c.RequestAborted);
            events!.Publish("history.changed", new { kind = "tag" });
            return Ok(new { tagged = Str(b, "name") });
        });
        add("GET", "/api/history/promote-preview", "history", "What promoting garage into studio would change.", false, s is null ? null : () => Ok(s.History.PreviewPromote()));
        add("POST", "/api/history/promote", "history", "Replaces studio with garage (the old studio stays in the history).", true, s is null ? null : async (HttpContext c) =>
        {
            NodeInfo n = await s.History.PromoteAsync(cancellationToken: c.RequestAborted);
            events!.Publish("history.changed", new { kind = "promote" });
            return Ok(new { node = n.NodeId });
        });

        // --- control: seeds, variants, regeneration ---
        add("GET", "/api/control/seed", "control", "A seed with its lock state and history.", false, s is null ? null : async (HttpContext c) =>
        {
            ControlService k = await ControlFor(s, c);
            string block = c.Request.Query["block"].ToString();
            return Ok(new { seed = k.GetSeed(block, "seed"), history = k.SeedHistory(block, "seed") });
        });
        add("POST", "/api/control/seed", "control", "Unlocks, locks, sets or randomizes a seed (a locked seed refuses changes).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Str(b, "path"));
            string block = Str(b, "block");
            string seedName = Opt(b, "name") ?? "seed";
            switch (Str(b, "action"))
            {
                case "unlock": await k.UnlockSeedAsync(block, seedName); break;
                case "lock": await k.LockSeedAsync(block, seedName); break;
                case "set": await k.SetSeedAsync(block, seedName, ulong.Parse(Str(b, "value"), System.Globalization.CultureInfo.InvariantCulture), Opt(b, "reason") ?? "changed in the UI"); break;
                case "randomize": await k.RandomizeSeedAsync(block, seedName, Opt(b, "reason") ?? "randomized in the UI"); break;
                default: throw new AmpleException("AMP5285", "Unknown seed action.");
            }

            return Ok(new { seed = k.GetSeed(block, seedName) });
        });
        add("GET", "/api/control/block", "control", "Everything the controls need for one block: freeze level, seed with its history, selected take, variants, the explainer and open regeneration decisions.", false, s is null ? null : async (HttpContext c) =>
        {
            string path = c.Request.Query["path"].ToString();
            string name = c.Request.Query["block"].ToString();
            AmpleDocument doc = s.ParseExpanded(await s.DocAsync(path, c.RequestAborted));
            BlockNode block = doc.Blocks.FirstOrDefault(x => !x.Skipped && x.Name == name) ?? throw new AmpleException("AMP5243", $"There is no block named '{name}'.");
            ControlService k = s.NewControl(doc, await s.RenderersAsync(c.RequestAborted));
            Explainer ex = k.Explain(block);
            return Ok(new
            {
                block = name,
                level = ControlService.LevelOf(block),
                variantFields = VariantFieldPlanner.Fields(block, s.Spec.Templates[block.Type]),
                unsupportedVariantFields = VariantFieldPlanner.UnsupportedFields(s.Spec.Templates[block.Type]),
                seeds = SeedNames(s, block).Select(n => { SeedState state = k.GetSeed(name, n); return new { name = n, state = new { state.Name, state.Value, valueText = state.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), state.Locked, state.Origin }, history = k.SeedHistory(name, n) }; }),
                selected = k.SelectedTake(name),
                variants = k.Variants(name),
                explain = new { ex.Warnings, ex.Effects },
                warning = ControlService.RegenerationWarning,
                pending = k.PendingRegens().Where(p => p.Block == name).Select(p => new { p.Id, p.OldTake, p.NewTake, p.Differences }),
            });
        });
        add("POST", "/api/control/variants/keep", "control", "Keeps a variant (it stays selectable).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Opt(b, "path"));
            await k.KeepAsync(Str(b, "block"), Str(b, "take"));
            return Ok(k.Variants(Str(b, "block")));
        });
        add("POST", "/api/control/variants/discard", "control", "Discards a variant (the take stays in the bank until cleanup).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Opt(b, "path"));
            await k.DiscardAsync(Str(b, "block"), Str(b, "take"), Opt(b, "reason") ?? "discarded in the UI");
            return Ok(k.Variants(Str(b, "block")));
        });
        add("POST", "/api/control/variants/preview", "control", "Previews the deterministic field changes for a variant request without rendering.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string path = Str(b, "path"), name = Str(b, "block"), kind = Opt(b, "kind") ?? "variant";
            AmpleDocument doc = s.ParseExpanded(await s.DocAsync(path, c.RequestAborted));
            BlockNode block = doc.Blocks.FirstOrDefault(node => !node.Skipped && node.Name == name) ?? throw new AmpleException("AMP5243", $"There is no block named '{name}'.");
            TemplateSpec template = s.Spec.Templates[block.Type];
            ControlService k = s.NewControl(doc, await s.RenderersAsync(c.RequestAborted));
            int count = b.TryGetProperty("count", out JsonElement countValue) ? countValue.GetInt32() : 2;
            return Ok(new { fields = k.PreviewVariants(doc, name, count, kind, VariantFieldNames(b, block, template), template) });
        });
        add("POST", "/api/control/variants", "control", "Makes one or more individually indexable takes of a block.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            string path = Str(b, "path"), name = Str(b, "block"), kind = Opt(b, "kind") ?? "variant";
            ControlService k = await ControlFor(s, c, path);
            DocSession current = await s.DocAsync(path, c.RequestAborted);
            if (b.TryGetProperty("baseVersion", out JsonElement baseVersion) && baseVersion.GetInt32() != current.Version) throw new AmpleException("AMP5261", "The song changed while variants were being made; completed takes are kept, and remaining takes were stopped.");
            AmpleDocument doc = s.ParseExpanded(current);
            BlockNode block = doc.Blocks.FirstOrDefault(node => !node.Skipped && node.Name == name) ?? throw new AmpleException("AMP5243", $"There is no block named '{name}'.");
            TemplateSpec template = s.Spec.Templates[block.Type];
            int count = b.TryGetProperty("count", out JsonElement n) ? n.GetInt32() : 3;
            int startIndex = b.TryGetProperty("startIndex", out JsonElement start) ? start.GetInt32() : 0;
            await k.RequestVariantsAsync(doc, name, count, startIndex, kind, VariantFieldNames(b, block, template), template, c.RequestAborted);
            return Ok(k.Variants(name));
        });
        add("POST", "/api/control/select", "control", "Selects a take for a block.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Opt(b, "path"));
            await k.SelectTakeAsync(Str(b, "block"), Str(b, "take"));
            return Ok(k.Variants(Str(b, "block")));
        });
        add("POST", "/api/control/regenerate", "control", "Regenerates a block safely: the selected take stays; a different result becomes an open decision (default: restore).", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Str(b, "path"));
            AmpleDocument doc = s.ParseExpanded(await s.DocAsync(Str(b, "path"), c.RequestAborted));
            return Ok(new { warning = ControlService.RegenerationWarning, result = await k.RegenerateAsync(doc, Str(b, "block"), b.TryGetProperty("seed", out JsonElement sd) ? ulong.Parse(sd.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null, c.RequestAborted) });
        });
        add("POST", "/api/control/verify-reproduction", "control", "Renders with the reference take's seed and current software/settings, compares output bytes and warns on recipe or version changes. Keeps the reference take selected.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Str(b, "path"));
            AmpleDocument doc = s.ParseExpanded(await s.DocAsync(Str(b, "path"), c.RequestAborted));
            return Ok(await k.VerifyReproductionAsync(doc, Str(b, "block"), c.RequestAborted));
        });
        add("POST", "/api/control/decide", "control", "Answers an open regeneration decision: restore (default) or keep-variant. Replacing needs the two-step act.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            ControlService k = await ControlFor(s, c, Opt(b, "path"));
            await k.DecideAsync(Str(b, "id"), Opt(b, "decision") ?? "restore");
            return Ok(new { pending = k.PendingRegens().Count });
        });

        // --- dangerous acts: always two steps ---
        add("POST", "/api/acts/drop-line", "acts", "Dry run of dropping a line; returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            DropLineAct act = new(s.History, Str(await Body(c), "line"));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/cleanup", "acts", "Dry run of the history cleanup; returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            CleanupAct act = new(s.History);
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/retag", "acts", "Dry run of moving or removing a tag.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            RetagAct act = new(s.History, Str(b, "tag"), Opt(b, "to"));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/replace-take", "acts", "Dry run of replacing the selected take with a regenerated one.", false, s is null ? null : async (HttpContext c) =>
        {
            ControlService k = await ControlFor(s, c, Opt(await Body(c), "path"));
            ReplaceTakeAct act = new(k, Str(await Body(c), "id"));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.ExecuteAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/prune-backups", "acts", "Dry run of pruning old backups (the last good one stays); returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            Ample.Storage.Backup.PruneBackupsAct act = new(new Ample.Storage.Backup.BackupStore(Str(b, "target"), s.Clock), new Ample.Storage.Backup.PruneRules(b.TryGetProperty("keepLast", out JsonElement k) ? k.GetInt32() : 5, b.TryGetProperty("keepDaily", out JsonElement d) ? d.GetInt32() : 0));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/restore-in-place", "acts", "Dry run of restoring a backup into this project; returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            Ample.Storage.Backup.RestoreInPlaceAct act = new(new Ample.Storage.Backup.BackupStore(Str(b, "target"), s.Clock), Str(b, "id"), s.Root);
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.RunAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/unlink-mount", "acts", "Dry run of unlinking a folder from the library (no file is touched); returns a token and what to type.", false, s is null ? null : async (HttpContext c) =>
        {
            Ample.Storage.Library.UnlinkMountAct act = new(s.Library, Str(await Body(c), "mount"));
            return await DryRun(s, c, act.Title, act.Acceptance, act.DryRunAsync, (typed, why) => act.ExecuteAsync(s.Journal, s.Context, "ui", typed, why).AsTask());
        });
        add("POST", "/api/acts/confirm", "acts", "Second step: confirm a dry run with the token, the typed acceptance and a reason.", true, s is null ? null : async (HttpContext c) =>
        {
            JsonElement b = await Body(c);
            await s.ConfirmActAsync(Str(b, "token"), Str(b, "typed"), Opt(b, "reason") ?? string.Empty);
            events!.Publish("history.changed", new { kind = "act" });
            return Ok(new { done = true });
        });

        // --- game score (adaptive music for a game) ---
        GameRoutes.Declare(add, s, events);

        // --- events and shutdown ---
        add("GET", "/api/events", "events", "Server-sent events: build.progress, build.done, doc.changed, history.changed, files.changed.", false, s is null ? null : async (HttpContext c) =>
        {
            c.Response.ContentType = "text/event-stream";
            await c.Response.WriteAsync(": connected\n\n", c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
            (System.Threading.Channels.ChannelReader<string> reader, IDisposable sub) = events!.Subscribe();
            using (sub)
            {
                try
                {
                    await foreach (string message in reader.ReadAllAsync(c.RequestAborted))
                    {
                        await c.Response.WriteAsync(message, c.RequestAborted);
                        await c.Response.Body.FlushAsync(c.RequestAborted);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The client went away.
                }
            }
        });
        add("POST", "/api/server/shutdown", "server", "Stops the server and releases the project.", true, s is null ? null : (HttpContext c) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await app!.StopAsync();
            });
            return Ok(new { stopping = true });
        });
    }

    private static int Pos(HttpContext c, string name) => int.Parse(c.Request.Query[name].ToString(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads a default ACE model of the project settings: the old one when the request does not mention it, empty to clear it, otherwise a family keyword or a .gguf file name.</summary>
    private static string DefaultModel(JsonElement faer, string name, string blockType, string old)
    {
        if (!faer.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return old;
        }

        string model = value.GetString()!.Trim();
        if (model.Length > 0)
        {
            _ = EngineChoice.Parse("local:" + model, blockType);
        }

        return model;
    }

    /// <summary>Replaces (or adds) one parameter of a block in a document that was just parsed. Used by a draft build, which must not touch the song.</summary>
    private static void ApplyDraftParameter(AmpleDocument doc, string block, string name, string value)
    {
        BlockNode target = doc.Blocks.FirstOrDefault(x => !x.Skipped && x.Name == block) ?? throw new AmpleException("AMP5243", $"There is no block named '{block}'.");
        int index = target.Parameters.FindIndex(p => p.Name == name);
        if (index >= 0)
        {
            target.Parameters[index] = target.Parameters[index] with { Value = value };
        }
        else
        {
            target.Parameters.Add(new Parameter(name, value, -1));
        }
    }

    private static void Check(DocSession d, JsonElement body)
    {
        if (!body.TryGetProperty("baseVersion", out JsonElement v) || v.GetInt32() != d.Version)
        {
            throw new AmpleException("AMP5280", $"The document changed (it is at version {d.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}); reload it and try again.");
        }
    }

    private static async Task<ControlService> ControlFor(ProjectSession s, HttpContext c, string? path = null)
    {
        string p = path ?? c.Request.Query["path"].ToString();
        AmpleDocument doc = p.Length == 0 ? Parser.Parse(string.Empty, s.Spec) : s.Parse(await s.DocAsync(p, c.RequestAborted));
        return s.NewControl(doc, await s.RenderersAsync(c.RequestAborted));
    }

    private static async Task<ProjectManifest> ReadManifest(ProjectSession session, CancellationToken cancellationToken) =>
        ProjectFile.Parse(await File.ReadAllTextAsync(Path.Combine(session.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), cancellationToken).ConfigureAwait(false));

    private static ProjectManifest ReadManifestSync(ProjectSession session) =>
        ProjectFile.Parse(File.ReadAllText(Path.Combine(session.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)));

    private static ProjectTreeItem[] ProjectTree(ProjectSession session, string folder, int depth, bool showHidden = false)
    {
        List<ProjectTreeItem> children = [];
        foreach (string directory in session.Fs.GetDirectories(folder))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals(".ample", StringComparison.OrdinalIgnoreCase) || (!showHidden && name.StartsWith('.')) || session.Fs.GetLinkTarget(directory) is not null) continue;
            string relative = Path.GetRelativePath(session.Root, directory).Replace('\\', '/');
            DateTimeOffset modified = Directory.GetLastWriteTimeUtc(directory);
            children.Add(new ProjectTreeItem(name, relative, "folder", 0, modified, true, depth > 0 ? ProjectTree(session, directory, depth - 1, showHidden) : null));
        }

        foreach (string file in session.Fs.GetFiles(folder))
        {
            string name = Path.GetFileName(file);
            if ((!showHidden && (name.StartsWith('.') || (name.Equals("ample.lock", StringComparison.Ordinal) && Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(session.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)))) || session.Fs.GetLinkTarget(file) is not null) continue;   // ample.lock is kept by the program: it is shown only with hidden=1
            string relative = Path.GetRelativePath(session.Root, file).Replace('\\', '/');
            string extension = Path.GetExtension(name).ToLowerInvariant();
            string kind = extension switch { ".amplet" => "song", ".module" => "module", ".wav" or ".mp3" or ".flac" or ".ogg" => "audio", ".mid" or ".midi" => "midi", ".lyr" or ".lrc" or ".srt" => "lyrics", _ => "file" };
            FileInfo info = new(file);
            children.Add(new ProjectTreeItem(name, relative, kind, info.Length, info.LastWriteTimeUtc, false, null));
        }

        return children.OrderBy(item => item.IsDir ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ProjectSourceFiles(ProjectSession session, string folder)
    {
        foreach (string file in session.Fs.GetFiles(folder))
        {
            if (session.Fs.GetLinkTarget(file) is null) yield return file;
        }

        foreach (string directory in session.Fs.GetDirectories(folder))
        {
            if (Path.GetFileName(directory).Equals(".ample", StringComparison.OrdinalIgnoreCase) || session.Fs.GetLinkTarget(directory) is not null) continue;
            foreach (string file in ProjectSourceFiles(session, directory)) yield return file;
        }
    }

    private static readonly string[] TextExtensions = [".lyr", ".amplelrc", ".lrc", ".chords", ".txt", ".md", ".csv", ".json", ".seq", ".ampleseq", ".amplec"];

    /// <summary>The optional set.json next to the WAVs (layersOffered, layersTaken, roundRobinsTaken); null when absent or unreadable.</summary>
    private static object? SetMeta(string dir)
    {
        string file = Path.Combine(dir, "set.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            int Num(string n) => d.RootElement.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
            return new { layersOffered = Num("layersOffered"), layersTaken = Num("layersTaken"), roundRobinsTaken = Num("roundRobinsTaken") };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Creates a new song with the project defaults of the plugin settings on a settings line (shared by the songs route and the new-file route).</summary>
    private static async Task<IResult> CreateSongAsync(ProjectSession s, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new AmpleException("AMP5285", "A song name is letters, digits, _ and - only.");
        }

        string rel = name + ".amplet";
        if (File.Exists(ProjectPath.Resolve(s.Root, rel)))
        {
            throw new AmpleException("AMP5285", $"There is already a song called {rel}.");
        }

        List<string> pairs = [];
        string defaults = Path.Combine(s.Root, ".ample", "plugin-settings.json");
        if (File.Exists(defaults) && JsonNode.Parse(File.ReadAllText(defaults)) is JsonObject all)
        {
            foreach ((string plugin, JsonNode? values) in all.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (values is JsonObject o)
                {
                    pairs.AddRange(o.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => p.Value is not null).Select(p => $"{plugin}.{p.Key} {Ample.Language.Edits.Quote(p.Value is JsonValue jv && jv.TryGetValue(out string? text) ? text : p.Value!.ToJsonString())}"));
                }
            }
        }

        string text2 = $"composition \"{name}\" {{ ample 1  tempo 100  master-seed {s.Manifest.MasterSeed} }}\n" + (pairs.Count > 0 ? $"settings {{ {string.Join("  ", pairs)} }}\n" : string.Empty);
        await s.Bus.ExecuteAsync(new WriteFileCommand(rel, text2), ct);
        return Ok(new { path = rel, settings = pairs.Count });
    }

    private static bool TextFile(string rel) => !rel.Replace('\\', '/').StartsWith(".ample", StringComparison.Ordinal) && TextExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant());

    private static string? ReduxRecordPath(JournalRecord record)
    {
        try { using JsonDocument payload = JsonDocument.Parse(record.PayloadJson); return payload.RootElement.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<string> ReduxSongHashes(JournalRecord record, string path)
    {
        if (record.PreImageJson is null) yield break;
        JsonDocument image;
        try { image = JsonDocument.Parse(record.PreImageJson); }
        catch (JsonException) { yield break; }
        using (image)
        {
            JsonElement root = image.RootElement;
            if (!root.TryGetProperty("path", out JsonElement imagePath) || imagePath.ValueKind != JsonValueKind.String || imagePath.GetString() != path || !root.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String) yield break;
            yield return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.GetString() ?? string.Empty)));
        }
    }

    private static object LibraryView(ProjectSession s)
    {
        IReadOnlyList<Ample.Storage.Library.AssetRecord> assets = s.Library.Assets();
        return new
        {
            home = s.Library.Home,
            mounts = s.Library.Mounts(s.Manifest.Id.ToString("D")),
            assets = assets.Take(5000),
            truncated = assets.Count > 5000,
            duplicates = s.Library.Duplicates().Select(g => g.Select(a => a.Uuid)),
        };
    }

    private static async Task AddProjectMount(ProjectSession s, string mountId, CancellationToken ct)
    {
        string file = Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
        ProjectManifest m = ProjectFile.Parse(await File.ReadAllTextAsync(file, ct));
        if (!m.Settings.Mounts.Contains(mountId))
        {
            await s.Bus.ExecuteAsync(new SetSettingsCommand(m.Settings with { Mounts = [.. m.Settings.Mounts, mountId] }), ct);
        }
    }

    private static async Task<IResult> DryRun(ProjectSession s, HttpContext c, string title, string acceptance, Func<CommandContext, CancellationToken, ValueTask<DryRunResult>> dry, Func<string, string, Task> run)
    {
        DryRunResult result = await dry(s.Context, c.RequestAborted);
        string token = s.AddAct(title, acceptance, run);
        return Ok(new { token, title, acceptance, impact = result.Impact, backupSuggestion = result.BackupSuggestion, note = "Confirm with POST /api/acts/confirm {token, typed, reason}." });
    }
}
