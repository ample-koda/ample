using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace Ample.Speech.Flite.Data;

/// <summary>One FLT1 data file of the Flite engine: the magic, the number of sections, a directory (name, length, absolute offset) and the section bytes. Written by tools/flite-data, read here once and kept immutable.</summary>
internal sealed class FliteBin
{
    private readonly byte[] bytes;
    private readonly Dictionary<string, (int Offset, int Length)> sections = new(StringComparer.Ordinal);

    private FliteBin(byte[] bytes)
    {
        this.bytes = bytes;
        if (bytes.Length < 8 || bytes[0] != 'F' || bytes[1] != 'L' || bytes[2] != 'T' || bytes[3] != '1') throw new InvalidDataException("Not a Flite data file.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int at = 8;
        for (int i = 0; i < count; i++)
        {
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
            string name = Encoding.ASCII.GetString(bytes, at + 2, nameLength);
            at += 2 + nameLength;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            at += 8;
            if (offset < 0 || length < 0 || offset + length > bytes.Length) throw new InvalidDataException($"The section '{name}' lies outside the file.");
            sections[name] = (offset, length);
        }
    }

    /// <summary>Reads the embedded resource <paramref name="fileName"/> of this assembly.</summary>
    public static FliteBin Open(string fileName)
    {
        Assembly assembly = typeof(FliteBin).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("Ample.Speech.Flite.Data." + fileName)
            ?? throw new InvalidOperationException($"The Flite data file '{fileName}' is not part of the assembly.");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return new FliteBin(bytes);
    }

    /// <summary>Reads a data file that is given as bytes (the clustergen voices are read from a file, they are not embedded in the assembly).</summary>
    public static FliteBin FromBytes(byte[] bytes) => new(bytes);

    public IEnumerable<string> Names => sections.Keys;

    public ReadOnlyMemory<byte> Section(string name) => sections.TryGetValue(name, out (int Offset, int Length) at)
        ? new ReadOnlyMemory<byte>(bytes, at.Offset, at.Length)
        : throw new InvalidDataException($"The Flite data file has no section '{name}'.");

    public FliteBinReader Reader(string name) => new(Section(name));
}

/// <summary>A cursor over the bytes of one section (little endian).</summary>
internal sealed class FliteBinReader(ReadOnlyMemory<byte> data)
{
    private int position;

    public bool AtEnd => position >= data.Length;

    public int Int32()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(data.Span[position..]);
        position += 4;
        return value;
    }

    public uint UInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[position..]);
        position += 4;
        return value;
    }

    public ushort UInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.Span[position..]);
        position += 2;
        return value;
    }

    public byte Byte() => data.Span[position++];

    public float Single()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(data.Span[position..]);
        position += 4;
        return value;
    }

    /// <summary>A byte string (32-bit length, -1 for a missing one) or null.</summary>
    public byte[]? BytesOrNull()
    {
        int length = Int32();
        if (length < 0) return null;
        byte[] value = data.Span.Slice(position, length).ToArray();
        position += length;
        return value;
    }

    public byte[] Bytes() => BytesOrNull() ?? [];

    public string? TextOrNull() => BytesOrNull() is { } bytes ? Encoding.Latin1.GetString(bytes) : null;

    public string Text() => TextOrNull() ?? string.Empty;

    public string[] TextList()
    {
        string[] items = new string[Int32()];
        for (int i = 0; i < items.Length; i++) items[i] = Text();
        return items;
    }
}
