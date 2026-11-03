namespace Ample.Audio.Midi;

/// <summary>Supported Standard MIDI File event categories.</summary>
public enum SmfEventKind
{
    /// <summary>Note-off event.</summary>
    NoteOff,
    /// <summary>Note-on event.</summary>
    NoteOn,
    /// <summary>Polyphonic key pressure.</summary>
    PolyphonicPressure,
    /// <summary>Control change.</summary>
    ControlChange,
    /// <summary>Program change.</summary>
    ProgramChange,
    /// <summary>Channel pressure.</summary>
    ChannelPressure,
    /// <summary>Pitch wheel change with both seven-bit bytes retained.</summary>
    PitchBend,
    /// <summary>Meta event such as tempo, meter or end-of-track.</summary>
    Meta,
    /// <summary>System-exclusive event.</summary>
    SystemExclusive
}

/// <summary>A MIDI event at an absolute tick within one track.</summary>
/// <param name="Tick">Absolute nonnegative tick.</param>
/// <param name="Kind">Event category.</param>
/// <param name="Channel">Zero-based channel for channel events.</param>
/// <param name="Data1">First channel-event data byte or system-exclusive status.</param>
/// <param name="Data2">Second channel-event data byte.</param>
/// <param name="MetaType">Meta-event type, when applicable.</param>
/// <param name="Payload">Opaque meta or system-exclusive payload.</param>
public sealed record SmfEvent(long Tick, SmfEventKind Kind, int Channel, int Data1, int Data2, byte? MetaType, byte[] Payload);

/// <summary>A sequence of events from one SMF track.</summary>
/// <param name="Events">Events sorted by absolute tick and original same-tick order.</param>
public sealed record SmfTrack(IReadOnlyList<SmfEvent> Events);

/// <summary>A tempo event represented as integer microseconds per quarter note.</summary>
/// <param name="Tick">Absolute tick at which the tempo starts.</param>
/// <param name="MicrosecondsPerQuarter">Positive microseconds per quarter note.</param>
public sealed record SmfTempo(long Tick, int MicrosecondsPerQuarter);

/// <summary>A time-signature event using the SMF denominator power representation.</summary>
/// <param name="Tick">Absolute tick at which the meter starts.</param>
/// <param name="Numerator">Positive numerator.</param>
/// <param name="DenominatorPower">The denominator is 2 raised to this power.</param>
/// <param name="ClocksPerMetronomeClick">SMF clocks per metronome click.</param>
/// <param name="ThirtySecondsPerQuarter">Thirty-second notes per MIDI quarter note.</param>
public sealed record SmfTimeSignature(long Tick, int Numerator, int DenominatorPower, int ClocksPerMetronomeClick, int ThirtySecondsPerQuarter);

/// <summary>An integer-PPQ Standard MIDI File in format 0 or 1.</summary>
/// <param name="Format">SMF format number, 0 or 1.</param>
/// <param name="TicksPerQuarter">Ticks per quarter note.</param>
/// <param name="Tracks">Tracks in file order.</param>
public sealed record SmfFile(int Format, int TicksPerQuarter, IReadOnlyList<SmfTrack> Tracks)
{
    /// <summary>Gets the tempo map, using the SMF default of 500,000 microseconds per quarter when no tempo event exists.</summary>
    public IReadOnlyList<SmfTempo> TempoMap
    {
        get
        {
            SmfTempo[] events = [.. Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.Meta && e.MetaType == 0x51 && e.Payload.Length == 3).Select(e => new SmfTempo(e.Tick, (e.Payload[0] << 16) | (e.Payload[1] << 8) | e.Payload[2])).OrderBy(e => e.Tick)];
            return events.Length == 0 || events[0].Tick > 0 ? [new SmfTempo(0, 500_000), .. events] : events;
        }
    }

    /// <summary>Gets time-signature events ordered by absolute tick.</summary>
    public IReadOnlyList<SmfTimeSignature> TimeSignatures =>
        [.. Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.Meta && e.MetaType == 0x58 && e.Payload.Length == 4).Select(e => new SmfTimeSignature(e.Tick, e.Payload[0], e.Payload[1], e.Payload[2], e.Payload[3])).OrderBy(e => e.Tick)];
}

/// <summary>Resource limits for reading or writing untrusted SMF content.</summary>
/// <param name="MaxFileBytes">Maximum encoded file length.</param>
/// <param name="MaxTracks">Maximum track count.</param>
/// <param name="MaxEventsPerTrack">Maximum events in one track.</param>
/// <param name="MaxTotalEvents">Maximum events across the file.</param>
/// <param name="MaxPayloadBytes">Maximum bytes in one event payload.</param>
public sealed record SmfLimits(int MaxFileBytes, int MaxTracks, int MaxEventsPerTrack, int MaxTotalEvents, int MaxPayloadBytes)
{
    /// <summary>Conservative limits for normal song files.</summary>
    public static SmfLimits Standard => new(64 * 1024 * 1024, 256, 1_000_000, 2_000_000, 16 * 1024 * 1024);
}

/// <summary>Invalid, unsupported or over-limit Standard MIDI File data.</summary>
/// <param name="message">Explanation that does not include file contents.</param>
public sealed class SmfFormatException(string message) : Exception(message);
