using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Instruments;
using Ample.Audio.Midi;
using Ample.Audio.Random;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The instruments that play real recordings: a drum kit (the <c>drums</c> block with <c>kit samples/&lt;folder&gt;</c>) and a guitar (the <c>guitar</c> block with
/// <c>set samples/&lt;folder&gt;</c>). Everything is integer arithmetic, so the same files, notes and seed give the same samples on every machine.
/// </summary>
internal static class SampledInstruments
{
    /// <summary>The steps of a bar are sixteenth notes: 16 in 4/4, 12 in 3/4 or 6/8, 20 in 5/4.</summary>
    private static int StepsOf(RenderRequest r) => Math.Max(1, (int)Math.Round(4 * r.BeatsPerBar));

    /// <summary>The steps in a bar of a drums or guitar block: its <c>steps</c> parameter (1 to 64), else four for every beat (16 in 4/4, 12 in 3/4).</summary>
    internal static int DrumSteps(RenderRequest r) => int.TryParse(Param(r, "steps", string.Empty).Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out int steps) && steps >= 1 ? Math.Min(steps, 64) : StepsOf(r);
    private static readonly Dictionary<string, string> NamedPatterns = new(StringComparer.Ordinal)
    {
        ["beat-A"] = "kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.",
        ["beat-B"] = "kick:x.....x.x.....x. snare:....x.......x... hat:x.x.x.x.x.x.x.x.",
    };

    private static string Param(RenderRequest r, string name, string fallback) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : (r.Template.Parameters.TryGetValue(name, out string? d) && d.Length > 0 ? d : fallback);

    private static int IntParam(RenderRequest r, string name, int fallback) => int.TryParse(Param(r, name, string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static SampleSet Set(RenderRequest r, string folder, bool asDrumKit = false)
    {
        SampleSet set = SampleSet.Load(r.ProjectRoot, r.AmpleRoot, folder, r.SampleRate, r.SampleChannel, asDrumKit);
        if (set.Stereo && r.Probe is { } probe) probe.Seen = true;
        return set;
    }

    /// <summary>
    /// Plays a sample renderer for a set with stereo files: once for the left channel of every file and once for the right (the same seed, so the same hits), then joins them.
    /// A set without stereo files is rendered once, exactly as before.
    /// </summary>
    internal static async ValueTask<IReadOnlyList<RenderOutput>> Stereo(RenderRequest r, Func<RenderRequest, ValueTask<IReadOnlyList<RenderOutput>>> render, string audioRole = "audio")
    {
        StereoProbe probe = new();
        IReadOnlyList<RenderOutput> first = await render(r with { SampleChannel = r.SampleChannel == 0 ? 1 : r.SampleChannel, Probe = probe }).ConfigureAwait(false);
        if (!probe.Seen || r.SampleChannel != 0) return first;
        IReadOnlyList<RenderOutput> second = await render(r with { SampleChannel = 2, Probe = probe }).ConfigureAwait(false);
        RenderOutput a = first.First(o => o.Role == audioRole), b = second.First(o => o.Role == audioRole);
        (StereoBuffer left, int rate) = ChannelAudio.Decode(a.Data);
        (StereoBuffer right, _) = ChannelAudio.Decode(b.Data);
        short[] rightSide = ChannelAudio.IsStereo(b.Data) ? right.Right : right.Left;
        byte[] joined = StereoWav.Encode(new StereoBuffer(left.Left, rightSide), rate);
        return [.. first.Select(o => o.Role == audioRole ? o with { Data = joined } : o)];
    }

    private sealed class MidiControlLane(MidiPerformanceEvent[] events)
    {
        internal readonly record struct State(int Volume, int Expression, int Bend, bool PedalDown);
        public MidiPerformanceEvent[] Events { get; } = events;
        public State[] Before { get; } = new State[events.Length + 1];
        public int[] NextStop { get; } = new int[events.Length + 1];
        public int[] NextRelease { get; } = new int[events.Length + 1];

        public void Initialize()
        {
            State state = new(127, 127, 8192, false);
            for (int i = 0; i < Events.Length; i++)
            {
                Before[i] = state;
                MidiPerformanceEvent ev = Events[i];
                if (ev.Kind == SmfEventKind.PitchBend) state = state with { Bend = ev.Data1 | ev.Data2 << 7 };
                else if (ev.Kind != SmfEventKind.ControlChange) { }
                else if (ev.Data1 == 7) state = state with { Volume = ev.Data2 };
                else if (ev.Data1 == 11) state = state with { Expression = ev.Data2 };
                else if (ev.Data1 == 64) state = state with { PedalDown = ev.Data2 >= 64 };
                else if (ev.Data1 == 121) state = new State(127, 127, 8192, false);
            }
            Before[Events.Length] = state;
            int stop = Events.Length, release = Events.Length;
            for (int i = Events.Length - 1; i >= 0; i--)
            {
                MidiPerformanceEvent ev = Events[i];
                if (ev.Kind == SmfEventKind.ControlChange && ev.Data1 is 120 or 123) stop = i;
                if (ev.Kind == SmfEventKind.ControlChange && (ev.Data1 is 120 or 121 || ev.Data1 == 64 && ev.Data2 < 64)) release = i;
                NextStop[i] = stop;
                NextRelease[i] = release;
            }
            NextStop[Events.Length] = Events.Length;
            NextRelease[Events.Length] = Events.Length;
        }

        public int LowerBound(int sample)
        {
            int low = 0, high = Events.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (Events[mid].Sample < sample) low = mid + 1; else high = mid;
            }
            return low;
        }
    }

    private readonly record struct MidiTempoPoint(long Tick, BigInteger Elapsed, int MicrosecondsPerQuarter);

    private static MidiTempoPoint[] TempoTimeline(IReadOnlyList<SmfTempo> tempos)
    {
        MidiTempoPoint[] points = new MidiTempoPoint[tempos.Count];
        for (int i = 0; i < tempos.Count; i++)
        {
            BigInteger elapsed = i == 0 ? BigInteger.Zero : points[i - 1].Elapsed + (BigInteger)(tempos[i].Tick - tempos[i - 1].Tick) * tempos[i - 1].MicrosecondsPerQuarter;
            points[i] = new MidiTempoPoint(tempos[i].Tick, elapsed, tempos[i].MicrosecondsPerQuarter);
        }
        return points;
    }

    private static void Mix(short[] buffer, short[] sample, int at, int gainQ15, int maxLength, int fade)
    {
        int n = Math.Min(sample.Length, maxLength);
        for (int i = 0; i < n && at + i < buffer.Length; i++)
        {
            if (at + i < 0)
            {
                continue;
            }

            long v = (long)sample[i] * gainQ15 >> 15;
            if (fade > 0 && i > n - fade)
            {
                v = v * (n - i) / fade;
            }

            buffer[at + i] = FixedAudio.Clip(buffer[at + i] + v);
        }
    }

    /// <summary>General MIDI instrument playback from a project SoundFont preset.</summary>
    public sealed class Gm : IBlockRenderer, IBlockRouter
    {
        // engine float: the same renderer, claiming machine-exact (an SF3 font decodes its samples with floating point)
        private static readonly MachineExactRenderer FloatEngine = new(new Gm());

        /// <summary>The renderer of a gm block: this one, or with <c>engine float</c> the machine-exact one.</summary>
        public RoutedRenderer RendererFor(BlockNode block, IReadOnlyDictionary<string, string> songSettings) => new(MachineExactRenderer.IsFloat(block) ? FloatEngine : this, new Dictionary<string, string>(StringComparer.Ordinal));

        public string BlockType => "gm";
        public string Id => "builtin.gm-sf2";
        public string Version => "33";
        public string Determinism => "exact";
        public string ToolprintJson => JsonSerializer.Serialize(new { engine = "ample-sf2", dsp = "integer" });

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string file = Param(r, "file", "");
            if (file.Length == 0) throw new AmpleException("AMP5311", "The GM instrument needs a project SoundFont file.");
            string path = SoundFontAsset.Resolve(r.ProjectRoot, r.AmpleRoot, file);
            if (!File.Exists(path) && string.Equals(file, SoundFontAsset.TemplateDefault, StringComparison.Ordinal))
            {
                path = SoundFontAsset.Resolve(r.ProjectRoot, r.AmpleRoot, SoundFontAsset.GeneralMidi);   // the template default is not in this project: use the installed General MIDI pack
            }

            string full = File.Exists(path) ? path : throw new AmpleException("AMP5311", $"The GM SoundFont file '{file}' is missing from the project. Use file pack:musescore-general for the installed General MIDI pack, or add the file to the project.");
            int program = IntParam(r, "program", 0);
            int bankNumber = IntParam(r, "bank", 0);
            int channel = IntParam(r, "channel", -1);
            if (program is < 0 or > 127 || bankNumber is < 0 or > 16383 || channel is < -1 or > 15) throw new AmpleException("AMP5311", "The GM program, bank or channel is outside its supported range.");
            int presetBank = channel == 9 && bankNumber == 0 ? 128 : bankNumber;
            int presetProgram = program;
            int gainQ15 = FixedAudio.GainFromDbWide(Math.Clamp(IntParam(r, "gain", 0), -24, FixedAudio.WideMaxDb));
            Sf2Bank bank;
            IReadOnlyList<Sf2PlayableRegion> regions;
            List<string> warnings = [];
            try
            {
                bank = Sf2Reader.Read(full);
                if (!bank.Presets.Any(preset => preset.Bank == presetBank && preset.Program == presetProgram))
                {
                    if (channel == 9 && bank.Presets.Any(preset => preset.Bank == 128 && preset.Program == 0)) (presetBank, presetProgram) = (128, 0);
                    else if (bank.Presets.Any(preset => preset.Bank == 0 && preset.Program == program)) presetBank = 0;
                    else if (bank.Presets.Any(preset => preset.Bank == 0 && preset.Program == 0)) (presetBank, presetProgram) = (0, 0);
                    else throw new Sf2FormatException($"AMP5311: SoundFont preset bank {bankNumber}, program {program} is unavailable and no GM fallback exists");
                    warnings.Add($"SoundFont preset bank {bankNumber}, program {program} is unavailable; using bank {presetBank}, program {presetProgram}.");
                }
                bool floatEngine = MachineExactRenderer.IsFloat(r.Block);
                if (bank.Samples.Any(sample => sample.Compressed) && !floatEngine)
                    throw new AmpleException("AMP5311", $"The SoundFont '{file}' is an SF3 file (Ogg Vorbis samples, decoded with floating point). Add engine float to the block to play it (the render can then differ slightly between machines; AMPLE keeps it per machine), or use an .sf2 version.");
                if (floatEngine) warnings.Add("engine float: this render is machine-exact (the same machine gives the same bytes; another machine may differ slightly)");
                regions = Sf2Program.Resolve(bank, presetBank, presetProgram);
                warnings.AddRange(Sf2Program.UnsupportedFeatures(bank, presetBank, presetProgram));
                if (regions.Any(region => region.InvalidLoop)) warnings.Add("SoundFont loop points fall outside the playable sample; one-shot playback is used for those zones");
            }
            catch (Sf2FormatException ex) { throw new AmpleException("AMP5311", ex.Message); }

            string limiter = Param(r, "limiter", "soft");
            if (limiter is not ("soft" or "off")) throw new AmpleException("AMP5352", "The limiter must be 'soft' or 'off'.");
            long[] buffer = new long[checked(r.BarCount * r.SamplesPerBar)];
            // PR06: a per-note `pan` (from the piano roll's note-property extension) also makes the output stereo, same as a pan generator or CC10
            bool notesHavePan = false;
            if (r.Inputs.TryGetValue("notes", out byte[]? panScanData))
            {
                using JsonDocument panScanDoc = JsonDocument.Parse(panScanData);
                foreach (JsonElement scanNote in panScanDoc.RootElement.GetProperty("notes").EnumerateArray())
                    if (scanNote.TryGetProperty("pan", out JsonElement panScanValue) && panScanValue.GetInt32() != 0) { notesHavePan = true; break; }
            }
            // the output is stereo only when the preset has pan generators, the song has pan (controller 10) events, or a note has its own pan; otherwise it stays mono as before
            // the controllers a midi-clip wrote with its notes (modwheel, volume, ..., cc): control changes on every channel the notes play on, next to those of a MIDI file
            List<MidiPerformanceEvent> performance = [.. r.PerformanceEvents ?? []];
            if (r.Inputs.TryGetValue("notes", out byte[]? controlData) && controlData.AsSpan().IndexOf("\"controls\""u8) >= 0)
            {
                using JsonDocument controlDoc = JsonDocument.Parse(controlData);
                if (controlDoc.RootElement.TryGetProperty("controls", out JsonElement clipControls) && clipControls.ValueKind == JsonValueKind.Array)
                {
                    HashSet<int> noteChannels = [];
                    foreach (JsonElement n in controlDoc.RootElement.GetProperty("notes").EnumerateArray()) noteChannels.Add(n.TryGetProperty("channel", out JsonElement nc) ? nc.GetInt32() : channel);
                    if (noteChannels.Count == 0) noteChannels.Add(channel);
                    foreach (JsonElement entry in clipControls.EnumerateArray())
                    {
                        int number = entry.GetProperty("cc").GetInt32();
                        foreach ((int sample, int value) in ClipControls.Events(entry.GetProperty("value").GetString() ?? string.Empty, entry.GetProperty("unit").GetString() ?? "raw", r.FirstBar, r.SamplesPerBar, r.SampleRate, r.BarCount * r.SamplesPerBar))
                            foreach (int noteChannel in noteChannels) performance.Add(new MidiPerformanceEvent(sample, SmfEventKind.ControlChange, noteChannel, number, value));
                    }

                    performance = [.. performance.OrderBy(e => e.Sample)];
                }
            }

            bool stereo = regions.Any(z => z.PanPermille != 0) || performance.Any(e => e.Kind == SmfEventKind.ControlChange && e.Data1 == 10) || notesHavePan;
            long[]? rightBuffer = stereo ? new long[buffer.Length] : null;
            // the reverb send: every note at its reverb send (generator 16 and controller 91, SF2 8.1.3 and 8.4.9), after its pan, so a song can put its own reverb on it
            long[] sendBuffer = new long[buffer.Length];
            long[]? sendRight = stereo ? new long[buffer.Length] : null;
            // the chorus send the same way (generator 15 and controller 93)
            long[] chorusBuffer = new long[buffer.Length];
            long[]? chorusRight = stereo ? new long[buffer.Length] : null;
            // the level reference: the same notes without volume, expression and the soft pedal, so level auto evens out the preset and keeps the dynamics the song asks for
            long[] referenceLeft = new long[buffer.Length];
            long[]? referenceRight = stereo ? new long[buffer.Length] : null;
            Dictionary<int, int[]> samples = [];   // with the extra bits of 24-bit samples (Sf2Bank.ExtraBits) below the 16
            Dictionary<(int Sample, int Alpha), int[]> smoothed = [];   // a sample low-passed for notes that read it faster than the output rate (see PlaySf2Note)
            MidiTempoPoint[]? tempoTimeline = r.MidiTempoMap is { Count: > 0 } tempos ? TempoTimeline(tempos) : null;
            Dictionary<int, MidiControlLane> controlLanes = [];
            foreach (IGrouping<int, MidiPerformanceEvent> group in performance.Where(e => e.Kind is SmfEventKind.ControlChange or SmfEventKind.PitchBend or SmfEventKind.ChannelPressure or SmfEventKind.PolyphonicPressure).GroupBy(e => e.Channel))
            {
                MidiControlLane lane = new([.. group.OrderBy(e => e.Sample)]);
                lane.Initialize();
                controlLanes[group.Key] = lane;
            }
            List<(int Start, int Duration, int Pitch, int Velocity, int Channel, bool Tied, List<Sf2PlayableRegion> Regions, MidiControlLane? Lane, int Pan, int Cut, int Fine, int Rel, int Port)> pending = [];
            List<(int Start, int End, int Pitch, int Channel, bool Tied)> slideNotes = [];
            List<(int Start, int End, int Pitch, int Channel, int Index)> portNotes = [];
            Dictionary<int, List<(int Start, int End, int Pitch)>> slidePaths = [];
            if (r.Inputs.TryGetValue("notes", out byte[]? noteData))
            {
                using JsonDocument doc = JsonDocument.Parse(noteData);
                foreach (JsonElement note in doc.RootElement.GetProperty("notes").EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bar = note.GetProperty("bar").GetInt32();
                    if (!r.ActiveBars.Contains(bar) || bar < r.FirstBar || bar >= r.FirstBar + r.BarCount) continue;
                    int pitch = note.GetProperty("pitch").GetInt32();
                    int velocity = note.TryGetProperty("velocity", out JsonElement v) ? v.GetInt32() : 96;
                    int notePan = note.TryGetProperty("pan", out JsonElement panEl) ? panEl.GetInt32() : 0;
                    int noteCut = note.TryGetProperty("cut", out JsonElement cutEl) ? cutEl.GetInt32() : 100;
                    int noteFine = note.TryGetProperty("fine", out JsonElement fineEl) ? fineEl.GetInt32() : 0;
                    int noteRel = note.TryGetProperty("rel", out JsonElement relEl) ? relEl.GetInt32() : -1;
                    bool slide = note.TryGetProperty("slide", out JsonElement slideEl) && slideEl.ValueKind == JsonValueKind.True;
                    int port = note.TryGetProperty("port", out JsonElement portEl) ? portEl.GetInt32() : 0;
                    if (channel >= 0 && (note.TryGetProperty("channel", out JsonElement sourceChannel) && sourceChannel.GetInt32() != channel || note.TryGetProperty("bank", out JsonElement sourceBank) && sourceBank.GetInt32() != bankNumber || note.TryGetProperty("program", out JsonElement sourceProgram) && sourceProgram.GetInt32() != program)) continue;
                    long beat;
                    long beats;
                    int ppq;
                    if (note.TryGetProperty("tick", out JsonElement tick) && note.TryGetProperty("ticks", out JsonElement ticks) && note.TryGetProperty("ppq", out JsonElement notePpq))
                    {
                        beat = tick.GetInt64(); beats = ticks.GetInt64(); ppq = notePpq.GetInt32();
                        if (ppq is < 1 or > 1_000_000 || beat < 0 || beat >= 4L * ppq || beats < 1 || beats > 4L * ppq - beat) throw new AmpleException("AMP5502", "The note input has invalid exact MIDI ticks.");
                    }
                    else
                    {
                        beat = note.TryGetProperty("beat", out JsonElement beatValue) ? Microbeats(beatValue) : 0; beats = Microbeats(note.GetProperty("beats")); ppq = StructuredMidiNotes.MicrobeatsPerBeat;
                    }
                    long tickStart = (long)(bar - 1) * 4 * ppq + beat, tickEnd = tickStart + beats;
                    int start = tempoTimeline is not null ? TempoSample(r, tickStart, tempoTimeline) : checked((int)((long)(bar - r.FirstBar) * r.SamplesPerBar + beat * r.SamplesPerBar / (4L * ppq)));
                    int duration = tempoTimeline is not null ? Math.Max(1, TempoSample(r, tickEnd, tempoTimeline) - start) : Math.Max(1, checked((int)(beats * r.SamplesPerBar / (4L * ppq))));
                    int noteChannel = note.TryGetProperty("channel", out JsonElement ch) ? ch.GetInt32() : channel;
                    MidiControlLane? lane = controlLanes.GetValueOrDefault(noteChannel);
                    bool tied = note.TryGetProperty("tie", out JsonElement tie) && tie.ValueKind == JsonValueKind.True;
                    if (slide) { slideNotes.Add((start, start + duration, pitch, noteChannel, tied)); continue; }
                    // a note that goes on over a bar line arrives in two pieces; the second piece continues the first one (it is not struck again)
                    int previous = tied ? pending.FindLastIndex(p => p.Pitch == pitch && p.Channel == noteChannel && Math.Abs(p.Start + p.Duration - start) <= 2) : -1;
                    if (previous >= 0)
                    {
                        (int pStart, int pDuration, int pPitch, int pVelocity, int pChannel, bool pTied, List<Sf2PlayableRegion> pRegions, MidiControlLane? pLane, int pPan, int pCut, int pFine, int pRel, int pPort) = pending[previous];
                        pending[previous] = (pStart, pDuration + duration, pPitch, pVelocity, pChannel, pTied, pRegions, pLane, pPan, pCut, pFine, pRel, pPort);
                        continue;
                    }

                    pending.Add((start, duration, pitch, velocity, noteChannel, tied, [.. regions.Where(z => pitch >= z.KeyLo && pitch <= z.KeyHi && velocity >= z.VelocityLo && velocity <= z.VelocityHi)], lane, notePan, noteCut, noteFine, noteRel, port));
                    if (port > 0) portNotes.Add((start, start + Math.Max(1, port * r.SampleRate / 1000), pitch, noteChannel, pending.Count - 1));
                }
            }

            foreach ((int slideStart, int slideEnd, int slidePitch, int slideChannel, bool slideTied) in slideNotes.OrderBy(n => n.Start))
            {
                int previous = Enumerable.Range(0, pending.Count).Where(i => pending[i].Channel == slideChannel && pending[i].Start <= slideStart && pending[i].Start + pending[i].Duration >= slideStart).OrderByDescending(i => pending[i].Start).DefaultIfEmpty(-1).First();
                if (previous < 0) { warnings.Add("A slide note has no active voice and is silent."); continue; }
                if (!slidePaths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? slidePath)) slidePaths[previous] = slidePath = [];
                if (slideTied && slidePath.Count > 0 && slidePath[^1].End == slideStart && slidePath[^1].Pitch == slidePitch) slidePath[^1] = (slidePath[^1].Start, slideEnd, slidePitch);
                else slidePath.Add((slideStart, slideEnd, slidePitch));
                var baseNote = pending[previous];
                pending[previous] = (baseNote.Start, Math.Max(baseNote.Duration, slideEnd - baseNote.Start), baseNote.Pitch, baseNote.Velocity, baseNote.Channel, baseNote.Tied, baseNote.Regions, baseNote.Lane, baseNote.Pan, baseNote.Cut, baseNote.Fine, baseNote.Rel, baseNote.Port);
            }
            foreach ((int portStart, int portEnd, int portPitch, int portChannel, int portIndex) in portNotes.OrderBy(n => n.Start))
            {
                int previous = Enumerable.Range(0, pending.Count).Where(i => i != portIndex && pending[i].Regions.Count > 0 && pending[i].Channel == portChannel && pending[i].Start < portStart).OrderByDescending(i => pending[i].Start).DefaultIfEmpty(-1).First();
                if (previous < 0) continue;
                if (!slidePaths.TryGetValue(previous, out List<(int Start, int End, int Pitch)>? portPath)) slidePaths[previous] = portPath = [];
                portPath.Add((portStart, portEnd, portPitch));
                var baseNote = pending[previous];
                pending[previous] = (baseNote.Start, Math.Max(baseNote.Duration, portEnd - baseNote.Start), baseNote.Pitch, baseNote.Velocity, baseNote.Channel, baseNote.Tied, baseNote.Regions, baseNote.Lane, baseNote.Pan, baseNote.Cut, baseNote.Fine, baseNote.Rel, baseNote.Port);
                var transitionNote = pending[portIndex];
                pending[portIndex] = (transitionNote.Start, transitionNote.Duration, transitionNote.Pitch, transitionNote.Velocity, transitionNote.Channel, transitionNote.Tied, [], transitionNote.Lane, transitionNote.Pan, transitionNote.Cut, transitionNote.Fine, transitionNote.Rel, transitionNote.Port);
            }
            foreach (List<(int Start, int End, int Pitch)> sortedPath in slidePaths.Values) sortedPath.Sort((a, b) => a.Start.CompareTo(b.Start));

            for (int i = 0; i < pending.Count; i++)
            {
                var (pStart, pDuration, pPitch, pVelocity, pChannel, _, pRegions, pLane, pPan, pCut, pFine, pRel, _) = pending[i];
                slidePaths.TryGetValue(i, out List<(int Start, int End, int Pitch)>? slidePath);
                foreach (Sf2PlayableRegion region in pRegions)
                {
                    // exclusive class (generator 57): a later note on the same channel with a zone of the same class cuts this one off (the closed hi-hat stops the open one)
                    int chokeAt = int.MaxValue;
                    int exclusive = region.Generators is { } regionGens ? regionGens[57] : 0;
                    if (exclusive != 0)
                    {
                        for (int j = 0; j < pending.Count; j++)
                        {
                            if (j == i || pending[j].Channel != pChannel || pending[j].Start <= pStart || pending[j].Start - pStart >= chokeAt) continue;
                            if (pending[j].Regions.Any(other => other.Generators is { } otherGens && otherGens[57] == exclusive)) chokeAt = pending[j].Start - pStart;
                        }
                    }

                    PlaySf2Note(buffer, rightBuffer, sendBuffer, sendRight, chorusBuffer, chorusRight, referenceLeft, referenceRight, bank, samples, smoothed, region, pPitch, pVelocity, pStart, pDuration, gainQ15, r.SampleRate, pLane, pPan, pCut, pFine, pRel, slidePath, chokeAt);
                }
            }
            if (Param(r, "level", "auto") != "raw")
            {
                // presets differ a lot in level (an acoustic piano can be 40 dB below a bass): the loudest sample goes to -6 dBFS at unity gain, and the gain parameter trims from there
                // presets differ a lot in level: the loudest sample of the reference (the notes without volume, expression and soft pedal; without those it is the audio itself) goes to -6 dBFS at unity gain
                long peak = 0;
                foreach (long sample in referenceLeft) peak = Math.Max(peak, Math.Abs(sample));
                if (referenceRight is not null) foreach (long sample in referenceRight) peak = Math.Max(peak, Math.Abs(sample));
                long unity = gainQ15 > 0 ? peak * 32768 / gainQ15 : 0;
                if (unity > 0)
                {
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        buffer[i] = buffer[i] * 16423 / unity;
                        sendBuffer[i] = sendBuffer[i] * 16423 / unity;
                        if (rightBuffer is not null) rightBuffer[i] = rightBuffer[i] * 16423 / unity;
                        if (sendRight is not null) sendRight[i] = sendRight[i] * 16423 / unity;
                        chorusBuffer[i] = chorusBuffer[i] * 16423 / unity;
                        if (chorusRight is not null) chorusRight[i] = chorusRight[i] * 16423 / unity;
                    }
                }
            }

            short[] pcm = [.. buffer.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))];
            byte[] audio = rightBuffer is null ? Wav.Encode(pcm, r.SampleRate) : StereoWav.Encode(new StereoBuffer(pcm, [.. rightBuffer.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))]), r.SampleRate);
            // no note sends anything: silence of the same length (a mono file, the same bytes for every such render, so the takebank keeps it once)
            byte[] Send(long[] left, long[]? right) => !left.Any(sample => sample != 0) && !(right?.Any(sample => sample != 0) ?? false) ? Wav.Encode(new short[buffer.Length], r.SampleRate)
                : right is null ? Wav.Encode([.. left.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))], r.SampleRate)
                : StereoWav.Encode(new StereoBuffer([.. left.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))], [.. right.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))]), r.SampleRate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", audio), new RenderOutput("send", Send(sendBuffer, sendRight)), new RenderOutput("chorus", Send(chorusBuffer, chorusRight)), new RenderOutput("warnings", Encoding.UTF8.GetBytes(string.Join('\n', warnings)))]);
        }

        private static long Microbeats(JsonElement value)
        {
            decimal scaled = value.GetDecimal() * StructuredMidiNotes.MicrobeatsPerBeat;
            if (scaled != decimal.Truncate(scaled) || scaled < 0 || scaled > int.MaxValue) throw new AmpleException("AMP5502", "The note input is not in exact structured MIDI time.");
            return (long)scaled;
        }

        private static int TempoSample(RenderRequest request, long tick, MidiTempoPoint[] tempos)
        {
            int low = 0, high = tempos.Length - 1;
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                if (tempos[mid].Tick <= tick) low = mid; else high = mid - 1;
            }
            BigInteger elapsed = tempos[low].Elapsed + (BigInteger)(tick - tempos[low].Tick) * tempos[low].MicrosecondsPerQuarter;
            BigInteger sample = elapsed * request.SampleRate / ((BigInteger)request.MidiPpq * 1_000_000) - (BigInteger)(request.FirstBar - 1) * request.SamplesPerBar;
            return sample <= 0 ? 0 : sample >= (long)request.BarCount * request.SamplesPerBar ? checked(request.BarCount * request.SamplesPerBar) : (int)sample;
        }

        /// <summary>One event of the channel into its controllers: control changes (with RPN, NRPN and reset), the pitch wheel, channel pressure; key pressure only for this key.</summary>
        private static void Feed(Sf2Controllers controllers, MidiPerformanceEvent ev, int key)
        {
            switch (ev.Kind)
            {
                case SmfEventKind.ControlChange: controllers.Control(ev.Data1, ev.Data2); break;
                case SmfEventKind.PitchBend: controllers.PitchWheel = ev.Data1 | ev.Data2 << 7; break;
                case SmfEventKind.ChannelPressure: controllers.ChannelPressure = ev.Data1; break;
                case SmfEventKind.PolyphonicPressure when ev.Data1 == key: controllers.PolyPressure = ev.Data2; break;
            }
        }

        /// <summary>Four one-pole low passes in a row over a whole sample (the anti-alias filter of a note read faster than the output rate).</summary>
        private static int[] AntiAlias(int[] source, long alpha, int extraBits)
        {
            int[] y = new int[source.Length];
            int top = short.MaxValue << extraBits, bottom = short.MinValue << extraBits;
            long a = 0, b = 0, c = 0, d = 0;
            for (int i = 0; i < source.Length; i++)
            {
                a += alpha * (source[i] - a) >> 15;
                b += alpha * (a - b) >> 15;
                c += alpha * (b - c) >> 15;
                d += alpha * (c - d) >> 15;
                y[i] = (int)Math.Clamp(d, bottom, top);
            }

            return y;
        }

        private static void PlaySf2Note(long[] output, long[]? right, long[] send, long[]? sendRight, long[] chorus, long[]? chorusRight, long[] reference, long[]? referenceRight, Sf2Bank bank, Dictionary<int, int[]> samples, Dictionary<(int Sample, int Alpha), int[]> smoothed, Sf2PlayableRegion region, int pitch, int velocity, int start, int duration, int gainQ15, int outputRate, MidiControlLane? lane, int notePan, int noteCut, int noteFine, int noteRel, IReadOnlyList<(int Start, int End, int Pitch)>? slidePath, int chokeAt = int.MaxValue)
        {
            // a zone with a fixed key (generator 46) or velocity (47) plays every note at that key or velocity (SF2 8.1.2)
            if (region.Generators is { } fixedGens)
            {
                if (fixedGens[46] is >= 0 and <= 127) pitch = fixedGens[46];
                if (fixedGens[47] is >= 1 and <= 127) velocity = fixedGens[47];
            }

            Sf2Sample header = bank.Samples[region.Sample];
            if (!samples.TryGetValue(region.Sample, out int[]? source)) samples[region.Sample] = source = bank.ReadSampleFrames(region.Sample);
            int extraBits = bank.ExtraBits;
            int sampleStart = region.Start - checked((int)header.Start);
            int sampleEnd = region.End - checked((int)header.Start);
            int loopStart = region.LoopStart - checked((int)header.Start);
            int loopEnd = region.LoopEnd - checked((int)header.Start);
            // the settings of this note: the zone's generators with the modulators of its key and velocity (SF2 9.5); a zone made by hand keeps its own fields
            Sf2NoteSettings note = Sf2Program.Note(region, pitch, velocity);
            bool modelled = region.Generators is not null;
            // scale tuning (generator 56): cents per key away from the root; 0 plays every key at the root's pitch
            long ratioQ16 = InstrumentMath.CentsToRatioQ16((long)(pitch - region.Root) * note.ScaleTuning + note.TuneCents);
            long stepQ16 = Math.Max(1, ratioQ16 * header.Rate / outputRate);
            // A note that reads its sample faster than one source sample per output sample (played more than a semitone above its root, or a sample of a higher rate than
            // the output) folds the highs of the sample back below the output's Nyquist: aliasing, heard as hiss and grit. Such a note reads a copy of the sample that is
            // low-passed first, just below the new Nyquist (four one-pole stages, -3 dB together at 0.9 of it), cached per sample and cutoff.
            int[] read = source;
            if (stepQ16 > 69468)
            {
                int rate = checked((int)header.Rate);
                int cutoffHz = (int)Math.Max(20, (long)rate * 65536 * 9 / (20L * stepQ16));
                long alpha = AudioEffects.CutoffCoefficient((int)Math.Min(rate / 2 - 1, (long)cutoffHz * 230 / 100), rate);
                if (!smoothed.TryGetValue((region.Sample, (int)alpha), out int[]? low)) smoothed[(region.Sample, (int)alpha)] = low = AntiAlias(source, alpha, extraBits);
                read = low;
            }

            int attack = modelled ? Sf2Program.Frames(note.VolAttackTc, (uint)outputRate) : checked((int)((long)region.Attack * outputRate / header.Rate));
            int decay = modelled ? Sf2Program.Frames(note.VolDecayTc, (uint)outputRate) : checked((int)((long)region.Decay * outputRate / header.Rate));
            // PR06: a per-note `rel` (0-5000 ms, -1 = the instrument's own default) replaces the SF2 region's own release time outright
            int release = noteRel >= 0 ? Math.Max(1, checked((int)((long)noteRel * outputRate / 1000))) : Math.Max(outputRate / 100, modelled ? Sf2Program.Frames(note.VolReleaseTc, (uint)outputRate) : checked((int)((long)region.Release * outputRate / header.Rate)));
            int antiClickAttack = Math.Max(1, outputRate * 4 / 1000);
            IntegerEnvelope envelope = new(attack, decay, modelled ? FixedAudio.GainFromDb(-Math.Clamp(note.VolSustainCb / 10, 0, 96)) : region.SustainQ15, release);
            Sf2ModEnvelope modEnvelope = new(note.ModEnv, outputRate);
            bool loopUntilRelease = region.Generators is { } loopGens && loopGens[54] == 3;
            // the two LFOs of the zone: the modulation LFO moves pitch, filter and volume, the vibrato LFO the pitch; the modulation envelope can move the pitch too
            Sf2Lfo modLfo = new(note.Lfos.ModDelay, note.Lfos.ModFreq, outputRate), vibLfo = new(note.Lfos.VibDelay, note.Lfos.VibFreq, outputRate);
            bool lfoPitch = note.Lfos.ModToPitch != 0 || note.Lfos.VibToPitch != 0 || note.ModEnvToPitchCents != 0;
            int lfoCents = 0, appliedLfoCents = int.MinValue, lfoVolumeQ15 = 32768;
            // a zone from a soundfont gets the SF2 volume envelope (delay, hold, a fall linear in decibels); a zone made by hand keeps the linear one
            Sf2VolEnvelope? volEnvelope = modelled ? new Sf2VolEnvelope(note.VolDelayTc, note.VolAttackTc, note.VolHoldTc, note.VolDecayTc, note.VolSustainCb, release, outputRate) : null;
            int noteOff = start + duration;
            int eventIndex = lane?.LowerBound(start) ?? 0;
            if (lane is not null)
            {
                int stopIndex = lane.NextStop[eventIndex];
                if (stopIndex < lane.Events.Length && lane.Events[stopIndex].Sample < noteOff) noteOff = Math.Max(start + 1, lane.Events[stopIndex].Sample);
                int offIndex = lane.LowerBound(noteOff);
                if (lane.Before[offIndex].PedalDown)
                {
                    int releaseIndex = lane.NextRelease[offIndex];
                    if (releaseIndex < lane.Events.Length) noteOff = Math.Max(start + 1, lane.Events[releaseIndex].Sample);
                }

                // sostenuto (controller 66): pressed while this note sounds, it holds the note until it is let go (a note that starts while it is down is not held)
                for (int i = eventIndex; i < lane.Events.Length && lane.Events[i].Sample < noteOff; i++)
                {
                    MidiPerformanceEvent press = lane.Events[i];
                    if (press.Kind != SmfEventKind.ControlChange || press.Data1 != 66 || press.Data2 < 64) continue;
                    int up = output.Length;
                    for (int j = i + 1; j < lane.Events.Length; j++)
                    {
                        MidiPerformanceEvent u = lane.Events[j];
                        if (u.Kind == SmfEventKind.ControlChange && (u.Data1 == 66 && u.Data2 < 64 || u.Data1 is 120 or 121 or 123)) { up = u.Sample; break; }
                    }

                    noteOff = Math.Max(noteOff, up);
                    break;
                }
            }
            duration = Math.Max(1, noteOff - start);
            int chokeFade = Math.Max(1, outputRate / 200);   // a choked note fades out in 5 ms, so the cut does not click
            int frames = Math.Min(Math.Min(output.Length - Math.Max(0, start), checked(duration + release)), chokeAt == int.MaxValue ? int.MaxValue : chokeAt + chokeFade);
            long phaseQ16 = (long)sampleStart << 16;
            // the attenuation of the note in centibels (the zone and its velocity modulators; the default one is velocity to attenuation, concave, about 40 log10(v/127) dB) as a gain
            static int GainOfAttenuation(int centibels) => (int)(InstrumentMath.CentsToRatioQ16(-((long)centibels * 199315 / 10000)) >> 1);
            int velocityQ15 = GainOfAttenuation(note.AttenuationCb);
            int referenceQ15 = velocityQ15;   // velocity and the zone without the controllers: the level reference of level auto
            int zoneGainQ15 = modelled ? 32768 : region.GainQ15;   // a modelled zone has its own attenuation in the note already
            MidiControlLane.State initial = lane is null ? new MidiControlLane.State(127, 127, 8192, false) : lane.Before[eventIndex];
            // PR06: a per-note `fine` (-100..100 cents) sets the resting pitch bend before the note starts (the renderer's bend range is a
            // fixed +-200 cents, see bendCents below); an actual PitchBend event still overrides it below, unchanged.
            int volume = initial.Volume, expression = initial.Expression;
            int bend = noteFine != 0 && (initial.Bend - 8192) * 200 / 8192 == noteFine
                ? initial.Bend
                : noteFine != 0 ? Math.Clamp(8192 + noteFine * 8192 / 200, 0, 16383) : initial.Bend;
            int appliedBend = int.MinValue, appliedGlide = int.MinValue;
            MidiPerformanceEvent[] controls = lane?.Events ?? [];
            // controller 1 (modulation wheel) is vibrato up to 50 cents at 5 Hz; controller 74 (brightness, 64 = untouched) closes a one-pole low-pass below 64.
            // PR06: a per-note `cut` (0-100, 100 = open) sets the resting brightness before the note starts; an actual CC74 event still overrides it below, unchanged.
            int modulation = 0, brightness = noteCut != 100 ? (int)Math.Clamp(64 + ((long)(noteCut - 100) * 64 / 100), 0, 64) : 64, appliedVibrato = int.MinValue, vibrato = 0;
            for (int c = 0; c < eventIndex && c < controls.Length; c++)
            {
                if (controls[c].Kind != SmfEventKind.ControlChange) continue;
                if (controls[c].Data1 == 1) modulation = controls[c].Data2;
                else if (controls[c].Data1 == 74) brightness = controls[c].Data2;
            }

            // A zone from a soundfont reads the controllers of its channel through its modulators (SF2 8.4 and its own): volume and expression lower the attenuation on
            // a concave curve, the modulation wheel deepens the vibrato LFO, the reverb and chorus controllers set the sends, and a soundfont's own controller modulators
            // (a modulation wheel that opens the filter, a breath controller) move the note while it sounds. The settings are read again when a controller changes.
            // the controllers of the channel as they stand when the note starts: the pitch-wheel range (RPN 0) for every zone, and for a zone from a soundfont everything its
            // modulators read (pressure, NRPN offsets); a zone made by hand does not read them otherwise
            Sf2Controllers channel = new();
            if (notePan != 0) channel.Cc[10] = (int)Math.Clamp(64 + ((long)notePan * 63 / 100), 1, 127);   // a per-note pan is the resting pan, as below
            for (int c = 0; c < eventIndex && c < controls.Length; c++) Feed(channel, controls[c], pitch);
            // the soft pedal (controller 67) down when the note starts: 4 dB softer and, for a soundfont zone, an octave darker (MIDI leaves the amount to the instrument)
            int softValue = channel.Cc[67];
            for (int c = eventIndex; c < controls.Length && controls[c].Sample <= start; c++) if (controls[c].Kind == SmfEventKind.ControlChange && controls[c].Data1 == 67) softValue = controls[c].Data2;   // pressed at the note's own start counts
            int softCb = softValue >= 64 ? 40 : 0, softCents = softCb > 0 ? 1200 : 0;
            velocityQ15 = GainOfAttenuation(note.AttenuationCb + softCb);
            channel.Cc[7] = volume;
            channel.Cc[11] = expression;
            channel.PitchWheel = bend;
            Sf2Controllers? live = modelled ? channel : null;
            bool liveChanged = false;
            if (live is not null)
            {
                note = Sf2Program.Note(region, pitch, velocity, live);
                velocityQ15 = GainOfAttenuation(note.AttenuationCb + softCb);
                lfoPitch = note.Lfos.ModToPitch != 0 || note.Lfos.VibToPitch != 0 || note.ModEnvToPitchCents != 0;
            }

            long lowpass = 0;
            // the low pass of the zone (initialFilterFc, generator 8, absolute cents), moved by the modulation envelope (generator 11, cents at its peak), with its
            // resonance (initialFilterQ, generator 9: the peak above the level below the cutoff, in centibels; 0 is no peak, a Butterworth). A two-pole state-variable
            // filter, 12 dB an octave as SF2 asks; as in FluidSynth a resonant filter is turned down by 1 / sqrt(Q), so the peak does not jump out. Open from 13500 cents.
            double svfK = 1, svfGain = 1, svfA1 = 0, svfA2 = 0, svfA3 = 0, ic1 = 0, ic2 = 0;
            bool filterOn = false;
            int filterCentsNow = int.MinValue;
            {
                double q = Math.Pow(10, ((note.FilterQCb / 10.0) - 3.01) / 20);
                svfK = 1 / q;
                svfGain = q > 1 ? 1 / Math.Sqrt(q) : 1;
            }

            void SetFilter(int cents)
            {
                double hz = 8.176 * Math.Pow(2, cents / 1200.0);
                if (cents >= 13500 || hz >= outputRate * 0.45) { filterOn = false; return; }
                double g = Math.Tan(Math.PI * hz / outputRate);
                svfA1 = 1 / (1 + (g * (g + svfK)));
                svfA2 = g * svfA1;
                svfA3 = g * svfA2;
                filterOn = true;
            }

            // pan: the pan generator of the zone plus controller 10 (64 is the middle). PR06: a per-note `pan` (-100..100) sets the resting pan
            // before the note starts; an actual CC10 event still overrides it below, unchanged.
            int panController = notePan != 0 ? (int)Math.Clamp(64 + ((long)notePan * 63 / 100), 1, 127) : 64;
            for (int c = 0; c < eventIndex && c < controls.Length; c++) if (controls[c].Kind == SmfEventKind.ControlChange && controls[c].Data1 == 10) panController = controls[c].Data2;
            // A sustain-looped sample keeps looping (its own designed, smooth loop seam) straight through the release: `frames` is hard-capped
            // at duration+release and `envelope.Gain` reaches exactly 0 by the end of it (see above), so there is no risk of a bell or pad
            // "ringing on forever" by letting the loop keep going - the envelope alone silences it. An earlier version exited the loop exactly
            // at the note-off frame instead (playing on into whatever raw data follows loopEnd); that produced an audible click/tick on many
            // real instruments (the exit point rarely lands on the loop's own smooth seam, and the data just past loopEnd is often not meant
            // to be played back at all), confirmed by an isolated A/B render comparison and a listening check (2026-10-15).
            for (int frame = 0; frame < frames; frame++)
            {
                int target = start + frame;
                if (target < 0 || target >= output.Length) continue;
                while (eventIndex < controls.Length && controls[eventIndex].Sample <= target)
                {
                    MidiPerformanceEvent ev = controls[eventIndex++];
                    if (ev.Kind == SmfEventKind.PitchBend) bend = ev.Data1 | ev.Data2 << 7;
                    else if (ev.Kind != SmfEventKind.ControlChange) { }
                    else if (ev.Data1 == 7) volume = ev.Data2;
                    else if (ev.Data1 == 11) expression = ev.Data2;
                    else if (ev.Data1 == 1) modulation = ev.Data2;
                    else if (ev.Data1 == 74) brightness = ev.Data2;
                    else if (ev.Data1 == 10) panController = ev.Data2;
                    else if (ev.Data1 == 121) { volume = 127; expression = 127; bend = 8192; modulation = 0; brightness = 64; }
                    Feed(channel, ev, pitch);
                    if (live is not null) liveChanged = true;
                }
                // sample mode 3 (generator 54): loop while the key is held, then play on through the end of the sample
                bool looping = region.Loop && !(loopUntilRelease && frame >= duration);
                int sourceIndex = (int)(phaseQ16 >> 16);
                if (looping && sourceIndex >= loopEnd) { sourceIndex = loopStart; phaseQ16 = (long)sourceIndex << 16; }
                if (sourceIndex < sampleStart || sourceIndex >= sampleEnd) break;
                int next = sourceIndex + 1;
                if (looping && next >= loopEnd) next = loopStart;
                else if (next >= sampleEnd) next = sampleEnd - 1;
                int fraction = (int)(phaseQ16 & 0xFFFF);
                long interpolated = ((long)read[sourceIndex] * (65536 - fraction) + (long)read[next] * fraction) >> 16;
                int env = volEnvelope is { } sf2Envelope ? sf2Envelope.Gain(frame, duration) : envelope.Gain(frame, duration);
                if (frame < antiClickAttack) env = (int)((long)env * frame / antiClickAttack);
                if (frame >= chokeAt) env = (int)((long)env * Math.Max(0, chokeAt + chokeFade - frame) / chokeFade);
                int tailFade = Math.Max(1, checked((int)(header.Rate / 500)));
                if (!looping && sampleEnd - sourceIndex < tailFade) env = (int)((long)env * (sampleEnd - sourceIndex) / tailFade);
                if (brightness < 64)
                {
                    long alpha = 2048 + ((long)brightness * brightness * (32768 - 2048) / (63 * 63));
                    lowpass += (interpolated - lowpass) * alpha >> 15;
                    interpolated = lowpass;
                }

                if ((frame & 15) == 0)
                {
                    if (liveChanged && live is not null)
                    {
                        // a controller changed: the settings of the note again, with what they move while it sounds (level, tuning, filter and resonance, LFO depths)
                        liveChanged = false;
                        Sf2NoteSettings previous = note;
                        note = Sf2Program.Note(region, pitch, velocity, live);
                        velocityQ15 = GainOfAttenuation(note.AttenuationCb + softCb);
                        // times that a modulator moved: the LFOs keep their phase, the envelopes change only the phases still to come (SF2 leaves this to the player; FluidSynth does the same)
                        if (note.Lfos != previous.Lfos)
                        {
                            modLfo = modLfo.Retuned(new Sf2Lfo(note.Lfos.ModDelay, note.Lfos.ModFreq, outputRate), frame);
                            vibLfo = vibLfo.Retuned(new Sf2Lfo(note.Lfos.VibDelay, note.Lfos.VibFreq, outputRate), frame);
                        }

                        if (note.ModEnv != previous.ModEnv) modEnvelope = modEnvelope.Following(new Sf2ModEnvelope(note.ModEnv, outputRate), frame, duration);
                        if (volEnvelope is { } current && (note.VolDelayTc, note.VolAttackTc, note.VolHoldTc, note.VolDecayTc, note.VolSustainCb) != (previous.VolDelayTc, previous.VolAttackTc, previous.VolHoldTc, previous.VolDecayTc, previous.VolSustainCb))
                            volEnvelope = current.Following(new Sf2VolEnvelope(note.VolDelayTc, note.VolAttackTc, note.VolHoldTc, note.VolDecayTc, note.VolSustainCb, release, outputRate), frame, duration);
                        lfoPitch = note.Lfos.ModToPitch != 0 || note.Lfos.VibToPitch != 0 || note.ModEnvToPitchCents != 0;
                        if (!lfoPitch) lfoCents = 0;
                        if (note.Lfos.ModToVolume == 0) lfoVolumeQ15 = 32768;
                        double liveQ = Math.Pow(10, ((note.FilterQCb / 10.0) - 3.01) / 20);
                        svfK = 1 / liveQ;
                        svfGain = liveQ > 1 ? 1 / Math.Sqrt(liveQ) : 1;
                        filterCentsNow = int.MinValue;
                        appliedBend = int.MinValue;
                    }

                    int modLfoNow = modLfo.At(frame);
                    if (lfoPitch) lfoCents = (int)(((long)note.Lfos.ModToPitch * modLfoNow + (long)note.Lfos.VibToPitch * vibLfo.At(frame) + (long)note.ModEnvToPitchCents * modEnvelope.Level(frame, duration)) / 32768);
                    if (note.Lfos.ModToVolume != 0) lfoVolumeQ15 = Sf2VolEnvelope.GainOfCb(-(long)note.Lfos.ModToVolume * modLfoNow / 32768);   // as FluidSynth: the attenuation swings by the LFO times -amount (a tremolo)
                    int cents = note.FilterCents - (modelled ? softCents : 0) + (int)((long)note.ModEnvToFilterCents * modEnvelope.Level(frame, duration) / 32768) + (int)((long)note.Lfos.ModToFilter * modLfoNow / 32768);
                    if (cents != filterCentsNow) { filterCentsNow = cents; SetFilter(cents); }
                }

                if (filterOn)
                {
                    double v3 = interpolated - ic2;
                    double v1 = (svfA1 * ic1) + (svfA2 * v3);
                    double v2 = ic2 + (svfA2 * ic1) + (svfA3 * v3);
                    ic1 = (2 * v1) - ic1;
                    ic2 = (2 * v2) - ic2;
                    interpolated = (long)Math.Round(v2 * svfGain);
                }
                else
                {
                    ic1 = 0;
                    ic2 = interpolated;   // open: the filter follows the sound, so closing it again does not click
                }

                long sample = interpolated * velocityQ15 >> 15;
                sample >>= extraBits;   // 24-bit samples: back to the 16-bit scale after the filters had the low bits
                sample = sample * zoneGainQ15 >> 15;
                sample = sample * gainQ15 >> 15;
                sample = sample * env >> 15;
                if (lfoVolumeQ15 != 32768) sample = sample * lfoVolumeQ15 >> 15;
                int controllerGainQ15 = live is not null ? 32768 : volume * expression * 32768 / (127 * 127);   // a soundfont zone has volume and expression in its attenuation
                long voiced = sample * controllerGainQ15 / 32768;
                long referenceSample = sample;
                if (referenceQ15 != velocityQ15 || controllerGainQ15 != 32768)
                {
                    referenceSample = (interpolated * referenceQ15 >> 15) >> extraBits;
                    referenceSample = referenceSample * zoneGainQ15 >> 15;
                    referenceSample = referenceSample * gainQ15 >> 15;
                    referenceSample = referenceSample * env >> 15;
                    if (lfoVolumeQ15 != 32768) referenceSample = referenceSample * lfoVolumeQ15 >> 15;
                }

                int reverbSend = note.ReverbPermille, chorusSend = note.ChorusPermille;
                if (right is null)
                {
                    output[target] += voiced;
                    reference[target] += referenceSample;
                    if (reverbSend > 0) send[target] += voiced * reverbSend / 1000;
                    if (chorusSend > 0) chorus[target] += voiced * chorusSend / 1000;
                }
                else
                {
                    // a soundfont zone has controller 10 in its pan through its modulator; a zone made by hand adds it here
                    int position = Math.Clamp((note.PanPermille * PanTable.Range / 500) + (live is not null ? 0 : (panController - 64) * PanTable.Range / 63), -PanTable.Range, PanTable.Range);
                    long left = voiced * PanTable.LeftGain(position) >> 15, rightSide = voiced * PanTable.RightGain(position) >> 15;
                    output[target] += left;
                    reference[target] += referenceSample * PanTable.LeftGain(position) >> 15;
                    if (referenceRight is not null) referenceRight[target] += referenceSample * PanTable.RightGain(position) >> 15;
                    right[target] += rightSide;
                    if (reverbSend > 0)
                    {
                        send[target] += left * reverbSend / 1000;
                        if (sendRight is not null) sendRight[target] += rightSide * reverbSend / 1000;
                    }

                    if (chorusSend > 0)
                    {
                        chorus[target] += left * chorusSend / 1000;
                        if (chorusRight is not null) chorusRight[target] += rightSide * chorusSend / 1000;
                    }
                }
                if (live is null && modulation > 0 && frame % 32 == 0)   // a zone made by hand: the modulation wheel is a vibrato of up to 50 cents at 5 Hz
                {
                    int p = (int)((long)frame * 5 * 1024 / outputRate % 1024);
                    int triangle = p < 512 ? p - 256 : 768 - p;   // -256..256
                    vibrato = modulation * 50 / 127 * triangle / 256;
                }
                else if (modulation == 0 || live is not null) vibrato = 0;
                int glideCents = slidePath is null ? 0 : NoteGlide.Cents(pitch, target, slidePath);
                if (bend != appliedBend || vibrato != appliedVibrato || glideCents != appliedGlide || lfoCents != appliedLfoCents)
                {
                    // the pitch wheel: a soundfont zone has it in its tuning through its modulator (with the range of RPN 0), a zone made by hand bends here by that range
                    int bendCents = (live is not null ? 0 : (bend - 8192) * channel.BendRangeCents / 8192) + vibrato + glideCents + lfoCents;
                    appliedVibrato = vibrato;
                    appliedLfoCents = lfoCents;
                    long bentRatioQ16 = InstrumentMath.CentsToRatioQ16((long)(pitch - region.Root) * note.ScaleTuning + note.TuneCents + bendCents);
                    stepQ16 = Math.Max(1, bentRatioQ16 * header.Rate / outputRate);
                    appliedBend = bend; appliedGlide = glideCents;
                }
                phaseQ16 += stepQ16;
            }
        }
    }

    /// <summary>
    /// The round-robin choices of one render: one <see cref="StableRng"/> stream per voice (block seed xor a constant xor a hash of the voice name), never the
    /// humanize stream, so the humanize bytes of existing takes do not change. A new instance starts every stream again, so a render is a function of seed, voice and hit index.
    /// </summary>
    internal sealed class RoundRobins(ulong seed)
    {
        private const ulong Constant = 0x524F554E44524F42UL;
        private readonly Dictionary<string, StableRng> streams = new(StringComparer.Ordinal);

        public int Next(string voice)
        {
            if (!streams.TryGetValue(voice, out StableRng? rng))
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in voice)
                {
                    h = (h ^ c) * 1099511628211UL;
                }

                streams[voice] = rng = new StableRng(seed ^ Constant ^ h);
            }

            return (int)(rng.Next() >> 33);
        }
    }

    /// <summary>
    /// A little human feel for pitched sample instruments: every note starts up to 6 ms early or late and up to 15 % louder or softer at humanize 100.
    /// It has its own stream (seed xor a constant), so round-robin choices and old takes do not change; at humanize 0 nothing is drawn and nothing moves.
    /// </summary>
    internal sealed class Humanizer(ulong seed, int amount, int sampleRate)
    {
        private readonly StableRng rng = new(seed ^ 0x48554D414E495A45UL);

        public bool On => amount > 0;

        public (int Offset, int GainQ15) Next()
        {
            if (amount <= 0) return (0, 32768);
            int jitter = (int)((rng.Next() >> 40) % 2001) - 1000;     // -1000 .. 1000
            int loud = (int)((rng.Next() >> 40) % 2001) - 1000;
            int offset = (int)((long)jitter * amount * sampleRate / 100 * 6 / 1000 / 1000);
            return (offset, 32768 + (int)((long)loud * amount * 32768 / 100 * 15 / 100 / 1000));
        }
    }

    private static int Humanize(RenderRequest r) => Math.Clamp(IntParam(r, "humanize", 0), 0, 100);

    /// <summary>A MIDI velocity (1 to 127) on the 0 to 32768 scale of the layers.</summary>
    private static int MidiVelocity(int midi) => midi * 32768 / 127;

    /// <summary>Reads <c>open:hat,pedal;crash:ride</c>: the first voice of a rule is cut by a hit of any voice after the colon.</summary>
    private static Dictionary<string, string[]> ParseChoke(string text)
    {
        Dictionary<string, string[]> rules = new(StringComparer.Ordinal);
        foreach (string rule in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = rule.IndexOf(':', StringComparison.Ordinal);
            string[] closers = colon <= 0 ? [] : [.. rule[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => v.ToLowerInvariant())];
            if (closers.Length == 0)
            {
                throw new AmpleException("AMP5351", $"The choke rule '{rule}' must look like open:hat,pedal (the voice that is cut, a colon, the voices that cut it; rules separated by ;).");
            }

            rules[rule[..colon].Trim().ToLowerInvariant()] = closers;
        }

        return rules;
    }

    /// <summary>Reads <c>kick:other-kit/kick2;snare:other-kit/clap</c>: a pattern voice and the sample (folder, a slash, the sample name) that plays instead of the kit's own.</summary>
    internal static Dictionary<string, (string Folder, string Sample)> ParseVoiceMap(string text)
    {
        Dictionary<string, (string Folder, string Sample)> map = new(StringComparer.Ordinal);
        foreach (string rule in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = rule.IndexOf(':', StringComparison.Ordinal);
            string target = colon <= 0 ? string.Empty : rule[(colon + 1)..].Trim().Trim('"');
            int slash = target.LastIndexOf('/');
            if (slash <= 0 || slash == target.Length - 1)
            {
                throw new AmpleException("AMP5351", $"The voice map entry '{rule}' must look like kick:other-kit/kick2 (the voice, a colon, a sample folder, a slash and the sample name; entries separated by ;).");
            }

            map[rule[..colon].Trim().ToLowerInvariant()] = (target[..slash], target[(slash + 1)..].ToLowerInvariant());
        }

        return map;
    }

    /// <summary>The kit folders a drum block names besides its own: the folders of the voice map and the fallback kit.</summary>
    internal static IReadOnlyList<string> ExtraKitFolders(BlockNode block)
    {
        string Value(string name) => block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;
        List<string> folders = [];
        try
        {
            folders.AddRange(ParseVoiceMap(Value("map")).Values.Select(m => m.Folder));
        }
        catch (AmpleException)
        {
            // A bad map is reported by the renderer; it adds no folders to the plan.
        }

        if (Value("fallback").Length > 0)
        {
            folders.Add(Value("fallback"));
        }

        return [.. folders.Distinct(StringComparer.Ordinal)];
    }

    private static int Velocity(char c) => c switch { 'X' => 32768, 'x' => 26000, 'o' => 14000, >= '1' and <= '9' => 32768 * (c - '0') / 9, _ => 0 };

    /// <summary>The drum kit: one WAV per voice, played on a sixteen-step grid per bar with accents, ghost notes, swing and a little deterministic human feel.</summary>
    /// <summary>One drum hit with the envelope of its set: the hit is played in full, so the envelope runs over its length.</summary>
    private static short[] ShapeHit(short[] sample, IntegerEnvelope envelope)
    {
        short[] shaped = new short[sample.Length];
        for (int frame = 0; frame < sample.Length; frame++) shaped[frame] = FixedAudio.Clip((long)sample[frame] * envelope.Gain(frame, sample.Length) >> 15);
        return shaped;
    }

    private sealed class EnvelopeKeyComparer : IEqualityComparer<(short[] Sample, IntegerEnvelope Envelope)>
    {
        public static readonly EnvelopeKeyComparer Instance = new();
        public bool Equals((short[] Sample, IntegerEnvelope Envelope) x, (short[] Sample, IntegerEnvelope Envelope) y) => ReferenceEquals(x.Sample, y.Sample) && x.Envelope == y.Envelope;
        public int GetHashCode((short[] Sample, IntegerEnvelope Envelope) value) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Sample), value.Envelope);
    }

    public static ValueTask<IReadOnlyList<RenderOutput>> Drums(RenderRequest r, string folder)
    {
        SampleSet kit = Set(r, folder, asDrumKit: true);
        string pattern = Param(r, "pattern", "beat-A");
        // the named patterns and the section sequence of the block (plan patterns P01); without a song the block plays its pattern on every bar
        DrumSong song = DrumSong.Parse(Param(r, "patterns", string.Empty), Param(r, "song", string.Empty), NamedPatterns.Keys);
        List<(string Voice, string Steps, int LevelDb, int Pan, bool Mute, int Swing, int Tune)> ParseVoices(string text)
        {
            string grid = NamedPatterns.TryGetValue(text, out string? named) ? named : text;
            List<(string Voice, string Steps, int LevelDb, int Pan, bool Mute, int Swing, int Tune)> voices = [];
            foreach (string part in grid.Split([' ', '|', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new AmpleException("AMP5351", $"The drum text '{text}' is not a name (beat-A, beat-B) or voice grids like kick:x...x... snare:....x...... (x hit, X accent, o ghost, . rest).");
                }

                string steps = part[(colon + 1)..];
                int levelDb = 0, pan = 0, voiceSwing = -1, voiceTune = 0;
                bool mute = false;
                int at = steps.IndexOf('@', StringComparison.Ordinal);
                if (at >= 0)
                {
                    // Options of the voice after an @, separated by commas: m (mute), l<dB> (level, -24 to 12) and p<pan> (-32 left to 32 right), for example kick:x...@l-3,p-20.
                    foreach (string option in steps[(at + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (option == "m")
                        {
                            mute = true;
                        }
                        else if (option.Length > 1 && option[0] == 'l' && int.TryParse(option[1..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int l))
                        {
                            levelDb = Math.Clamp(l, -24, 12);
                        }
                        else if (option.Length > 1 && option[0] == 't' && int.TryParse(option[1..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int tn))
                        {
                            voiceTune = Math.Clamp(tn, -24, 24);
                        }
                        else if (option.Length > 1 && option[0] == 's' && int.TryParse(option[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int sw))
                        {
                            voiceSwing = Math.Clamp(sw, 0, 60);
                        }
                        else if (option.Length > 1 && option[0] == 'p' && int.TryParse(option[1..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int pn))
                        {
                            pan = Math.Clamp(pn, -PanTable.Range, PanTable.Range);
                        }
                        else
                        {
                            throw new AmpleException("AMP5351", $"The option '{option}' of '{part[..colon]}' must be m (mute), l<dB>, p<pan>, s<swing> or t<semitones> (for example kick:x...@l-3,p-20,s30,t-2).");
                        }
                    }

                    steps = steps[..at];
                }

                if (steps.Any(c => c is not ('x' or 'X' or 'o' or '.') and not (>= '1' and <= '9')))
                {
                    throw new AmpleException("AMP5351", $"The grid of '{part[..colon]}' may only hold x, X, o, the digits 1 to 9 (velocity) and . (found '{steps}').");
                }

                voices.Add((part[..colon].ToLowerInvariant(), steps, levelDb, pan, mute, voiceSwing, voiceTune));
            }
            return voices;
        }

        Dictionary<string, List<(string Voice, string Steps, int LevelDb, int Pan, bool Mute, int Swing, int Tune)>> parsed = new(StringComparer.Ordinal);
        List<(string Voice, string Steps, int LevelDb, int Pan, bool Mute, int Swing, int Tune)> VoicesOf(string name)
        {
            if (parsed.TryGetValue(name, out var found)) return found;
            string text = name == DrumSong.MainName ? pattern : song.Patterns.TryGetValue(name, out string? defined) ? defined : name;
            return parsed[name] = ParseVoices(text);
        }

        _ = VoicesOf(DrumSong.MainName);
        foreach (string defined in song.Patterns.Keys) _ = VoicesOf(defined);   // a wrong grid is reported even when the song does not use it
        IReadOnlyList<string> sequence = song.Expand();

        int swing = Math.Clamp(IntParam(r, "swing", 0), 0, 60);
        int human = Math.Clamp(IntParam(r, "humanize", 0), 0, 100);
        StableRng rng = new(r.Seed);
        RoundRobins robins = new(r.Seed);
        short[] buf = new short[r.BarCount * r.SamplesPerBar];
        int stepsPerBar = DrumSteps(r);
        int stepLength = r.SamplesPerBar / stepsPerBar;
        // The block's own choke parameter is voice-name text, independent of any .sfz kit. An .sfz kit's own
        // group/off_by chokes through each hit's resolved Region (SFZ12: region precision, not kit.ChokeRules'
        // coarser voice-name text, which SfzReader still derives for its own documented wire format but which the
        // mixing pass below no longer consults — a voice with mixed regions, only some of them grouped, no longer
        // has every hit choked just because one of its regions is in a group).
        Dictionary<string, string[]> choke = ParseChoke(Param(r, "choke", string.Empty));

        Dictionary<(string, int), short[]> tuned = [];
        Dictionary<(short[] Sample, IntegerEnvelope Envelope), short[]> shaped = new(EnvelopeKeyComparer.Instance);
        List<(int At, string Voice, short[] Sample, int Gain, int Pan, int Polyphony, int Group, int OffBy)> hits = [];

        // Where each pattern voice comes from: a map entry (a sample of another kit), the kit itself, or the fallback kit when the kit lacks the voice (AMP5256).
        List<string> warnings = [.. kit.Warnings];
        Dictionary<string, (string Folder, string Sample)> map = ParseVoiceMap(Param(r, "map", string.Empty));
        string fallback = Param(r, "fallback", string.Empty).Trim().Trim('"');
        Dictionary<string, SampleSet> others = new(StringComparer.Ordinal);
        SampleSet Other(string folderName) => others.TryGetValue(folderName, out SampleSet? found) ? found : others[folderName] = Set(r, folderName, asDrumKit: true);
        Dictionary<string, (SampleSet Set, string Name)> sources = new(StringComparer.Ordinal);
        (SampleSet Set, string Name) SourceOf(string voice)
        {
            if (sources.TryGetValue(voice, out (SampleSet Set, string Name) known))
            {
                return known;
            }

            (SampleSet Set, string Name) source;
            if (map.TryGetValue(voice, out (string Folder, string Sample) mapped))
            {
                SampleSet other = Other(mapped.Folder);
                if (!other.Instrument.HasVoice(mapped.Sample))
                {
                    other.Need(mapped.Sample);
                }

                source = (other, mapped.Sample);
            }
            else if (kit.Instrument.HasVoice(voice))
            {
                source = (kit, voice);
            }
            else if (fallback.Length > 0 && Other(fallback).Instrument.HasVoice(voice))
            {
                warnings.Add($"AMP5256: voice '{voice}' is not in kit '{kit.Folder}'; using '{fallback}'.");
                source = (Other(fallback), voice);
            }
            else
            {
                kit.Need(voice);
                source = (kit, voice);
            }

            return sources[voice] = source;
        }

        Dictionary<string, int> barsPlayed = new(StringComparer.Ordinal);
        foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
        {
            int barStart = (bar - r.FirstBar) * r.SamplesPerBar;
            string barName = DrumSong.NameAtBar(sequence, bar - r.FirstBar);
            List<(string Voice, string Steps, int LevelDb, int Pan, bool Mute, int Swing, int Tune)> barVoices = VoicesOf(barName);
            // a grid longer than a bar goes on over the next bars (32 steps are two bars): the bars this pattern has played so far say where it is
            int played = barsPlayed.GetValueOrDefault(barName);
            barsPlayed[barName] = played + 1;
            foreach ((string voice, string steps, int levelDb, int pan, bool mute, int ownSwing, int voiceTune) in barVoices)
            {
                if (mute)
                {
                    continue;
                }

                (SampleSet source, string sourceName) = SourceOf(voice);

                for (int s = 0; s < stepsPerBar; s++)
                {
                    char c = steps[(steps.Length <= stepsPerBar ? s : (played * stepsPerBar) + s) % steps.Length];
                    if (c == '.')
                    {
                        continue;
                    }

                    int at = barStart + (s * stepLength) + (s % 2 == 1 ? stepLength * (ownSwing >= 0 ? ownSwing : swing) / 100 : 0);
                    int velocity = Velocity(c);
                    if (human > 0)
                    {
                        long jitter = (long)((rng.Next() >> 40) % 2001) - 1000;                               // -1000 .. 1000
                        velocity = (int)Math.Clamp(velocity + (velocity * jitter * human / 500_000), 2000, 32768);  // up to 20 % louder or softer at humanize 100
                        at += (int)((((long)((rng.Next() >> 40) % 1001)) - 500) * human * r.SampleRate / 10_000_000);  // up to 5 ms early or late at humanize 100
                    }

                    Region region = RegionResolver.PickVoice(source.Instrument, sourceName, velocity, robins.Next(voice))!;
                    short[] sample = region.Sample.Samples;
                    int shift = voiceTune - region.Tune;       // the tune of the set moves the sample down, the tune of the voice moves it up or down on top of that
                    if (shift != 0 && !tuned.TryGetValue((region.Sample.Name, shift), out sample!))
                    {
                        sample = tuned[(region.Sample.Name, shift)] = SampleSet.Shift(region.Sample.Samples, shift);
                    }

                    // the set's envelope (set.json) shapes a one-shot hit: attack, decay to the sustain level over the sample, the release at its end (a loop never applies to a hit)
                    if (region.Envelope is { } hitEnvelope)
                    {
                        if (!shaped.TryGetValue((sample, hitEnvelope), out short[]? shapedSample)) shapedSample = shaped[(sample, hitEnvelope)] = ShapeHit(sample, hitEnvelope);
                        sample = shapedSample;
                    }

                    long hitGain = (long)(velocity * 3 / 4) * region.GainQ15 >> 15;
                    if (levelDb != 0)
                    {
                        hitGain = hitGain * FixedAudio.GainFromDb(levelDb) >> 15;
                    }

                    hits.Add((at, voice, sample, (int)hitGain, pan, region.Polyphony, region.Group, region.OffBy));
                }
            }
        }

        // Collect first, then mix: a closing hit (choke rule) cuts the sounding hits of the choked voices with a short fade. Hits are mixed in the order they were made.
        (int At, string Voice, short[] Sample, int Gain, int Pan, int Polyphony, int Group, int OffBy, int Id)[] byTime = [.. hits.Select((h, i) => (h.At, h.Voice, h.Sample, h.Gain, h.Pan, h.Polyphony, h.Group, h.OffBy, Id: i)).OrderBy(h => h.At).ThenBy(h => h.Id)];
        // SFZ12: polyphony/note_polyphony caps how many hits of one voice may sound at once - first-come-first-served,
        // a bounded voice-steal rather than true live-performance stealing (there is no "newest wins" here, since this
        // renders an already-fixed pattern, not a live stream). A voice with no limit (the common case) is never checked.
        HashSet<int> polyphonySkip = [];
        Dictionary<string, List<int>> activeEndsByVoice = [];
        foreach ((int at, string voice, short[] sample, _, _, int polyphony, _, _, int id) in byTime)
        {
            if (polyphony <= 0)
            {
                continue;
            }

            if (!activeEndsByVoice.TryGetValue(voice, out List<int>? ends))
            {
                activeEndsByVoice[voice] = ends = [];
            }

            ends.RemoveAll(end => end <= at);
            if (ends.Count >= polyphony)
            {
                polyphonySkip.Add(id);
                continue;
            }

            ends.Add(at + sample.Length);
        }

        // SFZ03: an .sfz kit's own off_time overrides the fixed ~7 ms choke fade for the whole block.
        int fade = kit.ChokeFadeMs is { } chokeFadeMs ? Math.Max(1, chokeFadeMs) * r.SampleRate / 1000 : r.SampleRate * 7 / 1000;
        bool stereo = hits.Any(h => h.Pan != 0);
        short[]? right = stereo ? new short[buf.Length] : null;
        for (int hitId = 0; hitId < hits.Count; hitId++)
        {
            if (polyphonySkip.Contains(hitId))
            {
                continue;
            }

            (int at, string voice, short[] sample, int gain, int pan, _, int group, _) = hits[hitId];
            int length = sample.Length;
            int cut = -1;
            choke.TryGetValue(voice, out string[]? closers);
            foreach ((int otherAt, string otherVoice, _, _, _, _, _, int otherOffBy, _) in byTime)
            {
                if (otherAt < at || otherAt >= at + sample.Length || otherVoice == voice)
                {
                    continue;
                }

                // The block's own choke parameter (or SFZ03's voice-name choke rules) cuts by voice name; this region's
                // own SFZ off_by, read at region precision (SFZ12), cuts only when the sounding hit's own resolved
                // region is actually in the group its off_by closes - two regions sharing a voice name (for example a
                // soft, unchoked articulation layered with a choked one) no longer choke each other just because they
                // share a voice.
                bool voiceNameChoke = closers is not null && Array.IndexOf(closers, otherVoice) >= 0;
                bool regionChoke = group != 0 && otherOffBy == group;
                if (voiceNameChoke || regionChoke)
                {
                    cut = otherAt - at;
                    break;
                }
            }

            int hitLength = cut >= 0 ? cut : length, hitFade = cut >= 0 ? Math.Min(fade, cut) : 0;
            if (stereo)
            {
                Mix(buf, sample, at, (int)((long)gain * PanTable.LeftGain(pan) >> 15), hitLength, hitFade);
                Mix(right!, sample, at, (int)((long)gain * PanTable.RightGain(pan) >> 15), hitLength, hitFade);
            }
            else
            {
                Mix(buf, sample, at, gain, hitLength, hitFade);
            }
        }

        if (stereo)
        {
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", StereoWav.Encode(new StereoBuffer(buf, right!), r.SampleRate)), new RenderOutput("warnings", System.Text.Encoding.UTF8.GetBytes(string.Join('\n', warnings)))]);
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate)), new RenderOutput("warnings", System.Text.Encoding.UTF8.GetBytes(string.Join('\n', warnings)))]);
    }

    internal static Instrument Pitched(SampleSet set, string folder) => set.Instrument.HasPitched ? set.Instrument : throw new AmpleException("AMP5350", $"The sample folder '{folder}' has no files named by note (like c4.wav, f#3.wav, eb2.wav).");

    internal static void PlayNote(short[] buf, Instrument instrument, Dictionary<(string, int), short[]> shifted, int midi, int velocity, int counter, int start, int length, int amp, int sampleRate, bool legato = false)
    {
        int inputAmp = amp;
        Region region = RegionResolver.Pick(instrument, midi, velocity, counter, legato)!;
        int shift = midi - region.Root - region.Tune;
        amp = (int)((long)amp * region.GainQ15 >> 15);
        if (region.VelCurve is { } curve) amp = (int)((long)amp * curve[Math.Clamp(velocity * 127 / 32768, 0, 127)] >> 15);
        amp = (int)((long)amp * XfadeGainQ15(region, velocity) >> 15);

        // S08: with a loop or a region envelope, the needed output can run longer than the raw sample, and the
        // resulting bytes depend on the note length, so neither can use the plain per-(name,shift) cache; a
        // velocity-tracked cutoff (fil_veltrack) makes the filtered bytes depend on velocity too, same reason.
        bool special = region.LoopMode == "sustain" || region.Envelope is not null || region.FilterVelTrackCents != 0;
        int neededFrames = region.Envelope is { } withTail ? withTail.TotalLength(length) : length;
        short[] sample;
        if (special)
        {
            short[] preShift = region.FilterCutoffHz > 0 ? ApplyFilter(region.Sample.Samples, EffectiveCutoffHz(region, velocity), region.FilterPoles, sampleRate, region.ResonanceDb) : region.Sample.Samples;
            preShift = Loop(region, preShift, shift, neededFrames);
            sample = SampleSet.Shift(preShift, shift);
        }
        else if (region.FilterCutoffHz > 0)
        {
            sample = SampleSet.Shift(ApplyFilter(region.Sample.Samples, region.FilterCutoffHz, region.FilterPoles, sampleRate, region.ResonanceDb), shift);
        }
        else if (!shifted.TryGetValue((region.Sample.Name, shift), out sample!))
        {
            sample = shifted[(region.Sample.Name, shift)] = SampleSet.Shift(region.Sample.Samples, shift);
        }

        if (region.Envelope is { } env)
        {
            int n = Math.Min(sample.Length, neededFrames);
            short[] shaped = new short[n];
            for (int i = 0; i < n; i++)
            {
                shaped[i] = FixedAudio.Clip((long)sample[i] * env.Gain(i, length) >> 15);
            }

            Mix(buf, shaped, start, amp, n, 0);
        }
        else
        {
            Mix(buf, sample, start, amp, length, Math.Min(sampleRate * 12 / 100, length));
        }

        // S08: a release-triggered companion (<name>_rel.wav, or SFZ trigger=release) plays at note-off, from the
        // same velocity/round-robin stream, independent of the main hit's own fade/loop/envelope shaping above.
        if (RegionResolver.PickRelease(instrument, midi, velocity, counter) is { } release)
        {
            short[] relSample = SampleSet.Shift(release.Sample.Samples, midi - release.Root - release.Tune);
            int relAmp = (int)((long)inputAmp * release.GainQ15 >> 15);
            Mix(buf, relSample, start + length, relAmp, relSample.Length, Math.Min(sampleRate * 12 / 100, relSample.Length));
        }
    }

    /// <summary>S08: extends <paramref name="preShiftSource"/> (pre pitch-shift, at the sample's own recorded rate) with <see cref="LoopPlayer"/> when the
    /// region has a sustain loop and the output needs more frames than the raw recording has; unchanged bytes (the same array) otherwise. Sized generously
    /// from the semitone shift's pitch ratio so the post-shift result has at least <paramref name="neededOutputFrames"/> frames.</summary>
    private static short[] Loop(Region region, short[] preShiftSource, int shift, int neededOutputFrames)
    {
        if (region.LoopMode != "sustain")
        {
            return preShiftSource;
        }

        long ratioQ16 = SampleSet.RatioQ16Of(shift);
        int neededSourceFrames = (int)(((long)neededOutputFrames * ratioQ16 >> 16) + 2);
        if (neededSourceFrames <= preShiftSource.Length)
        {
            return preShiftSource;
        }

        LoopPlayer player = new(preShiftSource, region.LoopStart, region.LoopEnd, region.LoopCrossfade);
        short[] extended = new short[neededSourceFrames];
        player.Fill(extended, 0);
        return extended;
    }

    /// <summary>SFZ08: the cutoff a note actually plays at, after <c>fil_veltrack</c> (cents at max velocity, relative to velocity 0) moves it; the plain <c>FilterCutoffHz</c> when there is no tracking (the common case).</summary>
    private static int EffectiveCutoffHz(Region region, int velocity)
    {
        if (region.FilterVelTrackCents == 0)
        {
            return region.FilterCutoffHz;
        }

        long cents = (long)region.FilterVelTrackCents * Math.Clamp(velocity, 0, 32768) / 32768;
        return Math.Max(1, (int)((long)region.FilterCutoffHz * InstrumentMath.CentsToRatioQ16(cents) >> 16));
    }

    /// <summary>
    /// SFZ12: <c>xfin_lovel</c>/<c>xfin_hivel</c>/<c>xfout_lovel</c>/<c>xfout_hivel</c> as a one-sided gain taper on the one region
    /// <see cref="RegionResolver.Choose"/> already picked by its hard <c>VelLo</c>/<c>VelHi</c> range — a real, audible smoothing of
    /// that region's own edges, not the full spec behaviour (two neighbouring regions sounding together, weighted oppositely): that
    /// would need the resolver to return more than one region, a bigger change than this chunk makes. Q15, 32768 = no taper.
    /// </summary>
    private static int XfadeGainQ15(Region region, int velocity)
    {
        int gain = 32768;
        if (region.XfinLoVel >= 0 && region.XfinHiVel > region.XfinLoVel)
        {
            gain = velocity <= region.XfinLoVel ? 0
                 : velocity >= region.XfinHiVel ? 32768
                 : (int)((long)(velocity - region.XfinLoVel) * 32768 / (region.XfinHiVel - region.XfinLoVel));
        }

        if (region.XfoutLoVel >= 0 && region.XfoutHiVel > region.XfoutLoVel)
        {
            int outGain = velocity >= region.XfoutHiVel ? 0
                        : velocity <= region.XfoutLoVel ? 32768
                        : (int)((long)(region.XfoutHiVel - velocity) * 32768 / (region.XfoutHiVel - region.XfoutLoVel));
            gain = Math.Min(gain, outGain);
        }

        return gain;
    }

    /// <summary>SFZ08: a static low-pass (SFZ <c>cutoff</c>/<c>fil_type lpf_1p</c>/<c>lpf_2p</c>), reusing the existing integer one-pole filter (<see cref="AudioEffects.LowPass"/>) instead of new filter DSP. No caching (the cutoff is per region, not per sample file): correctness over render speed for this first cut.
    /// <paramref name="resonanceDb"/> (SFZ <c>resonance</c>) is an approximation - a gain bump in the band just below the cutoff (the difference
    /// between the cutoff lowpass and a lower one, the same band-carving technique the <c>eq</c> block's "mid" already uses), not a true resonant
    /// pole with the classic self-oscillating peak; see docs/samples.md "SFZ sets".</summary>
    private static short[] ApplyFilter(short[] samples, int cutoffHz, int poles, int sampleRate, int resonanceDb = 0)
    {
        int[] x = [.. samples.Select(s => (int)s)];
        int[] y = AudioEffects.LowPass(x, cutoffHz, sampleRate);
        if (poles >= 2)
        {
            y = AudioEffects.LowPass(y, cutoffHz, sampleRate);
        }

        if (resonanceDb > 0)
        {
            int belowHz = Math.Clamp(cutoffHz * 2 / 3, 20, cutoffHz);
            int[] below = AudioEffects.LowPass(x, belowHz, sampleRate);
            long boostQ15 = FixedAudio.GainFromDbWide(Math.Clamp(resonanceDb, 0, FixedAudio.WideMaxDb)) - 32768;
            for (int i = 0; i < y.Length; i++)
            {
                y[i] = FixedAudio.Clip(y[i] + ((y[i] - below[i]) * boostQ15 >> 15));
            }
        }

        return [.. y.Select(v => FixedAudio.Clip(v))];
    }

    internal static void PlayExpressiveNote(short[] buffer, Instrument instrument, int pitch, int velocity, int counter, int start, int held, int amp, RenderRequest request, SampleNoteExpression expression, IReadOnlyList<(int Start, int End, int Pitch)> path, bool legato = false)
    {
        int inputAmp = amp;
        Region region = RegionResolver.Pick(instrument, pitch, velocity, counter, legato)!;
        // SFZ08 fileg_*: a time-varying cutoff cannot be pre-baked into the sample buffer the way a static one is
        // (ApplyFilter below) - it needs a fresh coefficient every frame, done inline in the loop instead.
        bool hasFilterEnvelope = region.FilterEnvelope is not null;
        short[] source = region.FilterCutoffHz > 0 && !hasFilterEnvelope ? ApplyFilter(region.Sample.Samples, EffectiveCutoffHz(region, velocity), region.FilterPoles, request.SampleRate, region.ResonanceDb) : region.Sample.Samples;
        int filterEnvelopeBaseHz = EffectiveCutoffHz(region, velocity);
        long filterEnvPole1 = 0;
        long filterEnvPole2 = 0;
        // S08: a region envelope applies only when the note itself does not already carry an explicit release (expression.Rel >= 0 always wins - a
        // per-note override is more specific than the sample's own authored envelope); unchanged (today's fixed hold+linear-release) otherwise.
        IntegerEnvelope? regionEnvelope = expression.Rel < 0 ? region.Envelope : null;
        long phase = 0;
        long filtered = 0;
        int release = expression.Rel < 0 ? request.SampleRate * 12 / 100 : Math.Max(1, expression.Rel * request.SampleRate / 1000);
        int tail = regionEnvelope is { } re ? re.TotalLength(held) : Math.Max(1, held + release);
        LoopPlayer? loop = region.LoopMode == "sustain" ? new LoopPlayer(source, region.LoopStart, region.LoopEnd, region.LoopCrossfade) : null;
        int pan = Math.Clamp(expression.Pan, -100, 100) * PanTable.Range / 100;
        if (expression.Pan != 0 && request.Probe is { } probe) probe.Seen = true;
        int panGain = request.SampleChannel == 2 ? PanTable.RightGain(pan) : PanTable.LeftGain(pan);
        amp = (int)((long)amp * region.GainQ15 >> 15);
        if (region.VelCurve is { } curve) amp = (int)((long)amp * curve[Math.Clamp(velocity * 127 / 32768, 0, 127)] >> 15);
        amp = (int)((long)amp * XfadeGainQ15(region, velocity) >> 15);
        if (expression.Pan != 0) amp = (int)((long)amp * panGain >> 15);
        int alpha = Math.Clamp(expression.Cut * 32767 / 100, 128, 32767);
        for (int frame = 0; frame < tail && (loop is not null || phase >> 16 < source.Length - 1); frame++)
        {
            int at = start + frame;
            if (at >= buffer.Length) break;
            int index = (int)(phase >> 16);
            int fraction = (int)(phase & 65535);
            long value = loop is { } lp
                ? ((long)lp.At(index) * (65536 - fraction) + (long)lp.At(index + 1) * fraction) >> 16
                : ((long)source[index] * (65536 - fraction) + (long)source[index + 1] * fraction) >> 16;
            if (hasFilterEnvelope)
            {
                long envGain = region.FilterEnvelope!.Value.Gain(frame, held);
                long cutoffCents = (long)region.FilterEnvelopeDepthCents * envGain / 32768;
                int frameCutoffHz = (int)Math.Clamp((long)filterEnvelopeBaseHz * InstrumentMath.CentsToRatioQ16(cutoffCents) >> 16, 1, request.SampleRate / 2 - 1);
                long envAlpha = AudioEffects.Coefficient(frameCutoffHz, request.SampleRate);
                filterEnvPole1 += (value - filterEnvPole1) * envAlpha >> 15;
                value = region.FilterPoles >= 2 ? (filterEnvPole2 += (filterEnvPole1 - filterEnvPole2) * envAlpha >> 15) : filterEnvPole1;
            }

            filtered += (value - filtered) * alpha >> 15;
            long envelope = regionEnvelope is { } re2 ? re2.Gain(frame, held) : frame < held ? 32768 : (long)(tail - frame) * 32768 / release;
            if (at >= 0) buffer[at] = FixedAudio.Clip(buffer[at] + filtered * amp * envelope / (32768L * 32768));
            long lfoCents = 0;
            if (region.LfoFreqMilliHz != 0 && frame >= region.LfoDelaySamples)
            {
                long lfoFrame = frame - region.LfoDelaySamples;
                long lfoPhaseQ16 = lfoFrame * region.LfoFreqMilliHz % (1000L * request.SampleRate) * 65536 / (1000L * request.SampleRate);
                lfoCents = (long)region.LfoDepthCents * LfoTriangleQ15(lfoPhaseQ16) / 32768;
            }

            long cents = (long)(pitch - region.Root - region.Tune) * 100 + expression.Fine + NoteGlide.Cents(pitch, at, path) + lfoCents;
            phase += Math.Max(1, InstrumentMath.CentsToRatioQ16(cents));
        }

        // S08: a release-triggered companion (<name>_rel.wav, or SFZ trigger=release) plays at note-off.
        if (RegionResolver.PickRelease(instrument, pitch, velocity, counter) is { } releaseRegion)
        {
            short[] relSample = SampleSet.Shift(releaseRegion.Sample.Samples, pitch - releaseRegion.Root - releaseRegion.Tune);
            int relAmp = (int)((long)inputAmp * releaseRegion.GainQ15 >> 15);
            Mix(buffer, relSample, start + held, relAmp, relSample.Length, Math.Min(request.SampleRate * 12 / 100, relSample.Length));
        }
    }

    /// <summary>SFZ09: a deterministic integer triangle wave, one period per 65536 (Q16) of <paramref name="phaseQ16"/>, range -32768..32767 (Q15).
    /// Triangle is SFZ's own default wave and the only one this first cut supports; any other <c>lfoN_wave</c> value is kept-and-warned and still
    /// renders as a triangle (an honest approximation, not a silently wrong shape).</summary>
    private static int LfoTriangleQ15(long phaseQ16)
    {
        long t = ((phaseQ16 % 65536) + 65536) % 65536;
        long v = t < 32768 ? (-32768 + (t * 2)) : (32768 - ((t - 32768) * 2));
        return (int)Math.Clamp(v, -32768, 32767);
    }

    /// <summary>A piano (or any pitched instrument) from a sample folder: every note that arrives plays at its place and length, from the nearest recorded note moved to the right pitch.</summary>
    public static ValueTask<IReadOnlyList<RenderOutput>> Keys(RenderRequest r, string folder)
    {
        Instrument notes = Pitched(Set(r, folder), folder);
        int gain = FixedAudio.GainFromDbWide(Math.Clamp(IntParam(r, "gain", 0), -24, FixedAudio.WideMaxDb));
        short[] buf = new short[r.BarCount * r.SamplesPerBar];
        Dictionary<(string, int), short[]> shifted = [];
        RoundRobins robins = new(r.Seed);
        Humanizer human = new(r.Seed, Humanize(r), r.SampleRate);
        var expressionPlan = SampleNoteExpression.Plan(SampleNoteExpression.Read(r), r.SampleRate);
        // SFZ09: a pitch LFO needs the per-frame expressive playback path; force it for the whole block when any
        // region has one configured, since which region a given note picks (and so whether it has an LFO) is only
        // known after RegionResolver.Pick runs, too late to decide per note before this branch.
        bool expressive = expressionPlan.Voices.Any(n => n.Pan != 0 || n.Fine != 0 || n.Rel >= 0 || n.Cut != 100) || expressionPlan.Paths.Count > 0 || notes.NeedsPerFrameModulation;
        // SFZ12 trigger=first/legato: only worth tracking "is a previous note still sounding" when some region
        // actually reads it; a plain (all-attack) instrument never pays for this. The notes of each loop below
        // already arrive in non-decreasing start order, so a single running "previous note's end" is enough -
        // the offline equivalent of "was anything held when this note began" (no live performer, no ambiguity).
        int previousEnd = int.MinValue;
        if (expressive)
        {
            for (int i = 0; i < expressionPlan.Voices.Count; i++)
            {
                SampleNoteExpression note = expressionPlan.Voices[i];
                (int offset, int loud) = human.Next();
                int amp = (int)((long)gain * 11000 / 32768 * note.Velocity / 96 * loud >> 15);
                bool legato = notes.HasTriggerVariants && note.Start < previousEnd;
                if (notes.HasTriggerVariants) previousEnd = Math.Max(previousEnd, note.Start + note.Held);
                PlayExpressiveNote(buf, notes, note.Pitch, MidiVelocity(note.Velocity), robins.Next("notes"), note.Start + offset, note.Held, amp, r, note, expressionPlan.Paths.GetValueOrDefault(i) ?? [], legato);
            }

            if (expressionPlan.Voices.Count > 0)
            {
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate))]);
            }
            // No explicit note-expression list at all (plain chords-driven content): fall through to the ordinary
            // note-event loop below, still played through PlayExpressiveNote so an LFO still applies.
        }

        foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
        {
            foreach ((int pitch, double beat, double beats, int vel) in BuiltInRenderers.NoteEventsOf(r, bar))
            {
                (int offset, int loud) = human.Next();
                int start = ((bar - r.FirstBar) * r.SamplesPerBar) + (int)(beat * r.SamplesPerBar / r.BeatsPerBar) + offset;
                int length = Math.Max(1, (int)(beats * r.SamplesPerBar / r.BeatsPerBar)) + (r.SampleRate / 4);
                int amp = (int)((long)gain * 11000 / 32768 * vel / 96 * loud >> 15);
                bool legato = notes.HasTriggerVariants && start < previousEnd;
                if (notes.HasTriggerVariants) previousEnd = Math.Max(previousEnd, start + length);
                if (notes.NeedsPerFrameModulation) PlayExpressiveNote(buf, notes, pitch, MidiVelocity(vel), robins.Next("notes"), start, length, amp, r, NeutralExpression(start, length, pitch, vel), [], legato);
                else PlayNote(buf, notes, shifted, pitch, MidiVelocity(vel), robins.Next("notes"), start, length, amp, r.SampleRate, legato);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate))]);
    }

    /// <summary>A neutral <see cref="SampleNoteExpression"/> (no pan/fine/release/cut change) used to force a note through <see cref="PlayExpressiveNote"/>
    /// purely for a region's pitch LFO, when the block has no actual note-expression data of its own.</summary>
    private static SampleNoteExpression NeutralExpression(int start, int held, int pitch, int velocity) => new(start, held, pitch, velocity, 0, 0, 0, -1, 100, false, false, -1);

    /// <summary>A bass from a sample folder: the root of each bar (an octave under the lowest note that arrives) on every beat.</summary>
    public static ValueTask<IReadOnlyList<RenderOutput>> Bass(RenderRequest r, string folder)
    {
        Instrument notes = Pitched(Set(r, folder), folder);
        int gain = FixedAudio.GainFromDbWide(Math.Clamp(IntParam(r, "gain", 0), -24, FixedAudio.WideMaxDb));
        short[] buf = new short[r.BarCount * r.SamplesPerBar];
        Dictionary<(string, int), short[]> shifted = [];
        RoundRobins robins = new(r.Seed);
        int beat = (int)(r.SamplesPerBar / r.BeatsPerBar);
        Humanizer human = new(r.Seed, Humanize(r), r.SampleRate);
        var expressionPlan = SampleNoteExpression.Plan(SampleNoteExpression.Read(r), r.SampleRate);
        bool expressive = expressionPlan.Voices.Any(n => n.Pan != 0 || n.Fine != 0 || n.Rel >= 0 || n.Cut != 100) || expressionPlan.Paths.Count > 0 || notes.NeedsPerFrameModulation;
        if (expressionPlan.Paths.Count > 0)
        {
            for (int i = 0; i < expressionPlan.Voices.Count; i++)
            {
                SampleNoteExpression note = expressionPlan.Voices[i];
                (int offset, int loud) = human.Next();
                int amp = (int)((long)gain * 20000 / 32768 * note.Velocity / 96 * loud >> 15);
                IReadOnlyList<(int Start, int End, int Pitch)> path = expressionPlan.Paths.GetValueOrDefault(i)?.Select(p => (p.Start, p.End, p.Pitch - 12)).ToArray() ?? [];
                PlayExpressiveNote(buf, notes, note.Pitch - 12, MidiVelocity(note.Velocity), robins.Next("notes"), note.Start + offset, note.Held, amp, r, note, path);
            }
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate))]);
        }
        foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
        {
            (int Pitch, double Beat, double Beats, int Velocity)[] events = [.. BuiltInRenderers.NoteEventsOf(r, bar)];
            if (events.Length == 0)
            {
                continue;
            }

            (int Pitch, double Beat, double Beats, int Velocity) low = events.OrderBy(e => e.Pitch).First();
            SampleNoteExpression? expression = expressive ? expressionPlan.Voices.Where(n => n.Start / r.SamplesPerBar == bar - r.FirstBar).OrderBy(n => n.Pitch).FirstOrDefault() : null;
            for (int b = 0; b < 4; b++)
            {
                (int offset, int loud) = human.Next();
                int at = ((bar - r.FirstBar) * r.SamplesPerBar) + (b * beat) + offset;
                int amp = (int)((long)gain * 20000 / 32768 * low.Velocity / 96 * loud >> 15);
                if (expression is null && !notes.NeedsPerFrameModulation) PlayNote(buf, notes, shifted, low.Pitch - 12, MidiVelocity(low.Velocity), robins.Next("notes"), at, beat + (r.SampleRate / 8), amp, r.SampleRate);
                else PlayExpressiveNote(buf, notes, low.Pitch - 12, MidiVelocity(low.Velocity), robins.Next("notes"), at, beat, amp, r, expression ?? NeutralExpression(at, beat, low.Pitch - 12, MidiVelocity(low.Velocity)), []);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate))]);
    }

    /// <summary>The guitar: a strum pattern over the chords that arrive as notes, played from recorded notes pitched to the nearest sample.</summary>
    public sealed class Guitar : IBlockRenderer
    {
        internal static readonly Dictionary<char, (bool Down, int Velocity)> Strokes = new() { ['D'] = (true, 32768), ['d'] = (true, 20000), ['U'] = (false, 26000), ['u'] = (false, 16000) };

        /// <inheritdoc />
        public string BlockType => "guitar";

        /// <inheritdoc />
        public string Id => "builtin.guitar";

        /// <inheritdoc />
        public string Version => "7";

        /// <inheritdoc />
        public string Determinism => "exact";

        /// <inheritdoc />
        public string ToolprintJson => JsonSerializer.Serialize(new { engine = "ample-sampler", dsp = "integer" });

        /// <inheritdoc />
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(r);
            string folder = Param(r, "set", string.Empty);
            if (!folder.Contains('/', StringComparison.Ordinal))
            {
                throw new AmpleException("AMP5350", "The guitar needs a sample folder: set samples/<folder> (a folder of WAV files named by note: e2.wav, a2.wav, c3.wav, eb3.wav...).");
            }

            if (r.Probe is null && r.SampleChannel == 0) return Stereo(r, pass => RenderAsync(pass, cancellationToken));
            SampleSet set = Set(r, folder);
            Instrument notes = Pitched(set, folder);

            string strum = Param(r, "strum", "D...D...D.u.D.u.");
            string voicing = Param(r, "voicing", "open");
            if (voicing is not ("open" or "triad" or "power"))
            {
                throw new AmpleException("AMP5351", $"voicing must be open, triad or power (found '{voicing}').");
            }

            // named strums and the section song (plan patterns): each section plays one of them, the word pattern is the plain strum parameter
            DrumSong song = DrumSong.Parse(Param(r, "patterns", string.Empty), Param(r, "song", string.Empty), []);
            IReadOnlyList<string> sequence = song.Expand();
            string[] bars = strum.Split(['|', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (bars.Length == 0 || bars.Any(b => b.Any(c => c is not ('.' or 'x' or 'D' or 'd' or 'U' or 'u'))))
            {
                throw new AmpleException("AMP5351", $"The strum pattern '{strum}' may only hold D d U u x and . (D down, U up, capitals loud, x mute, . let it ring), 16 steps per bar (or the steps parameter), bars separated by |.");
            }

            // a strum of a whole number of bars (32 characters in a bar of 16 steps) goes on over the next bars, as a drum grid does; any other length plays as before
            int strumSteps = DrumSteps(r);
            static string[] Cut(string[] strums, int steps) => [.. strums.SelectMany(b => b.Length > steps && b.Length % steps == 0 ? Enumerable.Range(0, b.Length / steps).Select(i => b.Substring(i * steps, steps)) : new[] { b })];
            bars = Cut(bars, strumSteps);
            Dictionary<string, string[]> strumBars = new(StringComparer.Ordinal) { [DrumSong.MainName] = bars };
            foreach ((string name, string text) in song.Patterns)
            {
                string[] own = text.Split(['|', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (own.Length == 0 || own.Any(b => b.Any(c => c is not ('.' or 'x' or 'D' or 'd' or 'U' or 'u'))))
                {
                    throw new AmpleException("AMP5351", $"The strum pattern '{name}' ('{text}') may only hold D d U u x and . (D down, U up, capitals loud, x mute, . let it ring), 16 steps per bar (or the steps parameter), bars separated by |.");
                }

                strumBars[name] = Cut(own, strumSteps);
            }

            int staggerSamples = Math.Clamp(IntParam(r, "stagger", 22), 0, 80) * r.SampleRate / 1000;
            int gainQ15 = FixedAudio.GainFromDbWide(Math.Clamp(IntParam(r, "gain", 0), -24, FixedAudio.WideMaxDb));
            int stepsPerBar = DrumSteps(r);   // the steps parameter, else four for every beat
            int stepLength = r.SamplesPerBar / stepsPerBar;
            short[] buf = new short[r.BarCount * r.SamplesPerBar];
            List<(int At, int[] Chord, bool Down, int Velocity, bool Mute)> events = [];
            foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
            {
                int[] chord = Voice(Pitches(r, bar), voicing);
                if (chord.Length == 0)
                {
                    continue;
                }

                string[] of = strumBars[DrumSong.NameAtBar(sequence, bar - r.FirstBar)];
                string pattern = sequence.Count == 0 ? bars[(bar - 1) % bars.Length] : of[(bar - r.FirstBar) % of.Length];
                for (int s = 0; s < stepsPerBar; s++)
                {
                    char c = pattern[s % pattern.Length];
                    int at = ((bar - r.FirstBar) * r.SamplesPerBar) + (s * stepLength);
                    if (c == 'x')
                    {
                        events.Add((at, [], true, 0, true));
                    }
                    else if (Strokes.TryGetValue(c, out (bool Down, int Velocity) stroke))
                    {
                        events.Add((at, chord, stroke.Down, stroke.Velocity, false));
                    }
                }
            }

            Dictionary<(string, int), short[]> shifted = [];
            RoundRobins robins = new(r.Seed);
            Humanizer human = new(r.Seed, Humanize(r), r.SampleRate);
            var expressionPlan = SampleNoteExpression.Plan(SampleNoteExpression.Read(r), r.SampleRate);
            for (int e = 0; e < events.Count; e++)
            {
                (int at, int[] chord, bool down, int velocity, bool mute) = events[e];
                if (mute)
                {
                    continue;
                }

                int until = e + 1 < events.Count ? events[e + 1].At : buf.Length;
                int[] order = down ? chord : [.. Enumerable.Reverse(chord)];   // not chord.Reverse(): newer compilers pick the in-place span Reverse, which returns nothing
                for (int i = 0; i < order.Length; i++)
                {
                    int midi = order[i];
                    (int offset, int loud) = human.Next();
                    int start = at + (i * staggerSamples) + offset;
                    int length = Math.Max(1, until - start + (r.SampleRate / 8));
                    int amp = (int)((long)velocity * gainQ15 / 32768 * 11 / 25 * loud >> 15);
                    int barIndex = Math.Clamp(at / r.SamplesPerBar, 0, r.BarCount - 1);
                    int sourceIndex = Enumerable.Range(0, expressionPlan.Voices.Count).Where(j => expressionPlan.Voices[j].Start / r.SamplesPerBar == barIndex).OrderBy(j => Math.Abs(expressionPlan.Voices[j].Pitch % 12 - midi % 12)).DefaultIfEmpty(-1).First();
                    SampleNoteExpression? expression = sourceIndex < 0 ? null : expressionPlan.Voices[sourceIndex];
                    (int Start, int End, int Pitch)[] path = sourceIndex < 0 ? [] : expressionPlan.Paths.GetValueOrDefault(sourceIndex)?.Select(p => (p.Start, p.End, p.Pitch + midi - expression!.Pitch)).ToArray() ?? [];
                    if (!notes.NeedsPerFrameModulation && (expression is null || expression.Pan == 0 && expression.Fine == 0 && expression.Rel < 0 && expression.Cut == 100 && path.Length == 0))
                        PlayNote(buf, notes, shifted, midi, velocity, robins.Next("notes"), start, length, amp, r.SampleRate);
                    else PlayExpressiveNote(buf, notes, midi, velocity, robins.Next("notes"), start, length, amp, r, expression ?? NeutralExpression(start, length, midi, velocity), path);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buf, r.SampleRate))]);
        }

        private static int[] Pitches(RenderRequest r, int bar)
        {
            if (!r.Inputs.TryGetValue("notes", out byte[]? data))
            {
                return [];
            }

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(data);
            return [.. doc.RootElement.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("bar").GetInt32() == bar && !(n.TryGetProperty("slide", out JsonElement slide) && slide.ValueKind == JsonValueKind.True)).Select(n => n.GetProperty("pitch").GetInt32())];
        }

        /// <summary>Builds the strings of a chord from its notes (root first): open (five strings from the low root), triad (root, third, fifth around middle C) or power (root, fifth, octave).</summary>
        internal static int[] Voice(int[] notes, string voicing)
        {
            if (notes.Length == 0)
            {
                return [];
            }

            int root = notes[0] % 12;
            int third = notes.Length > 1 ? ((notes[1] - notes[0]) % 12 + 12) % 12 : 4;
            int low = 40 + (((root - 40) % 12) + 12) % 12;                    // the root between E2 and D#3
            return voicing switch
            {
                "power" => [low, low + 7, low + 12],
                "triad" => [48 + (((root - 48) % 12) + 12) % 12, 48 + (((root - 48) % 12) + 12) % 12 + third, 48 + (((root - 48) % 12) + 12) % 12 + 7],
                _ => [low, low + 7, low + 12, low + 12 + third, low + 19],
            };
        }
    }
}
