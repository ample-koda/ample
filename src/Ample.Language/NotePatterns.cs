using System.Globalization;
using System.Text;

namespace Ample.Language;

/// <summary>One way to write notes shorter: an expression, how much shorter, and whether it was evaluated and gives the very same notes.</summary>
/// <param name="Kind">What it uses (rhythm-cycles).</param>
/// <param name="Expression">The note pipeline, ready to be a notes value.</param>
/// <param name="OriginalLength">The characters of the notes as they are written now.</param>
/// <param name="Length">The characters of the expression.</param>
/// <param name="Verified">True when the expression was evaluated and every note, length and setting is the same as the original.</param>
/// <param name="Description">In words: what repeats.</param>
/// <param name="Comparison">The comparison with the original (differences when it is not the same).</param>
public sealed record PatternCandidate(string Kind, string Expression, int OriginalLength, int Length, bool Verified, string Description, NoteComparison? Comparison);

/// <summary>
/// Finds the mathematical shape of notes: a rhythm that repeats (every bar or every few bars) with pitches, velocities and other settings that cycle over it. The candidate is
/// written as a note pipeline (<c>rhythm |&gt; bars(1..=p) |&gt; repeat(m) |&gt; set(pitch="...")</c>), evaluated, and compared with the original, so "the result is the same" is a fact, not a hope.
/// </summary>
public static class NotePatterns
{
    private const int MaxNotes = 20_000;
    private const int MaxPeriod = 16;

    private sealed record Segment(int FirstWindow, int Windows, List<SemNote> Template, Dictionary<string, List<long>> Lists, int Period);

    /// <summary>Looks for a shorter way to write the notes.</summary>
    /// <param name="notes">The notes (from NoteSemantics).</param>
    /// <param name="tempo">The tempo of the song (only for evaluating).</param>
    /// <param name="originalLength">The characters of the notes as they are written now.</param>
    /// <returns>The candidates, the shortest verified one first.</returns>
    public static IReadOnlyList<PatternCandidate> Find(IReadOnlyList<SemNote> notes, string tempo, int originalLength)
    {
        if (notes.Count == 0 || notes.Count > MaxNotes) return [];
        long barLength = NotePipeline.Bar;
        int bars = (int)((notes.Max(n => n.Start + n.Length) + barLength - 1) / barLength);
        List<PatternCandidate> found = [];
        foreach (int period in Enumerable.Range(1, Math.Min(bars, MaxPeriod)).Concat(bars > MaxPeriod ? [bars] : []))
        {
            if (Build(notes, period, bars) is not { } segments) continue;
            string expression = Write(segments, period);
            PatternCandidate candidate = Check(expression, notes, tempo, originalLength, segments, period);
            found.Add(candidate);
        }

        return [.. found.OrderByDescending(c => c.Verified).ThenBy(c => c.Length).Take(5)];
    }

    private static PatternCandidate Check(string expression, IReadOnlyList<SemNote> original, string tempo, int originalLength, List<Segment> segments, int period)
    {
        PipeResult? result = null;
        try { result = NotePipeline.Evaluate(expression, new NotePipeline.Context { Tempo = tempo }); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { }
        string describe = $"{segments.Count} part{(segments.Count == 1 ? string.Empty : "s")}: " + string.Join("; ", segments.Select(s => $"{s.Windows}x a rhythm of {s.Template.Count} note(s) over {period} bar(s)" + (s.Lists.Count == 0 ? string.Empty : " with " + string.Join(", ", s.Lists.Select(l => $"{l.Key} cycling over {Period(l.Value)}")))));
        if (result?.Notes is null || !NoteSemantics.TryRead(result.Notes, 1_000_000, 0, out List<SemNote> got))
            return new PatternCandidate("rhythm-cycles", expression, originalLength, expression.Length, false, describe, new NoteComparison(false, original.Count, 0, original.Count, 0, 0, [result?.Errors.FirstOrDefault()?.Message ?? "the expression did not evaluate"]));
        NoteComparison comparison = NoteSemantics.Compare(original, got);
        return new PatternCandidate("rhythm-cycles", expression, originalLength, expression.Length, comparison.Equal, describe, comparison);
    }

    // the notes cut into windows of `period` bars; a run of windows with the same rhythm is one segment; null when a note goes over the end of its window (then this period cannot be used)
    private static List<Segment>? Build(IReadOnlyList<SemNote> notes, int period, int bars)
    {
        long window = period * NotePipeline.Bar;
        int count = (bars + period - 1) / period;
        List<SemNote>[] byWindow = [.. Enumerable.Range(0, count).Select(_ => new List<SemNote>())];
        foreach (SemNote n in notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch))
        {
            int w = (int)(n.Start / window);
            if (n.Start + n.Length > (w + 1) * window) return null;
            byWindow[w].Add(n);
        }

        string Signature(int w) => string.Join(' ', byWindow[w].Select(n => ((n.Start - (w * window)).ToString(CultureInfo.InvariantCulture)) + ":" + n.Length.ToString(CultureInfo.InvariantCulture)));
        List<Segment> segments = [];
        int at = 0;
        while (at < count)
        {
            if (byWindow[at].Count == 0) { at++; continue; }
            string signature = Signature(at);
            int end = at + 1;
            while (end < count && byWindow[end].Count > 0 && Signature(end) == signature) end++;
            List<SemNote> template = [.. byWindow[at].Select(n => n with { Start = n.Start - (at * window) })];
            Dictionary<string, List<long>> lists = [];
            foreach (string field in new[] { "pitch", "velocity", "channel", "bank", "program", "pan", "fine", "rel", "cut", "slide", "port", "bend" })
            {
                List<long> values = [.. Enumerable.Range(at, end - at).SelectMany(w => byWindow[w].Select(n => Value(n, field)))];
                List<long> own = [.. template.Select(n => Value(n, field))];
                bool same = values.Select((v, i) => v == own[i % own.Count]).All(x => x);
                if (!same) lists[field] = values;
            }

            segments.Add(new Segment(at, end - at, template, lists, period));
            at = end;
        }

        return segments;
    }

    private static long Value(SemNote n, string field) => field switch
    {
        "pitch" => n.Pitch, "velocity" => n.Velocity, "channel" => n.Channel, "bank" => n.Bank, "program" => n.Program, "pan" => n.Pan, "fine" => n.Fine, "rel" => n.Rel, "cut" => n.Cut, "slide" => n.Slide, "port" => n.Port, _ => n.Bend,
    };

    private static int Period(List<long> values)
    {
        for (int q = 1; q < values.Count; q++)
        {
            bool ok = true;
            for (int i = q; i < values.Count && ok; i++) ok = values[i] == values[i - q];
            if (ok) return q;
        }

        return values.Count;
    }

    private static string Literal(List<SemNote> template)
    {
        List<PipeNote> events = [.. template.Select(n =>
        {
            string tail = n.Channel >= 0 ? FormattableString.Invariant($":{n.Channel}:{n.Bank}:{n.Program}") : string.Empty;
            StringBuilder ext = new();
            if (n.Pan != 0) ext.Append(CultureInfo.InvariantCulture, $";pan={n.Pan}");
            if (n.Fine != 0) ext.Append(CultureInfo.InvariantCulture, $";fine={n.Fine}");
            if (n.Rel >= 0) ext.Append(CultureInfo.InvariantCulture, $";rel={n.Rel}");
            if (n.Cut != 100) ext.Append(CultureInfo.InvariantCulture, $";cut={n.Cut}");
            if (n.Slide != 0) ext.Append(";slide=1");
            if (n.Port != 0) ext.Append(CultureInfo.InvariantCulture, $";port={n.Port}");
            if (n.Bend != 0) ext.Append(CultureInfo.InvariantCulture, $";bend={n.Bend}");
            return new PipeNote(n.Start, n.Length, n.Pitch, n.Velocity, tail, ext.ToString());
        })];
        return NotePipeline.WriteNotes(events);
    }

    private static string Write(List<Segment> segments, int period)
    {
        StringBuilder sb = new();
        for (int i = 0; i < segments.Count; i++)
        {
            Segment s = segments[i];
            StringBuilder part = new(Literal(s.Template));
            part.Append(CultureInfo.InvariantCulture, $" |> bars(1..={period})");
            if (s.Windows > 1) part.Append(CultureInfo.InvariantCulture, $" |> repeat({s.Windows})");
            if (s.Lists.Count > 0)
            {
                part.Append(" |> set(");
                part.Append(string.Join(", ", s.Lists.Select(l =>
                {
                    List<long> cycle = [.. l.Value.Take(Period(l.Value))];
                    return l.Key + "=" + (cycle.Count == 1 ? cycle[0].ToString(CultureInfo.InvariantCulture) : "\"" + string.Join(' ', cycle) + "\"");
                })));
                part.Append(')');
            }

            long offset = (long)s.FirstWindow * period;
            if (i == 0) sb.Append(part).Append(offset > 0 ? FormattableString.Invariant($" |> shift({offset}bars)") : string.Empty);
            else sb.Append(" |> merge(").Append(part).Append(FormattableString.Invariant($" |> shift({offset}bars))"));
        }

        return sb.ToString();
    }
}
