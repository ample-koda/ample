using System.Buffers.Binary;

namespace Ample.Plugins.Analysis;

/// <summary>Validated PCM/float WAV metadata used before invoking an audio-analysis plugin.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="Frames">Interleaved sample frames.</param>
/// <param name="BitsPerSample">Bits per channel sample.</param>
/// <param name="Format">1 for PCM integer or 3 for IEEE float.</param>
public sealed record AudioInputInfo(int SampleRate, int Channels, long Frames, int BitsPerSample, int Format);

/// <summary>Performs a bounded structural WAV check without decoding or allocating the audio samples.</summary>
public static class AudioInput
{
    /// <summary>Validates an untrusted WAV before it is passed to a local analysis plugin.</summary>
    public static AudioInputInfo ValidateWav(string path, long maximumBytes, int maximumSeconds)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length is < 44 || file.Length > maximumBytes) throw new InvalidDataException("The audio file is missing or exceeds its size limit.");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)) throw new InvalidDataException("Only RIFF/WAVE audio is supported.");
        long riffEnd = checked(8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
        if (riffEnd != stream.Length) throw new InvalidDataException("The WAV container length is invalid.");

        int format = 0, channels = 0, sampleRate = 0, blockAlign = 0, bits = 0;
        long dataBytes = -1, position = 12;
        int chunks = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        while (position < riffEnd)
        {
            if (++chunks > 1024 || riffEnd - position < 8) throw new InvalidDataException("The WAV contains too many or truncated chunks.");
            stream.Position = position;
            stream.ReadExactly(chunk);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long body = checked(position + 8), end = checked(body + rawSize);
            if (end > riffEnd || (rawSize & 1) != 0 && end == riffEnd) throw new InvalidDataException("A WAV chunk exceeds the container bounds.");
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (format != 0 || rawSize < 16 || rawSize > 4096) throw new InvalidDataException("The WAV format chunk is invalid.");
                stream.ReadExactly(fmt);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                uint rawSampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                uint rawByteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
                if (rawSampleRate > int.MaxValue || rawByteRate > int.MaxValue) throw new InvalidDataException("The WAV sample or byte rate is invalid.");
                sampleRate = (int)rawSampleRate;
                int byteRate = (int)rawByteRate;
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format is not (1 or 3) || channels is < 1 or > 8 || sampleRate is < 8000 or > 192000 || bits is not (16 or 24 or 32) || format == 3 && bits != 32 || blockAlign != channels * bits / 8 || byteRate != sampleRate * blockAlign)
                    throw new InvalidDataException("The WAV format is unsupported or inconsistent.");
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (dataBytes >= 0) throw new InvalidDataException("The WAV has duplicate audio data chunks.");
                dataBytes = rawSize;
            }
            position = checked(end + (rawSize & 1));
        }

        if (format == 0 || dataBytes <= 0 || blockAlign <= 0 || dataBytes % blockAlign != 0) throw new InvalidDataException("The WAV is missing a valid format or audio data chunk.");
        long frames = dataBytes / blockAlign;
        if (frames > (long)sampleRate * maximumSeconds) throw new InvalidDataException("The audio exceeds the supported duration limit.");
        return new AudioInputInfo(sampleRate, channels, frames, bits, format);
    }
}
