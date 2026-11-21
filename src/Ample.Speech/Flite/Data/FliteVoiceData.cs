using System.Buffers.Binary;
using System.Globalization;

namespace Ample.Speech.Flite.Data;

/// <summary>One entry of the diphone index: the first frame of the diphone, and the pitch-mark counts of its two halves (Flite <c>cst_diphone_entry</c>).</summary>
/// <param name="Name">The diphone name such as <c>aa-ae</c>.</param>
/// <param name="StartPm">The number of the first short-term frame of the diphone.</param>
/// <param name="PbPm">The number of frames before the phone boundary.</param>
/// <param name="EndPm">The number of frames after the phone boundary.</param>
public readonly record struct FliteDiphoneEntry(string Name, int StartPm, int PbPm, int EndPm);

/// <summary>The diphone voice <c>cmu_us_kal16</c>: the short-term LPC frames, the residual bytes with their offsets, the diphone index and the voice parameters. Immutable; one shared instance.</summary>
public sealed class FliteVoiceData
{
    private static readonly Lazy<FliteVoiceData> Shared = new(() => new FliteVoiceData(FliteBin.Open("flite-kal16-voice.bin")), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly FliteDiphoneEntry[] diphones;
    private readonly ushort[] lpc;
    private readonly uint[] residualOffsets;
    private readonly Dictionary<string, float> floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> ints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);

    private FliteVoiceData(FliteBin file)
    {
        FliteBinReader sts = file.Reader("sts");
        NumFrames = sts.Int32();
        NumChannels = sts.Int32();
        SampleRate = sts.Int32();
        CoeffMin = sts.Single();
        CoeffRange = sts.Single();

        ReadOnlyMemory<byte> lpcBytes = file.Section("lpc");
        lpc = new ushort[lpcBytes.Length / 2];
        for (int i = 0; i < lpc.Length; i++) lpc[i] = BinaryPrimitives.ReadUInt16LittleEndian(lpcBytes.Span[(i * 2)..]);
        ReadOnlyMemory<byte> offsetBytes = file.Section("resi");
        residualOffsets = new uint[offsetBytes.Length / 4];
        for (int i = 0; i < residualOffsets.Length; i++) residualOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(offsetBytes.Span[(i * 4)..]);
        Residual = file.Section("res");

        FliteBinReader index = file.Reader("index");
        diphones = new FliteDiphoneEntry[index.Int32()];
        for (int i = 0; i < diphones.Length; i++) diphones[i] = new FliteDiphoneEntry(index.Text(), index.UInt16(), index.Byte(), index.Byte());

        FliteBinReader parameters = file.Reader("params");
        int count = parameters.Int32();
        for (int i = 0; i < count; i++)
        {
            string name = parameters.Text();
            switch (parameters.Byte())
            {
                case 0: floats[name] = parameters.Single(); break;
                case 1: ints[name] = parameters.Int32(); break;
                default: strings[name] = parameters.Text(); break;
            }
        }
    }

    /// <summary>The shared instance, read from the embedded data on first use.</summary>
    public static FliteVoiceData Instance => Shared.Value;

    /// <summary>The number of short-term frames (<c>num_sts</c>).</summary>
    public int NumFrames { get; }

    /// <summary>The LPC coefficients of every frame (<c>num_channels</c>).</summary>
    public int NumChannels { get; }

    /// <summary>The sample rate of the voice in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>The lowest coefficient value (<c>coeff_min</c>), for decoding the 16-bit frames.</summary>
    public float CoeffMin { get; }

    /// <summary>The range of the coefficient values (<c>coeff_range</c>).</summary>
    public float CoeffRange { get; }

    /// <summary>The 16-bit LPC values, <see cref="NumFrames"/> frames of <see cref="NumChannels"/> values.</summary>
    public ReadOnlyMemory<ushort> Lpc => lpc;

    /// <summary>The residual bytes (µ-law) of all frames.</summary>
    public ReadOnlyMemory<byte> Residual { get; }

    /// <summary>The start of every frame in <see cref="Residual"/>, and the end of the last as one more value (<see cref="NumFrames"/> + 1 values).</summary>
    public ReadOnlyMemory<uint> ResidualOffsets => residualOffsets;

    /// <summary>The diphone index, sorted by the byte order of the names.</summary>
    public IReadOnlyList<FliteDiphoneEntry> Diphones => diphones;

    /// <summary>The voice parameters that are plain numbers or strings in the voice definition (for example <c>duration_stretch</c> 1.1).</summary>
    public IReadOnlyDictionary<string, float> FloatParameters => floats;

    /// <summary>The integer voice parameters.</summary>
    public IReadOnlyDictionary<string, int> IntParameters => ints;

    /// <summary>The string voice parameters.</summary>
    public IReadOnlyDictionary<string, string> StringParameters => strings;

    /// <summary>The length of one frame of the residual in bytes.</summary>
    public int ResidualSize(int frame) => checked((int)(residualOffsets[frame + 1] - residualOffsets[frame]));

    /// <summary>The index of the diphone with this name by binary search in byte order (Flite <c>strcmp</c>), or -1.</summary>
    public int FindDiphone(string name)
    {
        int low = 0, high = diphones.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int order = string.CompareOrdinal(diphones[middle].Name, name);
            if (order == 0) return middle;
            if (order < 0) low = middle + 1;
            else high = middle - 1;
        }

        return -1;
    }

    /// <summary>The value of a float parameter, for example <c>duration_stretch</c>.</summary>
    public float FloatParameter(string name) => floats.TryGetValue(name, out float value) ? value : throw new KeyNotFoundException($"The voice has no float parameter '{name}'.");

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"cmu_us_kal16: {NumFrames} frames, {diphones.Length} diphones, {SampleRate} Hz");
}
