using System.Text;
using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (default_lexical_insertion). Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The step of the synthesis that turns the Word relation into syllables and segments (<c>default_lexical_insertion</c> in <c>cst_synth.c</c>;
/// the pauses are added by <c>FliteProsody.PauseInsertion</c>),
/// with the syllable rules of the CMU lexicon. A literal translation with the item semantics of <see cref="Hrg"/>:
/// the word items are shared into SylStructure, the syllables and segments are new items that are shared
/// into SylStructure as daughters.
/// </summary>
internal static class LexicalInsertion
{
    /// <summary><c>default_lexical_insertion</c>: for every word, look the phones up and build the Syllable, SylStructure and Segment relations.</summary>
    /// <param name="utterance">The utterance with the Word relation.</param>
    /// <param name="lexicon">The lexicon of the voice.</param>
    /// <param name="user">The words of the block that have their own phones (the run-time addenda of Flite), or null.</param>
    public static void Run(Utterance utterance, FliteLexicon lexicon, FliteUserLexicon? user = null)
    {
        Relation syllables = utterance.RelationCreate("Syllable");
        Relation sylStructure = utterance.RelationCreate("SylStructure");
        Relation segments = utterance.RelationCreate("Segment");
        string stress = "0";
        for (Item? word = utterance.GetRelation("Word").Head; word is not null; word = Hrg.Next(word))
        {
            Item sylWord = sylStructure.Append(word);
            string pos = FeaturePath.FfeatureString(word, "pos");
            string[] phones = PhonesOf(word, pos, lexicon, user);
            Item? sylItem = null;
            Item? sylInStructure = null;
            for (int p = 0; p < phones.Length; p++)
            {
                if (sylItem is null)
                {
                    sylItem = syllables.Append(null);
                    sylInStructure = Hrg.AddDaughter(sylWord, sylItem);
                    stress = "0";
                }

                Item segment = segments.Append(null);
                string name = phones[p];
                if (name.Length > 0 && name[^1] == '1')
                {
                    stress = "1";
                    name = name[..^1];
                }
                else if (name.Length > 0 && name[^1] == '0')
                {
                    stress = "0";
                    name = name[..^1];
                }

                Hrg.SetString(segment, "name", name);
                Item segmentInSyllable = Hrg.AddDaughter(sylInStructure!, segment)!;
                if (CmuSyllables.BoundaryMaximalOnset(segmentInSyllable, phones.AsSpan(p + 1)))
                {
                    sylItem = null;
                    if (sylInStructure is not null)
                    {
                        Hrg.SetString(sylInStructure, "stress", stress);
                    }
                }
            }
        }
    }

    private static string[] PhonesOf(Item word, string pos, FliteLexicon lexicon, FliteUserLexicon? user)
    {
        // an explicit pronunciation of the token (a feature "phones", set by markup) wins; the second word of such a token gets none
        Item? token = Hrg.Parent(Hrg.ItemAs(word, "Token"));
        if (Hrg.FeatPresent(token, "phones"))
        {
            Value? given = Hrg.Feat(token, "phones");
            if (given is StringValue text)
            {
                if (string.Equals(text.Text, FeaturePath.FfeatureString(word, "p.R:Token.parent.phones"), StringComparison.Ordinal))
                {
                    return [];
                }

                return text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }

            return ListOfStrings(given);
        }

        string name = Hrg.FeatString(word, "name");

        // the run-time addenda: val_assoc_string(name, lex_addenda), by the name of the word and not by its part of speech
        if (user is not null && user.TryGet(name, out string[] own))
        {
            return own;
        }

        return lexicon.Lookup(Encoding.UTF8.GetBytes(name), pos.Length > 0 ? pos[0] : '0');
    }

    private static string[] ListOfStrings(Value? list)
    {
        List<string> items = [];
        for (Value? v = list; v is ConsValue cons; v = cons.Cdr)
        {
            items.Add(Values.String(cons.Car));
        }

        return [.. items];
    }
}
