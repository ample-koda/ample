namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU), src/synth/cst_phoneset.c and include/cst_phoneset.h. Translated to C# and modified: see Value.cs.

/// <summary>
/// A phoneset (<c>cst_phoneset</c>): for each phone a row of feature values (vc, vlng, vheight, ...), as indexes into a table of shared values.
/// A loader fills the arrays (the US set has 50 phones and 8 features).
/// </summary>
/// <param name="Name">The name of the set ("us").</param>
/// <param name="FeatNames">The feature names.</param>
/// <param name="FeatVals">The shared values (strings such as "+", "-", "0", "s").</param>
/// <param name="PhoneNames">The phone names, in the order of <paramref name="FvTable"/>.</param>
/// <param name="Silence">The name of the silence phone ("pau").</param>
/// <param name="FvTable">For each phone, for each feature, the index into <paramref name="FeatVals"/>.</param>
internal sealed record Phoneset(string Name, string[] FeatNames, Value[] FeatVals, string[] PhoneNames, string Silence, int[][] FvTable)
{
    /// <summary><c>num_phones</c>.</summary>
    public int NumPhones => PhoneNames.Length;

    /// <summary><c>phone_id</c>: the index of the phone; an unknown phone gives 0 (the first phone), as in C.</summary>
    public int PhoneId(string phoneName)
    {
        for (int i = 0; i < NumPhones; i++)
        {
            if (string.Equals(PhoneNames[i], phoneName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary><c>phone_feat_id</c>: the index of the feature; an unknown feature gives 0, as in C.</summary>
    public int PhoneFeatId(string featName)
    {
        for (int i = 0; i < FeatNames.Length; i++)
        {
            if (string.Equals(FeatNames[i], featName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary><c>phone_feature</c>: the value of a feature of a phone.</summary>
    public Value PhoneFeature(string phoneName, string featName) => FeatVals[FvTable[PhoneId(phoneName)][PhoneFeatId(featName)]];

    /// <summary><c>phone_feature_string</c>: the value as a string (an error when it is not one).</summary>
    public string PhoneFeatureString(string phoneName, string featName) => Values.String(PhoneFeature(phoneName, featName));

    /// <summary><c>item_phoneset</c>: the phoneset in the feature "phoneset" of the utterance of the item.</summary>
    public static Phoneset OfItem(Item p) =>
        FeatureSet.Val(Hrg.Utt(p)!.Features, "phoneset") is PhonesetValue v ? v.Phoneset : throw new FliteException("VAL: tried to access phoneset in a val of another type");
}
