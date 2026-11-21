using System.Text.RegularExpressions;

namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_ffeatures.c and lang/usenglish/us_ffeatures.c: the feature functions
// that the trees and models of the voice ask for by name. Translated to C# and modified: see Value.cs.
// Not ported: the clustergen functions (lisp_cg_*, lisp_is_pau): only the clustergen voices (slt, rms, awb) use them, not kal16.
// Each function is a literal translation, including the quirks (an int in a string, a shared "0" when a path leads nowhere, the loop bound of 19).

/// <summary>A class of closed-class words of <c>us_gpos</c> (in, to, det, md, cc, wp, pps, aux): the guessed part of speech of a word is the class that lists it.</summary>
/// <param name="Name">The class (<c>us_gpos[s][0]</c>).</param>
/// <param name="Words">The words of the class.</param>
internal sealed record GposClass(StringValue Name, string[] Words)
{
    /// <summary>Makes a class from its name and words.</summary>
    public GposClass(string name, params string[] words)
        : this(new StringValue(name), words)
    {
    }
}

/// <summary>The feature functions of the English voice and their registration.</summary>
internal static class FeatureFunctions
{
    private static readonly StringValue Onset = new("onset");
    private static readonly StringValue Coda = new("coda");
    private static readonly StringValue Initial = new("initial");
    private static readonly StringValue Single = new("single");
    private static readonly StringValue Final = new("final");
    private static readonly StringValue Mid = new("mid");
    private static readonly StringValue Empty = new(string.Empty);
    private static readonly StringValue Content = new("content");
    private static readonly StringValue Numeric = new("numeric");
    private static readonly StringValue Number = new("number");
    private static readonly StringValue Month = new("month");
    private static readonly StringValue Day = new("day");
    private static readonly StringValue Other = new("_other_");
    private static readonly StringValue A = new("a");
    private static readonly StringValue Flight = new("flight");
    private static readonly StringValue To = new("to");

    // cst_rx_digits and cst_rx_double are compiled Henry Spencer programs; these are their languages (read out of the program bytes, checked against the C code by the tests)
    private static readonly Regex DigitsRx = new(@"\A[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex DoubleRx = new(@"\A-?(?:[0-9]+\.[0-9]*|[0-9]+|\.[0-9]+)(?:[eE][-+]?[0-9]+)?\z", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> MonthNames = new(StringComparer.Ordinal)
    {
        "jan", "january", "feb", "february", "mar", "march", "apr", "april", "may", "jun", "june", "jul", "july",
        "aug", "august", "sep", "sept", "september", "oct", "october", "nov", "november", "dec", "december",
    };

    private static readonly HashSet<string> DayNames = new(StringComparer.Ordinal)
    {
        "sun", "sunday", "mon", "monday", "tue", "tues", "tuesday", "wed", "wednesday", "thu", "thurs", "thursday", "fri", "friday", "sat", "saturday",
    };

    /// <summary><c>us_ff_register</c> (with <c>basic_ff_register</c>): puts the feature functions of the voice into the set that the utterance links to.</summary>
    /// <param name="ffunctions">The feature functions of the voice.</param>
    /// <param name="gpos">The word classes of <c>us_gpos</c> (data of milestone M3).</param>
    public static void Register(FeatureSet ffunctions, IReadOnlyList<GposClass> gpos)
    {
        void Reg(string name, Func<Item, Value?> f) => ffunctions.Set(name, new FunctionValue(f));

        Reg("ph_vc", PhVc);
        Reg("ph_vlng", PhVlng);
        Reg("ph_vheight", PhVheight);
        Reg("ph_vfront", PhVfront);
        Reg("ph_vrnd", PhVrnd);
        Reg("ph_ctype", PhCtype);
        Reg("ph_cplace", PhCplace);
        Reg("ph_cvox", PhCvox);
        Reg("word_numsyls", WordNumsyls);
        Reg("word_break", WordBreak);
        Reg("word_punc", WordPunc);
        Reg("ssyl_in", SsylIn);
        Reg("ssyl_out", SsylOut);
        Reg("syl_in", SylIn);
        Reg("syl_out", SylOut);
        Reg("syl_break", SylBreak);
        Reg("old_syl_break", SylBreak);
        Reg("syl_onsetsize", SylOnsetsize);
        Reg("syl_codasize", SylCodasize);
        Reg("accented", Accented);
        Reg("asyl_in", AsylIn);
        Reg("asyl_out", AsylOut);
        Reg("lisp_coda_fric", seg => SegCodaCtype(seg, "f"));
        Reg("lisp_onset_fric", seg => SegOnsetCtype(seg, "f"));
        Reg("lisp_coda_stop", seg => SegCodaCtype(seg, "s"));
        Reg("lisp_onset_stop", seg => SegOnsetCtype(seg, "s"));
        Reg("lisp_coda_nasal", seg => SegCodaCtype(seg, "n"));
        Reg("lisp_onset_nasal", seg => SegOnsetCtype(seg, "n"));
        Reg("lisp_coda_glide", SegCodaGlide);
        Reg("lisp_onset_glide", SegOnsetGlide);
        Reg("seg_onsetcoda", SegOnsetcoda);
        Reg("pos_in_syl", PosInSyl);
        Reg("position_type", PositionType);
        Reg("sub_phrases", SubPhrases);
        Reg("last_accent", LastAccent);
        Reg("next_accent", NextAccent);
        Reg("syl_final", SylFinal);
        Reg("syl_initial", SylInitial);
        Reg("segment_duration", SegmentDuration);
        Reg("pos_in_phrase", PosInPhrase);
        Reg("pos_in_word", PosInWord);
        Reg("syllable_duration", SyllableDuration);
        Reg("syl_vowel", SylVowel);
        Reg("syl_numphones", SylNumphones);
        Reg("gpos", word => Gpos(word, gpos));
        Reg("num_digits", NumDigits);
        Reg("month_range", MonthRange);
        Reg("token_pos_guess", TokenPosGuess);
        Reg("content_words_in", ContentWordsIn);
        Reg("content_words_out", ContentWordsOut);
    }

    /// <summary><c>ph_vc</c>: vowel (+) or consonant (-).</summary>
    public static Value? PhVc(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "vc");

    /// <summary><c>ph_vlng</c>: vowel length.</summary>
    public static Value? PhVlng(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "vlng");

    /// <summary><c>ph_vheight</c>: vowel height.</summary>
    public static Value? PhVheight(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "vheight");

    /// <summary><c>ph_vrnd</c>: vowel rounding.</summary>
    public static Value? PhVrnd(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "vrnd");

    /// <summary><c>ph_vfront</c>: vowel frontness.</summary>
    public static Value? PhVfront(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "vfront");

    /// <summary><c>ph_ctype</c>: consonant type.</summary>
    public static Value? PhCtype(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "ctype");

    /// <summary><c>ph_cplace</c>: consonant place.</summary>
    public static Value? PhCplace(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "cplace");

    /// <summary><c>ph_cvox</c>: consonant voicing.</summary>
    public static Value? PhCvox(Item p) => Phoneset.OfItem(p).PhoneFeature(Hrg.Name(p), "cvox");

    /// <summary><c>accented</c>: "1" when the syllable has an accent or an end tone, else "0".</summary>
    public static Value? Accented(Item syl) =>
        Hrg.FeatPresent(syl, "accent") || Hrg.FeatPresent(syl, "endtone") ? Values.String1 : Values.String0;

    /// <summary><c>word_numsyls</c>: the number of syllables of the word (an int).</summary>
    public static Value? WordNumsyls(Item word)
    {
        int c = 0;
        for (Item? d = Hrg.Daughter(Hrg.ItemAs(word, "SylStructure")); d is not null; d = Hrg.Next(d))
        {
            c++;
        }

        return Values.IntN(c);
    }

    /// <summary><c>word_break</c>: the break after the word: "1" inside a phrase, "4" at the end of a "BB" phrase, "3" at the end of a "B" phrase.</summary>
    public static Value? WordBreak(Item word)
    {
        Item? ww = Hrg.ItemAs(word, "Phrase");
        if (ww is null || Hrg.Next(ww) is not null)
        {
            return Values.String1;
        }

        Item? pp = Hrg.Parent(ww);
        string pname = Values.String(Hrg.Feat(pp, "name"));
        if (pname == "BB")
        {
            return Values.String4;
        }

        return pname == "B" ? Values.String3 : Values.String1;
    }

    /// <summary><c>word_punc</c>: the punctuation after the token of the word when the word is the last of its token, else "".</summary>
    public static Value? WordPunc(Item word)
    {
        Item? ww = Hrg.ItemAs(word, "Token");
        if (ww is not null && Hrg.Next(ww) is not null)
        {
            return Empty;
        }

        return FeaturePath.Ffeature(Hrg.Parent(ww), "punc");
    }

    /// <summary><c>ssyl_in</c>: the stressed syllables since the start of the phrase.</summary>
    public static Value? SsylIn(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughter.R:SylStructure.daughter");
        if (Hrg.Equal(ss, fs))
        {
            return Values.StringN(0);
        }

        int c = 0;
        for (Item? p = Hrg.Prev(ss); p is not null && !Hrg.Equal(p, fs) && c < Values.CountLimit; p = Hrg.Prev(p))
        {
            if (FeaturePath.FfeatureString(p, "stress") == "1")
            {
                c++;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>ssyl_out</c>: the stressed syllables until the end of the phrase.</summary>
    public static Value? SsylOut(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughtern.R:SylStructure.daughtern");
        if (Hrg.Equal(ss, fs))
        {
            return Values.StringN(0);
        }

        int c = 0;
        for (Item? p = Hrg.Next(ss); p is not null && c < Values.CountLimit; p = Hrg.Next(p))
        {
            if (FeaturePath.FfeatureString(p, "stress") == "1")
            {
                c++;
            }

            if (Hrg.Equal(p, fs))
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>syl_in</c>: the syllables since the start of the phrase.</summary>
    public static Value? SylIn(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughter.R:SylStructure.daughter");
        int c = 0;
        for (Item? p = ss; p is not null && c < Values.CountLimit; p = Hrg.Prev(p), c++)
        {
            if (Hrg.Equal(p, fs))
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>syl_out</c>: the syllables until the end of the phrase.</summary>
    public static Value? SylOut(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughtern.R:SylStructure.daughtern");
        int c = 0;
        for (Item? p = ss; p is not null && c < Values.CountLimit; p = Hrg.Next(p), c++)
        {
            if (Hrg.Equal(p, fs))
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>syl_break</c> (also registered as <c>old_syl_break</c>): the break after the syllable: "0" inside a word, else the break of its word.</summary>
    public static Value? SylBreak(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "SylStructure");
        if (ss is null)
        {
            return Values.String1;
        }

        if (Hrg.Next(ss) is not null)
        {
            return Values.String0;
        }

        Item? parent = Hrg.Parent(ss);
        return parent is null ? Values.String1 : WordBreak(parent);
    }

    /// <summary><c>syl_codasize</c>: 1 plus the consonants after the vowel (counting back from the end of the syllable).</summary>
    public static Value? SylCodasize(Item syl)
    {
        int c = 1;
        for (Item? d = Hrg.LastDaughter(Hrg.ItemAs(syl, "SylStructure")); d is not null; d = Hrg.Prev(d), c++)
        {
            if (Values.String(PhVc(d)) == "+")
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>syl_onsetsize</c>: the consonants before the vowel.</summary>
    public static Value? SylOnsetsize(Item syl)
    {
        int c = 0;
        for (Item? d = Hrg.Daughter(Hrg.ItemAs(syl, "SylStructure")); d is not null; d = Hrg.Next(d), c++)
        {
            if (Values.String(PhVc(d)) == "+")
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>asyl_in</c>: the accented syllables since the start of the phrase.</summary>
    public static Value? AsylIn(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughter.R:SylStructure.daughter");
        if (Hrg.Equal(ss, fs))
        {
            return Values.StringN(0);
        }

        int c = 0;
        for (Item? p = Hrg.Prev(ss); p is not null && c < Values.CountLimit; p = Hrg.Prev(p))
        {
            if (Values.Int(Accented(p!)) == 1)
            {
                c++;
            }

            if (Hrg.Equal(p, fs))
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>asyl_out</c>: the accented syllables until the end of the phrase.</summary>
    public static Value? AsylOut(Item syl)
    {
        Item? ss = Hrg.ItemAs(syl, "Syllable");
        Item? fs = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.daughtern.R:SylStructure.daughtern");
        if (Hrg.Equal(ss, fs))
        {
            return Values.StringN(0);
        }

        int c = 0;
        for (Item? p = Hrg.Next(ss); p is not null && c < Values.CountLimit; p = Hrg.Next(p))
        {
            if (Values.Int(Accented(p!)) == 1)
            {
                c++;
            }

            if (Hrg.Equal(p, fs))
            {
                break;
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>segment_duration</c>: the duration of the segment (end minus the end of the previous one, a float); "0" when the item is not a segment.</summary>
    public static Value? SegmentDuration(Item seg)
    {
        Item? s = Hrg.ItemAs(seg, "Segment");
        if (s is null)
        {
            return Values.String0;
        }

        if (Hrg.Prev(s) is null)
        {
            return Hrg.Feat(s, "end");
        }

        return new FloatValue((float)(Hrg.FeatFloat(s, "end") - Hrg.FeatFloat(Hrg.Prev(s), "end")));
    }

    private static StringValue SegCodaCtype(Item seg, string ctype)
    {
        Phoneset ps = Phoneset.OfItem(seg);
        for (Item? s = Hrg.LastDaughter(Hrg.Parent(Hrg.ItemAs(seg, "SylStructure"))); s is not null; s = Hrg.Prev(s))
        {
            if (ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "vc") == "+")
            {
                return Values.String0;
            }

            if (ctype == ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "ctype"))
            {
                return Values.String1;
            }
        }

        return Values.String0;
    }

    private static StringValue SegOnsetCtype(Item seg, string ctype)
    {
        Phoneset ps = Phoneset.OfItem(seg);
        for (Item? s = Hrg.Daughter(Hrg.Parent(Hrg.ItemAs(seg, "SylStructure"))); s is not null; s = Hrg.Next(s))
        {
            if (ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "vc") == "+")
            {
                return Values.String0;
            }

            if (ctype == ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "ctype"))
            {
                return Values.String1;
            }
        }

        return Values.String0;
    }

    private static StringValue SegCodaGlide(Item seg) =>
        ReferenceEquals(SegCodaCtype(seg, "r"), Values.String0) ? SegCodaCtype(seg, "l") : Values.String1;

    private static StringValue SegOnsetGlide(Item seg) =>
        ReferenceEquals(SegOnsetCtype(seg, "r"), Values.String0) ? SegOnsetCtype(seg, "l") : Values.String1;

    /// <summary><c>seg_onsetcoda</c>: "onset" when a vowel follows in the syllable, else "coda".</summary>
    public static Value? SegOnsetcoda(Item seg)
    {
        Phoneset ps = Phoneset.OfItem(seg);
        for (Item? s = Hrg.Next(Hrg.ItemAs(seg, "SylStructure")); s is not null; s = Hrg.Next(s))
        {
            if (ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "vc") == "+")
            {
                return Onset;
            }
        }

        return Coda;
    }

    /// <summary><c>pos_in_syl</c>: the place of the segment in its syllable (counting from 0).</summary>
    public static Value? PosInSyl(Item seg)
    {
        int c = -1;
        for (Item? s = Hrg.ItemAs(seg, "SylStructure"); s is not null; s = Hrg.Prev(s))
        {
            c++;
        }

        return Values.StringN(c);
    }

    /// <summary><c>position_type</c>: single, initial, mid or final in the word.</summary>
    public static Value? PositionType(Item syl)
    {
        Item? s = Hrg.ItemAs(syl, "SylStructure");
        if (s is null)
        {
            return Single;
        }

        if (Hrg.Next(s) is null)
        {
            return Hrg.Prev(s) is null ? Single : Final;
        }

        return Hrg.Prev(s) is null ? Initial : Mid;
    }

    /// <summary><c>sub_phrases</c>: the phrases before the phrase of the syllable (at most 19).</summary>
    public static Value? SubPhrases(Item syl)
    {
        int c = 0;
        for (Item? s = FeaturePath.PathToItem(syl, "R:SylStructure.parent.R:Phrase.parent.p"); s is not null && c < Values.CountLimit; s = Hrg.Prev(s), c++)
        {
        }

        return Values.StringN(c);
    }

    /// <summary><c>last_accent</c>: the syllables back to the last accented one.</summary>
    public static Value? LastAccent(Item syl)
    {
        int c = 0;
        for (Item? s = Hrg.ItemAs(syl, "Syllable"); s is not null && c < Values.CountLimit; s = Hrg.Prev(s), c++)
        {
            if (Values.Int(Accented(s)) != 0)
            {
                return Values.StringN(c);
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>next_accent</c>: the syllables to the next accented one.</summary>
    public static Value? NextAccent(Item syl)
    {
        Item? s = Hrg.ItemAs(syl, "Syllable");
        if (s is null)
        {
            return Values.StringN(0);
        }

        s = Hrg.Next(s);
        int c = 0;
        for (; s is not null && c < Values.CountLimit; s = Hrg.Next(s), c++)
        {
            if (Values.Int(Accented(s)) != 0)
            {
                return Values.StringN(c);
            }
        }

        return Values.StringN(c);
    }

    /// <summary><c>syl_final</c>: "1" for the last segment of a syllable.</summary>
    public static Value? SylFinal(Item seg)
    {
        Item? s = Hrg.ItemAs(seg, "SylStructure");
        return s is null || Hrg.Next(s) is null ? Values.String1 : Values.String0;
    }

    /// <summary><c>syl_initial</c>: "1" for the first segment of a syllable.</summary>
    public static Value? SylInitial(Item seg)
    {
        Item? s = Hrg.ItemAs(seg, "SylStructure");
        return s is null || Hrg.Prev(s) is null ? Values.String1 : Values.String0;
    }

    /// <summary><c>pos_in_word</c>: the place of the syllable in its word (from 0).</summary>
    public static Value? PosInWord(Item p0)
    {
        int i = 0;
        Item? p = Hrg.ItemAs(p0, "Syllable");
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.parent.daughter1"), "Syllable");
        for (; s is not null && !Hrg.Equal(p, s); s = Hrg.Next(s), i++)
        {
        }

        return Values.StringN(i);
    }

    /// <summary><c>syllable_duration</c>: the duration of the syllable (a float).</summary>
    public static Value? SyllableDuration(Item p) =>
        new FloatValue((float)(FeaturePath.FfeatureFloat(p, "R:SylStructure.daughtern.R:Segment.end")
            - FeaturePath.FfeatureFloat(p, "R:SylStructure.daughter1.R:Segment.p.end")));

    /// <summary><c>syl_vowel</c>: the name of the vowel of the syllable, or null (no value, so a path gives "0") when it has none.</summary>
    public static Value? SylVowel(Item p)
    {
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.daughter1"), "Segment");
        Item? ls = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.daughtern"), "Segment");
        for (; s is not null && !Hrg.Equal(s, ls); s = Hrg.Next(s))
        {
            if (Values.String(PhVc(s)) == "+")
            {
                return new StringValue(Hrg.Name(s));
            }
        }

        // C does not look at s here: with no segment at all it crashes
        if (s is null)
        {
            throw new FliteException("syl_vowel: the syllable has no segments");
        }

        return Values.String(PhVc(s)) == "+" ? new StringValue(Hrg.Name(s)) : null;
    }

    /// <summary><c>syl_numphones</c>: the number of segments of the syllable.</summary>
    public static Value? SylNumphones(Item p)
    {
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.daughter1"), "Segment");
        Item? ls = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.daughtern"), "Segment");
        int i = 1;
        for (; s is not null && !Hrg.Equal(s, ls); s = Hrg.Next(s))
        {
            i++;
        }

        return Values.StringN(i);
    }

    /// <summary><c>pos_in_phrase</c>: the place of the word in its phrase (from 0).</summary>
    public static Value? PosInPhrase(Item p0)
    {
        int i = 0;
        Item? p = Hrg.ItemAs(p0, "Word");
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.R:Phrase.parent.daughter1"), "Word");
        for (; s is not null && !Hrg.Equal(p, s); s = Hrg.Next(s), i++)
        {
        }

        return Values.StringN(i);
    }

    /// <summary><c>gpos</c>: the class of the word in the closed-class lists, or "content".</summary>
    public static Value? Gpos(Item word, IReadOnlyList<GposClass> classes)
    {
        string w = Hrg.FeatString(word, "name");
        foreach (GposClass c in classes)
        {
            if (c.Words.Contains(w, StringComparer.Ordinal))
            {
                return c.Name;
            }
        }

        return Content;
    }

    /// <summary><c>num_digits</c>: the length of the name of the token (an int); the name is a byte string, so this counts bytes (see ByteText).</summary>
    public static Value? NumDigits(Item token) => Values.IntN(Hrg.FeatString(token, "name").Length);

    /// <summary><c>month_range</c>: "1" when the token read as an int is 1 to 31, else "0".</summary>
    public static Value? MonthRange(Item token)
    {
        int v = Hrg.FeatInt(token, "name");
        return v is > 0 and < 32 ? Values.String1 : Values.String0;
    }

    /// <summary><c>token_pos_guess</c>: numeric, number, month, day, a, flight, to or _other_.</summary>
    public static Value? TokenPosGuess(Item token)
    {
        string dc = Values.Downcase(Hrg.FeatString(token, "name"));
        if (DigitsRx.IsMatch(dc))
        {
            return Numeric;
        }

        if (DoubleRx.IsMatch(dc))
        {
            return Number;
        }

        if (MonthNames.Contains(dc))
        {
            return Month;
        }

        if (DayNames.Contains(dc))
        {
            return Day;
        }

        return dc switch
        {
            "a" => A,
            "flight" => Flight,
            "to" => To,
            _ => Other,
        };
    }

    /// <summary><c>content_words_in</c>: the content words before this word in its phrase.</summary>
    public static Value? ContentWordsIn(Item p0)
    {
        int i = 0;
        Item? p = Hrg.ItemAs(p0, "Word");
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.R:Phrase.parent.daughter1"), "Word");
        for (; s is not null && !Hrg.Equal(p, s); s = Hrg.Next(s))
        {
            if (FeaturePath.FfeatureString(s, "gpos") == "content")
            {
                i++;
            }
        }

        return Values.StringN(i);
    }

    /// <summary><c>content_words_out</c>: the content words after this word in its phrase.</summary>
    public static Value? ContentWordsOut(Item p0)
    {
        int i = 0;
        Item? p = Hrg.ItemAs(p0, "Word");
        Item? s = Hrg.ItemAs(FeaturePath.PathToItem(p, "R:SylStructure.R:Phrase.parent.daughtern"), "Word");
        for (; s is not null && !Hrg.Equal(p, s); s = Hrg.Prev(s))
        {
            if (FeaturePath.FfeatureString(s, "gpos") == "content")
            {
                i++;
            }
        }

        return Values.StringN(i);
    }
}
