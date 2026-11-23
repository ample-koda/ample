using System.Net;

namespace Ample.Server;

/// <summary>
/// The address of the person behind a request, for the login lockout. Behind the reverse proxy on this machine every request comes from 127.0.0.1, so one
/// person's wrong guesses would lock everyone out; the proxy puts the real address in <c>X-Forwarded-For</c> (the nginx sites overwrite the header with
/// <c>$remote_addr</c>). The header is only believed when the request really comes from this machine and holds exactly one address: a list means a proxy
/// that appends, where the first entries come from the client and cannot be trusted.
/// </summary>
internal static class ClientAddress
{
    /// <summary>The visitor's address when a proxy on this machine gave exactly one; otherwise the address the connection came from.</summary>
    public static string Of(Microsoft.AspNetCore.Http.HttpContext context)
    {
        IPAddress? peer = context.Connection.RemoteIpAddress;
        if (peer is not null && IPAddress.IsLoopback(peer)
            && context.Request.Headers.TryGetValue("X-Forwarded-For", out Microsoft.Extensions.Primitives.StringValues values) && values.Count == 1
            && values[0] is { Length: > 0 and <= 64 } text && IPAddress.TryParse(text.Trim(), out IPAddress? visitor))
        {
            return visitor.ToString();
        }

        return peer?.ToString() ?? "local";
    }
}
