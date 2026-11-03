using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ample.Plugins;

/// <summary>A content-addressed input or output held by Faer.</summary>
public sealed record FaerAssetStatus(bool Present, long Received, long? Size);

/// <summary>A safe, content-free stage reported by a Faer worker.</summary>
public sealed record FaerProgress(string Stage, double? Fraction);

/// <summary>An output artifact that AMPLE must download and verify before storing a take.</summary>
public sealed record FaerOutput(string Role, string Hash, long Size);

/// <summary>The completed output list of a Faer job.</summary>
public sealed record FaerJobResult(IReadOnlyList<FaerOutput> Outputs);

/// <summary>A Faer job state; queued jobs include a one-based queue position.</summary>
public sealed record FaerJobFailure(string Code, string Stage, string Reason, string? Details);

/// <summary>A Faer job state; queued jobs include a one-based queue position.</summary>
public sealed record FaerJobStatus(string Id, string State, int? QueuePosition, FaerProgress? Progress, FaerJobResult? Result, FaerJobFailure? Failure)
{
    /// <summary>Whether Faer returned a completed render from its exact-input cache.</summary>
    public bool CacheHit { get; init; }
}

/// <summary>A request to run a named Faer capability using verified input hashes.</summary>
public sealed record FaerJobRequest(string Capability, IReadOnlyList<string> Inputs, JsonElement? Render)
{
    /// <summary>An optional UUID assigned before upload so cancellation can cover a delayed submission.</summary>
    public string? Id { get; init; }
    /// <summary>Runs the worker even when the exact request is cached, then replaces that cache entry.</summary>
    public bool ForceRender { get; init; }
}

/// <summary>Streams verified assets and controls jobs over a configured local Faer connection.</summary>
public sealed class FaerClient : IDisposable
{
    private const int ChunkBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly string? token;

    /// <summary>Creates a client; the endpoint and optional token come from machine-local configuration, never a project.</summary>
    public FaerClient(HttpClient http, Uri endpoint, string? token)
    {
        this.http = http;
        this.endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute);
        this.token = token;
        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Faer endpoint must use HTTP or HTTPS", nameof(endpoint));
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    private HttpRequestMessage Request(HttpMethod method, string route)
    {
        HttpRequestMessage request = new(method, new Uri(endpoint, route));
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<T> JsonResponseAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Faer returned an empty response");
        }
    }

    /// <summary>Reads Faer's aggregate capability status and ACE model filename inventory without exposing job content.</summary>
    public Task<JsonElement> StatusAsync(CancellationToken cancellationToken) => JsonResponseAsync<JsonElement>(Request(HttpMethod.Get, "v1/status"), cancellationToken);

    /// <summary>Checks whether Faer has a complete, hash-verified asset or a resumable partial upload.</summary>
    public Task<FaerAssetStatus> AssetAsync(string hash, CancellationToken cancellationToken) => JsonResponseAsync<FaerAssetStatus>(Request(HttpMethod.Get, "v1/assets/" + Hash(hash)), cancellationToken);

    /// <summary>Hashes a file, skips an existing verified copy, then uploads only missing chunks.</summary>
    public async Task<string> UploadFileAsync(string file, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        long total = input.Length;
        if (total == 0) throw new InvalidDataException("Faer cannot upload an empty asset");
        FaerAssetStatus state = await AssetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (state.Present)
        {
            if (state.Size != total) throw new InvalidDataException("Faer asset size does not match its hash");
            progress?.Invoke(total, total);
            return hash;
        }

        if (state.Received < 0 || state.Received >= total) throw new InvalidDataException("Faer returned an invalid upload offset");
        input.Position = state.Received;
        progress?.Invoke(state.Received, total);
        byte[] buffer = new byte[ChunkBytes];
        while (input.Position < total)
        {
            long offset = input.Position;
            int length = (int)Math.Min(buffer.Length, total - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            using HttpRequestMessage request = Request(HttpMethod.Put, "v1/assets/" + hash);
            request.Headers.Add("X-Faer-Total", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Faer-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Content = new ByteArrayContent(buffer, 0, length);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            FaerAssetStatus next = await response.Content.ReadFromJsonAsync<FaerAssetStatus>(Json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Faer returned no upload state");
            if (next.Received != offset + length || next.Present != (next.Received == total)) throw new InvalidDataException("Faer returned an invalid upload state");
            progress?.Invoke(next.Received, total);
        }

        return hash;
    }

    /// <summary>Submits a job after its input assets have been uploaded or verified.</summary>
    public Task<FaerJobStatus> SubmitAsync(FaerJobRequest job, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = Request(HttpMethod.Post, "v1/jobs");
        request.Content = JsonContent.Create(job, options: Json);
        return JsonResponseAsync<FaerJobStatus>(request, cancellationToken);
    }

    /// <summary>Reads a job state after a browser or transport reconnect.</summary>
    public Task<FaerJobStatus> JobAsync(string id, CancellationToken cancellationToken) => JsonResponseAsync<FaerJobStatus>(Request(HttpMethod.Get, "v1/jobs/" + Guid.Parse(id).ToString("D")), cancellationToken);

    /// <summary>Asks Faer to cancel one owned job.</summary>
    public Task<FaerJobStatus> CancelAsync(string id, CancellationToken cancellationToken) => JsonResponseAsync<FaerJobStatus>(Request(HttpMethod.Delete, "v1/jobs/" + Guid.Parse(id).ToString("D")), cancellationToken);

    /// <summary>Streams an artifact to a temporary file and publishes it only after size and SHA-256 verification.</summary>
    public async Task DownloadFileAsync(FaerOutput output, string destination, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        string hash = Hash(output.Hash);
        if (output.Size < 1) throw new InvalidDataException("Faer artifact size is invalid");
        string temp = destination + ".faer-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using HttpRequestMessage request = Request(HttpMethod.Get, "v1/assets/" + hash + "/bytes");
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream target = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous);
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[ChunkBytes];
            long received = 0;
            for (int length; (length = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0;)
            {
                received += length;
                if (received > output.Size) throw new InvalidDataException("Faer artifact exceeds its declared size");
                digest.AppendData(buffer, 0, length);
                await target.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                progress?.Invoke(received, output.Size);
            }

            if (received != output.Size || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash) throw new InvalidDataException("Faer artifact failed verification");
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
            File.Move(temp, destination);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Hash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? hash : throw new ArgumentException("Invalid Faer asset hash", nameof(hash));
}
