using System.Security.Cryptography;
using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Storage.Security;

/// <summary>
/// The only cryptography of AMPLE, all of it from the .NET platform (no hand-made primitives): AES-256-GCM for sealing,
/// PBKDF2-HMAC-SHA256 for turning a passphrase into a key, ECDSA P-256 for signatures (see <see cref="SigningKeys"/>), and the
/// operating system's random number generator. Decisions and limits are written down in <c>docs/security.md</c>.
/// </summary>
public static class Crypto
{
    /// <summary>The PBKDF2 iteration count for new keys.</summary>
    public const int DefaultIterations = 600_000;

    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int KeyBytes = 32;

    /// <summary>Makes random bytes.</summary>
    public static byte[] Random(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>Turns a passphrase into a 256-bit key.</summary>
    public static byte[] Derive(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    /// <summary>Seals data: nonce, tag and ciphertext in one array. The additional data is authenticated but not stored.</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> additional = default)
    {
        byte[] result = new byte[NonceBytes + TagBytes + plain.Length];
        Span<byte> nonce = result.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using AesGcm aes = new(key, TagBytes);
        aes.Encrypt(nonce, plain, result.AsSpan(NonceBytes + TagBytes), result.AsSpan(NonceBytes, TagBytes), additional);
        return result;
    }

    /// <summary>Opens sealed data.</summary>
    /// <exception cref="CryptographicException">The key is wrong or the data was changed.</exception>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> additional = default)
    {
        if (sealedData.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The sealed data is too short.");
        }

        byte[] plain = new byte[sealedData.Length - NonceBytes - TagBytes];
        using AesGcm aes = new(key, TagBytes);
        aes.Decrypt(sealedData[..NonceBytes], sealedData[(NonceBytes + TagBytes)..], sealedData.Slice(NonceBytes, TagBytes), plain, additional);
        return plain;
    }
}

/// <summary>A 256-bit data key with the ways AMPLE uses it: lines of the black box and files of a backup.</summary>
public sealed class DataCipher
{
    internal const string LinePrefix = "enc1:";
    private static readonly byte[] FileMagic = Encoding.ASCII.GetBytes("AMPLEENC1\n");
    private const int Frame = 1024 * 1024;
    private readonly byte[] key;

    /// <summary>Creates a cipher from a key.</summary>
    public DataCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        this.key = key.Length == Crypto.KeyBytes ? [.. key] : throw new ArgumentException("A data key is 32 bytes.", nameof(key));
    }

    /// <summary>The key, for the recovery kit.</summary>
    internal byte[] Key => [.. key];

    /// <summary>True when a line of the black box is sealed.</summary>
    public static bool IsSealedLine(string line) => line.StartsWith(LinePrefix, StringComparison.Ordinal);

    /// <summary>Seals one line.</summary>
    public string SealLine(string line) => LinePrefix + Convert.ToBase64String(Crypto.Seal(key, Encoding.UTF8.GetBytes(line)));

    /// <summary>Opens one line; a line that is not sealed is returned as it is.</summary>
    public string OpenLine(string line)
    {
        if (!IsSealedLine(line))
        {
            return line;
        }

        try
        {
            return Encoding.UTF8.GetString(Crypto.Open(key, Convert.FromBase64String(line[LinePrefix.Length..])));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new AmpleException("AMP5310", "A record of the black box cannot be opened with this key: it is damaged or it was sealed with another key.");
        }
    }

    /// <summary>True when a file starts with the marker of an encrypted AMPLE file.</summary>
    public static bool IsSealedFile(string path)
    {
        using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] head = new byte[FileMagic.Length];
        return s.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(FileMagic);
    }

    /// <summary>Writes an encrypted copy of a file: the marker, then frames of 1 MiB, each with its own nonce and tag.</summary>
    public void SealFile(string source, string destination)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(FileMagic);
        byte[] buffer = new byte[Frame];
        int read;
        while ((read = ReadFull(input, buffer)) > 0)
        {
            byte[] sealedFrame = Crypto.Seal(key, buffer.AsSpan(0, read));
            output.Write(BitConverter.GetBytes(read));
            output.Write(sealedFrame);
        }
    }

    /// <summary>Writes the plain copy of an encrypted file.</summary>
    /// <exception cref="AmpleException">AMP5310 when the key does not open it.</exception>
    public void OpenFile(string source, string destination)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] head = new byte[FileMagic.Length];
        if (ReadFull(input, head) != head.Length || !head.AsSpan().SequenceEqual(FileMagic))
        {
            throw new AmpleException("AMP5310", "This file is not an encrypted AMPLE file.");
        }

        byte[] size = new byte[4];
        while (ReadFull(input, size) == 4)
        {
            int plainLength = BitConverter.ToInt32(size);
            if (plainLength is < 0 or > Frame)
            {
                throw new AmpleException("AMP5310", "An encrypted file is damaged (a frame has an impossible size).");
            }

            byte[] frame = new byte[Crypto.NonceBytes + Crypto.TagBytes + plainLength];
            if (ReadFull(input, frame) != frame.Length)
            {
                throw new AmpleException("AMP5310", "An encrypted file is damaged (it ends inside a frame).");
            }

            try
            {
                output.Write(Crypto.Open(key, frame));
            }
            catch (CryptographicException)
            {
                throw new AmpleException("AMP5310", "An encrypted file cannot be opened with this key: it is damaged or sealed with another key.");
            }
        }
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
