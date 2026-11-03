using System.Buffers;
using System.IO.Pipes;
using System.Text.Json;

namespace Ample.Storage.Projects;

/// <summary>Connects to the current user's hub and exchanges one bounded JSON-lines message.</summary>
public static class HubPipeClient
{
    /// <summary>Sends one request, returning null only when no hub is listening.</summary>
    public static async Task<HubPipeResponse?> SendAsync(string pipeName, HubPipeRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        if (connectTimeout <= TimeSpan.Zero || connectTimeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        if (request.Version != HubPipeProtocol.Version) throw new ArgumentOutOfRangeException(nameof(request), "The request protocol version is unsupported.");
        if (request.RequestId == Guid.Empty) throw new ArgumentException("A request id is required.", nameof(request));

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, HubPipeProtocol.JsonOptions);
        if (payload.Length > HubPipeProtocol.MaxMessageBytes) throw new InvalidDataException("The hub request exceeds the protocol size limit.");
        byte[] line = [.. payload, (byte)'\n'];
        int connectTimeoutMilliseconds = Math.Max(1, (int)Math.Ceiling(connectTimeout.TotalMilliseconds));
        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HubPipeProtocol.RequestTimeout);
        byte[] responseBytes;
        try
        {
            await pipe.WriteAsync(line, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            responseBytes = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The AMPLE hub did not complete its request within 10 seconds.");
        }

        HubPipeResponse response = JsonSerializer.Deserialize<HubPipeResponse>(responseBytes, HubPipeProtocol.JsonOptions) ?? throw new InvalidDataException("The hub returned an empty response.");
        if (response.Version != HubPipeProtocol.Version || response.RequestId != request.RequestId) throw new InvalidDataException("The hub response did not match the request.");
        return response;
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        byte[] next = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The hub closed the pipe before replying.");
            if (next[0] == (byte)'\n')
            {
                byte[] line = buffer.WrittenMemory.ToArray();
                if (line.Length > 0 && line[^1] == (byte)'\r') Array.Resize(ref line, line.Length - 1);
                return line;
            }

            if (buffer.WrittenCount == HubPipeProtocol.MaxMessageBytes) throw new InvalidDataException("The hub response exceeds the protocol size limit.");
            buffer.GetSpan(1)[0] = next[0];
            buffer.Advance(1);
        }
    }
}
