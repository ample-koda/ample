using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ample.Speech.Flite.Cg;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>The canonical byte form of every table of a clustergen voice that the loader exposes. The same forms are written by plan/speech/flite/reference/cg/dumpcgdata.c from the
/// arrays that the compiled C program holds, so a digest that is equal on both sides proves that the converted data is what the C program has.</summary>
internal static class CgDataCanon
{
    private static string Hex(float value) => BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] FloatBits(ReadOnlySpan<float> values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] Trees(IReadOnlyList<FliteCart> trees, IReadOnlyList<string> types)
    {
        using MemoryStream stream = new();
        for (int i = 0; i < trees.Count; i++)
        {
            stream.Write(Latin1("T:" + types[i] + "\n"));
            stream.Write(DataCanon.CartText(trees[i]));
        }

        return stream.ToArray();
    }

    /// <summary>All tables of the voice by file name (<c>cg.VOICE.TABLE</c>), as dumpcgdata.c writes them.</summary>
    public static IReadOnlyDictionary<string, byte[]> All(string voice, FliteCgVoiceData data)
    {
        Dictionary<string, byte[]> all = new(StringComparer.Ordinal);
        string p = "cg." + voice + ".";
        StringBuilder consts = new();
        consts.Append("name ").Append(data.Name).Append('\n');
        consts.Append("sample_rate ").Append(Num(data.SampleRate)).Append('\n');
        consts.Append("f0_mean ").Append(Hex(data.F0Mean)).Append('\n');
        consts.Append("f0_stddev ").Append(Hex(data.F0Stddev)).Append('\n');
        consts.Append("frame_advance ").Append(Hex(data.FrameAdvance)).Append('\n');
        consts.Append("do_mlpg ").Append(data.DoMlpg ? 1 : 0).Append('\n');
        consts.Append("mlsa_alpha ").Append(Hex(data.MlsaAlpha)).Append('\n');
        consts.Append("mlsa_beta ").Append(Hex(data.MlsaBeta)).Append('\n');
        consts.Append("multimodel ").Append(data.MultiModel ? 1 : 0).Append('\n');
        consts.Append("mixed_excitation ").Append(data.MixedExcitation ? 1 : 0).Append('\n');
        consts.Append("me_num ").Append(Num(data.MixedExcitationFilterCount)).Append('\n');
        consts.Append("me_order ").Append(Num(data.MixedExcitationOrder)).Append('\n');
        consts.Append("spamf0 ").Append(data.Spamf0 ? 1 : 0).Append('\n');
        consts.Append("gain ").Append(Hex(data.Gain)).Append('\n');
        consts.Append("num_channels ").Append(Num(data.NumChannels)).Append('\n');
        consts.Append("num_frames ").Append(Num(data.NumFrames)).Append('\n');
        consts.Append("num_types ").Append(Num(data.Types.Count)).Append('\n');
        consts.Append("dynwinsize ").Append(Num(data.DynamicWindow.Length)).Append('\n');
        consts.Append("model_shape ").Append(Num(data.ModelShape)).Append('\n');
        all[p + "consts.txt"] = Latin1(consts.ToString());
        all[p + "types.txt"] = Latin1(string.Concat(data.Types.Select(type => type + "\n")));
        all[p + "min.bin"] = FloatBits(data.ModelMin.Span);
        all[p + "range.bin"] = FloatBits(data.ModelRange.Span);
        all[p + "dynwin.bin"] = FloatBits(data.DynamicWindow.Span);
        byte[] filters = new byte[data.MixedExcitationFilterCount * data.MixedExcitationOrder * 8];
        for (int i = 0; i < data.MixedExcitationFilterCount; i++)
        {
            for (int j = 0; j < data.MixedExcitationOrder; j++) BinaryPrimitives.WriteDoubleLittleEndian(filters.AsSpan(((i * data.MixedExcitationOrder) + j) * 8), data.MixedExcitationFilters[i][j]);
        }

        all[p + "mefilters.bin"] = filters;
        byte[] parameters = new byte[data.Parameters.Length * 2];
        for (int i = 0; i < data.Parameters.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(parameters.AsSpan(i * 2), data.Parameters.Span[i]);
        all[p + "params.bin"] = parameters;
        all[p + "f0trees.txt"] = Trees(data.F0Trees, data.Types);
        all[p + "mceptrees.txt"] = Trees(data.McepTrees, data.Types);
        all[p + "durcart.txt"] = Trees([data.DurationCart], ["dur"]);
        all[p + "durstats.txt"] = Latin1(string.Concat(data.DurationStats.Select(stat => $"{stat.Phone} {Hex(stat.Mean)} {Hex(stat.Stddev)}\n")));
        all[p + "states.txt"] = Latin1(string.Concat(data.PhoneStates.Select(states => string.Join('\t', states) + "\n")));
        return all;
    }
}
