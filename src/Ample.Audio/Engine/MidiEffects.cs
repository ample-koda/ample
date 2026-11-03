using System.Text;
using System.Text.Json.Nodes;
using Ample.Audio.Random;

namespace Ample.Audio.Engine;

/// <summary>
/// The MIDI effect blocks: they take the notes of a block (<c>notes</c>, JSON with a bar, pitch, beat, length and velocity per note) and give new notes, so they sit between a clip
/// and an instrument. Integer maths and the stable PRNG only, so the result is the same everywhere.
/// </summary>
public static class MidiEffects
{
    /// <summary>Gets the MIDI effect renderers.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } =
    [
        new MidiFx("transpose", "1", Transpose),
        new MidiFx("midi-humanize", "1", Humanize),
        new MidiFx("quantize", "1", Quantize),
        new MidiFx("arpeggiator", "1", Arpeggio),
        new MidiFx("chord", "1", ChordNotes),
//REGISTER
    ];

    private delegate IEnumerable<JsonObject> Transform(RenderRequest request, IReadOnlyList<JsonObject> notes, Func<string, string> text);

    private sealed class MidiFx(string blockType, string version, Transform transform) : IBlockRenderer
    {
        public string BlockType => blockType;

        public string Id => "builtin." + blockType;

        public string Version => version;

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
            JsonObject root = r.Inputs.TryGetValue("notes", out byte[]? bytes) && JsonNode.Parse(bytes) is JsonObject parsed ? parsed : [];
            if (Text("enabled") == "off" && r.Inputs.TryGetValue("notes", out byte[]? original))
            {
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("notes", original)]);
            }

            List<JsonObject> notes = [.. (root["notes"] as JsonArray ?? []).OfType<JsonObject>().Select(n => (JsonObject)n.DeepClone())];
            JsonArray result = [];
            foreach (JsonObject note in transform(r, notes, Text))
            {
                result.Add(note);
            }

            JsonObject output = [];
            foreach (KeyValuePair<string, JsonNode?> property in root)
            {
                if (property.Key != "notes")
                {
                    output[property.Key] = property.Value?.DeepClone();
                }
            }

            output["notes"] = result;
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("notes", Encoding.UTF8.GetBytes(output.ToJsonString()))]);
        }
    }

    private static int Int(string text, int fallback) => AudioEffects.Read(text, fallback);

    private static decimal Beat(JsonObject note, string property, decimal fallback) => note[property] is JsonValue value && value.TryGetValue(out decimal d) ? d : fallback;

    private static JsonObject Moved(JsonObject note, int pitch)
    {
        note["pitch"] = Math.Clamp(pitch, 0, 127);
        return note;
    }

    // transpose: every pitch moves by the number of semitones
    private static IEnumerable<JsonObject> Transpose(RenderRequest r, IReadOnlyList<JsonObject> notes, Func<string, string> text)
    {
        int semitones = Math.Clamp(Int(text("semitones"), 0), -48, 48);
        foreach (JsonObject note in notes)
        {
            yield return Moved(note, (note["pitch"]?.GetValue<int>() ?? 60) + semitones);
        }
    }

    // midi-humanize: a little deterministic variation of the velocity and of the start of each note (the same song always gets the same variation)
    private static IEnumerable<JsonObject> Humanize(RenderRequest r, IReadOnlyList<JsonObject> notes, Func<string, string> text)
    {
        int velocity = Math.Clamp(Int(text("velocity"), 10), 0, 60);
        int timing = Math.Clamp(Int(text("timing"), 20), 0, 200);      // thousandths of a beat
        StableRng rng = new(r.Seed ^ 0x48554D414E495A45UL);
        foreach (JsonObject note in notes)
        {
            int dv = (int)(rng.Next() >> 40) % ((2 * velocity) + 1) - velocity;
            int dt = (int)(rng.Next() >> 40) % ((2 * timing) + 1) - timing;
            note["velocity"] = Math.Clamp((note["velocity"]?.GetValue<int>() ?? 96) + dv, 1, 127);
            decimal beat = Math.Clamp(Beat(note, "beat", 0) + (dt / 1000m), 0m, 3.99m);
            note["beat"] = beat;
            note.Remove("tick");
            yield return note;
        }
    }

    // quantize: starts (and lengths) move to the nearest step of a grid, by a strength
    private static IEnumerable<JsonObject> Quantize(RenderRequest r, IReadOnlyList<JsonObject> notes, Func<string, string> text)
    {
        decimal grid = text("grid") switch { "1/4" => 1m, "1/8" => 0.5m, "1/32" => 0.125m, "1/8t" => 1m / 3m, _ => 0.25m };
        int strength = Math.Clamp(Int(text("strength"), 100), 0, 100);
        foreach (JsonObject note in notes)
        {
            decimal beat = Beat(note, "beat", 0), length = Beat(note, "beats", 1);
            decimal snapped = Math.Round(beat / grid, MidpointRounding.AwayFromZero) * grid;
            decimal newBeat = Math.Clamp(Math.Round(beat + ((snapped - beat) * strength / 100m), 3), 0m, 3.999m);
            note["beat"] = newBeat;
            note.Remove("tick");
            if (text("lengths") == "on")
            {
                decimal snappedLength = Math.Max(grid, Math.Round(length / grid, MidpointRounding.AwayFromZero) * grid);
                note["beats"] = Math.Round(length + ((snappedLength - length) * strength / 100m), 3);
                note.Remove("ticks");
            }

            yield return note;
        }
    }

    private static int UpDown(int i, int count)
    {
        int cycle = Math.Max(1, (count * 2) - 2), position = i % cycle;
        return position < count ? position : cycle - position;
    }

    // arpeggiator: the notes that start together (a chord) become a run of single notes, one per step, up, down, up and down or in a stable random order
    private static IEnumerable<JsonObject> Arpeggio(RenderRequest r, IReadOnlyList<JsonObject> notes, Func<string, string> text)
    {
        decimal step = text("rate") switch { "1/4" => 1m, "1/8" => 0.5m, "1/32" => 0.125m, _ => 0.25m };
        int octaves = Math.Clamp(Int(text("octaves"), 1), 1, 4);
        int gate = Math.Clamp(Int(text("gate"), 80), 10, 100);
        string pattern = text("pattern");
        StableRng rng = new(r.Seed ^ 0x415250454747494FUL);
        foreach (IGrouping<(int Bar, decimal Beat), JsonObject> chord in notes.GroupBy(n => (n["bar"]?.GetValue<int>() ?? 1, Beat(n, "beat", 0))).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
        {
            int[] pitches = [.. chord.Select(n => n["pitch"]?.GetValue<int>() ?? 60).Distinct().Order()];
            List<int> pool = [.. Enumerable.Range(0, octaves).SelectMany(o => pitches.Select(p => p + (12 * o)))];
            JsonObject template = chord.First();
            decimal total = Beat(template, "beats", 4), start = chord.Key.Beat;
            int count = (int)Math.Floor(total / step);
            for (int i = 0; i < count && start + (i * step) < 4m; i++)
            {
                int index = pattern switch
                {
                    "down" => pool.Count - 1 - (i % pool.Count),
                    "updown" => UpDown(i, pool.Count),
                    "random" => (int)(rng.Next() >> 40) % pool.Count,
                    _ => i % pool.Count,
                };
                JsonObject note = (JsonObject)template.DeepClone();
                note["pitch"] = Math.Clamp(pool[index], 0, 127);
                note["beat"] = start + (i * step);
                note["beats"] = Math.Round(step * gate / 100m, 3);
                note.Remove("tick");
                note.Remove("ticks");
                yield return note;
            }
        }
    }

    // chord: every note becomes a chord on that root note
    private static IEnumerable<JsonObject> ChordNotes(RenderRequest r, IReadOnlyList<JsonObject> notes, Func<string, string> text)
    {
        int[] intervals = text("type") switch
        {
            "minor" => [0, 3, 7],
            "sus2" => [0, 2, 7],
            "sus4" => [0, 5, 7],
            "7" => [0, 4, 7, 10],
            "maj7" => [0, 4, 7, 11],
            "min7" => [0, 3, 7, 10],
            "dim" => [0, 3, 6],
            "power" => [0, 7, 12],
            _ => [0, 4, 7],
        };
        int inversion = Math.Clamp(Int(text("inversion"), 0), 0, intervals.Length - 1);
        foreach (JsonObject note in notes)
        {
            int root = note["pitch"]?.GetValue<int>() ?? 60;
            int[] pitches = [.. intervals.Select((interval, i) => root + interval + (i < inversion ? 12 : 0))];
            foreach (int pitch in pitches)
            {
                yield return Moved((JsonObject)note.DeepClone(), pitch);
            }
        }
    }
    //BLOCKS
}
