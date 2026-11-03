using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace Ample.Audio.Midi;

/// <summary>What a score import made.</summary>
/// <param name="SequenceText">The text of the <c>.ampleseq</c> file.</param>
/// <param name="Parts">How many parts (channels).</param>
/// <param name="Notes">How many notes.</param>
/// <param name="Bars">How many bars.</param>
/// <param name="Tempo">The tempo of the score in beats per minute (set the project tempo to it).</param>
/// <param name="Warnings">What could not be kept (another time signature, tempo changes, repeat endings).</param>
public sealed record ScoreImportResult(string SequenceText, int Parts, int Notes, int Bars, double Tempo, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a score into a sequence: a MIDI file or an uncompressed MusicXML file. Every part becomes a <c>gm</c> channel (its program kept) with its notes as
/// <c>bar:beat:length:pitch:velocity</c> rows, grouped in patterns of a number of bars, with a playlist that plays them in order. Bars are four beats.
/// </summary>
public static class ScoreImport
{
    private sealed record PartNotes(string Name, int Program, int Bank, List<(int Bar, double Beat, double Length, int Pitch, int Velocity)> Notes)
    {
        /// <summary>Gets the ticks per quarter note when the part keeps the exact ticks of a MIDI file (0 for the beats of a MusicXML).</summary>
        public int Ppq { get; init; }

        /// <summary>Gets the track the part came from.</summary>
        public int Track { get; init; } = -1;

        /// <summary>Gets the MIDI channel the part came from.</summary>
        public int Channel { get; init; } = -1;

        /// <summary>Gets the notes in exact ticks, cut at bar lines (the pieces of one note share a tie id).</summary>
        public List<(int Bar, long BeatTicks, long LengthTicks, int Pitch, int Velocity, int Tie)> Exact { get; } = [];

        /// <summary>Gets the controllers (c) and pitch bends (p) of the channel.</summary>
        public List<(int Bar, long Tick, char Kind, int Data1, int Data2)> Events { get; } = [];
    }

    /// <summary>Imports a Standard MIDI File.</summary>
    public static ScoreImportResult FromMidi(byte[] data, string title, int barsPerPattern, bool compress = false)
    {
        SmfFile file = SmfReader.Parse(data, SmfLimits.Standard);
        List<string> warnings = [];
        double ppq = file.TicksPerQuarter;
        double tempo = 60_000_000.0 / (file.TempoMap.Count > 0 ? file.TempoMap[0].MicrosecondsPerQuarter : 500_000);
        if (file.TempoMap.Count > 1)
        {
            warnings.Add($"The score changes tempo {file.TempoMap.Count - 1} time(s); the notes and controllers are placed at their real time at the first tempo ({tempo:0.#} BPM), so the bars of the sequence are those of that tempo.");
        }

        // the bar is the beats of the meter of the file (its first time signature; 4/4 without one); a song that plays the result must say the same meter
        double beatsPerBar = file.TimeSignatures.OrderBy(t => t.Tick).Select(t => t.Numerator * 4.0 / (1 << t.DenominatorPower)).Cast<double?>().FirstOrDefault() ?? 4.0;
        string meterText = file.TimeSignatures.OrderBy(t => t.Tick).Select(t => $"{t.Numerator}/{1 << t.DenominatorPower}").FirstOrDefault() ?? "4/4";
        if (file.TimeSignatures.Any(t => Math.Abs(t.Numerator * 4.0 / (1 << t.DenominatorPower) - beatsPerBar) > 1e-9))
        {
            warnings.Add($"The score changes meter inside the file; one meter is used for the whole sequence ({meterText}, the first), so the bars after a change are cut or padded.");
        }

        // the notes and controllers of a score that changes tempo are put at their real time at the first tempo (the way the MIDI renderer places them)
        SmfTempo[] tempos = [.. file.TempoMap];
        BigInteger[] elapsedAt = new BigInteger[tempos.Length];
        for (int i = 1; i < tempos.Length; i++) elapsedAt[i] = elapsedAt[i - 1] + ((BigInteger)(tempos[i].Tick - tempos[i - 1].Tick) * tempos[i - 1].MicrosecondsPerQuarter);
        long micros0 = tempos.Length > 0 ? tempos[0].MicrosecondsPerQuarter : 500_000;
        // the unit of the warped time: the greatest common divisor of the tempos, so that every tick of the file lands on a whole unit (2 units a tick when a part runs at twice the speed of the first tempo)
        static long Gcd(long x, long y) { while (y != 0) (x, y) = (y, x % y); return x; }
        long unit = tempos.Length > 1 ? tempos.Select(t => (long)t.MicrosecondsPerQuarter).Aggregate(Gcd) : micros0;
        int outPpq = tempos.Length > 1 ? checked((int)(file.TicksPerQuarter * micros0 / unit)) : file.TicksPerQuarter;
        long Warp(long tick)
        {
            if (tempos.Length <= 1) return tick;
            int low = 0, high = tempos.Length - 1;
            while (low < high)
            {
                int mid = low + ((high - low + 1) / 2);
                if (tempos[mid].Tick <= tick) low = mid; else high = mid - 1;
            }

            BigInteger elapsed = elapsedAt[low] + ((BigInteger)(tick - tempos[low].Tick) * tempos[low].MicrosecondsPerQuarter);
            return (long)(elapsed / unit);
        }

        long barTicks = (long)Math.Round(beatsPerBar * outPpq);
        double warpedPpq = outPpq;
        List<PartNotes> parts = [];
        for (int t = 0; t < file.Tracks.Count; t++)
        {
            string trackName = file.Tracks[t].Events.Where(e => e.Kind == SmfEventKind.Meta && e.MetaType == 0x03).Select(e => Encoding.UTF8.GetString(e.Payload)).FirstOrDefault() ?? $"track{t + 1}";
            foreach (IGrouping<(int Channel, int Program, int Bank), MidiNoteSpan> g in MidiNotePairer.Pair(file.Tracks[t], t).Notes.GroupBy(n => (n.Channel, n.Program, n.Bank)))
            {
                string name = Sanitize(g.Key.Channel == 9 ? "drums" : trackName) + (parts.Any(p => p.Name.StartsWith(Sanitize(trackName), StringComparison.Ordinal)) ? "-" + (g.Key.Channel + 1) : string.Empty);
                PartNotes part = new(name, g.Key.Channel == 9 ? 0 : g.Key.Program, g.Key.Channel == 9 ? 128 : g.Key.Bank, []) { Ppq = outPpq, Track = t, Channel = g.Key.Channel };
                int tie = 0;
                foreach (MidiNoteSpan n in g.OrderBy(x => x.StartTick))
                {
                    // exact ticks, as long as the note is (a drum hit of 5 ticks stays 5 ticks); a note that goes over a bar line is cut there and its pieces share a tie id
                    long start = Warp(n.StartTick), end = Math.Max(start + 1, Warp(n.EndTick));
                    long firstBar = start / barTicks, lastBar = (end - 1) / barTicks;
                    int tieId = lastBar > firstBar ? tie++ : -1;
                    for (long b = firstBar; b <= lastBar; b++)
                    {
                        long from = Math.Max(start, b * barTicks), to = Math.Min(end, (b + 1) * barTicks);
                        part.Exact.Add(((int)b + 1, from - (b * barTicks), to - from, n.Pitch, n.Velocity, tieId));
                    }

                    double q = start / warpedPpq;
                    part.Notes.Add(((int)(q / beatsPerBar) + 1, q % beatsPerBar, (end - start) / warpedPpq, n.Pitch, n.Velocity));
                }

                parts.Add(part);
            }

            foreach (SmfEvent e in file.Tracks[t].Events.Where(e => e.Kind is SmfEventKind.ControlChange or SmfEventKind.PitchBend))
            {
                long tick = Warp(e.Tick);
                foreach (PartNotes part in parts.Where(x => x.Track == t && x.Channel == e.Channel)) part.Events.Add(((int)(tick / barTicks) + 1, tick % barTicks, e.Kind == SmfEventKind.PitchBend ? 'p' : 'c', e.Data1, e.Data2));
            }
        }

        return Build(title, "MIDI", tempo, parts, barsPerPattern, warnings, compress, beatsPerBar, meterText);
    }

    /// <summary>Imports an uncompressed MusicXML (score-partwise) document.</summary>
    public static ScoreImportResult FromMusicXml(string xml, string title, int barsPerPattern)
    {
        XDocument doc = XDocument.Parse(xml);
        XElement root = doc.Root ?? throw new FormatException("The MusicXML file is empty.");
        if (root.Name.LocalName != "score-partwise")
        {
            throw new FormatException("Only score-partwise MusicXML is supported (the root element is " + root.Name.LocalName + "). A compressed .mxl must be unzipped first.");
        }

        List<string> warnings = [];
        Dictionary<string, (string Name, int Program, int Bank, Dictionary<string, int> UnpitchedMap)> info = [];
        foreach (XElement sp in root.Descendants().Where(e => e.Name.LocalName == "score-part"))
        {
            string id = (string?)sp.Attribute("id") ?? "P?";
            string name = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "part-name")?.Value ?? id;
            int program = int.TryParse(sp.Descendants().FirstOrDefault(e => e.Name.LocalName == "midi-program")?.Value, out int mp) ? Math.Clamp(mp - 1, 0, 127) : 0;
            // a midi-unpitched entry maps a percussion sound (a score-instrument an <unpitched> note refers to) to a
            // General MIDI drum-map key; its value is 1-based, so the actual MIDI key is one less.
            Dictionary<string, int> unpitched = [];
            foreach (XElement mi in sp.Descendants().Where(e => e.Name.LocalName == "midi-instrument"))
            {
                string? instrId = (string?)mi.Attribute("id");
                if (instrId is not null && int.TryParse(mi.Elements().FirstOrDefault(e => e.Name.LocalName == "midi-unpitched")?.Value, out int u))
                {
                    unpitched[instrId] = Math.Clamp(u - 1, 0, 127);
                }
            }

            info[id] = (name, unpitched.Count > 0 ? 0 : program, unpitched.Count > 0 ? 128 : 0, unpitched);
        }

        double tempo = double.TryParse(root.Descendants().Where(e => e.Name.LocalName == "sound").Select(e => (string?)e.Attribute("tempo")).FirstOrDefault(x => x is not null), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) ? bpm : 120;
        // the meter is the first time signature of the score (4/4 without one); a measure of the score is a bar here
        List<(int Beats, int Type)> signatures = [.. root.Descendants().Where(e => e.Name.LocalName == "time").Select(x => (int.TryParse(x.Elements().FirstOrDefault(y => y.Name.LocalName == "beats")?.Value, out int nb) ? nb : 4, int.TryParse(x.Elements().FirstOrDefault(y => y.Name.LocalName == "beat-type")?.Value, out int bt) ? bt : 4))];
        (int meterBeats, int meterType) = signatures.Count == 0 ? (4, 4) : signatures[0];
        if (meterBeats is < 1 or > 16 || meterType is not (2 or 4 or 8 or 16)) { (meterBeats, meterType) = (4, 4); }
        double beatsPerBar = meterBeats * 4.0 / meterType;
        string meterText = meterBeats.ToString(CultureInfo.InvariantCulture) + "/" + meterType.ToString(CultureInfo.InvariantCulture);
        List<PartNotes> parts = [];
        bool signatureWarned = false, endingWarned = false, unpitchedWarned = false;
        foreach (XElement part in root.Elements().Where(e => e.Name.LocalName == "part"))
        {
            string id = (string?)part.Attribute("id") ?? "P?";
            (string name, int program, int bank, Dictionary<string, int> unpitchedMap) = info.GetValueOrDefault(id, (id, 0, 0, []));
            PartNotes result = new(Sanitize(name), program, bank, []);
            List<XElement> measures = [.. part.Elements().Where(e => e.Name.LocalName == "measure")];
            // the order the measures are played in: a backward repeat jumps back to the forward repeat (or the start) once
            List<int> order = [];
            int repeatStart = 0;
            HashSet<int> jumped = [];
            for (int i = 0; i < measures.Count && order.Count < measures.Count * 4; i++)
            {
                order.Add(i);
                foreach (XElement bl in measures[i].Elements().Where(e => e.Name.LocalName == "barline"))
                {
                    XElement? repeat = bl.Elements().FirstOrDefault(e => e.Name.LocalName == "repeat");
                    if ((repeat?.Attribute("direction")?.Value) == "forward")
                    {
                        repeatStart = i;
                    }
                    else if (repeat?.Attribute("direction")?.Value == "backward" && jumped.Add(i))
                    {
                        i = repeatStart - 1;
                        break;
                    }

                    if (bl.Elements().Any(e => e.Name.LocalName == "ending") && !endingWarned)
                    {
                        warnings.Add("The score has first and second endings; they are played in the written order, check the repeat bars.");
                        endingWarned = true;
                    }
                }
            }

            double divisions = 1;
            Dictionary<(int Pitch, int Staff), int> open = [];
            int bar = 0;
            foreach (int index in order)
            {
                bar++;
                XElement measure = measures[index];
                double position = 0, lastStart = 0;
                foreach (XElement e in measure.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "attributes":
                            if (double.TryParse(e.Elements().FirstOrDefault(x => x.Name.LocalName == "divisions")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
                            {
                                divisions = d;
                            }

                            XElement? time = e.Elements().FirstOrDefault(x => x.Name.LocalName == "time");
                            if (time is not null && !signatureWarned && signatures.Any(s => Math.Abs(s.Beats * 4.0 / s.Type - beatsPerBar) > 1e-9))
                            {
                                warnings.Add($"The score changes meter; one meter is used for the whole import ({meterText}, the first), so the bars after a change are cut or padded.");
                                signatureWarned = true;
                            }

                            break;
                        case "backup":
                            position -= (double.Parse(e.Elements().First(x => x.Name.LocalName == "duration").Value, CultureInfo.InvariantCulture)) / divisions;
                            break;
                        case "forward":
                            position += (double.Parse(e.Elements().First(x => x.Name.LocalName == "duration").Value, CultureInfo.InvariantCulture)) / divisions;
                            break;
                        case "note":
                            bool chord = e.Elements().Any(x => x.Name.LocalName == "chord");
                            double length = double.TryParse(e.Elements().FirstOrDefault(x => x.Name.LocalName == "duration")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double len) ? len / divisions : 0;
                            double start = chord ? lastStart : position;
                            XElement? pitch = e.Elements().FirstOrDefault(x => x.Name.LocalName == "pitch");
                            bool isGrace = e.Elements().Any(x => x.Name.LocalName == "grace");
                            if (pitch is null && e.Elements().Any(x => x.Name.LocalName == "unpitched") && length > 0 && !isGrace)
                            {
                                string? instrId = (string?)e.Elements().FirstOrDefault(x => x.Name.LocalName == "instrument")?.Attribute("id");
                                if (instrId is not null && unpitchedMap.TryGetValue(instrId, out int drumPitch))
                                {
                                    result.Notes.Add((bar, start, length, drumPitch, 90));
                                }
                                else if (!unpitchedWarned)
                                {
                                    warnings.Add("The score has unpitched (percussion) notes without a midi-instrument mapping; those are skipped.");
                                    unpitchedWarned = true;
                                }
                            }

                            if (pitch is not null && length > 0 && !isGrace)
                            {
                                string step = pitch.Elements().First(x => x.Name.LocalName == "step").Value;
                                int octave = int.Parse(pitch.Elements().First(x => x.Name.LocalName == "octave").Value, CultureInfo.InvariantCulture);
                                int alter = (int)Math.Round(double.TryParse(pitch.Elements().FirstOrDefault(x => x.Name.LocalName == "alter")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a : 0);
                                int midi = ((octave + 1) * 12) + "C D EF G A B".IndexOf(step, StringComparison.Ordinal) + alter;
                                int staff = int.TryParse(e.Elements().FirstOrDefault(x => x.Name.LocalName == "staff")?.Value, out int st) ? st : 1;
                                List<string> ties = [.. e.Elements().Where(x => x.Name.LocalName == "tie").Select(x => (string?)x.Attribute("type") ?? string.Empty)];
                                if (ties.Contains("stop") && open.TryGetValue((midi, staff), out int idx) && idx < result.Notes.Count)
                                {
                                    (int b, double bt, double l, int p, int v) = result.Notes[idx];
                                    result.Notes[idx] = (b, bt, l + length, p, v);
                                    if (!ties.Contains("start"))
                                    {
                                        open.Remove((midi, staff));
                                    }
                                }
                                else
                                {
                                    result.Notes.Add((bar, start, length, midi, 90));
                                    if (ties.Contains("start"))
                                    {
                                        open[(midi, staff)] = result.Notes.Count - 1;
                                    }
                                }
                            }

                            if (!chord)
                            {
                                lastStart = position;
                                position += length;
                            }

                            break;
                    }
                }
            }

            parts.Add(result);
        }

        return Build(title, "MusicXML", tempo, parts, barsPerPattern, warnings, false, beatsPerBar, meterText);
    }

    private static string Sanitize(string name)
    {
        string clean = new([.. name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        clean = string.Join('-', clean.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 ? "part" : clean;
    }

    private static string Num(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private static ScoreImportResult Build(string title, string source, double tempo, List<PartNotes> parts, int barsPerPattern, List<string> warnings, bool compress = false, double beatsPerBar = 4.0, string meterText = "4/4")
    {
        parts = [.. parts.Where(p => p.Notes.Count > 0)];
        // unique channel names
        HashSet<string> used = [];
        List<PartNotes> named = [];
        foreach (PartNotes p in parts)
        {
            string name = p.Name;
            for (int n = 2; !used.Add(name); n++)
            {
                name = p.Name + "-" + n;
            }

            named.Add(p with { Name = name });
        }

        int bars = named.Count == 0 ? 0 : named.Max(p => p.Notes.Max(n => n.Bar + (int)Math.Floor((n.Beat + n.Length - 0.0001) / beatsPerBar)));
        int size = Math.Clamp(barsPerPattern, 1, 64);
        StringBuilder sb = new();
        sb.Append("sequencer 1\n");
        sb.Append(FormattableString.Invariant($"# {title}: imported from {source}; {named.Count} part(s), {named.Sum(p => p.Notes.Count)} note(s), {bars} bar(s) at {tempo:0.#} BPM (set the project tempo to it{(Math.Abs(beatsPerBar - 4.0) > 1e-9 ? $" and the meter to {meterText}" : string.Empty)}).\n"));
        foreach (string w in warnings)
        {
            sb.Append("# Warning: ").Append(w).Append('\n');
        }

        foreach (PartNotes p in named)
        {
            sb.Append(FormattableString.Invariant($"channel {p.Name} gm file=pack:musescore-general program={p.Program} bank={p.Bank} vol=-6 pan=0{(p.Ppq > 0 ? $" ppq={p.Ppq}" : string.Empty)}\n"));
        }

        // the patterns: a pattern that says what an earlier one says is not written again (the playlist plays the earlier one), and one that shares most rows with an earlier one is a variation of it that lists only what differs
        StringBuilder patterns = new(), playlist = new();
        List<(string Name, int Count, Dictionary<string, string> Rows)> plain = [];
        string tempoText = tempo.ToString("0.###", CultureInfo.InvariantCulture);
        int formulas = 0, reused = 0, varied = 0;
        for (int start = 1; start <= Math.Max(1, bars); start += size)
        {
            int count = Math.Min(size, bars - start + 1);
            Dictionary<string, string> rows = new(StringComparer.Ordinal);
            foreach (PartNotes p in named)
            {
                IEnumerable<string> entries = p.Ppq > 0
                    ? p.Exact.Where(n => n.Bar >= start && n.Bar < start + count).OrderBy(n => n.Bar).ThenBy(n => n.BeatTicks).ThenBy(n => n.Pitch).Select(n => FormattableString.Invariant($"{n.Bar - start + 1}:{n.BeatTicks}:{n.LengthTicks}:{n.Pitch}:{n.Velocity}") + (n.Tie >= 0 ? FormattableString.Invariant($";tie={n.Tie}") : string.Empty))
                    : p.Notes.Where(n => n.Bar >= start && n.Bar < start + count).OrderBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch).Select(n => $"{n.Bar - start + 1}:{Num(n.Beat)}:{Num(n.Length)}:{n.Pitch}:{n.Velocity}");
                string line = string.Join(' ', entries);
                if (line.Length == 0) continue;
                // a formula when it is much shorter and gives the very same notes (checked by evaluating it): a rhythm that repeats, with the pitches and velocities that cycle over it
                if (compress && Ample.Language.NoteSemantics.TryRead(line, p.Ppq > 0 ? p.Ppq : 1_000_000, 0, out List<Ample.Language.SemNote> semantic) && semantic.Count >= 8
                    && Ample.Language.NotePatterns.Find(semantic, tempoText, line.Length).FirstOrDefault(c => c.Verified && c.Length * 10 < line.Length * 6) is { } shorter)
                {
                    line = "= " + shorter.Expression;
                    formulas++;
                }

                rows[p.Name] = line;
                // the controllers (c) and pitch bends (p) of the channel, in the exact ticks of the file: bar:tick:kind:data1:data2
                string events = string.Join(' ', p.Events.Where(e => e.Bar >= start && e.Bar < start + count).OrderBy(e => e.Bar).ThenBy(e => e.Tick).Select(e => FormattableString.Invariant($"{e.Bar - start + 1}:{e.Tick}:{e.Kind}:{e.Data1}:{e.Data2}")));
                if (events.Length > 0) rows[p.Name + ".events"] = events;
            }

            (string Name, int Count, Dictionary<string, string> Rows) same = plain.FirstOrDefault(x => x.Count == count && x.Rows.Count == rows.Count && rows.All(kv => x.Rows.TryGetValue(kv.Key, out string? other) && other == kv.Value));
            if (same.Name is not null)
            {
                playlist.Append(FormattableString.Invariant($"  clip {same.Name} track=1 at={start} for={count}\n"));
                reused++;
                continue;
            }

            string name = $"part-{start}";
            (string Name, int Count, Dictionary<string, string> Rows) basis = plain.Where(x => x.Count == count).OrderByDescending(x => x.Rows.Count(kv => rows.TryGetValue(kv.Key, out string? other) && other == kv.Value)).FirstOrDefault();
            int shared = basis.Name is null ? 0 : basis.Rows.Count(kv => rows.TryGetValue(kv.Key, out string? other) && other == kv.Value);
            if (basis.Name is not null && shared > 0 && shared * 2 >= rows.Count)
            {
                patterns.Append(FormattableString.Invariant($"pattern {name} bars={count} from={basis.Name}\n"));
                foreach ((string key, string row) in rows.Where(kv => !basis.Rows.TryGetValue(kv.Key, out string? other) || other != kv.Value)) patterns.Append("  ").Append(key).Append(' ').Append(row).Append('\n');
                foreach (string key in basis.Rows.Keys.Where(k => !rows.ContainsKey(k))) patterns.Append("  ").Append(key).Append(" -\n");
                varied++;
            }
            else
            {
                patterns.Append(FormattableString.Invariant($"pattern {name} bars={count}\n"));
                foreach ((string key, string row) in rows) patterns.Append("  ").Append(key).Append(' ').Append(row).Append('\n');
                plain.Add((name, count, rows));
            }

            playlist.Append(FormattableString.Invariant($"  clip {name} track=1 at={start} for={count}\n"));
        }

        if (formulas + reused + varied > 0) sb.Append(FormattableString.Invariant($"# Written short: {formulas} formula row(s), {reused} pattern(s) played again from an earlier one, {varied} variation(s) that list only what differs.\n"));
        sb.Append(patterns).Append("playlist\n").Append(playlist);

        return new ScoreImportResult(sb.ToString(), named.Count, named.Sum(p => p.Notes.Count), bars, tempo, warnings);
    }
}
