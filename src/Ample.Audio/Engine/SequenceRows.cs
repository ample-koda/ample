using System.Globalization;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The notes of a row of a sequencer pattern. A row is the notes of a channel for one pattern as text; a row that starts with <c>=</c> is a formula: a note pipeline (docs/language/12-note-pipelines.md)
/// that is worked out for the pattern (its bar 1 is the first bar of the pattern), so a long melody can be a rhythm that repeats plus values that cycle (<c>= 1:0:0.5:60:100 ... |&gt; repeat(8) |&gt; set(pitch="38 50")</c>).
/// </summary>
public static class SequenceRows
{
    /// <summary>Whether a row is a formula.</summary>
    /// <param name="row">The text of the row.</param>
    /// <returns>True when it starts with =.</returns>
    public static bool IsFormula(string row) => row.TrimStart().StartsWith('=');

    /// <summary>Reads the notes of a row: a formula is evaluated, plain rows are read in the ticks of the channel.</summary>
    /// <param name="row">The text of the row.</param>
    /// <param name="ticksPerQuarter">The ticks of the rows of the channel (1,000,000 when beats are decimals).</param>
    /// <param name="tempo">The tempo of the song, as a decimal text (only a formula needs it).</param>
    /// <param name="notes">The notes (every piece of a note that goes over a bar line is one note).</param>
    /// <param name="error">What is wrong, when it is.</param>
    /// <returns>True when the row could be read.</returns>
    public static bool TryRead(string row, int ticksPerQuarter, string tempo, out IReadOnlyList<StructuredMidiNote> notes, out string error)
    {
        notes = [];
        error = string.Empty;
        if (!IsFormula(row))
        {
            if (StructuredMidiNotes.TryParse(row, ticksPerQuarter, out notes)) return true;
            error = "the notes cannot be read";
            return false;
        }

        PipeResult result = NotePipeline.Evaluate(row.TrimStart()[1..], new NotePipeline.Context { Tempo = tempo, Limit = NotePipeline.DefaultBuildLimit });
        if (result.Notes is null)
        {
            error = "the formula has an error: " + (result.Errors.FirstOrDefault()?.Message ?? "it did not give notes");
            return false;
        }

        if (!StructuredMidiNotes.TryParse(result.Notes, StructuredMidiNotes.MicrobeatsPerBeat, out notes))
        {
            error = "the formula gave notes that cannot be read";
            return false;
        }

        return true;
    }

    /// <summary>The notes of a channel over the whole playlist, as they sound (the pattern bars the playlist plays, with the loop of the playlist applied up to the last clip).</summary>
    /// <param name="seq">The sequence.</param>
    /// <param name="channel">The channel name.</param>
    /// <param name="tempo">The tempo of the song, as a decimal text.</param>
    /// <param name="notes">The notes.</param>
    /// <param name="error">What is wrong, when it is.</param>
    /// <returns>True when all rows could be read.</returns>
    public static bool TryChannelNotes(SequenceFile seq, string channel, string tempo, out List<SemNote> notes, out string error)
    {
        notes = [];
        error = string.Empty;
        SeqChannel? c = seq.Channels.FirstOrDefault(x => x.Name == channel);
        if (c is null) { error = $"the sequence has no channel '{channel}' (it has: {string.Join(", ", seq.Channels.Select(x => x.Name))})"; return false; }
        int ppq = int.TryParse(c.Get("ppq"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 1_000_000 ? parsed : 1_000_000;
        List<(StructuredMidiNote Note, int BarOffset)> pieces = [];
        Dictionary<string, IReadOnlyList<StructuredMidiNote>> cache = new(StringComparer.Ordinal);
        foreach (SeqClip clip in seq.Clips)
        {
            SeqPattern? pattern = seq.Patterns.FirstOrDefault(p => p.Name == clip.Pattern);
            if (pattern is null) { error = $"a clip names the pattern '{clip.Pattern}', which is not defined"; return false; }
            string row = seq.RowsOf(pattern).GetValueOrDefault(channel, string.Empty);
            if (row.Length == 0) continue;
            string key = pattern.Name + "|" + row;
            if (!cache.TryGetValue(key, out IReadOnlyList<StructuredMidiNote>? parsedNotes))
            {
                if (!TryRead(row, ppq, tempo, out parsedNotes, out string why)) { error = $"pattern '{pattern.Name}': {why}"; return false; }
                cache[key] = parsedNotes;
            }

            for (int bar = 0; bar < clip.Bars; bar++)
            {
                int patternBar = bar % pattern.Bars;
                foreach (StructuredMidiNote n in parsedNotes.Where(x => x.Bar == patternBar + 1)) pieces.Add((n with { Bar = clip.At + bar }, 0));
            }
        }

        notes = NoteSemantics.Merge(pieces);
        return true;
    }
}
