using System.Buffers.Binary;
using System.Text;

namespace Ample.Audio.Engine;

/// <summary>Prototype stereo buffer for the S09 decision: two equal-length channels of 16-bit samples. Not wired into the renderers.</summary>
public sealed record StereoBuffer(short[] Left, short[] Right)
{
    /// <summary>Gets the number of frames.</summary>
    public int Length => Left.Length;

    /// <summary>Makes a stereo buffer from mono (both channels the same).</summary>
    public static StereoBuffer FromMono(short[] mono) => new(mono, (short[])mono.Clone());

    /// <summary>Pans a mono signal into a stereo buffer with the equal-power table.</summary>
    public static StereoBuffer Pan(short[] mono, int pan)
    {
        short[] l = new short[mono.Length];
        short[] r = new short[mono.Length];
        int gl = PanTable.LeftGain(pan);
        int gr = PanTable.RightGain(pan);
        for (int i = 0; i < mono.Length; i++)
        {
            l[i] = FixedAudio.Clip(PanTable.Apply(mono[i], gl));
            r[i] = FixedAudio.Clip(PanTable.Apply(mono[i], gr));
        }

        return new StereoBuffer(l, r);
    }

    /// <summary>Folds to mono by the integer mean of both channels (rounding toward negative infinity).</summary>
    public short[] ToMono()
    {
        short[] mono = new short[Length];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)((Left[i] + Right[i]) >> 1);
        }

        return mono;
    }

    /// <summary>Adds another buffer into this one per channel, clipped to 16 bits.</summary>
    public StereoBuffer Mix(StereoBuffer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int n = Math.Max(Length, other.Length);
        short[] l = new short[n];
        short[] r = new short[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = FixedAudio.Clip((i < Length ? Left[i] : 0) + (i < other.Length ? other.Left[i] : 0));
            r[i] = FixedAudio.Clip((i < Length ? Right[i] : 0) + (i < other.Length ? other.Right[i] : 0));
        }

        return new StereoBuffer(l, r);
    }
}

/// <summary>16-bit stereo WAV encoding and decoding (prototype for S09; the mono <see cref="Wav"/> is untouched).</summary>
public static class StereoWav
{
    /// <summary>Encodes a stereo buffer as an interleaved 16-bit PCM WAV.</summary>
    public static byte[] Encode(StereoBuffer buffer, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int data = buffer.Length * 4;
        byte[] bytes = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + data);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data);
        for (int i = 0; i < buffer.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (i * 4)), buffer.Left[i]);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(46 + (i * 4)), buffer.Right[i]);
        }

        return bytes;
    }

    /// <summary>Decodes a canonical 44-byte-header stereo WAV written by <see cref="Encode"/>.</summary>
    public static (StereoBuffer Buffer, int SampleRate) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes[..4]) != "RIFF" || BinaryPrimitives.ReadInt16LittleEndian(bytes[22..]) != 2)
        {
            throw new InvalidDataException("Not a stereo WAV file.");
        }

        int rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        // the data chunk is not always at byte 44: encoders such as ffmpeg put a LIST chunk in front of it
        int dataAt = 36, dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        for (int pos = 12; pos + 8 <= bytes.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 4)..]);
            if (bytes[pos] == 'd' && bytes[pos + 1] == 'a' && bytes[pos + 2] == 't' && bytes[pos + 3] == 'a') { dataAt = pos; dataLength = Math.Min(size < 0 ? int.MaxValue : size, bytes.Length - pos - 8); break; }
            pos += 8 + size + (size & 1);
        }

        int start = dataAt + 8;
        int frames = Math.Max(0, Math.Min(dataLength, bytes.Length - start)) / 4;
        short[] l = new short[frames];
        short[] r = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            l[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(start + (i * 4))..]);
            r[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(start + 2 + (i * 4))..]);
        }

        return (new StereoBuffer(l, r), rate);
    }
}
