using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (tokentosegs, utt_synth_phones). Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The text analysis of explicit phones (<c>tokentosegs</c> in <c>cst_synth.c</c>, the module of <c>utt_synth_phones</c> that
/// <c>flite -p</c> runs instead of the text analysis): every token of the input is one phone, copied into the Segment relation.
/// A literal translation with the item semantics of <see cref="Hrg"/>.
/// </summary>
internal static class PhoneInput
{
    /// <summary>
    /// <c>tokentosegs</c>: one Word (named "phonestring"), its syllables and the segments, from the Token relation. A trailing 1 or 0 of a token sets the stress of the
    /// syllable and is removed from the phone; the token "-" ends the syllable and makes no phone (but, as in C, it still appends an empty item to the Segment relation).
    /// </summary>
    /// <param name="u">The utterance that holds the Token relation and the voice features (the phoneset).</param>
    public static void TokenToSegments(Utterance u)
    {
        Phoneset ps = FeatureSet.Val(u.Features, "phoneset") is PhonesetValue v ? v.Phoneset : throw new FliteException("VAL: tried to access phoneset in a val of another type");
        Relation seg = u.RelationCreate("Segment");
        Relation syl = u.RelationCreate("Syllable");
        Relation word = u.RelationCreate("Word");
        Relation sylStructure = u.RelationCreate("SylStructure");
        Item? sylItem = null, sssyl = null, wordItem = null, sylStructureItem = null;
        for (Item? t = u.GetRelation("Token").Head; t is not null; t = Hrg.Next(t))
        {
            Item segItem = seg.Append(null);
            string name = Hrg.FeatString(t, "name");
            if (wordItem is null)
            {
                wordItem = word.Append(null);
                Hrg.SetString(wordItem, "name", "phonestring");
                sylStructureItem = sylStructure.Append(wordItem);
            }

            if (sylItem is null)
            {
                sylItem = syl.Append(null);
                sssyl = Hrg.AddDaughter(sylStructureItem!, sylItem);
            }

            if (name.Length > 0 && name[^1] == '1')
            {
                Hrg.SetString(sssyl!, "stress", "1");
                name = name[..^1];
            }
            else if (name.Length > 0 && name[^1] == '0')
            {
                Hrg.SetString(sssyl!, "stress", "0");
                name = name[..^1];
            }

            if (string.Equals(name, "-", StringComparison.Ordinal))
            {
                sylItem = null;   // syllable break
            }
            else if (ps.PhoneId(name) == -1)
            {
                // C tests for -1, but phone_id answers 0 for an unknown phone, so this error never fires there: the phone is taken as it is
                throw new FliteException($"Phone `{Hrg.FeatString(t, "name")}' not in phoneset");
            }
            else
            {
                Hrg.AddDaughter(sssyl!, segItem);
                Hrg.SetString(segItem, "name", name);
            }
        }
    }
}
