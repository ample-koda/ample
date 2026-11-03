namespace Ample.Storage.Projects;

/// <summary>Checks whether the configured local ACE-Step server accepts health requests.</summary>
public static class AceServerHealth
{
    /// <summary>Checks a health endpoint with the platform HTTP handler.</summary>
    public static Task<AceHealthResult> CheckAsync(Uri endpoint, TimeSpan timeout, CancellationToken cancellationToken) => CheckAsync(endpoint, timeout, new HttpClientHandler(), cancellationToken);

    /// <summary>Checks a health endpoint with a supplied handler, primarily for deterministic tests.</summary>
    public static async Task<AceHealthResult> CheckAsync(Uri endpoint, TimeSpan timeout, HttpMessageHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")) throw new ArgumentException("The ACE-Step endpoint must be an absolute HTTP URL.", nameof(endpoint));
        using HttpClient client = new(handler, true) { Timeout = timeout };
        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint, "health"), cancellationToken).ConfigureAwait(false);
            return new AceHealthResult(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AceHealthResult(false, "timed out");
        }
        catch (HttpRequestException)
        {
            return new AceHealthResult(false, "unreachable");
        }
    }
}

/// <summary>The non-content result of an ACE-Step health request.</summary>
public sealed record AceHealthResult(bool IsHealthy, string? Problem);
