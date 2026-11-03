using System.Globalization;

namespace Ample.Audio.Midi;

/// <summary>Unsupported node-editor features recorded by the MIDI module converter.</summary>
public sealed record MidiModuleFacts(IReadOnlyList<string> UnsupportedFeatures)
{
    /// <summary>Reads only the converter's versioned, anchored source-event comment grammar.</summary>
    public static MidiModuleFacts FromSource(string source)
    {
        SortedSet<string> found = new(StringComparer.Ordinal);
        foreach (string line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line == "# midi-fact feature=ties" || line.EndsWith(" feature=ties", StringComparison.Ordinal) && line.StartsWith("# midi-fact ", StringComparison.Ordinal)) { continue; } // ties are carried by the tie-id of the notes in the clip and shown in the node view
            if (!line.StartsWith("# source-event ", StringComparison.Ordinal)) continue;
            Dictionary<string, string> fields = [];
            foreach (string part in line[15..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0 || !fields.TryAdd(part[..equals], part[(equals + 1)..])) { found.Add("unclassified preserved MIDI events"); fields.Clear(); break; }
            }
            if (fields.Count == 0) continue;
            if (fields.Count != 8 || !fields.TryGetValue("kind", out string? kind) || !fields.TryGetValue("tick", out string? tick) || !long.TryParse(tick, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !fields.TryGetValue("track", out string? track) || !int.TryParse(track, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !fields.TryGetValue("channel", out string? channel) || !int.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !fields.TryGetValue("data1", out string? data1) || !int.TryParse(data1, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !fields.TryGetValue("data2", out string? data2) || !int.TryParse(data2, NumberStyles.None, CultureInfo.InvariantCulture, out _) || !fields.TryGetValue("meta", out string? meta) || (meta != "none" && (meta.Length != 2 || !byte.TryParse(meta, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))) || !fields.TryGetValue("payload", out string? payload) || payload.Length % 2 != 0 || payload.Any(c => !Uri.IsHexDigit(c))) { found.Add("unclassified preserved MIDI events"); continue; }
            switch (kind)
            {
                case "ProgramChange":
                case "ControlChange":
                case "PitchBend":
                case "PolyphonicPressure":
                case "ChannelPressure":
                case "SystemExclusive": break;
                case "Meta":
                    break;
                case "NoteOn":
                case "NoteOff": break;
                default: found.Add("unclassified preserved MIDI events"); break;
            }
        }
        return new MidiModuleFacts([.. found]);
    }
}
