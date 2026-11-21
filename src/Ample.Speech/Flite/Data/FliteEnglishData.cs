namespace Ample.Speech.Flite.Data;

/// <summary>What a CART node holds as its value: nothing, a string or a number.</summary>
public enum FliteCartValueKind : byte
{
    /// <summary>No value (the closing node of the table).</summary>
    None = 0,

    /// <summary>A string value.</summary>
    Text = 1,

    /// <summary>A 32-bit float value.</summary>
    Number = 2,
}

/// <summary>One row of a classification tree (Flite <c>cst_cart_node</c>). The yes branch is always the next row; <paramref name="NoNode"/> is the row of the no branch.
/// A leaf has the feature and the operator 255, and its value is the answer.</summary>
/// <param name="Feature">The index into the feature names of the tree, or 255 for a leaf.</param>
/// <param name="Operator">0 is, 1 in, 2 less, 3 greater, 4 matches, 5 equals, 255 none (a leaf).</param>
/// <param name="NoNode">The row of the no branch.</param>
/// <param name="Kind">What the value is.</param>
/// <param name="Text">The string value, when <paramref name="Kind"/> is <see cref="FliteCartValueKind.Text"/>.</param>
/// <param name="Number">The float value, when <paramref name="Kind"/> is <see cref="FliteCartValueKind.Number"/>.</param>
public readonly record struct FliteCartNode(byte Feature, byte Operator, ushort NoNode, FliteCartValueKind Kind, string? Text, float Number);

/// <summary>A classification tree as data: the rows and the names of the features they test. The interpreter lives elsewhere.</summary>
/// <param name="Name">The Flite name of the tree, for example <c>us_durz_cart</c>.</param>
/// <param name="Nodes">The rows, ending with a node that has the feature 255 and no value.</param>
/// <param name="Features">The feature names (paths such as <c>R:SylStructure.parent.R:Syllable.p.stress</c>).</param>
public sealed record FliteCart(string Name, IReadOnlyList<FliteCartNode> Nodes, IReadOnlyList<string> Features);

/// <summary>The phone feature table of the US English phone set (Flite <c>cst_phoneset</c>).</summary>
/// <param name="FeatureNames">The feature names (vc, vlng, vheight, ...).</param>
/// <param name="PhoneNames">The phone names, in the order of <paramref name="Rows"/>.</param>
/// <param name="FeatureValues">The distinct feature values; a row holds indexes into this list.</param>
/// <param name="Rows">Per phone the value index of every feature, followed by -1 as in the C table.</param>
/// <param name="Silence">The name of the silence phone.</param>
/// <param name="NumPhones">The number of phones the table states.</param>
public sealed record FlitePhoneset(IReadOnlyList<string> FeatureNames, IReadOnlyList<string> PhoneNames, IReadOnlyList<string> FeatureValues, IReadOnlyList<IReadOnlyList<int>> Rows, string Silence, int NumPhones);

/// <summary>One term of the linear regression F0 model (Flite <c>us_f0_lr_term</c>).</summary>
/// <param name="Feature">The feature of the term, or null.</param>
/// <param name="Start">The weight at the start of the syllable.</param>
/// <param name="Mid">The weight in the middle.</param>
/// <param name="End">The weight at the end.</param>
/// <param name="Type">The value that switches the term on, or null for a numeric feature.</param>
public sealed record FliteF0Term(string? Feature, float Start, float Mid, float End, string? Type);

/// <summary>The mean and the standard deviation of the duration of a phone (Flite <c>dur_stat</c>).</summary>
/// <param name="Phone">The phone name.</param>
/// <param name="Mean">The mean duration in seconds.</param>
/// <param name="Stddev">The standard deviation in seconds.</param>
public sealed record FliteDurationStat(string Phone, float Mean, float Stddev);

/// <summary>A list of closed-class words of the part-of-speech guesser.</summary>
/// <param name="Name">The C name of the list, such as <c>gpos_in_list</c>.</param>
/// <param name="Words">The words, in the order of the C list.</param>
public sealed record FliteWordList(string Name, IReadOnlyList<string> Words);

/// <summary>A compiled regular expression of the text analysis: the pattern as it was written, and the program of Henry Spencer's engine as compiled for Flite.</summary>
/// <param name="Name">The Flite name, for example <c>usmoney</c>.</param>
/// <param name="Pattern">The pattern in the POSIX basic syntax of the source (<c>\(</c> and <c>\|</c> group and alternate).</param>
/// <param name="RegexStart">The <c>regstart</c> field.</param>
/// <param name="RegexAnchored">The <c>reganch</c> field.</param>
/// <param name="RegexMustLength">The <c>regmlen</c> field.</param>
/// <param name="Program">The compiled program bytes.</param>
public sealed record FliteRegexSource(string Name, string Pattern, int RegexStart, int RegexAnchored, int RegexMustLength, ReadOnlyMemory<byte> Program);

/// <summary>A finite state machine of the "is this a pronounceable word" check: transitions are <c>state * vocabulary + symbol</c>, a state starts at its own index and ends with a 0.</summary>
/// <param name="VocabularySize">The number of symbols (128).</param>
/// <param name="Transitions">The transition values.</param>
public sealed record FliteFsm(int VocabularySize, IReadOnlyList<ushort> Transitions);

/// <summary>The data of the US English front end that the voice needs: the six classification trees, the phone set, the F0 and duration models, the closed-class word lists, the text-analysis regular expressions and the pronounceability machines. Immutable; one shared instance.</summary>
public sealed class FliteEnglishData
{
    private static readonly Lazy<FliteEnglishData> Shared = new(() => new FliteEnglishData(FliteBin.Open("flite-usenglish.bin")), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Dictionary<string, FliteCart> carts = new(StringComparer.Ordinal);

    private FliteEnglishData(FliteBin file)
    {
        foreach (string name in file.Names.Where(n => n.StartsWith("cart:", StringComparison.Ordinal)))
        {
            string cartName = name[5..];
            carts[cartName] = FliteCartCodec.Read(cartName, file.Section(name));
        }

        FliteBinReader ps = file.Reader("phoneset");
        string[] featureNames = ps.TextList(), phoneNames = ps.TextList(), featureValues = ps.TextList();
        int[][] rows = new int[ps.Int32()][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new int[ps.Int32()];
            for (int j = 0; j < rows[i].Length; j++) rows[i][j] = ps.Int32();
        }

        Phoneset = new FlitePhoneset(featureNames, phoneNames, featureValues, rows, ps.Text(), ps.Int32());

        FliteBinReader f0 = file.Reader("f0lr");
        FliteF0Term[] terms = new FliteF0Term[f0.Int32()];
        for (int i = 0; i < terms.Length; i++) terms[i] = new FliteF0Term(f0.TextOrNull(), f0.Single(), f0.Single(), f0.Single(), f0.TextOrNull());
        F0Terms = terms;

        FliteBinReader dur = file.Reader("dur-stats");
        FliteDurationStat[] stats = new FliteDurationStat[dur.Int32()];
        for (int i = 0; i < stats.Length; i++) stats[i] = new FliteDurationStat(dur.Text(), dur.Single(), dur.Single());
        DurationStats = stats;

        FliteBinReader gpos = file.Reader("gpos");
        FliteWordList[] lists = new FliteWordList[gpos.Int32()];
        for (int i = 0; i < lists.Length; i++) lists[i] = new FliteWordList(gpos.Text(), gpos.TextList());
        WordLists = lists;

        FliteBinReader regex = file.Reader("regex");
        FliteRegexSource[] expressions = new FliteRegexSource[regex.Int32()];
        for (int i = 0; i < expressions.Length; i++) expressions[i] = new FliteRegexSource(regex.Text(), regex.Text(), regex.Int32(), regex.Int32(), regex.Int32(), regex.Bytes());
        Regexes = expressions;

        FliteBinReader aswd = file.Reader("aswd");
        FliteFsm[] machines = new FliteFsm[2];
        for (int m = 0; m < machines.Length; m++)
        {
            int vocabulary = aswd.Int32();
            ushort[] transitions = new ushort[aswd.Int32()];
            for (int i = 0; i < transitions.Length; i++) transitions[i] = aswd.UInt16();
            machines[m] = new FliteFsm(vocabulary, transitions);
        }

        WordStartMachine = machines[0];
        WordEndMachine = machines[1];
    }

    /// <summary>The shared instance, read from the embedded data on first use.</summary>
    public static FliteEnglishData Instance => Shared.Value;

    /// <summary>The names of the six trees: <c>us_durz_cart</c>, <c>us_int_accent_cart</c>, <c>us_int_tone_cart</c>, <c>us_nums_cart</c>, <c>us_phrasing_cart</c> and <c>us_pos_cart</c>.</summary>
    public IReadOnlyCollection<string> CartNames => carts.Keys;

    /// <summary>The tree with this Flite name.</summary>
    public FliteCart Cart(string name) => carts.TryGetValue(name, out FliteCart? cart) ? cart : throw new KeyNotFoundException($"There is no tree '{name}'.");

    /// <summary>The US English phone set.</summary>
    public FlitePhoneset Phoneset { get; }

    /// <summary>The terms of the F0 regression model, without the closing empty term.</summary>
    public IReadOnlyList<FliteF0Term> F0Terms { get; }

    /// <summary>The phone duration statistics, in the order of the C list.</summary>
    public IReadOnlyList<FliteDurationStat> DurationStats { get; }

    /// <summary>The closed-class word lists, in the order of <c>us_gpos</c> (in, to, determiners, modals, conjunctions, wh-words, pronouns, auxiliaries, punctuation).</summary>
    public IReadOnlyList<FliteWordList> WordLists { get; }

    /// <summary>The compiled regular expressions of the text analysis.</summary>
    public IReadOnlyList<FliteRegexSource> Regexes { get; }

    /// <summary>The machine that reads a word from its start to the first vowel.</summary>
    public FliteFsm WordStartMachine { get; }

    /// <summary>The machine that reads a word from its end back to the last vowel.</summary>
    public FliteFsm WordEndMachine { get; }
}
