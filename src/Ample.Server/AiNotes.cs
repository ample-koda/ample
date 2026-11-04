using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server;

/// <summary>
/// What an assistant needs to write notes as a formula and know that nothing changed: the notes of a clip, of a sequencer channel or of a pipeline in one form (NoteSemantics), compared, and a finder of the
/// rhythm that repeats with values that cycle (NotePatterns). Both read only the proposal.
/// </summary>
internal sealed partial class AiService
{
    private sealed record NotesSubject(List<SemNote> Notes, int Characters, string Label);

    private (AmpleDocument Doc, AiTask Task, string Path) ProposalSong(string token, string path)
    {
        AiTask task = ContextTokens.TryGetValue(token, out AiTask? t) ? t : throw new AmpleException("AMP5458", "This task is no longer running.");
        string rel = AiContext.Norm(path ?? string.Empty);
        if (rel.StartsWith("project/", StringComparison.OrdinalIgnoreCase)) rel = rel[8..];
        if (rel.Length == 0) rel = task.Request.Song;
        string file = ProjectPath.Resolve(task.ProjectDir, rel);
        if (!File.Exists(file)) throw new AmpleException("AMP5458", $"There is no song '{rel}' in the proposal (check the path with ample_project).");
        string root = Path.Combine(task.Workspace, "project");
        AmpleDocument doc = new Ample.Engine.Modules.ModuleService(root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(File.ReadAllText(file), s.Spec);
        return (doc, task, rel);
    }

    private static string TempoText(AmpleDocument doc) => (doc.Project?.Tempo ?? 120).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Param(BlockNode block, string name) => block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;

    private static NotePipeline.SourceInfo ClipSource(AmpleDocument doc, string name, string tempo)
    {
        BlockNode? clip = doc.Blocks.FirstOrDefault(b => b.Name == name && b.Type == "midi-clip");
        if (clip is null) return new NotePipeline.SourceInfo(null, null, $"'{name}' is not a midi-clip of the song");
        string text = Param(clip, "notes");
        if (text.Length == 0) return new NotePipeline.SourceInfo(null, null, $"'{name}' has no notes text");
        if (int.TryParse(Param(clip, "ppq"), NumberStyles.None, CultureInfo.InvariantCulture, out int ppq) && ppq != 1_000_000) return new NotePipeline.SourceInfo(null, null, $"'{name}' is written in ticks (ppq {ppq}): compare it by block instead of using it in a pipeline");
        PipeResult result = NotePipeline.Evaluate(text, new NotePipeline.Context { Tempo = tempo });
        return result.Value is null ? new NotePipeline.SourceInfo(null, null, result.Errors.FirstOrDefault()?.Message ?? "the notes cannot be worked out") : new NotePipeline.SourceInfo(result.Value, "clip:" + name, null);
    }

    private static NotesSubject SubjectOf(AmpleDocument doc, AiTask task, string block, string channel, string expression)
    {
        string tempo = TempoText(doc);
        if (expression.Length > 0)
        {
            PipeResult result = NotePipeline.Evaluate(expression, new NotePipeline.Context { Tempo = tempo, BeatsPerBar = (decimal)(doc.Project?.BeatsPerBar ?? 4.0), Limit = NotePipeline.DefaultBuildLimit, Source = name => ClipSource(doc, name, tempo), Helpers = doc.HelperTable });
            if (result.Notes is null) throw new AmpleException("AMP5458", "The expression has an error: " + (result.Errors.FirstOrDefault()?.Message ?? "it gave no notes") + (result.Errors.FirstOrDefault() is { } first ? $" (at {first.Start}-{first.End})" : string.Empty));
            return NoteSemantics.TryRead(result.Notes, 1_000_000, 0, out List<SemNote> got) ? new NotesSubject(got, expression.Length, "the expression") : throw new AmpleException("AMP5458", "The expression gave notes that cannot be read.");
        }

        BlockNode node = doc.Blocks.FirstOrDefault(b => b.Name == block) ?? throw new AmpleException("AMP5458", $"There is no block '{block}' in the song (check the names with ample_song).");
        if (node.Type == "midi-clip")
        {
            string text = Param(node, "notes");
            if (text.Length == 0) throw new AmpleException("AMP5458", $"'{block}' has no notes text (it plays chords or a melody): read it with ample_block, or write it as notes first.");
            int ppq = int.TryParse(Param(node, "ppq"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 1_000_000 ? parsed : 1_000_000;
            return NoteSemantics.TryRead(text, ppq, 0, out List<SemNote> notes) ? new NotesSubject(notes, text.Length, block) : throw new AmpleException("AMP5458", $"The notes of '{block}' cannot be read.");
        }

        if (node.Type == "sequencer")
        {
            if (channel.Length == 0) throw new AmpleException("AMP5458", $"'{block}' is a sequencer: give channel=<name> (the channels are: {string.Join(", ", SequenceOf(node, task).Channels.Select(c => c.Name))}).");
            SequenceFile seq = SequenceOf(node, task);
            if (!SequenceRows.TryChannelNotes(seq, channel, tempo, out List<SemNote> notes, out string error)) throw new AmpleException("AMP5458", error + ".");
            int chars = seq.Patterns.Sum(p => seq.RowsOf(p).GetValueOrDefault(channel, string.Empty).Length);
            return new NotesSubject(notes, chars, $"{block}/{channel}");
        }

        throw new AmpleException("AMP5458", $"'{block}' is a {node.Type}: only a midi-clip or a sequencer channel has notes to compare.");
    }

    private static SequenceFile SequenceOf(BlockNode sequencer, AiTask task)
    {
        string embedded = sequencer.Parameters.FirstOrDefault(p => p.Name == "sequence")?.Value ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(embedded)) return SequenceFile.Parse(embedded);
        string file = Param(sequencer, "file");
        if (file.Length == 0) throw new AmpleException("AMP5458", $"'{sequencer.Name}' has no sequence (no file and no sequence in the song).");
        string full = ProjectPath.Resolve(task.ProjectDir, file);
        if (!File.Exists(full)) throw new AmpleException("AMP5458", $"The sequence file '{file}' is not in the proposal.");
        return SequenceFile.Parse(File.ReadAllText(full));
    }

    private static JsonObject Describe(NotesSubject subject)
    {
        long end = subject.Notes.Count == 0 ? 0 : subject.Notes.Max(n => n.Start + n.Length);
        return new JsonObject
        {
            ["label"] = subject.Label,
            ["notes"] = subject.Notes.Count,
            ["bars"] = (int)((end + NotePipeline.Bar - 1) / NotePipeline.Bar),
            ["characters"] = subject.Characters,
            ["signature"] = NoteSemantics.Signature(subject.Notes),
            ["first"] = new JsonArray([.. subject.Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).Take(8).Select(n => (JsonNode)n.Text())]),
        };
    }

    /// <summary>The notes of a clip, a sequencer channel or an expression, and when a second one is given: whether they are the same (every note, length and setting).</summary>
    public JsonObject ResolveNotes(string token, JsonElement body)
    {
        string S(string name) => body.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        (AmpleDocument doc, AiTask task, string rel) = ProposalSong(token, S("path"));
        NotesSubject first = SubjectOf(doc, task, S("block"), S("channel"), S("expression"));
        JsonObject result = new() { ["path"] = rel, ["a"] = Describe(first) };
        if (S("compare_block").Length > 0 || S("compare_expression").Length > 0)
        {
            NotesSubject second = SubjectOf(doc, task, S("compare_block"), S("compare_channel"), S("compare_expression"));
            NoteComparison comparison = NoteSemantics.Compare(first.Notes, second.Notes, 12);
            result["b"] = Describe(second);
            result["compare"] = new JsonObject
            {
                ["same"] = comparison.Equal,
                ["missingInB"] = comparison.Missing,
                ["extraInB"] = comparison.Extra,
                ["changed"] = comparison.Changed,
                ["differences"] = new JsonArray([.. comparison.Differences.Select(d => (JsonNode)d)]),
            };
            result["note"] = comparison.Equal ? "Every note, length and setting is the same (within 2 micro-beats): replacing a by b changes nothing you can hear." : "They are not the same: do not replace a by b until the differences are gone.";
        }

        return result;
    }

    private static JsonObject Candidate(PatternCandidate c, int original) => new()
    {
        ["expression"] = c.Expression,
        ["characters"] = c.Length,
        ["originalCharacters"] = original,
        ["savedPercent"] = original == 0 ? 0 : Math.Max(0, 100 - (c.Length * 100 / original)),
        ["verified"] = c.Verified,
        ["what"] = c.Description,
        ["differences"] = new JsonArray([.. (c.Comparison?.Differences ?? []).Take(5).Select(d => (JsonNode)d)]),
    };

    /// <summary>Looks for a shorter way to write the notes of a clip or a sequencer channel: a rhythm that repeats with pitches, velocities and other settings that cycle. Every candidate was evaluated and compared with the original.</summary>
    public JsonObject FindPatterns(string token, JsonElement body)
    {
        string S(string name) => body.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        (AmpleDocument doc, AiTask task, string rel) = ProposalSong(token, S("path"));
        string block = S("block"), channel = S("channel"), tempo = TempoText(doc);
        BlockNode node = doc.Blocks.FirstOrDefault(b => b.Name == block) ?? throw new AmpleException("AMP5458", $"There is no block '{block}' in the song (check the names with ample_song).");
        JsonObject result = new() { ["path"] = rel, ["block"] = block };
        if (node.Type == "sequencer")
        {
            SequenceFile seq = SequenceOf(node, task);
            string[] channels = channel.Length > 0 ? [channel] : [.. seq.Channels.Select(c => c.Name)];
            JsonArray perChannel = [];
            List<(string Name, List<SemNote> Notes)> read = [];
            foreach (string name in channels)
            {
                if (!SequenceRows.TryChannelNotes(seq, name, tempo, out List<SemNote> all, out string error)) { perChannel.Add(new JsonObject { ["channel"] = name, ["error"] = error }); continue; }
                read.Add((name, all));
                JsonObject entry = new() { ["channel"] = name, ["notes"] = all.Count };
                // each pattern row that is long enough: a formula row that gives the very same notes (only a row of this pattern, bar 1 is its first bar)
                JsonArray rows = [];
                foreach (SeqPattern pattern in seq.Patterns)
                {
                    string row = seq.RowsOf(pattern).GetValueOrDefault(name, string.Empty);
                    if (row.Length < 80 || SequenceRows.IsFormula(row)) continue;
                    int ppq = int.TryParse(seq.Channels.First(c => c.Name == name).Get("ppq"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 1_000_000 ? parsed : 1_000_000;
                    if (!NoteSemantics.TryRead(row, ppq, 0, out List<SemNote> one) || one.Count < 8) continue;
                    PatternCandidate? best = NotePatterns.Find(one, tempo, row.Length).FirstOrDefault(c => c.Verified && c.Length < row.Length);
                    if (best is null) continue;
                    JsonObject c = Candidate(best, row.Length);
                    c["pattern"] = pattern.Name;
                    c["row"] = "= " + best.Expression;
                    rows.Add(c);
                }

                entry["patternRows"] = rows;
                perChannel.Add(entry);
            }

            // channels that play the very same notes: one source can feed both
            JsonArray same = [];
            for (int i = 0; i < read.Count; i++)
            {
                for (int j = i + 1; j < read.Count; j++)
                {
                    if (read[i].Notes.Count >= 4 && read[i].Notes.Count == read[j].Notes.Count && NoteSemantics.Compare(read[i].Notes, read[j].Notes, 1).Equal) same.Add($"{read[i].Name} and {read[j].Name} play exactly the same notes");
                }
            }

            result["channels"] = perChannel;
            result["identicalChannels"] = same;
            result["how"] = "Per pattern row: ample_edit op=sequence_row (a sequence kept in the song) or edit the .ampleseq file with the row text from \"row\" (a formula row starts with =). The whole channel as one expression: ample_resolve_notes with expression=<it> compare_block=" + block + " compare_channel=<channel> proves it.";
            if (channel.Length > 0 && read.Count == 1)
            {
                List<SemNote> notes = read[0].Notes;
                int chars = seq.Patterns.Sum(p => seq.RowsOf(p).GetValueOrDefault(channels[0], string.Empty).Length);
                result["wholeChannel"] = new JsonArray([.. NotePatterns.Find(notes, tempo, chars).Take(3).Select(c => (JsonNode)Candidate(c, chars))]);
            }

            return result;
        }

        NotesSubject subject = SubjectOf(doc, task, block, channel, string.Empty);
        IReadOnlyList<PatternCandidate> found = NotePatterns.Find(subject.Notes, tempo, subject.Characters);
        result["notes"] = subject.Notes.Count;
        result["characters"] = subject.Characters;
        result["candidates"] = new JsonArray([.. found.Select(c => (JsonNode)Candidate(c, subject.Characters))]);
        result["how"] = found.Any(c => c.Verified)
            ? "Replace the notes of the clip by the first verified candidate: ample_edit op=set_param block=" + block + " name=notes value=<expression>. It was evaluated and compared with the original: every note, length and setting is the same."
            : "No shorter expression was found that gives exactly the same notes (rhythm cycles with cycling values only): keep the notes as they are, or look for structure that needs a helper (a sequence that rises by a constant step, a motif transposed per bar: ample_docs name=language/14-helpers-types-and-dynamic).";
        return result;
    }
}
