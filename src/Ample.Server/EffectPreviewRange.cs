using Ample.Audio.Engine;

namespace Ample.Server;

/// <summary>Cuts a WAV (mono or stereo, 16 bit) to a range in milliseconds: the audio that a preview of an effect works on.</summary>
internal static class EffectPreviewRange
{
    public static byte[] Cut(byte[] wav, int fromMs, int toMs)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length >= 44 && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 2)
        {
            (StereoBuffer channels, int stereoRate) = StereoWav.Decode(wav);
            (int first, int last) = Bounds(channels.Left.Length, stereoRate, fromMs, toMs);
            return StereoWav.Encode(new StereoBuffer(channels.Left[first..last], channels.Right[first..last]), stereoRate);
        }

        (short[] samples, int rate) = Wav.Decode(wav);
        (int from, int to) = Bounds(samples.Length, rate, fromMs, toMs);
        return Wav.Encode(samples.AsSpan(from, to - from), rate);
    }

    private static (int First, int Last) Bounds(int length, int rate, int fromMs, int toMs)
    {
        int first = Math.Min(length, checked((int)((long)Math.Max(0, fromMs) * rate / 1000)));
        int last = Math.Min(length, checked((int)((long)Math.Max(fromMs, toMs) * rate / 1000)));
        return (first, Math.Max(first, last));
    }
}
