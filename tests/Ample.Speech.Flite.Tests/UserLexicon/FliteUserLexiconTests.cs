using Ample.Speech.Flite.Lexicon;

namespace Ample.Speech.Flite.Tests.UserLexicon;

/// <summary>Reading the <c>lexicon</c> and <c>spell</c> parameters: what is accepted, how CMU phonemes become the phones of the voice, and what the message says when it is wrong.</summary>
public class FliteUserLexiconTests
{
    [Fact]
    public void Entries_are_read_with_their_phones_in_the_notation_of_the_voice()
    {
        FliteUserLexicon lexicon = FliteUserLexicon.Parse("Tomato = T AH0 M EY1 T OW0; ample = AE1 M P AX L\n# a comment\nfoo = F UW2", null);

        Assert.Equal(["t", "ax0", "m", "ey1", "t", "ow0"], lexicon.Entries["tomato"]);
        Assert.Equal(["ae1", "m", "p", "ax0", "l"], lexicon.Entries["ample"]);
        Assert.Equal(["f", "uw1"], lexicon.Entries["foo"]);   // the voice knows two stresses: 2 is read as 1
        Assert.False(lexicon.IsEmpty);
    }

    [Fact]
    public void Nothing_written_is_the_empty_lexicon()
    {
        Assert.Same(FliteUserLexicon.Empty, FliteUserLexicon.Parse(null, null));
        Assert.Same(FliteUserLexicon.Empty, FliteUserLexicon.Parse("  ;  \n", " , "));
        Assert.True(FliteUserLexicon.Empty.IsEmpty);
    }

    [Fact]
    public void Spell_takes_words_or_all_and_the_canonical_text_is_equal_for_equal_lexicons()
    {
        FliteUserLexicon a = FliteUserLexicon.Parse("b = B IY1; a = EY1", "cpu, USB");
        FliteUserLexicon b = FliteUserLexicon.Parse("a = EY1\nb = B IY1", "usb cpu");

        Assert.Equal(["cpu", "usb"], a.SpelledWords.Order(StringComparer.Ordinal));
        Assert.Equal(a.Canonical, b.Canonical);
        Assert.NotEqual(a.Canonical, FliteUserLexicon.Parse("a = EY1", null).Canonical);
        Assert.True(FliteUserLexicon.Parse(null, "all").SpellAll);
    }

    [Theory]
    [InlineData("tomato T AH0 M EY1 T OW0", "word = PHONEMES")]
    [InlineData("= T AH0", "word = PHONEMES")]
    [InlineData("to mato = T AH0", "not a word")]
    [InlineData("café = K AE1 F EY1", "not a word")]
    [InlineData("tomato = ", "no phonemes")]
    [InlineData("tomato = T AH0 XX1", "Unknown phoneme 'XX1'")]
    [InlineData("tomato = T AH M EY1", "needs a stress digit")]
    [InlineData("tomato = T0 AH0", "consonant")]
    [InlineData("tomato = T AH3", "0, 1 or 2")]
    [InlineData("tomato = T AX1", "unstressed")]
    [InlineData("a = EY1; a = EY1", "two entries")]
    public void A_wrong_entry_says_what_is_wrong(string lexicon, string message)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => FliteUserLexicon.Parse(lexicon, null));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_word_cannot_be_in_the_lexicon_and_spelled_and_the_sizes_are_limited()
    {
        Assert.Contains("choose one", Assert.Throws<ArgumentException>(() => FliteUserLexicon.Parse("cat = K AE1 T", "cat")).Message, StringComparison.Ordinal);
        string many = string.Join(';', Enumerable.Range(0, FliteUserLexicon.MaxEntries + 1).Select(i => $"w{(char)('a' + (i / 26 % 26))}{(char)('a' + (i % 26))}{(char)('a' + (i / 676))} = AE1"));
        Assert.Contains("entries", Assert.Throws<ArgumentException>(() => FliteUserLexicon.Parse(many, null)).Message, StringComparison.Ordinal);
        string tooLong = "w = " + string.Join(' ', Enumerable.Repeat("AE1", FliteUserLexicon.MaxPhones + 1));
        Assert.Contains("at most", Assert.Throws<ArgumentException>(() => FliteUserLexicon.Parse(tooLong, null)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => FliteUserLexicon.Parse(null, new string('a', FliteUserLexicon.MaxWordLength + 1)));
    }
}
