using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Audio.Midi;
using Ample.Engine.Engine;
using Ample.Engine.Midi;
using Ample.Storage.BlackBox;
using Ample.Core.Tests.Instruments;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "Midi")]
public sealed class MidiTests
{
    [Fact]
    public void Midi_volume_and_expression_controllers_apply_integer_gain_at_their_sample_ticks()
    {
        short[] samples = [12_000, 12_000, 12_000, 12_000, 12_000];
        BuiltInRenderers.ApplyMidiControllerGain(samples, [(0, 7, 64), (2, 11, 0), (4, 11, 127)]);
        Assert.InRange(samples[0], 6_047, 6_048);
        Assert.Equal(samples[0], samples[1]);
        Assert.Equal((short)0, samples[2]);
        Assert.Equal((short)0, samples[3]);
        Assert.Equal(samples[0], samples[4]);
    }

    [Fact]
    public async Task The_pieces_of_a_midi_clip_note_with_the_same_tie_id_continue_one_note()
    {
        BlockNode block = new() { Type = "midi-clip", Name = "clip" };
        block.Parameters.Add(new Parameter("ppq", "120", 0));
        block.Parameters.Add(new Parameter("notes", "\"1:360:120:67:80:0:0:0:0 2:0:120:67:80:0:0:0:0 2:240:120:60:80:0:0:0\"", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-clip"], 44_100, 88_200, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, Path.GetTempPath(), null, null);
        IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(request, CancellationToken.None);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(outputs.Single(o => o.Role == "notes").Data);
        System.Text.Json.JsonElement[] notes = [.. doc.RootElement.GetProperty("notes").EnumerateArray()];
        Assert.Equal(3, notes.Length);
        Assert.False(notes[0].TryGetProperty("tie", out _));
        Assert.True(notes[1].GetProperty("tie").GetBoolean());
        Assert.False(notes[2].TryGetProperty("tie", out _));
    }

    [Fact]
    public void A_note_that_goes_on_over_a_bar_line_is_marked_as_a_continuation_and_a_tempo_mismatch_is_not_silent()
    {
        // the file is at 86.5 BPM (693642 microseconds per quarter), the project at 86 BPM (123069 samples per bar at 44100 Hz)
        SmfFile source = new(0, 120, [new SmfTrack([new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x51, [0x0A, 0xA3, 0x8A])])]);
        BlockNode block = new() { Type = "midi-render", Name = "tie-check" };
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-render"], 44_100, 123_069, 1, 8, new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8 }, new Dictionary<string, byte[]>(), 0, Path.GetTempPath(), null, null);
        HashSet<(int Bar, int Pitch, int Channel)> ties = [];
        var mapped = BuiltInRenderers.MapMidiTempoNotes(request, source, [(2400, 2700, 69, 127, 0, 0, 0)], 120, [], ties);
        Assert.Equal(2, mapped.Count);
        Assert.Equal(mapped[0].Bar + 1, mapped[1].Bar);
        Assert.Equal(0, mapped[1].Beat);
        Assert.Contains((mapped[1].Bar, 69, 0), ties);
        Assert.DoesNotContain((mapped[0].Bar, 69, 0), ties);
        HashSet<(int Bar, int Pitch, int Channel)> none = [];
        BuiltInRenderers.MapMidiTempoNotes(request, source, [(120, 240, 60, 96, 0, 0, 0)], 120, [], none);
        Assert.Empty(none);
    }

    [Fact]
    public void Midi_audio_tempo_map_uses_integer_source_time_across_tempo_changes()
    {
        SmfFile source = new(0, 480,
        [new SmfTrack([
            new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x51, [0x0F, 0x42, 0x40]),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x51, [0x07, 0xA1, 0x20])
        ])]);
        BlockNode block = new() { Type = "midi-render", Name = "tempo-check" };
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-render"], 44_100, 176_400, 1, 4, new HashSet<int> { 1, 2, 3, 4 }, new Dictionary<string, byte[]>(), 0, Path.GetTempPath(), null, null);
        List<string> warnings = [];
        IReadOnlyList<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> mapped = BuiltInRenderers.MapMidiTempoNotes(request, source, [(480, 960, 60, 96, 0, 0, 0)], 480, warnings);
        var note = Assert.Single(mapped);
        Assert.Equal((1, 60, 480L, 240L, 96, 0, 0, 0), note);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Generated_module_tempo_map_uses_integer_sample_positions()
    {
        SmfTempo[] tempos = [new(0, 500_000), new(480, 250_000)];

        Assert.Equal(22_050, BuildEngine.MidiSampleAtTick(480, 480, tempos, 44_100, 176_400, 1, 705_600));
        Assert.Equal(33_075, BuildEngine.MidiSampleAtTick(960, 480, tempos, 44_100, 176_400, 1, 705_600));
    }

    [Fact]
    public void Midi_sustain_pedal_extends_matched_note_offs_for_module_and_audio_paths()
    {
        SmfTrack track = new([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(120, SmfEventKind.ControlChange, 0, 64, 127, null, []),
            new SmfEvent(240, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(300, SmfEventKind.NoteOn, 0, 60, 80, null, []),
            new SmfEvent(360, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.ControlChange, 0, 64, 0, null, [])
        ]);
        MidiNotePairing result = MidiNotePairer.Pair(track, 0);
        Assert.Equal(new long[] { 0, 300 }, result.Notes.Select(n => n.StartTick).ToArray());
        Assert.Equal(new long[] { 480, 480 }, result.Notes.Select(n => n.EndTick).ToArray());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Generated_module_and_export_preserve_sustain_extended_note_length()
    {
        SmfFile source = new(0, 480,
        [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(120, SmfEventKind.ControlChange, 0, 64, 127, null, []),
            new SmfEvent(240, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.ControlChange, 0, 64, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "sustain-fixture");
        AmpleDocument module = Parser.Parse("composition \"midi-test\" { ample 1  tempo 100 master-seed 1 }\n" + ModuleDefinition.Parse(converted.Text).Body, EngineRig.Spec);
        BlockNode clip = Assert.Single(module.Blocks, b => b.Type == "midi-clip");
        Assert.True(StructuredMidiNotes.TryParse(clip.Parameters.Single(p => p.Name == "notes").Value, 480, out IReadOnlyList<StructuredMidiNote> notes));
        Assert.Equal(480L, Assert.Single(notes).LengthTicks);
        SmfFile exported = SmfReader.Parse(MidiModuleExporter.Export(converted.Text, EngineRig.Spec).Bytes, SmfLimits.Standard);
        Assert.Equal(480L, Assert.Single(MidiNotePairer.Pair(exported.Tracks[0], 0).Notes).EndTick);
    }

    [Fact]
    public void Generated_module_routes_nested_mixers_through_the_mix_port()
    {
        SmfTrack[] tracks = Enumerable.Range(0, 17).Select(program => new SmfTrack([
            new SmfEvent(0, SmfEventKind.ProgramChange, 0, program, 0, null, []),
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(120, SmfEventKind.NoteOff, 0, 60, 0, null, [])
        ])).ToArray();
        MidiModuleConversion converted = MidiModuleConverter.Convert(new SmfFile(1, 480, tracks), "nested-mix-fixture");
        AmpleDocument module = Parser.Parse("composition \"midi-test\" { ample 1  tempo 100 master-seed 1 }\n" + ModuleDefinition.Parse(converted.Text).Body, EngineRig.Spec);
        Assert.DoesNotContain(module.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains("from mixer-00-001.audio -> audio1", converted.Text, StringComparison.Ordinal);
        Assert.Contains("from mixer-01-001.audio -> audio1", converted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_module_rejects_unbounded_bar_splitting()
    {
        SmfFile source = new(0, 1,
        [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(400_004, SmfEventKind.NoteOff, 0, 60, 0, null, [])
        ])]);
        Ample.Core.Abstractions.AmpleException error = Assert.Throws<Ample.Core.Abstractions.AmpleException>(() => MidiModuleConverter.Convert(source, "long-note-fixture"));
        Assert.Equal("AMP5504", error.DiagnosticCode);
    }

    [Fact]
    public void Module_facts_parse_generated_event_records_without_substring_matches()
    {
        const string source = "# unrelated note says tempo controllers\n" +
            "# source-event track=0 tick=0 kind=Meta channel=0 data1=0 data2=0 meta=58 payload=04021808\n" +
            "# source-event track=0 tick=1 kind=Meta channel=0 data1=0 data2=0 meta=58 payload=03021808\n" +
            "# source-event track=0 tick=2 kind=ControlChange channel=0 data1=64 data2=127 meta=none payload=\n" +
            "# source-event track=0 tick=3 kind=PitchBend channel=0 data1=0 data2=64 meta=none payload=\n" +
            "# source-event track=0 tick=4 kind=ProgramChange channel=0 data1=60 data2=0 meta=none payload=\n" +
            "# source-event track=0 tick=5 kind=Meta channel=0 data1=0 data2=0 meta=51 payload=07a120\n" +
            "# source-event track=0 tick=6 kind=SystemExclusive channel=0 data1=247 data2=0 meta=none payload=7e7f\n" +
            "# source-event track=0 tick=7 kind=Meta channel=0 data1=0 data2=0 meta=01 payload=74657874";
        IReadOnlyList<string> facts = MidiModuleFacts.FromSource(source).UnsupportedFeatures;
        Assert.Empty(facts);
        Assert.Contains("unclassified preserved MIDI events", MidiModuleFacts.FromSource(source + "\n# source-event unknown").UnsupportedFeatures);
    }

    [Fact]
    public async Task Generated_module_preserves_rendered_notes_and_advanced_events()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(0, 1000,
            [new SmfTrack([
                new SmfEvent(0, SmfEventKind.ProgramChange, 0, 24, 0, null, []),
                new SmfEvent(0, SmfEventKind.PitchBend, 0, 2, 65, null, []),
                new SmfEvent(3990, SmfEventKind.NoteOn, 0, 60, 99, null, []),
                new SmfEvent(4050, SmfEventKind.NoteOff, 0, 60, 0, null, []),
                new SmfEvent(4050, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
            ])]);
            MidiModuleConversion converted = MidiModuleConverter.Convert(source, "generated-fixture");
            ModuleDefinition definition = ModuleDefinition.Parse(converted.Text);
            Assert.Empty(definition.Problems);
            Assert.Contains("# source-event track=0 tick=0 kind=PitchBend", converted.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("cross-bar note ties", MidiModuleFacts.FromSource(converted.Text).UnsupportedFeatures);
            AmpleDocument module = Parser.Parse("composition \"midi-test\" { ample 1  tempo 100 master-seed 1 }\n" + definition.Body, EngineRig.Spec);
            Assert.DoesNotContain(module.Diagnostics, d => d.Severity == Severity.Error);
            BlockNode clip = Assert.Single(module.Blocks, b => b.Type == "midi-clip");
            BlockNode original = new() { Type = "midi", Name = "source" };
            original.Parameters.Add(new Parameter("file", "fixture.mid", 0));
            original.Parameters.Add(new Parameter("track", "all", 0));
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.mid"), SmfWriter.Write(source, SmfLimits.Standard));
            RenderRequest directRequest = new(original, EngineRig.Spec.Templates["midi"], 44_100, 176_400, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, root, null, null);
            RenderRequest moduleRequest = new(clip, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, root, null, null);
            IReadOnlyList<RenderOutput> directOutputs = await BuiltInRenderers.All.Single(r => r.BlockType == "midi").RenderAsync(directRequest, CancellationToken.None);
            IReadOnlyList<RenderOutput> expandedOutputs = await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(moduleRequest, CancellationToken.None);
            using JsonDocument directNotes = JsonDocument.Parse(directOutputs.Single(o => o.Role == "notes").Data);
            using JsonDocument expandedNotes = JsonDocument.Parse(expandedOutputs.Single(o => o.Role == "notes").Data);
            string[] noteFields = ["bar", "pitch", "beat", "beats", "velocity", "tick", "ticks", "ppq", "channel", "bank", "program", "tie"];
            Assert.Equal(
                directNotes.RootElement.GetProperty("notes").EnumerateArray().Select(n => string.Join('|', noteFields.Where(field => n.TryGetProperty(field, out _)).Select(field => n.GetProperty(field).ToString()))),
                expandedNotes.RootElement.GetProperty("notes").EnumerateArray().Select(n => string.Join('|', noteFields.Where(field => n.TryGetProperty(field, out _)).Select(field => n.GetProperty(field).ToString()))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Edited_generated_module_exports_edited_notes_and_retained_events()
    {
        SmfFile source = new(0, 480,
        [new SmfTrack([
            new SmfEvent(0, SmfEventKind.ProgramChange, 0, 24, 0, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 64, 127, null, []),
            new SmfEvent(120, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(360, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(360, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "edited-export-fixture");
        string edited = converted.Text.Replace("1:120:240:60:90:0:0:24", "1:120:240:65:100:0:0:24", StringComparison.Ordinal);
        MidiModuleExport export = MidiModuleExporter.Export(edited, EngineRig.Spec);
        SmfFile regenerated = SmfReader.Parse(export.Bytes, SmfLimits.Standard);
        SmfEvent note = Assert.Single(regenerated.Tracks[0].Events, e => e.Kind == SmfEventKind.NoteOn);
        Assert.Equal(120L, note.Tick);
        Assert.Equal(65, note.Data1);
        Assert.Equal(100, note.Data2);
        Assert.Contains(regenerated.Tracks[0].Events, e => e.Kind == SmfEventKind.ProgramChange && e.Data1 == 24);
        Assert.Contains(regenerated.Tracks[0].Events, e => e.Kind == SmfEventKind.ControlChange && e.Data1 == 64 && e.Data2 == 127);
    }

    [Fact]
    public void Module_export_maps_per_note_expression_to_an_isolated_midi_channel()
    {
        SmfFile source = new(0, 480, [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "expression-export-fixture");
        string entry = converted.Text.Split('\n').Single(line => line.TrimStart().StartsWith("notes ", StringComparison.Ordinal)).Trim()["notes ".Length..];
        string edited = converted.Text.Replace(entry, entry + ";pan=-100;fine=50;rel=2000;cut=30;port=1000", StringComparison.Ordinal);
        Assert.NotEqual(converted.Text, edited);
        SmfEvent[] events = SmfReader.Parse(MidiModuleExporter.Export(edited, EngineRig.Spec).Bytes, SmfLimits.Standard).Tracks[0].Events.ToArray();
        SmfEvent note = Assert.Single(events, e => e.Kind == SmfEventKind.NoteOn);
        Assert.NotEqual(0, note.Channel);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 10 && e.Data2 == 0);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 6 && e.Data2 == 1);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.PitchBend && e.Data2 > 64);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 72);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 74);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 65 && e.Data2 == 127);
        Assert.Contains(events, e => e.Channel == note.Channel && e.Kind == SmfEventKind.ControlChange && e.Data1 == 5);
        Assert.Contains(events, e => e.Kind == SmfEventKind.NoteOff && e.Channel == note.Channel);
    }

    [Fact]
    public void Module_import_reconstructs_note_expression_from_midi_controllers()
    {
        SmfFile source = new(0, 480, [new SmfTrack([
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 10, 0, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 101, 0, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 100, 0, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 6, 1, null, []),
            new SmfEvent(0, SmfEventKind.PitchBend, 0, 0, 96, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 72, 64, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 74, 32, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 65, 127, null, []),
            new SmfEvent(0, SmfEventKind.ControlChange, 0, 5, 64, null, []),
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        string notes = MidiModuleConverter.Convert(source, "expression-import-fixture").Text.Split('\n').Single(line => line.TrimStart().StartsWith("notes ", StringComparison.Ordinal));
        Assert.Contains(";pan=-100", notes, StringComparison.Ordinal);
        Assert.Contains(";fine=50", notes, StringComparison.Ordinal);
        Assert.Contains(";rel=", notes, StringComparison.Ordinal);
        Assert.Contains(";cut=", notes, StringComparison.Ordinal);
        Assert.Contains(";port=", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Slide_exports_as_pitchbend_ramp_and_reimports_as_editable_note()
    {
        SmfFile source = new(0, 480, [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "slide-roundtrip-fixture");
        string text = converted.Text.Replace("notes 1:0:480:60:90:0:0:0:0", "notes 1:0:480:60:90:0:0:0:0 1:120:240:64:90:0:0:0:1;slide=1", StringComparison.Ordinal);
        SmfFile exported = SmfReader.Parse(MidiModuleExporter.Export(text, EngineRig.Spec).Bytes, SmfLimits.Standard);
        Assert.Equal(1, exported.Tracks[0].Events.Count(e => e.Kind == SmfEventKind.NoteOn));
        Assert.Equal(33, exported.Tracks[0].Events.Count(e => e.Kind == SmfEventKind.PitchBend));
        Assert.Contains(exported.Tracks[0].Events, MidiSlideMarker.IsMarker);
        string restored = MidiModuleConverter.Convert(exported, "slide-reimport-fixture").Text;
        Assert.Contains(";slide=1", restored, StringComparison.Ordinal);
        Assert.Contains("1:0:480:60", restored, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_export_keeps_different_expression_values_on_different_channels()
    {
        SmfFile source = new(0, 480, [new SmfTrack([
            new SmfEvent(0, SmfEventKind.ControlChange, 1, 7, 100, null, []),
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 64, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 64, 0, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "channel-export-fixture");
        string[] entries = converted.Text.Split('\n').Single(line => line.TrimStart().StartsWith("notes ", StringComparison.Ordinal)).Trim()["notes ".Length..].Split(' ');
        string edited = converted.Text.Replace(entries[0], entries[0] + ";pan=-100", StringComparison.Ordinal)
            .Replace(entries[1], entries[1] + ";pan=100", StringComparison.Ordinal);
        Assert.NotEqual(converted.Text, edited);
        SmfEvent[] events = SmfReader.Parse(MidiModuleExporter.Export(edited, EngineRig.Spec).Bytes, SmfLimits.Standard).Tracks[0].Events.ToArray();
        SmfEvent[] notes = [.. events.Where(e => e.Kind == SmfEventKind.NoteOn)];
        Assert.Equal(2, notes.Length);
        Assert.NotEqual(notes[0].Channel, notes[1].Channel);
        Assert.DoesNotContain(notes, note => note.Channel is 0 or 1 or 9);
    }

    [Fact]
    public void Module_export_preserves_unmatched_raw_note_events_for_repair()
    {
        SmfFile source = new(0, 480, [new SmfTrack([
            new SmfEvent(120, SmfEventKind.NoteOn, 0, 60, 91, null, []),
            new SmfEvent(240, SmfEventKind.NoteOff, 1, 62, 37, null, []),
            new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "orphan-note-fixture");
        SmfFile regenerated = SmfReader.Parse(MidiModuleExporter.Export(converted.Text, EngineRig.Spec).Bytes, SmfLimits.Standard);
        SmfEvent[] retained = [.. regenerated.Tracks[0].Events.Where(e => e.Kind is SmfEventKind.NoteOn or SmfEventKind.NoteOff)];
        Assert.Collection(retained,
            item => { Assert.Equal(SmfEventKind.NoteOn, item.Kind); Assert.Equal(60, item.Data1); Assert.Equal(91, item.Data2); },
            item => { Assert.Equal(SmfEventKind.NoteOff, item.Kind); Assert.Equal(62, item.Data1); Assert.Equal(37, item.Data2); });
    }

    [Fact]
    public void Module_export_keeps_adjacent_same_pitch_retriggers_separate()
    {
        SmfFile source = new(0, 480,
        [new SmfTrack([
            new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(480, SmfEventKind.NoteOn, 0, 60, 90, null, []),
            new SmfEvent(960, SmfEventKind.NoteOff, 0, 60, 0, null, []),
            new SmfEvent(960, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
        ])]);
        MidiModuleConversion converted = MidiModuleConverter.Convert(source, "retrigger-export-fixture");
        SmfFile regenerated = SmfReader.Parse(MidiModuleExporter.Export(converted.Text, EngineRig.Spec).Bytes, SmfLimits.Standard);
        SmfEvent[] starts = [.. regenerated.Tracks[0].Events.Where(e => e.Kind == SmfEventKind.NoteOn)];
        Assert.Equal(new long[] { 0, 480 }, starts.Select(e => e.Tick).ToArray());
    }

    [Fact]
    public async Task Generated_polyphonic_multitrack_midi_matches_every_converted_clip_note()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(1, 480,
            [
                new SmfTrack([
                    new SmfEvent(0, SmfEventKind.ProgramChange, 0, 24, 0, null, []),
                    new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 100, null, []),
                    new SmfEvent(240, SmfEventKind.NoteOn, 0, 64, 80, null, []),
                    new SmfEvent(720, SmfEventKind.NoteOff, 0, 64, 0, null, []),
                    new SmfEvent(960, SmfEventKind.NoteOff, 0, 60, 0, null, []),
                    new SmfEvent(960, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
                ]),
                new SmfTrack([
                    new SmfEvent(0, SmfEventKind.NoteOn, 9, 36, 110, null, []),
                    new SmfEvent(480, SmfEventKind.NoteOff, 9, 36, 0, null, []),
                    new SmfEvent(480, SmfEventKind.NoteOn, 9, 38, 90, null, []),
                    new SmfEvent(960, SmfEventKind.NoteOff, 9, 38, 0, null, []),
                    new SmfEvent(960, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
                ])
            ]);
            string midiPath = Path.Combine(root, "fixture.mid");
            await File.WriteAllBytesAsync(midiPath, SmfWriter.Write(source, SmfLimits.Standard));
            SmfFile parsed = SmfReader.Parse(await File.ReadAllBytesAsync(midiPath), SmfLimits.Standard);
            MidiModuleConversion conversion = MidiModuleConverter.Convert(parsed, "polyphonic-fixture");
            Assert.Equal(conversion.Text, MidiModuleConverter.Convert(parsed, "polyphonic-fixture").Text);
            Assert.Contains("gm instrument-001-gm-", conversion.Text, StringComparison.Ordinal);
            Assert.Contains("gm instrument-002-gm-", conversion.Text, StringComparison.Ordinal);
            Assert.Contains("  bank 128\n  program 0\n  channel 9", conversion.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            AmpleDocument module = Parser.Parse("composition \"midi-test\" { ample 1  tempo 100 master-seed 1 }\n" + ModuleDefinition.Parse(conversion.Text).Body, EngineRig.Spec);
            Assert.DoesNotContain(module.Diagnostics, d => d.Severity == Severity.Error);
            BlockNode[] clips = [.. module.Blocks.Where(b => b.Type == "midi-clip")];
            Assert.Equal(2, clips.Length);

            BlockNode midi = new() { Type = "midi", Name = "source" };
            midi.Parameters.Add(new Parameter("file", "fixture.mid", 0));
            midi.Parameters.Add(new Parameter("track", "all", 0));
            HashSet<int> bars = [1, 2];
            RenderRequest midiRequest = new(midi, EngineRig.Spec.Templates["midi"], 44_100, 176_400, 1, 2, bars, new Dictionary<string, byte[]>(), 0, root, null, null);
            IBlockRenderer midiRenderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi");
            NoteView[] expected = NoteViews((await midiRenderer.RenderAsync(midiRequest, CancellationToken.None)).Single(o => o.Role == "notes").Data);

            List<NoteView> converted = [];
            IBlockRenderer clipRenderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip");
            foreach (BlockNode clip in clips)
            {
                RenderRequest clipRequest = new(clip, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 2, bars, new Dictionary<string, byte[]>(), 0, root, null, null);
                converted.AddRange(NoteViews((await clipRenderer.RenderAsync(clipRequest, CancellationToken.None)).Single(o => o.Role == "notes").Data));
            }

            Assert.Equal(expected.OrderBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch), converted.OrderBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Module_write_uses_project_command_bus_and_refuses_name_collision()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(0, 120, [new SmfTrack([new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])]);
            SystemClock clock = new();
            using Journal journal = await Journal.OpenAsync(new PhysicalFileSystem(), clock, root, Guid.NewGuid().ToString("D"));
            CommandBus bus = new(journal, new CommandContext(new PhysicalFileSystem(), root, clock), "test");
            await MidiModuleConverter.WriteAsync(source, root, "atomic-module", bus, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(root, "modules", "atomic-module.module")));
            AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => { _ = await MidiModuleConverter.WriteAsync(source, root, "atomic-module", bus, CancellationToken.None); });
            Assert.Equal("AMP5504", error.DiagnosticCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Midi_block_reads_generated_project_file_and_emits_canonical_notes()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(0, 480,
            [
                new SmfTrack([
                    new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 100, null, []),
                    new SmfEvent(480, SmfEventKind.NoteOn, 0, 64, 70, null, []),
                    new SmfEvent(480, SmfEventKind.NoteOff, 0, 60, 0, null, []),
                    new SmfEvent(960, SmfEventKind.NoteOff, 0, 64, 0, null, []),
                    new SmfEvent(960, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
                ])
            ]);
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.mid"), SmfWriter.Write(source, SmfLimits.Standard));
            BlockNode block = new() { Type = "midi", Name = "source" };
            block.Parameters.Add(new Parameter("file", "fixture.mid", 0));
            block.Parameters.Add(new Parameter("track", "0", 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["midi"], 44_100, 176_400, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, root, null, null);
            IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi");
            IReadOnlyList<RenderOutput> outputs = await renderer.RenderAsync(request, CancellationToken.None);
            using JsonDocument output = JsonDocument.Parse(outputs.Single(o => o.Role == "notes").Data);
            JsonElement[] notes = [.. output.RootElement.GetProperty("notes").EnumerateArray()];

            Assert.Equal(2, notes.Length);
            Assert.Equal(60, notes[0].GetProperty("pitch").GetInt32());
            Assert.Equal(0, notes[0].GetProperty("beat").GetDouble());
            Assert.Equal(1, notes[0].GetProperty("beats").GetDouble());
            Assert.Equal(100, notes[0].GetProperty("velocity").GetInt32());
            Assert.Equal(64, notes[1].GetProperty("pitch").GetInt32());
            Assert.Equal(1, notes[1].GetProperty("beat").GetDouble());
            Assert.Equal(1, notes[1].GetProperty("beats").GetDouble());
            Assert.Equal(70, notes[1].GetProperty("velocity").GetInt32());
            BlockNode clip = new() { Type = "midi-clip", Name = "clip" };
            clip.Parameters.Add(new Parameter("ppq", "480", 0));
            clip.Parameters.Add(new Parameter("notes", "1:0:480:60:100:0:0:0:0 1:480:480:64:70:0:0:0:1", 0));
            RenderRequest clipRequest = request with { Block = clip, Template = EngineRig.Spec.Templates["midi-clip"] };
            using JsonDocument clipOutput = JsonDocument.Parse((await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(clipRequest, CancellationToken.None)).Single().Data);
            Assert.Equal(output.RootElement.GetProperty("notes").GetRawText(), clipOutput.RootElement.GetProperty("notes").GetRawText());
            Assert.Contains("follows the song timeline", Encoding.UTF8.GetString(outputs.Single(o => o.Role == "warnings").Data), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Midi_file_changes_invalidate_the_cached_block_take()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        string path = Path.Combine(rig.ProjectRoot, "fixture.mid");
        string bankFolder = Path.Combine(rig.ProjectRoot, "audio-packs", "licensed", "musescore-general");
        Directory.CreateDirectory(bankFolder);
        byte[] sf2 = Sf2Fixture.Build([("sample", 512, (32u, 480u), 60)], [("instrument", [new Sf2Fixture.Zone((Sf2Fixture.SampleId, 0))])], [("piano", 0, 0, [new Sf2Fixture.Zone((Sf2Fixture.Instrument, 0))])]);
        await File.WriteAllBytesAsync(Path.Combine(bankFolder, "MuseScore_General.sf2"), sf2);
        static SmfFile WithPitch(int pitch) => new(0, 480, [new SmfTrack([new SmfEvent(0, SmfEventKind.NoteOn, 0, pitch, 100, null, []), new SmfEvent(480, SmfEventKind.NoteOff, 0, pitch, 0, null, []), new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])]);
        await File.WriteAllBytesAsync(path, SmfWriter.Write(WithPitch(60), SmfLimits.Standard));
        AmpleDocument song = EngineRig.Parse("composition \"midi-cache\" { ample 1  tempo 92 master-seed 1 }\nmidi source {\n  file \"fixture.mid\"\n  track all\n  bars 1\n}\n");

        BuildReport first = await rig.Engine.BuildAsync(song);
        Assert.Equal("rendered", first.Blocks.Single(b => b.Block == "source").Status);
        await File.WriteAllBytesAsync(path, SmfWriter.Write(WithPitch(64), SmfLimits.Standard));
        BuildReport changed = await rig.Engine.BuildAsync(song);
        Assert.Equal("rendered", changed.Blocks.Single(b => b.Block == "source").Status);
    }

    [Fact]
    public void Midi_block_rejects_project_path_escapes()
    {
        BlockNode block = new() { Type = "midi", Name = "source" };
        block.Parameters.Add(new Parameter("file", "../outside.mid", 0));
        AmpleException error = Assert.Throws<AmpleException>(() => BuiltInRenderers.MidiFile(Path.GetTempPath(), block));
        Assert.Equal("AMP5201", error.DiagnosticCode);
    }

    [Fact]
    public async Task Midi_block_filters_tracks_and_reports_unsupported_features()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(1, 120,
            [
                new SmfTrack([new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x51, [3, 13, 64]), new SmfEvent(0, SmfEventKind.ControlChange, 0, 1, 2, null, []), new SmfEvent(120, SmfEventKind.Meta, 0, 0, 0, 0x51, [7, 161, 32]), new SmfEvent(120, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])]),
                new SmfTrack([new SmfEvent(0, SmfEventKind.NoteOn, 0, 67, 88, null, []), new SmfEvent(120, SmfEventKind.NoteOff, 0, 67, 0, null, []), new SmfEvent(120, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])
            ]);
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.mid"), SmfWriter.Write(source, SmfLimits.Standard));
            BlockNode block = new() { Type = "midi", Name = "source" };
            block.Parameters.Add(new Parameter("file", "fixture.mid", 0));
            block.Parameters.Add(new Parameter("track", "1", 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["midi"], 44_100, 176_400, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 0, root, null, null);
            IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "midi").RenderAsync(request, CancellationToken.None);
            using JsonDocument output = JsonDocument.Parse(outputs.Single(o => o.Role == "notes").Data);
            Assert.Equal(67, Assert.Single(output.RootElement.GetProperty("notes").EnumerateArray()).GetProperty("pitch").GetInt32());
            Assert.Contains("follows the song timeline", Encoding.UTF8.GetString(outputs.Single(o => o.Role == "warnings").Data), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Midi_block_rejects_invalid_track_and_preserves_non_decimal_ticks()
    {
        string root = Path.Combine(Path.GetTempPath(), "ample-midi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SmfFile source = new(0, 7,
            [new SmfTrack([
                new SmfEvent(1, SmfEventKind.NoteOn, 0, 60, 80, null, []),
                new SmfEvent(2, SmfEventKind.NoteOff, 0, 60, 0, null, []),
                new SmfEvent(2, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
            ])]);
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.mid"), SmfWriter.Write(source, SmfLimits.Standard));
            BlockNode block = new() { Type = "midi", Name = "source" };
            block.Parameters.Add(new Parameter("file", "fixture.mid", 0));
            block.Parameters.Add(new Parameter("track", "4", 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["midi"], 44_100, 176_400, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 0, root, null, null);
            IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi");
            AmpleException trackError = await Assert.ThrowsAsync<AmpleException>(async () => { _ = await renderer.RenderAsync(request, CancellationToken.None); });
            Assert.Equal("AMP5503", trackError.DiagnosticCode);

            block.Parameters[0] = new Parameter("file", "missing.mid", 0);
            AmpleException missing = await Assert.ThrowsAsync<AmpleException>(async () => { _ = await renderer.RenderAsync(request with { Block = block }, CancellationToken.None); });
            Assert.Equal("AMP5501", missing.DiagnosticCode);
            await File.WriteAllBytesAsync(Path.Combine(root, "invalid.mid"), [0x4D, 0x54, 0x68, 0x64]);
            block.Parameters[0] = new Parameter("file", "invalid.mid", 0);
            AmpleException invalid = await Assert.ThrowsAsync<AmpleException>(async () => { _ = await renderer.RenderAsync(request with { Block = block }, CancellationToken.None); });
            Assert.Equal("AMP5501", invalid.DiagnosticCode);

            block.Parameters[1] = new Parameter("track", "all", 0);
            block.Parameters[0] = new Parameter("file", "fixture.mid", 0);
            byte[] noteBytes = (await renderer.RenderAsync(request with { Block = block }, CancellationToken.None)).Single(o => o.Role == "notes").Data;
            using JsonDocument notes = JsonDocument.Parse(noteBytes);
            JsonElement note = Assert.Single(notes.RootElement.GetProperty("notes").EnumerateArray());
            Assert.Equal(1, note.GetProperty("tick").GetInt64());
            Assert.Equal(1, note.GetProperty("ticks").GetInt64());
            Assert.Equal(7, note.GetProperty("ppq").GetInt32());

            MidiModuleConversion conversion = MidiModuleConverter.Convert(source, "tick-fixture");
            Assert.Contains("1:1:1:60:80", conversion.Text, StringComparison.Ordinal);
            AmpleDocument module = Parser.Parse("composition \"midi-test\" { ample 1  tempo 100 master-seed 1 }\n" + ModuleDefinition.Parse(conversion.Text).Body, EngineRig.Spec);
            Assert.DoesNotContain(module.Diagnostics, d => d.Severity == Severity.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Midi_template_exposes_project_file_track_and_bar_contract()
    {
        TemplateSpec template = EngineRig.Spec.Templates["midi"];
        Assert.Equal("midi", Assert.Single(template.Outputs, p => p.Name == "notes").Type);
        Assert.Equal("midi-sequence", Assert.Single(template.Outputs, p => p.Name == "sequence").Type);
        Assert.Equal("audio", Assert.Single(template.Outputs, p => p.Name == "audio").Type);
        Assert.Equal("midi-render", EngineRig.Spec.Templates["midi-render"].Id);
        Assert.Contains(".mid", template.ParameterSpecs["file"].Accept!);
        Assert.Equal("8", template.Parameters["bars"]);
        AmpleDocument invalid = Parser.Parse("composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi source {\n  track first\n}\n", EngineRig.Spec);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "AMP2015");
    }

    [Fact]
    public async Task Structured_notes_render_polyphony_with_integer_microbeat_precision()
    {
        BlockNode block = new() { Type = "midi-clip", Name = "clip" };
        block.Parameters.Add(new Parameter("notes", "1:0.125:0.5:60:99 1:0.125:0.25:64:80 2:3.999999:0.000001:72:1", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, string.Empty, null, null);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip");

        using JsonDocument output = JsonDocument.Parse((await renderer.RenderAsync(request, CancellationToken.None)).Single().Data);
        JsonElement[] notes = [.. output.RootElement.GetProperty("notes").EnumerateArray()];

        Assert.Equal(3, notes.Length);
        Assert.Equal(60, notes[0].GetProperty("pitch").GetInt32());
        Assert.Equal(0.125, notes[0].GetProperty("beat").GetDouble());
        Assert.Equal(0.5, notes[0].GetProperty("beats").GetDouble());
        Assert.Equal(99, notes[0].GetProperty("velocity").GetInt32());
        Assert.Equal(64, notes[1].GetProperty("pitch").GetInt32());
        Assert.Equal(72, notes[2].GetProperty("pitch").GetInt32());
        Assert.Equal(3.999999, notes[2].GetProperty("beat").GetDouble());
        Assert.Equal(0.000001, notes[2].GetProperty("beats").GetDouble());
    }

    [Fact]
    public async Task Piano_roll_bar_segments_continue_the_voice_without_forcing_a_GM_program()
    {
        BlockNode block = new() { Type = "midi-clip", Name = "clip" };
        block.Parameters.Add(new Parameter("notes", "1:3:1:60:96;tie=4 2:0:4:60:96;tie=4", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 2, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 0, string.Empty, null, null);
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip");
        using JsonDocument output = JsonDocument.Parse((await renderer.RenderAsync(request, CancellationToken.None)).Single().Data);
        JsonElement[] notes = [.. output.RootElement.GetProperty("notes").EnumerateArray()];
        Assert.Equal(2, notes.Length);
        Assert.False(notes[0].TryGetProperty("tie", out _));
        Assert.True(notes[1].GetProperty("tie").GetBoolean());
        Assert.All(notes, note => { Assert.False(note.TryGetProperty("channel", out _)); Assert.False(note.TryGetProperty("program", out _)); });
    }

    [Fact]
    public async Task Legacy_chords_and_melody_keep_their_midi_note_output()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip");
        RenderRequest Request(BlockNode block) => new(block, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 0, string.Empty, null, null);

        BlockNode chords = new() { Type = "midi-clip", Name = "chords" };
        chords.Parameters.Add(new Parameter("chords", "Am", 0));
        using JsonDocument chordOutput = JsonDocument.Parse((await renderer.RenderAsync(Request(chords), CancellationToken.None)).Single().Data);
        JsonElement[] chordNotes = [.. chordOutput.RootElement.GetProperty("notes").EnumerateArray()];
        Assert.Equal([57, 60, 64], chordNotes.Select(n => n.GetProperty("pitch").GetInt32()));
        Assert.All(chordNotes, note => Assert.Equal(4, note.GetProperty("beats").GetInt32()));

        BlockNode melody = new() { Type = "midi-clip", Name = "melody" };
        melody.Parameters.Add(new Parameter("melody", "c4:1:110 r:1 f#4:2", 0));
        using JsonDocument melodyOutput = JsonDocument.Parse((await renderer.RenderAsync(Request(melody), CancellationToken.None)).Single().Data);
        JsonElement[] melodyNotes = [.. melodyOutput.RootElement.GetProperty("notes").EnumerateArray()];
        Assert.Equal([60, 66], melodyNotes.Select(n => n.GetProperty("pitch").GetInt32()));
        Assert.Equal(110, melodyNotes[0].GetProperty("velocity").GetInt32());
        Assert.Equal(0, melodyNotes[0].GetProperty("beat").GetInt32());
        Assert.Equal(2, melodyNotes[1].GetProperty("beat").GetInt32());
    }

    [Fact]
    public void Structured_notes_parse_format_complete_and_validate()
    {
        const string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip clip {\n  notes 1:0.125:0.5:60:99 1:0.125:0.25:64:80\n}\n";
        AmpleDocument document = Parser.Parse(song, EngineRig.Spec);
        string formatted = Formatter.Format(song, EngineRig.Spec);
        IReadOnlyList<CompletionItem> completions = Queries.CompletionAt(Parser.Parse("composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip clip {\n  \n}\n", EngineRig.Spec), EngineRig.Spec, 3, 3);

        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "AMP2012");
        Assert.Contains(document.Blocks.Single().Parameters, p => p.Name == "notes" && p.Value.Contains("1:0.125:0.5:60:99", StringComparison.Ordinal));
        Assert.Equal(formatted, Formatter.Format(formatted, EngineRig.Spec));
        Assert.Contains(completions, c => c.Label == "notes");

        AmpleDocument invalid = Parser.Parse(song.Replace("1:0.125:0.5:60:99", "1:4:1:60:99", StringComparison.Ordinal), EngineRig.Spec);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "AMP2012");
    }

    [Fact]
    public void Structured_note_parser_rejects_invalid_ranges_precision_and_limits()
    {
        Assert.False(StructuredMidiNotes.TryParse("1:4:1:60:90", out _));
        Assert.False(StructuredMidiNotes.TryParse("1:0:4.000001:60:90", out _));
        Assert.False(StructuredMidiNotes.TryParse("1:0.0000001:1:60:90", out _));
        Assert.False(StructuredMidiNotes.TryParse("1:0:1:128:90", out _));
        Assert.False(StructuredMidiNotes.TryParse(new string('1', StructuredMidiNotes.MaximumTextLength + 1), out _));
        Assert.True(StructuredMidiNotes.TryParse(string.Empty, out IReadOnlyList<StructuredMidiNote> empty));
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Explicitly_empty_structured_notes_render_silence_instead_of_legacy_chords()
    {
        BlockNode block = new() { Type = "midi-clip", Name = "empty" };
        block.Parameters.Add(new Parameter("notes", "\"\"", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["midi-clip"], 44_100, 176_400, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 0, string.Empty, null, null);
        byte[] data = (await BuiltInRenderers.All.Single(r => r.BlockType == "midi-clip").RenderAsync(request, CancellationToken.None)).Single().Data;
        using JsonDocument output = JsonDocument.Parse(data);
        Assert.Empty(output.RootElement.GetProperty("notes").EnumerateArray());
    }

    [Fact]
    public void Format_zero_round_trips_channel_meta_and_system_exclusive_events()
    {
        SmfFile file = new(0, 480,
        [
            new SmfTrack([
                new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x51, [0x07, 0xA1, 0x20]),
                new SmfEvent(0, SmfEventKind.ProgramChange, 2, 10, 0, null, []),
                new SmfEvent(0, SmfEventKind.NoteOn, 2, 60, 100, null, []),
                new SmfEvent(240, SmfEventKind.NoteOff, 2, 60, 64, null, []),
                new SmfEvent(240, SmfEventKind.ControlChange, 2, 64, 127, null, []),
                new SmfEvent(480, SmfEventKind.PitchBend, 2, 0, 64, null, []),
                new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x58, [4, 2, 24, 8]),
                new SmfEvent(480, SmfEventKind.SystemExclusive, 0, 0xF0, 0, null, [1, 2, 0xF7]),
                new SmfEvent(480, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])
            ])
        ]);
        byte[] bytes = SmfWriter.Write(file, SmfLimits.Standard);
        SmfFile parsed = SmfReader.Parse(bytes, SmfLimits.Standard);
        Assert.Equal(0, parsed.Format);
        Assert.Equal(480, parsed.TicksPerQuarter);
        Assert.Equal(file.Tracks[0].Events.Count, parsed.Tracks[0].Events.Count);
        Assert.Equal(bytes, SmfWriter.Write(parsed, SmfLimits.Standard));
        Assert.Contains(parsed.Tracks[0].Events, e => e.Kind == SmfEventKind.ControlChange && e.Data1 == 64 && e.Tick == 240);
        Assert.Equal(500_000, Assert.Single(parsed.TempoMap).MicrosecondsPerQuarter);
        Assert.Equal(new SmfTimeSignature(480, 4, 2, 24, 8), Assert.Single(parsed.TimeSignatures));
    }

    [Fact]
    public void Format_one_keeps_tracks_and_same_tick_order()
    {
        SmfFile file = new(1, 96,
        [
            new SmfTrack([new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x03, [65]), new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])]),
            new SmfTrack([new SmfEvent(0, SmfEventKind.NoteOn, 0, 64, 70, null, []), new SmfEvent(0, SmfEventKind.NoteOff, 0, 64, 0, null, []), new SmfEvent(0, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])
        ]);
        SmfFile parsed = SmfReader.Parse(SmfWriter.Write(file, SmfLimits.Standard), SmfLimits.Standard);
        Assert.Equal(2, parsed.Tracks.Count);
        Assert.Equal(SmfEventKind.NoteOn, parsed.Tracks[1].Events[0].Kind);
        Assert.Equal(SmfEventKind.NoteOff, parsed.Tracks[1].Events[1].Kind);
    }

    [Fact]
    public void Velocity_zero_note_on_is_normalized_to_note_off()
    {
        SmfEvent note = Assert.Single(SmfReader.Parse(TrackFile([0, 0x90, 60, 0, 0, 0xFF, 0x2F, 0]), SmfLimits.Standard).Tracks[0].Events, e => e.Kind != SmfEventKind.Meta);
        Assert.Equal(SmfEventKind.NoteOff, note.Kind);
        Assert.Equal(60, note.Data1);
        Assert.Equal(0, note.Data2);
    }

    [Fact]
    public void Running_status_is_accepted_and_cleared_by_meta_events()
    {
        byte[] valid = TrackFile([0, 0x90, 60, 90, 0, 61, 91, 0, 0xFF, 0x2F, 0]);
        byte[] invalid = TrackFile([0, 0x90, 60, 90, 0, 0xFF, 1, 0, 0, 61, 91, 0, 0xFF, 0x2F, 0]);
        Assert.Equal(3, SmfReader.Parse(valid, SmfLimits.Standard).Tracks[0].Events.Count);
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse(invalid, SmfLimits.Standard));
    }

    [Fact]
    public void Every_truncated_prefix_is_rejected()
    {
        byte[] valid = SmfWriter.Write(new SmfFile(0, 120,
        [
            new SmfTrack([new SmfEvent(0, SmfEventKind.NoteOn, 0, 60, 80, null, []), new SmfEvent(120, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])])
        ]), SmfLimits.Standard);
        for (int length = 0; length < valid.Length; length++)
            Assert.Throws<SmfFormatException>(() => SmfReader.Parse(valid.AsSpan(0, length), SmfLimits.Standard));
    }

    [Fact]
    public void Five_byte_variable_length_quantity_is_rejected()
    {
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse(TrackFile([0x81, 0x80, 0x80, 0x80, 0, 0, 0xFF, 0x2F, 0]), SmfLimits.Standard));
    }

    [Fact]
    public void Track_event_count_limit_is_enforced()
    {
        byte[] bytes = TrackFile([0, 0x90, 60, 90, 0, 0xFF, 0x2F, 0]);
        SmfLimits limits = new(1024, 2, 1, 2, 64);
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse(bytes, limits));
    }

    [Fact]
    public void Invalid_division_and_file_trailing_data_are_rejected()
    {
        byte[] valid = TrackFile([0, 0xFF, 0x2F, 0]);
        byte[] smpte = [.. valid];
        BinaryPrimitives.WriteUInt16BigEndian(smpte.AsSpan(12, 2), 0xE728);
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse(smpte, SmfLimits.Standard));
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse([.. valid, 0], SmfLimits.Standard));
    }

    [Fact]
    public void Malformed_tempo_meta_event_is_rejected()
    {
        Assert.Throws<SmfFormatException>(() => SmfReader.Parse(TrackFile([0, 0xFF, 0x51, 2, 1, 2, 0, 0xFF, 0x2F, 0]), SmfLimits.Standard));
    }

    private sealed record NoteView(int Bar, int Pitch, double Beat, double Beats, int Velocity, long Tick, long Ticks, int Ppq, int Channel, int Bank, int Program);

    private static NoteView[] NoteViews(byte[] data)
    {
        using JsonDocument document = JsonDocument.Parse(data);
        return [.. document.RootElement.GetProperty("notes").EnumerateArray().Select(note => new NoteView(note.GetProperty("bar").GetInt32(), note.GetProperty("pitch").GetInt32(), note.GetProperty("beat").GetDouble(), note.GetProperty("beats").GetDouble(), note.GetProperty("velocity").GetInt32(), note.GetProperty("tick").GetInt64(), note.GetProperty("ticks").GetInt64(), note.GetProperty("ppq").GetInt32(), note.GetProperty("channel").GetInt32(), note.GetProperty("bank").GetInt32(), note.GetProperty("program").GetInt32()))];
    }

    private static byte[] TrackFile(byte[] body)
    {
        byte[] result = new byte[22 + body.Length];
        "MThd"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12, 2), 480);
        "MTrk"u8.CopyTo(result.AsSpan(14, 4));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(18, 4), checked((uint)body.Length));
        body.CopyTo(result, 22);
        return result;
    }
}
