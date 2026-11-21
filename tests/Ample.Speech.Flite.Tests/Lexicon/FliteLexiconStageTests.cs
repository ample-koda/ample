using System.Globalization;
using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Lexicon;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Tests.Lexicon;

/// <summary>
/// The phones stage of the ladder (plan/speech/flite/2-verification.md, step 4) with the words and the phrases of the C program as input: lexical insertion, pause insertion and the post-lexical rules
/// give the same segments (name, syllable, word) and the same syllable stress as the pinned C program, for the 341 lines of the corpus and 22 extra lines with 's, 've, 'll, 'd, "the" and homographs.
/// <c>Fixtures/phones-stage.txt</c> is cut from the golden stages by <c>reference/m4/make-phones-stage.mjs</c>.
/// </summary>
public class FliteLexiconStageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class Case
    {
        public string Header = string.Empty;
        public string Text = string.Empty;
        public List<(string Name, string Pos)> Words = [];
        public List<(string Name, int[] Words)> Phrases = [];
        public List<string> Stress = [];
        public List<(string Name, int Syllable, int Word)> Segments = [];
    }

    private static List<Case> Cases()
    {
        List<Case> cases = [];
        Case? current = null;
        foreach (string line in File.ReadAllLines(Fixture("phones-stage.txt")))
        {
            string[] f = line.Split('\t');
            switch (line.StartsWith("#---", StringComparison.Ordinal) ? "#---" : f[0])
            {
                case "#---":
                    current = new Case { Header = line };
                    cases.Add(current);
                    break;
                case "#TEXT":
                    current!.Text = f.Length > 1 ? f[1] : string.Empty;
                    break;
                case "#WORD":
                    current!.Words.Add((f[2], f[3]));
                    break;
                case "#PHRASE":
                    current!.Phrases.Add((f[2], f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()));
                    break;
                case "#SYL":
                    current!.Stress.Add(f[2]);
                    break;
                case "#SEG":
                    current!.Segments.Add((f[2], int.Parse(f[3], CultureInfo.InvariantCulture), int.Parse(f[4], CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return cases;
    }

    private static int IndexOf(Relation relation, Item? item)
    {
        if (item is null)
        {
            return -1;
        }

        int index = 0;
        for (Item? i = relation.Head; i is not null; i = Hrg.Next(i), index++)
        {
            if (Hrg.Equal(i, item))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The utterance of a case as the C program has it before the durations: words with their part of speech, phrases, then lexical insertion, pause insertion and postlex.</summary>
    private static Utterance Build(Case c)
    {
        Utterance u = FliteProsodyVoice.Create(FliteEnglishData.Instance).NewUtterance();
        Relation words = u.RelationCreate("Word");
        List<Item> items = [];
        foreach ((string name, string pos) in c.Words)
        {
            Item word = words.Append(null);
            Hrg.SetString(word, "name", name);
            Hrg.SetString(word, "pos", pos);
            items.Add(word);
        }

        Relation phrases = u.RelationCreate("Phrase");
        foreach ((string name, int[] members) in c.Phrases)
        {
            Item phrase = phrases.Append(null);
            Hrg.SetString(phrase, "name", name);
            foreach (int member in members)
            {
                Hrg.AddDaughter(phrase, items[member]);
            }
        }

        LexicalInsertion.Run(u, FliteLexicon.Cmu);
        FliteProsody.PauseInsertion(u);
        CmuPostlex.Run(u);
        return u;
    }

    [Fact]
    public void The_segments_and_the_stress_equal_the_c_program_on_the_corpus_and_the_extra_lines()
    {
        List<Case> cases = Cases();
        Assert.True(cases.Count >= 363, "the corpus has 341 lines and there are 22 extra lines");
        List<string> failures = [];
        foreach (Case c in cases)
        {
            Utterance u = Build(c);
            Relation syllables = u.GetRelation("Syllable");
            Relation words = u.GetRelation("Word");
            List<(string, int, int)> segments = [];
            for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
            {
                Item? syl = FeaturePath.PathToItem(s, "R:SylStructure.parent");
                Item? word = FeaturePath.PathToItem(s, "R:SylStructure.parent.parent");
                segments.Add((Hrg.Name(s), IndexOf(syllables, syl is null ? null : Hrg.ItemAs(syl, "Syllable")), IndexOf(words, word is null ? null : Hrg.ItemAs(word, "Word"))));
            }

            List<string> stress = [];
            for (Item? syl = syllables.Head; syl is not null; syl = Hrg.Next(syl))
            {
                stress.Add(Hrg.FeatPresent(syl, "stress") ? Hrg.FeatString(syl, "stress") : "-");
            }

            string mine = string.Join(' ', segments.Select(x => $"{x.Item1}/{x.Item2}/{x.Item3}")) + " | " + string.Join(' ', stress);
            string theirs = string.Join(' ', c.Segments.Select(x => $"{x.Name}/{x.Syllable}/{x.Word}")) + " | " + string.Join(' ', c.Stress);
            if (mine != theirs)
            {
                failures.Add($"{c.Header} '{c.Text}'\n  C : {theirs}\n  C#: {mine}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} differ:\n" + string.Join('\n', failures.Take(8)));
    }

    [Theory]
    [InlineData("cat 's", "k ae t s")]
    [InlineData("church 's", "ch er ch ax z")]
    [InlineData("the apple", "dh iy ae p ax l")]
    [InlineData("the cat", "dh ax k ae t")]
    public void The_postlex_rules_change_the_segments_like_the_c_program(string words, string expected)
    {
        Utterance u = FliteProsodyVoice.Create(FliteEnglishData.Instance).NewUtterance();
        Relation relation = u.RelationCreate("Word");
        Relation phrases = u.RelationCreate("Phrase");
        Item phrase = phrases.Append(null);
        foreach (string w in words.Split(' '))
        {
            Item word = relation.Append(null);
            Hrg.SetString(word, "name", w);
            Hrg.SetString(word, "pos", w == "'s" ? "n" : "0");
            Hrg.AddDaughter(phrase, word);
        }

        LexicalInsertion.Run(u, FliteLexicon.Cmu);
        FliteProsody.PauseInsertion(u);
        CmuPostlex.Run(u);
        List<string> names = [];
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
        {
            names.Add(Hrg.Name(s));
        }

        Assert.Equal("pau " + expected + " pau", string.Join(' ', names));
    }
}
