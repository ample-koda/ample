using System.ComponentModel;
using System.Diagnostics;

namespace Ample.Server.Lite;

/// <summary>
/// Turns the WAV of a Lite build into MP3 or FLAC for the download. The input is the audio that AMPLE itself just made (never a file of the user), the program (ffmpeg) is started with
/// fixed arguments, reads the WAV from its input and writes the result to its output, so no path, no file name and no text of the user reaches it. It has a time limit and a size limit
/// and is killed when either is passed.
/// </summary>
public static class LiteEncoder
{
    /// <summary>The formats that can be asked for: <c>mp3</c> and <c>flac</c> (a WAV needs no encoding).</summary>
    public static IReadOnlySet<string> Formats { get; } = new HashSet<string>(StringComparer.Ordinal) { "mp3", "flac" };

    /// <summary>Gets the content type of a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The content type.</returns>
    public static string ContentType(string format) => format switch { "mp3" => "audio/mpeg", "flac" => "audio/flac", _ => "audio/wav" };

    /// <summary>The fixed arguments of ffmpeg for a format (a constant list: nothing of the user's is in it).</summary>
    /// <param name="format">The format.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> Arguments(string format) => format switch
    {
        "mp3" => ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "wav", "-i", "pipe:0", "-vn", "-map_metadata", "-1", "-codec:a", "libmp3lame", "-b:a", "192k", "-f", "mp3", "pipe:1"],
        "flac" => ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "wav", "-i", "pipe:0", "-vn", "-map_metadata", "-1", "-codec:a", "flac", "-compression_level", "5", "-f", "flac", "pipe:1"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Encodes a WAV.</summary>
    /// <param name="wav">The WAV bytes of a build.</param>
    /// <param name="format">mp3 or flac.</param>
    /// <param name="limits">The limits (time, size).</param>
    /// <param name="cancellationToken">Cancelled when the page goes away.</param>
    /// <returns>The encoded bytes.</returns>
    /// <exception cref="LiteRejectedException">The encoder is missing, too slow or its result too large.</exception>
    public static async Task<byte[]> EncodeAsync(byte[] wav, string format, LiteLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(limits);
        ProcessStartInfo info = new(Environment.GetEnvironmentVariable("AMPLE_FFMPEG") is { Length: > 0 } configured ? configured : "ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments(format)) info.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new LiteRejectedException("AMPL935", 503, "Downloads as MP3 or FLAC are not available on this server (no encoder installed); the WAV works.");
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(limits.EncodeSeconds));
        using MemoryStream result = new();
        try
        {
            Task copy = Task.Run(async () =>
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    result.Write(buffer, 0, read);
                    if (result.Length > limits.MaxEncodedBytes) throw new LiteRejectedException("AMPL936", 413, "The encoded file would be too large for Remix.");
                }
            }, limit.Token);
            Task drain = process.StandardError.ReadToEndAsync(limit.Token);
            await process.StandardInput.BaseStream.WriteAsync(wav, limit.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(copy, drain).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new LiteRejectedException("AMPL937", 504, $"Encoding took longer than {limits.EncodeSeconds} seconds and was stopped.");
        }
        catch
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0 || result.Length == 0) throw new LiteRejectedException("AMPL938", 500, "The encoder could not convert this audio.");
        return result.ToArray();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it ended by itself in the meantime
        }
    }
}
