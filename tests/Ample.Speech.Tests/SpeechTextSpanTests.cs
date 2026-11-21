namespace Ample.Speech.Tests;

public sealed class SpeechTextSpanTests
{
    [Theory]
    [InlineData("I am a robot.")]
    [InlineData("  Get down, get funky!  ")]
    [InlineData("Call 911 now; it's 'quoted' - really\u2014yes\u2026 ok")]
    [InlineData("a1b 22 x")]
    [InlineData("...,,,")]
    [InlineData("")]
    public void The_spans_are_the_words_one_for_one_and_each_span_is_its_place_in_the_text(string text)
    {
        IReadOnlyList<string> words = SpeechText.Words(text);
        IReadOnlyList<SpeechWordSpan> spans = SpeechText.WordSpans(text);

        Assert.Equal(words, spans.Select(span => span.Word));
        foreach (SpeechWordSpan span in spans)
        {
            Assert.InRange(span.From, 0, text.Length);
            Assert.InRange(span.To, span.From + 1, text.Length);
            string token = text[span.From..span.To];
            Assert.DoesNotContain(token[0], " \t\r\n,.!?;:-'");
            Assert.True(token.Equals(span.Word, StringComparison.Ordinal) || token.All(char.IsAsciiDigit), $"'{token}' is not '{span.Word}'");
        }
    }

    [Fact]
    public void A_number_is_spoken_digit_by_digit_and_every_digit_shares_the_span_of_the_number()
    {
        IReadOnlyList<SpeechWordSpan> spans = SpeechText.WordSpans("go 42 now");

        Assert.Equal(["go", "four", "two", "now"], spans.Select(span => span.Word));
        Assert.Equal((3, 5), (spans[1].From, spans[1].To));
        Assert.Equal((3, 5), (spans[2].From, spans[2].To));
    }

    [Fact]
    public void The_apostrophes_at_the_ends_of_a_token_are_not_part_of_its_span()
    {
        SpeechWordSpan span = Assert.Single(SpeechText.WordSpans("'hello'"));

        Assert.Equal("hello", span.Word);
        Assert.Equal((1, 6), (span.From, span.To));
    }
}
