using System.Globalization;

namespace Ample.Language.Tests;

/// <summary>The finder of mathematical shape in notes, and the comparison that makes its answer a fact.</summary>
public sealed class NotePatternsTests
{
    private static string Bass(int bars)
    {
        int[] pitches = [38, 43, 45, 38];
        int[] velocities = [105, 82, 91];
        List<string> entries = [];
        int k = 0;
        for (int bar = 1; bar <= bars; bar++)
        {
            int root = pitches[(bar - 1) % 4];
            foreach ((string beat, string length) in new[] { ("0", "0.5"), ("0.75", "0.25"), ("1.5", "0.5"), ("2.5", "0.25"), ("3", "0.5") })
                entries.Add(string.Create(CultureInfo.InvariantCulture, $"{bar}:{beat}:{length}:{root + (k % 2 == 0 ? 0 : 12)}:{velocities[k++ % 3]}"));
        }

        return string.Join(' ', entries);
    }

    [Fact]
    public void A_rhythm_that_repeats_every_bar_with_pitches_and_velocities_that_cycle_is_found_shorter_and_verified_the_same()
    {
        string text = Bass(32);
        Assert.True(NoteSemantics.TryRead(text, 1_000_000, 0, out List<SemNote> notes));
        IReadOnlyList<PatternCandidate> found = NotePatterns.Find(notes, "100", text.Length);
        PatternCandidate best = found[0];
        Assert.True(best.Verified, string.Join(" | ", best.Comparison?.Differences ?? []));
        Assert.True(best.Length < text.Length / 4, $"{best.Length} of {text.Length}: {best.Expression}");
        Assert.Contains("repeat(", best.Expression, StringComparison.Ordinal);
        Assert.Contains("set(", best.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_of_rhythm_makes_a_second_part_and_the_whole_is_still_the_same()
    {
        string text = Bass(8) + " " + string.Join(' ', Enumerable.Range(9, 8).Select(b => string.Create(CultureInfo.InvariantCulture, $"{b}:0:2:50:100 {b}:2:2:{(b % 2 == 0 ? 52 : 55)}:100")));
        Assert.True(NoteSemantics.TryRead(text, 1_000_000, 0, out List<SemNote> notes));
        PatternCandidate best = NotePatterns.Find(notes, "100", text.Length)[0];
        Assert.True(best.Verified, string.Join(" | ", best.Comparison?.Differences ?? []));
        Assert.Contains("merge(", best.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparing_sees_a_changed_velocity_a_missing_note_and_a_tie_piece_as_one_note()
    {
        Assert.True(NoteSemantics.TryRead("1:3:1:60:100;tie=5 2:0:1:60:100;tie=5 2:2:1:62:100", 1_000_000, 0, out List<SemNote> a));
        Assert.Equal(2, a.Count);   // the two pieces are one note
        Assert.Equal(2_000_000, a.Single(n => n.Pitch == 60).Length);
        Assert.True(NoteSemantics.TryRead("1:3:2:60:100 2:2:1:62:90", 1_000_000, 0, out List<SemNote> b) is false);   // a note over a bar line is not a valid entry
        Assert.True(NoteSemantics.TryRead("1:3:1:60:100;tie=9 2:0:1:60:100;tie=9 2:2:1:62:90", 1_000_000, 0, out b));
        NoteComparison same = NoteSemantics.Compare(a, b);
        Assert.False(same.Equal);
        Assert.Equal(1, same.Changed);
        Assert.Contains("62", same.Differences[0], StringComparison.Ordinal);
        Assert.True(NoteSemantics.Compare(a, a).Equal);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Any_notes_with_every_setting_can_be_written_as_a_formula_that_gives_exactly_the_same_notes(int seed)
    {
        // the built-ins are complete: whatever a note is (any start, length, pitch, velocity, routing and every setting, over bar lines, in any mix), the finder has an expression that was worked out and compared
        Random random = new(seed);
        List<SemNote> notes = [];
        int bars = 20;
        for (int i = 0; i < 150; i++)
        {
            long start = random.NextInt64(0, (bars * 4_000_000L) - 1_000_000);
            long length = Math.Min(random.NextInt64(1, 6_000_000), (bars * 4_000_000L) - start);
            bool routed = random.Next(2) == 0;
            notes.Add(new SemNote(start, length, random.Next(0, 128), random.Next(1, 128), routed ? random.Next(0, 16) : -1, routed ? random.Next(0, 200) : -1, routed ? random.Next(0, 128) : -1,
                random.Next(2) == 0 ? 0 : random.Next(-100, 101), random.Next(2) == 0 ? 0 : random.Next(-100, 101), random.Next(2) == 0 ? -1 : random.Next(0, 5001), random.Next(2) == 0 ? 100 : random.Next(0, 101),
                random.Next(2), random.Next(2) == 0 ? 0 : random.Next(0, 2001), random.Next(2) == 0 ? 0 : random.Next(-2400, 2401)));
        }

        IReadOnlyList<PatternCandidate> found = NotePatterns.Find(notes, "100", 10_000);
        Assert.NotEmpty(found);
        PatternCandidate best = found[0];
        Assert.True(best.Verified, string.Join(" | ", best.Comparison?.Differences ?? []));
    }
}
