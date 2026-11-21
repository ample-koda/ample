using System.Security.Cryptography;

namespace Ample.Speech;

/// <summary>Deterministic CMU-style pronunciation lookup with an auditable fixture override.</summary>
public static class HumanicoPronunciationLexicon
{
    public const string SourceIdentity = "cmudict/2026-11-18-sha256-81917843C7F44CE2B094AC63873C2C7A4CF802040792C455BA3CA406891C3D22";

    private static readonly Dictionary<string, SpeechPhone[]> Entries = new(StringComparer.Ordinal)
    {
        ["ample"] = [new("AE", 1), new("M", -1), new("P", -1), new("AH", 0), new("L", -1)],
        ["world"] = [new("W", -1), new("ER", 1), new("L", -1), new("D", -1)]
    };

    public static bool TryLookup(string word, out IReadOnlyList<SpeechPhone> phones)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (Entries.TryGetValue(word.ToLowerInvariant(), out SpeechPhone[]? entry))
        {
            phones = entry;
            return true;
        }

        if (HumanicoCmuDictionary.TryLookup(word, out phones)) return true;

        phones = [];
        return false;
    }
}

internal static class HumanicoCmuDictionary
{
    private static readonly Lazy<Dictionary<string, SpeechPhone[]>> Data = new(Load);

    public static bool TryLookup(string word, out IReadOnlyList<SpeechPhone> phones)
    {
        if (Data.Value.TryGetValue(word.ToLowerInvariant(), out SpeechPhone[]? entry))
        {
            phones = entry;
            return true;
        }

        phones = [];
        return false;
    }

    private const string ResourceName = "Ample.Speech.Data.cmudict.dict";

    /// <summary>The SHA-256 (hex, upper case) that <see cref="HumanicoPronunciationLexicon.SourceIdentity"/> pins.</summary>
    internal static string PinnedSha256 => HumanicoPronunciationLexicon.SourceIdentity[(HumanicoPronunciationLexicon.SourceIdentity.LastIndexOf('-') + 1)..];

    /// <summary>
    /// Reads the dictionary that ships inside the assembly and refuses it unless its bytes are the pinned ones: a missing or
    /// different file must never give another pronunciation under the same renderer identity (the toolprint names the hash).
    /// </summary>
    private static Dictionary<string, SpeechPhone[]> Load()
    {
        using Stream stream = typeof(HumanicoCmuDictionary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Humanico pronunciation dictionary is missing from the Ample.Speech assembly.");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Parse(bytes.ToArray(), PinnedSha256);
    }

    /// <summary>Checks the bytes against the pinned hash, then parses them.</summary>
    internal static Dictionary<string, SpeechPhone[]> Parse(byte[] data, string expectedSha256)
    {
        string actual = Convert.ToHexString(SHA256.HashData(data));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Humanico pronunciation dictionary does not match its pinned identity, so speech is refused rather than spoken differently.");
        return ParseLines(System.Text.Encoding.UTF8.GetString(data).Split('\n'));
    }

    /// <summary>One entry per line: <c>word PH1 PH2 # optional comment</c>; lines that start with a semicolon are comments; the first variant of a word wins.</summary>
    internal static Dictionary<string, SpeechPhone[]> ParseLines(IEnumerable<string> lines)
    {
        Dictionary<string, SpeechPhone[]> result = new(StringComparer.Ordinal);
        foreach (string text in lines)
        {
            string line = text.TrimEnd((char)13);
            if (line.Length == 0 || line[0] == ';') continue;
            int comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string word = parts[0];
            int variant = word.LastIndexOf('(');
            if (variant > 0) word = word[..variant];
            word = word.ToLowerInvariant();
            if (result.ContainsKey(word)) continue;
            List<SpeechPhone> phones = [];
            foreach (string raw in parts[1..])
            {
                int stress = raw.Length > 1 && char.IsDigit(raw[^1]) ? raw[^1] - '0' : -1;
                string symbol = stress >= 0 ? raw[..^1] : raw;
                phones.Add(new(symbol, HumanicoFrontEnd.IsVowel(symbol) ? stress : -1));
            }

            result[word] = [.. phones];
        }

        return result;
    }
}
