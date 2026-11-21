using System.Text;

namespace Ample.Speech;

/// <summary>
/// The phones of a <c>phonemes</c> parameter for the flite engine: the CMU-style phonemes of the other engines (<c>HH AH0 L OW1</c>) in the phoneset of the voice
/// (<c>hh ax l ow</c>: lower case, with its own symbol for the unstressed AH and ER). Flite speaks explicit phones as one syllable, so the stress digits only choose the
/// reduced vowels: AH0 is the schwa <c>ax</c>, ER0 is <c>axr</c>; every other digit is accepted and has no further effect.
/// </summary>
public static class FlitePhoneTable
{
    private static readonly Dictionary<string, string> Plain = new(StringComparer.Ordinal)
    {
        ["IY"] = "iy",
        ["IH"] = "ih",
        ["EH"] = "eh",
        ["AE"] = "ae",
        ["AA"] = "aa",
        ["AO"] = "ao",
        ["UH"] = "uh",
        ["UW"] = "uw",
        ["AH"] = "ah",
        ["AX"] = "ax",
        ["ER"] = "er",
        ["EY"] = "ey",
        ["AY"] = "ay",
        ["OY"] = "oy",
        ["OW"] = "ow",
        ["AW"] = "aw",
        ["P"] = "p",
        ["T"] = "t",
        ["K"] = "k",
        ["B"] = "b",
        ["D"] = "d",
        ["G"] = "g",
        ["S"] = "s",
        ["Z"] = "z",
        ["SH"] = "sh",
        ["ZH"] = "zh",
        ["F"] = "f",
        ["V"] = "v",
        ["TH"] = "th",
        ["DH"] = "dh",
        ["HH"] = "hh",
        ["M"] = "m",
        ["N"] = "n",
        ["NG"] = "ng",
        ["L"] = "l",
        ["R"] = "r",
        ["W"] = "w",
        ["Y"] = "y",
        ["CH"] = "ch",
        ["JH"] = "jh"
    };

    private static readonly HashSet<string> Vowels = new(StringComparer.Ordinal)
    {
        "IY", "IH", "EH", "AE", "AA", "AO", "UH", "UW", "AH", "AX", "ER", "EY", "AY", "OY", "OW", "AW"
    };

    /// <summary>Gets the accepted phonemes in the notation of the block, for a message.</summary>
    public static IReadOnlyList<string> Phonemes { get; } = [.. Plain.Keys];

    /// <summary>Translates a phoneme string to the phones of the voice, separated by a space.</summary>
    /// <param name="phonemes">Space-separated phonemes; a stress digit 0 to 2 is allowed on a vowel; case does not matter.</param>
    /// <returns>The phones of the voice, for example <c>hh ax l ow</c>.</returns>
    /// <exception cref="ArgumentException">There is no phoneme, or one is not a phoneme of the voice, or a stress digit stands on a consonant.</exception>
    public static string Translate(string phonemes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phonemes);
        StringBuilder phones = new();
        foreach (string raw in phonemes.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string symbol = raw.ToUpperInvariant();
            int stress = symbol.Length > 1 && char.IsDigit(symbol[^1]) ? symbol[^1] - '0' : -1;
            if (stress is < -1 or > 2) throw new ArgumentException($"Unsupported phoneme stress in '{raw}'.", nameof(phonemes));
            if (stress >= 0) symbol = symbol[..^1];
            if (!Plain.TryGetValue(symbol, out string? plain))
            {
                throw new ArgumentException($"Unsupported phoneme '{raw}' for the flite engine. The phonemes are: {string.Join(' ', Plain.Keys)}.", nameof(phonemes));
            }

            if (stress >= 0 && !Vowels.Contains(symbol)) throw new ArgumentException($"Stress is only valid on vowels: '{raw}'.", nameof(phonemes));
            string phone = (symbol, stress) switch
            {
                ("AH", 0) => "ax",
                ("ER", 0) => "axr",
                _ => plain
            };
            if (phones.Length > 0) phones.Append(' ');
            phones.Append(phone);
        }

        return phones.Length == 0 ? throw new ArgumentException("At least one phoneme is required.", nameof(phonemes)) : phones.ToString();
    }
}
