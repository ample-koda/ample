namespace Ample.Audio.Midi;

/// <summary>A matched MIDI note with its effective end tick after sustain controls.</summary>
/// <param name="StartTick">The note-on tick.</param>
/// <param name="EndTick">The note-off tick, extended by sustain when applicable.</param>
/// <param name="Pitch">MIDI key number.</param>
/// <param name="Velocity">Note-on velocity.</param>
/// <param name="Channel">Zero-based MIDI channel.</param>
/// <param name="Bank">Resolved bank at note-on.</param>
/// <param name="Program">Resolved program at note-on.</param>
public sealed record MidiNoteSpan(long StartTick, long EndTick, int Pitch, int Velocity, int Channel, int Bank, int Program);

/// <summary>Paired note events and bounded, content-free diagnostics.</summary>
/// <param name="Notes">Matched note spans.</param>
/// <param name="Warnings">Pairing issues such as zero-length or unmatched note events.</param>
public sealed record MidiNotePairing(IReadOnlyList<MidiNoteSpan> Notes, IReadOnlyList<string> Warnings);

/// <summary>Pairs SMF note events and applies MIDI sustain and note-reset controls.</summary>
public static class MidiNotePairer
{
    /// <summary>Pairs one track's events in order, retaining the program and bank selected at each note-on.</summary>
    /// <param name="track">The track to process.</param>
    /// <param name="trackIndex">Zero-based track index used in diagnostics.</param>
    /// <returns>Matched notes and diagnostics.</returns>
    public static MidiNotePairing Pair(SmfTrack track, int trackIndex)
    {
        Dictionary<(int Channel, int Pitch), Queue<MidiNoteSpan>> active = [];
        Dictionary<int, List<MidiNoteSpan>> sustained = [];
        Dictionary<int, int> bankMsb = [], bankLsb = [], programs = [];
        HashSet<int> pedalDown = [];
        List<MidiNoteSpan> notes = [];
        List<string> warnings = [];
        long lastTick = 0;
        bool zeroLength = false, unmatchedOff = false;

        void Close(MidiNoteSpan note, long endTick)
        {
            if (endTick > note.StartTick) notes.Add(note with { EndTick = endTick });
            else zeroLength = true;
        }

        void ReleaseSustained(int channel, long tick)
        {
            if (!sustained.Remove(channel, out List<MidiNoteSpan>? waiting)) return;
            foreach (MidiNoteSpan note in waiting) Close(note, tick);
        }

        void NoteOff(int channel, int pitch, long tick)
        {
            if (!active.TryGetValue((channel, pitch), out Queue<MidiNoteSpan>? starts) || starts.Count == 0)
            {
                unmatchedOff = true;
                return;
            }
            MidiNoteSpan note = starts.Dequeue();
            if (pedalDown.Contains(channel))
            {
                if (!sustained.TryGetValue(channel, out List<MidiNoteSpan>? waiting)) sustained[channel] = waiting = [];
                waiting.Add(note);
            }
            else Close(note, tick);
        }

        foreach (SmfEvent item in track.Events)
        {
            lastTick = Math.Max(lastTick, item.Tick);
            if (item.Kind == SmfEventKind.NoteOn)
            {
                int bank = bankMsb.GetValueOrDefault(item.Channel) * 128 + bankLsb.GetValueOrDefault(item.Channel);
                if (item.Channel == 9 && bank == 0) bank = 128;
                var key = (item.Channel, item.Data1);
                if (!active.TryGetValue(key, out Queue<MidiNoteSpan>? starts)) active[key] = starts = new();
                starts.Enqueue(new MidiNoteSpan(item.Tick, item.Tick, item.Data1, item.Data2, item.Channel, bank, programs.GetValueOrDefault(item.Channel)));
            }
            else if (item.Kind == SmfEventKind.NoteOff) NoteOff(item.Channel, item.Data1, item.Tick);
            else if (item.Kind == SmfEventKind.ProgramChange) programs[item.Channel] = item.Data1;
            else if (item.Kind == SmfEventKind.ControlChange)
            {
                if (item.Data1 == 0) bankMsb[item.Channel] = item.Data2;
                else if (item.Data1 == 32) bankLsb[item.Channel] = item.Data2;
                else if (item.Data1 == 64)
                {
                    if (item.Data2 >= 64) pedalDown.Add(item.Channel);
                    else { pedalDown.Remove(item.Channel); ReleaseSustained(item.Channel, item.Tick); }
                }
                else if (item.Data1 == 121) { pedalDown.Remove(item.Channel); ReleaseSustained(item.Channel, item.Tick); }
                else if (item.Data1 == 120 || item.Data1 == 123)
                {
                    foreach (var pair in active.Where(p => p.Key.Channel == item.Channel).ToArray())
                    {
                        while (pair.Value.Count > 0) NoteOff(item.Channel, pair.Key.Pitch, item.Tick);
                    }
                    if (item.Data1 == 120) ReleaseSustained(item.Channel, item.Tick);
                }
            }
        }

        if (zeroLength) warnings.Add($"track {trackIndex} has zero-length notes that cannot be represented by editable clips");
        if (unmatchedOff) warnings.Add($"track {trackIndex} has unmatched note-off events");
        if (active.Values.Any(q => q.Count > 0)) warnings.Add($"track {trackIndex} has unmatched note-on events");
        foreach (int channel in sustained.Keys.ToArray()) ReleaseSustained(channel, lastTick);
        return new MidiNotePairing([.. notes.OrderBy(n => n.StartTick).ThenBy(n => n.Pitch)], [.. warnings.Distinct(StringComparer.Ordinal)]);
    }
}
