using Ample.Audio.Engine;

namespace Ample.Audio.Tests;

/// <summary>
/// The effect levels used in whole decibels are rounded, not cut off (Studio bug hunt 2026-11-24, bug 23): a knob showed -3 dB, the song said -2.8 dB and the build
/// played -2 dB; anything between -1 and 1 played as 0 dB.
/// </summary>
public sealed class WholeDecibelLevelTests
{
    [Theory]
    [InlineData("-2.8 dB", -3)]
    [InlineData("-0.7 dB", -1)]
    [InlineData("0.5", 1)]
    [InlineData("-18.5 dB", -19)]
    [InlineData("-3 dB", -3)]
    public void A_level_with_a_decimal_part_is_rounded(string text, int expected)
    {
        Assert.Equal(expected, AudioEffects.ReadRoundedDb(text, 99));
    }

    [Fact]
    public void The_levels_of_the_effects_follow_the_one_table()
    {
        Assert.True(AudioEffects.IsWholeDb("eq", "low"));
        Assert.True(AudioEffects.IsWholeDb("compressor", "makeup"));
        Assert.True(AudioEffects.IsWholeDb("limiter", "ceiling"));
        Assert.False(AudioEffects.IsWholeDb("delay", "time"));
        Assert.False(AudioEffects.IsWholeDb("volume", "gain"));
    }
}
