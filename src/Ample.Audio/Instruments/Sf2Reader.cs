using System.Buffers.Binary;
using System.Text;

namespace Ample.Audio.Instruments;

/// <summary>Raised for any damaged, truncated or unsupported SoundFont (diagnostic AMP5311). Never a crash on untrusted input.</summary>
internal sealed class Sf2FormatException(string message) : Exception(message);

/// <summary>One generator (operator and raw 16-bit amount).</summary>
internal readonly record struct Sf2Gen(ushort Oper, short Amount)
{
    /// <summary>Low byte as a range bound (key or velocity ranges).</summary>
    public int RangeLo => (ushort)Amount & 0xFF;
    /// <summary>High byte as a range bound.</summary>
    public int RangeHi => ((ushort)Amount >> 8) & 0xFF;
}

/// <summary>A preset or instrument zone: its generators in file order. The first zone without a terminal generator is the global zone.</summary>
internal sealed class Sf2Zone
{
    /// <summary>Generators of this zone.</summary>
    public List<Sf2Gen> Gens { get; } = new();
    /// <summary>Modulator count.</summary>
    public int ModulatorCount { get; set; }
    /// <summary>The modulators of this zone (SF2 8.2), in file order.</summary>
    public List<Sf2Mod> Mods { get; } = new();
    /// <summary>The generator with this operator, or null.</summary>
    public Sf2Gen? Find(ushort oper) { foreach (var g in Gens) if (g.Oper == oper) return g; return null; }
}

/// <summary>A preset header with its zones.</summary>
internal sealed class Sf2Preset
{
    /// <summary>Name (up to 20 characters).</summary>
    public string Name { get; set; } = "";
    /// <summary>MIDI program number.</summary>
    public int Program { get; set; }
    /// <summary>MIDI bank.</summary>
    public int Bank { get; set; }
    /// <summary>Zones in order.</summary>
    public List<Sf2Zone> Zones { get; } = new();
}

/// <summary>An instrument with its zones.</summary>
internal sealed class Sf2Instrument
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Zones in order.</summary>
    public List<Sf2Zone> Zones { get; } = new();
}

/// <summary>A sample header (positions are in sample frames of the smpl chunk).</summary>
internal sealed class Sf2Sample
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Start frame.</summary>
    public uint Start { get; set; }
    /// <summary>End frame (exclusive).</summary>
    public uint End { get; set; }
    /// <summary>Loop start frame.</summary>
    public uint LoopStart { get; set; }
    /// <summary>Loop end frame.</summary>
    public uint LoopEnd { get; set; }
    /// <summary>Sample rate in Hz.</summary>
    public uint Rate { get; set; }
    /// <summary>Original pitch (MIDI key).</summary>
    public int OriginalPitch { get; set; }
    /// <summary>Pitch correction in cents.</summary>
    public int PitchCorrection { get; set; }
    /// <summary>Linked sample index for stereo.</summary>
    public int Link { get; set; }
    /// <summary>Type flags (1 mono, 2 right, 4 left, 8 linked; 0x10 Ogg Vorbis compressed (SF3); 0x8000 ROM).</summary>
    public int Type { get; set; }

    /// <summary>Whether the sample is an Ogg Vorbis stream (SF3): then <see cref="ByteStart"/> and <see cref="ByteEnd"/> hold its bytes in the smpl chunk, and the frame positions
    /// are virtual (from <see cref="Start"/>, which is <see cref="ByteStart"/>), with the loop relative to the start of the decoded sample as SF3 writes it.</summary>
    public bool Compressed => (Type & 0x10) != 0;

    /// <summary>The first byte of a compressed sample in the smpl chunk.</summary>
    public long ByteStart { get; set; }

    /// <summary>The end (exclusive) of a compressed sample in the smpl chunk.</summary>
    public long ByteEnd { get; set; }

    /// <summary>Whether <see cref="End"/> of a compressed sample is known (it is set when the sample is decoded).</summary>
    public bool LaidOut { get; set; } = true;
}

/// <summary>A parsed SoundFont. Only the headers are in memory; sample data is read on demand.</summary>
internal sealed class Sf2Bank
{
    private readonly Func<Stream> _open;
    private readonly long _smplOffset;
    private readonly long _smplLength;
    private readonly long _sm24Offset;
    private readonly long _sm24Length;
    private readonly Dictionary<int, short[]> _decoded = [];

    internal Sf2Bank(Func<Stream> open, long smplOffset, long smplLength, long sm24Offset = 0, long sm24Length = 0)
    {
        _open = open; _smplOffset = smplOffset; _smplLength = smplLength; _sm24Offset = sm24Offset; _sm24Length = sm24Length;
    }

    /// <summary>Whether the bank has 24-bit samples (an sm24 chunk with one low byte for every frame of smpl, SF2 2.04); a chunk of another size is ignored, as the spec asks.</summary>
    public bool Has24Bit => _sm24Length > 0 && _sm24Length >= _smplLength / 2 && _sm24Length <= (_smplLength / 2) + 1;

    /// <summary>The bits below 16 that <see cref="ReadSampleFrames"/> adds: 8 with 24-bit samples, else 0.</summary>
    public int ExtraBits => Has24Bit ? 8 : 0;

    /// <summary>
    /// Makes the frame positions of a compressed (SF3) sample complete: it is decoded once (and kept) to learn its length. Uncompressed samples are complete already.
    /// </summary>
    public void LayOut(int sampleIndex)
    {
        if ((uint)sampleIndex >= (uint)Samples.Count) throw new Sf2FormatException("AMP5311: sample index out of range");
        Sf2Sample s = Samples[sampleIndex];
        if (s.LaidOut) return;
        short[] data = Decode(sampleIndex);
        s.End = checked(s.Start + (uint)data.Length);
        s.LaidOut = true;
    }

    /// <summary>Reads one sample with all its bits: 16-bit frames shifted up by <see cref="ExtraBits"/> with the low byte of sm24 below them, or a decoded SF3 sample.</summary>
    public int[] ReadSampleFrames(int sampleIndex)
    {
        short[] high = ReadSampleData(sampleIndex);
        int[] frames = new int[high.Length];
        if (!Has24Bit || Samples[sampleIndex].Compressed)
        {
            for (int i = 0; i < high.Length; i++) frames[i] = high[i];
            return frames;
        }

        Sf2Sample s = Samples[sampleIndex];
        byte[] low = new byte[high.Length];
        using (Stream st = _open())
        {
            st.Seek(_sm24Offset + s.Start, SeekOrigin.Begin);
            st.ReadExactly(low);
        }

        for (int i = 0; i < high.Length; i++) frames[i] = (high[i] << 8) | low[i];
        return frames;
    }

    private short[] Decode(int sampleIndex)
    {
        if (_decoded.TryGetValue(sampleIndex, out short[]? known)) return known;
        Sf2Sample s = Samples[sampleIndex];
        if (s.ByteEnd <= s.ByteStart || s.ByteEnd > _smplLength || s.ByteEnd - s.ByteStart > 64 * 1024 * 1024) throw new Sf2FormatException("AMP5311: compressed sample range outside the data chunk");
        byte[] bytes = new byte[s.ByteEnd - s.ByteStart];
        using (Stream st = _open())
        {
            st.Seek(_smplOffset + s.ByteStart, SeekOrigin.Begin);
            st.ReadExactly(bytes);
        }

        short[] data = Ample.Audio.IO.OggVorbis.Decode(bytes, 64 * 1024 * 1024) ?? throw new Sf2FormatException("AMP5311: a compressed (SF3) sample is not a readable Ogg Vorbis stream, or too long");

        _decoded[sampleIndex] = data;
        return data;
    }

    /// <summary>The file the bank was read from, when it came from a file.</summary>
    public string? SourcePath { get; internal set; }

    /// <summary>Bank name from INFO/INAM, empty when absent.</summary>
    public string Name { get; internal set; } = "";
    /// <summary>Major version from ifil.</summary>
    public int VersionMajor { get; internal set; }
    /// <summary>Minor version from ifil.</summary>
    public int VersionMinor { get; internal set; }
    /// <summary>Presets (the terminal record is dropped).</summary>
    public List<Sf2Preset> Presets { get; } = new();
    /// <summary>Instruments (the terminal record is dropped).</summary>
    public List<Sf2Instrument> Instruments { get; } = new();
    /// <summary>Sample headers (the terminal record is dropped).</summary>
    public List<Sf2Sample> Samples { get; } = new();
    /// <summary>Number of 16-bit frames in the smpl chunk.</summary>
    public long SampleFrames => _smplLength / 2;

    /// <summary>Reads one sample's 16-bit frames (a compressed SF3 sample is decoded). Only this range is read from the file.</summary>
    public short[] ReadSampleData(int sampleIndex)
    {
        if ((uint)sampleIndex >= (uint)Samples.Count) throw new Sf2FormatException("AMP5311: sample index out of range");
        var s = Samples[sampleIndex];
        if (s.Compressed) return Decode(sampleIndex);
        if (s.End < s.Start || s.End > SampleFrames) throw new Sf2FormatException("AMP5311: sample range outside the data chunk");
        var count = (int)(s.End - s.Start);
        if (count > 64 * 1024 * 1024) throw new Sf2FormatException("AMP5311: sample too large");
        var bytes = new byte[count * 2];
        using var st = _open();
        st.Seek(_smplOffset + ((long)s.Start * 2), SeekOrigin.Begin);
        st.ReadExactly(bytes);
        var data = new short[count];
        for (var i = 0; i < count; i++) data[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return data;
    }
}

/// <summary>Reads SoundFont 2 (.sf2) RIFF files. Bounds-checked: every size and index is validated before use.</summary>
internal static class Sf2Reader
{
    private const int MaxPdtaChunk = 64 * 1024 * 1024;
    private const int MaxInfoChunk = 1024 * 1024;
    private const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>Opens a file and reads the headers.</summary>
    public static Sf2Bank Read(string path)
    {
        if (!File.Exists(path)) throw new Sf2FormatException("AMP5311: SoundFont file not found");
        if (new FileInfo(path).Length > MaxFileBytes) throw new Sf2FormatException("AMP5311: SoundFont exceeds the 512 MiB size limit");
        Sf2Bank bank = Read(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        bank.SourcePath = path;
        return bank;
    }

    /// <summary>Reads from a bytes array (tests, embedded fonts).</summary>
    public static Sf2Bank Read(byte[] data) => Read(() => new MemoryStream(data, writable: false));

    /// <summary>Reads headers from any re-openable seekable source.</summary>
    public static Sf2Bank Read(Func<Stream> open)
    {
        try
        {
            using var st = open();
            return ReadHeaders(st, open);
        }
        catch (EndOfStreamException) { throw new Sf2FormatException("AMP5311: SoundFont truncated"); }
        catch (IOException) { throw new Sf2FormatException("AMP5311: SoundFont unreadable"); }
        catch (UnauthorizedAccessException) { throw new Sf2FormatException("AMP5311: SoundFont unreadable"); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException) { throw new Sf2FormatException("AMP5311: SoundFont contains invalid bounds or indexes"); }
    }

    private static Sf2Bank ReadHeaders(Stream st, Func<Stream> open)
    {
        var len = st.Length;
        if (len > MaxFileBytes) throw new Sf2FormatException("AMP5311: SoundFont exceeds the 512 MiB size limit");
        var head = new byte[12];
        st.ReadExactly(head);
        if (!Is(head, 0, "RIFF") || !Is(head, 8, "sfbk")) throw new Sf2FormatException("AMP5311: not a SoundFont");
        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (riffEnd < 12 || riffEnd > len) throw new Sf2FormatException("AMP5311: RIFF size is truncated or invalid");

        long smplOff = 0, smplLen = 0, sm24Off = 0, sm24Len = 0;
        string name = "";
        int vmaj = 0, vmin = 0;
        Dictionary<string, byte[]> pdta = new(StringComparer.Ordinal);

        var pos = 12L;
        while (pos + 8 <= riffEnd)
        {
            st.Seek(pos, SeekOrigin.Begin);
            var h = new byte[12];
            var got = st.Read(h, 0, 8);
            if (got < 8) break;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            var body = pos + 8;
            if (body + size > riffEnd) throw new Sf2FormatException("AMP5311: chunk runs past the end of the file");
            if (Is(h, 0, "LIST") && size >= 4)
            {
                st.ReadExactly(h.AsSpan(8, 4));
                var kind = Encoding.ASCII.GetString(h, 8, 4);
                var inner = body + 4;
                var innerEnd = body + size;
                while (inner + 8 <= innerEnd)
                {
                    st.Seek(inner, SeekOrigin.Begin);
                    var ch = new byte[8];
                    st.ReadExactly(ch);
                    var csize = BinaryPrimitives.ReadUInt32LittleEndian(ch.AsSpan(4));
                    var cbody = inner + 8;
                    if (cbody + csize > innerEnd) throw new Sf2FormatException("AMP5311: sub-chunk runs past its list");
                    var id = Encoding.ASCII.GetString(ch, 0, 4);
                    if (kind == "sdta" && id == "smpl") { smplOff = cbody; smplLen = csize; }
                    else if (kind == "sdta" && id == "sm24") { sm24Off = cbody; sm24Len = csize; }
                    else if (kind == "INFO" && id == "ifil" && csize >= 4)
                    {
                        var b = new byte[4]; st.ReadExactly(b);
                        vmaj = BinaryPrimitives.ReadUInt16LittleEndian(b); vmin = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2));
                    }
                    else if (kind == "INFO" && id == "INAM" && csize <= MaxInfoChunk)
                    {
                        var b = new byte[csize]; st.ReadExactly(b);
                        name = Z(b, 0, b.Length);
                    }
                    else if (kind == "pdta")
                    {
                        if (csize > MaxPdtaChunk) throw new Sf2FormatException("AMP5311: chunk too large");
                        var b = new byte[csize]; st.ReadExactly(b);
                        pdta[id] = b;
                    }
                    inner = cbody + csize + (csize & 1);
                }
            }
            pos = body + size + (size & 1);
        }
        if (smplOff == 0 && smplLen == 0 && !pdta.ContainsKey("shdr")) throw new Sf2FormatException("AMP5311: no sample data or headers");
        foreach (var need in new[] { "phdr", "pbag", "pgen", "inst", "ibag", "igen", "shdr" })
            if (!pdta.ContainsKey(need)) throw new Sf2FormatException("AMP5311: missing pdta chunk");

        var bank = new Sf2Bank(open, smplOff, smplLen, sm24Off, sm24Len) { Name = name, VersionMajor = vmaj, VersionMinor = vmin };
        BuildPresets(bank, pdta);
        BuildInstruments(bank, pdta);
        BuildSamples(bank, pdta);
        return bank;
    }

    private static List<T> Records<T>(byte[] data, int size, Func<byte[], int, T> make)
    {
        if (data.Length % size != 0 || data.Length < size) throw new Sf2FormatException("AMP5311: bad record table size");
        var n = data.Length / size;
        var l = new List<T>(n);
        for (var i = 0; i < n; i++) l.Add(make(data, i * size));
        return l;
    }

    private static List<int> Bags(byte[] bagData, byte[] genData, byte[] modData, out List<int> modCounts) => Bags(bagData, genData, modData, out modCounts, out _);

    private static List<int> Bags(byte[] bagData, byte[] genData, byte[] modData, out List<int> modCounts, out List<int> modStarts)
    {
        var bags = Records(bagData, 4, (b, o) => (Gen: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)), Mod: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 2))));
        modCounts = new List<int>();
        modStarts = bags.Select(b => b.Mod).ToList();
        var starts = new List<int>();
        var genCount = genData.Length / 4;
        var modTotal = modData.Length / 10;
        var prev = -1;
        foreach (var b in bags)
        {
            if (b.Gen < prev) throw new Sf2FormatException("AMP5311: generator index not ascending");
            if (b.Gen > genCount || b.Mod > modTotal) throw new Sf2FormatException("AMP5311: bag index out of range");
            prev = b.Gen;
            starts.Add(b.Gen);
        }
        for (var i = 0; i + 1 < bags.Count; i++) modCounts.Add(Math.Max(0, bags[i + 1].Mod - bags[i].Mod));
        return starts;
    }

    private static List<Sf2Mod> ReadMods(byte[] m) => m.Length == 0 ? [] : Records(m, 10, (b, o) => new Sf2Mod(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 2)),
        BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 4)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 8))));

    private static List<Sf2Gen> ReadGens(byte[] g) => Records(g, 4, (b, o) => new Sf2Gen(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)), BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 2))));

    private static void BuildZones(List<Sf2Zone> zones, List<int> starts, List<int> modCounts, List<Sf2Gen> gens, int bagFrom, int bagTo, List<int>? modStarts = null, List<Sf2Mod>? mods = null)
    {
        if (bagFrom < 0 || bagTo < bagFrom || bagTo > starts.Count - 1) throw new Sf2FormatException("AMP5311: bag range out of bounds");
        for (var z = bagFrom; z < bagTo; z++)
        {
            var from = starts[z];
            var to = starts[z + 1];
            if (to < from || to > gens.Count) throw new Sf2FormatException("AMP5311: generator range out of bounds");
            var zone = new Sf2Zone { ModulatorCount = modCounts[z] };
            for (var g = from; g < to; g++) zone.Gens.Add(gens[g]);
            if (modStarts is not null && mods is not null && z + 1 < modStarts.Count)
            {
                for (var m = modStarts[z]; m < modStarts[z + 1] && m < mods.Count; m++) zone.Mods.Add(mods[m]);
            }
            zones.Add(zone);
        }
    }

    private static void BuildPresets(Sf2Bank bank, Dictionary<string, byte[]> p)
    {
        var heads = Records(p["phdr"], 38, (b, o) => (Name: Z(b, o, 20), Prog: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 20)),
            Bank: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 22)), Bag: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 24))));
        var gens = ReadGens(p["pgen"]);
        var starts = Bags(p["pbag"], p["pgen"], p.GetValueOrDefault("pmod") ?? [], out var mods, out var modStarts);
        var modList = ReadMods(p.GetValueOrDefault("pmod") ?? []);
        for (var i = 0; i + 1 < heads.Count; i++)
        {
            var pr = new Sf2Preset { Name = heads[i].Name, Program = heads[i].Prog, Bank = heads[i].Bank };
            BuildZones(pr.Zones, starts, mods, gens, heads[i].Bag, heads[i + 1].Bag, modStarts, modList);
            bank.Presets.Add(pr);
        }
    }

    private static void BuildInstruments(Sf2Bank bank, Dictionary<string, byte[]> p)
    {
        var heads = Records(p["inst"], 22, (b, o) => (Name: Z(b, o, 20), Bag: (int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 20))));
        var gens = ReadGens(p["igen"]);
        var starts = Bags(p["ibag"], p["igen"], p.GetValueOrDefault("imod") ?? [], out var mods, out var modStarts);
        var modList = ReadMods(p.GetValueOrDefault("imod") ?? []);
        for (var i = 0; i + 1 < heads.Count; i++)
        {
            var ins = new Sf2Instrument { Name = heads[i].Name };
            BuildZones(ins.Zones, starts, mods, gens, heads[i].Bag, heads[i + 1].Bag, modStarts, modList);
            bank.Instruments.Add(ins);
        }
    }

    private static void BuildSamples(Sf2Bank bank, Dictionary<string, byte[]> p)
    {
        var all = Records(p["shdr"], 46, (b, o) => new Sf2Sample
        {
            Name = Z(b, o, 20),
            Start = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 20)),
            End = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 24)),
            LoopStart = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 28)),
            LoopEnd = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 32)),
            Rate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 36)),
            OriginalPitch = b[o + 40],
            PitchCorrection = (sbyte)b[o + 41],
            Link = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 42)),
            Type = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 44)),
        });
        for (var i = 0; i + 1 < all.Count; i++)
        {
            Sf2Sample s = all[i];
            if (s.Compressed)
            {
                // SF3: start and end are the bytes of an Ogg Vorbis stream, the loop is in frames from the start of the decoded sample. The frames get virtual positions from
                // the first byte (each sample is only compared with itself); the end is known once the sample is decoded (Sf2Bank.LayOut).
                s.ByteStart = s.Start;
                s.ByteEnd = s.End;
                s.LoopStart = checked(s.Start + s.LoopStart);
                s.LoopEnd = checked(s.Start + s.LoopEnd);
                s.End = s.Start;
                s.LaidOut = false;
            }

            bank.Samples.Add(s);
        }
    }

    private static bool Is(byte[] b, int off, string id) =>
        b[off] == id[0] && b[off + 1] == id[1] && b[off + 2] == id[2] && b[off + 3] == id[3];

    private static string Z(byte[] b, int off, int max)
    {
        var end = off;
        var limit = Math.Min(b.Length, off + max);
        while (end < limit && b[end] != 0) end++;
        var sb = new StringBuilder(end - off);
        for (var i = off; i < end; i++) sb.Append(b[i] is >= 32 and < 127 ? (char)b[i] : '?');
        return sb.ToString();
    }
}
