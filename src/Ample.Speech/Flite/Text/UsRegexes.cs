using System.Text.RegularExpressions;

namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), lang/usenglish/make_us_regexes and src/regex/cst_regex_defs.h. Translated to C# and modified: see UsText.cs.

/// <summary>
/// The regular expressions of the US English text analysis. Flite compiles them with Henry Spencer's engine, anchored at both ends; here each is the same language written for .NET
/// (ASCII classes only, <c>.</c> matches every character including a line feed, <c>$</c> is the end of the string only).
/// The tests compare every expression with the compiled C program on generated strings.
/// </summary>
internal static class UsRegexes
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.ExplicitCapture;

    private static Regex Anchored(string pattern) => new($@"\A(?:{pattern})\z", Options);

    /// <summary><c>cst_rx_digits</c>.</summary>
    public static readonly Regex Digits = Anchored("[0-9]+");

    /// <summary><c>cst_rx_alpha</c>.</summary>
    public static readonly Regex Alpha = Anchored("[A-Za-z]+");

    /// <summary><c>cst_rx_double</c>.</summary>
    public static readonly Regex Double = Anchored(@"-?(?:[0-9]+\.[0-9]*|[0-9]+|\.[0-9]+)(?:[eE][-+]?[0-9]+)?");

    /// <summary><c>cst_rx_commaint</c>: 1,234 and 12,345,678 with an optional fraction.</summary>
    public static readonly Regex CommaInt = Anchored(@"[0-9][0-9]?[0-9]?,(?:[0-9]{3},)*[0-9]{3}(?:\.[0-9]+)?");

    /// <summary><c>ordinal_number</c>.</summary>
    public static readonly Regex OrdinalNumber = Anchored("[0-9][0-9,]*(?:th|TH|st|ST|nd|ND|rd|RD)");

    /// <summary><c>hasvowel</c>.</summary>
    public static readonly Regex HasVowel = Anchored(".*[aeiouAEIOU].*");

    /// <summary><c>usmoney</c>.</summary>
    public static readonly Regex UsMoney = Anchored(@"\$[0-9,]+(?:\.[0-9]+)?");

    /// <summary><c>illion</c>.</summary>
    public static readonly Regex Illion = Anchored(".*illion");

    /// <summary><c>romannums</c>.</summary>
    public static readonly Regex RomanNums = Anchored("(?:II?I?|IV|VI?I?I?|IX|X[VIX]*)");

    /// <summary><c>drst</c>.</summary>
    public static readonly Regex DrSt = Anchored("(?:[dD][Rr]|[Ss][tT])");

    /// <summary><c>numess</c>.</summary>
    public static readonly Regex NumEss = Anchored("[0-9]+s");

    /// <summary><c>leadingzerodigits</c>.</summary>
    public static readonly Regex LeadingZeroDigits = Anchored("0[0-9]+");

    /// <summary><c>sevenphonenumber</c>.</summary>
    public static readonly Regex SevenPhoneNumber = Anchored("[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]");

    /// <summary><c>fourdigits</c>.</summary>
    public static readonly Regex FourDigits = Anchored("[0-9][0-9][0-9][0-9]");

    /// <summary><c>threedigits</c>.</summary>
    public static readonly Regex ThreeDigits = Anchored("[0-9][0-9][0-9]");

    /// <summary><c>numbertime</c>.</summary>
    public static readonly Regex NumberTime = Anchored("[0-9]?[0-9]:[0-5][0-9]");

    /// <summary><c>numbertimexm</c>.</summary>
    public static readonly Regex NumberTimeXm = Anchored("[0-9]?[0-9][:.][0-5][0-9][ap]m");

    /// <summary><c>dottedabbrevs</c>.</summary>
    public static readonly Regex DottedAbbrevs = Anchored(@"(?:[A-Za-z]\.)+[A-Za-z]\.?");

    /// <summary><c>digitsslashdigits</c>.</summary>
    public static readonly Regex DigitsSlashDigits = Anchored("[0-9]+/[0-9]+");

    /// <summary><c>digits2dash</c>.</summary>
    public static readonly Regex Digits2Dash = Anchored("(?:[0-9]+-+)+[0-9]+");

    /// <summary><c>wandm</c>: weights and measures.</summary>
    public static readonly Regex WeightsAndMeasures = Anchored("[0-9,]*[0-9]+(?:lb|LB|lbs|LBS|ft|FT|kg|km|oz|hz|Hz|HZ|KHz|MHz|GHz|cm|mm|ml|KB|MB|GB|TB)");

    /// <summary><c>cst_regex_match</c>.</summary>
    /// <param name="regex">One of the expressions of this class.</param>
    /// <param name="text">The byte string.</param>
    /// <returns>Whether the whole string matches.</returns>
    public static bool Match(Regex regex, string text) => regex.IsMatch(text);
}
