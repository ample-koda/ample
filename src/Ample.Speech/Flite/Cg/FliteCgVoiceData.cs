using System.Buffers.Binary;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), include/cst_cg.h (cst_cg_db) and the generated data of lang/cmu_us_slt, rms and awb.
// Translated to C# and modified: the C tables are read from converted files.

/// <summary>The converted data of one Flite clustergen voice (<c>slt</c>, <c>rms</c> or <c>awb</c>; the C structure is <c>cst_cg_db</c>): the cluster vectors, the trees that choose
/// a cluster for each frame, the duration model, the states of each phone and the constants of the voice. The data is read from a file written by tools/flite-data (<c>--cg</c>):
/// the files are embedded in the assembly (licence of the recordings: LICENSES/cmu-arctic.txt). The values are exactly those of the C tables; nothing is interpreted here.</summary>
public sealed class FliteCgVoiceData
{
    private readonly ushort[] parameters;

    private FliteCgVoiceData(FliteBin file)
    {
        FliteBinReader consts = file.Reader("consts");
        Name = consts.Text();
        SampleRate = consts.Int32();
        F0Mean = consts.Single();
        F0Stddev = consts.Single();
        FrameAdvance = consts.Single();
        DoMlpg = consts.Int32() != 0;
        MlsaAlpha = consts.Single();
        MlsaBeta = consts.Single();
        MultiModel = consts.Int32() != 0;
        MixedExcitation = consts.Int32() != 0;
        MixedExcitationFilterCount = consts.Int32();
        MixedExcitationOrder = consts.Int32();
        Spamf0 = consts.Int32() != 0;
        Gain = consts.Single();
        NumChannels = consts.Int32();
        NumFrames = consts.Int32();
        int numTypes = consts.Int32();
        int dynamicWindowSize = consts.Int32();
        ModelShape = consts.Int32();

        Types = file.Reader("types").TextList();
        if (Types.Count != numTypes) throw new InvalidDataException("The number of types does not match.");
        ModelMin = Floats(file, "min", NumChannels);
        ModelRange = Floats(file, "range", NumChannels);
        DynamicWindow = Floats(file, "dynwin", dynamicWindowSize);

        ReadOnlySpan<byte> filters = file.Section("mefilters").Span;
        if (filters.Length != MixedExcitationFilterCount * MixedExcitationOrder * 8) throw new InvalidDataException("The mixed excitation filters have the wrong size.");
        double[][] taps = new double[MixedExcitationFilterCount][];
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] = new double[MixedExcitationOrder];
            for (int j = 0; j < MixedExcitationOrder; j++) taps[i][j] = BinaryPrimitives.ReadDoubleLittleEndian(filters[(((i * MixedExcitationOrder) + j) * 8)..]);
        }

        MixedExcitationFilters = taps;

        ReadOnlySpan<byte> raw = file.Section("params").Span;
        if (raw.Length != NumFrames * NumChannels * 2) throw new InvalidDataException("The cluster vectors have the wrong size.");
        parameters = new ushort[NumFrames * NumChannels];
        for (int i = 0; i < parameters.Length; i++) parameters[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);

        F0Trees = Trees(file.Reader("f0trees"), Types);
        McepTrees = Trees(file.Reader("mceptrees"), Types);
        DurationCart = FliteCartCodec.Read("dur", file.Section("durcart"));

        FliteBinReader stats = file.Reader("durstats");
        FliteDurationStat[] durations = new FliteDurationStat[stats.Int32()];
        for (int i = 0; i < durations.Length; i++) durations[i] = new FliteDurationStat(stats.Text(), stats.Single(), stats.Single());
        DurationStats = durations;

        if (Spamf0)
        {
            Spamf0AccentTree = FliteCartCodec.Read("spamf0-accent", file.Section("spamf0accent"));
            Spamf0PhraseTree = FliteCartCodec.Read("spamf0-phrase", file.Section("spamf0phrase"));
            FliteBinReader vectors = file.Reader("spamf0vectors");
            int channels = vectors.Int32(), rows = vectors.Int32();
            float[][] accent = new float[rows][];
            for (int i = 0; i < rows; i++)
            {
                accent[i] = new float[channels];
                for (int j = 0; j < channels; j++) accent[i][j] = vectors.Single();
            }

            Spamf0AccentVectors = accent;
        }

        FliteBinReader states = file.Reader("states");
        string[][] phones = new string[states.Int32()][];
        for (int i = 0; i < phones.Length; i++) phones[i] = states.TextList();
        PhoneStates = phones;
    }

    /// <summary>Gets the names of the voices whose data is embedded in the assembly.</summary>
    public static IReadOnlyList<string> VoiceNames { get; } = ["slt", "rms", "awb"];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<FliteCgVoiceData>> Shared = new(StringComparer.Ordinal);

    /// <summary>Gets the voice with this name from the data embedded in the assembly; it is read once and shared (the data is immutable). The hash of the embedded file is checked.</summary>
    /// <param name="voice">The name: <c>slt</c>, <c>rms</c> or <c>awb</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such voice.</exception>
    /// <exception cref="InvalidOperationException">The embedded file is missing or does not have the expected hash.</exception>
    public static FliteCgVoiceData Embedded(string voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (!VoiceNames.Contains(voice, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(voice), $"There is no clustergen voice '{voice}'.");
        return Shared.GetOrAdd(voice, name => new Lazy<FliteCgVoiceData>(() => ReadEmbedded(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Gets the identity of the takes that the voice makes: the Flite source, the converted data of the voice and the shared front end (a changed value invalidates cached takes).</summary>
    /// <param name="voice">The name: <c>slt</c>, <c>rms</c> or <c>awb</c>.</param>
    public static string IdentityOf(string voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (!FliteCgDataIdentity.Sha256.TryGetValue(voice, out string? hash)) throw new ArgumentOutOfRangeException(nameof(voice), $"There is no clustergen voice '{voice}'.");
        return $"flite/cmu_us_{voice}/{FliteCgDataIdentity.SourceCommit[..8]}+{hash[..16]}+{FliteDataIdentity.DataSha256[..8]}";
    }

    private static FliteCgVoiceData ReadEmbedded(string voice)
    {
        string name = $"Ample.Speech.Flite.Cg.Data.flite-cg-{voice}.bin";
        using Stream stream = typeof(FliteCgVoiceData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The data file of the voice '{voice}' is not part of the assembly.");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != FliteCgDataIdentity.Sha256[voice]) throw new InvalidOperationException($"The data of the voice '{voice}' does not have the expected hash.");
        return new FliteCgVoiceData(FliteBin.FromBytes(bytes));
    }

    /// <summary>Reads the converted file of a voice from disk.</summary>
    /// <param name="path">The path of <c>flite-cg-VOICE.bin</c>.</param>
    public static FliteCgVoiceData Load(string path) => new(FliteBin.FromBytes(File.ReadAllBytes(path)));

    /// <summary>Reads the converted file of a voice from its bytes.</summary>
    /// <param name="bytes">The contents of <c>flite-cg-VOICE.bin</c>.</param>
    public static FliteCgVoiceData FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new FliteCgVoiceData(FliteBin.FromBytes(bytes));
    }

    /// <summary>Gets the name of the voice, for example <c>cmu_us_slt</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the sample rate of the output in Hz (16,000 for the three American voices).</summary>
    public int SampleRate { get; }

    /// <summary>Gets the mean of the F0 of the training data in Hz (the voice's default for <c>int_f0_target_mean</c>).</summary>
    public float F0Mean { get; }

    /// <summary>Gets the standard deviation of the F0 of the training data in Hz (the default for <c>int_f0_target_stddev</c>).</summary>
    public float F0Stddev { get; }

    /// <summary>Gets the time between two parameter frames in seconds (0.005).</summary>
    public float FrameAdvance { get; }

    /// <summary>Gets a value indicating whether the parameter track is smoothed with MLPG (the cluster vectors then hold the means and standard deviations).</summary>
    public bool DoMlpg { get; }

    /// <summary>Gets the all-pass constant of the mel-cepstrum filter.</summary>
    public float MlsaAlpha { get; }

    /// <summary>Gets the post filter factor of the MLSA filter.</summary>
    public float MlsaBeta { get; }

    /// <summary>Gets a value indicating whether the voice has several models (always false for these voices).</summary>
    public bool MultiModel { get; }

    /// <summary>Gets a value indicating whether the excitation is mixed (a pulse train and noise shaped by the five filters).</summary>
    public bool MixedExcitation { get; }

    /// <summary>Gets the number of mixed excitation filters (5).</summary>
    public int MixedExcitationFilterCount { get; }

    /// <summary>Gets the number of taps of each mixed excitation filter (48).</summary>
    public int MixedExcitationOrder { get; }

    /// <summary>Gets a value indicating whether the accent and phrase F0 model (<c>spamf0</c>) is used; only <c>rms</c> sets it.</summary>
    public bool Spamf0 { get; }

    /// <summary>Gets the gain of the vocoder output.</summary>
    public float Gain { get; }

    /// <summary>Gets the number of values of a cluster vector (114).</summary>
    public int NumChannels { get; }

    /// <summary>Gets the number of cluster vectors.</summary>
    public int NumFrames { get; }

    /// <summary>Gets the model shape: 1 is <c>CST_CG_MODEL_SHAPE_BASE_MINRANGE</c> (every value is a 16 bit number scaled with the minimum and range of its channel).</summary>
    public int ModelShape { get; }

    /// <summary>Gets the names of the types: the phone states (<c>zh_204</c>...) in the order of the trees.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>Gets the smallest value of each channel (the offset of the 16 bit scale).</summary>
    public ReadOnlyMemory<float> ModelMin { get; }

    /// <summary>Gets the range of each channel (the width of the 16 bit scale).</summary>
    public ReadOnlyMemory<float> ModelRange { get; }

    /// <summary>Gets the window of the delta coefficients that MLPG uses.</summary>
    public ReadOnlyMemory<float> DynamicWindow { get; }

    /// <summary>Gets the taps of the five mixed excitation filters.</summary>
    public IReadOnlyList<double[]> MixedExcitationFilters { get; }

    /// <summary>Gets the F0 trees, one for each type, in the order of <see cref="Types"/>; an answer is a float.</summary>
    public IReadOnlyList<FliteCart> F0Trees { get; }

    /// <summary>Gets the trees that choose a cluster vector for each type, in the order of <see cref="Types"/>; an answer is the number of the vector.</summary>
    public IReadOnlyList<FliteCart> McepTrees { get; }

    /// <summary>Gets the tree that predicts the duration of a state as a z-score.</summary>
    public FliteCart DurationCart { get; }

    /// <summary>Gets the mean and standard deviation of the duration of each state, in the order of the table of the C file.</summary>
    public IReadOnlyList<FliteDurationStat> DurationStats { get; }

    /// <summary>Gets the states of each phone: the phone name followed by the names of its states, in the order of the table of the C file.</summary>
    public IReadOnlyList<IReadOnlyList<string>> PhoneStates { get; }

    /// <summary>Gets the tree of the accent F0 model (<c>spamf0</c>) that chooses a row of <see cref="Spamf0AccentVectors"/> for a syllable; null when the voice has no spamf0.</summary>
    public FliteCart? Spamf0AccentTree { get; }

    /// <summary>Gets the tree of the phrase F0 model (<c>spamf0</c>) that gives the F0 level of a segment as a float; null when the voice has no spamf0.</summary>
    public FliteCart? Spamf0PhraseTree { get; }

    /// <summary>Gets the accent vectors of spamf0 (the peak, the amplitude, the duration and the tilt of a tilt event, in the order of the columns); null when the voice has no spamf0.</summary>
    public IReadOnlyList<float[]>? Spamf0AccentVectors { get; }

    /// <summary>Gets all cluster vectors one after the other: <see cref="NumFrames"/> rows of <see cref="NumChannels"/> values.</summary>
    public ReadOnlyMemory<ushort> Parameters => parameters;

    /// <summary>Gets one value of one cluster vector as the 16 bit number of the file (the C <c>model_vectors[0][frame][channel]</c>).</summary>
    /// <param name="frame">The number of the cluster vector.</param>
    /// <param name="channel">The channel in the vector.</param>
    public ushort Parameter(int frame, int channel) => parameters[(frame * NumChannels) + channel];

    private static float[] Floats(FliteBin file, string section, int count)
    {
        ReadOnlySpan<byte> raw = file.Section(section).Span;
        if (raw.Length != count * 4) throw new InvalidDataException($"The section '{section}' has the wrong size.");
        float[] values = new float[count];
        for (int i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(raw[(i * 4)..]);
        return values;
    }

    private static FliteCart[] Trees(FliteBinReader reader, IReadOnlyList<string> types)
    {
        FliteCart[] trees = new FliteCart[reader.Int32()];
        if (trees.Length != types.Count) throw new InvalidDataException("There is not one tree for each type.");
        for (int i = 0; i < trees.Length; i++) trees[i] = FliteCartCodec.Read(types[i], reader.Bytes());
        return trees;
    }
}
