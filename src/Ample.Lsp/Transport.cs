using System.Text;
using System.Threading.Channels;

namespace Ample.Lsp;

/// <summary>A way to exchange whole JSON-RPC messages with an editor (stdio uses Content-Length framing, a WebSocket one message per frame).</summary>
public interface ILspChannel
{
    /// <summary>Reads the next message, or null when the other side has gone.</summary>
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Sends one message.</summary>
    ValueTask WriteAsync(string json, CancellationToken cancellationToken);
}

/// <summary>The stdio transport of the Language Server Protocol: headers, an empty line, then the body.</summary>
public sealed class StdioChannel(Stream input, Stream output) : ILspChannel, IDisposable
{
    private readonly SemaphoreSlim writeLock = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => writeLock.Dispose();

    /// <inheritdoc />
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        int length = -1;
        for (; ; )
        {
            string? line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line["Content-Length:".Length..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                length = n;
            }
        }

        if (length < 0 || length > 64 * 1024 * 1024)
        {
            return null;
        }

        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int got = await input.ReadAsync(body.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            read += got;
        }

        return Encoding.UTF8.GetString(body);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string json, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n\r\n");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async ValueTask<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        StringBuilder sb = new();
        byte[] one = new byte[1];
        for (; ; )
        {
            int got = await input.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }

            if (one[0] == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }

            sb.Append((char)one[0]);
            if (sb.Length > 8192)
            {
                return null;
            }
        }
    }
}

/// <summary>Two channels wired to each other in memory (tests and the in-process editor).</summary>
public sealed class MemoryChannel : ILspChannel
{
    private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();
    private MemoryChannel? other;

    /// <summary>Creates two connected ends.</summary>
    public static (MemoryChannel Client, MemoryChannel Server) CreatePair()
    {
        MemoryChannel a = new();
        MemoryChannel b = new();
        a.other = b;
        b.other = a;
        return (a, b);
    }

    /// <summary>Closes the sending side of this end: the other end reads null.</summary>
    public void Complete() => other!.incoming.Writer.TryComplete();

    /// <inheritdoc />
    public async ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(string json, CancellationToken cancellationToken)
    {
        other!.incoming.Writer.TryWrite(json);
        return ValueTask.CompletedTask;
    }
}
