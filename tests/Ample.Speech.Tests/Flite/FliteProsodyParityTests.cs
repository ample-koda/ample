using System.Globalization;
using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Tests.Flite;

/// <summary>
/// Flite M5 parity with the reference build of the C program (commit 6c9f20dc): phrasing, pause insertion, accents and tones, segment durations and F0 targets
/// of the 341 lines of the corpus. The input of each line (tokens, words with their part of speech, syllables with their stress and segments) is rebuilt from the
/// reference dump; the modules run on it and their output is compared with the dump. Floats are compared as bit patterns.
/// </summary>
public sealed class FliteProsodyParityTests
{
    private static readonly Lazy<List<Block>> Blocks = new(Load);
    private static readonly Lazy<FliteProsodyVoice> Voice = new(() => FliteProsodyVoice.Create(FliteEnglishData.Instance));

    [Fact]
    public void The_reference_dump_has_the_whole_corpus()
    {
        Assert.Equal(341, Blocks.Value.Count);
        Assert.Equal(341, Blocks.Value.Count(b => b.Segments.Count > 0));
    }

    [Fact]
    public void Phrasing_pauses_accents_durations_and_targets_equal_the_reference_bit_for_bit()
    {
        List<string> failures = [];
        int compared = 0;
        foreach (Block b in Blocks.Value.Where(b => b.Segments.Count > 0))
        {
            compared++;
            Utterance u = Build(b);
            FliteProsody.Phrasing(u);
            FliteProsody.PauseInsertion(u);
            FliteProsody.Intonation(u);
            FliteProsody.Duration(u);
            FliteProsody.F0Model(u, FliteEnglishData.Instance.F0Terms);
            Compare(b, u, failures);
        }

        Assert.Equal(341, compared);
        Assert.True(failures.Count == 0, $"{failures.Count} differences, first ones:\n{string.Join('\n', failures.Take(12))}");
    }

    private static void Compare(Block b, Utterance u, List<string> failures)
    {
        Relation word = u.GetRelation("Word");
        List<Item> words = Walk(word);
        // phrasing
        List<string> phrases = [];
        int k = 0;
        for (Item? p = u.GetRelation("Phrase").Head; p is not null; p = Hrg.Next(p), k++)
        {
            string daughters = string.Concat(Daughters(p).Select(d => $"{IndexOf(words, Hrg.ItemAs(d, "Word"))} "));
            phrases.Add($"{k}\t{SF(p, "name")}\t{daughters}");
        }

        CompareLines(b, "phrase", b.Phrases, phrases, failures);

        // segments (names, ends as bit patterns, syllable and word index) after pauses and durations
        Relation syl = u.GetRelation("Syllable");
        List<Item> syls = Walk(syl);
        List<string> segs = [];
        k = 0;
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s), k++)
        {
            Item? ss = Hrg.Parent(Hrg.ItemAs(s, "SylStructure"));
            Item? w = ss is null ? null : Hrg.Parent(Hrg.ItemAs(ss, "SylStructure"));
            float end = Hrg.FeatPresent(s, "end") ? Hrg.FeatFloat(s, "end") : -1;
            segs.Add($"{k}\t{SF(s, "name")}\t{Bits(end)}\t{(ss is null ? -1 : IndexOf(syls, Hrg.ItemAs(ss, "Syllable")))}\t{(w is null ? -1 : IndexOf(words, Hrg.ItemAs(w, "Word")))}");
        }

        CompareLines(b, "segment", b.Segments.Select(s => $"{s.Index}\t{s.Name}\t{Bits(s.End)}\t{s.Syllable}\t{s.Word}").ToList(), segs, failures);

        // accents and tones
        List<string> sylLines = [];
        k = 0;
        for (Item? s = syl.Head; s is not null; s = Hrg.Next(s), k++)
        {
            sylLines.Add($"{k}\t{SF(s, "accent")}\t{SF(s, "endtone")}");
        }

        CompareLines(b, "accent/tone", b.Syllables.Select(s => $"{s.Index}\t{s.Accent}\t{s.Endtone}").ToList(), sylLines, failures);

        // F0 targets
        List<string> targets = [];
        k = 0;
        for (Item? t = u.GetRelation("Target").Head; t is not null; t = Hrg.Next(t), k++)
        {
            targets.Add($"{k}\t{Bits(Hrg.FeatFloat(t, "pos"))}\t{Bits(Hrg.FeatFloat(t, "f0"))}");
        }

        CompareLines(b, "target", b.Targets.Select(t => $"{t.Index}\t{Bits(t.Pos)}\t{Bits(t.F0)}").ToList(), targets, failures);
    }

    private static void CompareLines(Block b, string stage, List<string> expected, List<string> actual, List<string> failures)
    {
        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return;
        }

        int i = 0;
        while (i < expected.Count && i < actual.Count && expected[i] == actual[i])
        {
            i++;
        }

        failures.Add($"line {b.Number} '{b.Text}' {stage}: expected {(i < expected.Count ? expected[i] : "<end>")} but was {(i < actual.Count ? actual[i] : "<end>")}");
    }

    private static string Bits(float v) => $"{BitConverter.SingleToInt32Bits(v):x8}";

    private static string SF(Item i, string name) => Hrg.FeatPresent(i, name) ? Hrg.FeatString(i, name) : "-";

    private static List<Item> Walk(Relation r)
    {
        List<Item> items = [];
        for (Item? i = r.Head; i is not null; i = Hrg.Next(i))
        {
            items.Add(i);
        }

        return items;
    }

    private static IEnumerable<Item> Daughters(Item p)
    {
        for (Item? d = Hrg.Daughter(p); d is not null; d = Hrg.Next(d))
        {
            yield return d;
        }
    }

    private static int IndexOf(List<Item> items, Item? x)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (Hrg.Equal(items[i], x))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The utterance after the lexicon: tokens, words (with <c>pos</c>), SylStructure, syllables with their stress, and the segments without silences.</summary>
    private static Utterance Build(Block b)
    {
        Utterance u = Voice.Value.NewUtterance();
        Relation tokens = u.RelationCreate("Token");
        Relation words = u.RelationCreate("Word");
        Relation sylStructure = u.RelationCreate("SylStructure");
        Relation syllables = u.RelationCreate("Syllable");
        Relation segments = u.RelationCreate("Segment");
        List<Item> tokenItems = [];
        foreach (Tok t in b.Tokens)
        {
            Item item = tokens.Append(null);
            Hrg.SetString(item, "name", t.Name);
            Hrg.SetString(item, "whitespace", t.Whitespace);
            Hrg.SetString(item, "prepunctuation", t.Prepunctuation);
            Hrg.SetString(item, "punc", t.Punc);
            tokenItems.Add(item);
        }

        foreach (Wrd w in b.Words)
        {
            Item word = Hrg.AddDaughter(tokenItems[w.Token], null)!;
            Hrg.SetString(word, "name", w.Name);
            Hrg.SetString(word, "pos", w.Pos);
            if (b.Breaks.TryGetValue(w.Index, out string? brk))
            {
                Hrg.SetString(word, "break", brk);
            }

            words.Append(word);
            Item ssword = sylStructure.Append(word);
            foreach (Syl s in b.Syllables.Where(s => b.Segments.Any(g => g.Syllable == s.Index && g.Word == w.Index)))
            {
                Item sylItem = syllables.Append(null);
                Item sssyl = Hrg.AddDaughter(ssword, sylItem)!;
                if (s.Stress != "-")
                {
                    Hrg.SetString(sssyl, "stress", s.Stress);
                }

                foreach (Seg g in b.Segments.Where(g => g.Syllable == s.Index))
                {
                    Item segItem = segments.Append(null);
                    Hrg.SetString(segItem, "name", g.Name);
                    Hrg.AddDaughter(sssyl, segItem);
                }
            }
        }

        return u;
    }

    private static List<Block> Load()
    {
        List<Block> blocks = [];
        Block? cur = null;
        foreach (string line in File.ReadLines(FliteReferenceFixtures.Path("flite-m5-prosody.txt")))
        {
            string[] f = line.Split('\t');
            if (line.StartsWith("#---", StringComparison.Ordinal))
            {
                cur = new Block { Number = int.Parse(line.AsSpan(5, 3), CultureInfo.InvariantCulture) };
                blocks.Add(cur);
                continue;
            }

            switch (f[0])
            {
                case "#TEXT":
                    cur!.Text = f.Length > 1 ? f[1] : string.Empty;
                    break;
                case "#TOKEN":
                    cur!.Tokens.Add(new Tok(f[2], Unbracket(f[3]), Unbracket(f[4]), Unbracket(f[5])));
                    break;
                case "#WORD":
                    cur!.Words.Add(new Wrd(int.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[3], int.Parse(f[5], CultureInfo.InvariantCulture)));
                    break;
                case "#BREAK":
                    cur!.Breaks[int.Parse(f[1], CultureInfo.InvariantCulture)] = f[2];
                    break;
                case "#PHRASE":
                    cur!.Phrases.Add(string.Join('\t', f.Skip(1)));
                    break;
                case "#SYL":
                    cur!.Syllables.Add(new Syl(int.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[3], f[4]));
                    break;
                case "#SEG":
                    cur!.Segments.Add(new Seg(int.Parse(f[1], CultureInfo.InvariantCulture), f[2], Float(f[3]), int.Parse(f[4], CultureInfo.InvariantCulture), int.Parse(f[5], CultureInfo.InvariantCulture)));
                    break;
                case "#TARGET":
                    cur!.Targets.Add(new Tgt(int.Parse(f[1], CultureInfo.InvariantCulture), Float(f[2]), Float(f[3])));
                    break;
            }
        }

        return blocks;
    }

    private static string Unbracket(string s) => s.Length >= 2 && s[0] == '[' && s[^1] == ']' ? s[1..^1] : s;

    private static float Float(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed record Tok(string Name, string Whitespace, string Prepunctuation, string Punc);

    private sealed record Wrd(int Index, string Name, string Pos, int Token);

    private sealed record Syl(int Index, string Stress, string Accent, string Endtone);

    private sealed record Seg(int Index, string Name, float End, int Syllable, int Word);

    private sealed record Tgt(int Index, float Pos, float F0);

    private sealed class Block
    {
        public int Number { get; set; }

        public string Text { get; set; } = string.Empty;

        public List<Tok> Tokens { get; } = [];

        public List<Wrd> Words { get; } = [];

        public Dictionary<int, string> Breaks { get; } = [];

        public List<string> Phrases { get; } = [];

        public List<Syl> Syllables { get; } = [];

        public List<Seg> Segments { get; } = [];

        public List<Tgt> Targets { get; } = [];
    }
}
