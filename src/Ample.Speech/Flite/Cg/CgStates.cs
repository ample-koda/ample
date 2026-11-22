using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_cg.c. Translated to C# and modified.
// Copyright and licence: LICENSES/flite.txt, including the original authors.
internal static class CgStates
{
    /// <summary>Build the HMMstate and segstate relations from the existing segment relation.</summary>
    public static void Make(Utterance utterance, FliteCgVoiceData voice, CancellationToken cancellationToken)
    {
        if (voice.PhoneStates.Count == 0) throw new InvalidDataException("The clustergen voice has no phone states.");
        cancellationToken.ThrowIfCancellationRequested();
        Relation hmm = utterance.RelationCreate("HMMstate");
        Relation links = utterance.RelationCreate("segstate");
        for (Item? segment = utterance.GetRelation("Segment").Head; segment is not null; segment = Hrg.Next(segment))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Item parent = links.Append(segment);
            string name = Hrg.Name(segment);
            IReadOnlyList<string> states = voice.PhoneStates.FirstOrDefault(row => row.Count > 0 && row[0] == name)
                ?? voice.PhoneStates[0];
            for (int i = 1; i < states.Count; i++)
            {
                Item state = hmm.Append(null);
                Hrg.AddDaughter(parent, state);
                Hrg.SetString(state, "name", states[i]);
                Hrg.SetInt(state, "statepos", i);
            }
        }
    }
}
