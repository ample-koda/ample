namespace Ample.Speech.Flite.Data;

/// <summary>One row of the letter-to-sound decision trees (Flite <c>cst_lts_rule</c>): the feature and letter to test, the row to go to when true and when false. A leaf has the feature 255 and the phone index in <paramref name="Value"/>.</summary>
/// <param name="Feature">The window position to test, or 255 for a leaf.</param>
/// <param name="Value">The letter to compare with, or the phone index of a leaf.</param>
/// <param name="True">The row after a true test.</param>
/// <param name="False">The row after a false test.</param>
public readonly record struct FliteLtsRule(byte Feature, byte Value, ushort True, ushort False);

/// <summary>The letter-to-sound rules of the CMU lexicon: one decision tree per letter, in rows of six bytes, with the first row of each letter, the phone table and the letter table. Immutable; one shared instance.</summary>
public sealed class FliteLtsData
{
    private static readonly Lazy<FliteLtsData> Shared = new(() => new FliteLtsData(FliteBin.Open("flite-cmu-lts.bin")), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ushort[] letterIndex;
    private readonly string[] phones;
    private readonly string[] letters;

    private FliteLtsData(FliteBin file)
    {
        FliteBinReader info = file.Reader("info");
        RowCount = info.Int32();
        ContextWindowSize = info.Int32();
        ContextExtraFeatures = info.Int32();
        Model = file.Section("model");
        FliteBinReader index = file.Reader("letter-index");
        letterIndex = new ushort[index.Int32()];
        for (int i = 0; i < letterIndex.Length; i++) letterIndex[i] = index.UInt16();
        phones = file.Reader("phones").TextList();
        letters = file.Reader("letters").TextList();
    }

    /// <summary>The shared instance, read from the embedded data on first use.</summary>
    public static FliteLtsData Instance => Shared.Value;

    /// <summary>The rows of the trees as six bytes each: feature, letter, true address (16 bit), false address (16 bit); the last row is all zero.</summary>
    public ReadOnlyMemory<byte> Model { get; }

    /// <summary>The number of rows, the closing zero row included.</summary>
    public int RowCount { get; }

    /// <summary>The number of letters on each side of the letter that is looked at (<c>context_window_size</c>).</summary>
    public int ContextWindowSize { get; }

    /// <summary>The extra features the rules look at after the letters (<c>context_extra_feats</c>).</summary>
    public int ContextExtraFeatures { get; }

    /// <summary>The first row of the tree of each letter <c>a</c> to <c>z</c> (27 values, the last is a closing zero).</summary>
    public IReadOnlyList<ushort> LetterIndex => letterIndex;

    /// <summary>The phones a leaf can name (a leaf value is an index into this table, which can hold two phones joined by a dash).</summary>
    public IReadOnlyList<string> PhoneTable => phones;

    /// <summary>The letters of the table (<c>cmu_lts_letter_table</c>).</summary>
    public IReadOnlyList<string> LetterTable => letters;

    /// <summary>The row at this index.</summary>
    public FliteLtsRule Rule(int row)
    {
        ReadOnlySpan<byte> bytes = Model.Span.Slice(row * 6, 6);
        return new FliteLtsRule(bytes[0], bytes[1], (ushort)(bytes[2] | (bytes[3] << 8)), (ushort)(bytes[4] | (bytes[5] << 8)));
    }
}
