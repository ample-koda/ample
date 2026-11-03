using System.Globalization;
using System.Text;

namespace Ample.Language;

/// <summary>Converts the readable MIDI note body to the existing exact structured note value.</summary>
public static class ExpandedMidiNotes
{
    private static readonly string[] Required = ["bar", "start", "length", "pitch", "velocity"];
    private static readonly string[] Routing = ["channel", "bank", "program"];

    /// <summary>Reads a notes body; comments stay in the source and are ignored only for sound.</summary>
    public static bool TryCompact(string source, int ppq, out string compact, double beatsPerBar = 4.0)
    {
        compact = string.Empty;
        if (source.Length > StructuredMidiNotes.MaximumTextLength || ppq is < 1 or > 1_000_000) return false;
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "notes {") return false;
        List<string> entries = [];
        Dictionary<string, string>? fields = null;
        bool closed = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (closed) return false;
            if (fields is null)
            {
                if (line == "note {")
                {
                    fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    continue;
                }
                if (line == "}") { closed = true; continue; }
                return false;
            }
            if (line == "}")
            {
                if (!Required.All(fields.ContainsKey) || Routing.Any(fields.ContainsKey) && !Routing.All(fields.ContainsKey)) return false;
                string[] values = [.. Required.Select(key => fields[key])];
                if (Routing.All(fields.ContainsKey)) values = [.. values, .. Routing.Select(key => fields[key])];
                bool hasTie = fields.TryGetValue("tie-id", out string? tieId);
                if (hasTie && Routing.All(fields.ContainsKey)) values = [.. values, tieId!];
                entries.Add(string.Join(':', values) + (hasTie && !Routing.All(fields.ContainsKey) ? $";tie={tieId}" : string.Empty));
                if (entries.Count > StructuredMidiNotes.MaximumNotes) return false;
                fields = null;
                continue;
            }
            int split = line.IndexOfAny([' ', '\t']);
            if (split <= 0 || split == line.Length - 1) return false;
            string key = line[..split];
            string value = line[(split + 1)..].Trim();
            if (!Required.Contains(key, StringComparer.Ordinal) && !Routing.Contains(key, StringComparer.Ordinal) && key != "tie-id" ||
                value.Length == 0 || value.Any(char.IsWhiteSpace) || !fields.TryAdd(key, value)) return false;
        }
        if (!closed || fields is not null) return false;
        compact = string.Join(' ', entries);
        return StructuredMidiNotes.TryParse(compact, ppq, out _, beatsPerBar);
    }

    /// <summary>Expands a valid compact value to named fields without changing note timing.</summary>
    public static bool TryExpand(string value, int ppq, out string expanded)
    {
        expanded = string.Empty;
        if (!StructuredMidiNotes.TryParse(value, ppq, out IReadOnlyList<StructuredMidiNote> notes)) return false;
        StringBuilder text = new("notes {\n");
        foreach (StructuredMidiNote note in notes)
        {
            text.Append("  note {\n    bar ").Append(note.Bar.ToString(CultureInfo.InvariantCulture))
                .Append("\n    start ").Append(Time(note.BeatTicks, ppq))
                .Append("\n    length ").Append(Time(note.LengthTicks, ppq))
                .Append("\n    pitch ").Append(note.Pitch.ToString(CultureInfo.InvariantCulture))
                .Append("\n    velocity ").Append(note.Velocity.ToString(CultureInfo.InvariantCulture));
            if (note.Channel >= 0)
                text.Append("\n    channel ").Append(note.Channel.ToString(CultureInfo.InvariantCulture))
                    .Append("\n    bank ").Append(note.Bank.ToString(CultureInfo.InvariantCulture))
                    .Append("\n    program ").Append(note.Program.ToString(CultureInfo.InvariantCulture));
            if (note.TieId >= 0) text.Append("\n    tie-id ").Append(note.TieId.ToString(CultureInfo.InvariantCulture));
            text.Append("\n  }\n");
        }
        text.Append('}');
        expanded = text.ToString();
        return true;
    }

    private static string Time(long ticks, int ppq)
    {
        if (ppq != StructuredMidiNotes.MicrobeatsPerBeat) return ticks.ToString(CultureInfo.InvariantCulture);
        long whole = ticks / ppq;
        long part = ticks % ppq;
        return part == 0 ? whole.ToString(CultureInfo.InvariantCulture) :
            whole.ToString(CultureInfo.InvariantCulture) + "." + part.ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
    }
}
