namespace Ample.Speech.Flite.Data;

/// <summary>Reads one classification tree from its bytes in a Flite data file: the node rows, the pool of the string values and the names of the features.
/// The bytes are written by tools/flite-data (<c>Extract.CartBytes</c>).</summary>
internal static class FliteCartCodec
{
    /// <summary>Decodes the tree <paramref name="name"/> from the bytes of one section or one length-prefixed blob.</summary>
    public static FliteCart Read(string name, ReadOnlyMemory<byte> bytes)
    {
        FliteBinReader reader = new(bytes);
        FliteCartNode[] nodes = new FliteCartNode[reader.Int32()];
        (byte Feature, byte Operator, ushort No, byte Kind, uint Payload)[] raw = new (byte, byte, ushort, byte, uint)[nodes.Length];
        for (int i = 0; i < raw.Length; i++) raw[i] = (reader.Byte(), reader.Byte(), reader.UInt16(), reader.Byte(), reader.UInt32());
        string[] pool = reader.TextList();
        string[] features = reader.TextList();
        for (int i = 0; i < raw.Length; i++)
        {
            (byte feature, byte op, ushort no, byte kind, uint payload) = raw[i];
            nodes[i] = kind switch
            {
                1 => new FliteCartNode(feature, op, no, FliteCartValueKind.Text, pool[(int)payload], 0f),
                2 => new FliteCartNode(feature, op, no, FliteCartValueKind.Number, null, BitConverter.UInt32BitsToSingle(payload)),
                _ => new FliteCartNode(feature, op, no, FliteCartValueKind.None, null, 0f)
            };
        }

        return new FliteCart(name, nodes, features);
    }
}
