namespace Ample.Audio.IO;

/// <summary>
/// Decodes an Ogg Vorbis stream (the samples of an SF3 SoundFont) to 16-bit frames of its first channel, with NVorbis. Vorbis is a floating-point codec; the decoder is
/// the same managed code on every machine, so the same bytes decode to the same frames. It lives here, outside the integer-only instrument sources.
/// </summary>
internal static class OggVorbis
{
    /// <summary>The frames of the first channel, rounded to 16 bits; null when the bytes are not a readable stream or longer than <paramref name="maxFrames"/>.</summary>
    public static short[]? Decode(byte[] bytes, int maxFrames)
    {
        try
        {
            using NVorbis.VorbisReader reader = new(new MemoryStream(bytes, writable: false), true);
            int channels = Math.Max(1, reader.Channels);
            List<short> frames = [];
            float[] buffer = new float[4096 * channels];
            int read;
            while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i += channels) frames.Add((short)Math.Clamp((int)MathF.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue));
                if (frames.Count > maxFrames) return null;
            }

            return [.. frames];
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException or EndOfStreamException)
        {
            return null;
        }
    }
}
