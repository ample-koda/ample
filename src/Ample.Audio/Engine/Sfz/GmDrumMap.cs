namespace Ample.Audio.Engine.Sfz;

/// <summary>
/// SFZ05: maps a MIDI key to the voice name an AMPLE <c>drums</c> block pattern would use (<c>kick</c>, <c>snare</c>,
/// ...), using the standard General MIDI percussion key map (a real, documented standard — not guessed) since vanilla
/// SFZ has no opcode that names a region's voice directly. Several related GM keys collapse onto one AMPLE voice name
/// (every tom becomes <c>tom</c>, every crash-type cymbal becomes <c>crash</c>) — a deliberate simplification, see
/// <c>docs/samples.md</c> "SFZ sets". A key this map does not know becomes <c>key&lt;N&gt;</c> by the caller, so it is
/// still addressable in a drums pattern even without a friendly name.
/// </summary>
internal static class GmDrumMap
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [35] = "kick",
        [36] = "kick",
        [37] = "rim",
        [38] = "snare",
        [40] = "snare",
        [39] = "clap",
        [41] = "tom",
        [43] = "tom",
        [45] = "tom",
        [47] = "tom",
        [48] = "tom",
        [50] = "tom",
        [42] = "hat",
        [44] = "hat",
        [46] = "hat-open",
        [49] = "crash",
        [52] = "crash",
        [55] = "crash",
        [57] = "crash",
        [51] = "ride",
        [53] = "ride",
        [59] = "ride",
        [54] = "tambourine",
        [56] = "cowbell",
        [58] = "perc",
    };

    /// <summary>The AMPLE voice name of a General MIDI percussion key, or null when the key is not a standard one.</summary>
    public static string? NameOf(int midiKey) => Names.GetValueOrDefault(midiKey);
}
