using Ample.Audio.Midi;
using Ample.Engine.Midi;

namespace Ample.Core.Tests;

/// <summary>A MIDI file keeps its own meter through import and export: the module cuts the notes at the bar lines of that meter, and exports them back to the same ticks.</summary>
public class MidiMeterTests
{
    private const int Ppq = 480;

    private static SmfFile File(int numerator, int denominatorPower, params (long Start, long End, int Pitch)[] notes)
    {
        List<SmfEvent> events = [new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x58, [(byte)numerator, (byte)denominatorPower, 24, 8])];
        foreach ((long start, long end, int pitch) in notes)
        {
            events.Add(new SmfEvent(start, SmfEventKind.NoteOn, 0, pitch, 90, null, []));
            events.Add(new SmfEvent(end, SmfEventKind.NoteOff, 0, pitch, 0, null, []));
        }

        events.Add(new SmfEvent(events.Max(e => e.Tick), SmfEventKind.Meta, 0, 0, 0, 0x2F, []));
        return new SmfFile(1, Ppq, [new SmfTrack([.. events.OrderBy(e => e.Tick)])]);
    }

    private static string Clip(string moduleText)
    {
        string[] lines = moduleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int at = Array.FindIndex(lines, l => l.TrimStart().StartsWith("notes", StringComparison.Ordinal));
        return string.Join('\n', lines.Skip(at).Take(6));
    }

    [Theory]
    [InlineData(3, 2, 3 * Ppq, 2, "meter=3/4")]      // the second bar of 3/4 starts after three quarter notes
    [InlineData(6, 3, 3 * Ppq, 2, "meter=6/8")]      // 6/8 is three quarter notes too
    [InlineData(5, 2, 5 * Ppq, 2, "meter=5/4")]
    [InlineData(4, 2, 4 * Ppq, 2, "")]                // 4/4 stays as it was: no meter fact
    public void The_bars_of_the_module_are_the_beats_of_the_meter_of_the_file(int numerator, int denominatorPower, long barTicks, int secondBar, string fact)
    {
        SmfFile file = File(numerator, denominatorPower, (0, 240, 60), (barTicks, barTicks + 240, 62));
        MidiModuleConversion converted = MidiModuleConverter.Convert(file, "m");
        string header = converted.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').First(l => l.StartsWith("# midi-source ", StringComparison.Ordinal));
        if (fact.Length == 0) Assert.DoesNotContain("meter=", header, StringComparison.Ordinal);
        else Assert.Contains(fact, header, StringComparison.Ordinal);
        string clip = converted.Text;
        Assert.Contains($"{secondBar}:0:", clip.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);   // the second note is on the first beat of the second bar
        Assert.Equal(numerator != 4 || denominatorPower != 2, converted.Warnings.Any(w => w.Contains("must say meter", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(6, 3)]
    [InlineData(5, 2)]
    [InlineData(7, 3)]
    [InlineData(4, 2)]
    public void A_file_comes_back_with_the_same_note_ticks_after_import_and_export(int numerator, int denominatorPower)
    {
        long barTicks = (long)Math.Round(4.0 * numerator / (1 << denominatorPower) * Ppq);
        SmfFile file = File(numerator, denominatorPower, (0, 240, 60), (barTicks + 120, barTicks + 600, 64), (2 * barTicks, 2 * barTicks + 360, 67));
        MidiModuleConversion converted = MidiModuleConverter.Convert(file, "round");
        SmfFile exported = SmfReader.Parse(MidiModuleExporter.Export(converted.Text, EngineRig.Spec).Bytes, SmfLimits.Standard);
        (long Start, int Pitch)[] before = [.. file.Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.NoteOn).Select(e => (e.Tick, e.Data1)).OrderBy(x => x.Tick)];
        (long Start, int Pitch)[] after = [.. exported.Tracks.SelectMany(t => t.Events).Where(e => e.Kind == SmfEventKind.NoteOn).Select(e => (e.Tick, e.Data1)).OrderBy(x => x.Tick)];
        Assert.Equal(before, after);
        Assert.Contains(exported.TimeSignatures, t => t.Numerator == numerator && t.DenominatorPower == denominatorPower);   // the retained meter event travels with it
    }

    [Fact]
    public void A_file_that_changes_meter_gets_one_meter_and_a_warning()
    {
        List<SmfEvent> events =
        [
            new(0, SmfEventKind.Meta, 0, 0, 0, 0x58, [3, 2, 24, 8]),
            new(1440, SmfEventKind.Meta, 0, 0, 0, 0x58, [4, 2, 24, 8]),
            new(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new(240, SmfEventKind.NoteOff, 0, 60, 0, null, []),
        ];
        MidiModuleConversion converted = MidiModuleConverter.Convert(new SmfFile(1, Ppq, [new SmfTrack(events)]), "changes");
        Assert.Contains(converted.Warnings, w => w.Contains("changes meter", StringComparison.Ordinal));
        Assert.Contains("meter=3/4", converted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_score_in_three_four_is_cut_into_bars_of_three_beats_and_says_which_meter_to_set()
    {
        SmfFile file = File(3, 2, (0, 480, 60), (3 * Ppq, 4 * Ppq, 62));
        ScoreImportResult result = ScoreImport.FromMidi(SmfWriter.Write(file, SmfLimits.Standard), "Waltz", 4);
        Assert.Contains("meter to 3/4", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("2:0:", result.SequenceText, StringComparison.Ordinal);   // the second note is on the first beat of bar 2
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not in 4/4", StringComparison.Ordinal));
    }

    [Fact]
    public void A_musicxml_score_in_three_four_keeps_its_measures_and_names_the_meter()
    {
        string xml = "<score-partwise><part-list><score-part id=\"P1\"><part-name>Lead</part-name></score-part></part-list><part id=\"P1\">"
            + "<measure number=\"1\"><attributes><divisions>1</divisions><time><beats>3</beats><beat-type>4</beat-type></time></attributes>"
            + "<note><pitch><step>C</step><octave>4</octave></pitch><duration>3</duration></note></measure>"
            + "<measure number=\"2\"><note><pitch><step>D</step><octave>4</octave></pitch><duration>3</duration></note></measure></part></score-partwise>";
        ScoreImportResult result = ScoreImport.FromMusicXml(xml, "Waltz", 4);
        Assert.Contains("meter to 3/4", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("2:0:", result.SequenceText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not in 4/4", StringComparison.Ordinal));
    }
}
