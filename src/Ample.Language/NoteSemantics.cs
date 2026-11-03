using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ample.Language;

/// <summary>One note as it sounds, in absolute micro-beats from the start of the song: notes of a clip, of a sequencer channel and of a pipeline are compared in this form (a note cut at a bar line is one note again).</summary>
/// <param name="Start">Start in micro-beats from bar 1.</param>
/// <param name="Length">Length in micro-beats.</param>
/// <param name="Pitch">MIDI pitch.</param>
/// <param name="Velocity">Velocity.</param>
/// <param name="Channel">MIDI channel, or -1 when the note has none.</param>
/// <param name="Bank">Bank, or -1.</param>
/// <param name="Program">Program, or -1.</param>
/// <param name="Pan">Pan.</param>
/// <param name="Fine">Fine tuning in cents.</param>
/// <param name="Rel">Release in ms, or -1.</param>
/// <param name="Cut">Percent played.</param>
/// <param name="Slide">Slide flag.</param>
/// <param name="Port">Portamento in ms.</param>
/// <param name="Bend">Bend in cents.</param>
public sealed record SemNote(long Start, long Length, int Pitch, int Velocity, int Channel, int Bank, int Program, int Pan, int Fine, int Rel, int Cut, int Slide, int Port, int Bend)
{
    /// <summary>Gets the compact text of the note (bar:beat:length:pitch:velocity and the settings that are not the default).</summary>
    public string Text()
    {
        string Micro(long m) => (m / 1_000_000).ToString(CultureInfo.InvariantCulture) + (m % 1_000_000 == 0 ? string.Empty : "." + (m % 1_000_000).ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0'));
        StringBuilder sb = new();
        sb.Append(((Start / 4_000_000) + 1).ToString(CultureInfo.InvariantCulture)).Append(':').Append(Micro(Start % 4_000_000)).Append(':').Append(Micro(Length)).Append(':').Append(Pitch.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Velocity.ToString(CultureInfo.InvariantCulture));
        if (Channel >= 0) sb.Append(CultureInfo.InvariantCulture, $":{Channel}:{Bank}:{Program}");
        if (Pan != 0) sb.Append(CultureInfo.InvariantCulture, $";pan={Pan}");
        if (Fine != 0) sb.Append(CultureInfo.InvariantCulture, $";fine={Fine}");
        if (Rel >= 0) sb.Append(CultureInfo.InvariantCulture, $";rel={Rel}");
        if (Cut != 100) sb.Append(CultureInfo.InvariantCulture, $";cut={Cut}");
        if (Slide != 0) sb.Append(";slide=1");
        if (Port != 0) sb.Append(CultureInfo.InvariantCulture, $";port={Port}");
        if (Bend != 0) sb.Append(CultureInfo.InvariantCulture, $";bend={Bend}");
        return sb.ToString();
    }

    private string Key => FormattableString.Invariant($"{Pitch}|{Velocity}|{Channel}|{Bank}|{Program}|{Pan}|{Fine}|{Rel}|{Cut}|{Slide}|{Port}|{Bend}");

    internal string GroupKey() => Key;
}

/// <summary>The outcome of comparing two lists of notes.</summary>
/// <param name="Equal">Whether every note of one has its note in the other (within the tolerance).</param>
/// <param name="CountA">The notes of the first.</param>
/// <param name="CountB">The notes of the second.</param>
/// <param name="Missing">Notes of the first that the second does not have.</param>
/// <param name="Extra">Notes of the second that the first does not have.</param>
/// <param name="Changed">Notes that are in both at the same moment and pitch but differ in length or in a setting.</param>
/// <param name="Differences">The first differences, in words.</param>
public sealed record NoteComparison(bool Equal, int CountA, int CountB, int Missing, int Extra, int Changed, IReadOnlyList<string> Differences);

/// <summary>Notes as they sound, whatever wrote them (a clip, a sequencer row, a pipeline): the common form that makes "the result is the same" checkable.</summary>
public static class NoteSemantics
{
    /// <summary>Micro-beats the starts and lengths of two notes may differ by and still be the same note (a tick of a MIDI file is not a whole number of micro-beats).</summary>
    public const long Tolerance = 2;

    /// <summary>Reads compact notes text (the rows of a clip or of a sequencer pattern) as notes that sound.</summary>
    /// <param name="text">The notes text.</param>
    /// <param name="ticksPerQuarter">The ticks of the rows (1,000,000 for beats written as decimals).</param>
    /// <param name="barOffset">Bars to add to every bar number (the first bar of a pattern in the playlist, minus 1).</param>
    /// <param name="notes">The notes.</param>
    /// <returns>False when the text cannot be read.</returns>
    public static bool TryRead(string text, int ticksPerQuarter, int barOffset, out List<SemNote> notes)
    {
        notes = [];
        if (!StructuredMidiNotes.TryParse(text, ticksPerQuarter, out IReadOnlyList<StructuredMidiNote> parsed)) return false;
        notes = Merge(parsed.Select(n => (n, barOffset)));
        return true;
    }

    /// <summary>Puts pieces of notes from several rows together (the pieces of a note that goes over a bar line become one note again).</summary>
    /// <param name="pieces">The notes with the bars to add to them.</param>
    /// <returns>The notes in order.</returns>
    public static List<SemNote> Merge(IEnumerable<(StructuredMidiNote Note, int BarOffset)> pieces)
    {
        List<SemNote> result = [];
        Dictionary<(int Tie, int Pitch, int Channel), int> open = [];
        foreach ((StructuredMidiNote n, int offset) in pieces.OrderBy(x => x.Note.Bar + x.BarOffset).ThenBy(x => x.Note.BeatTicks * 1_000_000L / x.Note.TicksPerQuarter).ThenBy(x => x.Note.Pitch))
        {
            long start = (((long)n.Bar + offset - 1) * 4_000_000L) + Round(n.BeatTicks, n.TicksPerQuarter), length = Math.Max(1, Round(n.LengthTicks, n.TicksPerQuarter));
            SemNote note = new(start, length, n.Pitch, n.Velocity, n.Channel, n.Bank, n.Program, n.Pan, n.Fine, n.Rel, n.Cut, n.Slide, n.Port, n.Bend);
            if (n.TieId >= 0 && open.TryGetValue((n.TieId, n.Pitch, n.Channel), out int at) && Math.Abs(result[at].Start + result[at].Length - start) <= Tolerance)
            {
                result[at] = result[at] with { Length = (start + length) - result[at].Start };
                continue;
            }

            result.Add(note);
            if (n.TieId >= 0) open[(n.TieId, n.Pitch, n.Channel)] = result.Count - 1;
        }

        return result;
    }

    private static long Round(long ticks, int ppq) => ((ticks * 1_000_000L) + (ppq / 2)) / ppq;

    /// <summary>A signature of the notes: the hash of their text in a fixed order (it only equals another signature when every start, length and setting is exactly the same; use Compare for the tolerance).</summary>
    /// <param name="notes">The notes.</param>
    /// <returns>The hash text.</returns>
    public static string Signature(IEnumerable<SemNote> notes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenBy(n => n.GroupKey(), StringComparer.Ordinal).Select(n => n.Start.ToString(CultureInfo.InvariantCulture) + " " + n.Length.ToString(CultureInfo.InvariantCulture) + " " + n.GroupKey())))));

    /// <summary>Compares two lists of notes.</summary>
    /// <param name="a">The first (the original).</param>
    /// <param name="b">The second (what replaces it).</param>
    /// <param name="maxDifferences">How many differences are written out.</param>
    /// <returns>The comparison.</returns>
    public static NoteComparison Compare(IReadOnlyList<SemNote> a, IReadOnlyList<SemNote> b, int maxDifferences = 10)
    {
        List<SemNote> unmatchedA = [], unmatchedB = [];
        foreach (IGrouping<string, SemNote> group in a.GroupBy(n => n.GroupKey(), StringComparer.Ordinal))
        {
            SemNote[] left = [.. group.OrderBy(n => n.Start)];
            SemNote[] right = [.. b.Where(n => n.GroupKey() == group.Key).OrderBy(n => n.Start)];
            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                long delta = right[j].Start - left[i].Start;
                if (Math.Abs(delta) <= Tolerance)
                {
                    // the same moment: the same note when the length is the same too, else both wait to be paired as a changed note
                    if (Math.Abs(right[j].Length - left[i].Length) > Tolerance) { unmatchedA.Add(left[i]); unmatchedB.Add(right[j]); }
                    i++;
                    j++;
                }
                else if (delta > 0) unmatchedA.Add(left[i++]);
                else unmatchedB.Add(right[j++]);
            }

            unmatchedA.AddRange(left.Skip(i));
            unmatchedB.AddRange(right.Skip(j));
        }

        HashSet<string> seen = [.. a.Select(n => n.GroupKey())];
        unmatchedB.AddRange(b.Where(n => !seen.Contains(n.GroupKey())));
        List<string> differences = [];
        int changed = 0;
        for (int k = unmatchedA.Count - 1; k >= 0; k--)
        {
            int pair = unmatchedB.FindIndex(x => x.Pitch == unmatchedA[k].Pitch && Math.Abs(x.Start - unmatchedA[k].Start) <= Tolerance);
            if (pair < 0) continue;
            changed++;
            if (differences.Count < maxDifferences) differences.Add("changed: " + unmatchedA[k].Text() + " became " + unmatchedB[pair].Text());
            unmatchedB.RemoveAt(pair);
            unmatchedA.RemoveAt(k);
        }

        foreach (SemNote n in unmatchedA.OrderBy(n => n.Start).Take(Math.Max(0, maxDifferences - differences.Count))) differences.Add("missing in the second: " + n.Text());
        foreach (SemNote n in unmatchedB.OrderBy(n => n.Start).Take(Math.Max(0, maxDifferences - differences.Count))) differences.Add("extra in the second: " + n.Text());
        return new NoteComparison(unmatchedA.Count == 0 && unmatchedB.Count == 0 && changed == 0, a.Count, b.Count, unmatchedA.Count, unmatchedB.Count, changed, differences);
    }
}
