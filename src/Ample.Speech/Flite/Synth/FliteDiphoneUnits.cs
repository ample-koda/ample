using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Synth;

// Port of Flite (CMU, commit 6c9f20dc), src/wavesynth/cst_diphone.c: get_diphone_units and the search of the diphone index. Translated to C# and modified: see FliteSynthesizer.cs.

/// <summary>What the diphone selection reads of one item of the Segment relation (Flite reads these features with <c>ffeature</c>).</summary>
/// <param name="Name">The phone name (<c>name</c>), for example <c>hh</c> or <c>pau</c>.</param>
/// <param name="End">The end time of the segment in seconds (<c>end</c>), a 32-bit float as the duration model made it.</param>
/// <param name="PhVc">The feature <c>ph_vc</c> of the segment: <c>+</c> for a vowel, <c>-</c> for a consonant.</param>
/// <param name="NextInSyllablePhVc">The feature <c>R:SylStructure.n.ph_vc</c>: <c>ph_vc</c> of the next segment in the same syllable, or <c>0</c> when there is none.</param>
internal readonly record struct FliteSegmentInput(string Name, float End, string PhVc, string NextInSyllablePhVc);

/// <summary>One item of the Unit relation: half of a diphone, with the frames of the voice it takes and where it ends in the output.</summary>
/// <param name="Name">The diphone name (<c>aa-ae</c>, or <c>t_-_r</c> for a consonant cluster).</param>
/// <param name="TargetEnd">The end of the half in output samples (<c>target_end</c>).</param>
/// <param name="UnitEntry">The index of the diphone in the index of the voice (<c>unit_entry</c>); 0 when the diphone is missing.</param>
/// <param name="UnitStart">The first frame of the voice (<c>unit_start</c>).</param>
/// <param name="UnitEnd">The frame after the last one (<c>unit_end</c>).</param>
internal readonly record struct FliteUnit(string Name, int TargetEnd, int UnitEntry, int UnitStart, int UnitEnd);

/// <summary>The selection of the diphones for a list of segments (<c>get_diphone_units</c>).</summary>
internal static class FliteDiphoneUnits
{
    /// <summary>Two units (the two halves of the diphone) for every pair of consecutive segments.</summary>
    /// <param name="voice">The voice data.</param>
    /// <param name="segments">The segments in order.</param>
    /// <param name="missing">Receives the name of every diphone that is not in the voice; Flite prints these to its error output and takes entry 0 in their place.</param>
    internal static List<FliteUnit> Select(FliteVoiceData voice, IReadOnlyList<FliteSegmentInput> segments, List<string>? missing = null)
    {
        List<FliteUnit> units = [];
        int sampleRate = voice.SampleRate;
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            FliteSegmentInput s0 = segments[i], s1 = segments[i + 1];
            int unitEntry = -1;
            string diphoneName = string.Empty;
            if (s0.PhVc == "-" && s0.NextInSyllablePhVc == "-")
            {
                // might have a consonant cluster diphone
                diphoneName = Cut(s0.Name) + "_-_" + Cut(s1.Name);
                unitEntry = voice.FindDiphone(diphoneName);
            }

            if (unitEntry == -1)
            {
                // no consonant cluster diphone
                diphoneName = Cut(s0.Name) + "-" + Cut(s1.Name);
                unitEntry = voice.FindDiphone(diphoneName);
            }

            if (unitEntry == -1)
            {
                missing?.Add(diphoneName);
                unitEntry = 0;
            }

            FliteDiphoneEntry entry = voice.Diphones[unitEntry];
            float end0 = s0.End, end1 = s1.End;

            // first half of the diphone: float times int is a float in C, and the cast to int cuts
            units.Add(new FliteUnit(diphoneName, (int)(end0 * sampleRate), unitEntry, entry.StartPm, entry.StartPm + entry.PbPm));

            // second half of the diphone: the sum is a float, the division by 2.0 and the product are doubles
            double middle = ((float)(end0 + end1) / 2.0) * sampleRate;
            units.Add(new FliteUnit(diphoneName, (int)middle, unitEntry, entry.StartPm + entry.PbPm, entry.StartPm + entry.PbPm + entry.EndPm));
        }

        return units;
    }

    /// <summary>The C format <c>%.10s</c>: at most the first 10 characters (phone names are ASCII).</summary>
    private static string Cut(string name) => name.Length > 10 ? name[..10] : name;
}
