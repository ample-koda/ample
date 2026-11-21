using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Tests.Text;

/// <summary>Examples of the text front end (milestone M3), each of them checked against the C program in <see cref="FliteTextParityTests"/> as well.</summary>
public sealed class FliteTextTests
{
    private static string Words(string text, params string[] knownWords)
    {
        RecordedLexicon lexicon = new(knownWords.Select(w => $"#LEX\t{w}\t1"));
        Utterance u = new UsEnglishFrontEnd(lexicon).Analyse(ByteText.FromText(text));
        List<string> words = [];
        for (Item? i = u.GetRelation("Word").Head; i is not null; i = Hrg.Next(i))
        {
            words.Add(Hrg.FeatString(i, "name") + (Hrg.FeatPresent(i, "break") ? "!" : string.Empty));
        }

        return string.Join(' ', words);
    }

    [Theory]
    [InlineData("$1,234.56", "one thousand two hundred thirty four dollars fifty six cents")]
    [InlineData("Dr. Smith", "doctor smith")]
    [InlineData("3:30pm", "three thirty p m")]
    [InlineData("234-3434", "two three four! three four three four")]
    [InlineData("Henry VIII", "henry the eighth")]
    [InlineData("22-23", "twenty two to twenty three")]
    [InlineData("1/2", "a half")]
    [InlineData("Portland, OR", "portland oregon")]
    [InlineData("123-456-789", "one two three! four five six! seven eight nine!")]
    [InlineData("A B", "_a b")]
    [InlineData("the 5th", "the fifth")]
    [InlineData("100%", "one hundred per cent")]
    public void The_words_of_a_text_are_the_ones_the_C_program_gives(string text, string expected) => Assert.Equal(expected, Words(text));

    [Fact]
    public void A_word_that_is_in_the_lexicon_stays_a_word_and_one_that_is_not_and_cannot_be_pronounced_is_spelled()
    {
        Assert.Equal("xyzzy", Words("xyzzy", "xyzzy"));
        Assert.Equal("q w r t", Words("qwrt"));
    }

    [Fact]
    public void The_token_stream_cuts_white_space_and_punctuation_as_Flite_does()
    {
        TokenStream ts = new("  (Hello), world!!\t");
        TokenRead a = ts.Get();
        Assert.Equal(("Hello", "  ", "(", "),"), (a.Name, a.Whitespace, a.Prepunctuation, a.Postpunctuation));
        TokenRead b = ts.Get();
        Assert.Equal(("world", " ", string.Empty, "!!"), (b.Name, b.Whitespace, b.Prepunctuation, b.Postpunctuation));
        Assert.False(ts.Eof);
        Assert.Equal(string.Empty, ts.Get().Name);   // only the tab is left
        Assert.True(ts.Eof);
    }

    [Fact]
    public void A_token_keeps_its_first_character_even_when_that_is_punctuation()
    {
        TokenStream ts = new("...");
        TokenRead t = ts.Get();
        Assert.Equal((".", ".."), (t.Name, t.Postpunctuation));
    }

    [Fact]
    public void The_text_is_a_byte_string_that_stops_at_a_NUL_byte()
    {
        Assert.Equal("cafÃ©", ByteText.FromText("café"));
        Assert.Equal("ab", ByteText.FromUtf8([(byte)'a', (byte)'b', 0, (byte)'c']));
        Assert.Equal("café", ByteText.ToText(ByteText.FromText("café")));
    }

    [Fact]
    public void A_digit_outside_the_tables_of_the_C_code_is_an_error_here()
    {
        Assert.Throws<FliteException>(() => UsExpand.Number("1,2"));
    }
}
