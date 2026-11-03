using System.Buffers.Binary;

namespace Ample.Audio.Midi;

/// <summary>Parses bounded Standard MIDI Files without floating-point time conversion.</summary>
public static class SmfReader
{
    /// <summary>Parses an SMF format 0 or 1 byte sequence.</summary>
    /// <param name="data">Complete encoded file.</param>
    /// <param name="limits">Resource limits to enforce.</param>
    /// <returns>Parsed tracks and events with absolute integer ticks.</returns>
    /// <exception cref="SmfFormatException">The file is malformed, unsupported or exceeds a configured limit.</exception>
    public static SmfFile Parse(ReadOnlySpan<byte> data, SmfLimits limits)
    {
        ValidateLimits(limits);
        if (data.Length > limits.MaxFileBytes) throw Invalid("The MIDI file exceeds the configured size limit.");
        Reader reader = new(data);
        if (!reader.ReadTag("MThd")) throw Invalid("The MIDI file has no MThd header.");
        uint headerLength = reader.ReadUInt32();
        if (headerLength < 6 || headerLength > reader.Remaining) throw Invalid("The MIDI header length is invalid.");
        int format = reader.ReadUInt16();
        int trackCount = reader.ReadUInt16();
        int division = reader.ReadUInt16();
        reader.Skip(checked((int)headerLength - 6));
        if (format is not (0 or 1)) throw Invalid("Only MIDI file formats 0 and 1 are supported.");
        if (trackCount < 1 || trackCount > limits.MaxTracks || (format == 0 && trackCount != 1)) throw Invalid("The MIDI track count is invalid or exceeds a configured limit.");
        if ((division & 0x8000) != 0 || division == 0) throw Invalid("Only positive ticks-per-quarter-note timing is supported.");
        List<SmfTrack> tracks = new(trackCount);
        int totalEvents = 0;
        for (int i = 0; i < trackCount; i++)
        {
            if (!reader.ReadTag("MTrk")) throw Invalid("A MIDI track chunk is missing.");
            uint chunkLength = reader.ReadUInt32();
            if (chunkLength > reader.Remaining) throw Invalid("A MIDI track chunk is truncated.");
            tracks.Add(ParseTrack(reader.ReadSpan(checked((int)chunkLength)), limits, ref totalEvents));
        }
        if (reader.Remaining != 0) throw Invalid("The MIDI file contains data after its declared tracks.");
        return new SmfFile(format, division, tracks);
    }

    private static SmfTrack ParseTrack(ReadOnlySpan<byte> data, SmfLimits limits, ref int totalEvents)
    {
        TrackReader reader = new(data);
        List<SmfEvent> events = [];
        long tick = 0;
        byte runningStatus = 0;
        bool ended = false;
        while (reader.Remaining > 0)
        {
            if (events.Count >= limits.MaxEventsPerTrack || totalEvents >= limits.MaxTotalEvents) throw Invalid("The MIDI event count exceeds a configured limit.");
            uint delta = reader.ReadVlq();
            try { tick = checked(tick + delta); }
            catch (OverflowException) { throw Invalid("The MIDI tick position overflows the supported range."); }
            byte statusOrData = reader.ReadByte();
            byte status;
            byte firstData = 0;
            bool hasFirstData = false;
            if (statusOrData < 0x80)
            {
                if (runningStatus == 0) throw Invalid("A MIDI data byte has no running channel status.");
                status = runningStatus;
                firstData = statusOrData;
                hasFirstData = true;
            }
            else status = statusOrData;
            SmfEvent item;
            if (status is >= 0x80 and <= 0xEF)
            {
                runningStatus = status;
                int channel = status & 0x0F;
                int command = status & 0xF0;
                int data1 = hasFirstData ? firstData : reader.ReadDataByte();
                int data2 = command is 0xC0 or 0xD0 ? 0 : reader.ReadDataByte();
                SmfEventKind kind = command switch
                {
                    0x80 => SmfEventKind.NoteOff,
                    0x90 => data2 == 0 ? SmfEventKind.NoteOff : SmfEventKind.NoteOn,
                    0xA0 => SmfEventKind.PolyphonicPressure,
                    0xB0 => SmfEventKind.ControlChange,
                    0xC0 => SmfEventKind.ProgramChange,
                    0xD0 => SmfEventKind.ChannelPressure,
                    0xE0 => SmfEventKind.PitchBend,
                    _ => throw Invalid("A MIDI channel event has an invalid status.")
                };
                item = new SmfEvent(tick, kind, channel, data1, data2, null, []);
            }
            else if (status == 0xFF)
            {
                runningStatus = 0;
                byte metaType = reader.ReadByte();
                int length = reader.ReadLength(limits.MaxPayloadBytes);
                byte[] payload = reader.ReadBytes(length);
                if ((metaType == 0x51 && (length != 3 || payload[0] == 0 && payload[1] == 0 && payload[2] == 0)) || (metaType == 0x58 && (length != 4 || payload[0] == 0)))
                    throw Invalid("A MIDI tempo or time-signature meta event is malformed.");
                item = new SmfEvent(tick, SmfEventKind.Meta, 0, 0, 0, metaType, payload);
                if (metaType == 0x2F)
                {
                    if (length != 0 || reader.Remaining != 0) throw Invalid("The end-of-track event is malformed or is not the last track event.");
                    ended = true;
                }
            }
            else if (status is 0xF0 or 0xF7)
            {
                runningStatus = 0;
                int length = reader.ReadLength(limits.MaxPayloadBytes);
                item = new SmfEvent(tick, SmfEventKind.SystemExclusive, 0, status, 0, null, reader.ReadBytes(length));
            }
            else throw Invalid("System-common and real-time events are not valid SMF track events.");
            events.Add(item);
            totalEvents++;
            if (ended) break;
        }
        if (!ended) throw Invalid("A MIDI track has no end-of-track event.");
        return new SmfTrack(events);
    }

    internal static void ValidateLimits(SmfLimits limits)
    {
        if (limits.MaxFileBytes < 14 || limits.MaxTracks < 1 || limits.MaxEventsPerTrack < 1 || limits.MaxTotalEvents < 1 || limits.MaxPayloadBytes < 0)
            throw Invalid("The MIDI resource limits are invalid.");
    }

    internal static SmfFormatException Invalid(string message) => new(message);

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;
        public Reader(ReadOnlySpan<byte> data) { this.data = data; position = 0; }
        public int Remaining => data.Length - position;
        public bool ReadTag(string tag)
        {
            if (Remaining < 4) return false;
            bool match = tag switch
            {
                "MThd" => data[position] == 0x4D && data[position + 1] == 0x54 && data[position + 2] == 0x68 && data[position + 3] == 0x64,
                "MTrk" => data[position] == 0x4D && data[position + 1] == 0x54 && data[position + 2] == 0x72 && data[position + 3] == 0x6B,
                _ => false
            };
            if (match) position += 4;
            return match;
        }
        public ushort ReadUInt16() { Ensure(2); ushort value = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2; return value; }
        public uint ReadUInt32() { Ensure(4); uint value = BinaryPrimitives.ReadUInt32BigEndian(data[position..]); position += 4; return value; }
        public void Skip(int count) { Ensure(count); position += count; }
        public ReadOnlySpan<byte> ReadSpan(int count) { Ensure(count); ReadOnlySpan<byte> value = data.Slice(position, count); position += count; return value; }
        private void Ensure(int count) { if (count < 0 || count > Remaining) throw Invalid("The MIDI file is truncated."); }
    }

    private ref struct TrackReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;
        public TrackReader(ReadOnlySpan<byte> data) { this.data = data; position = 0; }
        public readonly int Remaining => data.Length - position;
        public byte ReadByte() { Ensure(1); return data[position++]; }
        public int ReadDataByte() { byte value = ReadByte(); if (value >= 0x80) throw Invalid("A MIDI event data byte has its status bit set."); return value; }
        public uint ReadVlq()
        {
            uint value = 0;
            for (int i = 0; i < 4; i++)
            {
                byte part = ReadByte();
                value = (value << 7) | (uint)(part & 0x7F);
                if ((part & 0x80) == 0) return value;
            }
            throw Invalid("A MIDI variable-length quantity is longer than four bytes.");
        }
        public int ReadLength(int max)
        {
            uint length = ReadVlq();
            if (length > max || length > Remaining) throw Invalid("A MIDI event payload exceeds a configured limit or the track boundary.");
            return checked((int)length);
        }
        public byte[] ReadBytes(int count) { Ensure(count); byte[] result = data.Slice(position, count).ToArray(); position += count; return result; }
        private readonly void Ensure(int count) { if (count < 0 || count > Remaining) throw Invalid("A MIDI track event is truncated."); }
    }
}
