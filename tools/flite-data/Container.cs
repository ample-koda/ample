using System.Text;

namespace FliteData;

/// <summary>One section of a .bin file: a little-endian byte stream with the helpers the loaders read back.</summary>
internal sealed class SectionWriter
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter writer;

    public SectionWriter() => writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

    public void Int(int value) => writer.Write(value);

    public void UInt(uint value) => writer.Write(value);

    public void UShort(ushort value) => writer.Write(value);

    public void Byte(byte value) => writer.Write(value);

    public void Float(float value) => writer.Write(value);

    public void Raw(byte[] bytes) => writer.Write(bytes);

    /// <summary>A byte string: a 32-bit length (-1 for a missing string) and the bytes.</summary>
    public void Bytes(byte[]? bytes)
    {
        writer.Write(bytes?.Length ?? -1);
        if (bytes is not null) writer.Write(bytes);
    }

    public void Text(string? text) => Bytes(text is null ? null : Encoding.Latin1.GetBytes(text));

    public void TextList(IReadOnlyCollection<string> items)
    {
        Int(items.Count);
        foreach (string item in items) Text(item);
    }

    public byte[] ToArray()
    {
        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>The FLT1 container: the magic, the number of sections, a directory (name, length, absolute offset), then the section bytes.</summary>
internal static class Container
{
    public static byte[] Write(IReadOnlyList<KeyValuePair<string, byte[]>> sections)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("FLT1"u8.ToArray());
        writer.Write(sections.Count);
        int directory = 8;
        foreach (KeyValuePair<string, byte[]> section in sections) directory += 2 + Encoding.ASCII.GetByteCount(section.Key) + 8;
        int offset = directory;
        foreach (KeyValuePair<string, byte[]> section in sections)
        {
            byte[] name = Encoding.ASCII.GetBytes(section.Key);
            writer.Write((ushort)name.Length);
            writer.Write(name);
            writer.Write(section.Value.Length);
            writer.Write(offset);
            offset += section.Value.Length;
        }

        foreach (KeyValuePair<string, byte[]> section in sections) writer.Write(section.Value);
        writer.Flush();
        return stream.ToArray();
    }
}
