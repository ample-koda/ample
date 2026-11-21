namespace Ample.Speech.Flite.Data;

/// <summary>The CMU lexicon as Flite packs it: a binary-searchable blob of Huffman-coded entries (each entry: the coded word with its part-of-speech letter, a byte 255, the coded phones, a byte 0), the two Huffman tables, the phone table and the addenda that are looked up first. Immutable; one shared instance.</summary>
public sealed class FliteLexiconData
{
    /// <summary>Slots of a Huffman table in the C data (slot 0 is reserved, the entries follow, the rest is missing).</summary>
    public const int HuffmanSlots = 257;

    private static readonly Lazy<FliteLexiconData> Shared = new(() => new FliteLexiconData(FliteBin.Open("flite-cmu-lexicon.bin")), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly string[] phones;
    private readonly byte[]?[] phoneHuffman = new byte[]?[HuffmanSlots];
    private readonly byte[]?[] entryHuffman = new byte[]?[HuffmanSlots];
    private readonly string[][] addenda;

    private FliteLexiconData(FliteBin file)
    {
        Data = file.Section("data");
        FliteBinReader info = file.Reader("info");
        NumEntries = info.Int32();
        NumBytes = info.Int32();
        phones = file.Reader("phones").TextList();
        FillHuffman(file.Reader("phone-huff"), phoneHuffman);
        FillHuffman(file.Reader("entry-huff"), entryHuffman);
        FliteBinReader lists = file.Reader("addenda");
        addenda = new string[lists.Int32()][];
        for (int i = 0; i < addenda.Length; i++) addenda[i] = lists.TextList();
    }

    /// <summary>The shared instance, read from the embedded data on first use.</summary>
    public static FliteLexiconData Instance => Shared.Value;

    /// <summary>The coded entries, starting with the byte 0 that Flite puts in front (<see cref="NumBytes"/> bytes).</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The number of entries (<c>cmu_lex_num_entries</c>).</summary>
    public int NumEntries { get; }

    /// <summary>The number of bytes of <see cref="Data"/>.</summary>
    public int NumBytes { get; }

    /// <summary>The phone names a coded phone byte stands for (the Huffman codes decode to indexes into this table, plus one).</summary>
    public IReadOnlyList<string> PhoneTable => phones;

    /// <summary>The Huffman table of the phones, <see cref="HuffmanSlots"/> slots; a slot is the bytes of a decoded string or null.</summary>
    public IReadOnlyList<byte[]?> PhoneHuffman => phoneHuffman;

    /// <summary>The Huffman table of the words (<see cref="HuffmanSlots"/> slots).</summary>
    public IReadOnlyList<byte[]?> EntryHuffman => entryHuffman;

    /// <summary>The addenda: each list is the key (a part-of-speech letter and the word) followed by its phones.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Addenda => addenda;

    private static void FillHuffman(FliteBinReader reader, byte[]?[] table)
    {
        int count = reader.Int32();
        for (int i = 0; i < count; i++) table[i + 1] = reader.Bytes();   // slot 0 stays reserved
    }
}
