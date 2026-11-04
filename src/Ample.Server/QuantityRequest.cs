using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Server;

/// <summary>
/// Recognises when the user asked for a number of samples or clips ("give me five samples", "laat me 6 varianten horen") and reads that number. It is deliberately careful: when a request holds
/// more than one quantity, or none that is clear, there is no limit (<see cref="Limit"/> returns null) and Codex is not held to a count.
/// </summary>
internal static class QuantityRequest
{
    private const string Words = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|een|twee|drie|vier|vijf|zes|zeven|acht|negen|tien|elf|twaalf";

    private const string Things = @"(?:samples?|clips?|clipjes|geluiden|sounds?|variants?|variations?|varianten|versies|versions?|voorstellen|options?|opties|examples?|voorbeelden)";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly Regex Asking = new(@"\b(?:give|geef|maak|make|show|toon|provide|want|wil|play|speel|let|laat|present|sample|hoor|hear|send|stuur|need|nodig)\b[^\r\n.!?]{0,80}\b(?:\d+|" + Words + @"|more|meer|extra|additional|another|nog)\s+(?:(?:more|extra|additional|meer|different|andere|verschillende)\s+)?" + Things + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Counted = new(@"\b(\d+|" + Words + @")\s+(?:(?:more|extra|additional|meer|different|andere|verschillende)\s+)?" + Things + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    /// <summary>Whether the text asks for (more) samples, clips or variants.</summary>
    public static bool IsRequest(string text) => !string.IsNullOrWhiteSpace(text) && Asking.IsMatch(text);

    /// <summary>The one quantity the text asks for, or null when there is none or more than one number (no guessing).</summary>
    public static int? Limit(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        MatchCollection matches = Counted.Matches(text);
        if (matches.Count != 1)
        {
            return null;
        }

        string value = matches[0].Groups[1].Value.ToLowerInvariant();
        int? number = value switch
        {
            "one" or "een" => 1,
            "two" or "twee" => 2,
            "three" or "drie" => 3,
            "four" or "vier" => 4,
            "five" or "vijf" => 5,
            "six" or "zes" => 6,
            "seven" or "zeven" => 7,
            "eight" or "acht" => 8,
            "nine" or "negen" => 9,
            "ten" or "tien" => 10,
            "eleven" or "elf" => 11,
            "twelve" or "twaalf" => 12,
            _ => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null,
        };
        return number is > 0 and <= 200 ? number : null;
    }
}
