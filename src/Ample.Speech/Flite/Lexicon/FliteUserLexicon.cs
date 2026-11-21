using Ample.Speech.Flite.Text;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), src/lexicon/cst_lexicon.c (cst_lex_make_entry, the addenda that flite -add_lex reads) and src/synth/cst_synth.c (default_lexical_insertion,
// which looks a word up in the run-time addenda first). Translated to C# and modified: the entries come from the text of a block instead of a file, the phones are written as CMU
// phonemes, and a word can be read letter by letter. See FLITE-NOTICE.md in the data folder.

/// <summary>
/// The pronunciations that a song gives to the Flite voice: words with their own phones (<c>lexicon "tomato = T AH0 M EY1 T OW0; aample = EY1 M P AH0 L"</c>) and words that are read
/// letter by letter (<c>spell "cpu usb"</c>, or <c>spell "all"</c> for the whole text). Immutable and thread-safe. A word is matched without regard to case, as the words of the text
/// are lower case before they are looked up.
/// </summary>
public sealed class FliteUserLexicon
{
    /// <summary>The most entries a block may give.</summary>
    public const int MaxEntries = 256;

    /// <summary>The most phones one entry may have.</summary>
    public const int MaxPhones = 40;

    /// <summary>The longest word, in letters.</summary>
    public const int MaxWordLength = 40;

    private static readonly HashSet<string> Consonants = new(StringComparer.Ordinal)
    {
        "p", "t", "k", "b", "d", "g", "s", "z", "sh", "zh", "f", "v", "th", "dh", "hh", "m", "n", "ng", "l", "r", "w", "y", "ch", "jh",
    };

    private static readonly HashSet<string> Vowels = new(StringComparer.Ordinal)
    {
        "aa", "ae", "ah", "ao", "aw", "ax", "ay", "eh", "er", "ey", "ih", "iy", "ow", "oy", "uh", "uw",
    };

    private readonly Dictionary<string, string[]> entries;
    private readonly HashSet<string> spelled;

    private FliteUserLexicon(Dictionary<string, string[]> entries, HashSet<string> spelled, bool spellAll, string canonical)
    {
        this.entries = entries;
        this.spelled = spelled;
        SpellAll = spellAll;
        Canonical = canonical;
    }

    /// <summary>Gets a lexicon without entries (nothing changes in the speech).</summary>
    public static FliteUserLexicon Empty { get; } = new([], [], false, string.Empty);

    /// <summary>Gets the phoneme symbols of the notation of a block, in the order of the message that names them.</summary>
    public static IReadOnlyList<string> Phonemes { get; } =
        [.. Consonants.Order(StringComparer.Ordinal).Select(c => c.ToUpperInvariant()), .. Vowels.Order(StringComparer.Ordinal).Select(v => v.ToUpperInvariant())];

    /// <summary>Gets a value indicating whether every word of the text is read letter by letter.</summary>
    public bool SpellAll { get; }

    /// <summary>Gets the words that have their own phones, lower case, with their phones in the notation of the lexicon of the voice (<c>ax0</c>, <c>ey1</c>, <c>t</c>).</summary>
    public IReadOnlyDictionary<string, string[]> Entries => entries;

    /// <summary>Gets the words that are read letter by letter.</summary>
    public IReadOnlyCollection<string> SpelledWords => spelled;

    /// <summary>Gets a text that is equal for equal lexicons: it is part of the key of the renders that are kept and of the identity of a take.</summary>
    public string Canonical { get; }

    /// <summary>Gets a value indicating whether there is nothing in this lexicon.</summary>
    public bool IsEmpty => entries.Count == 0 && spelled.Count == 0 && !SpellAll;

    /// <summary>Reads the <c>lexicon</c> and <c>spell</c> parameters of a block.</summary>
    /// <param name="lexicon">Entries <c>word = PHONEMES</c>, separated by <c>;</c> or a new line, or null.</param>
    /// <param name="spell">The words to read letter by letter, separated by spaces or commas, or <c>all</c>, or null.</param>
    /// <returns>The lexicon.</returns>
    /// <exception cref="ArgumentException">An entry or a word is not valid; the message names it and says what is allowed.</exception>
    public static FliteUserLexicon Parse(string? lexicon, string? spell)
    {
        Dictionary<string, string[]> entries = new(StringComparer.Ordinal);
        List<string> lines = [];
        foreach (string raw in (lexicon ?? string.Empty).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = raw.Trim();
            if (entry.Length == 0 || entry[0] == '#') continue;
            int equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) throw new ArgumentException($"The lexicon entry '{entry}' must be 'word = PHONEMES', for example 'tomato = T AH0 M EY1 T OW0'.", nameof(lexicon));
            string word = CleanWord(entry[..equals].Trim(), nameof(lexicon));
            string[] phones = TranslatePhonemes(entry[(equals + 1)..], word);
            if (!entries.TryAdd(word, phones)) throw new ArgumentException($"The word '{word}' has two entries in the lexicon.", nameof(lexicon));
            if (entries.Count > MaxEntries) throw new ArgumentException($"The lexicon has more than {MaxEntries} entries.", nameof(lexicon));
            lines.Add(word + "=" + string.Join(' ', phones));
        }

        HashSet<string> spelled = new(StringComparer.Ordinal);
        bool all = false;
        foreach (string raw in (spell ?? string.Empty).Split([' ', ',', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
            {
                all = true;
                continue;
            }

            string word = CleanWord(raw, nameof(spell));
            if (entries.ContainsKey(word)) throw new ArgumentException($"The word '{word}' is in the lexicon and in spell: choose one.", nameof(spell));
            spelled.Add(word);
        }

        if (entries.Count == 0 && spelled.Count == 0 && !all) return Empty;
        string canonical = "lex[" + string.Join(';', lines.Order(StringComparer.Ordinal)) + "]spell[" + (all ? "all" : string.Join(',', spelled.Order(StringComparer.Ordinal))) + "]";
        return new(entries, spelled, all, canonical);
    }

    /// <summary>Translates CMU phonemes (<c>T AH0 M EY1 T OW0</c>) to the phones of the lexicon of the voice (<c>t ax0 m ey1 t ow0</c>).</summary>
    /// <param name="phonemes">Space-separated phonemes; every vowel carries a stress digit (0, 1 or 2; the voice knows two stresses, so 2 is read as 1); <c>AX</c> needs none.</param>
    /// <returns>The phones, one string each.</returns>
    /// <exception cref="ArgumentException">There is no phoneme, one is unknown, or a vowel has no stress digit, or a consonant has one.</exception>
    public static string[] TranslatePhonemes(string phonemes) => TranslatePhonemes(phonemes, null);

    /// <summary>The phones that this lexicon gives a word, or null when it has no entry for it.</summary>
    /// <param name="word">The word as the text analysis names it (lower case).</param>
    /// <param name="phones">The phones of the lexicon of the voice.</param>
    /// <returns>True when the word has an entry.</returns>
    internal bool TryGet(string word, out string[] phones)
    {
        if (entries.Count > 0 && entries.TryGetValue(ByteText.Downcase(word), out string[]? found))
        {
            phones = found;
            return true;
        }

        phones = [];
        return false;
    }

    /// <summary>The text analysis, with this lexicon in front of <paramref name="inner"/>.</summary>
    internal ILexicon Over(ILexicon inner) => new Overlay(this, inner);

    private static string CleanWord(string raw, string parameter)
    {
        string word = raw.Trim().ToLowerInvariant();
        if (word.Length == 0 || word.Length > MaxWordLength || !word.All(c => c is >= 'a' and <= 'z' || c == '\''))
            throw new ArgumentException($"'{raw}' is not a word for the lexicon: use letters a to z (an apostrophe is allowed), at most {MaxWordLength}.", parameter);
        return word;
    }

    private static string[] TranslatePhonemes(string phonemes, string? word)
    {
        string where = word is null ? string.Empty : $" in the entry for '{word}'";
        List<string> phones = [];
        foreach (string raw in phonemes.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string symbol = raw.ToLowerInvariant();
            int stress = -1;
            if (symbol.Length > 1 && char.IsDigit(symbol[^1]))
            {
                stress = symbol[^1] - '0';
                symbol = symbol[..^1];
                if (stress > 2) throw new ArgumentException($"The stress digit of '{raw}'{where} must be 0, 1 or 2.");
            }

            if (Consonants.Contains(symbol))
            {
                if (stress >= 0) throw new ArgumentException($"'{raw}'{where} is a consonant: it takes no stress digit.");
                phones.Add(symbol);
            }
            else if (Vowels.Contains(symbol))
            {
                if (symbol == "ax")
                {
                    if (stress > 0) throw new ArgumentException($"'{raw}'{where}: AX is the unstressed vowel, write AX or AX0.");
                    phones.Add("ax0");
                }
                else
                {
                    if (stress < 0)
                    {
                        throw new ArgumentException($"The vowel '{raw}'{where} needs a stress digit: 0 (no stress), 1 (stressed) or 2 (read as 1), for example '{raw.ToUpperInvariant()}1'.");
                    }

                    phones.Add(symbol == "ah" && stress == 0 ? "ax0" : symbol + (stress == 0 ? "0" : "1"));
                }
            }
            else
            {
                throw new ArgumentException($"Unknown phoneme '{raw}'{where}. The phonemes are: {string.Join(' ', Phonemes)}.");
            }
        }

        if (phones.Count == 0) throw new ArgumentException($"There are no phonemes{where}.");
        if (phones.Count > MaxPhones) throw new ArgumentException($"An entry has at most {MaxPhones} phonemes{where}.");
        return [.. phones];
    }

    /// <summary>The lexicon as the text analysis asks it: a word of the block is known, and the words of <c>spell</c> are read letter by letter.</summary>
    private sealed class Overlay(FliteUserLexicon user, ILexicon inner) : ILexicon
    {
        // in_lex stays the question of the voice (as in C); only a list of letters that the rules cannot pronounce is not spelled when the block gave it phones (Has)
        public bool InLex(string word) => inner.InLex(word);

        public bool Spells(string name) => user.SpellAll || user.spelled.Contains(name);

        public bool Has(string name) => user.entries.ContainsKey(name);
    }
}
