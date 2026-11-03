using Ample.Audio.Midi;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

/// <summary>The score import (MIDI), the project log and the environment check.</summary>
public sealed class ScoreImportAndLogTests
{
    private static byte[] Midi()
    {
        byte[] track =
        [
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,   // tempo 500000: 120 BPM
            0x00, 0xC0, 0x28,                            // program 40
            0x00, 0x90, 0x3C, 0x64,                      // C4 on
            0x83, 0x60, 0x80, 0x3C, 0x40,                // off after 480 ticks (one beat)
            0x83, 0x60, 0x90, 0x40, 0x50,                // E4 on one beat later (beat 2)
            0x87, 0x40, 0x80, 0x40, 0x40,                // off after 960 ticks
            0x00, 0xFF, 0x2F, 0x00,
        ];
        List<byte> bytes = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0, .. "MTrk"u8.ToArray(), 0, 0, 0, (byte)track.Length, .. track];
        return [.. bytes];
    }

    [Fact]
    public void A_midi_file_becomes_channels_patterns_and_a_playlist_with_its_tempo()
    {
        ScoreImportResult result = ScoreImport.FromMidi(Midi(), "tune", 4);
        Assert.Equal(1, result.Parts);
        Assert.Equal(2, result.Notes);
        Assert.Equal(120, result.Tempo, 1);
        Assert.Contains("program=40", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("1:0:480:60:100", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("1:960:960:64:80", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("clip part-1 track=1 at=1 for=1", result.SequenceText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_drum_gate_keeps_its_length_and_a_pitch_bend_is_kept_as_an_event()
    {
        byte[] track =
        [
            0x00, 0xE9, 0x00, 0x50,                      // pitch bend on channel 10 (kept as an event)
            0x00, 0x99, 0x24, 0x64,                      // kick on (channel 10)
            0x05, 0x89, 0x24, 0x40,                      // off after 5 ticks of 480
            0x00, 0xFF, 0x2F, 0x00,
        ];
        List<byte> bytes = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0, .. "MTrk"u8.ToArray(), 0, 0, 0, (byte)track.Length, .. track];
        ScoreImportResult result = ScoreImport.FromMidi([.. bytes], "beat", 4);
        Assert.Contains("1:0:5:36:100", result.SequenceText, StringComparison.Ordinal);   // 5 ticks of 480: not stretched
        Assert.Contains("drums.events 1:0:p:0:80", result.SequenceText, StringComparison.Ordinal);   // the bend is kept as an event of the channel
    }

    [Fact]
    public void A_note_over_a_bar_line_is_cut_there_with_a_tie_and_the_file_reads_back_as_a_sequence()
    {
        byte[] track =
        [
            0x8E, 0x08, 0x90, 0x3C, 0x64,                // C4 on at tick 1800 (a bar is 1920 ticks)
            0x82, 0x2C, 0x80, 0x3C, 0x40,                // off 300 ticks later, in bar 2
            0x00, 0xFF, 0x2F, 0x00,
        ];
        List<byte> bytes = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0, .. "MTrk"u8.ToArray(), 0, 0, 0, (byte)track.Length, .. track];
        ScoreImportResult result = ScoreImport.FromMidi([.. bytes], "tie", 4);
        Assert.Contains("1:1800:120:60:100;tie=0", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("2:0:180:60:100;tie=0", result.SequenceText, StringComparison.Ordinal);
        Ample.Audio.Engine.SequenceFile read = Ample.Audio.Engine.SequenceFile.Parse(result.SequenceText);
        Assert.Equal("480", read.Channels[0].Get("ppq"));
        Assert.True(Ample.Language.StructuredMidiNotes.TryParse(read.Patterns[0].Rows[read.Channels[0].Name], 480, out var notes));
        Assert.Equal(2, notes.Count);
    }

    private static string MusicXmlWithPercussion() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <score-partwise version="3.1">
          <part-list>
            <score-part id="P1">
              <part-name>Drums</part-name>
              <score-instrument id="P1-I1"><instrument-name>Snare</instrument-name></score-instrument>
              <score-instrument id="P1-I2"><instrument-name>Kick</instrument-name></score-instrument>
              <midi-instrument id="P1-I1"><midi-channel>10</midi-channel><midi-unpitched>39</midi-unpitched></midi-instrument>
              <midi-instrument id="P1-I2"><midi-channel>10</midi-channel><midi-unpitched>37</midi-unpitched></midi-instrument>
            </score-part>
          </part-list>
          <part id="P1">
            <measure number="1">
              <attributes><divisions>1</divisions></attributes>
              <note>
                <unpitched><display-step>C</display-step><display-octave>5</display-octave></unpitched>
                <instrument id="P1-I2"/>
                <duration>1</duration>
                <type>quarter</type>
              </note>
              <note>
                <unpitched><display-step>G</display-step><display-octave>5</display-octave></unpitched>
                <instrument id="P1-I1"/>
                <duration>1</duration>
                <type>quarter</type>
              </note>
              <note>
                <unpitched><display-step>G</display-step><display-octave>5</display-octave></unpitched>
                <duration>2</duration>
                <type>half</type>
              </note>
            </measure>
          </part>
        </score-partwise>
        """;

    [Fact]
    public void MusicXml_unpitched_percussion_notes_are_mapped_through_the_midi_instrument()
    {
        ScoreImportResult result = ScoreImport.FromMusicXml(MusicXmlWithPercussion(), "beat", 4);
        Assert.Equal(1, result.Parts);
        Assert.Equal(2, result.Notes);
        Assert.Contains("bank=128", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("1:0:1:36:90", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("1:1:1:38:90", result.SequenceText, StringComparison.Ordinal);
        Assert.Contains("without a midi-instrument mapping", string.Join(' ', result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void The_project_log_keeps_one_entry_per_problem_and_can_resolve_it()
    {
        string root = Directory.CreateTempSubdirectory("ample-log-").FullName;
        try
        {
            Assert.True(ProjectLog.Append(root, "missing", "ffmpeg", "install it"));
            Assert.False(ProjectLog.Append(root, "missing", "ffmpeg", "install it"));
            Assert.True(ProjectLog.Append(root, "missing", "python", "install 3.10"));
            Assert.Equal(["python", "ffmpeg"], ProjectLog.List(root).Select(e => e.What));
            Assert.Equal(1, ProjectLog.Resolve(root, "ffmpeg"));
            Assert.Equal(["python"], ProjectLog.List(root).Select(e => e.What));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_environment_check_names_a_library_the_build_lists_but_the_folder_lacks()
    {
        string folder = Directory.CreateTempSubdirectory("ample-env-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Demo.deps.json"), "{\"targets\":{\"t\":{\"Lib/1.0\":{\"runtime\":{\"lib/net9.0/Lib.Missing.dll\":{},\"lib/net9.0/Lib.Here.dll\":{}}}}}}");
            File.WriteAllBytes(Path.Combine(folder, "Lib.Here.dll"), [0]);
            EnvironmentFinding libraries = EnvironmentCheck.Run(folder, null).First(f => f.Name == "program libraries");
            Assert.False(libraries.Ok);
            Assert.Contains("Lib.Missing.dll", libraries.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Lib.Here.dll", libraries.Detail, StringComparison.Ordinal);
            Assert.Contains("whole build folder", libraries.Hint, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
