using Ample.Speech.Flite.Lexicon;
using Ample.Speech.Flite.Tests.EndToEnd;

namespace Ample.Speech.Flite.Tests.UserLexicon;

/// <summary>
/// The words of a block that have their own phones are the run-time addenda of Flite (<c>flite -add_lex</c>, <c>default_lexical_insertion</c> looks a word up in them first):
/// a text spoken with a user lexicon gives the same samples as the C program with the same entry in an addenda file, on 320 entries with generated phones (the word twice in the
/// text, in lower case). A word that the block spells is read the way the C program reads an unpronounceable word of letters, which it does by itself for a word
/// without a vowel: 160 such words give the same samples with <c>spell</c> and without it, and the same as C.
/// The references are <c>plan/speech/flite/reference/x6</c> (<c>run-addenda.sh</c>, <c>run-spell.sh</c>), with the Ubuntu build of Flite that equals the pinned commit on the kal16 sound.
/// </summary>
public class FliteUserLexiconParityTests
{
    private static readonly Lazy<string[][]> Addenda = new(() => [.. File.ReadAllLines(EndToEndFixtures.Path("flite-x6-addenda.tsv")).Select(l => l.Split('\t'))]);
    private static readonly Lazy<string[][]> Spell = new(() => [.. File.ReadAllLines(EndToEndFixtures.Path("flite-x6-spell.tsv")).Select(l => l.Split('\t'))]);

    [Fact]
    public void Every_entry_gives_the_samples_of_the_c_program_with_the_same_addendum_except_where_c_spells_the_word()
    {
        Assert.Equal(320, Addenda.Value.Length);
        List<string> failures = [], spelledByC = [];
        foreach (string[] line in Addenda.Value)
        {
            (string word, string phonemes, string text, string sha) = (line[0], line[1], line[2], line[3]);
            FliteUserLexicon lexicon = FliteUserLexicon.Parse($"{word} = {phonemes}", null);
            string mine = EndToEndFixtures.Sha256(FliteEngine.Default.Render(text, new FliteOptions { UserLexicon = lexicon }).Samples);
            if (mine == sha) continue;
            // C asks only the addenda of the voice when it decides whether a word can be said (in_lex): a word that its text analysis spells or expands (gpu, usb, fbi, etc,
            // tchaikovsky) never reaches the run-time entry, and C then speaks the text as if there were none. The block's words count as words here, so the entry is used: the one
            // difference to C, and it exists only where C ignored the entry.
            if (EndToEndFixtures.Sha256(FliteEngine.Default.Render(text).Samples) == sha) spelledByC.Add(word); else failures.Add($"{word} = {phonemes}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} entries differ from C: {string.Join("; ", failures.Take(10))}");
        Assert.InRange(spelledByC.Count, 1, 20);
        Assert.True(320 - spelledByC.Count >= 300, "at least 300 entries are checked against C");
    }

    [Theory]
    [InlineData("gpu", "JH IY1 P IY1 Y UW1")]
    [InlineData("dvd", "D IY1 V IY1 D IY1")]
    public void A_list_of_consonants_that_c_would_spell_is_spoken_with_the_phones_of_the_block(string word, string phonemes)
    {
        string text = $"I said {word} twice.";
        FliteRenderResult plain = FliteEngine.Default.Render(text);
        FliteRenderResult own = FliteEngine.Default.Render(text, new FliteOptions { UserLexicon = FliteUserLexicon.Parse($"{word} = {phonemes}", null) });

        Assert.NotEqual(plain.Samples, own.Samples);
        Assert.Equal(1, own.Words.Count(w => w.Spoken == word));   // one word with the phones of the block instead of its letters
        Assert.DoesNotContain(plain.Words, w => w.Spoken == word);
    }

    [Fact]
    public void An_abbreviation_that_the_text_analysis_expands_keeps_its_expansion()
    {
        string text = "I said etc twice.";
        short[] plain = FliteEngine.Default.Render(text).Samples;
        short[] own = FliteEngine.Default.Render(text, new FliteOptions { UserLexicon = FliteUserLexicon.Parse("etc = EH1 T S EH1 T ER0 AX", null) }).Samples;

        Assert.Equal(plain, own);   // as in C: the run-time addenda are asked for the words that are left, and etc is expanded to et cetera before
    }

    [Fact]
    public void The_entries_change_the_sound_and_a_lexicon_without_entries_changes_nothing()
    {
        string[] first = Addenda.Value[0];
        short[] plain = FliteEngine.Default.Render(first[2]).Samples;
        short[] same = FliteEngine.Default.Render(first[2], new FliteOptions { UserLexicon = FliteUserLexicon.Empty }).Samples;
        short[] own = FliteEngine.Default.Render(first[2], new FliteOptions { UserLexicon = FliteUserLexicon.Parse($"{first[0]} = {first[1]}", null) }).Samples;

        Assert.Equal(plain, same);
        Assert.NotEqual(plain, own);
    }

    [Fact]
    public void A_word_that_is_spelled_gives_the_samples_of_the_c_program_that_spells_it()
    {
        Assert.Equal(160, Spell.Value.Length);
        List<string> failures = [];
        foreach (string[] line in Spell.Value)
        {
            (string word, string sha) = (line[0], line[1]);
            string text = $"I said {word} twice.";
            short[] plain = FliteEngine.Default.Render(text).Samples;
            short[] spelled = FliteEngine.Default.Render(text, new FliteOptions { UserLexicon = FliteUserLexicon.Parse(null, word) }).Samples;
            if (EndToEndFixtures.Sha256(plain) != sha) failures.Add($"{word}: the port without spell differs from C");
            if (EndToEndFixtures.Sha256(spelled) != sha) failures.Add($"{word}: with spell differs from C");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differences: {string.Join("; ", failures.Take(10))}");
    }

    [Fact]
    public void Spelling_a_pronounceable_word_reads_every_letter_and_all_spells_every_word()
    {
        FliteRenderResult plain = FliteEngine.Default.Render("a cat sat");
        FliteRenderResult cat = FliteEngine.Default.Render("a cat sat", new FliteOptions { UserLexicon = FliteUserLexicon.Parse(null, "cat") });
        FliteRenderResult all = FliteEngine.Default.Render("a cat sat", new FliteOptions { UserLexicon = FliteUserLexicon.Parse(null, "all") });

        Assert.Equal(["a", "cat", "sat"], plain.Words.Select(w => w.Spoken));
        Assert.Equal(["a", "c", "a", "t", "sat"], cat.Words.Select(w => w.Spoken == "_a" ? "a" : w.Spoken));
        Assert.Equal(["_a", "c", "_a", "t", "s", "_a", "t"], all.Words.Select(w => w.Spoken));
        // the letters of one token stand where the token stands in the text
        Assert.All(cat.Words.Skip(1).Take(3), w => Assert.Equal("cat", w.Token));
    }
}
