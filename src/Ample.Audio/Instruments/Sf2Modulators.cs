namespace Ample.Audio.Instruments;

/// <summary>One SF2 modulator (a pmod or imod record, SF2 2.04 section 8.2).</summary>
/// <remarks><paramref name="LinkTarget"/>: for a modulator whose destination is another modulator (SF2 8.2.2, destination bit 15), that modulator's index in the zone's resolved list; else -1.</remarks>
internal readonly record struct Sf2Mod(ushort Source, ushort Destination, short Amount, ushort AmountSource, ushort Transform, int LinkTarget = -1);

/// <summary>
/// The MIDI controllers of a channel as SF2 modulators read them: the 128 controllers, channel pressure, and the pitch wheel with its range. A note reads them when it
/// starts and again whenever one changes while it sounds.
/// </summary>
internal sealed class Sf2Controllers
{
    /// <summary>The value of every controller (0..127). Volume (7) and expression (11) start at 127 and pan (10) at 64, as AMPLE plays a channel without controllers.</summary>
    public int[] Cc { get; } = new int[128];

    /// <summary>Channel pressure (0..127).</summary>
    public int ChannelPressure { get; set; }

    /// <summary>The pitch wheel (0..16383, 8192 is the middle).</summary>
    public int PitchWheel { get; set; } = 8192;

    /// <summary>The range of the pitch wheel in semitones (RPN 0, data entry MSB).</summary>
    public int PitchWheelSensitivity { get; set; } = 2;

    /// <summary>The range of the pitch wheel in cents (RPN 0: semitones from data entry MSB, cents from LSB); 200 until a song sets it.</summary>
    public int BendRangeCents { get; private set; } = 200;

    /// <summary>The polyphonic key pressure of the note that reads these controllers (0..127).</summary>
    public int PolyPressure { get; set; }

    /// <summary>The generator offsets a song set with SF2 NRPNs (SF2 9.6: NRPN MSB 120, LSB the generator, data entry the value minus 8192), per generator.</summary>
    public int[] NrpnOffsets { get; } = new int[61];

    // the units of one NRPN step per generator (SF2 9.6.3; the table of FluidSynth): 2 for pitch, filter and times, 4 for LFO frequencies, 0 where an NRPN may not reach
    private static readonly int[] NrpnScale = [1, 1, 1, 1, 1, 2, 2, 2, 2, 1, 2, 2, 1, 1, 0, 1, 1, 1, 0, 0, 0, 2, 4, 2, 4, 2, 2, 2, 2, 1, 2, 1, 1, 2, 2, 2, 2, 1, 2, 1, 1, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0];

    private int rpnMsb = 127, rpnLsb = 127, nrpnMsb = 127, nrpnLsb = 127, dataMsb, dataLsb;
    private bool nrpnSelected;

    public Sf2Controllers()
    {
        Cc[7] = 127;
        Cc[11] = 127;
        Cc[10] = 64;
    }

    /// <summary>
    /// Plays one control change: its value, and what it means for the registered and non-registered parameters (data entry 6 and 38 after 101/100 or 99/98) and
    /// for a reset of all controllers (121: the controllers and parameter numbers go back, volume and pan stay, as MIDI asks; the pitch-wheel range stays too).
    /// </summary>
    public void Control(int number, int value)
    {
        if (number is < 0 or > 127) return;
        Cc[number] = value;
        switch (number)
        {
            case 101: rpnMsb = value; nrpnSelected = false; break;
            case 100: rpnLsb = value; nrpnSelected = false; break;
            case 99: nrpnMsb = value; nrpnSelected = true; break;
            case 98: nrpnLsb = value; nrpnSelected = true; break;
            case 6: dataMsb = value; dataLsb = 0; DataEntry(); break;
            case 38: dataLsb = value; DataEntry(); break;
            case 121:
                int volume = Cc[7], pan = Cc[10];
                Array.Clear(Cc);
                Cc[7] = volume;
                Cc[10] = pan;
                Cc[11] = 127;
                ChannelPressure = 0;
                PitchWheel = 8192;
                rpnMsb = rpnLsb = nrpnMsb = nrpnLsb = 127;
                break;
        }
    }

    private void DataEntry()
    {
        if (!nrpnSelected)
        {
            if (rpnMsb == 0 && rpnLsb == 0) { PitchWheelSensitivity = dataMsb; BendRangeCents = (dataMsb * 100) + Math.Min(dataLsb, 99); }
        }
        else if (nrpnMsb == 120 && nrpnLsb < NrpnOffsets.Length)
        {
            NrpnOffsets[nrpnLsb] = (((dataMsb << 7) | dataLsb) - 8192) * NrpnScale[nrpnLsb];   // generators 0..60 are addressed directly (the 100..102 escapes of SF2 9.6 reach numbers above them)
        }
    }
}

/// <summary>
/// SF2 modulators in integer arithmetic. At note-on, without controllers, only the sources known at that moment are played (no controller, velocity, key); with the
/// controllers of the channel every source is played, so a modulator from the modulation wheel or volume moves the note while it sounds. The renderer still plays pan
/// (controller 10), the pitch wheel and brightness (controller 74) itself.
/// </summary>
internal static class Sf2Modulators
{
    /// <summary>
    /// The default modulators that apply unless a zone has one with the same source, destination and amount source (SF2 8.4): note-on velocity to attenuation
    /// (960 cB, concave); channel pressure and the modulation wheel (controller 1) to the vibrato LFO's pitch (50 cents); volume (7) and expression (11) to attenuation
    /// (960 cB, concave, falling); the reverb (91) and chorus (93) sends (20 %). Like FluidSynth (the player the MuseScore soundfonts are made with), the default
    /// velocity to filter cutoff (8.4.2) is not applied: its two versions in the specification disagree, and a soundfont that wants it says so with a modulator of its
    /// own. Pan (10) to pan (500, bipolar) and the pitch wheel to the pitch (12700 cents times the wheel's range in semitones / 128, so 2 semitones by default) are
    /// modulators too, so a soundfont can change them. Velocity stays first: a zone made by hand uses only that one.
    /// </summary>
    public static readonly Sf2Mod[] Defaults =
    [
        new(0x0502, 48, 960, 0, 0),
        new(0x000D, 6, 50, 0, 0),
        new(0x0081, 6, 50, 0, 0),
        new(0x0587, 48, 960, 0, 0),
        new(0x058B, 48, 960, 0, 0),
        new(0x00DB, 16, 200, 0, 0),
        new(0x00DD, 15, 200, 0, 0),
        new(0x028A, 17, 500, 0, 0),
        new(0x020E, 59, 12700, 0x0010, 0),
    ];

    /// <summary>The value of volume or expression (a falling concave source of 960 cB, the default modulator) whose attenuation is closest to <paramref name="centibels"/>.</summary>
    public static int ControllerForAttenuation(int centibels)
    {
        int best = 127;
        long bestError = long.MaxValue;
        for (int value = 127; value >= 0; value--)
        {
            long error = Math.Abs(((long)Concave[127 - value] * 960 / 32768) - centibels);
            if (error < bestError) { bestError = error; best = value; }
        }

        return best;
    }

    /// <summary>Whether a source is the output of other modulators (the link source, SF2 8.2.1: general controller 127).</summary>
    public static bool IsLink(ushort source) => (source & 0xFF) == 0x7F;

    // the concave and convex curves of SF2 8.2.4 in Q15 (index 0..127), computed once: concave(i) = -(400/960) log10((127 - i) / 127), convex(i) = 1 + (400/960) log10(i / 127)
    private static readonly int[] Concave = [0, 47, 94, 142, 190, 238, 287, 336, 386, 436, 486, 537, 589, 640, 693, 745, 798, 852, 906, 961, 1016, 1072, 1128, 1185, 1242, 1300, 1358, 1417, 1477, 1537, 1598, 1659, 1721, 1784, 1848, 1912, 1976, 2042, 2108, 2175, 2243, 2312, 2381, 2451, 2522, 2594, 2667, 2740, 2815, 2891, 2967, 3045, 3123, 3203, 3283, 3365, 3448, 3532, 3618, 3704, 3792, 3881, 3972, 4064, 4157, 4252, 4348, 4446, 4546, 4647, 4750, 4855, 4962, 5071, 5182, 5295, 5410, 5527, 5647, 5769, 5894, 6022, 6152, 6285, 6422, 6561, 6704, 6850, 7001, 7155, 7313, 7475, 7642, 7814, 7991, 8174, 8362, 8556, 8757, 8965, 9181, 9405, 9637, 9879, 10132, 10395, 10671, 10961, 11265, 11585, 11924, 12284, 12666, 13075, 13515, 13990, 14505, 15071, 15695, 16394, 17186, 18100, 19181, 20504, 22210, 24614, 28724, 32768];
    private static readonly int[] Convex = [0, 4044, 8154, 10558, 12264, 13587, 14668, 15582, 16374, 17073, 17697, 18263, 18778, 19253, 19693, 20102, 20484, 20844, 21183, 21503, 21807, 22097, 22373, 22636, 22889, 23131, 23363, 23587, 23803, 24011, 24212, 24406, 24594, 24777, 24954, 25126, 25293, 25455, 25613, 25767, 25918, 26064, 26207, 26346, 26483, 26616, 26746, 26874, 26999, 27121, 27241, 27358, 27473, 27586, 27697, 27806, 27913, 28018, 28121, 28222, 28322, 28420, 28516, 28611, 28704, 28796, 28887, 28976, 29064, 29150, 29236, 29320, 29403, 29485, 29565, 29645, 29723, 29801, 29877, 29953, 30028, 30101, 30174, 30246, 30317, 30387, 30456, 30525, 30593, 30660, 30726, 30792, 30856, 30920, 30984, 31047, 31109, 31170, 31231, 31291, 31351, 31410, 31468, 31526, 31583, 31640, 31696, 31752, 31807, 31862, 31916, 31970, 32023, 32075, 32128, 32179, 32231, 32282, 32332, 32382, 32432, 32481, 32530, 32578, 32626, 32674, 32721, 32768];

    /// <summary>Two modulators are the same one when source, destination and amount source agree (a later one replaces an earlier one, SF2 9.5).</summary>
    public static bool Same(Sf2Mod a, Sf2Mod b) => a.Source == b.Source && a.Destination == b.Destination && a.AmountSource == b.AmountSource;

    /// <summary>The modulators of one level: each later layer replaces the same modulators of the earlier layers and adds the others (defaults, global zone, local zone).</summary>
    public static List<Sf2Mod> Merge(params IEnumerable<Sf2Mod>[] layers)
    {
        List<Sf2Mod> list = [];
        foreach (IEnumerable<Sf2Mod> layer in layers)
        {
            foreach (Sf2Mod mod in layer)
            {
                int at = list.FindIndex(existing => Same(existing, mod));
                if (at >= 0) list[at] = mod;
                else list.Add(mod);
            }
        }

        return list;
    }

    /// <summary>
    /// Whether a source is known when the note starts: no controller, note-on velocity, key number, or MIDI controller 2 (breath). The expressive presets of
    /// MuseScore General (banks 17 and 18) take their dynamic from controller 2, which MuseScore sets from the dynamic of the note; here it follows the note's velocity.
    /// </summary>
    public static bool AtNoteOn(ushort source) => (source & 0x80) == 0 ? (source & 0x7F) is 0 or 2 or 3 : (source & 0x7F) == 2;

    /// <summary>
    /// The value a modulator adds to its destination for a note (in the units of that generator): amount x source x amount source, then the transform. Without
    /// <paramref name="controllers"/> a modulator whose sources are not known at note-on adds nothing.
    /// </summary>
    public static long Value(Sf2Mod mod, int key, int velocity, Sf2Controllers? controllers = null, int link = 0)
    {
        if (controllers is null && ((!AtNoteOn(mod.Source) && !IsLink(mod.Source)) || !AtNoteOn(mod.AmountSource))) return 0;
        long value = (long)mod.Amount * Source(mod.Source, key, velocity, controllers, link) / 32768 * Source(mod.AmountSource, key, velocity, controllers, link) / 32768;
        return mod.Transform == 2 ? Math.Abs(value) : value;
    }

    /// <summary>
    /// A source in Q15 (0..32768 unipolar, -32768..32768 bipolar) after its direction, polarity and curve (SF2 8.2.1). No controller is 1. A source that is not known
    /// (a controller without <paramref name="controllers"/>, polyphonic pressure) reads 0.
    /// </summary>
    public static int Source(ushort source, int key, int velocity, Sf2Controllers? controllers = null, int link = 0)
    {
        int index = source & 0x7F;
        bool midiController = (source & 0x80) != 0;
        if (index == 0 && !midiController) return 32768;
        if (IsLink(source)) return Math.Clamp(link, -32768, 32768);   // the summed output of the modulators linked to this one, as a value of -1..1 (Q15)
        if (!midiController && index == 14 && controllers is not null && source >> 10 == 0)
        {
            // the pitch wheel, linear: all 14 bits (a bend moves in steps of a cent, not of 3)
            int wheel = (source & 0x100) != 0 ? 16383 - controllers.PitchWheel : controllers.PitchWheel;
            return (source & 0x200) != 0 ? Math.Clamp((wheel - 8192) * 32768 / 8192, -32768, 32768) : wheel * 32768 / 16384;
        }

        // velocity, or controller 2 that follows it (see AtNoteOn); the key; then the controllers of the channel: channel pressure (13), the pitch wheel (14, its top
        // seven bits), the range of the pitch wheel (16, in semitones), and the MIDI controllers
        int raw = Math.Clamp(index == 2 ? velocity
            : !midiController && index == 3 ? key
            : controllers is null ? 0
            : midiController ? controllers.Cc[index]
            : index switch { 10 => controllers.PolyPressure, 13 => controllers.ChannelPressure, 14 => controllers.PitchWheel >> 7, 16 => controllers.PitchWheelSensitivity, _ => 0 }, 0, 127);
        bool negative = (source & 0x100) != 0, bipolar = (source & 0x200) != 0;
        int type = source >> 10;
        if (!bipolar) return Curve(type, negative ? 127 - raw : raw, negative);
        // bipolar: -1 at one end, 0 in the middle, 1 at the other; the curve on each half, mirrored
        int signedRaw = negative ? 127 - raw : raw;
        if (type == 3) return signedRaw >= 64 ? 32768 : -32768;
        if (type == 0) return (signedRaw * 2 - 128) * 32768 / 128;
        int half = Math.Abs(signedRaw - 64) * 2;
        int shaped = Curve(type, Math.Min(127, half), false);
        return signedRaw >= 64 ? shaped : -shaped;
    }

    // linear is raw / 128 (1 - raw / 128 for a negative source, as FluidSynth); the curves take the index already turned for a negative source
    private static int Curve(int type, int index, bool negative) => type switch
    {
        1 => Concave[index],
        2 => Convex[index],
        3 => index >= 64 ? 32768 : 0,
        _ => negative ? 32768 - ((127 - index) * 32768 / 128) : index * 32768 / 128,
    };
}
