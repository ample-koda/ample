using System.Net.WebSockets;
using System.Text;
using Ample.Lsp;

namespace Ample.Server;

/// <summary>The WebSocket transport of the language server: every text frame is one JSON-RPC message (no Content-Length headers).</summary>
internal sealed class WebSocketChannel(WebSocket socket) : ILspChannel, IDisposable
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => writeLock.Dispose();

    /// <inheritdoc />
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        for (; ; )
        {
            WebSocketReceiveResult r;
            try
            {
                r = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, r.Count);
            if (message.Length > 64 * 1024 * 1024)
            {
                return null;
            }

            if (r.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string json, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The editor went away.
        }
        finally
        {
            writeLock.Release();
        }
    }
}
