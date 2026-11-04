using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Midi;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>What a renderer is asked to make.</summary>
/// <param name="Block">The block.</param>
/// <param name="Template">Its template.</param>
/// <param name="SampleRate">Sample rate.</param>
/// <param name="SamplesPerBar">Samples in one bar.</param>
/// <param name="FirstBar">First bar rendered (1-based).</param>
/// <param name="BarCount">Number of bars rendered.</param>
/// <param name="ActiveBars">The absolute bars in which the block sounds.</param>
/// <param name="Inputs">Content of each connected input by input name.</param>
/// <param name="Seed">The seed of the block.</param>
/// <param name="ProjectRoot">The project folder (where sample folders are).</param>
/// <param name="Report">Called by a renderer that wants to show how far it is (null when nobody listens).</param>
/// <param name="Settings">The settings of this song for the renderer's plugin (name to text as written in the song), or null.</param>
public sealed record RenderRequest(BlockNode Block, TemplateSpec Template, int SampleRate, int SamplesPerBar, int FirstBar, int BarCount, IReadOnlySet<int> ActiveBars, IReadOnlyDictionary<string, byte[]> Inputs, ulong Seed, string ProjectRoot, Action<ProgressReport>? Report = null, IReadOnlyDictionary<string, string>? Settings = null)
{
    /// <summary>Gets the length of a bar in quarter notes: 4 in 4/4 (the default), 3 in 3/4 or 6/8, 5 in 5/4. The beat positions of notes are in quarter notes.</summary>
    public double BeatsPerBar { get; init; } = 4.0;
    /// <summary>Gets the length of a bar in quarter notes as a decimal, for the renderers that count in decimals.</summary>
    public decimal BeatsPerBarDecimal => (decimal)BeatsPerBar;
    /// <summary>Gets the AMPLE root passed to the server, when the request came through a server.</summary>
    public string? AmpleRoot { get; init; }
    /// <summary>Bypasses remote renderer caches for this block and refreshes them after a successful render.</summary>
    public bool ForceRender { get; init; }
    /// <summary>Gets event automation attached by the MIDI block or generated MIDI module, when present.</summary>
    public IReadOnlyList<MidiPerformanceEvent>? PerformanceEvents { get; init; }
    /// <summary>Gets the module source PPQ for exact-tick notes, or zero when notes already use the song timeline.</summary>
    public int MidiPpq { get; init; }
    /// <summary>Gets the original module tempo map for mapping exact source ticks onto output samples.</summary>
    public IReadOnlyList<SmfTempo>? MidiTempoMap { get; init; }
    /// <summary>Gets which channel of stereo sample files a sample renderer plays: 0 the mono mix (default), 1 the left channel, 2 the right channel (A02: a stereo set is rendered once per channel).</summary>
    public int SampleChannel { get; init; }
    /// <summary>Gets an object that learns whether a sample set with stereo files was loaded.</summary>
    public StereoProbe? Probe { get; init; }
}

/// <summary>Set by <see cref="SampleSet.Load(string, string?, string, int, int, bool)"/> users when a stereo sample file was loaded during a render.</summary>
public sealed class StereoProbe
{
    /// <summary>Gets or sets a value indicating whether a stereo sample file was loaded.</summary>
    public bool Seen { get; set; }
}

/// <summary>A bounded MIDI channel event mapped onto the output sample timeline.</summary>
public sealed record MidiPerformanceEvent(int Sample, SmfEventKind Kind, int Channel, int Data1, int Data2);


/// <summary>A renderer that reads settings from the song (the settings line, keys written as <c>key.name</c>). Its settings are part of the plan, so changing one makes a new take.</summary>
public interface IConfigurableRenderer
{
    /// <summary>Gets the key before the dot in the settings line (the plugin id).</summary>
    string SettingsKey { get; }
}

/// <summary>The renderer that makes one block and the song settings it overrides (names without the plugin prefix).</summary>
/// <param name="Renderer">The renderer for this block.</param>
/// <param name="Overrides">Settings that win over the song's own for this block (for example the model a block asked for).</param>
public sealed record RoutedRenderer(IBlockRenderer Renderer, IReadOnlyDictionary<string, string> Overrides);

/// <summary>A renderer that chooses, per block, which renderer makes it (the <c>engine</c> parameter of the ACE blocks). The build asks before it makes the plan, so the choice is part of the plan identity.</summary>
public interface IBlockRouter
{
    /// <summary>Gets the renderer for a block.</summary>
    /// <param name="block">The block.</param>
    /// <param name="songSettings">The song's settings for this renderer's plugin (names without the prefix).</param>
    /// <exception cref="Ample.Core.Abstractions.AmpleException">AMP5383 when the engine of the block is not valid, AMP5380 when it asks for Faer without a connection.</exception>
    RoutedRenderer RendererFor(BlockNode block, IReadOnlyDictionary<string, string> songSettings);
}

/// <summary>How far a block is: the whole (0 to 1) and optionally one named sub-step with its own 0 to 1 (null means busy, but the renderer cannot say how far).</summary>
/// <param name="Fraction">The progress of the whole block from 0 to 1, or null to leave it as it was.</param>
/// <param name="Step">The name of a sub-step, or null.</param>
/// <param name="StepFraction">The progress of that sub-step from 0 to 1, or null when it is busy without a known end.</param>
public sealed record ProgressReport(double? Fraction, string? Step = null, double? StepFraction = null);

/// <summary>A rendered output.</summary>
/// <param name="Role">The output port name (for example <c>audio</c> or <c>notes</c>).</param>
/// <param name="Data">The content.</param>
public sealed record RenderOutput(string Role, byte[] Data)
{
    /// <summary>Gets the renderer's resolved recipe and provenance, retained with the take.</summary>
    public string? MetadataJson { get; init; }
}

/// <summary>Makes the outputs of one block type. Built-in renderers are pure and bit-reproducible; plugin renderers arrive in chunk 60.</summary>
public interface IBlockRenderer
{
    /// <summary>Gets the block type this renderer handles.</summary>
    string BlockType { get; }

    /// <summary>Gets the renderer id stored in plans.</summary>
    string Id { get; }

    /// <summary>Gets the renderer version stored in plans.</summary>
    string Version { get; }

    /// <summary>Gets the determinism claim: <c>exact</c> (same input, same bytes everywhere), <c>machine-exact</c> (same bytes on the same machine and toolprint) or <c>unverified</c>.</summary>
    string Determinism => "exact";

    /// <summary>Gets the toolprint (what produced the result: engine, plugin, model hashes, coarse hardware class) as JSON text; it becomes part of the plan.</summary>
    string ToolprintJson => JsonSerializer.Serialize(new { engine = "ample-builtin", dsp = "integer" });

    /// <summary>Renders the block.</summary>
    ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
}

/// <summary>The built-in deterministic renderers.</summary>
public static partial class BuiltInRenderers
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    internal static IReadOnlyList<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> MapMidiTempoNotes(RenderRequest request, SmfFile file, IReadOnlyList<(long StartTick, long EndTick, int Pitch, int Velocity, int Channel, int Bank, int Program)> notes, int ppq, List<string> warnings, HashSet<(int Bar, int Pitch, int Channel)>? ties = null) => Midi.MapTempoNotes(request, file, notes, ppq, warnings, ties);
    internal static void ApplyMidiControllerGain(short[] samples, IReadOnlyList<(int Sample, int Controller, int Value)> events) => Midi.ApplyControllerGain(samples, events);

    /// <summary>Gets all built-in renderers, including SF2 General MIDI instruments.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = [new MidiClip(), new Midi(), new MidiRender(), new Drums(), new Bass(), new Piano(), new SampledInstruments.Guitar(), new SampledInstruments.Gm(), new MachineExactRenderer(new SampledInstruments.Gm()), new MachineExactRenderer(new MidiRender()), new Mixer("mixer"), new Mixer("bus"), new Master(), new Group(), new Lyrics(), new MoodScene(), new SplitterRenderer(), new SynthRenderer(), new DelayLlamaRenderer(), new ImportAudioRenderer(), new RegionMergeRenderer(), new OneShotSamplerRenderer(), new SendRenderer(), new FadeRenderer(), new CrossfadeRenderer(), new SequencerRenderer(), .. MidiEffects.All, .. AudioEffects.All, .. ExtraEffects.All, .. ControlBlocks.All, .. TrimBlock.All, .. SilenceBlock.All, .. ClickBlock.All];

    /// <summary>Parses a chord name (<c>Am</c>, <c>F</c>, <c>C#m</c>, <c>Bb</c>) into a triad of MIDI notes around C3.</summary>
    public static int[] Triad(string chord)
    {
        string root = chord.Length > 1 && chord[1] is '#' or 'b' ? chord[..2] : chord[..1];
        bool minor = chord[root.Length..].StartsWith('m');
        int index = root.EndsWith('b') ? (Array.IndexOf(Names, root[..1]) + 11) % 12 : Array.IndexOf(Names, root);
        if (index < 0)
        {
            index = 0;
        }

        int baseNote = 48 + index;
        return [baseNote, baseNote + (minor ? 3 : 4), baseNote + 7];
    }

    private static List<string> Chords(RenderRequest r) => [.. (r.Block.Parameters.FirstOrDefault(p => p.Name == "chords")?.Value is { Length: > 0 } own ? own : r.Template.Parameters.GetValueOrDefault("chords") is { Length: > 0 } fallback ? fallback : "C").Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// The tones of one chord symbol: the root and quality of <see cref="NoteTheory.TryChord"/> (Am, F, G7, Cmaj7, Dsus4 ...), and a slash chord <c>C/E</c> puts the named bass note lowest
    /// (an inversion when it is a tone of the chord, otherwise an extra bass note below the root). A symbol that is not known falls back to <see cref="Triad"/>, so older songs sound the same.
    /// </summary>
    public static int[] ChordTones(string symbol)
    {
        string main = symbol, bass = string.Empty;
        int slash = symbol.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0) { main = symbol[..slash]; bass = symbol[(slash + 1)..]; }
        if (!NoteTheory.TryChord(main, out int pc, out int[] intervals)) return Triad(main);
        int root = 48 + pc;
        int[] tones = [.. intervals.Select(i => root + i)];
        if (bass.Length == 0) return tones;
        if (!NoteTheory.TryRoot(bass, out int bassClass)) return tones;
        int at = Array.FindIndex(tones, x => ((x % 12) + 12) % 12 == bassClass);
        if (at >= 0)
        {
            // inversion: the tones below the bass note move up an octave
            return [.. tones.Select((x, i) => i < at ? x + 12 : x).Order()];
        }

        int low = 48 + bassClass;
        while (low >= tones.Min()) low -= 12;
        return [low, .. tones];
    }

    /// <summary>The chords of a bar: one symbol, or several joined by <c>.</c> (<c>Am.F</c> is two chords of two beats) that share the bar evenly.</summary>
    private static IEnumerable<(int Bar, int[] Notes, double Beat, double Beats)> ChordBars(RenderRequest r, List<string> chords) =>
        r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order().SelectMany(b =>
        {
            string[] parts = chords[(b - 1) % chords.Count].Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) parts = ["C"];
            double each = r.BeatsPerBar / parts.Length;
            return parts.Select((p, i) => (b, ChordTones(p), i * each, each));
        });

    private static int[] Notes(RenderRequest r, int bar)
    {
        if (!r.Inputs.TryGetValue("notes", out byte[]? data))
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(data);
        return [.. doc.RootElement.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("bar").GetInt32() == bar).Select(n => n.GetProperty("pitch").GetInt32())];
    }

    /// <summary>Parses a melody such as <c>c4:1 d4:1 e4:2 r:1</c> (note name, optional # or b, octave, then beats; r is a rest, 1 beat when no length is given) into notes per bar. A note stops at the end of its bar.</summary>
    public static IEnumerable<(int Bar, int Pitch, double Beat, double Beats)> Melody(string text, int firstBar, int barCount, double beatsPerBar = 4.0) =>
        MelodyWithVelocity(text, firstBar, barCount, beatsPerBar).Select(n => (n.Bar, n.Pitch, n.Beat, n.Beats));

    /// <summary>The velocity of a dynamic marker word in a melody: <c>pp p mp mf f ff</c> (32 to 112 in steps of 16; <c>f</c> is the default of 96).</summary>
    public static readonly IReadOnlyDictionary<string, int> DynamicMarkers = new Dictionary<string, int>(StringComparer.Ordinal) { ["pp"] = 32, ["p"] = 48, ["mp"] = 64, ["mf"] = 80, ["f"] = 96, ["ff"] = 112 };

    /// <summary>Like <see cref="Melody"/>, with a MIDI velocity (1 to 127, default 96). A note can carry its own as a third field (<c>c4:1:110</c>); a marker word (<c>pp p mp mf f ff</c>, see <see cref="DynamicMarkers"/>) sets the velocity of the notes after it; <c>&lt;</c> (crescendo) or <c>&gt;</c> (decrescendo) ramps the notes after it, by their place in time, to the next marker word (to <c>ff</c> or <c>pp</c> at the end of the melody when none follows). A note with its own velocity is never changed by a marker. Markers take no time.</summary>
    public static IEnumerable<(int Bar, int Pitch, double Beat, double Beats, int Velocity)> MelodyWithVelocity(string text, int firstBar, int barCount, double beatsPerBar = 4.0)
    {
        List<(int Bar, int Pitch, double Beat, double Beats, int Velocity)> result = [];
        List<double> starts = [];
        double at = 0;
        int current = 96;
        double hairAt = 0;
        int hairFrom = 0, hairDir = 0;
        List<int> hairNotes = [];

        void Resolve(int target, double endAt)
        {
            double span = endAt - hairAt;
            foreach (int index in hairNotes)
            {
                double t = span > 0 ? Math.Clamp((starts[index] - hairAt) / span, 0, 1) : 0;
                (int bar, int pitch, double beat, double beats, _) = result[index];
                result[index] = (bar, pitch, beat, beats, Math.Clamp((int)Math.Round(hairFrom + ((target - hairFrom) * t), MidpointRounding.AwayFromZero), 1, 127));
            }

            hairNotes = [];
            hairDir = 0;
        }

        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            string name = parts[0].ToLowerInvariant();
            if (parts.Length == 1 && DynamicMarkers.TryGetValue(name, out int marker))
            {
                if (hairDir != 0) Resolve(marker, at);
                current = marker;
                continue;
            }

            if (parts.Length == 1 && name is "<" or ">")
            {
                if (hairDir != 0) Resolve(hairDir > 0 ? 112 : 32, at);
                hairDir = name == "<" ? 1 : -1;
                hairAt = at;
                hairFrom = current;
                continue;
            }

            double beats = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0 ? b : 1;
            int barIndex = (int)(at / beatsPerBar);
            if (barIndex >= barCount)
            {
                break;
            }

            if (name != "r" && name.Length >= 2 && "cdefgab".Contains(name[0]))
            {
                int semitone = name[0] switch { 'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, _ => 11 };
                int i = 1;
                if (name[i] == '#') { semitone++; i++; }
                else if (name[i] == 'b' && name.Length > i + 1) { semitone--; i++; }
                if (int.TryParse(name[i..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
                {
                    double beat = at - (barIndex * beatsPerBar);
                    bool own = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
                    int velocity = own ? Math.Clamp(int.Parse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture), 1, 127) : current;
                    if (hairDir != 0 && !own) hairNotes.Add(result.Count);
                    starts.Add(at);
                    result.Add((firstBar + barIndex, Math.Clamp(12 * (octave + 1) + semitone, 0, 127), beat, Math.Min(beats, beatsPerBar - beat), velocity));
                }
            }

            at += beats;
        }

        if (hairDir != 0) Resolve(hairDir > 0 ? 112 : 32, at);
        return result;
    }

    internal static IEnumerable<(int Pitch, double Beat, double Beats, int Velocity)> NoteEventsOf(RenderRequest r, int bar) => NoteEvents(r, bar);

    private static IEnumerable<(int Pitch, double Beat, double Beats, int Velocity)> NoteEvents(RenderRequest r, int bar)
    {
        if (!r.Inputs.TryGetValue("notes", out byte[]? data))
        {
            yield break;
        }

        using JsonDocument doc = JsonDocument.Parse(data);
        foreach (JsonElement n in doc.RootElement.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("bar").GetInt32() == bar))
        {
            double beat = n.TryGetProperty("beat", out JsonElement be) ? be.GetDouble() : 0;
            double beats = n.TryGetProperty("beats", out JsonElement bs) ? bs.GetDouble() : 4;
            int velocity = n.TryGetProperty("velocity", out JsonElement ve) ? Math.Clamp(ve.GetInt32(), 1, 127) : 96;
            yield return (n.GetProperty("pitch").GetInt32(), beat, Math.Min(beats, 4 - beat), velocity);
        }
    }

    private static short[] Buffer(RenderRequest r) => new short[r.BarCount * r.SamplesPerBar];

    /// <summary>The <c>humanize</c> parameter (0 to 100) of a built-in instrument that has no sample set: the same deterministic feel as the sampled instruments (up to 6 ms early or late, up to 15 % louder or softer), drawn from the seed.</summary>
    private static SampledInstruments.Humanizer HumanizerOf(RenderRequest r)
    {
        string text = r.Block.Parameters.FirstOrDefault(p => p.Name == "humanize")?.Value.Trim().Trim('"') ?? string.Empty;
        return new SampledInstruments.Humanizer(r.Seed, Math.Clamp(AudioEffects.Read(text, 0), 0, 100), r.SampleRate);
    }

    /// <summary>Applies the <c>gain</c> parameter (dB) to the sound of a built-in instrument that has no sample set; 0 dB (or no parameter) leaves the samples as they are.</summary>
    private static short[] WithGain(RenderRequest r, short[] buf)
    {
        string text = r.Block.Parameters.FirstOrDefault(p => p.Name == "gain")?.Value.Trim().Trim('"') ?? string.Empty;
        int db = Math.Clamp(AudioEffects.Read(text, 0), -24, FixedAudio.WideMaxDb);
        if (db == 0) return buf;
        int gain = FixedAudio.GainFromDbWide(db);
        for (int i = 0; i < buf.Length; i++) buf[i] = FixedAudio.Clip((long)buf[i] * gain / 32768);
        return buf;
    }

    /// <summary>Resolves the project-owned MIDI file named by a midi block, or null when no file is selected.</summary>
    public static string? MidiFile(string projectRoot, BlockNode block)
    {
        if (block.Type != "midi" || block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') is not { Length: > 0 } value) return null;
        string path = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, value);
        return File.Exists(path) && new FileInfo(path).Length <= SmfLimits.Standard.MaxFileBytes ? path : null;
    }

    /// <summary>Reads one bounded MIDI asset without trusting its observed file size to remain fixed.</summary>
    public static byte[] ReadMidiFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        if (input.Length > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5501", "The MIDI file exceeds the supported size limit.");
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5501", "The MIDI file exceeds the supported size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static ValueTask<IReadOnlyList<RenderOutput>> Audio(RenderRequest r, string role, short[] samples) => ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput(role, Wav.Encode(samples, r.SampleRate))]);

    private sealed class MidiClip : IBlockRenderer
    {
        public string BlockType => "midi-clip";

        public string Id => "builtin.midi-clip";

        public string Version => "4";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            Parameter? ppqParameter = r.Block.Parameters.FirstOrDefault(p => p.Name == "ppq");
            int ppq = ppqParameter is not null && int.TryParse(ppqParameter.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPpq) ? parsedPpq : StructuredMidiNotes.MicrobeatsPerBeat;
            using MemoryStream stream = new();
            using Utf8JsonWriter writer = new(stream);
            writer.WriteStartObject();
            writer.WriteNumber("ppq", ppq);
            writer.WriteStartArray("notes");
            Parameter? structuredParameter = r.Block.Parameters.FirstOrDefault(p => p.Name == "notes");
            string structured = structuredParameter?.Value.Trim().Trim('"') ?? string.Empty;
            bool hasStructuredNotes = structuredParameter is not null;
            string melody = r.Block.Parameters.FirstOrDefault(p => p.Name == "melody")?.Value.Trim().Trim('"') ?? string.Empty;
            if (hasStructuredNotes)
            {
                if (!StructuredMidiNotes.TryParse(structured, ppq, out IReadOnlyList<StructuredMidiNote> notes, r.BeatsPerBar)) throw new AmpleException("AMP2012", "Structured MIDI notes are invalid.");
                HashSet<int> seenTies = [];   // a note cut at a bar line has the same tie id in each piece; every piece after the first continues it (it is not struck again)
                foreach (StructuredMidiNote note in notes.Where(n => n.Bar >= r.FirstBar && n.Bar < r.FirstBar + r.BarCount))
                {
                    bool continuation = note.TieId >= 0 && !seenTies.Add(note.TieId);
                    writer.WriteStartObject();
                    writer.WriteNumber("bar", note.Bar);
                    writer.WriteNumber("pitch", note.Pitch);
                    WriteMicrobeats(writer, "beat", note.BeatMicrobeats);
                    WriteMicrobeats(writer, "beats", note.LengthMicrobeats);
                    writer.WriteNumber("velocity", note.Velocity);
                    writer.WriteNumber("tick", note.BeatTicks);
                    writer.WriteNumber("ticks", note.LengthTicks);
                    writer.WriteNumber("ppq", note.TicksPerQuarter);
                    if (note.Channel >= 0) { writer.WriteNumber("channel", note.Channel); writer.WriteNumber("bank", note.Bank); writer.WriteNumber("program", note.Program); }
                    if (continuation) writer.WriteBoolean("tie", true);
                    // Per-note extension values pass through only when set; the sampler renderers consume pan, fine, release, cut and slide.
                    if (note.Pan != 0) writer.WriteNumber("pan", note.Pan);
                    if (note.Fine != 0) writer.WriteNumber("fine", note.Fine);
                    if (note.Rel >= 0) writer.WriteNumber("rel", note.Rel);
                    if (note.Cut != 100) writer.WriteNumber("cut", note.Cut);
                    if (note.Slide != 0) writer.WriteBoolean("slide", true);
                    if (note.Port != 0) writer.WriteNumber("port", note.Port);
                    if (note.Bend != 0) writer.WriteNumber("bend", note.Bend);
                    writer.WriteEndObject();
                }
            }
            else if (melody.Length > 0)
            {
                foreach ((int bar, int pitch, double beat, double beats, int velocity) in MelodyWithVelocity(melody, r.FirstBar, r.BarCount, r.BeatsPerBar))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("bar", bar);
                    writer.WriteNumber("pitch", pitch);
                    writer.WriteNumber("beat", decimal.Parse(beat.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    writer.WriteNumber("beats", decimal.Parse(beats.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    writer.WriteNumber("velocity", velocity);
                    writer.WriteEndObject();
                }
            }

            foreach ((int bar, int[] notes, double chordBeat, double chordBeats) in hasStructuredNotes || melody.Length > 0 ? [] : ChordBars(r, Chords(r)))
            {
                foreach (int pitch in notes)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("bar", bar);
                    writer.WriteNumber("pitch", pitch);
                    if (chordBeat > 0) writer.WriteNumber("beat", decimal.Parse(chordBeat.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    writer.WriteNumber("beats", decimal.Parse(chordBeats.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    writer.WriteNumber("velocity", 96);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            // the pitch bend curve of the clip (percent of bend_range semitones) goes along with the notes; the block that plays them bends its sound (PitchBend)
            string bend = r.Block.Parameters.FirstOrDefault(p => p.Name == "bend")?.Value.Trim().Trim('"') ?? string.Empty;
            if (bend.Length > 0)
            {
                writer.WriteString("bend", bend);
                string range = r.Block.Parameters.FirstOrDefault(p => p.Name == "bend_range")?.Value.Trim().Trim('"') ?? string.Empty;
                writer.WriteNumber("bend_range", int.TryParse(range.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int semitones) ? Math.Clamp(semitones, 1, 48) : 2);
            }

            // the controllers of the clip (modwheel, volume, ..., cc) go along with the notes; the gm block plays them as control changes (ClipControls)
            List<(int Number, string Value, string Unit)> controls = [];
            foreach ((string name, int number, string unit) in ClipControls.Named)
            {
                string value = r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;
                if (value.Length == 0) continue;
                if (!ClipControls.Valid(value, unit)) throw new AmpleException("AMP5506", $"The {name} of the clip is not a value it can play: '{value}'. {name} takes {ClipControlSyntax.UnitText(unit)}, or a curve such as auto(1:0, 2:100).");
                controls.Add((number, value, unit));
            }

            string ccText = r.Block.Parameters.FirstOrDefault(p => p.Name == "cc")?.Value.Trim().Trim('"') ?? string.Empty;
            if (ccText.Length > 0)
            {
                IReadOnlyList<(int Number, string Value)> entries = ClipControls.ParseCc(ccText) ?? throw new AmpleException("AMP5506", $"The cc of the clip is not a list of controller=value entries: '{ccText}'. Write it like \"74=auto(1:20, 4:100) 67=127\" (controllers 0 to 127, values 0 to 127 or a curve).");
                foreach ((int number, string value) in entries) controls.Add((number, value, "raw"));
            }

            if (controls.Count > 0)
            {
                writer.WriteStartArray("controls");
                foreach ((int number, string value, string unit) in controls)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("cc", number);
                    writer.WriteString("value", value);
                    writer.WriteString("unit", unit);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.Flush();
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("notes", stream.ToArray())]);
        }

        internal static void WriteMicrobeats(Utf8JsonWriter writer, string property, int value) => writer.WriteNumber(property, value / (decimal)StructuredMidiNotes.MicrobeatsPerBeat);
    }

    private sealed class Midi : IBlockRenderer
    {
        public string BlockType => "midi";
        public string Id => "builtin.midi";
        public string Version => "8";

        public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string value = r.Block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') ?? string.Empty;
            if (value.Length == 0) throw new AmpleException("AMP5501", "The MIDI block needs a project MIDI file.");
            string path = Ample.Audio.IO.ProjectPath.Resolve(r.ProjectRoot, value);
            FileInfo info = new(path);
            if (!info.Exists || info.Length > SmfLimits.Standard.MaxFileBytes) throw new AmpleException("AMP5501", "The MIDI file is missing or exceeds the supported size limit.");
            byte[] sourceBytes = ReadMidiFile(path);
            SmfFile file;
            try { file = SmfReader.Parse(sourceBytes, SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string selected = r.Block.Parameters.FirstOrDefault(p => p.Name == "track")?.Value.Trim().Trim('"') ?? "all";
            int[] tracks = selected == "all" ? [.. Enumerable.Range(0, file.Tracks.Count)] : int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out int track) && track >= 0 && track < file.Tracks.Count ? [track] : throw new AmpleException("AMP5503", "The MIDI track selector must be 'all' or a valid zero-based track number.");
            List<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> notes = [];
            List<(long StartTick, long EndTick, int Pitch, int Velocity, int Channel, int Bank, int Program)> sourceNotes = [];
            List<string> warnings = [];
            HashSet<(int Bar, int Pitch, int Channel)> sourceTies = [];
            if (file.TempoMap.Count > 1 || file.TempoMap[0].MicrosecondsPerQuarter != (long)(r.SamplesPerBar * 1_000_000.0 / (r.SampleRate * r.BeatsPerBar))) warnings.Add("the note output follows the song timeline; MIDI audio follows its source tempo map");
            if (file.TimeSignatures.Any(t => Math.Abs(t.Numerator * 4.0 / (1 << t.DenominatorPower) - r.BeatsPerBar) > 1e-9)) warnings.Add("the meter of the MIDI file differs from the meter of the song: the notes are placed by their time, so they do not fall on the bar lines of the song (write meter on the composition line)");
            int ppq = file.TicksPerQuarter;
            if (ppq <= 0) throw new AmpleException("AMP5501", "The MIDI division is invalid.");
            foreach (int index in tracks)
            {
                MidiNotePairing pairing = MidiNotePairer.Pair(file.Tracks[index], index);
                warnings.AddRange(pairing.Warnings);
                sourceNotes.AddRange(pairing.Notes.Select(n => (n.StartTick, n.EndTick, n.Pitch, n.Velocity, n.Channel, n.Bank, n.Program)));
                if (file.Tracks[index].Events.Any(e => e.Kind == SmfEventKind.ControlChange)) warnings.Add("controllers are not represented in note output");
                if (file.Tracks[index].Events.Any(e => e.Kind is SmfEventKind.PitchBend or SmfEventKind.PolyphonicPressure or SmfEventKind.ChannelPressure)) warnings.Add("bends and pressure events are not represented in note output");
                if (pairing.Notes.Any(n => n.StartTick / (4L * ppq) != (n.EndTick - 1) / (4L * ppq))) warnings.Add("notes crossing bars are split into adjacent note events");
                foreach (MidiNoteSpan note in pairing.Notes)
                {
                    long first = note.StartTick / (4L * ppq), last = (note.EndTick - 1) / (4L * ppq);
                    for (long bar = Math.Max(first, r.FirstBar - 1L); bar <= Math.Min(last, r.FirstBar + (long)r.BarCount - 2); bar++)
                    {
                        long barStart = bar * 4L * ppq, end = Math.Min(note.EndTick, barStart + 4L * ppq);
                        if (r.ActiveBars.Contains(checked((int)bar + 1)))
                        {
                            if (notes.Count >= StructuredMidiNotes.MaximumNotes) throw new AmpleException("AMP5504", "The MIDI note output exceeds the structured note limit.");
                            if (bar > first) sourceTies.Add((checked((int)bar + 1), note.Pitch, note.Channel));
                            notes.Add((checked((int)bar + 1), note.Pitch, Math.Max(0, note.StartTick - barStart), end - Math.Max(note.StartTick, barStart), note.Velocity, note.Channel, note.Bank, note.Program));
                        }
                    }
                }
            }
            byte[] noteOutput = SerializeNotes(notes.OrderBy(n => n.Bar).ThenBy(n => n.Beat).ThenBy(n => n.Pitch), ppq, sourceTies);
            HashSet<(int Bar, int Pitch, int Channel)> ties = [];
            // the project tempo and the tempo of the file should agree: if not, the notes slide against the bars (and notes over a bar line are tied)
            if (file.TempoMap.Count > 0)
            {
                double projectBpm = 4.0 * 60 * r.SampleRate / r.SamplesPerBar, fileBpm = 60_000_000.0 / file.TempoMap[0].MicrosecondsPerQuarter;
                if (Math.Abs(projectBpm - fileBpm) > 0.2) warnings.Add($"AMP5289: the project tempo is {projectBpm:0.##} BPM but the MIDI file is at {fileBpm:0.##} BPM; the notes do not line up with the bars. Set the project tempo to {fileBpm:0.##}.");
            }
            IReadOnlyList<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> audioNotes = MapTempoNotes(r, file, sourceNotes, ppq, warnings, ties);
            IReadOnlyList<RenderOutput> audio;
            try { audio = await RenderMidiAudio(r, audioNotes, ppq, "pack:musescore-general", 0, file, tracks, cancellationToken, ties).ConfigureAwait(false); }
            catch (AmpleException ex) when (ex.DiagnosticCode == "AMP5311" && ex.Message.Contains("pack is not installed", StringComparison.Ordinal))
            {
                audio = [new RenderOutput("audio", Wav.Encode(new short[checked(r.BarCount * r.SamplesPerBar)], r.SampleRate))];
                warnings.Add("MIDI audio is silent because the GM SoundFont pack is not installed; run tools/fetch-gm-soundfont.mjs --download.");
            }
            warnings.AddRange(audio.Where(o => o.Role == "warnings").SelectMany(o => Encoding.UTF8.GetString(o.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            string warningText = string.Join('\n', warnings.Distinct(StringComparer.Ordinal));
            return [new RenderOutput("notes", noteOutput), new RenderOutput("sequence", sourceBytes), .. audio.Where(o => o.Role == "audio"), new RenderOutput("warnings", Encoding.UTF8.GetBytes(warningText))];
        }

        private static byte[] SerializeNotes(IEnumerable<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> notes, int ppq, HashSet<(int Bar, int Pitch, int Channel)>? ties = null)
        {
            using MemoryStream stream = new();
            using Utf8JsonWriter writer = new(stream);
            writer.WriteStartObject();
            writer.WriteNumber("ppq", ppq);
            writer.WriteStartArray("notes");
            foreach (var n in notes)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bar", n.Bar);
                writer.WriteNumber("pitch", n.Pitch);
                MidiClip.WriteMicrobeats(writer, "beat", checked((int)(n.Beat * StructuredMidiNotes.MicrobeatsPerBeat / ppq)));
                MidiClip.WriteMicrobeats(writer, "beats", checked((int)(n.Length * StructuredMidiNotes.MicrobeatsPerBeat / ppq)));
                writer.WriteNumber("velocity", n.Velocity);
                writer.WriteNumber("tick", n.Beat);
                writer.WriteNumber("ticks", n.Length);
                writer.WriteNumber("ppq", ppq);
                writer.WriteNumber("channel", n.Channel);
                writer.WriteNumber("bank", n.Bank);
                writer.WriteNumber("program", n.Program);
                if (n.Beat == 0 && ties is not null && ties.Contains((n.Bar, n.Pitch, n.Channel))) writer.WriteBoolean("tie", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return stream.ToArray();
        }

        public static List<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> MapTempoNotes(RenderRequest request, SmfFile file, IReadOnlyList<(long StartTick, long EndTick, int Pitch, int Velocity, int Channel, int Bank, int Program)> sourceNotes, int ppq, List<string> warnings, HashSet<(int Bar, int Pitch, int Channel)>? ties = null)
        {
            SmfTempo[] tempos = [.. file.TempoMap];
            BigInteger[] elapsedNumerators = new BigInteger[tempos.Length];
            for (int i = 1; i < tempos.Length; i++) elapsedNumerators[i] = elapsedNumerators[i - 1] + (BigInteger)(tempos[i].Tick - tempos[i - 1].Tick) * tempos[i - 1].MicrosecondsPerQuarter;
            BigInteger AtTick(long tick)
            {
                int low = 0, high = tempos.Length - 1;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    if (tempos[mid].Tick <= tick) low = mid; else high = mid - 1;
                }
                BigInteger timeNumerator = elapsedNumerators[low] + (BigInteger)(tick - tempos[low].Tick) * tempos[low].MicrosecondsPerQuarter;
                return timeNumerator * request.SampleRate * 4 / (1_000_000L * request.SamplesPerBar);
            }

            List<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> mapped = [];
            long barTicks = checked(4L * ppq);
            long renderStart = checked((long)(request.FirstBar - 1) * barTicks);
            long renderEnd = checked(((long)request.FirstBar + request.BarCount - 1) * barTicks);
            bool clipped = false;
            foreach (var note in sourceNotes)
            {
                long start = checked((long)BigInteger.Min(AtTick(note.StartTick), long.MaxValue));
                long end = checked((long)BigInteger.Min(AtTick(note.EndTick), long.MaxValue));
                if (end > renderEnd) clipped = true;
                long visibleStart = Math.Max(start, renderStart), visibleEnd = Math.Min(end, renderEnd);
                if (visibleEnd <= visibleStart) continue;
                long firstBar = visibleStart / barTicks, lastBar = (visibleEnd - 1) / barTicks;
                for (long bar = firstBar; bar <= lastBar; bar++)
                {
                    int absoluteBar = checked((int)bar + 1);
                    if (!request.ActiveBars.Contains(absoluteBar)) continue;
                    long barStart = bar * barTicks, segmentEnd = Math.Min(visibleEnd, barStart + barTicks);
                    if (mapped.Count >= StructuredMidiNotes.MaximumNotes) throw new AmpleException("AMP5504", "The MIDI audio note list exceeds the structured note limit.");
                    if (bar > firstBar) ties?.Add((absoluteBar, note.Pitch, note.Channel));   // the part of a note that goes on over a bar line: a continuation, not a new strike
                    mapped.Add((absoluteBar, note.Pitch, Math.Max(0, visibleStart - barStart), segmentEnd - Math.Max(visibleStart, barStart), note.Velocity, note.Channel, note.Bank, note.Program));
                }
            }
            if (clipped) warnings.Add("MIDI audio extends beyond the block's configured bar count and is truncated");
            return mapped;
        }

        public static async ValueTask<IReadOnlyList<RenderOutput>> RenderMidiAudio(RenderRequest request, IReadOnlyList<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> notes, int ppq, string soundFont, int gain, SmfFile file, IReadOnlyList<int> tracks, CancellationToken cancellationToken, HashSet<(int Bar, int Pitch, int Channel)>? ties = null)
        {
            string limiter = request.Block.Parameters.FirstOrDefault(p => p.Name == "limiter")?.Value.Trim().Trim('"') ?? "soft";
            if (limiter is not ("soft" or "off")) throw new AmpleException("AMP5352", "The limiter must be 'soft' or 'off'.");
            long[] mixed = new long[checked(request.BarCount * request.SamplesPerBar)];
            SampledInstruments.Gm renderer = new();
            List<string> warnings = [];
            SmfTempo[] tempoMap = [.. file.TempoMap];
            BigInteger[] tempoElapsed = new BigInteger[tempoMap.Length];
            for (int i = 1; i < tempoMap.Length; i++) tempoElapsed[i] = tempoElapsed[i - 1] + (BigInteger)(tempoMap[i].Tick - tempoMap[i - 1].Tick) * tempoMap[i - 1].MicrosecondsPerQuarter;
            foreach (var group in notes.GroupBy(n => (n.Bank, n.Program, n.Channel)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BlockNode instrument = new() { Type = "gm", Name = "midi-playback" };
                instrument.Parameters.Add(new Parameter("file", soundFont, 0));
                instrument.Parameters.Add(new Parameter("bank", group.Key.Bank.ToString(CultureInfo.InvariantCulture), 0));
                instrument.Parameters.Add(new Parameter("program", group.Key.Program.ToString(CultureInfo.InvariantCulture), 0));
                instrument.Parameters.Add(new Parameter("channel", group.Key.Channel.ToString(CultureInfo.InvariantCulture), 0));
                instrument.Parameters.Add(new Parameter("gain", gain.ToString(CultureInfo.InvariantCulture), 0));
                instrument.Parameters.Add(new Parameter("limiter", limiter, 0));
                instrument.Parameters.Add(new Parameter("engine", request.Block.Parameters.FirstOrDefault(p => p.Name == "engine")?.Value ?? "integer", 0));
                byte[] groupNotes = SerializeNotes(group, ppq, ties);
                MidiPerformanceEvent[] controls = [.. tracks.SelectMany(track => file.Tracks[track].Events)
                    .Where(e => e.Channel == group.Key.Channel && e.Kind is SmfEventKind.ControlChange or SmfEventKind.PitchBend or SmfEventKind.ChannelPressure or SmfEventKind.PolyphonicPressure)
                    .Select(e => new MidiPerformanceEvent(TickToSample(request, file.TicksPerQuarter, tempoMap, tempoElapsed, e.Tick), e.Kind, e.Channel, e.Data1, e.Data2))
                    .OrderBy(e => e.Sample)];
                RenderRequest gmRequest = request with { Block = instrument, Inputs = new Dictionary<string, byte[]> { ["notes"] = groupNotes }, PerformanceEvents = controls };
                IReadOnlyList<RenderOutput> outputs = await renderer.RenderAsync(gmRequest, cancellationToken).ConfigureAwait(false);
                (short[] samples, _) = Wav.Decode(outputs.Single(o => o.Role == "audio").Data);
                warnings.AddRange(outputs.Where(o => o.Role == "warnings").SelectMany(o => Encoding.UTF8.GetString(o.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
                for (int i = 0; i < mixed.Length; i++) mixed[i] += samples[i];
            }
            short[] pcm = [.. mixed.Select(sample => FixedAudio.Limit(sample, limiter == "soft"))];
            return [new RenderOutput("audio", Wav.Encode(pcm, request.SampleRate)), new RenderOutput("warnings", Encoding.UTF8.GetBytes(string.Join('\n', warnings.Distinct(StringComparer.Ordinal))))];
        }

        private static int TickToSample(RenderRequest request, int ppq, SmfTempo[] tempos, BigInteger[] elapsedAtTempo, long tick)
        {
            int low = 0, high = tempos.Length - 1;
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                if (tempos[mid].Tick <= tick) low = mid; else high = mid - 1;
            }
            BigInteger elapsed = elapsedAtTempo[low] + (BigInteger)(tick - tempos[low].Tick) * tempos[low].MicrosecondsPerQuarter;
            BigInteger sample = elapsed * request.SampleRate / ((BigInteger)ppq * 1_000_000) - (BigInteger)(request.FirstBar - 1) * request.SamplesPerBar;
            return sample <= 0 ? 0 : sample >= request.BarCount * (long)request.SamplesPerBar ? checked(request.BarCount * request.SamplesPerBar) : (int)sample;
        }

        internal static void ApplyControllerGain(short[] samples, IReadOnlyList<(int Sample, int Controller, int Value)> events)
        {
            int volume = 127, expression = 127, eventIndex = 0;
            var ordered = events.OrderBy(e => e.Sample).ToArray();
            for (int i = 0; i < samples.Length; i++)
            {
                while (eventIndex < ordered.Length && ordered[eventIndex].Sample <= i)
                {
                    var ev = ordered[eventIndex++];
                    if (ev.Controller == 7) volume = Math.Clamp(ev.Value, 0, 127);
                    else if (ev.Controller == 11) expression = Math.Clamp(ev.Value, 0, 127);
                }
                int gainQ15 = volume * expression * 32768 / (127 * 127);
                samples[i] = FixedAudio.Clip((long)samples[i] * gainQ15 / 32768);
            }
        }

    }

    private sealed class MidiRender : IBlockRenderer, IBlockRouter
    {
        private static readonly MachineExactRenderer FloatEngine = new(new MidiRender());

        /// <summary>The renderer of a midi-render block: this one, or with <c>engine float</c> the machine-exact one (SF3 fonts).</summary>
        public RoutedRenderer RendererFor(BlockNode block, IReadOnlyDictionary<string, string> songSettings) => new(MachineExactRenderer.IsFloat(block) ? FloatEngine : this, new Dictionary<string, string>(StringComparer.Ordinal));

        public string BlockType => "midi-render";
        public string Id => "builtin.midi-render";
        public string Version => "5/gm" + new SampledInstruments.Gm().Version;   // notes are played with the GM renderer, so its version is part of this one
        public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            if (!r.Inputs.TryGetValue("sequence", out byte[]? sourceBytes))
            {
                if (!r.Inputs.ContainsKey("notes")) throw new AmpleException("AMP5505", "Connect a MIDI sequence or MIDI notes to the converter.");
                return [.. (await new SampledInstruments.Gm().RenderAsync(r, cancellationToken).ConfigureAwait(false)).Where(o => o.Role is not ("send" or "chorus"))];   // the converter has no send outputs
            }
            SmfFile file;
            try { file = SmfReader.Parse(sourceBytes, SmfLimits.Standard); }
            catch (SmfFormatException ex) { throw new AmpleException("AMP5501", ex.Message); }
            string selected = r.Block.Parameters.FirstOrDefault(p => p.Name == "track")?.Value.Trim().Trim('"') ?? "all";
            int[] tracks = selected == "all" ? [.. Enumerable.Range(0, file.Tracks.Count)] : int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out int track) && track >= 0 && track < file.Tracks.Count ? [track] : throw new AmpleException("AMP5503", "The MIDI track selector must be 'all' or a valid zero-based track number.");
            int ppq = file.TicksPerQuarter;
            List<(long StartTick, long EndTick, int Pitch, int Velocity, int Channel, int Bank, int Program)> sourceNotes = [];
            List<string> warnings = [];
            if (file.TimeSignatures.Any(t => Math.Abs(t.Numerator * 4.0 / (1 << t.DenominatorPower) - r.BeatsPerBar) > 1e-9)) warnings.Add("the meter of the MIDI file differs from the meter of the song: the audio follows the time of the file, so it does not fall on the bar lines of the song (write meter on the composition line)");
            if (file.Tracks.SelectMany(t => t.Events).Any(e => e.Kind == SmfEventKind.ControlChange && e.Data1 is not (0 or 7 or 11 or 32 or 64 or 120 or 121 or 123))) warnings.Add("Some MIDI controllers are preserved by the sequence but not applied to audio");
            if (file.Tracks.SelectMany(t => t.Events).Any(e => e.Kind is SmfEventKind.PolyphonicPressure or SmfEventKind.ChannelPressure)) warnings.Add("MIDI pressure events are preserved by the sequence but not applied to audio");
            foreach (int index in tracks)
            {
                MidiNotePairing pairing = MidiNotePairer.Pair(file.Tracks[index], index);
                warnings.AddRange(pairing.Warnings);
                sourceNotes.AddRange(pairing.Notes.Select(n => (n.StartTick, n.EndTick, n.Pitch, n.Velocity, n.Channel, n.Bank, n.Program)));
            }
            int gain = AudioEffects.ReadDb(r.Block.Parameters.FirstOrDefault(p => p.Name == "gain")?.Value, 0, -24, 12);
            string soundFont = r.Block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') ?? "pack:musescore-general";
            IReadOnlyList<(int Bar, int Pitch, long Beat, long Length, int Velocity, int Channel, int Bank, int Program)> notes = Midi.MapTempoNotes(r, file, sourceNotes, ppq, warnings);
            int? bankOverride = SourceOverride(r, "bank", 16_383), programOverride = SourceOverride(r, "program", 127);
            if (bankOverride is not null || programOverride is not null) notes = [.. notes.Select(n => (n.Bar, n.Pitch, n.Beat, n.Length, n.Velocity, n.Channel, bankOverride ?? n.Bank, programOverride ?? n.Program))];
            IReadOnlyList<RenderOutput> audio;
            try { audio = await Midi.RenderMidiAudio(r, notes, ppq, soundFont, gain, file, tracks, cancellationToken).ConfigureAwait(false); }
            catch (AmpleException ex) when (ex.DiagnosticCode == "AMP5311" && ex.Message.Contains("pack is not installed", StringComparison.Ordinal))
            {
                audio = [new RenderOutput("audio", Wav.Encode(new short[checked(r.BarCount * r.SamplesPerBar)], r.SampleRate))];
                warnings.Add("MIDI audio is silent because the GM SoundFont pack is not installed.");
            }
            warnings.AddRange(audio.Where(o => o.Role == "warnings").SelectMany(o => Encoding.UTF8.GetString(o.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            return [.. audio.Where(o => o.Role == "audio"), new RenderOutput("warnings", Encoding.UTF8.GetBytes(string.Join('\n', warnings.Distinct(StringComparer.Ordinal))))];
        }

        private static int? SourceOverride(RenderRequest request, string name, int maximum)
        {
            string value = request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? "source";
            if (value.Equals("source", StringComparison.OrdinalIgnoreCase)) return null;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed <= maximum) return parsed;
            throw new AmpleException("AMP5503", $"The MIDI {name} override must be 'source' or an integer from 0 to {maximum}.");
        }
    }

    private sealed class Drums : IBlockRenderer
    {
        public string BlockType => "drums";

        public string Id => "builtin.drums";

        public string Version => "8";

        /// <summary>The two named patterns as grids (the sound they have when a bar does not have the usual number of steps).</summary>
        private static readonly Dictionary<string, string> NamedGrids = new(StringComparer.Ordinal)
        {
            ["beat-A"] = "kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.",
            ["beat-B"] = "kick:x.....x.x.....x. snare:....x.......x... hat:x.x.x.x.x.x.x.x.",
        };

        /// <summary>One voice of the built-in sound: a pitched hit that falls (kick, toms) or a burst of noise, how many steps it lasts, and how loud it is.</summary>
        private readonly record struct DrumVoice(bool Tonal, int Steps, int Amp, long High, long Drop);

        /// <summary>One hit of a voice grid, collected first so that a choke rule can cut it short.</summary>
        private sealed record GridHit(int At, string Voice, DrumVoice Kind, int Length, int Amp);

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            if (SampleSet.FolderOf(r.ProjectRoot, r.AmpleRoot, r.Block) is { } folder)
            {
                return SampledInstruments.Stereo(r, pass => SampledInstruments.Drums(pass, folder));
            }

            string pattern = r.Block.Parameters.FirstOrDefault(p => p.Name == "pattern")?.Value ?? "beat-A";
            string ParamText(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty;
            int Percent(string name, int maximum) => double.TryParse(ParamText(name).Trim().Trim('"').TrimEnd('%', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Clamp((int)v, 0, maximum) : 0;
            DrumSong song = DrumSong.Parse(ParamText("patterns"), ParamText("song"), ["beat-A", "beat-B"]);
            IReadOnlyList<string> sequence = song.Expand();
            short[] buf = Buffer(r);
            Ample.Audio.Random.StableRng noise = new(r.Seed), jitter = new(r.Seed + 1);
            int swing = Percent("swing", 60), human = Percent("humanize", 100);
            int stepsInBar = SampledInstruments.DrumSteps(r);   // 16 in 4/4, 12 in 3/4, or the steps parameter
            int step = r.SamplesPerBar / stepsInBar;
            bool plainSteps = stepsInBar == Math.Max(1, (int)Math.Round(4 * r.BeatsPerBar));   // the steps of a bar are the usual ones: beat-A and beat-B keep their own sound
            Dictionary<string, int> barsPlayed = new(StringComparer.Ordinal);
            List<GridHit> hits = [];
            foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
            {
                int barStart = (bar - r.FirstBar) * r.SamplesPerBar;
                string barPattern = DrumSong.NameAtBar(sequence, bar - r.FirstBar);
                string chosen = barPattern == DrumSong.MainName ? pattern : song.Patterns.TryGetValue(barPattern, out string? definedGrid) ? definedGrid : barPattern;
                string named = chosen.Trim().Trim('"');
                if (!plainSteps && NamedGrids.TryGetValue(named, out string? namedGrid))
                {
                    chosen = namedGrid;   // beat-A and beat-B as grids, so that they follow the steps of the bar
                }

                int played = barsPlayed.GetValueOrDefault(chosen);
                barsPlayed[chosen] = played + 1;
                if (chosen.Contains(':', StringComparison.Ordinal))
                {
                    // voice grids (kick:x...x... snare:....x...): the steps are used, as with a sample kit: sixteen steps in a 4/4 bar, a shorter grid repeats inside the bar,
                    // a longer grid goes on over the next bars (32 steps are two bars)
                    foreach ((string voice, string steps, int levelDb, int ownSwing, bool mute) in GridVoices(chosen))
                    {
                        if (mute)
                        {
                            continue;
                        }

                        DrumVoice kind = VoiceOf(voice);
                        for (int s = 0; s < stepsInBar; s++)
                        {
                            if (!GridLevel(steps[(steps.Length <= stepsInBar ? s : (played * stepsInBar) + s) % steps.Length], out int level))
                            {
                                continue;
                            }

                            int at = barStart + (s * step) + (s % 2 == 1 ? step * (ownSwing >= 0 ? ownSwing : swing) / 100 : 0);
                            double loudness = level / 100.0 * Math.Pow(10, levelDb / 20.0);
                            if (human > 0)
                            {
                                loudness *= 1 + (((long)((jitter.Next() >> 40) % 2001) - 1000) * human / 500_000.0);      // up to 20 % louder or softer at humanize 100
                                at += (int)((((long)((jitter.Next() >> 40) % 1001)) - 500) * human * r.SampleRate / 10_000_000);   // up to 5 ms early or late at humanize 100
                            }

                            hits.Add(new GridHit(Math.Max(0, at), voice, kind, step * kind.Steps, (int)(kind.Amp * loudness)));
                        }
                    }

                    continue;
                }

                int[] kick = named == "beat-B" ? [0, 6, 8, 14] : [0, 8];
                for (int s = 0; s < stepsInBar; s++)
                {
                    int at = barStart + s * step;
                    if (kick.Contains(s))
                    {
                        Hit(buf, at, step * 4, true, 30000, noise, 60_000_000, 40_000_000);
                    }

                    if (s is 4 or 12)
                    {
                        Hit(buf, at, step * 3, false, 18000, noise);
                    }

                    if (s % 2 == 0)
                    {
                        Hit(buf, at, step, false, 6000, noise);
                    }
                }
            }

            // choke: the voice before the colon is cut short by the next hit of any voice after it (open:hat cuts an open hat when a closed hat is struck)
            Dictionary<string, string[]> choke = ChokeRules(ParamText("choke"));
            foreach (GridHit hit in hits)
            {
                int length = hit.Length;
                if (choke.TryGetValue(hit.Voice, out string[]? cutters))
                {
                    int next = hits.Where(h => h.At > hit.At && cutters.Contains(h.Voice)).Select(h => h.At).DefaultIfEmpty(int.MaxValue).Min();
                    length = Math.Min(length, Math.Max(1, next - hit.At));
                }

                Hit(buf, hit.At, length, hit.Kind.Tonal, hit.Amp, noise, hit.Kind.High, hit.Kind.Drop);
            }

            return Audio(r, "audio", buf);
        }

        /// <summary>The built-in voices: kick and toms are pitched hits that fall, the others bursts of noise of different length (a rim is a click, a clap a short burst, a crash a long one). A voice the built-in sound does not know sounds like a closed hat.</summary>
        private static DrumVoice VoiceOf(string voice)
        {
            if (voice.StartsWith("kick", StringComparison.Ordinal) || voice == "bd") return new DrumVoice(true, 4, 30000, 60_000_000, 40_000_000);
            if (voice.StartsWith("snare", StringComparison.Ordinal) || voice == "sd") return new DrumVoice(false, 3, 18000, 0, 0);
            if (voice.Contains("tom", StringComparison.Ordinal)) return new DrumVoice(true, 4, 22000, voice.Contains("low", StringComparison.Ordinal) ? 90_000_000 : voice.Contains("hi", StringComparison.Ordinal) ? 160_000_000 : 120_000_000, 55_000_000);
            if (voice.Contains("clap", StringComparison.Ordinal)) return new DrumVoice(false, 2, 16000, 0, 0);
            if (voice.Contains("rim", StringComparison.Ordinal)) return new DrumVoice(false, 1, 14000, 0, 0);
            if (voice.Contains("open", StringComparison.Ordinal) || voice == "oh") return new DrumVoice(false, 4, 6000, 0, 0);
            if (voice.Contains("ride", StringComparison.Ordinal) || voice.Contains("crash", StringComparison.Ordinal) || voice.Contains("cymbal", StringComparison.Ordinal)) return new DrumVoice(false, 8, 5000, 0, 0);
            return new DrumVoice(false, 1, 6000, 0, 0);
        }

        /// <summary>The choke text ("open:hat,pedal; ...") as voice to the voices that cut it.</summary>
        private static Dictionary<string, string[]> ChokeRules(string text)
        {
            Dictionary<string, string[]> rules = new(StringComparer.Ordinal);
            foreach (string rule in text.Trim().Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = rule.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    rules[rule[..colon].Trim().ToLowerInvariant()] = [.. rule[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => v.ToLowerInvariant())];
                }
            }

            return rules;
        }

        /// <summary>The voices of a grid text ("kick:x...x...@l-3,s30 snare:....x..."): the lower-case voice name, its steps, and its options after an @: m (mute), l (level in dB), s (swing). The pan and tune options belong to a sample kit and are not used here.</summary>
        private static List<(string Voice, string Steps, int LevelDb, int Swing, bool Mute)> GridVoices(string text)
        {
            List<(string Voice, string Steps, int LevelDb, int Swing, bool Mute)> voices = [];
            foreach (string part in text.Trim().Trim('"').Split([' ', '|', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                string steps = part[(colon + 1)..];
                int levelDb = 0, swing = -1;
                bool mute = false;
                int at = steps.IndexOf('@', StringComparison.Ordinal);
                if (at >= 0)
                {
                    foreach (string option in steps[(at + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (option == "m")
                        {
                            mute = true;
                        }
                        else if (option.Length > 1 && option[0] == 'l' && int.TryParse(option[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int l))
                        {
                            levelDb = Math.Clamp(l, -24, 12);
                        }
                        else if (option.Length > 1 && option[0] == 's' && int.TryParse(option[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int sw))
                        {
                            swing = Math.Clamp(sw, 0, 60);
                        }
                    }

                    steps = steps[..at];
                }

                if (steps.Length > 0)
                {
                    voices.Add((part[..colon].ToLowerInvariant(), steps, levelDb, swing, mute));
                }
            }

            return voices;
        }

        /// <summary>The loudness of a grid character in percent: x a hit, X an accent, o a ghost, 1 to 9 a velocity; a . (or anything else) is a rest.</summary>
        private static bool GridLevel(char c, out int level)
        {
            level = c switch { 'x' => 100, 'X' => 127, 'o' => 40, >= '1' and <= '9' => (c - '0') * 100 / 9, _ => 0 };
            return level > 0;
        }

        private static void Hit(short[] buf, int start, int length, bool tonal, int amp, Ample.Audio.Random.StableRng noise, long high = 60_000_000, long drop = 40_000_000)
        {
            uint phase = 0;
            for (int i = 0; i < length && start + i < buf.Length; i++)
            {
                int env = (int)((long)amp * (length - i) / length);
                int sample;
                if (tonal)
                {
                    phase += (uint)(high - ((long)i * drop / length));
                    sample = (int)((long)FixedAudio.Triangle(phase) * env / 32768);
                }
                else
                {
                    sample = (int)(((long)(noise.Next() >> 48) - 32768) * env / 32768);
                }

                buf[start + i] = FixedAudio.Clip(buf[start + i] + sample);
            }
        }
    }

    private sealed class Bass : IBlockRenderer
    {
        public string BlockType => "bass";

        public string Id => "builtin.bass";

        public string Version => "6";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            if (SampleSet.FolderOf(r.ProjectRoot, r.AmpleRoot, r.Block) is { } folder)
            {
                return SampledInstruments.Stereo(r, pass => SampledInstruments.Bass(pass, folder));
            }

            short[] buf = Buffer(r);
            SampledInstruments.Humanizer human = HumanizerOf(r);
            int beat = (int)(r.SamplesPerBar / r.BeatsPerBar);
            foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
            {
                int[] notes = Notes(r, bar);
                if (notes.Length == 0)
                {
                    continue;
                }

                uint inc = FixedAudio.Increment(notes.Min() - 12, r.SampleRate);
                uint phase = 0;
                for (int b = 0; b < (int)Math.Ceiling(r.BeatsPerBar); b++)
                {
                    (int offset, int loud) = human.On ? human.Next() : (0, 32768);
                    for (int i = 0; i < beat && (b * beat) + i < r.SamplesPerBar; i++)
                    {
                        phase += inc;
                        int idx = (bar - r.FirstBar) * r.SamplesPerBar + b * beat + i + offset;
                        if (idx < 0 || idx >= buf.Length)
                        {
                            continue;
                        }

                        long env = 20000L * (beat - i) / beat * loud / 32768;
                        buf[idx] = FixedAudio.Clip(buf[idx] + (FixedAudio.Square(phase) * env / 20000));
                    }
                }
            }

            return Audio(r, "audio", WithGain(r, buf));
        }
    }

    private sealed class Piano : IBlockRenderer
    {
        public string BlockType => "piano";

        public string Id => "builtin.piano";

        public string Version => "6";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            if (SampleSet.FolderOf(r.ProjectRoot, r.AmpleRoot, r.Block) is { } folder)
            {
                return SampledInstruments.Stereo(r, pass => SampledInstruments.Keys(pass, folder));
            }

            short[] buf = Buffer(r);
            SampledInstruments.Humanizer human = HumanizerOf(r);
            foreach (int bar in r.ActiveBars.Where(b => b >= r.FirstBar && b < r.FirstBar + r.BarCount).Order())
            {
                int barStart = (bar - r.FirstBar) * r.SamplesPerBar;
                foreach ((int note, double beat, double beats, _) in NoteEvents(r, bar))
                {
                    (int offset, int loud) = human.On ? human.Next() : (0, 32768);
                    int start = Math.Max(0, barStart + (int)(beat * r.SamplesPerBar / r.BeatsPerBar) + offset);
                    int length = Math.Max(1, (int)(beats * r.SamplesPerBar / r.BeatsPerBar));
                    uint inc = FixedAudio.Increment(note, r.SampleRate);
                    uint phase = 0;
                    for (int i = 0; i < length && start + i < buf.Length; i++)
                    {
                        phase += inc;
                        long env = 9000L * (length - i) / length * loud / 32768;
                        buf[start + i] = FixedAudio.Clip(buf[start + i] + (((FixedAudio.Triangle(phase) + FixedAudio.Saw(phase)) / 2) * env / 32768));
                    }
                }
            }

            return Audio(r, "audio", WithGain(r, buf));
        }
    }

    private sealed class Mixer(string blockType) : IBlockRenderer
    {
        public string BlockType => blockType;

        public string Id => "builtin." + blockType;

        public string Version => blockType == "bus" ? "1" : "3";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string[] gains = (r.Block.Parameters.FirstOrDefault(p => p.Name == "gains")?.Value ?? "0 0 0 0").Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long[] sum = new long[r.BarCount * r.SamplesPerBar];
            // pan (left, centre, right or a number from -32 to 32 per input) and channels stereo make the mix stereo even when every input is mono; without them the mix is as before
            string[] pans = (r.Block.Parameters.FirstOrDefault(p => p.Name == "pan")?.Value ?? "c").Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int PanOf(int k) => k - 1 < pans.Length ? pans[k - 1].ToLowerInvariant() switch { "l" or "left" => -PanTable.Range, "r" or "right" => PanTable.Range, "c" or "centre" or "center" => 0, var n => int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, -PanTable.Range, PanTable.Range) : 0 } : 0;
            bool placed = Enumerable.Range(1, 16).Any(k => PanOf(k) != 0) || r.Block.Parameters.FirstOrDefault(p => p.Name == "channels")?.Value.Trim().Trim('"') == "stereo";
            long[]? rightSum = placed || r.Inputs.Values.Any(data => ChannelAudio.IsStereo(data)) ? new long[sum.Length] : null;
            // a bus has one more gain for the whole sum and a mute
            bool muted = blockType == "bus" && r.Block.Parameters.FirstOrDefault(p => p.Name == "mute")?.Value.Trim().Trim('"') == "on";
            int busDb = blockType == "bus" ? AudioEffects.ReadDb(r.Block.Parameters.FirstOrDefault(p => p.Name == "gain")?.Value, 0, -60, FixedAudio.WideMaxDb) : 0;
            int busGain = muted ? 0 : FixedAudio.GainFromDbWide(busDb);
            for (int k = 1; k <= 16; k++)
            {
                if (!r.Inputs.TryGetValue("in" + k, out byte[]? data))
                {
                    continue;
                }

                int db = k - 1 < gains.Length && int.TryParse(gains[k - 1], out int g) ? g : 0;
                int gain = FixedAudio.GainFromDbWide(db);
                if (rightSum is not null)
                {
                    (StereoBuffer channels, _) = ChannelAudio.Decode(data);
                    int pan = PanOf(k);
                    int leftScale = pan == 0 ? 32768 : (int)Math.Min(32768, (long)PanTable.LeftGain(pan) * 32768 / PanTable.LeftGain(0));
                    int rightScale = pan == 0 ? 32768 : (int)Math.Min(32768, (long)PanTable.RightGain(pan) * 32768 / PanTable.RightGain(0));
                    for (int i = 0; i < sum.Length && i < channels.Length; i++) { sum[i] += (long)PanTable.Apply(channels.Left[i], leftScale) * gain / 32768; rightSum[i] += (long)PanTable.Apply(channels.Right[i], rightScale) * gain / 32768; }
                    continue;
                }
                (short[] samples, _) = Wav.Decode(data);
                for (int i = 0; i < sum.Length && i < samples.Length; i++)
                {
                    sum[i] += (long)samples[i] * gain / 32768;
                }
            }

            if (blockType == "bus")
            {
                for (int i = 0; i < sum.Length; i++) { sum[i] = sum[i] * busGain / 32768; if (rightSum is not null) rightSum[i] = rightSum[i] * busGain / 32768; }
            }

            // channels mono: the two sides are added into one (the mean), so the mix is a plain mono file even when an input is stereo
            if (rightSum is not null && r.Block.Parameters.FirstOrDefault(p => p.Name == "channels")?.Value.Trim().Trim('"') == "mono")
            {
                for (int i = 0; i < sum.Length; i++) sum[i] = (sum[i] + rightSum[i]) / 2;
                rightSum = null;
            }

            if (rightSum is not null) return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("mix", StereoWav.Encode(new StereoBuffer([.. sum.Select(sample => FixedAudio.Limit(sample, true))], [.. rightSum.Select(sample => FixedAudio.Limit(sample, true))]), r.SampleRate))]);
            return Audio(r, "mix", [.. sum.Select(sample => FixedAudio.Limit(sample, true))]);
        }
    }

    private sealed class Master : IBlockRenderer
    {
        public string BlockType => "master";

        public string Id => "builtin.master";

        public string Version => "4";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            if (!r.Inputs.TryGetValue("mix", out byte[]? data))
            {
                return Audio(r, "master", Buffer(r));
            }

            // the limiter parameter is the ceiling: peaks above it are pulled down (look-ahead, no clipping); quieter music passes untouched
            string text = r.Block.Parameters.FirstOrDefault(p => p.Name == "limiter")?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault("limiter") ?? "-1 dB";
            byte[] limited = AudioEffects.LimitAudio(data, Math.Clamp(AudioEffects.Read(text, -1), -12, 0));
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("master", Slice(r, limited))]);
        }

        /// <summary>The master slice: only the bars slice_from to slice_to (counted in the song, both included) are kept and the sound starts at the beginning of that part; without them the whole song is the output.</summary>
        private static byte[] Slice(RenderRequest r, byte[] audio)
        {
            string? Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"');
            bool hasFrom = int.TryParse(Text("slice_from"), NumberStyles.None, CultureInfo.InvariantCulture, out int from), hasTo = int.TryParse(Text("slice_to"), NumberStyles.None, CultureInfo.InvariantCulture, out int to);
            if (!hasFrom && !hasTo) return audio;
            from = hasFrom ? Math.Max(from, 1) : r.FirstBar;
            to = hasTo ? to : r.FirstBar + r.BarCount - 1;
            (StereoBuffer both, int rate) = ChannelAudio.Decode(audio);
            long start = Math.Clamp((long)(from - r.FirstBar) * r.SamplesPerBar, 0, both.Length), end = Math.Clamp((long)(to - r.FirstBar + 1) * r.SamplesPerBar, start, both.Length);
            if (start == 0 && end == both.Length) return audio;
            short[] left = both.Left[(int)start..(int)end], right = both.Right[(int)start..(int)end];
            return ChannelAudio.IsStereo(audio) ? StereoWav.Encode(new StereoBuffer(left, right), rate) : Wav.Encode(left, rate);
        }
    }

    private sealed class Lyrics : IBlockRenderer
    {
        public string BlockType => "lyrics";

        public string Id => "builtin.lyrics";

        public string Version => "3";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("words", Encoding.UTF8.GetBytes(WordsOf(r)))]);
    }

    private sealed class MoodScene : IBlockRenderer
    {
        public string BlockType => "mood-scene";

        public string Id => "builtin.mood-scene";

        public string Version => "1";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string direction = (r.Block.Parameters.FirstOrDefault(p => p.Name == "direction")?.Value ?? string.Empty).Replace("\\n", "\n", StringComparison.Ordinal);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("style", Encoding.UTF8.GetBytes(direction))]);
        }
    }

    /// <summary>The words of a lyrics block: the lyrics file of the project (without its [bar N] markers), empty when there is no file or it is empty.</summary>
    internal static string WordsOf(RenderRequest r)
    {
        if (LyricsFile(r.ProjectRoot, r.Block) is { } file)
        {
            return string.Join('\n', File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => BarMarker().Replace(l, string.Empty))).Trim();
        }

        return string.Empty;
    }

    /// <summary>The full path of the lyrics file a lyrics block names with <c>file</c>, when it exists in the project and is not empty; else null.</summary>
    public static string? LyricsFile(string? projectRoot, BlockNode block)
    {
        if (block.Type != "lyrics" || projectRoot is null || block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') is not { Length: > 0 } value)
        {
            return null;
        }

        try
        {
            string full = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, value);
            return File.Exists(full) && new FileInfo(full).Length > 0 ? full : null;
        }
        catch (AmpleException)
        {
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^\\s*\\[bar\\s+[0-9.]+\\]\\s*", System.Text.RegularExpressions.RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex BarMarker();

    private sealed class Group : IBlockRenderer
    {
        public string BlockType => "group";

        public string Id => "builtin.group";

        public string Version => "1";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RenderOutput>>(r.Inputs.TryGetValue("in", out byte[]? data) ? [new RenderOutput("out", data)] : []);
    }
}
