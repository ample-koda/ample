namespace Ample.Speech.Tests;

public sealed class PronunciationDictionaryTests
{
    private static readonly HashSet<string> Supported = ["IY", "IH", "EH", "AE", "AA", "AO", "UH", "UW", "AH", "AX", "ER", "EY", "AY", "OY", "OW", "AW", "P", "T", "K", "B", "D", "G", "S", "Z", "SH", "ZH", "F", "V", "TH", "DH", "HH", "M", "N", "NG", "L", "R", "W", "Y", "CH", "JH"];

    private static byte[] Shipped()
    {
        using Stream stream = typeof(HumanicoPronunciationLexicon).Assembly.GetManifestResourceStream("Ample.Speech.Data.cmudict.dict")!;
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void An_inline_comment_is_not_a_phoneme()
    {
        Dictionary<string, SpeechPhone[]> entries = HumanicoCmuDictionary.ParseLines(["hiv EY1 CH AY1 V IY1 # abbrev", "aalen AE1 L AH0 N # place, german"]);

        Assert.Equal(["EY", "CH", "AY", "V", "IY"], entries["hiv"].Select(phone => phone.Symbol));
        Assert.Equal(["AE", "L", "AH", "N"], entries["aalen"].Select(phone => phone.Symbol));
        Assert.Equal(1, entries["hiv"][0].Stress);
    }

    [Fact]
    public void Plain_lines_variants_and_comment_lines_are_read_as_before()
    {
        Dictionary<string, SpeechPhone[]> entries = HumanicoCmuDictionary.ParseLines([";;; a comment line", "", "read R EH1 D", "read(2) R IY1 D", "# only a comment", "lone"]);

        Assert.Equal(["read"], entries.Keys);   // a comment-only line and a word without phonemes add nothing
        Assert.Equal(["R", "EH", "D"], entries["read"].Select(phone => phone.Symbol));   // the first variant wins
    }

    [Fact]
    public void The_shipped_dictionary_has_only_phonemes_the_voice_can_speak()
    {
        Dictionary<string, SpeechPhone[]> entries = HumanicoCmuDictionary.Parse(Shipped(), HumanicoCmuDictionary.PinnedSha256);

        string[] unknown = [.. entries.Values.SelectMany(phones => phones).Select(phone => phone.Symbol).Distinct().Where(symbol => !Supported.Contains(symbol)).Order(StringComparer.Ordinal)];
        Assert.Empty(unknown);
        Assert.True(entries.Count > 100_000);
    }

    [Fact]
    public void A_word_with_an_inline_comment_speaks_through_the_default_engine()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("hiv", Seed: 1));

        Assert.Contains(result.Samples, sample => sample != 0);
    }

    [Fact]
    public void A_changed_or_missing_dictionary_is_refused_and_the_message_shows_no_path()
    {
        byte[] changed = Shipped();
        changed[changed.Length / 2] ^= 1;

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => HumanicoCmuDictionary.Parse(changed, HumanicoCmuDictionary.PinnedSha256));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, error.Message);
        Assert.DoesNotContain(AppContext.BaseDirectory, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => HumanicoCmuDictionary.Parse([], HumanicoCmuDictionary.PinnedSha256));
    }

    [Fact]
    public void The_pinned_hash_is_the_hash_of_the_shipped_bytes_and_of_the_manifest()
    {
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Shipped())), HumanicoCmuDictionary.PinnedSha256);
        Assert.Equal(HumanicoReferenceManifest.PinnedFixture.DictionarySha256, HumanicoCmuDictionary.PinnedSha256);
        Assert.Contains(typeof(HumanicoCmuDictionary).Assembly.GetManifestResourceNames(), name => name == "Ample.Speech.Data.cmudict.dict");
    }
}
