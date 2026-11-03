using System.Text.Json;

namespace Ample.Audio.Engine;

/// <summary>A sample-set note with the compact MIDI clip's per-note expression and slide path.</summary>
internal sealed record SampleNoteExpression(int Start, int Held, int Pitch, int Velocity, int Channel, int Pan, int Fine, int Rel, int Cut, bool Slide, bool Tied, int Port)
{
    internal static IReadOnlyList<SampleNoteExpression> Read(RenderRequest request)
    {
        if (!request.Inputs.TryGetValue("notes", out byte[]? data)) return [];
        using JsonDocument doc = JsonDocument.Parse(data);
        List<SampleNoteExpression> notes = [];
        foreach (JsonElement note in doc.RootElement.GetProperty("notes").EnumerateArray())
        {
            int bar = note.GetProperty("bar").GetInt32();
            if (!request.ActiveBars.Contains(bar) || bar < request.FirstBar || bar >= request.FirstBar + request.BarCount) continue;
            decimal beat = note.TryGetProperty("beat", out JsonElement beatValue) ? beatValue.GetDecimal() : 0;
            decimal beats = note.TryGetProperty("beats", out JsonElement beatsValue) ? beatsValue.GetDecimal() : 1;
            int start = checked((int)((bar - request.FirstBar) * (long)request.SamplesPerBar + beat * request.SamplesPerBar / request.BeatsPerBarDecimal));
            int held = Math.Max(1, checked((int)(beats * request.SamplesPerBar / request.BeatsPerBarDecimal)));
            int Value(string key, int fallback) => note.TryGetProperty(key, out JsonElement value) ? value.GetInt32() : fallback;
            notes.Add(new SampleNoteExpression(start, held, Value("pitch", 60), Value("velocity", 96), Value("channel", 0), Value("pan", 0), Value("fine", 0), Value("rel", -1), Value("cut", 100), note.TryGetProperty("slide", out JsonElement slide) && slide.ValueKind == JsonValueKind.True, note.TryGetProperty("tie", out JsonElement tie) && tie.ValueKind == JsonValueKind.True, Value("port", 0)));
        }
        return notes;
    }

    internal static (List<SampleNoteExpression> Voices, Dictionary<int, List<(int Start, int End, int Pitch)>> Paths) Plan(IReadOnlyList<SampleNoteExpression> notes, int sampleRate)
    {
        List<SampleNoteExpression> voices = [.. notes.Where(n => !n.Slide && n.Port == 0)];
        Dictionary<int, List<(int Start, int End, int Pitch)>> paths = [];
        foreach (SampleNoteExpression slide in notes.Where(n => n.Slide).OrderBy(n => n.Start))
        {
            int previous = Enumerable.Range(0, voices.Count).Where(i => voices[i].Channel == slide.Channel && voices[i].Start <= slide.Start && voices[i].Start + voices[i].Held >= slide.Start).OrderByDescending(i => voices[i].Start).DefaultIfEmpty(-1).First();
            if (previous < 0) continue;
            if (!paths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? path)) paths[previous] = path = [];
            int end = checked(slide.Start + slide.Held);
            if (slide.Tied && path.Count > 0 && path[^1].End == slide.Start && path[^1].Pitch == slide.Pitch) path[^1] = (path[^1].Start, end, slide.Pitch);
            else path.Add((slide.Start, end, slide.Pitch));
            voices[previous] = voices[previous] with { Held = Math.Max(voices[previous].Held, end - voices[previous].Start) };
        }
        foreach (SampleNoteExpression port in notes.Where(n => !n.Slide && n.Port > 0).OrderBy(n => n.Start))
        {
            int previous = Enumerable.Range(0, voices.Count).Where(i => voices[i].Channel == port.Channel && voices[i].Start < port.Start).OrderByDescending(i => voices[i].Start).DefaultIfEmpty(-1).First();
            if (previous < 0) { voices.Add(port with { Port = 0 }); continue; }
            int end = checked(port.Start + Math.Max(1, port.Port * sampleRate / 1000));
            if (!paths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? path)) paths[previous] = path = [];
            path.Add((port.Start, end, port.Pitch));
            voices[previous] = voices[previous] with { Held = Math.Max(voices[previous].Held, end - voices[previous].Start) };
        }
        foreach (List<(int Start, int End, int Pitch)> path in paths.Values) path.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (voices, paths);
    }
}
