using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Tests;

/// <summary>The canonical byte form of every table the loaders expose. The same forms are written by plan/speech/flite/reference/dumpdata.c from the arrays that the compiled C program holds in memory,
/// so a digest that is equal on both sides proves that the converted data is what the C program has.</summary>
internal static class DataCanon
{
    private static string Hex(float value) => BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static byte[] Text(Action<StringBuilder> write)
    {
        StringBuilder text = new();
        write(text);
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] LittleEndian16(ReadOnlySpan<ushort> values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    public static IReadOnlyDictionary<string, byte[]> All()
    {
        Dictionary<string, byte[]> all = new(StringComparer.Ordinal);
        FliteVoiceData voice = FliteVoiceData.Instance;
        all["voice.sts.txt"] = Text(t => t.Append(Num(voice.NumFrames)).Append(' ').Append(Num(voice.NumChannels)).Append(' ').Append(Num(voice.SampleRate)).Append(' ').Append(Hex(voice.CoeffMin)).Append(' ').Append(Hex(voice.CoeffRange)).Append('\n'));
        all["voice.lpc.bin"] = LittleEndian16(voice.Lpc.Span);
        byte[] offsets = new byte[voice.ResidualOffsets.Length * 4];
        for (int i = 0; i < voice.ResidualOffsets.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(offsets.AsSpan(i * 4), voice.ResidualOffsets.Span[i]);
        all["voice.resi.bin"] = offsets;
        all["voice.res.bin"] = voice.Residual.ToArray();
        all["voice.index.txt"] = Text(t =>
        {
            foreach (FliteDiphoneEntry entry in voice.Diphones) t.Append(entry.Name).Append('\t').Append(Num(entry.StartPm)).Append('\t').Append(Num(entry.PbPm)).Append('\t').Append(Num(entry.EndPm)).Append('\n');
        });
        all["voice.params.txt"] = Text(t =>
        {
            foreach (KeyValuePair<string, float> pair in voice.FloatParameters.OrderBy(p => p.Key, StringComparer.Ordinal)) t.Append("f ").Append(pair.Key).Append(' ').Append(Hex(pair.Value)).Append('\n');
            foreach (KeyValuePair<string, string> pair in voice.StringParameters.OrderBy(p => p.Key, StringComparer.Ordinal)) t.Append("s ").Append(pair.Key).Append(' ').Append(pair.Value).Append('\n');
        });

        FliteLexiconData lexicon = FliteLexiconData.Instance;
        all["lex.data.bin"] = lexicon.Data.ToArray();
        all["lex.info.txt"] = Text(t => t.Append(Num(lexicon.NumEntries)).Append(' ').Append(Num(lexicon.NumBytes)).Append('\n'));
        all["lex.phones.txt"] = Text(t =>
        {
            foreach (string phone in lexicon.PhoneTable) t.Append(phone).Append('\n');
        });
        all["lex.phonehuff.txt"] = Huffman(lexicon.PhoneHuffman);
        all["lex.entryhuff.txt"] = Huffman(lexicon.EntryHuffman);
        all["lex.addenda.txt"] = Text(t =>
        {
            foreach (IReadOnlyList<string> list in lexicon.Addenda) t.Append(string.Join('\t', list)).Append('\n');
        });

        FliteLtsData lts = FliteLtsData.Instance;
        all["lts.info.txt"] = Text(t => t.Append(Num(lts.RowCount)).Append(' ').Append(Num(lts.ContextWindowSize)).Append(' ').Append(Num(lts.ContextExtraFeatures)).Append('\n'));
        all["lts.model.bin"] = lts.Model.ToArray();
        all["lts.letterindex.bin"] = LittleEndian16([.. lts.LetterIndex]);
        all["lts.phones.txt"] = Text(t =>
        {
            foreach (string phone in lts.PhoneTable) t.Append(phone).Append('\n');
        });
        all["lts.letters.txt"] = Text(t =>
        {
            foreach (string letter in lts.LetterTable) t.Append(letter).Append('\n');
        });

        FliteEnglishData english = FliteEnglishData.Instance;
        foreach (string name in english.CartNames) all[$"cart.{name}.txt"] = CartText(english.Cart(name));
        FlitePhoneset ps = english.Phoneset;
        all["phoneset.txt"] = Text(t =>
        {
            foreach (string name in ps.FeatureNames) t.Append("n:").Append(name).Append('\n');
            foreach (string name in ps.PhoneNames) t.Append("p:").Append(name).Append('\n');
            foreach (string value in ps.FeatureValues) t.Append("v:").Append(value).Append('\n');
            for (int i = 0; i < ps.NumPhones; i++) t.Append("r:").Append(string.Join(' ', ps.Rows[i].Select(Num))).Append('\n');
            t.Append("s:").Append(ps.Silence).Append("\nc:").Append(Num(ps.NumPhones)).Append('\n');
        });
        all["f0lr.txt"] = Text(t =>
        {
            foreach (FliteF0Term term in english.F0Terms) t.Append(term.Feature ?? "-").Append(' ').Append(Hex(term.Start)).Append(' ').Append(Hex(term.Mid)).Append(' ').Append(Hex(term.End)).Append(' ').Append(term.Type ?? "-").Append('\n');
        });
        all["durstats.txt"] = Text(t =>
        {
            foreach (FliteDurationStat stat in english.DurationStats) t.Append(stat.Phone).Append(' ').Append(Hex(stat.Mean)).Append(' ').Append(Hex(stat.Stddev)).Append('\n');
        });
        all["gpos.txt"] = Text(t =>
        {
            foreach (FliteWordList list in english.WordLists) t.Append(string.Join('\t', list.Words)).Append('\n');
        });
        all["regex.txt"] = Text(t =>
        {
            foreach (FliteRegexSource regex in english.Regexes) t.Append(regex.Name).Append(' ').Append(Num(regex.RegexStart)).Append(' ').Append(Num(regex.RegexAnchored)).Append(' ').Append(Num(regex.RegexMustLength)).Append(' ').Append(Convert.ToHexString(regex.Program.Span).ToLowerInvariant()).Append('\n');
        });
        return all;
    }

    private static byte[] Huffman(IReadOnlyList<byte[]?> table) => Text(t =>
    {
        foreach (byte[]? entry in table) t.Append(entry is null ? "-" : Convert.ToHexString(entry).ToLowerInvariant()).Append('\n');
    });

    internal static byte[] CartText(FliteCart cart) => Text(t =>
    {
        foreach (FliteCartNode node in cart.Nodes)
        {
            t.Append(Num(node.Feature)).Append(' ').Append(Num(node.Operator)).Append(' ').Append(Num(node.NoNode)).Append(' ');
            t.Append(node.Kind switch { FliteCartValueKind.Text => "S:" + node.Text, FliteCartValueKind.Number => "F:" + Hex(node.Number), _ => "-" }).Append('\n');
        }

        foreach (string feature in cart.Features) t.Append("f:").Append(feature).Append('\n');
    });
}
