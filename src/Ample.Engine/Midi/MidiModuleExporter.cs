using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Audio.Midi;

namespace Ample.Engine.Midi;

/// <summary>A regenerated MIDI file and explicit limits found while exporting an edited module.</summary>
public sealed record MidiModuleExport(byte[] Bytes, IReadOnlyList<string> Warnings);

/// <summary>Rebuilds editable MIDI note clips while retaining the other source events recorded by the importer.</summary>
public static class MidiModuleExporter
{
    private const int MaximumModuleCharacters = 64 * 1024 * 1024;
    private sealed record ExportNote(long Start, long End, int Pitch, int Velocity, int Channel, int Bank, int Program, int TieId, int Pan, int Fine, int Rel, int Cut, int Slide, int Port);
    private sealed record SlideRamp(long Start, long End, int Pitch, int FromPitch, int Channel, int Velocity, long BaseEnd, long ParentStart, int ParentPitch);

    /// <summary>Regenerates an SMF from a generated module's current midi-clip text and retained source-event comments.</summary>
    public static MidiModuleExport Export(string text, SpecData spec)
    {
        if (text.Length > MaximumModuleCharacters) throw new AmpleException("AMP5504", "The MIDI module exceeds its export text limit.");
        ModuleDefinition definition = ModuleDefinition.Parse(text);
        if (definition.Problems.Count > 0) throw new AmpleException("AMP5504", "The MIDI module declaration is invalid.");
        // the bar of the module is the meter it was imported with (meter=3/4 in the retained source facts; 4/4 without it): the notes are checked and placed in bars of that length
        System.Text.RegularExpressions.Match meterFact = System.Text.RegularExpressions.Regex.Match(text.Replace("\r\n", "\n", StringComparison.Ordinal), @"^# midi-source [^\n]*? meter=(\d+)/(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        int meterNumerator = meterFact.Success ? int.Parse(meterFact.Groups[1].Value, CultureInfo.InvariantCulture) : 4, meterDenominator = meterFact.Success ? int.Parse(meterFact.Groups[2].Value, CultureInfo.InvariantCulture) : 4;
        if (meterNumerator is < 1 or > 16 || meterDenominator is not (2 or 4 or 8 or 16)) (meterNumerator, meterDenominator) = (4, 4);
        AmpleDocument document = Parser.Parse("composition \"midi-export\" { ample 1  tempo 100  meter " + meterNumerator.ToString(CultureInfo.InvariantCulture) + "/" + meterDenominator.ToString(CultureInfo.InvariantCulture) + "  master-seed 1 }\n" + definition.Body, spec);
        if (document.Diagnostics.Any(d => d.Severity == Severity.Error)) throw new AmpleException("AMP5504", "The MIDI module contains language errors.");

        string? header = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault(line => line.StartsWith("# midi-source ", StringComparison.Ordinal));
        if (header is null) throw new AmpleException("AMP5504", "The module does not contain retained MIDI source facts.");
        Dictionary<string, string> facts = ParseFields(header[14..]);
        if (!facts.TryGetValue("format", out string? formatText) || !int.TryParse(formatText, NumberStyles.None, CultureInfo.InvariantCulture, out int format) || format is not (0 or 1) ||
            !facts.TryGetValue("ppq", out string? ppqText) || !int.TryParse(ppqText, NumberStyles.None, CultureInfo.InvariantCulture, out int ppq) || ppq is < 1 or > 0x7FFF)
            throw new AmpleException("AMP5504", "The module's retained MIDI format or PPQ is invalid.");

        Dictionary<int, List<SmfEvent>> retained = [];
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("# source-event ", StringComparison.Ordinal)) continue;
            Dictionary<string, string> fields = ParseFields(trimmed[15..]);
            if (!TryInt(fields, "track", out int track) || track is < 0 or >= 256 || !TryLong(fields, "tick", out long tick) || tick < 0 ||
                !fields.TryGetValue("kind", out string? kindText) || !Enum.TryParse(kindText, out SmfEventKind kind) ||
                !TryInt(fields, "channel", out int channel) || channel is < 0 or > 15 || !TryInt(fields, "data1", out int data1) || data1 < 0 || data1 > (kind == SmfEventKind.SystemExclusive ? 255 : 127) ||
                !TryInt(fields, "data2", out int data2) || data2 is < 0 or > 127 || !fields.TryGetValue("meta", out string? metaText) ||
                !fields.TryGetValue("payload", out string? payloadText)) throw new AmpleException("AMP5504", "A retained MIDI source-event record is malformed.");
            byte? meta = metaText == "none" ? null : byte.TryParse(metaText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedMeta) ? parsedMeta : throw new AmpleException("AMP5504", "A retained MIDI meta type is malformed.");
            byte[] payload;
            try { payload = Convert.FromHexString(payloadText); }
            catch (FormatException) { throw new AmpleException("AMP5504", "A retained MIDI event payload is malformed."); }
            if (!retained.TryGetValue(track, out List<SmfEvent>? events)) retained[track] = events = [];
            events.Add(new SmfEvent(tick, kind, channel, data1, data2, meta, payload));
        }
        int trackCount = Math.Max(retained.Keys.DefaultIfEmpty(-1).Max() + 1, document.Blocks.Count(b => b.Type == "midi-clip"));
        if (trackCount is < 1 or > 256 || format == 0 && trackCount != 1) throw new AmpleException("AMP5504", "The edited module's track count does not match its retained MIDI format.");
        BlockNode[] clips = [.. document.Blocks.Where(b => b.Type == "midi-clip")];
        if (clips.Length != trackCount) throw new AmpleException("AMP5504", "Keep one midi-clip block for each imported MIDI track before exporting.");
        List<string> warnings = [];
        List<SmfTrack> tracks = [];
        HashSet<int> occupiedChannels = [.. retained.Values.SelectMany(events => events).Where(e => e.Kind is not (SmfEventKind.Meta or SmfEventKind.SystemExclusive)).Select(e => e.Channel)];
        occupiedChannels.Add(0); // unscoped notes without retained events use channel zero
        foreach (BlockNode sourceClip in clips)
        {
            int sourcePpq = int.TryParse(sourceClip.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSourcePpq) ? parsedSourcePpq : StructuredMidiNotes.MicrobeatsPerBeat;
            if (StructuredMidiNotes.TryParse(sourceClip.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty, sourcePpq, out IReadOnlyList<StructuredMidiNote> channelNotes))
                occupiedChannels.UnionWith(channelNotes.Where(n => n.Channel >= 0).Select(n => n.Channel));
        }
        Dictionary<(int Track, int Channel, int Pan, int Fine, int Rel, int Cut, int Port), int> expressionChannels = [];
        for (int track = 0; track < trackCount; track++)
        {
            if (!retained.TryGetValue(track, out List<SmfEvent>? sourceEvents)) sourceEvents = [];
            HashSet<int> unmatchedNoteEvents = UnmatchedNoteEvents(sourceEvents, out bool zeroLength);
            bool unmatchedOn = unmatchedNoteEvents.Any(index => sourceEvents[index].Kind == SmfEventKind.NoteOn), unmatchedOff = unmatchedNoteEvents.Any(index => sourceEvents[index].Kind == SmfEventKind.NoteOff);
            if (unmatchedOn) warnings.Add($"track {track} has unmatched note-on events; they remain as raw source events in module export");
            if (unmatchedOff) warnings.Add($"track {track} has unmatched note-off events; they remain as raw source events in module export");
            if (zeroLength) warnings.Add($"track {track} has zero-length notes; their raw note events remain in module export");
            int channel = sourceEvents.Where(e => e.Kind is SmfEventKind.NoteOn or SmfEventKind.NoteOff).GroupBy(e => e.Channel).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
            BlockNode clip = clips[track];
            int clipPpq = int.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPpq) ? parsedPpq : StructuredMidiNotes.MicrobeatsPerBeat;
            if (clipPpq != ppq || !StructuredMidiNotes.TryParse(clip.Parameters.FirstOrDefault(p => p.Name == "notes")?.Value ?? string.Empty, clipPpq, out IReadOnlyList<StructuredMidiNote> sourceNotes))
                throw new AmpleException("AMP5504", $"Track {track} has invalid structured note timing or PPQ.");
            if (sourceNotes.Any(n => n.Channel < 0) && sourceEvents.Where(e => e.Kind is SmfEventKind.NoteOn or SmfEventKind.NoteOff).Select(e => e.Channel).Distinct().Skip(1).Any()) warnings.Add($"track {track} had multiple channels; unscoped edited notes use dominant channel {channel}");
            List<ExportNote> merged = MergeTies(sourceNotes, ppq, channel, (long)Math.Round(4.0 * meterNumerator / meterDenominator * ppq));
            merged = [.. merged.Select(note => ApplyInstrumentEdits(document, track, note))];
            List<SlideRamp> ramps = [];
            foreach (ExportNote slide in merged.Where(n => n.Slide != 0).OrderBy(n => n.Start).ToArray())
            {
                int parent = merged.FindLastIndex(n => n.Slide == 0 && n.Channel == slide.Channel && n.Start <= slide.Start && n.End >= slide.Start);
                if (parent < 0) { warnings.Add($"track {track} has an unattached slide; it exports as an ordinary note"); continue; }
                ExportNote voice = merged[parent];
                int fromPitch = ramps.LastOrDefault(r => r.Channel == voice.Channel && r.Start >= voice.Start && r.End <= slide.Start)?.Pitch ?? voice.Pitch;
                ramps.Add(new SlideRamp(slide.Start, slide.End, slide.Pitch, fromPitch, voice.Channel, slide.Velocity, voice.End, voice.Start, voice.Pitch));
                merged[parent] = voice with { End = Math.Max(voice.End, slide.End) };
                merged.Remove(slide);
            }
            for (int i = 0; i < merged.Count; i++)
            {
                ExportNote note = merged[i];
                bool glides = ramps.Any(r => r.ParentStart == note.Start && r.ParentPitch == note.Pitch && r.Channel == note.Channel);
                bool expressionAlreadyRetained = !glides && ExpressionMatches(sourceEvents, note);
                if (expressionAlreadyRetained) continue;
                bool isolated = glides || note.Port > 0;
                if (note.Pan == 0 && note.Fine == 0 && note.Rel < 0 && note.Cut == 100 && note.Port == 0 && !glides) continue;
                var key = (track, note.Channel, note.Pan, note.Fine, note.Rel, note.Cut, note.Port);
                int expressionChannel;
                if (isolated || !expressionChannels.TryGetValue(key, out expressionChannel))
                {
                    int? free = Enumerable.Range(0, 16).Where(candidate => candidate != 9 && !occupiedChannels.Contains(candidate)).Cast<int?>().FirstOrDefault();
                    if (free is null) throw new AmpleException("AMP5504", "MIDI export needs a free channel for each distinct note expression.");
                    expressionChannel = free.Value;
                    if (!isolated) expressionChannels.Add(key, expressionChannel);
                    occupiedChannels.Add(expressionChannel);
                }
                merged[i] = note with { Channel = expressionChannel };
                for (int ramp = 0; ramp < ramps.Count; ramp++) if (ramps[ramp].Channel == note.Channel && ramps[ramp].ParentStart == note.Start && ramps[ramp].ParentPitch == note.Pitch) ramps[ramp] = ramps[ramp] with { Channel = expressionChannel };
            }
            List<(long Tick, int Priority, int Order, SmfEvent Event)> ordered = [];
            int order = 0;
            for (int sourceIndex = 0; sourceIndex < sourceEvents.Count; sourceIndex++)
            {
                SmfEvent item = sourceEvents[sourceIndex];
                if ((item.Kind is SmfEventKind.NoteOn or SmfEventKind.NoteOff) && !unmatchedNoteEvents.Contains(sourceIndex)) continue;
                if (MidiSlideMarker.IsMarker(item)) continue;
                if (item.Kind == SmfEventKind.Meta && item.MetaType == 0x2F) continue;
                ordered.Add((item.Tick, 1, order++, item));
            }
            foreach (var note in merged.OrderBy(n => n.Start).ThenBy(n => n.Pitch))
            {
                ordered.Add((note.Start, 2, order++, new SmfEvent(note.Start, SmfEventKind.ControlChange, note.Channel, 0, note.Bank / 128, null, [])));
                ordered.Add((note.Start, 2, order++, new SmfEvent(note.Start, SmfEventKind.ControlChange, note.Channel, 32, note.Bank % 128, null, [])));
                ordered.Add((note.Start, 2, order++, new SmfEvent(note.Start, SmfEventKind.ProgramChange, note.Channel, note.Program, 0, null, [])));
                void Control(int number, int value) => ordered.Add((note.Start, 2, order++, new SmfEvent(note.Start, SmfEventKind.ControlChange, note.Channel, number, value, null, [])));
                if (note.Pan != 0) Control(10, (int)(64 + (long)note.Pan * (note.Pan < 0 ? 64 : 63) / 100));
                if (note.Fine != 0 || ramps.Any(r => r.Channel == note.Channel && r.Start >= note.Start && r.End <= note.End))
                {
                    int range = ramps.Any(r => r.Channel == note.Channel && r.Start >= note.Start && r.End <= note.End) ? 48 : 1;
                    Control(101, 0); Control(100, 0); Control(6, range); Control(38, 0); Control(101, 127); Control(100, 127);
                    if (note.Fine != 0)
                    {
                        int bend = (int)Math.Clamp(8192 + (long)note.Fine * 8192 / (range * 100), 0, 16383);
                        ordered.Add((note.Start, 2, order++, new SmfEvent(note.Start, SmfEventKind.PitchBend, note.Channel, bend & 127, bend >> 7, null, [])));
                    }
                }
                if (note.Rel >= 0) Control(72, (int)((long)note.Rel * 127 / 5000));
                if (note.Cut != 100) Control(74, note.Cut * 127 / 100);
                if (note.Port > 0) { Control(65, 127); Control(5, Math.Clamp(note.Port * 127 / 2000, 0, 127)); }
            }
            foreach (SlideRamp ramp in ramps)
            {
                for (int step = 0; step <= 32; step++)
                {
                    long tick = ramp.Start + (ramp.End - ramp.Start) * step / 32;
                    long cents = (long)(ramp.FromPitch - ramp.ParentPitch) * 100 + (long)(ramp.Pitch - ramp.FromPitch) * 100 * step / 32;
                    int bend = (int)Math.Clamp(8192 + cents * 8192 / 4800, 0, 16383);
                    ordered.Add((tick, 2, order++, new SmfEvent(tick, SmfEventKind.PitchBend, ramp.Channel, bend & 127, bend >> 7, null, [])));
                }
                ordered.Add((ramp.Start, 2, order++, MidiSlideMarker.Write(ramp.Start, ramp.End, ramp.Pitch, ramp.Velocity, ramp.Channel, ramp.BaseEnd)));
            }
            foreach (var note in merged)
            {
                ordered.Add((note.Start, 3, order++, new SmfEvent(note.Start, SmfEventKind.NoteOn, note.Channel, note.Pitch, note.Velocity, null, [])));
                ordered.Add((note.End, 0, order++, new SmfEvent(note.End, SmfEventKind.NoteOff, note.Channel, note.Pitch, 0, null, [])));
                if (note.Port > 0) ordered.Add((note.End, 1, order++, new SmfEvent(note.End, SmfEventKind.ControlChange, note.Channel, 65, 0, null, [])));
            }
            long endTick = ordered.Select(x => x.Tick).DefaultIfEmpty(0).Max();
            ordered.Add((endTick, 3, order, new SmfEvent(endTick, SmfEventKind.Meta, 0, 0, 0, 0x2F, [])));
            tracks.Add(new SmfTrack([.. ordered.OrderBy(x => x.Tick).ThenBy(x => x.Priority).ThenBy(x => x.Order).Select(x => x.Event)]));
        }
        if (retained.Any(pair => { HashSet<int> unmatched = UnmatchedNoteEvents(pair.Value, out _); return pair.Value.Select((item, index) => (item, index)).Any(x => x.item.Kind == SmfEventKind.NoteOff && x.item.Data2 != 0 && !unmatched.Contains(x.index)); })) warnings.Add("note-off release velocities of clip notes are regenerated as zero");
        if (Enumerable.Range(0, trackCount).Any(track => !retained.ContainsKey(track))) warnings.Add("some source-event comments are missing; missing non-note events cannot be regenerated");
        try { return new MidiModuleExport(SmfWriter.Write(new SmfFile(format, ppq, tracks), SmfLimits.Standard), [.. warnings.Distinct(StringComparer.Ordinal)]); }
        catch (SmfFormatException ex) { throw new AmpleException("AMP5504", ex.Message); }
    }

    private static List<ExportNote> MergeTies(IReadOnlyList<StructuredMidiNote> notes, int ppq, int fallbackChannel, long barTicks)
    {
        List<ExportNote> merged = [];
        foreach (ExportNote note in notes.Select(n => new ExportNote(checked((long)(n.Bar - 1) * barTicks + n.BeatTicks), checked((long)(n.Bar - 1) * barTicks + n.BeatTicks + n.LengthTicks), n.Pitch, n.Velocity, n.Channel < 0 ? fallbackChannel : n.Channel, n.Bank < 0 ? 0 : n.Bank, n.Program < 0 ? 0 : n.Program, n.TieId, n.Pan, n.Fine, n.Rel, n.Cut, n.Slide, n.Port)).OrderBy(n => n.Start).ThenBy(n => n.Pitch))
        {
            int previous = note.TieId < 0 ? -1 : merged.FindLastIndex(n => n.TieId == note.TieId);
            if (previous >= 0 && merged[previous].End == note.Start && merged[previous] with { Start = note.Start, End = note.End } == note)
                merged[previous] = merged[previous] with { End = note.End };
            else merged.Add(note);
        }
        return merged;
    }

    private static ExportNote ApplyInstrumentEdits(AmpleDocument document, int track, ExportNote note)
    {
        string name = $"instrument-{track + 1:D3}-gm-{note.Bank:D5}-{note.Program:D3}-ch{note.Channel:D2}";
        BlockNode? instrument = document.Blocks.FirstOrDefault(b => b.Name == name && b.Type == "gm");
        if (instrument is null) return note;
        int bank = int.TryParse(instrument.Parameters.FirstOrDefault(p => p.Name == "bank")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedBank) ? parsedBank : note.Bank;
        int program = int.TryParse(instrument.Parameters.FirstOrDefault(p => p.Name == "program")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedProgram) ? parsedProgram : note.Program;
        if (bank is < 0 or > 16383 || program is < 0 or > 127) throw new AmpleException("AMP5504", "An edited GM instrument has a bank or program outside MIDI bounds.");
        return note with { Bank = bank, Program = program };
    }

    private static bool ExpressionMatches(IReadOnlyList<SmfEvent> events, ExportNote note)
    {
        var source = MidiModuleConverter.ExpressionAt(new SmfTrack(events), new MidiNoteSpan(note.Start, note.End, note.Pitch, note.Velocity, note.Channel, note.Bank, note.Program));
        return (note.Pan, note.Fine, note.Rel, note.Cut, note.Port) == source;
    }

    private static Dictionary<string, string> ParseFields(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || !fields.TryAdd(pair[0], pair[1])) throw new AmpleException("AMP5504", "A retained MIDI comment contains malformed or duplicate fields.");
        }
        return fields;
    }

    private static HashSet<int> UnmatchedNoteEvents(List<SmfEvent> events, out bool zeroLength)
    {
        Dictionary<(int Channel, int Pitch), Queue<int>> active = [];
        HashSet<int> unmatched = [];
        zeroLength = false;
        for (int index = 0; index < events.Count; index++)
        {
            SmfEvent item = events[index];
            var key = (item.Channel, item.Data1);
            if (item.Kind == SmfEventKind.NoteOn)
            {
                if (item.Data2 == 0) { unmatched.Add(index); continue; }
                if (!active.TryGetValue(key, out Queue<int>? queue)) active[key] = queue = new();
                queue.Enqueue(index);
            }
            else if (item.Kind == SmfEventKind.NoteOff)
            {
                if (active.TryGetValue(key, out Queue<int>? queue) && queue.Count > 0)
                {
                    int start = queue.Dequeue();
                    if (events[start].Tick >= item.Tick) { unmatched.Add(start); unmatched.Add(index); zeroLength |= events[start].Tick == item.Tick; }
                }
                else unmatched.Add(index);
            }
        }
        foreach (Queue<int> queue in active.Values) while (queue.Count > 0) unmatched.Add(queue.Dequeue());
        return unmatched;
    }

    private static bool TryInt(Dictionary<string, string> fields, string name, out int value)
    {
        value = 0;
        return fields.TryGetValue(name, out string? text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryLong(Dictionary<string, string> fields, string name, out long value)
    {
        value = 0;
        return fields.TryGetValue(name, out string? text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
