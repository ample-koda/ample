namespace Ample.Speech;

/// <summary>A Humanico front-end phone with CMU-style stress (0, 1 or 2 for vowels; -1 for consonants).</summary>
public sealed record SpeechPhone(string Symbol, int Stress);

/// <summary>Deterministic first-stage pronunciation front end. The bundled table is intentionally small until the pinned CMUdict asset is packaged.</summary>
public static class HumanicoFrontEnd
{
    private static readonly HashSet<string> SupportedPhones = ["IY", "IH", "EH", "AE", "AA", "AO", "UH", "UW", "AH", "AX", "ER", "EY", "AY", "OY", "OW", "AW", "P", "T", "K", "B", "D", "G", "S", "Z", "SH", "ZH", "F", "V", "TH", "DH", "HH", "M", "N", "NG", "L", "R", "W", "Y", "CH", "JH"];
    private static readonly Dictionary<char, string> LetterFallback = new()
    {
        ['a'] = "AH",
        ['b'] = "B",
        ['c'] = "K",
        ['d'] = "D",
        ['e'] = "EH",
        ['f'] = "F",
        ['g'] = "G",
        ['h'] = "HH",
        ['i'] = "IH",
        ['j'] = "JH",
        ['k'] = "K",
        ['l'] = "L",
        ['m'] = "M",
        ['n'] = "N",
        ['o'] = "AA",
        ['p'] = "P",
        ['q'] = "K",
        ['r'] = "R",
        ['s'] = "S",
        ['t'] = "T",
        ['u'] = "AH",
        ['v'] = "V",
        ['w'] = "W",
        ['x'] = "K",
        ['y'] = "Y",
        ['z'] = "Z"
    };

    public static IReadOnlyList<SpeechPhone> WordToPhones(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (HumanicoPronunciationLexicon.TryLookup(word, out IReadOnlyList<SpeechPhone> known)) return known;
        List<SpeechPhone> phones = [];
        foreach (char character in word.ToLowerInvariant())
        {
            if (LetterFallback.TryGetValue(character, out string? symbol))
                phones.Add(new(symbol, IsVowel(symbol) ? 0 : -1));
        }

        return phones;
    }

    public static IReadOnlyList<SpeechPhone> ParsePhones(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        List<SpeechPhone> phones = [];
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string symbol = raw.ToUpperInvariant();
            int stress = symbol.Length > 1 && char.IsDigit(symbol[^1]) ? symbol[^1] - '0' : -1;
            if (stress is < -1 or > 2) throw new ArgumentException($"Unsupported phoneme stress in '{raw}'.", nameof(text));
            if (stress >= 0) symbol = symbol[..^1];
            if (!SupportedPhones.Contains(symbol)) throw new ArgumentException($"Unsupported phoneme '{raw}'.", nameof(text));
            if (stress >= 0 && !IsVowel(symbol)) throw new ArgumentException($"Stress is only valid on vowels: '{raw}'.", nameof(text));
            phones.Add(new(symbol, IsVowel(symbol) ? stress : -1));
        }
        return phones.Count == 0 ? throw new ArgumentException("At least one phoneme is required.", nameof(text)) : phones;
    }

    public static bool IsVowel(string symbol) => symbol is "IY" or "IH" or "EH" or "AE" or "AA" or "AO" or "UH" or "UW" or "AH" or "AX" or "ER" or "EY" or "AY" or "OY" or "OW" or "AW";
}
