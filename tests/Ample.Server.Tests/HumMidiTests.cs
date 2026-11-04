using System.Text.Json;
using Ample.Audio.Midi;

namespace Ample.Server.Tests;

public sealed class HumMidiTests
{
    [Fact]
    public void TheNotesOfAHumBecomeAMidiFileAtTheTempoOfTheSong()
    {
        using JsonDocument notes = JsonDocument.Parse("[{\"start\":0,\"duration\":0.5,\"pitch\":60,\"strength\":1},{\"start\":0.5,\"duration\":0.5,\"pitch\":64,\"strength\":0.5}]");
        byte[] bytes = HumMidi.Build(notes.RootElement.EnumerateArray(), 120);
        SmfFile file = SmfReader.Parse(bytes, SmfLimits.Standard);
        SmfEvent[] on = [.. file.Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.NoteOn)];
        Assert.Equal([60, 64], on.Select(e => e.Data1));
        Assert.Equal(480, file.TicksPerQuarter);
        Assert.Equal(480, on[1].Tick);   // half a second at 120 beats a minute is one beat
        Assert.Equal(500_000, file.TempoMap[0].MicrosecondsPerQuarter);
        Assert.True(on[0].Data2 > on[1].Data2);   // the stronger note is louder
    }
}
