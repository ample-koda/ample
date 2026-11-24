using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;

namespace Ample.Server.Tests;

public sealed class WebSocketChannelTests
{
    /// <summary>One end of a connection between two sockets in this process.</summary>
    private sealed class Duplex(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
    }

    private static (WebSocket Client, WebSocket Server) Connected()
    {
        Pipe toServer = new(), toClient = new();
        Duplex clientEnd = new(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        Duplex serverEnd = new(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        return (WebSocket.CreateFromStream(clientEnd, new WebSocketCreationOptions { IsServer = false }), WebSocket.CreateFromStream(serverEnd, new WebSocketCreationOptions { IsServer = true }));
    }

    [Fact]
    public async Task A_message_within_the_limit_is_read_and_a_longer_one_ends_the_read()
    {
        (WebSocket client, WebSocket server) = Connected();
        using WebSocketChannel channel = new(server, 1024);
        await client.SendAsync(Encoding.UTF8.GetBytes("{\"ok\":true}"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("{\"ok\":true}", await channel.ReadAsync(CancellationToken.None));
        await client.SendAsync(new byte[4096], WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Null(await channel.ReadAsync(CancellationToken.None));
    }
}
