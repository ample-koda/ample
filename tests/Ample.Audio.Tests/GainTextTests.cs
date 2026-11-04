using Ample.Audio.Engine;

namespace Ample.Audio.Tests;

/// <summary>A level in decibels is read the same with and without its unit, in quotes or not, and is cut to the range of the parameter.</summary>
public class GainTextTests
{
    [Theory]
    [InlineData("-12 dB", -12)]
    [InlineData("-12", -12)]
    [InlineData("\"-12 dB\"", -12)]
    [InlineData(" 6 dB ", 6)]
    [InlineData("+3 dB", 3)]
    [InlineData("3.5 dB", 3)]
    [InlineData("0", 0)]
    public void The_number_is_read_with_or_without_the_unit(string text, int expected)
    {
        Assert.Equal(expected, AudioEffects.ReadDb(text, 99, -60, 48));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("dB")]
    public void A_missing_or_unreadable_value_gives_the_fallback(string? text)
    {
        Assert.Equal(7, AudioEffects.ReadDb(text, 7, -60, 48));
    }

    [Theory]
    [InlineData("-90 dB", -24)]
    [InlineData("100 dB", 12)]
    public void The_value_is_cut_to_the_range(string text, int expected)
    {
        Assert.Equal(expected, AudioEffects.ReadDb(text, 0, -24, 12));
    }
}
