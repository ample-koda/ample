using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_ffeatures.c. Translated to C# and modified.
// Copyright and licence: LICENSES/flite.txt, including the original author Alan W Black.
internal static class CgFeatureFunctions
{
    private const string StateFirst = "R:mcep_link.parent.daughter1.frame_number";
    private const string StateLast = "R:mcep_link.parent.daughtern.frame_number";
    private const string PhoneFirst = "R:mcep_link.parent.R:segstate.parent.daughter1.R:mcep_link.daughter1.frame_number";
    private const string PhoneLast = "R:mcep_link.parent.R:segstate.parent.daughtern.R:mcep_link.daughtern.frame_number";
    private const string Phrase = "R:mcep_link.parent.R:segstate.parent.R:SylStructure.parent.parent.R:Phrase.parent.";
    private const string PhraseFirst = Phrase + "daughter1.R:SylStructure.daughter1.daughter1.R:Segment.p.end";
    private const string PhraseLast = Phrase + "daughtern.R:SylStructure.daughtern.daughtern.R:Segment.end";

    // Registered only by the clustergen voice; kal16 keeps its existing feature environment.
    public static void Register(FeatureSet functions)
    {
        void Add(string name, Func<Item, Value?> fn) => functions.Set(name, new FunctionValue(fn));
        Add("lisp_cg_duration", Duration);
        Add("lisp_cg_state_pos", StatePosition);
        Add("lisp_cg_state_place", p => Place(p, StateFirst, StateLast));
        Add("lisp_cg_state_index", p => Index(p, StateFirst, false));
        Add("lisp_cg_state_rindex", p => Index(p, StateLast, true));
        Add("lisp_cg_phone_place", p => Place(p, PhoneFirst, PhoneLast));
        Add("lisp_cg_phone_index", p => Index(p, PhoneFirst, false));
        Add("lisp_cg_phone_rindex", p => Index(p, PhoneLast, true));
        Add("lisp_is_pau", p => Hrg.Name(p) == "pau" ? Values.String1 : Values.String0);
        Add("lisp_cg_find_phrase_number", PhraseNumber);
        Add("lisp_cg_position_in_phrasep", p => PositionInPhrase(p, true));
        Add("lisp_cg_position_in_phrase", p => PositionInPhrase(p, false));
        Add("lisp_cg_break", Break);
        Add("lisp_cg_syl_ratio", SyllableRatio);
        Add("lisp_cg_phrase_ratio", PhraseRatio);
        Add("lisp_cg_syls_in_phrase", SyllablesInPhrase);
        Add("lisp_cg_content_words_in_phrase", ContentWordsInPhrase);   // lang/usenglish/us_ffeatures.c
    }

    private static Value? Duration(Item p) => Hrg.Prev(p) is null ? Hrg.Feat(p, "end")
        : new FloatValue(Hrg.FeatFloat(p, "end") - Hrg.FeatFloat(Hrg.Prev(p), "end"));

    private static Value StatePosition(Item p)
    {
        string name = Hrg.Name(p);
        if (name != FeaturePath.FfeatureString(p, "p.name")) return new StringValue("b");
        return new StringValue(name == FeaturePath.FfeatureString(p, "n.name") ? "m" : "e");
    }

    private static FloatValue Place(Item p, string first, string last)
    {
        float start = FeaturePath.FfeatureInt(p, first);
        float end = FeaturePath.FfeatureInt(p, last);
        int current = Hrg.FeatInt(p, "frame_number");
        return new FloatValue(end == start ? 0 : (current - start) / (end - start));
    }

    private static FloatValue Index(Item p, string path, bool reverse)
    {
        float boundary = FeaturePath.FfeatureInt(p, path);
        int current = Hrg.FeatInt(p, "frame_number");
        return new FloatValue(reverse ? boundary - current : current - boundary);
    }

    private static Value PhraseNumber(Item p)
    {
        int count = 0;
        for (Item? previous = Hrg.Prev(p); previous is not null; previous = Hrg.Prev(previous)) count++;
        return Values.IntN(count);
    }

    private static FloatValue PositionInPhrase(Item p, bool number)
    {
        float start = FeaturePath.FfeatureFloat(p, PhraseFirst);
        float end = FeaturePath.FfeatureFloat(p, PhraseLast);
        if (end == start) return new FloatValue(-1);
        float phrase = number ? FeaturePath.FfeatureFloat(p, Phrase + "lisp_cg_find_phrase_number") : 0;
        float position = (float)(phrase + ((0.005 * Hrg.FeatFloat(p, "frame_number")) - start) / (end - start));
        return new FloatValue(position);
    }

    private static FloatValue SyllableRatio(Item p) =>
        new((1 + FeaturePath.FfeatureFloat(p, "syl_in")) / (1 + FeaturePath.FfeatureFloat(p, "syl_in") + FeaturePath.FfeatureFloat(p, "syl_out")));

    private static FloatValue PhraseRatio(Item p)
    {
        Item last = p;
        while (Hrg.Next(last) is { } next) last = next;
        return new((1 + FeaturePath.FfeatureFloat(p, "lisp_cg_find_phrase_number")) / (1 + FeaturePath.FfeatureFloat(last, "lisp_cg_find_phrase_number")));
    }

    private static FloatValue SyllablesInPhrase(Item p)
    {
        Item? word = Hrg.ItemAs(Hrg.Daughter(p), "Word");
        return new(1 + FeaturePath.FfeatureFloat(word!, "R:SylStructure.daughter1.R:Syllable.syl_out"));
    }

    private static FloatValue ContentWordsInPhrase(Item p) =>
        new(FeaturePath.FfeatureFloat(p, "R:SylStructure.parent.parent.R:Word.content_words_in") + FeaturePath.FfeatureFloat(p, "R:SylStructure.parent.parent.R:Word.content_words_out"));

    private static Value Break(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "SylStructure");
        if (ss is null || Hrg.Next(ss) is not null) return Values.String0;
        if (FeaturePath.PathToItem(ss, "R:SylStructure.parent.R:Word.n") is null) return Values.String4;
        return FeaturePath.PathToItem(ss, "R:SylStructure.parent.R:Phrase.n") is null ? Values.String3 : Values.String1;
    }
}
