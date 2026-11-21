namespace Ample.Speech;

/// <summary>One spoken word and where it stands in the text: UTF-16 offsets, from inclusive to exclusive. Several words can share one span (a number is spoken digit by digit).</summary>
public sealed record SpeechWordSpan(string Word, int From, int To);

public static class SpeechText
{
    private static readonly string[] Digits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '-'];

    /// <summary>
    /// The words of <see cref="Words"/>, one for one and in the same order, with their place in the text (the dashes and the ellipsis count as the
    /// one character that they are replaced with, so offsets are those of the text as it was given). A word is a token between separators, without
    /// the apostrophes at its ends.
    /// </summary>
    public static IReadOnlyList<SpeechWordSpan> WordSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string flat = text.Replace('\u2014', '-').Replace('\u2013', '-').Replace('\u2026', '.');
        List<SpeechWordSpan> spans = [];
        int i = 0;
        while (i < flat.Length)
        {
            if (Array.IndexOf(Separators, flat[i]) >= 0) { i++; continue; }
            int start = i;
            while (i < flat.Length && Array.IndexOf(Separators, flat[i]) < 0) i++;
            int from = start, to = i;
            while (from < to && flat[from] == '\'') from++;
            while (to > from && flat[to - 1] == '\'') to--;
            if (to == from) continue;
            string value = flat[from..to];
            if (value.All(char.IsAsciiDigit)) spans.AddRange(value.Select(digit => new SpeechWordSpan(Digits[digit - '0'], from, to)));
            else spans.Add(new SpeechWordSpan(value, from, to));
        }

        return spans;
    }

    public static IReadOnlyList<string> Words(string text)
    {
        text = text.Replace('\u2014', '-').Replace('\u2013', '-').Replace('\u2026', '.');
        List<string> words = [];
        foreach (string token in text.Split([' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            string value = token.Trim('\'');
            if (value.Length == 0) continue;
            if (value.All(char.IsAsciiDigit))
            {
                words.AddRange(value.Select(digit => Digits[digit - '0']));
            }
            else if (value.Any(char.IsDigit))
            {
                // Non-ASCII numeral systems stay text until a locale-aware number policy is approved.
                words.Add(value);
            }
            else
            {
                words.Add(value);
            }
        }

        return words;
    }
}
