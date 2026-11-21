namespace Ample.Speech;

public sealed record SpeechNoteEvent(long StartTick, int MidiPitch, int Velocity, int Channel, long Ordinal);

public sealed record SpeechWordNote(int WordIndex, string Word, SpeechNoteEvent Note);

public static class HumanicoWordNotePlanner
{
    public static IReadOnlyList<SpeechWordNote> Assign(IReadOnlyList<string> words, IEnumerable<SpeechNoteEvent> notes)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(notes);
        List<SpeechWordNote> result = [];
        SpeechNoteEvent[] ordered = [.. notes.Where(note => note.Velocity > 0)
            .OrderBy(note => note.StartTick).ThenBy(note => note.MidiPitch).ThenBy(note => note.Channel).ThenBy(note => note.Ordinal)];
        int count = Math.Min(words.Count, ordered.Length);
        for (int i = 0; i < count; i++) result.Add(new(i, words[i], ordered[i]));
        return result;
    }
}
