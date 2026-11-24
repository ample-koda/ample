using System.Net;
using Microsoft.AspNetCore.Http;

namespace Ample.Server.Tests;

/// <summary>The visitor's address is read from X-Forwarded-For only when the proxy on this machine sent exactly one address.</summary>
public sealed class ClientAddressTests
{
    private static DefaultHttpContext Request(string peer, string? forwarded)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (forwarded is not null) context.Request.Headers["X-Forwarded-For"] = forwarded;
        return context;
    }

    [Theory]
    [InlineData("127.0.0.1", "203.0.113.7", "203.0.113.7")]
    [InlineData("127.0.0.1", " 203.0.113.7 ", "203.0.113.7")]
    [InlineData("::1", "2001:db8::5", "2001:db8::5")]
    [InlineData("127.0.0.1", null, "127.0.0.1")]
    [InlineData("127.0.0.1", "203.0.113.7, 198.51.100.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "not-an-address", "127.0.0.1")]
    [InlineData("127.0.0.1", "", "127.0.0.1")]
    [InlineData("198.51.100.20", "203.0.113.7", "198.51.100.20")]
    public void Only_one_address_from_a_proxy_on_this_machine_is_believed(string peer, string? forwarded, string expected) =>
        Assert.Equal(expected, ClientAddress.Of(Request(peer, forwarded)));

    [Fact]
    public void Two_header_lines_are_not_believed()
    {
        DefaultHttpContext context = Request("127.0.0.1", null);
        context.Request.Headers.Append("X-Forwarded-For", "203.0.113.7");
        context.Request.Headers.Append("X-Forwarded-For", "198.51.100.1");
        Assert.Equal("127.0.0.1", ClientAddress.Of(context));
    }
}
