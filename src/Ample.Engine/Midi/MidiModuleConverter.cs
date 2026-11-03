using System.Globalization;
using System.Text;
using Ample.Audio.IO;
using Ample.Audio.Midi;
using Ample.Storage.BlackBox;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Engine.Midi;

/// <summary>The generated module text and conversion notes for one SMF.</summary>
/// <param name="Name">A path-safe module name.</param>
/// <param name="Text">A deterministic module source file.</param>
/// <param name="Warnings">Conversion limits or fallback instrument mappings.</param>
public sealed record MidiModuleConversion(string Name, string Text, IReadOnlyList<string> Warnings);

/// <summary>Converts bounded MIDI event records into an editable AMPLE module without writing project files.</summary>
public static class MidiModuleConverter
{
    private const int MaximumModuleCharacters = 64 * 1024 * 1024;

    /// <summary>Creates one note clip per track, instrument routing, a mixer graph and comments preserving source events.</summary>
    /// <param name="file">Parsed Standard MIDI File.</param>
    /// <param name="name">Requested module name; only letters, digits, hyphen and underscore are accepted.</param>
    /// <returns>The module and deterministic warnings.</returns>
    public static MidiModuleConversion Convert(SmfFile file, string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new AmpleException("AMP5504", "The generated module name must contain 1 to 64 letters, digits, hyphens or underscores.");
        if (file.Tracks.Count is < 1 or > 256 || file.TicksPerQuarter is < 1 or > 0x7FFF)
            throw new AmpleException("AMP5504", "The MIDI model exceeds converter track or timing bounds.");

        StringBuilder source = new();
        List<string> warnings = [];
        // the bar is the beats of the meter of the file (its first time signature; 4/4 without one): the notes are cut at those bar lines, and a song that uses the module must say the same meter
        SmfTimeSignature[] signatures = [.. file.TimeSignatures.OrderBy(t => t.Tick)];
        double beatsPerBar = signatures.Length == 0 ? 4.0 : signatures[0].Numerator * 4.0 / (1 << signatures[0].DenominatorPower);
        long barTicks = Math.Max(1, (long)Math.Round(beatsPerBar * file.TicksPerQuarter));
        string meterText = signatures.Length == 0 ? "4/4" : signatures[0].Numerator.ToString(CultureInfo.InvariantCulture) + "/" + (1 << signatures[0].DenominatorPower).ToString(CultureInfo.InvariantCulture);
        string meterFact = Math.Abs(beatsPerBar - 4.0) > 1e-9 ? " meter=" + meterText : string.Empty;
        if (signatures.Any(t => Math.Abs(t.Numerator * 4.0 / (1 << t.DenominatorPower) - beatsPerBar) > 1e-9))
            warnings.Add($"the file changes meter; one meter is used for the whole module ({meterText}, the first), so the bars after a change are cut or padded");
        if (Math.Abs(beatsPerBar - 4.0) > 1e-9)
            warnings.Add($"the file is in {meterText}: the song that uses this module must say meter {meterText} on its composition line");
        List<string> instruments = [];
        int totalNotes = 0;
        source.Append("module ").Append(name).AppendLine(" version 1.0");
        source.Append("# midi-source version=1 format=").Append(file.Format.ToString(CultureInfo.InvariantCulture)).Append(" ppq=").Append(file.TicksPerQuarter.ToString(CultureInfo.InvariantCulture)).Append(meterFact).AppendLine();
        source.AppendLine("output audio mix.mix");
        source.AppendLine();

        for (int trackIndex = 0; trackIndex < file.Tracks.Count; trackIndex++)
        {
            SmfTrack track = file.Tracks[trackIndex];
            string clip = $"track-{trackIndex + 1:D3}";
            List<StructuredMidiNote> notes = NotesOf(track, file.TicksPerQuarter, trackIndex, warnings, barTicks);
            if (warnings.Any(w => w.StartsWith($"track {trackIndex} has notes split across bar boundaries", StringComparison.Ordinal))) source.Append("# midi-fact track=").Append(trackIndex.ToString(CultureInfo.InvariantCulture)).AppendLine(" feature=ties");
            totalNotes += notes.Count;
            if (notes.Count > StructuredMidiNotes.MaximumNotes)
                throw new AmpleException("AMP5504", $"Track {trackIndex} exceeds the structured clip note limit.");
            source.Append("midi-clip ").Append(clip).Append(" at 1 for ").Append(Math.Max(1, notes.Count == 0 ? 8 : notes.Max(n => n.Bar))).AppendLine(" {");
            source.Append("  ppq ").Append(file.TicksPerQuarter).AppendLine();
            if (notes.Count > 0)
            {
                int noteStart = source.Length;
                source.Append("  notes ");
                for (int i = 0; i < notes.Count; i++)
                {
                    if (i > 0) source.Append(' ');
                    StructuredMidiNote note = notes[i];
                    source.Append(note.Bar).Append(':').Append(note.BeatTicks).Append(':').Append(note.LengthTicks);
                    source.Append(':').Append(note.Pitch).Append(':').Append(note.Velocity).Append(':').Append(note.Channel).Append(':').Append(note.Bank).Append(':').Append(note.Program).Append(':').Append(note.TieId);
                    if (note.Pan != 0) source.Append(";pan=").Append(note.Pan);
                    if (note.Fine != 0) source.Append(";fine=").Append(note.Fine);
                    if (note.Rel >= 0) source.Append(";rel=").Append(note.Rel);
                    if (note.Cut != 100) source.Append(";cut=").Append(note.Cut);
                    if (note.Slide != 0) source.Append(";slide=1");
                    if (note.Port != 0) source.Append(";port=").Append(note.Port);
                }
                source.AppendLine();
                if (source.Length - noteStart > StructuredMidiNotes.MaximumTextLength) throw new AmpleException("AMP5504", $"Track {trackIndex} exceeds the structured clip text limit.");
            }
            else source.AppendLine("  notes \"\"");
            source.AppendLine("}");

            int bars = Math.Max(1, notes.Count == 0 ? 8 : notes.Max(n => n.Bar));
            IEnumerable<(int Bank, int Program, int Channel)> programs = notes.Select(n => (n.Bank, n.Program, n.Channel)).Distinct().OrderBy(p => p.Bank).ThenBy(p => p.Program).ThenBy(p => p.Channel);
            if (notes.Count == 0) programs = [(0, 0, 0)];
            foreach ((int bank, int program, int channel) in programs)
            {
                string instrument = $"instrument-{trackIndex + 1:D3}-gm-{bank:D5}-{program:D3}-ch{channel:D2}";
                instruments.Add(instrument);
                source.Append("gm ").Append(instrument).Append(" at 1 for ").Append(bars).AppendLine(" {");
                source.AppendLine("  file pack:musescore-general").Append("  bank ").Append(bank).AppendLine().Append("  program ").Append(program).AppendLine().Append("  channel ").Append(channel).AppendLine();
                source.Append("  from ").Append(clip).AppendLine(".notes -> notes");
                source.AppendLine("}");
            }
            foreach (SmfEvent item in track.Events)
            {
                if (MidiSlideMarker.IsMarker(item)) continue;
                source.Append("# source-event track=").Append(trackIndex.ToString(CultureInfo.InvariantCulture)).Append(" tick=").Append(item.Tick.ToString(CultureInfo.InvariantCulture))
                    .Append(" kind=").Append(item.Kind).Append(" channel=").Append(item.Channel.ToString(CultureInfo.InvariantCulture))
                    .Append(" data1=").Append(item.Data1.ToString(CultureInfo.InvariantCulture)).Append(" data2=").Append(item.Data2.ToString(CultureInfo.InvariantCulture))
                    .Append(" meta=").Append(item.MetaType?.ToString("X2", CultureInfo.InvariantCulture) ?? "none")
                    .Append(" payload=").Append(System.Convert.ToHexStringLower(item.Payload)).AppendLine();
                if (source.Length > MaximumModuleCharacters) throw new AmpleException("AMP5504", "The generated module exceeds its 64 MiB text limit.");
            }
            source.AppendLine();
        }

        List<(string Name, string Port)> level = instruments.Select(instrument => (instrument, "audio")).ToList();
        int mixerLevel = 0;
        while (level.Count > 4)
        {
            List<(string Name, string Port)> next = [];
            for (int offset = 0; offset < level.Count; offset += 4)
            {
                string mix = $"mixer-{mixerLevel:D2}-{offset / 4 + 1:D3}";
                source.Append("mixer ").Append(mix).AppendLine(" {");
                for (int i = 0; i < 4 && offset + i < level.Count; i++) source.Append("  from ").Append(level[offset + i].Name).Append('.').Append(level[offset + i].Port).Append(" -> audio").Append(i + 1).AppendLine();
                source.AppendLine("}");
                next.Add((mix, "audio"));
            }
            level = next;
            mixerLevel++;
        }

        source.AppendLine("mixer mix {");
        for (int i = 0; i < level.Count; i++) source.Append("  from ").Append(level[i].Name).Append('.').Append(level[i].Port).Append(" -> audio").Append(i + 1).AppendLine();
        source.AppendLine("}");
        if (totalNotes == 0) warnings.Add("MIDI contains no matched notes");
        return new MidiModuleConversion(name, source.ToString(), [.. warnings.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Writes a generated module into a project's modules folder through the command bus.</summary>
    public static async Task<MidiModuleConversion> WriteAsync(SmfFile file, string projectRoot, string name, CommandBus bus, CancellationToken cancellationToken)
    {
        MidiModuleConversion conversion = Convert(file, name);
        string relative = "modules/" + name + ".module";
        string target = ProjectPath.Resolve(projectRoot, relative);
        if (File.Exists(target)) throw new AmpleException("AMP5504", $"The generated module path '{relative}' already exists.");
        Ample.Language.ModuleDefinition definition = Ample.Language.ModuleDefinition.Parse(conversion.Text);
        if (definition.Problems.Count > 0) throw new AmpleException("AMP5504", "The generated module failed its structural validation.");
        await bus.ExecuteAsync(new WriteFileCommand(relative, conversion.Text), cancellationToken).ConfigureAwait(false);
        return conversion;
    }

    private static List<StructuredMidiNote> NotesOf(SmfTrack track, int ppq, int trackIndex, List<string> warnings, long barTicks)
    {
        MidiNotePairing paired = MidiNotePairer.Pair(track, trackIndex);
        warnings.AddRange(paired.Warnings);
        List<MidiNoteSpan> spans = [.. paired.Notes];
        HashSet<MidiNoteSpan> slideSpans = [];
        Dictionary<int, long> originalEnds = [];
        foreach (SmfEvent marker in track.Events.Where(MidiSlideMarker.IsMarker))
        {
            if (!MidiSlideMarker.TryRead(marker, out var slide)) { warnings.Add($"track {trackIndex} has a malformed AMPLE slide marker"); continue; }
            int parent = spans.FindLastIndex(n => n.Channel == slide.Channel && n.StartTick <= slide.Start && n.EndTick >= slide.End && !slideSpans.Contains(n));
            if (parent < 0) { warnings.Add($"track {trackIndex} has an unattached AMPLE slide marker"); continue; }
            MidiNoteSpan voice = spans[parent];
            originalEnds[parent] = Math.Min(originalEnds.GetValueOrDefault(parent, voice.EndTick), slide.BaseEnd);
            MidiNoteSpan added = new(slide.Start, slide.End, slide.Pitch, slide.Velocity, slide.Channel, voice.Bank, voice.Program);
            spans.Add(added);
            slideSpans.Add(added);
        }
        foreach ((int parent, long end) in originalEnds) spans[parent] = spans[parent] with { EndTick = end };
        List<StructuredMidiNote> notes = [];
        int nextTieId = 0;
        foreach (MidiNoteSpan note in spans)
        {
            (int pan, int fine, int rel, int cut, int port) = slideSpans.Contains(note) ? (0, 0, -1, 100, 0) : ExpressionAt(track, note);
            long firstBar = note.StartTick / barTicks;
            long lastBar = (note.EndTick - 1) / barTicks;
            if (lastBar >= 100_000) throw new AmpleException("AMP5504", $"Track {trackIndex} exceeds the generated module's 100,000-bar limit.");
            if (lastBar - firstBar + 1 > StructuredMidiNotes.MaximumNotes - notes.Count) throw new AmpleException("AMP5504", $"Track {trackIndex} exceeds the structured clip note limit.");
            int tieId = nextTieId++;
            for (long bar = firstBar; bar <= lastBar; bar++)
            {
                long barStart = bar * barTicks;
                long segmentEnd = Math.Min(note.EndTick, barStart + barTicks);
                long beatTicks = note.StartTick > barStart ? note.StartTick - barStart : 0;
                long lengthTicks = segmentEnd - Math.Max(note.StartTick, barStart);
                int beat = checked((int)(beatTicks * StructuredMidiNotes.MicrobeatsPerBeat / ppq));
                int length = checked((int)(lengthTicks * StructuredMidiNotes.MicrobeatsPerBeat / ppq));
                notes.Add(new StructuredMidiNote(checked((int)bar + 1), beat, length, note.Pitch, note.Velocity, ppq, beatTicks, lengthTicks, note.Channel, note.Bank, note.Program, tieId, pan, fine, rel, cut, slideSpans.Contains(note) && bar == firstBar ? 1 : 0, port));
            }
            if (firstBar != lastBar) warnings.Add($"track {trackIndex} has notes split across bar boundaries");
        }
        foreach (SmfEvent item in track.Events.Where(e => (e.Kind == SmfEventKind.ControlChange && e.Data1 is not (0 or 32 or 64 or 120 or 121 or 123)) || e.Kind is (SmfEventKind.PitchBend or SmfEventKind.PolyphonicPressure or SmfEventKind.ChannelPressure)))
            warnings.Add($"track {trackIndex} contains controller, bend or pressure events retained as source-event comments");
        return [.. notes.OrderBy(n => n.Bar).ThenBy(n => n.BeatTicks).ThenBy(n => n.Pitch)];
    }

    internal static (int Pan, int Fine, int Rel, int Cut, int Port) ExpressionAt(SmfTrack track, MidiNoteSpan note)
    {
        int pan = 0, fine = 0, rel = -1, cut = 100, port = 0, bendRange = 2, portamento = 0;
        int rpnMsb = 127, rpnLsb = 127;
        foreach (SmfEvent item in track.Events)
        {
            if (item.Tick > note.StartTick) break;
            if (item.Channel != note.Channel) continue;
            if (item.Kind == SmfEventKind.ControlChange)
            {
                switch (item.Data1)
                {
                    case 10: pan = Math.Clamp((item.Data2 - 64) * 100 / (item.Data2 < 64 ? 64 : 63), -100, 100); break;
                    case 72: rel = item.Data2 * 5000 / 127; break;
                    case 74: cut = item.Data2 * 100 / 127; break;
                    case 65: portamento = item.Data2; if (portamento == 0) port = 0; break;
                    case 5 when portamento >= 64: port = item.Data2 * 2000 / 127; break;
                    case 101: rpnMsb = item.Data2; break;
                    case 100: rpnLsb = item.Data2; break;
                    case 6 when rpnMsb == 0 && rpnLsb == 0: bendRange = item.Data2; break;
                    case 121: pan = 0; fine = 0; rel = -1; cut = 100; bendRange = 2; break;
                }
            }
            else if (item.Kind == SmfEventKind.PitchBend)
                fine = Math.Clamp((item.Data1 + (item.Data2 << 7) - 8192) * bendRange * 100 / 8192, -100, 100);
        }
        return (pan, fine, rel, cut, port);
    }

}
