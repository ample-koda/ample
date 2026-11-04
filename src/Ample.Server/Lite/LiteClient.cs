using System.Net;
using Microsoft.AspNetCore.Http;

namespace Ample.Server.Lite;

/// <summary>
/// Who a Lite request comes from, for the limits of <see cref="LiteGovernor"/>: two keys, a random cookie that the server hands out (so one browser is one client) and the address.
/// Behind a reverse proxy on the same machine the address is the first one of <c>X-Forwarded-For</c>; that header is believed only when the request itself comes from the loopback
/// (the proxy), and the proxy must overwrite it with the real address of the visitor, never pass on what the visitor sent.
/// </summary>
public static class LiteClient
{
    /// <summary>The name of the cookie.</summary>
    public const string CookieName = "ample-lite-client";

    /// <summary>Gets the keys of the client of a request (and gives the browser its cookie when it has none).</summary>
    /// <param name="context">The request.</param>
    /// <returns>The keys: <c>c:&lt;cookie&gt;</c> and <c>ip:&lt;address&gt;</c>.</returns>
    public static IReadOnlyList<string> Keys(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string id = context.Request.Cookies[CookieName] ?? string.Empty;
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        {
            id = Guid.NewGuid().ToString("N");
            bool secure = context.Request.IsHttps || string.Equals(context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);
            context.Response.Cookies.Append(CookieName, id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = secure, MaxAge = TimeSpan.FromDays(30), Path = "/" });
        }

        return [$"c:{id}", $"ip:{AddressOf(context)}"];
    }

    private static string AddressOf(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null) return "unknown";
        if (IPAddress.IsLoopback(remote) && context.Request.Headers["X-Forwarded-For"].FirstOrDefault() is { Length: > 0 } forwarded)
        {
            string first = forwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out IPAddress? visitor)) return visitor.ToString();
        }

        return remote.ToString();
    }
}
