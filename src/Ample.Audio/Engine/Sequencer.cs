using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Audio.Instruments;
using Ample.Audio.Midi;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>The files a sequencer block reads: its sequence file and the samples, loops and sample sets its channels name.</summary>
public static class SequencerFiles
{
    /// <summary>Gets the existing files of a sequencer block, as full paths in a stable order (the sequence file first).</summary>
    public static IReadOnlyList<string> Of(string projectRoot, string? ampleRoot, BlockNode block, ISet<string>? setFiles = null)
    {
        string? value = block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"');
        string? embedded = block.Parameters.FirstOrDefault(p => p.Name == "sequence")?.Value;
        if (string.IsNullOrEmpty(value) && string.IsNullOrWhiteSpace(embedded)) return [];
        List<string> files = [];
        try
        {
            string text;
            if (!string.IsNullOrWhiteSpace(embedded)) text = embedded;
            else
            {
                string path = ProjectPath.Resolve(projectRoot, value!);
                if (!File.Exists(path)) return [];
                files.Add(path);
                text = File.ReadAllText(path);
            }

            SequenceFile seq = SequenceFile.Parse(text);
            SortedSet<string> more = new(StringComparer.Ordinal);
            foreach (SeqChannel c in seq.Channels)
            {
                if (c.Type == "gm")
                {
                    string font = c.Get("file");
                    if (font.Length > 0 && SoundFontAsset.Resolve(projectRoot, ampleRoot, font) is { } fontPath && File.Exists(fontPath)) more.Add(fontPath);
                    continue;
                }

                foreach (string key in new[] { "sample", "file" })
                {
                    string v = c.Get(key);
                    if (v.Length > 0 && ProjectPath.Resolve(projectRoot, v) is { } full && File.Exists(full)) more.Add(full);
                }

                string set = c.Get("set");
                if (set.Contains('/', StringComparison.Ordinal))
                {
                    string folder = SampleSet.ResolveFolder(projectRoot, ampleRoot, set);
                    if (Directory.Exists(folder)) foreach (string wav in Directory.EnumerateFiles(folder, "*.wav")) { more.Add(wav); setFiles?.Add(wav); }
                }
            }

            files.AddRange(more);
        }
        catch (AmpleException)
        {
            // a bad file is reported by the renderer
        }

        return files;
    }
}

/// <summary>
/// The <c>sequencer</c> block (plan fl-block FL02, FL03): channels (drum steps, loops, notes with a built-in tone or a sample set), patterns and a playlist, mixed
/// into <c>mix</c> and into up to eight <c>lane</c> outputs that name the channels they carry. All arithmetic is integer.
/// </summary>
internal sealed class SequencerRenderer : IBlockRenderer
{
    public string BlockType => "sequencer";
    public string Id => "builtin.sequencer";
    // the version of the GM renderer is part of it: a sequencer plays its gm channels with that renderer, so a new GM sound must build the sequencer again
    public string Version => "3/gm" + new SampledInstruments.Gm().Version;

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        // stereo sample files are played once per channel and joined (A02); the sequencer's own main output is named "mix", not "audio"
        // like a plain instrument's (a gm channel whose soundfont preset pans - as GM 44 Tremolo Strings and 45 Pizzicato Strings do in at
        // least one pack - otherwise flagged this stereo and Stereo() then looked for a role that was never there)
        if (r.Probe is null && r.SampleChannel == 0) return SampledInstruments.Stereo(r, pass => RenderAsync(pass, cancellationToken), audioRole: "mix");
        string Value(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        string file = Value("file");
        // a sequence kept in the song itself (a sequence { ... } part of the block) is used before a file
        string? embedded = r.Block.Parameters.FirstOrDefault(p => p.Name == "sequence")?.Value;
        string sequenceText;
        if (!string.IsNullOrWhiteSpace(embedded)) sequenceText = embedded;
        else
        {
            if (file.Length == 0) throw new AmpleException("AMP5380", "The sequencer has no sequence: choose a sequences/<name>.ampleseq file of the project (Add > Sequence in the project panel makes one), or keep the sequence in the song.");
            string path = ProjectPath.Resolve(r.ProjectRoot, file);
            if (!File.Exists(path)) throw new AmpleException("AMP5380", $"The sequence file '{file}' is not in the project.");
            sequenceText = File.ReadAllText(path);
        }

        SequenceFile seq = SequenceFile.Parse(sequenceText);
        IReadOnlyDictionary<int, string[]> lanes = seq.ParseLanes(Value("lanes"));
        int spb = r.SamplesPerBar, length = r.BarCount * spb;
        int[] mixL = new int[length], mixR = new int[length];
        Dictionary<int, (int[] L, int[] R)> laneBuffers = lanes.Keys.ToDictionary(k => k, _ => (new int[length], new int[length]));
        Dictionary<string, short[]> cache = new(StringComparer.Ordinal);
        bool anySolo = seq.Channels.Any(c => c.Get("solo") == "on");
        List<SeqChannel> playing = seq.Channels.Where(c => c.Get("mute") != "on" && (!anySolo || c.Get("solo") == "on")).ToList();
        int rendered = 0;
        foreach (SeqChannel channel in seq.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channel.Get("mute") == "on" || (anySolo && channel.Get("solo") != "on")) continue;
            // a long score is one block: say which channel is being rendered, so the build shows how far it is
            r.Report?.Invoke(new ProgressReport((double)rendered / playing.Count, $"{channel.Name} ({rendered + 1}/{playing.Count})", 0));
            short[] mono = RenderChannel(r, seq, channel, length, cache, cancellationToken);
            rendered++;
            r.Report?.Invoke(new ProgressReport((double)rendered / playing.Count, $"{channel.Name} ({rendered}/{playing.Count})", 1));
            int gain = FixedAudio.GainFromDb(Math.Clamp(Db(channel.Get("vol")), -60, 12));
            int pan = Math.Clamp(Db(channel.Get("pan")), -PanTable.Range, PanTable.Range);
            long left = (long)gain * PanTable.LeftGain(pan) >> 15, right = (long)gain * PanTable.RightGain(pan) >> 15;
            IEnumerable<(int[] L, int[] R)> targets = lanes.Where(l => l.Value.Contains(channel.Name)).Select(l => laneBuffers[l.Key]);
            (int[] L, int[] R)[] into = [(mixL, mixR), .. targets];
            for (int i = 0; i < length; i++)
            {
                if (mono[i] == 0) continue;
                int l = (int)(mono[i] * left >> 15), rr = (int)(mono[i] * right >> 15);
                foreach ((int[] L, int[] R) target in into) { target.L[i] += l; target.R[i] += rr; }
            }
        }

        int master = FixedAudio.GainFromDb(Math.Clamp(Db(Value("gain")), -60, 12));
        List<RenderOutput> outputs = [new RenderOutput("mix", Encode(mixL, mixR, master, r.SampleRate))];
        foreach ((int lane, (int[] l, int[] rr)) in laneBuffers.OrderBy(x => x.Key)) outputs.Add(new RenderOutput("lane" + lane.ToString(CultureInfo.InvariantCulture), Encode(l, rr, master, r.SampleRate)));
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>(outputs);
    }

    private static byte[] Encode(int[] left, int[] right, int gainQ15, int rate)
    {
        short[] l = new short[left.Length], rr = new short[left.Length];
        for (int i = 0; i < l.Length; i++)
        {
            l[i] = FixedAudio.Clip((long)left[i] * gainQ15 >> 15);
            rr[i] = FixedAudio.Clip((long)right[i] * gainQ15 >> 15);
        }

        return StereoWav.Encode(new StereoBuffer(l, rr), rate);
    }

    private static int Db(string text) => int.TryParse(text.Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static int Velocity(char c) => c switch { 'X' => 32768, 'x' => 26000, 'o' => 14000, >= '1' and <= '9' => 32768 * (c - '0') / 9, _ => 0 };

    private static short[] Load(RenderRequest r, string setting, string value, Dictionary<string, short[]> cache)
    {
        if (cache.TryGetValue(value, out short[]? known)) return known;
        string full = ProjectPath.Resolve(r.ProjectRoot, value);
        if (!File.Exists(full)) throw new AmpleException("AMP5380", $"The {setting} '{value}' is not in the project.");
        try
        {
            byte[] bytes = File.ReadAllBytes(full);
            if (ChannelAudio.IsStereo(bytes) && r.Probe is { } probe) probe.Seen = true;
            return cache[value] = SampleSet.ReadWav(bytes, r.SampleRate, r.SampleChannel);
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5380", $"The {setting} '{value}' cannot be used: {ex.Message}");
        }
    }

    private static short[] RenderChannel(RenderRequest r, SequenceFile seq, SeqChannel channel, int length, Dictionary<string, short[]> cache, CancellationToken cancellationToken)
    {
        short[] buf = new short[length];
        int spb = r.SamplesPerBar;
        short[]? drum = channel.Type == "drum" ? Load(r, "sample", channel.Get("sample"), cache) : null;
        short[]? loop = channel.Type == "loop" ? Load(r, "file", channel.Get("file"), cache) : null;
        int loopBars = Math.Max(1, int.TryParse(channel.Get("bars", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out int lb) ? lb : 1);
        SampleSet? set = null;
        Instrument? instrument = null;
        Dictionary<(string, int), short[]> shifted = [];
        bool pitchedNotes = channel.Type is "notes" or "bass" or "piano";
        if ((pitchedNotes && channel.Get("set").Contains('/', StringComparison.Ordinal)) || channel.Type == "guitar")
        {
            if (!channel.Get("set").Contains('/', StringComparison.Ordinal)) throw new AmpleException("AMP5350", $"The guitar channel '{channel.Name}' needs a sample folder: set=samples/<folder> (WAV files named by note: e2.wav, a2.wav, c3.wav...).");
            set = SampleSet.Load(r.ProjectRoot, r.AmpleRoot, channel.Get("set"), r.SampleRate, r.SampleChannel);
            if (set.Stereo && r.Probe is { } setProbe) setProbe.Seen = true;
            instrument = SampledInstruments.Pitched(set, channel.Get("set"));
        }

        string voicing = channel.Get("voicing", "open");
        if (channel.Type == "guitar" && voicing is not ("open" or "triad" or "power")) throw new AmpleException("AMP5351", $"voicing must be open, triad or power (found '{voicing}').");
        int stagger = Math.Clamp(int.TryParse(channel.Get("stagger", "22"), NumberStyles.None, CultureInfo.InvariantCulture, out int st) ? st : 22, 0, 80) * r.SampleRate / 1000;
        string wave = channel.Get("wave", channel.Type == "bass" ? "saw" : channel.Type == "piano" ? "triangle" : "square");
        int shift = int.TryParse(channel.Get("shift", channel.Type == "bass" ? "-12" : "0"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sh) ? sh : 0;
        int counter = 0;
        Dictionary<string, IReadOnlyList<StructuredMidiNote>> parsed = new(StringComparer.Ordinal);
        Dictionary<string, (string[] Strums, string[] Chords)> guitarRows = new(StringComparer.Ordinal);
        List<(int At, int[] Chord, bool Down, int Velocity, bool Mute)> strums = [];
        List<object> gmNotes = [], gmDrumNotes = [];
        // an imported MIDI writes its rows in the exact ticks of the file (ppq=480): the notes keep their tick and length, a note cut at a bar line is joined again by its tie id,
        // and the controllers and bends of the channel are in a row of their own (<channel>.events: bar:tick:c|p:data1:data2) that the soundfont renderer plays with the notes
        int rowPpq = int.TryParse(channel.Get("ppq"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPpq) && parsedPpq is >= 1 and <= 1_000_000 ? parsedPpq : 1_000_000;
        Dictionary<int, int> tieBar = [];
        // a formula row (= note pipeline) needs the tempo of the song, for lengths in seconds
        string tempoText = (240.0 * r.SampleRate / r.SamplesPerBar).ToString("0.###", CultureInfo.InvariantCulture);
        List<MidiPerformanceEvent> performance = [];
        List<(int Start, int Length, int Bend)> bends = [];
        List<DelayLlama.Note> llamaNotes = [];
        HashSet<int> windowBars = [];
        for (int ob = 0; ob < r.BarCount; ob++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int absolute = r.FirstBar + ob;
            windowBars.Add(absolute);
            int song = seq.SourceBar(absolute);
            int barStart = ob * spb;
            foreach (SeqClip clip in seq.Clips)
            {
                if (song < clip.At || song >= clip.At + clip.Bars) continue;
                int k = song - clip.At;
                SeqPattern pattern = seq.Patterns.FirstOrDefault(p => p.Name == clip.Pattern) ?? throw new AmpleException("AMP5380", $"A clip names the pattern '{clip.Pattern}', which is not defined.");
                IReadOnlyDictionary<string, string> rows = seq.RowsOf(pattern);
                string row = rows.GetValueOrDefault(channel.Name, string.Empty);
                int patternBar = k % pattern.Bars;
                if (drum is not null)
                {
                    string[] groups = row.Split('|', StringSplitOptions.TrimEntries);
                    string grid = groups[patternBar % groups.Length].Replace(" ", string.Empty, StringComparison.Ordinal);
                    if (grid.Length == 0) continue;
                    if (grid.Any(c => c is not ('x' or 'X' or 'o' or '.') and not (>= '1' and <= '9'))) throw new AmpleException("AMP5380", $"The steps of '{channel.Name}' may only hold x, X, o, the digits 1 to 9 and . (found '{grid}').");
                    // 16 steps a bar; a bar written with 12 (eighth triplets), 24 (sixteenth triplets) or 32 (thirty-second) characters has that many steps instead
                    int sixteenths = Math.Max(1, (int)Math.Round(4 * r.BeatsPerBar));
                    int steps = grid.Length is 12 or 24 or 32 ? grid.Length : sixteenths;
                    for (int st2 = 0; st2 < steps; st2++)
                    {
                        int velocity = Velocity(grid[st2 % grid.Length]);
                        if (velocity > 0) MixSample(buf, drum, barStart + (steps == 16 && sixteenths == 16 ? st2 * (spb / 16) : (int)((long)st2 * spb / steps)), velocity * 3 / 4, int.MaxValue);
                    }
                }
                else if (loop is not null)
                {
                    if (rows.ContainsKey(channel.Name) && k % loopBars == 0) MixSample(buf, loop, barStart, 24576, Math.Min(loopBars, clip.Bars - k) * spb);
                }
                else if (channel.Type == "guitar")
                {
                    if (row.Length == 0) continue;
                    string key = pattern.Name + "/" + channel.Name + "/" + row;
                    if (!guitarRows.TryGetValue(key, out (string[] Strums, string[] Chords) guitarRow)) guitarRows[key] = guitarRow = ParseGuitarRow(channel, pattern, row);
                    string strum = guitarRow.Strums[patternBar % guitarRow.Strums.Length];
                    int[] chord = SampledInstruments.Guitar.Voice(ChordNotes(guitarRow.Chords[patternBar % guitarRow.Chords.Length], channel, pattern), voicing);
                    int strumSteps = Math.Max(1, (int)Math.Round(4 * r.BeatsPerBar));
                    for (int st2 = 0; st2 < strumSteps; st2++)
                    {
                        char c = strum[st2 % strum.Length];
                        int at = barStart + (strumSteps == 16 ? st2 * (spb / 16) : (int)((long)st2 * spb / strumSteps));
                        if (c == 'x') strums.Add((at, [], true, 0, true));
                        else if (SampledInstruments.Guitar.Strokes.TryGetValue(c, out (bool Down, int Velocity) stroke)) strums.Add((at, chord, stroke.Down, stroke.Velocity, false));
                    }
                }
                else if (pitchedNotes || channel.Type is "gm" or "llama")
                {
                    if (row.Length == 0) continue;
                    string key = pattern.Name + "/" + channel.Name + "/" + row;
                    if (!parsed.TryGetValue(key, out IReadOnlyList<StructuredMidiNote>? notes))
                    {
                        if (!SequenceRows.TryRead(row, rowPpq, tempoText, out notes, out string readError)) throw new AmpleException("AMP5380", $"The notes of '{channel.Name}' in the pattern '{pattern.Name}': {readError}.");
                        parsed[key] = notes;
                    }

                    if (channel.Type == "gm" && rows.GetValueOrDefault(channel.Name + ".events") is { Length: > 0 } eventsRow)
                    {
                        foreach (string entry in eventsRow.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] f = entry.Split(':');
                            if (f.Length != 5 || !int.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out int eventBar) || eventBar != patternBar + 1 || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long eventTick) || f[2] is not ("c" or "p") ||
                                !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int data1) || !int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out int data2)) continue;
                            performance.Add(new MidiPerformanceEvent(checked((int)((long)ob * spb + eventTick * spb / (4L * rowPpq))), f[2] == "c" ? SmfEventKind.ControlChange : SmfEventKind.PitchBend, -1, data1, data2));
                        }
                    }

                    foreach (StructuredMidiNote n in notes.Where(n => n.Bar == patternBar + 1))
                    {
                        if (channel.Type == "llama")
                        {
                            // a Delay Llama channel: the notes are sung together at the end (one voice, with glide between notes that touch)
                            int llamaStart = barStart + (int)((long)n.BeatMicrobeats * spb / 4_000_000);
                            llamaNotes.Add(new DelayLlama.Note(llamaStart, Math.Max(1, (int)((long)n.LengthMicrobeats * spb / 4_000_000)), Math.Clamp(n.Pitch + shift, 0, 127), n.Velocity, n.Fine));
                            if (n.Bend != 0) bends.Add((llamaStart, Math.Max(1, (int)((long)n.LengthMicrobeats * spb / 4_000_000)), n.Bend));
                            continue;
                        }

                        if (channel.Type == "gm")
                        {
                            // a note of the General MIDI drum channel (10) is a hit of the soundfont's drum kit; the others sound with the program and bank of the channel. Pan and fine tune of the note go along.
                            Dictionary<string, object> item = new() { ["bar"] = absolute, ["beat"] = n.BeatMicrobeats / 1_000_000m, ["beats"] = n.LengthMicrobeats / 1_000_000m, ["pitch"] = n.Pitch, ["velocity"] = n.Velocity };
                            if (n.TicksPerQuarter != 1_000_000) { item["tick"] = n.BeatTicks; item["ticks"] = n.LengthTicks; item["ppq"] = n.TicksPerQuarter; }
                            if (n.TieId >= 0)
                            {
                                // the piece after the first one of a note cut at a bar line goes on with it (it is not struck again)
                                if (tieBar.TryGetValue(n.TieId, out int lastBar) && lastBar == absolute - 1) item["tie"] = true;
                                tieBar[n.TieId] = absolute;
                            }

                            if (n.Pan != 0) item["pan"] = n.Pan;
                            if (n.Fine != 0) item["fine"] = n.Fine;
                            if (n.Bend != 0) bends.Add((barStart + (int)((long)n.BeatMicrobeats * spb / 4_000_000), Math.Max(1, (int)((long)n.LengthMicrobeats * spb / 4_000_000)), n.Bend));
                            (n.Channel == 9 ? gmDrumNotes : gmNotes).Add(item);
                            continue;
                        }

                        int start = barStart + (int)((long)n.BeatMicrobeats * spb / 4_000_000);
                        int noteLength = Math.Max(r.SampleRate / 100, (int)((long)n.LengthMicrobeats * spb / 4_000_000));
                        if (n.Bend != 0) bends.Add((start, noteLength, n.Bend));
                        int midi = Math.Clamp(n.Pitch + shift, 0, 127);
                        if (instrument is not null) PlaySet(buf, instrument, shifted, midi, n.Velocity, counter++, start, noteLength, r.SampleRate);
                        else PlayTone(buf, wave, midi, n.Velocity, start, noteLength, r.SampleRate);
                    }
                }
            }
        }

        if (channel.Type == "guitar" && instrument is not null) PlayStrums(buf, instrument, shifted, strums, stagger, r);
        if (channel.Type == "gm" && gmNotes.Count > 0) PlayGm(buf, r, channel, gmNotes, windowBars, drum: false, mix: false, performance, cancellationToken);
        if (channel.Type == "gm" && gmDrumNotes.Count > 0) PlayGm(buf, r, channel, gmDrumNotes, windowBars, drum: true, mix: gmNotes.Count > 0, performance, cancellationToken);
        if (channel.Type == "llama")
        {
            return SingLlama(r, channel, llamaNotes, bends, length, cancellationToken);
        }

        return PitchBend.Notes(buf, r.SampleRate, bends, cancellationToken);
    }

    // a Delay Llama channel: its settings are the words of the channel (vowel=30 voice=60 glide=120 unison=3 echo=20 ...); the bend of its notes is sung natively
    private static short[] SingLlama(RenderRequest r, SeqChannel channel, List<DelayLlama.Note> notes, List<(int Start, int Length, int Bend)> bends, int length, CancellationToken cancellationToken)
    {
        DelayLlama.Settings defaults = new();
        DelayLlama.Settings settings = DelayLlama.Read(name => channel.Get(name)) with
        {
            Vowel = DelayLlama.Shape(channel.Get("vowel"), defaults.Vowel, r, length, vowel: true).Value,
            Voice = DelayLlama.Shape(channel.Get("voice"), defaults.Voice, r, length).Value,
            Mouth = DelayLlama.Shape(channel.Get("mouth"), defaults.Mouth, r, length).Value,
        };
        double[]? bend = null;
        if (bends.Count > 0)
        {
            bend = new double[length];
            foreach ((int start, int span, int cents) in bends)
            {
                for (int i = Math.Max(0, start); i < Math.Min(length, start + span); i++) bend[i] += cents / 100.0 * (i - start) / Math.Max(1, span);
            }
        }

        (double[] left, double[] right) = DelayLlama.Render(notes, settings, r.SampleRate, length, r.Seed ^ (ulong)channel.Name.GetHashCode(StringComparison.Ordinal), bend: bend, cancellationToken: cancellationToken);
        return [.. left.Select((v, i) => (short)Math.Clamp(Math.Round((v + right[i]) / 2 * 32767), short.MinValue, short.MaxValue))];
    }

    // the row of a guitar channel: strum bars (D d U u x .), then the word chords and one chord name for each bar
    private static (string[] Strums, string[] Chords) ParseGuitarRow(SeqChannel channel, SeqPattern pattern, string row)
    {
        int at = row.IndexOf(" chords ", StringComparison.Ordinal);
        string strum = at < 0 ? row : row[..at];
        string[] chords = at < 0 ? [] : row[(at + 8)..].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        string[] bars = strum.Split(['|', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (chords.Length == 0) throw new AmpleException("AMP5380", $"The guitar row of '{channel.Name}' in the pattern '{pattern.Name}' needs chords: for example D...D.u.D.u.D.u. chords Am F.");
        if (bars.Length == 0 || bars.Any(b => b.Any(c => c is not ('.' or 'x' or 'D' or 'd' or 'U' or 'u')))) throw new AmpleException("AMP5380", $"The strums of '{channel.Name}' in the pattern '{pattern.Name}' may only hold D d U u x and . (16 steps a bar, bars separated by |).");
        return (bars, chords);
    }

    // a chord name as the notes the guitar voicing starts from: the root, then the third (minor 3, otherwise 4)
    private static int[] ChordNotes(string name, SeqChannel channel, SeqPattern pattern)
    {
        int root = name.Length > 0 ? "C.D.EF.G.A.B".IndexOf(char.ToUpperInvariant(name[0]), StringComparison.Ordinal) : -1;
        if (root < 0 || !char.IsAsciiLetterUpper(name[0])) throw new AmpleException("AMP5380", $"The chord '{name}' of '{channel.Name}' in the pattern '{pattern.Name}' is not a chord name (like C, Am, F#m, Bb7).");
        int i = 1;
        if (i < name.Length && name[i] == '#') { root++; i++; }
        else if (i < name.Length && name[i] == 'b') { root--; i++; }
        string rest = name[i..];
        int third = rest.StartsWith('m') && !rest.StartsWith("maj", StringComparison.Ordinal) ? 3 : 4;
        int pitch = 48 + (((root % 12) + 12) % 12);
        return [pitch, pitch + third];
    }

    private static void PlayStrums(short[] buf, Instrument instrument, Dictionary<(string, int), short[]> shifted, List<(int At, int[] Chord, bool Down, int Velocity, bool Mute)> events, int stagger, RenderRequest r)
    {
        SampledInstruments.RoundRobins robins = new(r.Seed);
        for (int e = 0; e < events.Count; e++)
        {
            (int at, int[] chord, bool down, int velocity, bool mute) = events[e];
            if (mute) continue;
            int until = e + 1 < events.Count ? events[e + 1].At : buf.Length;
            int[] order = down ? chord : [.. Enumerable.Reverse(chord)];
            for (int i = 0; i < order.Length; i++)
            {
                int start = at + (i * stagger);
                int length = Math.Max(1, until - start + (r.SampleRate / 8));
                SampledInstruments.PlayNote(buf, instrument, shifted, order[i], velocity, robins.Next("notes"), start, length, (int)((long)velocity * 11 / 25), r.SampleRate);
            }
        }
    }

    // the notes go to the General MIDI renderer of the gm block, with the soundfont, program and bank of the channel
    private static void PlayGm(short[] buf, RenderRequest r, SeqChannel channel, List<object> notes, HashSet<int> windowBars, bool drum, bool mix, List<MidiPerformanceEvent> performance, CancellationToken cancellationToken)
    {
        BlockNode block = new() { Type = "gm", Name = channel.Name };
        void Add(string name, string value) => block.Parameters.Add(new Parameter(name, value, 0));
        Add("file", channel.Get("file", SoundFontAsset.GeneralMidi));
        Add("program", drum ? "0" : channel.Get("program", "0"));
        Add("bank", drum ? "0" : channel.Get("bank", "0"));
        if (drum) Add("channel", "9");   // the renderer takes channel 10 with bank 0 as the drum kit (bank 128)
        Add("gain", "0");
        Add("limiter", "soft");
        // the controllers and bends go along in the text too (the renderer reads only the notes of it), so a channel whose events changed is not taken from the cache
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { notes, events = performance.Select(e => new[] { e.Sample, (int)e.Kind, e.Data1, e.Data2 }) });
        // the General MIDI render is exact: the same notes, soundfont, program, bank and timing always give the same audio, so a channel that did not change is not rendered again
        string? key = GmChannelCache.KeyFor(r, channel, json, windowBars, buf.Length, drum);
        if (key is not null && GmChannelCache.TryGet(key, out short[]? cachedAudio, out bool cachedStereo))
        {
            if (cachedStereo && r.Probe is { } cachedProbe) cachedProbe.Seen = true;
            Place(buf, cachedAudio!, mix);
            return;
        }

        RenderRequest request = r with { Block = block, ActiveBars = windowBars, Inputs = new Dictionary<string, byte[]> { ["notes"] = json }, PerformanceEvents = performance.Count > 0 ? [.. performance.OrderBy(e => e.Sample)] : null };
        IReadOnlyList<RenderOutput> outputs = new SampledInstruments.Gm().RenderAsync(request, cancellationToken).AsTask().GetAwaiter().GetResult();
        byte[] gmAudio = outputs.First(o => o.Role == "audio").Data;
        if (ChannelAudio.IsStereo(gmAudio) && r.Probe is { } gmProbe) gmProbe.Seen = true;
        short[] audio = SampleSet.ReadWav(gmAudio, r.SampleRate, r.SampleChannel);
        Place(buf, audio, mix);
        if (key is not null) GmChannelCache.Put(key, audio, ChannelAudio.IsStereo(gmAudio));
    }

    /// <summary>The audio of one General MIDI render goes into the channel's buffer: the first one is copied, the drum hits that follow are added to what is there.</summary>
    private static void Place(short[] buf, short[] audio, bool mix)
    {
        int count = Math.Min(audio.Length, buf.Length);
        if (!mix) { Array.Copy(audio, buf, count); return; }
        for (int i = 0; i < count; i++) buf[i] = FixedAudio.Clip((long)buf[i] + audio[i]);
    }

    private static void MixSample(short[] buf, short[] sample, int start, int gain, int maxLength)
    {
        int count = Math.Min(Math.Min(sample.Length, maxLength), buf.Length - start);
        for (int i = 0; i < count; i++)
        {
            int at = start + i;
            if (at >= 0) buf[at] = FixedAudio.Clip(buf[at] + ((long)sample[i] * gain >> 15));
        }
    }

    private static void PlaySet(short[] buf, Instrument instrument, Dictionary<(string, int), short[]> shifted, int midi, int velocity, int counter, int start, int length, int sampleRate) =>
        SampledInstruments.PlayNote(buf, instrument, shifted, midi, velocity, counter, start, length, velocity * 160, sampleRate);

    // a built-in tone: a wave with a short attack and a release, so it needs no files
    private static void PlayTone(short[] buf, string wave, int midi, int velocity, int start, int length, int rate)
    {
        uint inc = FixedAudio.Increment(midi, rate), phase = 0;
        int attack = Math.Max(1, rate * 4 / 1000), release = Math.Max(1, rate * 50 / 1000), total = length + release;
        long amp = Math.Clamp(velocity, 1, 127) * 90L;
        for (int i = 0; i < total && start + i < buf.Length; i++)
        {
            phase += inc;
            if (start + i < 0) continue;
            int shape = wave switch { "saw" => FixedAudio.Saw(phase), "triangle" => FixedAudio.Triangle(phase), _ => FixedAudio.Square(phase) };
            long env = i < attack ? (long)i * 32768 / attack : i < length ? 32768 : (long)(total - i) * 32768 / release;
            buf[start + i] = FixedAudio.Clip(buf[start + i] + (shape * amp >> 15) * env / 32768 * 4);
        }
    }
}

/// <summary>The audio of General MIDI channels that were rendered in this session, so an unchanged channel of a big score is not rendered again when another channel (or the lane of one) is. In memory only and bounded: it is a speed-up, the take of the block stays the record.</summary>
internal static class GmChannelCache
{
    private const long BudgetBytes = 384L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (short[] Audio, bool Stereo, LinkedListNode<string> Order)> Entries = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Recent = new();
    private static long bytes;

    /// <summary>Everything that decides the audio of the channel, or null when the soundfont cannot be found (the renderer then reports it).</summary>
    public static string? KeyFor(RenderRequest r, SeqChannel channel, byte[] notesJson, HashSet<int> windowBars, int length, bool drum)
    {
        string file = channel.Get("file", SoundFontAsset.GeneralMidi);
        string path = SoundFontAsset.Resolve(r.ProjectRoot, r.AmpleRoot, file);
        if (!File.Exists(path) && string.Equals(file, SoundFontAsset.TemplateDefault, StringComparison.Ordinal)) path = SoundFontAsset.Resolve(r.ProjectRoot, r.AmpleRoot, SoundFontAsset.GeneralMidi);
        if (!File.Exists(path)) return null;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Put(string text) { hash.AppendData(Encoding.UTF8.GetBytes(text)); hash.AppendData([0]); }
        SampledInstruments.Gm renderer = new();
        Put(drum ? "gm-channel/1/drum" : "gm-channel/1");
        Put(renderer.Id + "@" + renderer.Version);
        Put(FileHashCache.Sha256Hex(path));
        Put(channel.Get("program", "0"));
        Put(channel.Get("bank", "0"));
        Put(r.SampleRate.ToString(CultureInfo.InvariantCulture));
        Put(r.SamplesPerBar.ToString(CultureInfo.InvariantCulture));
        Put(r.FirstBar.ToString(CultureInfo.InvariantCulture));
        Put(r.BarCount.ToString(CultureInfo.InvariantCulture));
        Put(r.SampleChannel.ToString(CultureInfo.InvariantCulture));
        Put(length.ToString(CultureInfo.InvariantCulture));
        Put(string.Join(',', windowBars.Order()));
        Put(r.MidiTempoMap is { Count: > 0 } tempos ? string.Join(';', tempos.Select(t => t.Tick.ToString(CultureInfo.InvariantCulture) + ':' + t.MicrosecondsPerQuarter.ToString(CultureInfo.InvariantCulture))) : string.Empty);
        Put(r.Settings is { Count: > 0 } settings ? string.Join(';', settings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + '=' + p.Value)) : string.Empty);
        hash.AppendData(notesJson);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool TryGet(string key, out short[]? audio, out bool stereo)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                Recent.Remove(entry.Order);
                Recent.AddFirst(entry.Order);
                (audio, stereo) = (entry.Audio, entry.Stereo);
                return true;
            }
        }

        (audio, stereo) = (null, false);
        return false;
    }

    public static void Put(string key, short[] audio, bool stereo)
    {
        long size = (long)audio.Length * sizeof(short);
        if (size > BudgetBytes / 2) return;   // one channel that big would push everything else out
        lock (Gate)
        {
            if (Entries.ContainsKey(key)) return;
            while (bytes + size > BudgetBytes && Recent.Last is { } oldest)
            {
                bytes -= (long)Entries[oldest.Value].Audio.Length * sizeof(short);
                Entries.Remove(oldest.Value);
                Recent.RemoveLast();
            }

            Entries[key] = ((short[])audio.Clone(), stereo, Recent.AddFirst(key));
            bytes += size;
        }
    }

    /// <summary>Empties the cache (tests).</summary>
    public static void Clear() { lock (Gate) { Entries.Clear(); Recent.Clear(); bytes = 0; } }
}
