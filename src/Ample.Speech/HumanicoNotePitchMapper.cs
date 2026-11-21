namespace Ample.Speech;

public sealed record SpeechNotePitch(double FundamentalHz, double Amplitude, bool Saturated);

public static class HumanicoNotePitchMapper
{
    public static SpeechNotePitch Map(SpeechNoteEvent note, int referenceMidiNote = 60, double referenceHz = 105)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (referenceMidiNote is < 0 or > 127 || !double.IsFinite(referenceHz) || referenceHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceMidiNote));
        if (note.MidiPitch is < 0 or > 127 || note.Velocity is < 1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(note));
        double requested = referenceHz * Math.Pow(2, (note.MidiPitch - referenceMidiNote) / 12d);
        double frequency = Math.Clamp(requested, 45, 700);
        return new(frequency, note.Velocity / 127d, frequency != requested);
    }
}
