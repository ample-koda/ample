using System.Globalization;

namespace Ample.Language;

/// <summary>One polyphonic note in the structured MIDI clip parameter.</summary>
/// <param name="Bar">One-based bar.</param>
/// <param name="BeatMicrobeats">Start beat within the bar, in millionths of a quarter note (display compatibility).</param>
/// <param name="LengthMicrobeats">Duration in millionths of a quarter note (display compatibility).</param>
/// <param name="Pitch">MIDI pitch from 0 through 127.</param>
/// <param name="Velocity">MIDI velocity from 1 through 127.</param>
/// <param name="TicksPerQuarter">The exact tick scale in the source clip.</param>
/// <param name="BeatTicks">Exact start tick within the bar.</param>
/// <param name="LengthTicks">Exact duration in ticks.</param>
/// <param name="Channel">Optional source MIDI channel, or -1 for unscoped notes.</param>
/// <param name="Bank">Optional source instrument bank, or -1 for unscoped notes.</param>
/// <param name="Program">Optional source instrument program, or -1 for unscoped notes.</param>
/// <param name="TieId">Optional identity shared by bar-split segments of one source note, or -1 when absent.</param>
/// <param name="Pan">Stereo position of this note's voice, -100 (left) through 100 (right); 0 when not written (PR06).</param>
/// <param name="Fine">Fine pitch offset of this note only, in cents, -100 through 100; 0 when not written (PR06).</param>
/// <param name="Rel">Release time of this note in milliseconds, 0 through 5000, or -1 for the instrument's own default (PR06).</param>
/// <param name="Cut">Low-pass cutoff of this note, 0 through 100 percent; 100 (no filter) when not written (PR06).</param>
/// <param name="Slide">Whether this is a slide/porta note (PR08): 1 glides the previous note's voice to this pitch, 0 (default) does not.</param>
/// <param name="Port">Portamento time into this note from the previous note of the clip, in milliseconds, 0 through 2000 (PR06).</param>
/// <param name="Bend">Pitch bend of this note in cents, -2400 through 2400: a glide from its own pitch at the start to that much higher (or lower) at its end; 0 when not written.</param>
public sealed record StructuredMidiNote(int Bar, int BeatMicrobeats, int LengthMicrobeats, int Pitch, int Velocity, int TicksPerQuarter, long BeatTicks, long LengthTicks, int Channel, int Bank, int Program, int TieId,
    int Pan = 0, int Fine = 0, int Rel = -1, int Cut = 100, int Slide = 0, int Port = 0, int Bend = 0);

/// <summary>Parses and validates the space-separated structured MIDI note syntax.</summary>
public static class StructuredMidiNotes
{
    /// <summary>Fixed integer units per quarter-note beat.</summary>
    public const int MicrobeatsPerBeat = 1_000_000;
    /// <summary>Maximum source characters accepted for one parameter.</summary>
    public const int MaximumTextLength = 4_000_000;
    /// <summary>Maximum number of note entries accepted for one parameter.</summary>
    public const int MaximumNotes = 100_000;

    /// <summary>Parses five-field notes or notes with MIDI channel, bank, program and optional tie metadata.</summary>
    /// <param name="value">Space-separated note entries.</param>
    /// <param name="notes">Parsed notes, empty when the parameter is empty or invalid.</param>
    /// <returns>True when all entries are valid.</returns>
    public static bool TryParse(string value, out IReadOnlyList<StructuredMidiNote> notes)
        => TryParse(value, MicrobeatsPerBeat, out notes);

    /// <summary>Parses the legacy decimal form or exact integer ticks when the scale differs from the legacy million-unit scale.</summary>
    public static bool TryParse(string value, int ticksPerQuarter, out IReadOnlyList<StructuredMidiNote> notes, double beatsPerBar = 4.0)
    {
        notes = [];
        long barMicrobeats = (long)Math.Round(beatsPerBar * MicrobeatsPerBeat), barTicks = (long)Math.Round(beatsPerBar * ticksPerQuarter);   // one bar of the song's meter: 4 beats in 4/4
        value = value.Trim().Trim('"');
        if (value.Length > MaximumTextLength || ticksPerQuarter is < 1 or > 1_000_000) return false;
        string[] entries = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > MaximumNotes) return false;
        List<StructuredMidiNote> parsed = new(entries.Length);
        foreach (string entry in entries)
        {
            int semicolon = entry.IndexOf(';');
            string basePart = semicolon < 0 ? entry : entry[..semicolon];
            string[] fields = basePart.Split(':');
            if (fields.Length is not (5 or 8 or 9) || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int bar) || bar is < 1 or > 1_000_000 ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int pitch) || pitch is < 0 or > 127 ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int velocity) || velocity is < 1 or > 127)
                return false;
            int channel = -1, bank = -1, program = -1;
            if (fields.Length >= 8 && (!int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out channel) || channel is < 0 or > 15 ||
                !int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out bank) || bank is < 0 or > 16383 ||
                !int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out program) || program is < 0 or > 127)) return false;
            int tieId = -1;
            if (fields.Length == 9 && (!int.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out tieId) || tieId is < 0 or > 2_000_000)) return false;
            int beat, length;
            long beatTicks, lengthTicks;
            if (ticksPerQuarter == MicrobeatsPerBeat)
            {
                if (!TryMicrobeats(fields[1], out beat) || beat >= barMicrobeats ||
                    !TryMicrobeats(fields[2], out length) || length < 1 || beat > barMicrobeats - length) return false;
                beatTicks = beat;
                lengthTicks = length;
            }
            else
            {
                if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out beatTicks) || beatTicks < 0 || beatTicks >= barTicks ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out lengthTicks) || lengthTicks < 1 || beatTicks > barTicks - lengthTicks) return false;
                beat = checked((int)(beatTicks * MicrobeatsPerBeat / ticksPerQuarter));
                length = checked((int)(lengthTicks * MicrobeatsPerBeat / ticksPerQuarter));
            }
            int pan = 0, fine = 0, rel = -1, cut = 100, slide = 0, port = 0, bend = 0;
            if (semicolon >= 0 && !TryExtensions(entry[(semicolon + 1)..], ref pan, ref fine, ref rel, ref cut, ref slide, ref port, ref bend, ref tieId)) return false;
            parsed.Add(new StructuredMidiNote(bar, beat, length, pitch, velocity, ticksPerQuarter, beatTicks, lengthTicks, channel, bank, program, tieId, pan, fine, rel, cut, slide, port, bend));
        }
        notes = parsed;
        return true;
    }

    /// <summary>Reads the `;key=value` extension pairs after a note entry (PR06). A known key with an out-of-range or non-integer value fails the whole
    /// entry (never repaired silently); an unknown key is accepted and ignored here (the JS lossless model is what keeps it byte-exact in the source text).</summary>
    private static bool TryExtensions(string text, ref int pan, ref int fine, ref int rel, ref int cut, ref int slide, ref int port, ref int bend, ref int tieId)
    {
        foreach (string pair in text.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) return false;
            string key = pair[..eq];
            bool isKnown = key is "pan" or "fine" or "rel" or "cut" or "slide" or "port" or "bend" or "tie";
            if (!int.TryParse(pair[(eq + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            {
                if (isKnown) return false;
                continue;
            }
            switch (key)
            {
                case "tie": if (value is < 0 or > 2_000_000 || tieId >= 0) return false; tieId = value; break;
                case "pan": if (value is < -100 or > 100) return false; pan = value; break;
                case "fine": if (value is < -100 or > 100) return false; fine = value; break;
                case "rel": if (value is < 0 or > 5000) return false; rel = value; break;
                case "cut": if (value is < 0 or > 100) return false; cut = value; break;
                case "slide": if (value is < 0 or > 1) return false; slide = value; break;
                case "port": if (value is < 0 or > 2000) return false; port = value; break;
                case "bend": if (value is < -2400 or > 2400) return false; bend = value; break;
                default: break;   // unknown key: a valid integer value, just not one the engine reads
            }
        }
        return true;
    }

    private static bool TryMicrobeats(string value, out int microbeats)
    {
        microbeats = 0;
        int dot = value.IndexOf('.');
        string wholeText = dot < 0 ? value : value[..dot];
        string fractionText = dot < 0 ? string.Empty : value[(dot + 1)..];
        if (wholeText.Length == 0 || wholeText.Length > 1 && wholeText[0] == '0' || (dot >= 0 && (fractionText.Length == 0 || fractionText.Length > 6)) ||
            !int.TryParse(wholeText, NumberStyles.None, CultureInfo.InvariantCulture, out int whole))
            return false;
        int fraction = 0;
        foreach (char digit in fractionText)
        {
            if (digit is < '0' or > '9') return false;
            fraction = (fraction * 10) + (digit - '0');
        }
        for (int i = fractionText.Length; i < 6; i++) fraction *= 10;
        if (whole > 4 || (whole == 4 && fraction != 0)) return false;
        microbeats = (whole * MicrobeatsPerBeat) + fraction;
        return true;
    }
}
