using System.Buffers.Binary;
using System.Text;

namespace Ample.Core.Tests.Instruments;

/// <summary>Writes tiny synthetic SoundFont 2 files from code (no third-party audio, no licences).</summary>
internal static class Sf2Fixture
{
    /// <summary>Generator operator numbers used by the fixtures.</summary>
    public const ushort StartOffset = 0, KeyRange = 43, VelRange = 44, Instrument = 41, SampleModes = 54, SampleId = 53, OverridingRoot = 58;

    /// <summary>A generator list for one zone.</summary>
    public sealed record Zone(params (ushort Oper, short Amount)[] Gens);

    /// <summary>Builds a two-sample, one-instrument, one-preset font: sample 0 has 100 frames and a loop, sample 1 has 50.</summary>
    public static byte[] Tiny()
    {
        var samples = new[] { (Name: "saw", Frames: 100, Loop: (10u, 90u), Pitch: 60), (Name: "sq", Frames: 50, Loop: (5u, 45u), Pitch: 64) };
        var inst = new[] { ("lead", new[] { new Zone((KeyRange, Range(0, 59)), (SampleId, 0)), new Zone((KeyRange, Range(60, 127)), (VelRange, Range(0, 100)), (SampleId, 1)) }) };
        var presets = new[] { ("Tiny Lead", 5, 0, new[] { new Zone((Instrument, 0)) }) };
        return Build(samples, inst, presets);
    }

    /// <summary>Packs a lo/hi range into a generator amount.</summary>
    public static short Range(int lo, int hi) => (short)(lo | (hi << 8));

    /// <summary>Builds a font from the given samples, instruments and presets.</summary>
    public static byte[] Build((string Name, int Frames, (uint S, uint E) Loop, int Pitch)[] samples,
        (string Name, Zone[] Zones)[] insts, (string Name, int Program, int Bank, Zone[] Zones)[] presets, byte[]? ogg = null, bool sm24 = false)
    {
        var smpl = new MemoryStream();
        var shdr = new MemoryStream();
        uint pos = 0;
        foreach (var s in samples)
        {
            for (var i = 0; i < s.Frames; i++) W16(smpl, (short)((s.Pitch * 100) + i));
            W(shdr, Name20(s.Name)); W32(shdr, pos); W32(shdr, pos + (uint)s.Frames); W32(shdr, pos + s.Loop.S); W32(shdr, pos + s.Loop.E);
            W32(shdr, 44100); shdr.WriteByte((byte)s.Pitch); shdr.WriteByte(0); W16(shdr, 0); W16(shdr, 1);
            pos += (uint)s.Frames + 46; // 46 zero frames of padding after every sample, as the spec asks
            for (var i = 0; i < 46; i++) W16(smpl, 0);
        }
        if (ogg is not null)
        {
            // SF3: the first sample is this Ogg Vorbis stream; start and end are its bytes, the loop is relative to the decoded start, the type says mono and Ogg (0x11)
            var s = samples[0];
            smpl = new MemoryStream(); W(smpl, ogg); if ((ogg.Length & 1) != 0) smpl.WriteByte(0);
            shdr = new MemoryStream();
            W(shdr, Name20(s.Name)); W32(shdr, 0); W32(shdr, (uint)ogg.Length); W32(shdr, s.Loop.S); W32(shdr, s.Loop.E);
            W32(shdr, 22050); shdr.WriteByte((byte)s.Pitch); shdr.WriteByte(0); W16(shdr, 0); W16(shdr, 0x11);
        }

        W(shdr, Name20("EOS")); for (var i = 0; i < 26; i++) shdr.WriteByte(0);

        var (ibag, igen, imod, inst) = Zones(insts.Select(i => (i.Name, i.Zones)).ToArray(), 22);
        var (pbag, pgen, pmod, phdr) = Zones(presets.Select(p => (p.Name, p.Zones)).ToArray(), 38, presets.Select(p => (p.Program, p.Bank)).ToArray());

        var info = Chunks(("ifil", ogg is null ? new byte[] { 2, 0, 1, 0 } : new byte[] { 3, 0, 1, 0 }), ("isng", Encoding.ASCII.GetBytes("EMU8000\0")), ("INAM", Encoding.ASCII.GetBytes("Tiny\0\0\0\0")));
        // sm24: one low byte for every frame of smpl, (frame * 37) mod 256, so a test can tell the low bits apart
        var sdta = sm24 ? Chunks(("smpl", smpl.ToArray()), ("sm24", Enumerable.Range(0, (int)(smpl.Length / 2)).Select(i => (byte)(i * 37)).ToArray())) : Chunks(("smpl", smpl.ToArray()));
        var pdta = Chunks(("phdr", phdr), ("pbag", pbag), ("pmod", pmod), ("pgen", pgen), ("inst", inst), ("ibag", ibag), ("imod", imod), ("igen", igen), ("shdr", shdr.ToArray()));
        var body = new MemoryStream();
        W(body, Encoding.ASCII.GetBytes("sfbk"));
        List(body, "INFO", info); List(body, "sdta", sdta); List(body, "pdta", pdta);
        var file = new MemoryStream();
        W(file, Encoding.ASCII.GetBytes("RIFF")); W32(file, (uint)body.Length); W(file, body.ToArray());
        return file.ToArray();
    }

    private static (byte[] Bag, byte[] Gen, byte[] Mod, byte[] Head) Zones((string Name, Zone[] Zones)[] items, int headSize, (int Program, int Bank)[]? pb = null)
    {
        var bag = new MemoryStream(); var gen = new MemoryStream(); var head = new MemoryStream();
        uint bagCount = 0, genCount = 0;
        for (var n = 0; n < items.Length; n++)
        {
            W(head, Name20(items[n].Name));
            if (headSize == 38) { W16(head, (short)pb![n].Program); W16(head, (short)pb[n].Bank); }
            W16(head, (short)bagCount);
            if (headSize == 38) for (var i = 0; i < 12; i++) head.WriteByte(0);
            foreach (var z in items[n].Zones)
            {
                W16(bag, (short)genCount); W16(bag, 0);
                bagCount++;
                foreach (var (o, a) in z.Gens) { W16(gen, (short)o); W16(gen, a); genCount++; }
            }
        }
        W(head, Name20("EOx"));
        if (headSize == 38) { W16(head, 0); W16(head, 0); }
        W16(head, (short)bagCount);
        if (headSize == 38) for (var i = 0; i < 12; i++) head.WriteByte(0);
        W16(bag, (short)genCount); W16(bag, 0);
        W16(gen, 0); W16(gen, 0);
        return (bag.ToArray(), gen.ToArray(), new byte[10], head.ToArray());
    }

    private static byte[] Chunks(params (string Id, byte[] Data)[] chunks)
    {
        var m = new MemoryStream();
        foreach (var (id, d) in chunks) { W(m, Encoding.ASCII.GetBytes(id)); W32(m, (uint)d.Length); W(m, d); if ((d.Length & 1) != 0) m.WriteByte(0); }
        return m.ToArray();
    }

    private static void List(Stream s, string kind, byte[] inner) { W(s, Encoding.ASCII.GetBytes("LIST")); W32(s, (uint)(inner.Length + 4)); W(s, Encoding.ASCII.GetBytes(kind)); W(s, inner); }
    private static byte[] Name20(string n) { var b = new byte[20]; Encoding.ASCII.GetBytes(n, 0, Math.Min(19, n.Length), b, 0); return b; }
    private static void W(Stream s, byte[] b) => s.Write(b, 0, b.Length);
    private static void W16(Stream s, short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); s.Write(b); }
    private static void W32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
}
