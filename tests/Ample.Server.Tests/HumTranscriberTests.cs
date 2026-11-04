namespace Ample.Server.Tests;

public sealed class HumTranscriberTests
{
    private static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];

    private static byte[] Wav(IEnumerable<(double Hz, double Seconds)> line, int rate = 44100, double vibrato = 0.0)
    {
        List<short> samples = [];
        double phase = 0;
        foreach ((double hz, double seconds) in line)
        {
            int n = (int)(seconds * rate);
            for (int i = 0; i < n; i++)
            {
                double f = hz * (1 + (vibrato * Math.Sin(2 * Math.PI * 5.5 * i / rate)));
                phase += 2 * Math.PI * f / rate;
                double fade = Math.Min(1, Math.Min(i, n - i) / (0.02 * rate));
                // a voice has harmonics: a sine alone would be too easy
                double v = hz == 0 ? 0 : fade * 0.5 * (Math.Sin(phase) + (0.4 * Math.Sin(2 * phase)) + (0.2 * Math.Sin(3 * phase))) / 1.6;
                samples.Add((short)(v * 32767));
            }
        }

        byte[] bytes = new byte[44 + (samples.Count * 2)];
        void Put(int at, string text) { for (int i = 0; i < text.Length; i++) { bytes[at + i] = (byte)text[i]; } }
        Put(0, "RIFF"); BitConverter.GetBytes(36 + (samples.Count * 2)).CopyTo(bytes, 4); Put(8, "WAVE"); Put(12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(bytes, 16); BitConverter.GetBytes((short)1).CopyTo(bytes, 20); BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(rate).CopyTo(bytes, 24); BitConverter.GetBytes(rate * 2).CopyTo(bytes, 28); BitConverter.GetBytes((short)2).CopyTo(bytes, 32); BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        Put(36, "data"); BitConverter.GetBytes(samples.Count * 2).CopyTo(bytes, 40);
        for (int i = 0; i < samples.Count; i++)
        {
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, 44 + (i * 2));
        }

        return bytes;
    }

    [Fact]
    public void A_sung_line_becomes_the_notes_that_were_sung_in_the_tempo_of_the_song()
    {
        // A4 C5 E5, a pause, G4: half a second each, at 120 BPM one beat each
        HumResult r = HumTranscriber.Transcribe(Wav([(440, 0.5), (523.25, 0.5), (659.26, 0.5), (0, 0.3), (392, 0.5)], vibrato: 0.01), new HumOptions(Tempo: 120, Grid: 0.25));
        Assert.Equal([69, 72, 76, 67], r.Notes.Select(n => n.Pitch));
        Assert.All(r.Notes, n => Assert.InRange(n.Duration, 0.35, 0.6));
        Assert.StartsWith("1:0:1:69:", r.NotesText, StringComparison.Ordinal);
        Assert.Contains(" 1:1:1:72:", r.NotesText, StringComparison.Ordinal);
        Assert.Contains(" 1:2:1:76:", r.NotesText, StringComparison.Ordinal);
        Assert.Contains("1:3.5:", r.NotesText, StringComparison.Ordinal);   // after the pause of 0.3 s (0.6 beat) the next note snaps to the grid
    }

    [Fact]
    public void A_note_that_is_a_little_out_of_tune_snaps_to_the_key_and_a_silent_recording_finds_nothing()
    {
        // C major line sung a bit flat/sharp: 262 (C4), 296 (D4, +20 cents), 321 (E4 -40 cents) are the scale notes
        HumResult r = HumTranscriber.Transcribe(Wav([(261.63, 0.4), (297.0, 0.4), (326.0, 0.4), (349.23, 0.4), (392.0, 0.4), (523.25, 0.6)]), new HumOptions(Tempo: 120, Grid: 0.5, Key: "auto"));
        Assert.Equal("C major", r.Key);
        Assert.All(r.Notes, n => Assert.Contains(((n.Pitch % 12) + 12) % 12, MajorScale));

        HumResult silent = HumTranscriber.Transcribe(Wav([(0, 1.0)]), new HumOptions());
        Assert.Empty(silent.Notes);
        Assert.Contains("No melody", silent.Quality, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_over_a_bar_line_is_written_as_two_notes_and_the_start_bar_is_added()
    {
        HumResult r = HumTranscriber.Transcribe(Wav([(0, 0.01), (440, 3.0)]), new HumOptions(Tempo: 120, Grid: 0.5, StartBar: 5));
        // 3 s at 120 BPM = 6 beats: 4 beats in bar 5 and the rest in bar 6
        Assert.Contains("5:0:4:69:", r.NotesText, StringComparison.Ordinal);
        Assert.Contains("6:0:2:69:", r.NotesText, StringComparison.Ordinal);
    }
}
