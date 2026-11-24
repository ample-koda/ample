using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>
/// The size of what one render makes. Every renderer allocates <c>BarCount * SamplesPerBar</c> samples; with a slow tempo, a long meter and many bars
/// the product does not fit an int (tempo 20, meter 16/2 and 1024 bars gave a master that was silently 100 times too short, and about 480 bars asked for
/// a 4 GB buffer). It is checked once, where the request is made, so that the product is always a valid size afterwards (security finding 47).
/// </summary>
public static class RenderLimits
{
    /// <summary>The most samples one render of a block may be long: 2^28, about 100 minutes at 44.1 kHz.</summary>
    public const long MaxRenderSamples = 1L << 28;

    /// <summary>Refuses a render whose length in samples is not between 0 and <see cref="MaxRenderSamples"/>.</summary>
    /// <param name="samplesPerBar">The samples of one bar.</param>
    /// <param name="bars">The number of bars that are rendered.</param>
    public static void Check(int samplesPerBar, int bars)
    {
        if (samplesPerBar < 0 || bars < 0 || (long)samplesPerBar * bars > MaxRenderSamples)
        {
            throw new AmpleException(
                "AMP5531",
                $"The song is too long to render: {bars} bars of {samplesPerBar} samples are more than {MaxRenderSamples} samples (about 100 minutes). " +
                "Use a faster tempo or fewer bars.");
        }
    }
}
