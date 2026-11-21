namespace Ample.Speech;

public static class HumanicoMusicalPhraseRenderer
{
    public static short[] Render(IReadOnlyList<string> words, IEnumerable<SpeechNoteEvent> notes, ulong seed, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SpeechWordNote> assignments = HumanicoWordNotePlanner.Assign(words, notes);
        List<short> result = [];
        foreach (SpeechWordNote assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpeechNotePitch pitch = HumanicoNotePitchMapper.Map(assignment.Note);
            IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(
                HumanicoSegmentPlanner.PlanWord(assignment.Word, cancellationToken), pitch.FundamentalHz, 1, cancellationToken);
            short[] samples = HumanicoFrameSynthesizer.Render(frames, seed ^ (ulong)assignment.WordIndex, cancellationToken);
            for (int i = 0; i < samples.Length; i++) result.Add((short)Math.Clamp(Math.Round(samples[i] * pitch.Amplitude), short.MinValue, short.MaxValue));
        }

        return [.. result];
    }
}
