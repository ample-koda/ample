using System.Buffers.Binary;
using System.Text;
using Ample.Core.Abstractions;

namespace Ample.Audio.Instruments;

/// <summary>
/// Makes a smaller SoundFont 2 file out of a big one: only the presets that were asked for, the instruments they use and the samples those use. Every record that is kept is copied
/// as it is (zones, generators, loops, sample rates); only the numbers that point at other records are moved to their new places, and the sample positions follow the new data.
/// What the engine reads from the result for those presets is therefore the same as from the original.
/// </summary>
public static class Sf2Subset
{
    private const int PhdrSize = 38, BagSize = 4, ModSize = 10, GenSize = 4, InstSize = 22, ShdrSize = 46;
    private const int PadFrames = 46;

    /// <summary>The facts about a subset that was made.</summary>
    /// <param name="Presets">The presets that were kept, as <c>bank:program name</c>.</param>
    /// <param name="Instruments">How many instruments were kept.</param>
    /// <param name="Samples">How many samples were kept.</param>
    /// <param name="OriginalBytes">The size of the original file.</param>
    /// <param name="SubsetBytes">The size of the new file.</param>
    public sealed record Result(IReadOnlyList<string> Presets, int Instruments, int Samples, long OriginalBytes, long SubsetBytes);

    /// <summary>Writes the subset of <paramref name="sourcePath"/> with the given (bank, program) presets to <paramref name="outputPath"/>.</summary>
    /// <exception cref="AmpleException">AMP5311 when the SoundFont cannot be read or a preset is not in it.</exception>
    public static Result Write(string sourcePath, IReadOnlyCollection<(int Bank, int Program)> wanted, string outputPath, string provenance)
    {
        byte[] file = File.ReadAllBytes(sourcePath);
        Raw raw = ReadRaw(file);
        int presetCount = raw.Phdr.Length / PhdrSize - 1;
        List<int> keptPresets = [];
        foreach ((int bank, int program) in wanted.OrderBy(x => x.Bank).ThenBy(x => x.Program))
        {
            int found = Enumerable.Range(0, presetCount).FirstOrDefault(i => U16(raw.Phdr, i * PhdrSize + 22) == bank && U16(raw.Phdr, i * PhdrSize + 20) == program, -1);
            if (found < 0)
            {
                throw new AmpleException("AMP5311", $"The SoundFont has no preset bank {bank}, program {program}.");
            }

            keptPresets.Add(found);
        }

        keptPresets = [.. keptPresets.Distinct().Order()];
        int pbagCount = raw.Pbag.Length / BagSize, pgenCount = raw.Pgen.Length / GenSize, instCount = raw.Inst.Length / InstSize - 1, ibagCount = raw.Ibag.Length / BagSize, igenCount = raw.Igen.Length / GenSize, sampleCount = raw.Shdr.Length / ShdrSize - 1;

        // presets -> instruments
        SortedSet<int> keptInstruments = [];
        foreach (int p in keptPresets)
        {
            (int bagFrom, int bagTo) = (U16(raw.Phdr, p * PhdrSize + 24), U16(raw.Phdr, (p + 1) * PhdrSize + 24));
            for (int b = bagFrom; b < bagTo; b++)
            {
                (int genFrom, int genTo) = (U16(raw.Pbag, b * BagSize), U16(raw.Pbag, (b + 1) * BagSize));
                for (int g = genFrom; g < genTo; g++)
                {
                    if (U16(raw.Pgen, g * GenSize) == 41)
                    {
                        keptInstruments.Add(U16(raw.Pgen, (g * GenSize) + 2));
                    }
                }
            }
        }

        // instruments -> samples (a stereo sample brings the sample it is linked to)
        SortedSet<int> keptSamples = [];
        foreach (int i in keptInstruments)
        {
            (int bagFrom, int bagTo) = (U16(raw.Inst, (i * InstSize) + 20), U16(raw.Inst, ((i + 1) * InstSize) + 20));
            for (int b = bagFrom; b < bagTo; b++)
            {
                (int genFrom, int genTo) = (U16(raw.Ibag, b * BagSize), U16(raw.Ibag, (b + 1) * BagSize));
                for (int g = genFrom; g < genTo; g++)
                {
                    if (U16(raw.Igen, g * GenSize) == 53)
                    {
                        keptSamples.Add(U16(raw.Igen, (g * GenSize) + 2));
                    }
                }
            }
        }

        Queue<int> pending = new(keptSamples);
        while (pending.Count > 0)
        {
            int s = pending.Dequeue();
            int type = U16(raw.Shdr, (s * ShdrSize) + 44);
            int link = U16(raw.Shdr, (s * ShdrSize) + 42);
            if ((type & 0xE) != 0 && link < sampleCount && keptSamples.Add(link))
            {
                pending.Enqueue(link);
            }
        }

        if (keptSamples.Any(s => s >= sampleCount) || keptInstruments.Any(i => i >= instCount))
        {
            throw new AmpleException("AMP5311", "The SoundFont points at an instrument or sample that does not exist.");
        }

        Dictionary<int, int> instMap = keptInstruments.Select((old, n) => (old, n)).ToDictionary(x => x.old, x => x.n);
        Dictionary<int, int> sampleMap = keptSamples.Select((old, n) => (old, n)).ToDictionary(x => x.old, x => x.n);

        // the new sample data and the new sample headers
        using MemoryStream smpl = new();
        List<byte[]> shdr = [];
        foreach (int old in keptSamples)
        {
            byte[] h = raw.Shdr.AsSpan(old * ShdrSize, ShdrSize).ToArray();
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20)), end = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24));
            uint loopStart = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(28)), loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(32));
            long frames = raw.SmplLength / 2;
            if (end < start || end > frames)
            {
                throw new AmpleException("AMP5311", "A sample of the SoundFont lies outside its data.");
            }

            uint newStart = (uint)(smpl.Length / 2);
            smpl.Write(raw.Smpl, (int)(start * 2), (int)((end - start) * 2));
            smpl.Write(new byte[PadFrames * 2]);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), newStart);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), newStart + (end - start));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), unchecked(newStart + (loopStart - start)));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(32), unchecked(newStart + (loopEnd - start)));
            int link = U16(h, 42);
            if ((U16(h, 44) & 0xE) != 0 && sampleMap.TryGetValue(link, out int newLink))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(42), (ushort)newLink);
            }

            shdr.Add(h);
        }

        // the instruments, their zones and generators
        MemoryStream inst = new(), ibag = new(), igen = new(), imod = new();
        int ibagTotal = 0, igenTotal = 0, imodTotal = 0;
        foreach (int i in keptInstruments)
        {
            byte[] head = raw.Inst.AsSpan(i * InstSize, InstSize).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(20), (ushort)ibagTotal);
            inst.Write(head);
            (int bagFrom, int bagTo) = (U16(raw.Inst, (i * InstSize) + 20), U16(raw.Inst, ((i + 1) * InstSize) + 20));
            for (int b = bagFrom; b < bagTo; b++)
            {
                (int genFrom, int genTo) = (U16(raw.Ibag, b * BagSize), U16(raw.Ibag, (b + 1) * BagSize));
                (int modFrom, int modTo) = (U16(raw.Ibag, (b * BagSize) + 2), U16(raw.Ibag, ((b + 1) * BagSize) + 2));
                WriteU16(ibag, igenTotal);
                WriteU16(ibag, imodTotal);
                for (int g = genFrom; g < genTo; g++)
                {
                    byte[] gen = raw.Igen.AsSpan(g * GenSize, GenSize).ToArray();
                    if (U16(gen, 0) == 53)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(gen.AsSpan(2), (ushort)sampleMap[U16(gen, 2)]);
                    }

                    igen.Write(gen);
                    igenTotal++;
                }

                for (int m = modFrom; m < modTo; m++)
                {
                    imod.Write(raw.Imod.AsSpan(m * ModSize, ModSize));
                    imodTotal++;
                }

                ibagTotal++;
            }
        }

        byte[] eoi = new byte[InstSize];
        Encoding.ASCII.GetBytes("EOI").CopyTo(eoi, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eoi.AsSpan(20), (ushort)ibagTotal);
        inst.Write(eoi);
        WriteU16(ibag, igenTotal);
        WriteU16(ibag, imodTotal);
        igen.Write(new byte[GenSize]);
        imod.Write(new byte[ModSize]);

        // the presets
        MemoryStream phdr = new(), pbag = new(), pgen = new(), pmod = new();
        int pbagTotal = 0, pgenTotal = 0, pmodTotal = 0;
        foreach (int p in keptPresets)
        {
            byte[] head = raw.Phdr.AsSpan(p * PhdrSize, PhdrSize).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(24), (ushort)pbagTotal);
            phdr.Write(head);
            (int bagFrom, int bagTo) = (U16(raw.Phdr, (p * PhdrSize) + 24), U16(raw.Phdr, ((p + 1) * PhdrSize) + 24));
            for (int b = bagFrom; b < bagTo; b++)
            {
                (int genFrom, int genTo) = (U16(raw.Pbag, b * BagSize), U16(raw.Pbag, (b + 1) * BagSize));
                (int modFrom, int modTo) = (U16(raw.Pbag, (b * BagSize) + 2), U16(raw.Pbag, ((b + 1) * BagSize) + 2));
                WriteU16(pbag, pgenTotal);
                WriteU16(pbag, pmodTotal);
                for (int g = genFrom; g < genTo; g++)
                {
                    byte[] gen = raw.Pgen.AsSpan(g * GenSize, GenSize).ToArray();
                    if (U16(gen, 0) == 41)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(gen.AsSpan(2), (ushort)instMap[U16(gen, 2)]);
                    }

                    pgen.Write(gen);
                    pgenTotal++;
                }

                for (int m = modFrom; m < modTo; m++)
                {
                    pmod.Write(raw.Pmod.AsSpan(m * ModSize, ModSize));
                    pmodTotal++;
                }

                pbagTotal++;
            }
        }

        byte[] eop = new byte[PhdrSize];
        Encoding.ASCII.GetBytes("EOP").CopyTo(eop, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eop.AsSpan(24), (ushort)pbagTotal);
        phdr.Write(eop);
        WriteU16(pbag, pgenTotal);
        WriteU16(pbag, pmodTotal);
        pgen.Write(new byte[GenSize]);
        pmod.Write(new byte[ModSize]);
        MemoryStream shdrStream = new();
        foreach (byte[] h in shdr)
        {
            shdrStream.Write(h);
        }

        byte[] eos = new byte[ShdrSize];
        Encoding.ASCII.GetBytes("EOS").CopyTo(eos, 0);
        shdrStream.Write(eos);

        // the RIFF
        MemoryStream info = new();
        Chunk(info, "ifil", raw.Ifil.Length == 4 ? raw.Ifil : [2, 0, 1, 0]);
        Chunk(info, "isng", Z("EMU8000"));
        Chunk(info, "INAM", Z((raw.Name.Length > 0 ? raw.Name : "SoundFont") + " (subset)"));
        Chunk(info, "ICMT", Z(provenance));
        MemoryStream sdta = new();
        Chunk(sdta, "smpl", smpl.ToArray());
        MemoryStream pdta = new();
        Chunk(pdta, "phdr", phdr.ToArray());
        Chunk(pdta, "pbag", pbag.ToArray());
        Chunk(pdta, "pmod", pmod.ToArray());
        Chunk(pdta, "pgen", pgen.ToArray());
        Chunk(pdta, "inst", inst.ToArray());
        Chunk(pdta, "ibag", ibag.ToArray());
        Chunk(pdta, "imod", imod.ToArray());
        Chunk(pdta, "igen", igen.ToArray());
        Chunk(pdta, "shdr", shdrStream.ToArray());
        MemoryStream body = new();
        body.Write(Encoding.ASCII.GetBytes("sfbk"));
        List(body, "INFO", info.ToArray());
        List(body, "sdta", sdta.ToArray());
        List(body, "pdta", pdta.ToArray());
        using (FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(Encoding.ASCII.GetBytes("RIFF"));
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            output.Write(size);
            body.Position = 0;
            body.CopyTo(output);
        }

        List<string> names = [.. keptPresets.Select(p => $"{U16(raw.Phdr, (p * PhdrSize) + 22)}:{U16(raw.Phdr, (p * PhdrSize) + 20)} {Name(raw.Phdr, p * PhdrSize)}")];
        return new Result(names, keptInstruments.Count, keptSamples.Count, file.LongLength, new FileInfo(outputPath).Length);
    }

    private sealed record Raw(byte[] Phdr, byte[] Pbag, byte[] Pmod, byte[] Pgen, byte[] Inst, byte[] Ibag, byte[] Imod, byte[] Igen, byte[] Shdr, byte[] Smpl, long SmplLength, byte[] Ifil, string Name);

    private static Raw ReadRaw(byte[] f)
    {
        if (f.Length < 12 || Encoding.ASCII.GetString(f, 0, 4) != "RIFF" || Encoding.ASCII.GetString(f, 8, 4) != "sfbk")
        {
            throw new AmpleException("AMP5311", "This is not a SoundFont file.");
        }

        Dictionary<string, byte[]> chunks = new(StringComparer.Ordinal);
        string name = string.Empty;
        byte[] smpl = [];
        long smplLength = 0;
        int pos = 12;
        int riffEnd = Math.Min(f.Length, 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(4)));
        while (pos + 12 <= riffEnd)
        {
            string id = Encoding.ASCII.GetString(f, pos, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(pos + 4));
            if (id == "LIST" && size >= 4)
            {
                string kind = Encoding.ASCII.GetString(f, pos + 8, 4);
                int inner = pos + 12, end = pos + 8 + size;
                while (inner + 8 <= end)
                {
                    string cid = Encoding.ASCII.GetString(f, inner, 4);
                    int csize = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(inner + 4));
                    if (inner + 8 + csize > end)
                    {
                        throw new AmpleException("AMP5311", "A chunk of the SoundFont runs past its list.");
                    }

                    if (kind == "sdta" && cid == "smpl")
                    {
                        smpl = f;
                        smplLength = csize;
                        chunks["smplOffset"] = BitConverter.GetBytes(inner + 8);
                    }
                    else if (kind == "pdta" || (kind == "INFO" && cid is "ifil" or "INAM"))
                    {
                        chunks[cid] = f.AsSpan(inner + 8, csize).ToArray();
                    }

                    inner += 8 + csize + (csize & 1);
                }
            }

            pos += 8 + size + (size & 1);
        }

        foreach (string need in new[] { "phdr", "pbag", "pgen", "inst", "ibag", "igen", "shdr", "smplOffset" })
        {
            if (!chunks.ContainsKey(need))
            {
                throw new AmpleException("AMP5311", "The SoundFont misses a table (" + need + ").");
            }
        }

        if (chunks.TryGetValue("INAM", out byte[]? inam))
        {
            int zero = Array.IndexOf(inam, (byte)0);
            name = Encoding.ASCII.GetString(inam, 0, zero < 0 ? inam.Length : zero);
        }

        int offset = BitConverter.ToInt32(chunks["smplOffset"]);
        byte[] data = f.AsSpan(offset, (int)smplLength).ToArray();
        return new Raw(chunks["phdr"], chunks["pbag"], chunks.GetValueOrDefault("pmod") ?? new byte[ModSize], chunks["pgen"], chunks["inst"], chunks["ibag"], chunks.GetValueOrDefault("imod") ?? new byte[ModSize], chunks["igen"], chunks["shdr"], data, smplLength, chunks.GetValueOrDefault("ifil") ?? [], name);
    }

    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));

    private static void WriteU16(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
        s.Write(b);
    }

    private static string Name(byte[] b, int at)
    {
        int end = at;
        while (end < at + 20 && b[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(b, at, end - at);
    }

    private static byte[] Z(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text.Length > 250 ? text[..250] : text);
        byte[] result = new byte[(bytes.Length + 2) & ~1];
        bytes.CopyTo(result, 0);
        return result;
    }

    private static void Chunk(Stream s, string id, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        s.Write(size);
        s.Write(data);
        if ((data.Length & 1) == 1)
        {
            s.WriteByte(0);
        }
    }

    private static void List(Stream s, string kind, byte[] inner)
    {
        s.Write(Encoding.ASCII.GetBytes("LIST"));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(inner.Length + 4));
        s.Write(size);
        s.Write(Encoding.ASCII.GetBytes(kind));
        s.Write(inner);
    }
}
