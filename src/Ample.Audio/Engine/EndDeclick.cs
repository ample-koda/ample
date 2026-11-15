namespace Ample.Audio.Engine;

/// <summary>
/// A voice that is still sounding when its block ends is cut at the last sample of the block: a step in the wave, heard as a click when the next block of the same instrument goes on at once
/// (a cello chord that rings over the bar where the next eight bars begin). This fades the end of the buffer out softly instead, with integer arithmetic only.
/// </summary>
internal static class EndDeclick
{
    /// <summary>How long the end fades out.</summary>
    internal const int Milliseconds = 40;

    /// <summary>
    /// Fades out the last <see cref="Milliseconds"/> of the buffer, but only when something is still sounding at its end: a buffer that ends in silence stays exactly as it was. The gain falls from one to
    /// zero on a smooth step (3x² - 2x³), so the last sample is zero.
    /// </summary>
    internal static void Apply(long[]? buffer, int sampleRate)
    {
        if (buffer is null || buffer.Length < 2)
        {
            return;
        }

        int fade = Math.Min(buffer.Length, sampleRate * Milliseconds / 1000);
        if (fade < 2 || !Sounding(buffer))
        {
            return;
        }

        for (int k = 0; k < fade; k++)
        {
            long x = (long)(fade - 1 - k) * 32768 / (fade - 1);   // 1.0 at the start of the fade, 0 at the last sample (fifteen bits behind the point)
            long gain = ((3 * x * x) - (2 * x * x * x / 32768)) / 32768;
            int at = buffer.Length - fade + k;
            buffer[at] = buffer[at] * gain / 32768;
        }
    }

    private static bool Sounding(long[] buffer)
    {
        for (int i = Math.Max(0, buffer.Length - 8); i < buffer.Length; i++)
        {
            if (buffer[i] != 0)
            {
                return true;
            }
        }

        return false;
    }
}
