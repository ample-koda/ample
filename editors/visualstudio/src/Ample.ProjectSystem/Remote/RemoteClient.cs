using System.Net;
#if !NET5_0_OR_GREATER
using System.Net.Http;
#endif
using System.Text;

namespace Ample.VisualStudio.Remote;

/// <summary>How to reach the AMPLE of a server.</summary>
public enum RemoteMode
{
    /// <summary>Straight to the https address of the server.</summary>
    Direct,

    /// <summary>Through an SSH tunnel to the port the server's AMPLE listens on.</summary>
    Tunnel,
}

/// <summary>What the remote client needs to know. The password is asked from <see cref="PasswordProvider"/> and never kept in a file.</summary>
public sealed class RemoteOptions
{
    /// <summary>Gets or sets the address of the AMPLE, for example https://ample.example.org (direct) or http://127.0.0.1:55077 on the server (tunnel).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets how to connect.</summary>
    public RemoteMode Mode { get; set; }

    /// <summary>Gets or sets the SSH target for a tunnel, for example user and host of your server.</summary>
    public string SshHost { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name of the server's AMPLE.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets where the password comes from (the credential store, or a prompt).</summary>
    public System.Func<string?> PasswordProvider { get; set; } = () => null;

    /// <summary>Gets or sets the waits between attempts after a lost connection (the last one repeats).</summary>
    public System.TimeSpan[] Backoff { get; set; } = [System.TimeSpan.FromSeconds(1), System.TimeSpan.FromSeconds(2), System.TimeSpan.FromSeconds(4), System.TimeSpan.FromSeconds(8)];

    /// <summary>Gets or sets how many attempts a request gets after a lost connection.</summary>
    public int Attempts { get; set; } = 5;
}

/// <summary>
/// One connection to the AMPLE of a server: logs in (user and password give a session token), sends requests with the token, logs in again when the token
/// has expired (401), and after a lost connection tries again with growing waits (restarting the SSH tunnel when there is one). It reports what it
/// is doing through <see cref="Status"/>, so the caller can show it in the status bar and never needs a modal dialog.
/// </summary>
public sealed class RemoteClient : System.IDisposable
{
    private readonly RemoteOptions options;
    private readonly HttpClient http;
    private readonly System.Threading.SemaphoreSlim gate = new(1, 1);
    private SshTunnel? tunnel;
    private string? token;
    private string? baseUrl;

    /// <summary>Creates a client; nothing is connected until the first request.</summary>
    /// <param name="options">Where and how to connect.</param>
    /// <param name="handler">A message handler to send through (tests); null for the normal one.</param>
    public RemoteClient(RemoteOptions options, HttpMessageHandler? handler = null)
    {
        this.options = options;
        http = handler is null ? new HttpClient { Timeout = System.TimeSpan.FromMinutes(5) } : new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>Raised with a short human sentence about what the client is doing or what went wrong (a lost connection, a wrong password).</summary>
    public event System.Action<string>? Status;

    /// <summary>Gets the address requests go to (the local end of the tunnel, or the server address).</summary>
    public string? BaseUrl => baseUrl;

    /// <summary>Sends a request (made by <paramref name="create"/>, called again for a retry) with the session token.</summary>
    public async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(System.Func<string, HttpRequestMessage> create, System.Threading.CancellationToken cancellationToken = default)
    {
        System.Exception? last = null;
        for (int attempt = 0; attempt < options.Attempts; attempt++)
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                HttpRequestMessage request = create(baseUrl!);
                request.Headers.Add("X-Ample-Token", token);
                HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    token = null;   // expired: log in again once, with the same attempt count
                    Report("The session ended; signing in again.");
                    continue;
                }

                return response;
            }
            catch (RemoteAuthException)
            {
                throw;
            }
            catch (System.Exception ex) when (ex is HttpRequestException or System.IO.IOException or System.Net.Sockets.SocketException or System.Threading.Tasks.TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                System.TimeSpan wait = options.Backoff[System.Math.Min(attempt, options.Backoff.Length - 1)];
                Report($"The connection to the server was lost ({ex.Message.Split('\n')[0].Trim()}); trying again in {wait.TotalSeconds:0} s.");
                tunnel?.Dispose();
                tunnel = null;
                baseUrl = null;
                await System.Threading.Tasks.Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new RemoteConnectionException("The server could not be reached after " + options.Attempts + " attempts.", last);
    }

    /// <summary>Convenience: GET a path and give the body as text; a non-success status throws with the server's answer.</summary>
    public async System.Threading.Tasks.Task<string> GetStringAsync(string path, System.Threading.CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(b => new HttpRequestMessage(HttpMethod.Get, b + path), cancellationToken).ConfigureAwait(false);
        string body = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RemoteRequestException((int)response.StatusCode, body);
        return body;
    }

    /// <summary>GET a path and give the body as bytes (a file); a non-success status throws with the server's answer.</summary>
    public async System.Threading.Tasks.Task<byte[]> GetBytesAsync(string path, System.Threading.CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(b => new HttpRequestMessage(HttpMethod.Get, b + path), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RemoteRequestException((int)response.StatusCode, await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false));
#if NET5_0_OR_GREATER
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
    }

    /// <summary>POST a JSON text and give the answer as text; a non-success status throws with the server's answer.</summary>
    public async System.Threading.Tasks.Task<string> PostJsonAsync(string path, string json, System.Threading.CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            b => new HttpRequestMessage(HttpMethod.Post, b + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, cancellationToken).ConfigureAwait(false);
        string body = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RemoteRequestException((int)response.StatusCode, body);
        return body;
    }

    /// <summary>POST bytes (a file) to a path; a non-success status throws with the server's answer.</summary>
    public async System.Threading.Tasks.Task PostBytesAsync(string path, byte[] bytes, System.Threading.CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(
            b => new HttpRequestMessage(HttpMethod.Post, b + path) { Content = new ByteArrayContent(bytes) }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RemoteRequestException((int)response.StatusCode, await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Signs in now (a request signs in by itself when needed).</summary>
    public async System.Threading.Tasks.Task ConnectAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        // a first harmless request: it signs in, and a lost connection gets the same retries and messages as any other request
        await GetStringAsync("/api/ping", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the tunnel (when there is one) and releases the connection.</summary>
    public void Dispose()
    {
        tunnel?.Dispose();
        http.Dispose();
        gate.Dispose();
    }

    private static System.Threading.Tasks.Task<string> ReadAsync(HttpContent content, System.Threading.CancellationToken cancellationToken) =>
#if NET5_0_OR_GREATER
        content.ReadAsStringAsync(cancellationToken);
#else
        content.ReadAsStringAsync();
#endif

    private void Report(string message) => Status?.Invoke(message);

    private async System.Threading.Tasks.Task EnsureConnectedAsync(System.Threading.CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (baseUrl is null)
            {
                if (options.Mode == RemoteMode.Tunnel)
                {
                    Report("Opening the SSH tunnel to " + options.SshHost + " ...");
                    tunnel = await SshTunnel.StartAsync(options.SshHost, options.Url, cancellationToken).ConfigureAwait(false);
                    baseUrl = tunnel.LocalUrl;
                }
                else
                {
                    baseUrl = options.Url.TrimEnd('/');
                }
            }

            if (token is null)
            {
                string password = options.PasswordProvider() ?? throw new RemoteAuthException("There is no password for " + options.User + " on this server; connect again and fill it in.");
                string json = "{\"user\":" + Quote(options.User) + ",\"password\":" + Quote(password) + ",\"stamp\":" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                using HttpRequestMessage login = new(HttpMethod.Post, baseUrl + "/api/login") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                using HttpResponseMessage response = await http.SendAsync(login, cancellationToken).ConfigureAwait(false);
                string body = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new RemoteAuthException("The server refused the user or password (" + options.User + ").");
                if ((int)response.StatusCode == 429) throw new RemoteAuthException("Too many wrong attempts; the server asks to wait a minute.");
                if (!response.IsSuccessStatusCode) throw new RemoteRequestException((int)response.StatusCode, body);
                token = ReadToken(body) ?? throw new RemoteRequestException((int)response.StatusCode, "The server answered the sign-in without a token.");
                Report("Signed in to " + baseUrl + " as " + options.User + ".");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? ReadToken(string body)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(body, "\"token\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Quote(string value)
    {
        StringBuilder sb = new("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append(c);
        }

        return sb.Append('"').ToString();
    }
}

/// <summary>The server refused the sign-in (or there is no password): asking again is the only way on, so no retry.</summary>
public sealed class RemoteAuthException(string message) : System.Exception(message);

/// <summary>The server could not be reached, also after the retries.</summary>
public sealed class RemoteConnectionException(string message, System.Exception? inner) : System.Exception(message, inner);

/// <summary>The server answered with an error status.</summary>
public sealed class RemoteRequestException(int status, string body) : System.Exception("The server answered " + status + ": " + body)
{
    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; } = status;
}
