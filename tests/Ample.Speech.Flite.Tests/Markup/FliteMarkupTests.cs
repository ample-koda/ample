namespace Ample.Speech.Flite.Tests.Markup;

/// <summary>Reading the tags: what is accepted, the plain text and the places, and what the message says when a tag is wrong.</summary>
public class FliteMarkupTests
{
    [Fact]
    public void The_plain_text_has_no_tags_and_entities_are_decoded_and_places_map_back()
    {
        FliteMarkup m = FliteMarkup.Parse("a <emph>b &amp; c</emph> d &lt; e", 95);

        Assert.Equal("a b & c d < e", m.Plain);
        Assert.Equal("a <emph>".Length, m.SourceStart(2));   // b stands after the tag
        Assert.Equal("a <emph>b &amp; c".Length, m.SourceEnd(7));   // the end of c, the entity counted whole
    }

    [Fact]
    public void A_less_than_that_is_not_a_tag_stays_a_character()
    {
        FliteMarkup m = FliteMarkup.Parse("5 < 6 and 7 <8", 95);

        Assert.Equal("5 < 6 and 7 <8", m.Plain);
        Assert.False(m.HasMarkup);
        Assert.False(FliteMarkup.LooksLikeMarkup(m.Plain));
    }

    [Fact]
    public void Pitch_in_hertz_or_semitones_becomes_a_factor_of_the_pitch_of_the_block()
    {
        FliteMarkup hz = FliteMarkup.Parse("<pitch 190>x</pitch>", 95);
        FliteMarkup st = FliteMarkup.Parse("<pitch +12st>x</pitch>", 95);

        Assert.True(hz.TokenFeatures(0, out _, out float shiftHz, out _));
        Assert.True(st.TokenFeatures(0, out _, out float shiftSt, out _));
        Assert.Equal(2f, shiftHz, 4);
        Assert.Equal(2f, shiftSt, 4);
    }

    [Fact]
    public void Rate_nests_by_multiplying_and_emph_raises_and_lengthens()
    {
        FliteMarkup m = FliteMarkup.Parse("<rate 2>a <rate 2>b</rate> <emph>c</emph></rate> d", 95);

        Assert.True(m.TokenFeatures(0, out float outer, out _, out _));
        Assert.Equal(0.5f, outer, 4);
        Assert.True(m.TokenFeatures(2, out float inner, out _, out _));
        Assert.Equal(0.25f, inner, 4);
        Assert.True(m.TokenFeatures(4, out float stretch, out float shift, out _));
        Assert.Equal(0.5f * 1.15f, stretch, 4);
        Assert.Equal((float)Math.Pow(2, 3d / 12), shift, 4);
        Assert.False(m.TokenFeatures(m.Plain.Length - 1, out _, out _, out _));   // d is outside every tag
    }

    [Fact]
    public void A_break_keeps_its_place_and_length()
    {
        FliteMarkup m = FliteMarkup.Parse("one<break 300ms/> two <break 0.5s/>three", 95);

        Assert.Equal("one two three", m.Plain);
        Assert.Equal([new MarkupBreak(3, 300), new MarkupBreak(8, 500)], m.Breaks);
    }

    [Theory]
    [InlineData("<pitch>x</pitch>", "needs one value")]
    [InlineData("<pitch 20>x</pitch>", "50 to 480")]
    [InlineData("<pitch +30st>x</pitch>", "-24st to +24st")]
    [InlineData("<rate 9>x</rate>", "0.25 to 4")]
    [InlineData("<range 300>x</range>", "0 to 200")]
    [InlineData("<emph 2>x</emph>", "takes no value")]
    [InlineData("<pitch 100>x", "not closed")]
    [InlineData("x</pitch>", "no opening tag")]
    [InlineData("<pitch 100><rate 2>x</pitch></rate>", "does not match")]
    [InlineData("<break 300/>", "needs a unit")]
    [InlineData("<break 0ms/>", "longer than 0")]
    [InlineData("<break 9s/>", "at most 5000")]
    [InlineData("<break 300ms>x", "no closing tag")]
    [InlineData("<voice x>y</voice>", "Unknown tag")]
    [InlineData("<pitch 100", "not closed with '>'")]
    public void A_wrong_tag_says_what_is_wrong(string text, string message)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => FliteMarkup.Parse(text, 95));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_depth_and_the_number_of_breaks_are_limited()
    {
        string deep = string.Concat(Enumerable.Repeat("<emph>", FliteMarkup.MaxDepth + 1)) + "x";
        string many = string.Concat(Enumerable.Repeat("a<break 10ms/>", FliteMarkup.MaxBreaks + 1));

        Assert.Contains("open at the same time", Assert.Throws<ArgumentException>(() => FliteMarkup.Parse(deep, 95)).Message, StringComparison.Ordinal);
        Assert.Contains("breaks", Assert.Throws<ArgumentException>(() => FliteMarkup.Parse(many, 95)).Message, StringComparison.Ordinal);
    }
}
