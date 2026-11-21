namespace Ample.Speech.Tests.Flite;

/// <summary>The translation of the phonemes of a block to the phones of the Flite voice.</summary>
public sealed class FlitePhoneTableTests
{
    [Theory]
    [InlineData("HH AH0 L OW1", "hh ax l ow")]
    [InlineData("hh ah1 l ow1", "hh ah l ow")]
    [InlineData("HH AH2 L OW0", "hh ah l ow")]
    [InlineData("K ER0 K ER1", "k axr k er")]
    [InlineData("AX", "ax")]
    [InlineData("  S   P   IY1   CH  ", "s p iy ch")]
    [InlineData("TH IH1 S", "th ih s")]
    public void Phonemes_become_the_phones_of_the_voice(string phonemes, string expected) =>
        Assert.Equal(expected, FlitePhoneTable.Translate(phonemes));

    [Fact]
    public void Every_phoneme_of_the_other_engine_is_known()
    {
        string[] cmu = ["IY", "IH", "EH", "AE", "AA", "AO", "UH", "UW", "AH", "AX", "ER", "EY", "AY", "OY", "OW", "AW", "P", "T", "K", "B", "D", "G", "S", "Z", "SH", "ZH", "F", "V",
            "TH", "DH", "HH", "M", "N", "NG", "L", "R", "W", "Y", "CH", "JH"];
        Assert.Equal(cmu.Order(), FlitePhoneTable.Phonemes.Order());
        Assert.Equal(cmu.Length, FlitePhoneTable.Translate(string.Join(' ', cmu)).Split(' ').Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX")]
    [InlineData("HH-AH0")]
    [InlineData("HH AH3")]
    [InlineData("HH1 AH0")]
    [InlineData("pau")]
    public void An_input_that_is_not_phonemes_of_the_voice_is_refused(string phonemes) =>
        Assert.ThrowsAny<ArgumentException>(() => FlitePhoneTable.Translate(phonemes));

    [Fact]
    public void The_refusal_names_the_phoneme_and_lists_the_valid_ones()
    {
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(() => FlitePhoneTable.Translate("HH QQ"));

        Assert.Contains("'QQ'", error.Message, StringComparison.Ordinal);
        Assert.Contains("HH", error.Message, StringComparison.Ordinal);
        Assert.Contains("ZH", error.Message, StringComparison.Ordinal);
    }
}
