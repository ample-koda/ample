using System.Globalization;
using System.Security.Cryptography;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>One sample file of a set.</summary>
/// <param name="Name">File name without extension, lower case (<c>kick</c>, <c>a2</c>, <c>eb3</c>).</param>
/// <param name="Hash">sha256 of the file, <c>sha256:</c>-prefixed.</param>
/// <param name="Samples">The audio as 16-bit mono at the build's sample rate.</param>
public sealed record SampleFile(string Name, string Hash, short[] Samples);

/// <summary>Splits a sample file name (without extension) into base name, layer and round robin: <c>name</c> is layer 1, round robin 1; <c>name_v2_r3</c>, <c>name_v2</c> and <c>name_r3</c> are explicit.</summary>
public static class SampleName
{
    /// <summary>Parses a name (lower case); only the exact suffix pattern (<c>_v&lt;n&gt;</c> then <c>_r&lt;n&gt;</c>, n from 1 to 999, base not empty) counts, anything else is a plain base name.</summary>
    public static (string Base, int Layer, int RoundRobin) Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string rest = name.ToLowerInvariant();
        int rr = 1;
        int layer = 1;
        int cut = rest.LastIndexOf("_r", StringComparison.Ordinal);
        if (cut > 0 && Number(rest.AsSpan(cut + 2)) is int r)
        {
            rr = r;
            rest = rest[..cut];
        }

        cut = rest.LastIndexOf("_v", StringComparison.Ordinal);
        if (cut > 0 && Number(rest.AsSpan(cut + 2)) is int l)
        {
            layer = l;
            rest = rest[..cut];
        }

        return (rest, layer, rr);
    }

    private static int? Number(ReadOnlySpan<char> s)
    {
        if (s.Length is 0 or > 3)
        {
            return null;
        }

        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        int n = int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        return n >= 1 ? n : null;
    }
}

/// <summary>
/// A folder of WAV files in the project that a block plays: a drum kit (<c>kick.wav</c>, <c>snare.wav</c>, <c>hat.wav</c>, ...) or the notes of an instrument
/// (<c>a2.wav</c>, <c>eb3.wav</c>, ...). The block's <c>kit</c> or <c>set</c> parameter names the folder (for example <c>samples/rusty-kit</c>). The hash of every
/// file goes into the plan of the take, so changing a sample makes a new take and the plan says exactly which samples were used.
/// All arithmetic is integer (fixed point), so the same files give the same samples on every machine.
/// </summary>
public sealed class SampleSet
{
    private static readonly long[] RatioQ16 = BuildRatios();

    private SampleSet(string folder, IReadOnlyDictionary<string, SampleFile> files, IReadOnlyDictionary<string, IReadOnlyList<(int Layer, int RoundRobin)>> variants, int gainQ15, int tune, bool stereo,
        IReadOnlyDictionary<string, (int Start, int End, int Crossfade)>? loops = null, IntegerEnvelope? envelope = null)
    {
        Stereo = stereo;
        Folder = folder;
        Files = files;
        Variants = variants;
        Instrument = Instrument.FromFiles(files.Values, gainQ15, tune, loops, envelope is { } e ? AllNames(files.Keys, e) : null);
    }

    private static Dictionary<string, IntegerEnvelope> AllNames(IEnumerable<string> fileNames, IntegerEnvelope envelope) =>
        fileNames.Select(f => SampleName.Parse(f).Base).Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => envelope, StringComparer.Ordinal);

    /// <summary>An <c>.sfz</c>-sourced set: the regions come straight from <see cref="Sfz.SfzReader"/>, not from the <c>name_v&lt;L&gt;_r&lt;R&gt;.wav</c>
    /// filename convention, so this bypasses <see cref="Engine.Instrument.FromFiles"/>. There is no per-name file lookup (<see cref="Need"/>) and no
    /// layer/round-robin report (<see cref="Variants"/> stays empty) for an SFZ set yet; see <c>docs/samples.md</c> "SFZ sets".</summary>
    private SampleSet(string folder, Instrument instrument, bool stereo, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? chokeRules = null, int? chokeFadeMs = null)
    {
        Stereo = stereo;
        Folder = folder;
        Files = new Dictionary<string, SampleFile>(StringComparer.Ordinal);
        Variants = new Dictionary<string, IReadOnlyList<(int Layer, int RoundRobin)>>(StringComparer.Ordinal);
        Instrument = instrument;
        Warnings = warnings ?? [];
        ChokeRules = chokeRules ?? [];
        ChokeFadeMs = chokeFadeMs;
    }

    /// <summary>Gets a value indicating whether a project-relative or <c>pack:</c> value names an <c>.sfz</c> file rather than a folder of WAVs.</summary>
    public static bool IsSfz(string value) => value.EndsWith(".sfz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Build warnings from reading the set (today: only an <c>.sfz</c> set has any — unread opcodes, AMP5394); empty for a plain WAV folder.</summary>
    public IReadOnlyList<string> Warnings { get; } = [];

    /// <summary>SFZ03: choke rules derived from an <c>.sfz</c> drum kit's <c>group</c>/<c>off_by</c> opcodes, in the same <c>voice:closer1,closer2</c>
    /// text form a <c>drums</c> block's own <c>choke</c> parameter uses; empty for a plain WAV folder or a pitched <c>.sfz</c> set.</summary>
    public IReadOnlyList<string> ChokeRules { get; } = [];

    /// <summary>SFZ03: the choke fade duration in milliseconds (SFZ <c>off_time</c>, the longest value found across the kit's closer regions),
    /// overriding the drums renderer's fixed ~7 ms choke fade for the whole block; null (the default, for a plain WAV folder or a kit with no
    /// off_time) keeps that fixed fade.</summary>
    public int? ChokeFadeMs { get; }

    /// <summary>Gets, per base name, the (layer, round robin) pairs found on disk, sorted. The engine plays only layer 1, round robin 1 until the layer chunks land.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(int Layer, int RoundRobin)>> Variants { get; }

    /// <summary>The number of distinct layers on disk for a base name (0 when the name is unknown).</summary>
    public int Layers(string baseName) => Variants.TryGetValue(baseName, out IReadOnlyList<(int Layer, int RoundRobin)>? v) ? v.Select(x => x.Layer).Distinct().Count() : 0;

    /// <summary>The most round robins any layer of a base name has on disk (0 when the name is unknown).</summary>
    public int RoundRobins(string baseName) => Variants.TryGetValue(baseName, out IReadOnlyList<(int Layer, int RoundRobin)>? v) ? v.GroupBy(x => x.Layer).Max(g => g.Count()) : 0;

    /// <summary>Gets a value indicating whether any file of the set has two channels (A02: such a set is rendered once per channel and the result is stereo).</summary>
    public bool Stereo { get; }

    /// <summary>Gets the folder as the block names it.</summary>
    public string Folder { get; }

    /// <summary>Gets the instrument (regions) built from the files.</summary>
    public Instrument Instrument { get; }

    /// <summary>Gets the files by name.</summary>
    public IReadOnlyDictionary<string, SampleFile> Files { get; }

    /// <summary>The parameters of a block that may name a sample folder.</summary>
    public static readonly string[] Parameters = ["kit", "set"];

    /// <summary>Finds the folder a block names (project relative, must exist and hold WAV files); null when the block names none (for example the built-in <c>kit/tape</c>).</summary>
    public static string? FolderOf(string? projectRoot, BlockNode block) => FolderOf(projectRoot, null, block);

    /// <summary>Finds a project-relative or <c>pack:</c> sample folder for a server with a known AMPLE root.</summary>
    public static string? FolderOf(string? projectRoot, string? ampleRoot, BlockNode block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (projectRoot is null)
        {
            return null;
        }

        foreach (string name in Parameters)
        {
            string? value = block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"');
            if (string.IsNullOrEmpty(value) || !value.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                string full = ResolveFolder(projectRoot, ampleRoot, value);
                if (IsSfz(value) ? File.Exists(full) : Directory.Exists(full) && Directory.EnumerateFiles(full, "*.wav").Any())
                {
                    return value;
                }
            }
            catch (AmpleException)
            {
                // A path that cannot be a project path is simply not a sample folder.
            }
        }

        return null;
    }

    /// <summary>The name of the optional settings file of a set: <c>{ "gain": -3, "tune": 0 }</c> (gain in dB, tune in semitones the recordings are sharp).</summary>
    public const string SettingsFile = "set.json";

    /// <summary>The pitch ratio (16.16 fixed point) of a semitone shift (-24..24), the same table <see cref="Shift"/> resamples with — exposed so a caller can size a buffer before shifting without duplicating the ratio math.</summary>
    public static long RatioQ16Of(int semitones) => RatioQ16[Math.Clamp(semitones, -24, 24) + 24];

    /// <summary>The hash of the settings file of a folder (<c>sha256:</c>-prefixed), or null when there is none.</summary>
    public static string? SettingsHash(string projectRoot, string folder) => SettingsHash(projectRoot, null, folder);

    /// <summary>Gets the settings-file hash for a project-relative or installed-pack sample folder.</summary>
    public static string? SettingsHash(string projectRoot, string? ampleRoot, string folder)
    {
        string path = Path.Combine(ResolveFolder(projectRoot, ampleRoot, folder), SettingsFile);
        return File.Exists(path) ? "sha256:" + FileHashCache.Sha256Hex(path) : null;
    }

    /// <summary>
    /// Settings of a sample folder (<c>S08</c>): <c>gain</c>/<c>tune</c> as before, plus optional <c>loops</c> (per base
    /// name, sustain-loop points and crossfade in samples at the build rate: <c>{ "arco": { "start": 1000, "end": 5000,
    /// "crossfade": 200 } }</c>, turning <c>LoopMode</c> from <c>none</c> to <c>sustain</c> for that name only) and
    /// <c>envelope</c> (an integer ADSR in whole milliseconds, applied per note in place of the renderer's fixed fade:
    /// <c>{ "attack": 5, "decay": 50, "sustain": 80, "release": 200 }</c>, one envelope for the whole set). Neither key
    /// changes a set without it: <c>Load</c> only ever builds "sustain"/non-null fields when the JSON actually has them.
    /// </summary>
    private static (int GainQ15, int Tune, IReadOnlyDictionary<string, (int Start, int End, int Crossfade)> Loops, IntegerEnvelope? Envelope) ReadSettings(string full, string folder, int sampleRate)
    {
        string path = Path.Combine(full, SettingsFile);
        if (!File.Exists(path))
        {
            return (32768, 0, EmptyLoops, null);
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            int gain = doc.RootElement.TryGetProperty("gain", out System.Text.Json.JsonElement g) ? g.GetInt32() : 0;
            int tune = doc.RootElement.TryGetProperty("tune", out System.Text.Json.JsonElement t) ? t.GetInt32() : 0;
            Dictionary<string, (int, int, int)> loops = new(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("loops", out System.Text.Json.JsonElement loopsEl) && loopsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (System.Text.Json.JsonProperty p in loopsEl.EnumerateObject())
                {
                    int start = p.Value.TryGetProperty("start", out System.Text.Json.JsonElement s) ? s.GetInt32() : 0;
                    int end = p.Value.GetProperty("end").GetInt32();
                    int crossfade = p.Value.TryGetProperty("crossfade", out System.Text.Json.JsonElement c) ? c.GetInt32() : 0;
                    loops[p.Name.ToLowerInvariant()] = (Math.Max(0, start), Math.Max(1, end), Math.Max(0, crossfade));
                }
            }

            IntegerEnvelope? envelope = null;
            if (doc.RootElement.TryGetProperty("envelope", out System.Text.Json.JsonElement envEl) && envEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                int attackMs = envEl.TryGetProperty("attack", out System.Text.Json.JsonElement a) ? a.GetInt32() : 0;
                int decayMs = envEl.TryGetProperty("decay", out System.Text.Json.JsonElement d) ? d.GetInt32() : 0;
                int sustainPercent = envEl.TryGetProperty("sustain", out System.Text.Json.JsonElement su) ? su.GetInt32() : 100;
                int releaseMs = envEl.TryGetProperty("release", out System.Text.Json.JsonElement r) ? r.GetInt32() : 0;
                int holdMs = envEl.TryGetProperty("hold", out System.Text.Json.JsonElement h) ? h.GetInt32() : 0;
                envelope = new IntegerEnvelope(
                    Math.Max(0, attackMs) * sampleRate / 1000,
                    Math.Max(0, decayMs) * sampleRate / 1000,
                    Math.Clamp(sustainPercent, 0, 100) * IntegerEnvelope.One / 100,
                    Math.Max(0, releaseMs) * sampleRate / 1000,
                    Math.Max(0, holdMs) * sampleRate / 1000);
            }

            return (gain == 0 ? 32768 : FixedAudio.GainFromDbWide(Math.Clamp(gain, -24, FixedAudio.WideMaxDb)), Math.Clamp(tune, -24, 24), loops, envelope);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new AmpleException("AMP5350", $"The settings of '{folder}' ({SettingsFile}) cannot be used: it must be an object with whole-number gain (dB), tune (semitones), and optional loops/envelope objects.");
        }
    }

    private static readonly IReadOnlyDictionary<string, (int Start, int End, int Crossfade)> EmptyLoops = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);

    /// <summary>The hashes of the WAV files of a folder (name to hash), sorted by name.</summary>
    public static IReadOnlyList<(string Name, string Hash)> Hashes(string projectRoot, string folder) => Hashes(projectRoot, null, folder);

    /// <summary>Gets the sample hashes from a project-relative or installed-pack sample folder, or (for an <c>.sfz</c> value) every file it actually used: the <c>.sfz</c> itself, its <c>#include</c>s and the samples it references.</summary>
    public static IReadOnlyList<(string Name, string Hash)> Hashes(string projectRoot, string? ampleRoot, string folder)
    {
        string full = ResolveFolder(projectRoot, ampleRoot, folder);
        if (IsSfz(folder))
        {
            return [.. Sfz.SfzReader.Read(full, 44100).UsedFiles.Select(f => (f.RelativePath, f.Hash))];
        }

        return [.. Directory.EnumerateFiles(full, "*.wav").Order(StringComparer.Ordinal).Select(f => (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), "sha256:" + FileHashCache.Sha256Hex(f)))];
    }

    /// <summary>Loads the folder at a sample rate.</summary>
    /// <exception cref="AmpleException">AMP5350 when the folder or a file cannot be used.</exception>
    public static SampleSet Load(string projectRoot, string folder, int sampleRate) => Load(projectRoot, null, folder, sampleRate);

    /// <summary>Loads a project-relative or installed-pack sample folder, or an <c>.sfz</c> file, at a sample rate. <paramref name="asDrumKit"/>
    /// is true for a <c>drums</c> block's <c>kit</c> (an <c>.sfz</c> region then becomes an unpitched, voice-named region via the General MIDI
    /// drum map instead of a pitched one) and false for a <c>set</c> (guitar/bass/piano); it is ignored for a plain WAV folder.</summary>
    public static SampleSet Load(string projectRoot, string? ampleRoot, string folder, int sampleRate, int channel = 0, bool asDrumKit = false)
    {
        string full = ResolveFolder(projectRoot, ampleRoot, folder);
        if (IsSfz(folder))
        {
            if (!File.Exists(full))
            {
                throw new AmpleException("AMP5350", $"The sample file '{folder}' is not in the project.");
            }

            Sfz.SfzReader.Result sfz;
            try
            {
                sfz = Sfz.SfzReader.Read(full, sampleRate, channel, asDrumKit, JailRoot(projectRoot, ampleRoot, folder));
            }
            catch (AmpleException ex)
            {
                throw new AmpleException("AMP5350", $"The SFZ sample set '{folder}' cannot be used: {ex.Message}");
            }

            // Stereo detection needs the raw WAV bytes, which SfzReader does not keep after decoding; an SFZ set is
            // mixed to mono for now (same as a plain folder set until this is revisited), see docs/samples.md.
            return new SampleSet(folder, sfz.Instrument, false, sfz.Warnings, sfz.ChokeRules, sfz.ChokeFadeMs);
        }

        if (!Directory.Exists(full))
        {
            throw new AmpleException("AMP5350", $"The sample folder '{folder}' is not in the project.");
        }

        (int gainQ15, int tune, IReadOnlyDictionary<string, (int Start, int End, int Crossfade)> loops, IntegerEnvelope? envelope) = ReadSettings(full, folder, sampleRate);
        Dictionary<string, SampleFile> files = [];
        Dictionary<string, List<(int Layer, int RoundRobin)>> variants = [];
        bool anyStereo = false;
        foreach (string file in Directory.EnumerateFiles(full, "*.wav").Order(StringComparer.Ordinal))
        {
            (string name, int layer, int rr) = SampleName.Parse(Path.GetFileNameWithoutExtension(file));
            if (!variants.TryGetValue(name, out List<(int Layer, int RoundRobin)>? list))
            {
                variants[name] = list = [];
            }

            list.Add((layer, rr));
            byte[] bytes = File.ReadAllBytes(file);
            anyStereo |= ChannelAudio.IsStereo(bytes);
            try
            {
                files[Path.GetFileNameWithoutExtension(file).ToLowerInvariant()] = new SampleFile(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), ReadWav(bytes, sampleRate, channel));
            }
            catch (InvalidDataException ex)
            {
                throw new AmpleException("AMP5350", $"The sample '{Path.GetFileName(file)}' in '{folder}' cannot be used: {ex.Message}");
            }
        }

        return files.Count == 0 ? throw new AmpleException("AMP5350", $"The sample folder '{folder}' holds no WAV files.") : new SampleSet(folder, files, variants.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(int Layer, int RoundRobin)>)[.. kv.Value.Order()], StringComparer.Ordinal), gainQ15, tune, anyStereo, loops, envelope);
    }

    internal static string ResolveFolder(string projectRoot, string? ampleRoot, string folder)
    {
        if (!folder.StartsWith("pack:", StringComparison.Ordinal)) return ProjectPath.Resolve(projectRoot, folder);
        string relative = folder["pack:".Length..];
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part == "..")) throw new AmpleException("AMP5311", "The installed-pack sample path must be relative to a pack folder.");
        IReadOnlyList<string> searched = [.. AmpleHome.PackFolders(AmpleHome.DefaultPath(), ampleRoot, projectRoot).Select(root => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))];
        string? found = searched.FirstOrDefault(p => Directory.Exists(p) || File.Exists(p));
        return found ?? throw new AmpleException("AMP5311", "The sample pack folder is not installed. Searched: " + string.Join("; ", searched) + ". Run 'ample packs link <folder>' or the fetch command.");
    }

    /// <summary>The furthest-out folder an <c>.sfz</c> file's <c>#include</c>/<c>sample=</c> paths may resolve into (SFZ06): the whole
    /// project for a project-relative value, or the matched pack's own root folder for a <c>pack:</c> value - wider than the <c>.sfz</c>
    /// file's own folder (so a real pack whose programs and samples sit in sibling folders, like Karoryfer's Frankensnare, still works)
    /// but never the whole filesystem.</summary>
    internal static string JailRoot(string projectRoot, string? ampleRoot, string folder)
    {
        if (!folder.StartsWith("pack:", StringComparison.Ordinal))
        {
            return Path.GetFullPath(projectRoot);
        }

        string relative = folder["pack:".Length..];
        string? root = AmpleHome.PackFolders(AmpleHome.DefaultPath(), ampleRoot, projectRoot)
            .FirstOrDefault(r => { string p = Path.Combine(r, relative.Replace('/', Path.DirectorySeparatorChar)); return Directory.Exists(p) || File.Exists(p); });
        return Path.GetFullPath(root ?? projectRoot);
    }

    /// <summary>Gets a file by name or throws a plain message that lists what the folder has.</summary>
    public SampleFile Need(string name) => Files.TryGetValue(name, out SampleFile? f) ? f : throw new AmpleException("AMP5350", $"The sample folder '{Folder}' has no '{name}.wav'. It has: {string.Join(", ", Files.Keys)}.");

    /// <summary>The native sample rate of a WAV file's <c>fmt </c> chunk, or 0 when it cannot be found — used to rescale a loop point given in the file's own frames (SFZ's <c>loop_start</c>/<c>loop_end</c>) to the build rate <see cref="ReadWav"/> resamples to.</summary>
    public static int NativeRateOf(ReadOnlySpan<byte> b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
        {
            return 0;
        }

        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b.Slice(pos, 4));
            int size = BitConverter.ToInt32(b.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id == "fmt " && body + 8 <= b.Length)
            {
                return BitConverter.ToInt32(b.Slice(body + 4, 4));
            }

            pos = body + size + (size & 1);
        }

        return 0;
    }

    /// <summary>Reads 8, 16, 24 or 32-bit PCM or 32-bit float WAV (one or two channels), mixes to mono, converts to 16 bits and resamples (linear, fixed point).</summary>
    public static short[] ReadWav(ReadOnlySpan<byte> b, int targetRate, int channel = 0)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
        {
            throw new InvalidDataException("not a WAV file");
        }

        int pos = 12;
        int code = 0;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        int dataAt = -1;
        int dataLength = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b.Slice(pos, 4));
            int size = BitConverter.ToInt32(b.Slice(pos + 4, 4));
            int body = pos + 8;
            if (id == "fmt " && body + 16 <= b.Length)
            {
                code = BitConverter.ToInt16(b.Slice(body, 2));
                channels = BitConverter.ToInt16(b.Slice(body + 2, 2));
                rate = BitConverter.ToInt32(b.Slice(body + 4, 4));
                bits = BitConverter.ToInt16(b.Slice(body + 14, 2));
            }
            else if (id == "data")
            {
                dataAt = body;
                dataLength = size < 0 || body + size > b.Length ? b.Length - body : size;
                break;
            }

            pos = body + size + (size & 1);
        }

        if (dataAt < 0 || channels is < 1 or > 2 || rate <= 0 || !((code == 1 && bits is 8 or 16 or 24 or 32) || (code == 3 && bits == 32)))
        {
            throw new InvalidDataException("only PCM (8, 16, 24, 32 bit) or 32-bit float WAV with one or two channels can be played");
        }

        int bytes = bits / 8;
        int frames = dataLength / (bytes * channels);
        short[] mono = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            long sum = 0;
            int used = channel is 1 or 2 && channels == 2 ? 1 : channels;   // channel 1 or 2 picks the left or right of a stereo file, otherwise both are mixed
            for (int c = 0; c < channels; c++)
            {
                if (used == 1 && channels == 2 && c != channel - 1) continue;
                ReadOnlySpan<byte> at = b[(dataAt + ((((i * channels) + c) * bytes)))..];
                sum += code == 3 ? (long)(BitConverter.ToSingle(at) * 32768f) : bits switch
                {
                    8 => (at[0] - 128) << 8,
                    16 => BitConverter.ToInt16(at),
                    24 => ((at[2] << 24) | (at[1] << 16) | (at[0] << 8)) >> 16,
                    _ => BitConverter.ToInt32(at) >> 16,
                };
            }

            mono[i] = FixedAudio.Clip(sum / used);
        }

        return rate == targetRate ? mono : Resample(mono, (long)rate * 65536 / targetRate);
    }

    /// <summary>Plays a sample higher (positive) or lower (negative) by a number of semitones: the same note at another pitch, shorter or longer (like a tape).</summary>
    public static short[] Shift(short[] sample, int semitones)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return semitones == 0 ? sample : Resample(sample, RatioQ16[Math.Clamp(semitones, -24, 24) + 24]);
    }

    /// <summary>The MIDI note a file name stands for (<c>a2</c>, <c>eb3</c>, <c>c#4</c>; C4 is 60), or null when it is not a note name.</summary>
    public static int? NoteOf(string name)
    {
        name = SampleName.Parse(name).Base;
        if (name.Length < 2)
        {
            return null;
        }

        int index = "c d ef g a b".IndexOf(name[0], StringComparison.Ordinal);
        if (index < 0 || name[0] == ' ')
        {
            return null;
        }

        int i = 1;
        int accidental = 0;
        if (name[i] == '#')
        {
            accidental = 1;
            i++;
        }
        else if (name[i] == 'b' && name.Length > i + 1)
        {
            accidental = -1;
            i++;
        }

        return int.TryParse(name.AsSpan(i), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int octave) ? ((octave + 1) * 12) + index + accidental : null;
    }

    private static short[] Resample(short[] x, long stepQ16)
    {
        if (x.Length < 2 || stepQ16 <= 0)
        {
            return x;
        }

        long frames = (((long)x.Length - 1) << 16) / stepQ16;
        short[] y = new short[frames];
        for (long i = 0; i < frames; i++)
        {
            long p = i * stepQ16;
            int k = (int)(p >> 16);
            int f = (int)(p & 0xFFFF);
            y[i] = (short)(((x[k] * (65536L - f)) + (x[Math.Min(k + 1, x.Length - 1)] * (long)f)) >> 16);
        }

        return y;
    }

    private static long[] BuildRatios()
    {
        // 2^(n/12) for n = -24..24 in 16.16 fixed point, from the decimal twelfth root of two (no floating point, no platform math).
        const decimal Semitone = 1.0594630943592952645618252949m;
        long[] table = new long[49];
        decimal up = 1m;
        for (int n = 0; n <= 24; n++)
        {
            table[24 + n] = (long)Math.Round(up * 65536m);
            up *= Semitone;
        }

        for (int n = 1; n <= 24; n++)
        {
            table[24 - n] = (long)Math.Round(65536m * 65536m / table[24 + n]);
        }

        return table;
    }
}
