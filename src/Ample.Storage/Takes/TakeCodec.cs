using System.Buffers.Binary;
using System.IO.Compression;

namespace Ample.Storage.Takes;

/// <summary>
/// How a WAV take is kept on disk: a small container around a byte-plane shuffle of the 16 bit samples and Brotli. Lossless (the decoded bytes are the
/// same bytes, so the content address, the plan ids and every take id stay as they were); silence and quiet passages shrink to almost nothing.
/// Files that do not start with the container's magic are plain data and are read as they are.
/// </summary>
public static class TakeCodec
{
    private static readonly byte[] Magic = "AMPZ"u8.ToArray();
    private const int HeaderLength = 4 + 1 + 8;
    private const byte Version = 1;

    /// <summary>The smallest blob that is worth compressing.</summary>
    public const int MinimumLength = 64 * 1024;

    /// <summary>Determines whether the bytes are a WAV file (the only data compressed).</summary>
    public static bool IsWav(ReadOnlySpan<byte> start) => start.Length >= 12 && start[..4].SequenceEqual("RIFF"u8) && start.Slice(8, 4).SequenceEqual("WAVE"u8);

    /// <summary>Determines whether the bytes start the container.</summary>
    public static bool IsContainer(ReadOnlySpan<byte> start) => start.Length >= 4 && start[..4].SequenceEqual(Magic);

    /// <summary>Writes the container for <paramref name="plain"/>.</summary>
    public static void Encode(ReadOnlySpan<byte> plain, Stream output)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(header[5..], plain.Length);
        output.Write(header);
        // low bytes of all samples first, then the high bytes: the high bytes of audio are smooth and compress far better apart
        byte[] shuffled = new byte[plain.Length];
        int pairs = plain.Length / 2;
        for (int i = 0; i < pairs; i++)
        {
            shuffled[i] = plain[2 * i];
            shuffled[pairs + i] = plain[(2 * i) + 1];
        }

        if ((plain.Length & 1) == 1)
        {
            shuffled[^1] = plain[^1];
        }

        using BrotliStream brotli = new(output, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true);
        brotli.Write(shuffled);
    }

    /// <summary>Reads a container back into the original bytes.</summary>
    public static byte[] Decode(Stream container)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        container.ReadExactly(header);
        if (!IsContainer(header) || header[4] != Version)
        {
            throw new InvalidDataException("This is not a take container of a known version.");
        }

        long length = BinaryPrimitives.ReadInt64LittleEndian(header[5..]);
        if (length < 0 || length > int.MaxValue)
        {
            throw new InvalidDataException("The take container names an impossible length.");
        }

        byte[] shuffled = new byte[length];
        using (BrotliStream brotli = new(container, CompressionMode.Decompress, leaveOpen: true))
        {
            brotli.ReadExactly(shuffled);
        }

        byte[] plain = new byte[length];
        int pairs = (int)length / 2;
        for (int i = 0; i < pairs; i++)
        {
            plain[2 * i] = shuffled[i];
            plain[(2 * i) + 1] = shuffled[pairs + i];
        }

        if ((length & 1) == 1)
        {
            plain[^1] = shuffled[^1];
        }

        return plain;
    }
}
