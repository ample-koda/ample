using Ample.Audio.Engine;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

/// <summary>The size of a render is checked once: the product of the bars and the samples of a bar always fits afterwards (security finding 47).</summary>
public sealed class RenderLimitsTests
{
    [Theory]
    [InlineData(4_233_600, 1024)]   // tempo 20, meter 16/2: the product wrapped mod 2^32 to a small number and the master was silently too short
    [InlineData(4_233_600, 700)]    // the product is above an int: it was an overflow without a code
    [InlineData(4_233_600, 64)]     // just above the limit
    [InlineData(int.MaxValue, 2)]
    [InlineData(-1, 4)]
    [InlineData(100, -1)]
    public void A_render_that_is_too_long_or_has_no_size_is_refused_with_its_own_code(int samplesPerBar, int bars)
    {
        AmpleException refused = Assert.Throws<AmpleException>(() => RenderLimits.Check(samplesPerBar, bars));
        Assert.Equal("AMP5531", refused.DiagnosticCode);
    }

    [Theory]
    [InlineData(4_233_600, 63)]     // just below the limit
    [InlineData(88_200, 1024)]      // a normal song: 120 bpm in 4/4 at 44.1 kHz is 88,200 samples a bar
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void A_render_up_to_the_limit_is_allowed(int samplesPerBar, int bars)
    {
        RenderLimits.Check(samplesPerBar, bars);
        Assert.True((long)samplesPerBar * bars <= RenderLimits.MaxRenderSamples);
    }
}
