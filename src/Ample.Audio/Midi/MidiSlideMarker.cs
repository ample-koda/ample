using System.Globalization;
using System.Text;

namespace Ample.Audio.Midi;

/// <summary>Editable AMPLE slide identity carried beside the audible pitch bend ramp in a MIDI marker event.</summary>
internal static class MidiSlideMarker
{
    private const string Prefix = "AMPLE-SLIDE-1:";

    internal static SmfEvent Write(long start, long end, int pitch, int velocity, int channel, long baseEnd)
    {
        string value = string.Join(':', Prefix.TrimEnd(':'), start.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture), pitch.ToString(CultureInfo.InvariantCulture), velocity.ToString(CultureInfo.InvariantCulture), channel.ToString(CultureInfo.InvariantCulture), baseEnd.ToString(CultureInfo.InvariantCulture));
        return new SmfEvent(start, SmfEventKind.Meta, 0, 0, 0, 0x06, Encoding.ASCII.GetBytes(value));
    }

    internal static bool IsMarker(SmfEvent item) => item.Kind == SmfEventKind.Meta && item.MetaType == 0x06 && item.Payload.AsSpan().StartsWith(Encoding.ASCII.GetBytes(Prefix));

    internal static bool TryRead(SmfEvent item, out (long Start, long End, int Pitch, int Velocity, int Channel, long BaseEnd) slide)
    {
        slide = default;
        if (!IsMarker(item)) return false;
        string[] parts = Encoding.ASCII.GetString(item.Payload).Split(':');
        if (parts.Length != 7 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long end) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int pitch) ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int velocity) ||
            !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out int channel) ||
            !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out long baseEnd) ||
            start != item.Tick || end <= start || pitch is < 0 or > 127 || velocity is < 1 or > 127 || channel is < 0 or > 15 || baseEnd <= 0) return false;
        slide = (start, end, pitch, velocity, channel, baseEnd);
        return true;
    }
}
