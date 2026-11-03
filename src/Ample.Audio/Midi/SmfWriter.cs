using System.Buffers.Binary;

namespace Ample.Audio.Midi;

/// <summary>Writes deterministic Standard MIDI Files from the supported integer event model.</summary>
public static class SmfWriter
{
    /// <summary>Encodes an SMF format 0 or 1 file with explicit channel statuses.</summary>
    /// <param name="file">File model to encode.</param>
    /// <param name="limits">Resource limits to enforce.</param>
    /// <returns>Complete encoded bytes.</returns>
    /// <exception cref="SmfFormatException">The model is invalid or exceeds a configured limit.</exception>
    public static byte[] Write(SmfFile file, SmfLimits limits)
    {
        SmfReader.ValidateLimits(limits);
        if (file.Format is not (0 or 1) || file.TicksPerQuarter is < 1 or > 0x7FFF || file.Tracks.Count is < 1 || file.Tracks.Count > limits.MaxTracks || (file.Format == 0 && file.Tracks.Count != 1))
            throw SmfReader.Invalid("The MIDI file model has an invalid format, division or track count.");
        using MemoryStream output = new();
        WriteTag(output, "MThd");
        WriteUInt32(output, 6);
        WriteUInt16(output, checked((ushort)file.Format));
        WriteUInt16(output, checked((ushort)file.Tracks.Count));
        WriteUInt16(output, checked((ushort)file.TicksPerQuarter));
        int total = 0;
        foreach (SmfTrack track in file.Tracks)
        {
            if (track.Events.Count > limits.MaxEventsPerTrack || track.Events.Count > limits.MaxTotalEvents - total) throw SmfReader.Invalid("The MIDI event count exceeds a configured limit.");
            total += track.Events.Count;
            using MemoryStream body = new();
            long previousTick = 0;
            bool hasEnd = false;
            foreach (SmfEvent item in track.Events)
            {
                if (hasEnd) throw SmfReader.Invalid("The end-of-track event must be the final track event.");
                if (item.Tick < previousTick) throw SmfReader.Invalid("MIDI events must be ordered by nondecreasing tick.");
                WriteVlq(body, checked((ulong)(item.Tick - previousTick)));
                previousTick = item.Tick;
                int remainingBytes = limits.MaxFileBytes - checked((int)output.Length) - 8 - checked((int)body.Length);
                if (remainingBytes < 0) throw SmfReader.Invalid("The encoded MIDI file exceeds the configured size limit.");
                WriteEvent(body, item, limits, remainingBytes);
                if (body.Length + output.Length + 8 > limits.MaxFileBytes) throw SmfReader.Invalid("The encoded MIDI file exceeds the configured size limit.");
                hasEnd = item.Kind == SmfEventKind.Meta && item.MetaType == 0x2F;
            }
            if (!hasEnd) throw SmfReader.Invalid("A MIDI track must end with an end-of-track event.");
            if (body.Length + output.Length + 8 > limits.MaxFileBytes) throw SmfReader.Invalid("The encoded MIDI file exceeds the configured size limit.");
            WriteTag(output, "MTrk");
            WriteUInt32(output, checked((uint)body.Length));
            body.Position = 0;
            body.CopyTo(output);
        }
        return output.ToArray();
    }

    private static void WriteEvent(Stream output, SmfEvent item, SmfLimits limits, int remainingBytes)
    {
        if (item.Tick < 0) throw SmfReader.Invalid("MIDI ticks must be nonnegative.");
        if (item.Kind == SmfEventKind.Meta)
        {
            if (item.MetaType is null || item.Payload.Length > limits.MaxPayloadBytes || item.Payload.Length > remainingBytes) throw SmfReader.Invalid("A MIDI meta event is invalid or over its payload limit.");
            if ((item.MetaType == 0x2F && item.Payload.Length != 0) || (item.MetaType == 0x51 && (item.Payload.Length != 3 || item.Payload[0] == 0 && item.Payload[1] == 0 && item.Payload[2] == 0)) || (item.MetaType == 0x58 && (item.Payload.Length != 4 || item.Payload[0] == 0)))
                throw SmfReader.Invalid("A MIDI end, tempo or time-signature event is malformed.");
            output.WriteByte(0xFF);
            output.WriteByte(item.MetaType.Value);
            WriteVlq(output, checked((ulong)item.Payload.Length));
            output.Write(item.Payload);
            return;
        }
        if (item.Kind == SmfEventKind.SystemExclusive)
        {
            if (item.Data1 is not (0xF0 or 0xF7) || item.Payload.Length > limits.MaxPayloadBytes || item.Payload.Length > remainingBytes) throw SmfReader.Invalid("A MIDI system-exclusive event is invalid or over its payload limit.");
            output.WriteByte(checked((byte)item.Data1));
            WriteVlq(output, checked((ulong)item.Payload.Length));
            output.Write(item.Payload);
            return;
        }
        if (item.Channel is < 0 or > 15 || item.Data1 is < 0 or > 127 || item.Data2 is < 0 or > 127) throw SmfReader.Invalid("A MIDI channel event contains an invalid channel or data byte.");
        byte command = item.Kind switch
        {
            SmfEventKind.NoteOff => 0x80,
            SmfEventKind.NoteOn => 0x90,
            SmfEventKind.PolyphonicPressure => 0xA0,
            SmfEventKind.ControlChange => 0xB0,
            SmfEventKind.ProgramChange => 0xC0,
            SmfEventKind.ChannelPressure => 0xD0,
            SmfEventKind.PitchBend => 0xE0,
            _ => throw SmfReader.Invalid("The MIDI event kind is unsupported.")
        };
        bool oneDataByte = command is 0xC0 or 0xD0;
        output.WriteByte((byte)(command | item.Channel));
        output.WriteByte(checked((byte)item.Data1));
        if (!oneDataByte) output.WriteByte(checked((byte)item.Data2));
    }

    private static void WriteVlq(Stream output, ulong value)
    {
        if (value > 0x0FFFFFFF) throw SmfReader.Invalid("A MIDI delta time exceeds the four-byte variable-length limit.");
        Span<byte> bytes = stackalloc byte[4];
        int at = 3;
        bytes[at] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0) bytes[--at] = (byte)((value & 0x7F) | 0x80);
        output.Write(bytes[at..]);
    }

    private static void WriteTag(Stream output, string tag) => output.Write(System.Text.Encoding.ASCII.GetBytes(tag));
    private static void WriteUInt16(Stream output, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); output.Write(bytes); }
    private static void WriteUInt32(Stream output, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); output.Write(bytes); }
}
