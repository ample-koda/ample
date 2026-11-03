namespace Ample.Language.Tests;

public sealed class NoteRowFormatTests
{
    [Fact]
    public void A_beat_of_four_or_more_is_not_a_note_of_that_bar_and_the_next_bar_starts_at_2_0()
    {
        Assert.False(StructuredMidiNotes.TryParse("1:0:0.5:36:112 1:4:0.5:36:112", 1_000_000, out _));
        Assert.True(StructuredMidiNotes.TryParse("1:0:0.5:36:112 2:0:0.5:36:112 1:3.5:0.25:39:108", 1_000_000, out IReadOnlyList<StructuredMidiNote> notes));
        Assert.Equal(3, notes.Count);
    }
}
