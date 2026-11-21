using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), lang/cmulex/cmu_postlex.c. Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The post-lexical rules of the CMU lexicon (<c>cmu_postlex.c</c>): a schwa before 's, 've, 'll and 'd where the sound needs one,
/// a voiceless "s" after a voiceless phone, and "the" before a vowel as "iy".
/// A literal translation; the added schwa is a new segment that is also put into SylStructure before the segment (the item_prepend of both relations).
/// </summary>
internal static class CmuPostlex
{
    /// <summary><c>cmu_postlex</c>.</summary>
    public static void Run(Utterance utterance)
    {
        ApostropheS(utterance);
        TheIyAx(utterance);
    }

    private static void ApostropheS(Utterance utterance)
    {
        Relation segments = utterance.GetRelation("Segment");
        for (Item? s = Hrg.Next(segments.Head); s is not null; s = Hrg.Next(s))
        {
            Phoneset ps = Phoneset.OfItem(s);
            string word = Values.String(FeaturePath.Ffeature(s, "R:SylStructure.parent.parent.name"));
            if (word == "'s")
            {
                string previous = Hrg.FeatString(Hrg.Prev(s), "name");
                string ctype = ps.PhoneFeatureString(previous, "ctype");
                string cplace = ps.PhoneFeatureString(previous, "cplace");
                if (StrChr("fa", ctype) && !StrChr("dbg", cplace))
                {
                    AddSchwa(s);
                }
                else if (ps.PhoneFeatureString(previous, "cvox") == "-")
                {
                    Hrg.SetString(s, "name", "s");
                }
            }
            else if (word is "'ve" or "'ll" or "'d")
            {
                if (FeaturePath.FfeatureString(s, "p.ph_vc") == "-")
                {
                    AddSchwa(s);
                }
            }
        }
    }

    private static void AddSchwa(Item s)
    {
        Item schwa = Hrg.Prepend(s, null);
        Hrg.SetString(schwa, "name", "ax");
        Hrg.Prepend(Hrg.ItemAs(s, "SylStructure")!, schwa);
    }

    private static void TheIyAx(Utterance utterance)
    {
        for (Item? i = utterance.GetRelation("Segment").Head; i is not null; i = Hrg.Next(i))
        {
            if (Hrg.Name(i) == "ax")
            {
                string word = FeaturePath.FfeatureString(i, "R:SylStructure.parent.parent.name");
                if (word == "the" && FeaturePath.FfeatureString(i, "n.ph_vc") == "+")
                {
                    Hrg.SetString(i, "name", "iy");
                }
            }
        }
    }

    private static bool StrChr(string set, string value) => value.Length == 0 || set.Contains(value[0], StringComparison.Ordinal);
}
