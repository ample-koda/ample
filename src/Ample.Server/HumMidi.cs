using System.Text.Json;
using Ample.Audio.Midi;

namespace Ample.Server;

/// <summary>Turns the notes found in a hum into a Standard MIDI File (one track, the tempo of the song), so the melody can be kept in a folder of the project.</summary>
internal static class HumMidi
{
    private const int TicksPerQuarter = 480;

    /// <summary>The bytes of a .mid with these notes. A note is { start, duration (seconds), pitch, strength 0..1 }.</summary>
    public static byte[] Build(IEnumerable<JsonElement> notes, double tempo)
    {
        double ticksPerSecond = Math.Max(1, tempo) / 60.0 * TicksPerQuarter;
        int microseconds = (int)Math.Round(60_000_000.0 / Math.Max(1, tempo));
        List<SmfEvent> events = [new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x51, [(byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds])];
        long last = 0;
        foreach (JsonElement note in notes)
        {
            int pitch = Math.Clamp(note.GetProperty("pitch").GetInt32(), 0, 127);
            double startSeconds = note.GetProperty("start").GetDouble();
            long start = (long)Math.Round(startSeconds * ticksPerSecond);
            long end = Math.Max(start + 1, (long)Math.Round((startSeconds + note.GetProperty("duration").GetDouble()) * ticksPerSecond));
            double strength = note.TryGetProperty("strength", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 1;
            int velocity = Math.Clamp((int)Math.Round(55 + 55 * strength), 30, 110);
            events.Add(new SmfEvent(start, SmfEventKind.NoteOn, 0, pitch, velocity, null, []));
            events.Add(new SmfEvent(end, SmfEventKind.NoteOff, 0, pitch, 0, null, []));
            last = Math.Max(last, end);
        }

        List<SmfEvent> ordered = [.. events.OrderBy(e => e.Tick).ThenBy(e => e.Kind == SmfEventKind.NoteOff ? 0 : 1)];
        ordered.Add(new SmfEvent(last, SmfEventKind.Meta, 0, 0, 0, 0x2F, []));
        return SmfWriter.Write(new SmfFile(1, TicksPerQuarter, [new SmfTrack(ordered)]), SmfLimits.Standard);
    }
}
