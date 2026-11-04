using System.CommandLine;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Audio.Instruments;
using Ample.Audio.Midi;
using Ample.Engine.Midi;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

internal static class MidiCommands
{
    internal static Command Create(TextWriter output)
    {
        Argument<string> file = new("file") { Description = "Input .mid or .midi file." };
        Option<string?> project = new("--project") { Description = "Project folder (default: current folder)." };
        Option<string?> name = new("--output") { Description = "Generated module name in modules/ (letters, digits, _ and -)." };
        Command convert = new("convert", "Converts an SMF file into a project module.") { file, project, name };
        convert.SetAction(async (parse, token) =>
        {
            try
            {
                string root = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
                string input = Path.GetFullPath(parse.GetValue(file)!);
                string moduleName = parse.GetValue(name) ?? Path.GetFileNameWithoutExtension(input);
                SmfFile smf = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(input), SmfLimits.Standard);
                SystemClock clock = new();
                PhysicalFileSystem fs = new();
                using OpenProject opened = await ProjectStore.OpenAsync(fs, clock, root, readOnly: false, token);
                using Journal journal = await Journal.OpenAsync(fs, clock, root, opened.Manifest.Id.ToString("D"), cancellationToken: token);
                MidiModuleConversion result = await MidiModuleConverter.WriteAsync(smf, root, moduleName, new CommandBus(journal, new CommandContext(fs, root, clock), "cli"), token);
                output.WriteLine($"modules/{result.Name}.module");
                foreach (string warning in result.Warnings) output.WriteLine($"warning: {warning}");
                return 0;
            }
            catch (Exception ex) when (ex is AmpleException or SmfFormatException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(ex is AmpleException ample ? $"{ample.DiagnosticCode}: {ample.Message}" : ex.Message);
                return 1;
            }
        });
        Option<string?> soundFontFile = new("--file") { Description = "Project-relative SF2 file or pack:musescore-general." };
        Command soundFontCheck = new("soundfont-check", "Checks GM preset and percussion coverage in an SF2 bank.") { soundFontFile };
        soundFontCheck.SetAction(parse =>
        {
            try
            {
                string root = Directory.GetCurrentDirectory();
                string path = SoundFontAsset.Resolve(root, parse.GetValue(soundFontFile) ?? SoundFontAsset.GeneralMidi);
                (int programs, int drums) = SoundFontAsset.VerifyGeneralMidi(path);
                output.WriteLine($"GM coverage: {programs} melodic programs, {drums} percussion kits.");
                return 0;
            }
            catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(ex is AmpleException ample ? $"{ample.DiagnosticCode}: {ample.Message}" : ex.Message);
                return 1;
            }
        });
        Argument<string> verifyInput = new("file") { Description = "Input .mid or .midi file to parse and convert in memory." };
        Command verify = new("verify-conversion", "Checks that a MIDI file parses and converts to a valid module without writing project files.") { verifyInput };
        verify.SetAction(parse =>
        {
            try
            {
                string path = Path.GetFullPath(parse.GetValue(verifyInput)!);
                SmfFile smf = SmfReader.Parse(BuiltInRenderers.ReadMidiFile(path), SmfLimits.Standard);
                MidiModuleConversion result = MidiModuleConverter.Convert(smf, "verification");
                ModuleDefinition definition = ModuleDefinition.Parse(result.Text);
                if (definition.Problems.Count > 0) throw new AmpleException("AMP5504", "Converted module structure is invalid.");
                SpecData spec = SpecRoot.Find(Directory.GetCurrentDirectory()).LoadSpecData();
                AmpleDocument module = Parser.Parse("composition \"midi-verification\" { ample 1  tempo 100 master-seed 1 }\n" + definition.Body, spec);
                int errors = module.Diagnostics.Count(d => d.Severity == Severity.Error);
                if (errors > 0) throw new AmpleException("AMP5504", "Converted module has language validation errors.");
                int noteOns = smf.Tracks.Sum(t => t.Events.Count(e => e.Kind == SmfEventKind.NoteOn));
                int clips = module.Blocks.Count(b => b.Type == "midi-clip");
                int noteEntries = 0;
                foreach (BlockNode clip in module.Blocks.Where(b => b.Type == "midi-clip"))
                {
                    int ppq = int.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, out int parsedPpq) ? parsedPpq : StructuredMidiNotes.MicrobeatsPerBeat;
                    if (!StructuredMidiNotes.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty, ppq, out IReadOnlyList<StructuredMidiNote> notes)) throw new AmpleException("AMP5504", "Converted module contains invalid structured notes.");
                    noteEntries += notes.Count;
                }
                MidiSegment[] sourceNotes = SourceSegments(smf);
                MidiSegment[] convertedNotes = ModuleSegments(module);
                if (!sourceNotes.SequenceEqual(convertedNotes)) throw new AmpleException("AMP5504", "Converted module note segments do not match the source MIDI.");
                MidiModuleExport exported = MidiModuleExporter.Export(result.Text, spec);
                SmfFile regenerated = SmfReader.Parse(exported.Bytes, SmfLimits.Standard);
                MidiSegment[] exportedNotes = SourceSegments(regenerated);
                if (!sourceNotes.SequenceEqual(exportedNotes) || !RetainsNonNoteEvents(smf, regenerated)) throw new AmpleException("AMP5504", "Generated MIDI does not retain the source notes and supported non-note events.");
                int retained = smf.Tracks.Sum(t => t.Events.Count(e => e.Kind is not (SmfEventKind.NoteOn or SmfEventKind.NoteOff) && !(e.Kind == SmfEventKind.Meta && e.MetaType == 0x2F)));
                output.WriteLine($"MIDI conversion valid: {smf.Tracks.Count} tracks, PPQ {smf.TicksPerQuarter}, {noteOns} note-ons, {clips} clips, {noteEntries} note segments, exact matched-note parity, {retained} supported non-note source events retained, {result.Warnings.Count + exported.Warnings.Count} conversion/export notes.");
                return 0;
            }
            catch (Exception ex) when (ex is AmpleException or SmfFormatException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine(ex is AmpleException ample ? $"{ample.DiagnosticCode}: {ample.Message}" : ex.Message);
                return 1;
            }
        });
        return new Command("midi", "MIDI file conversion commands.") { convert, soundFontCheck, verify };
    }

    private sealed record MidiSegment(int Track, int Bar, long Beat, long Length, int Pitch, int Velocity, int Channel, int Bank, int Program);

    private static MidiSegment[] SourceSegments(SmfFile file)
    {
        List<MidiSegment> notes = [];
        long ticksPerBar = 4L * file.TicksPerQuarter;
        for (int trackIndex = 0; trackIndex < file.Tracks.Count; trackIndex++)
        {
            foreach (MidiNoteSpan note in MidiNotePairer.Pair(file.Tracks[trackIndex], trackIndex).Notes)
            {
                long firstBar = note.StartTick / ticksPerBar, lastBar = (note.EndTick - 1) / ticksPerBar;
                for (long bar = firstBar; bar <= lastBar; bar++)
                {
                    long barStart = bar * ticksPerBar, end = Math.Min(note.EndTick, barStart + ticksPerBar);
                    notes.Add(new MidiSegment(trackIndex, checked((int)bar + 1), Math.Max(0, note.StartTick - barStart), end - Math.Max(note.StartTick, barStart), note.Pitch, note.Velocity, note.Channel, note.Bank, note.Program));
                }
            }
        }
        return [.. notes.OrderBy(n => n.Track).ThenBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch).ThenBy(n => n.Channel)];
    }

    private static MidiSegment[] ModuleSegments(AmpleDocument module)
    {
        List<MidiSegment> notes = [];
        int track = 0;
        foreach (BlockNode clip in module.Blocks.Where(b => b.Type == "midi-clip"))
        {
            int ppq = int.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, out int parsedPpq) ? parsedPpq : StructuredMidiNotes.MicrobeatsPerBeat;
            if (!StructuredMidiNotes.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty, ppq, out IReadOnlyList<StructuredMidiNote> entries)) throw new AmpleException("AMP5504", "Converted module contains invalid structured notes.");
            notes.AddRange(entries.Select(n => new MidiSegment(track, n.Bar, n.BeatTicks, n.LengthTicks, n.Pitch, n.Velocity, n.Channel, n.Bank, n.Program)));
            track++;
        }
        return [.. notes.OrderBy(n => n.Track).ThenBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch).ThenBy(n => n.Channel)];
    }

    private static bool RetainsNonNoteEvents(SmfFile source, SmfFile regenerated)
    {
        if (source.Tracks.Count != regenerated.Tracks.Count) return false;
        for (int track = 0; track < source.Tracks.Count; track++)
        {
            SmfEvent[] candidates = [.. regenerated.Tracks[track].Events.Where(e => e.Kind is not (SmfEventKind.NoteOn or SmfEventKind.NoteOff) && !(e.Kind == SmfEventKind.Meta && e.MetaType == 0x2F))];
            bool[] used = new bool[candidates.Length];
            foreach (SmfEvent expected in source.Tracks[track].Events.Where(e => e.Kind is not (SmfEventKind.NoteOn or SmfEventKind.NoteOff) && !(e.Kind == SmfEventKind.Meta && e.MetaType == 0x2F)))
            {
                int found = -1;
                for (int i = 0; i < candidates.Length; i++) if (!used[i] && SameEvent(expected, candidates[i])) { found = i; break; }
                if (found < 0) return false;
                used[found] = true;
            }
        }
        return true;
    }

    private static bool SameEvent(SmfEvent left, SmfEvent right) => left.Tick == right.Tick && left.Kind == right.Kind && left.Channel == right.Channel && left.Data1 == right.Data1 && left.Data2 == right.Data2 && left.MetaType == right.MetaType && left.Payload.AsSpan().SequenceEqual(right.Payload);
}
