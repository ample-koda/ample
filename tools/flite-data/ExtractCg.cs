using System.Globalization;
using System.Text.RegularExpressions;

namespace FliteData;

/// <summary>Parses the generated C files of a Flite clustergen voice (<c>lang/cmu_us_slt</c>, <c>cmu_us_rms</c>, <c>cmu_us_awb</c>) into the sections of its .bin file.
/// Every table is counted and checked against what the C files state about themselves (the number of frames, channels and types, the order of the trees).</summary>
internal static class ExtractCg
{
    private static string Cs(string root, string voice, string file) => CSource.Strip(CSource.Read(Path.Combine(root, "lang", "cmu_us_" + voice, file)));

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    private static string[] Names(string body) => [.. CSource.Items(body).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)];

    public static List<KeyValuePair<string, byte[]>> Voice(string root, string voice)
    {
        string prefix = "cmu_us_" + voice;
        string cg = Cs(root, voice, $"{prefix}_cg.c");
        Dictionary<string, string> defines = CSource.Defines(cg);
        int channels = CSource.Eval($"{prefix}_single_num_channels", defines);
        int frames = CSource.Eval($"{prefix}_single_num_frames", defines);
        int numTypes = CSource.Eval($"{prefix}_num_types", defines);

        // the structure of the voice, in the field order of cst_cg_db (include/cst_cg.h)
        List<string> db = CSource.Items(CSource.Body(cg, $"{prefix}_cg_db ="));
        Check(db.Count == 39, $"The cg_db of {voice} has {db.Count} fields, expected 39.");
        Check(CSource.DecodeText(db[0]) == prefix, "The name of the voice does not match.");
        int Eval(int field) => CSource.Eval(db[field], defines);
        Check(Eval(2) == numTypes && Eval(6) == 1 && Eval(8) == 1 && Eval(23) == 1, "The voice does not have exactly one f0, one parameter and one duration model.");
        Check(db[20] == "NULL" && db[21] == "CST_CG_MODEL_SHAPE_BASE_MINRANGE", "Only the BASE_MINRANGE model shape without a qtable is supported.");

        string[] types = Names(CSource.Body(cg, $"{prefix}_types[] ="));
        Check(types.Length == numTypes, $"The voice has {types.Length} types, the file says {numTypes}.");

        SectionWriter consts = new();
        consts.Text(prefix);
        consts.Int(Eval(3));                                  // sample rate
        consts.Float(CSource.Float(db[4]));                   // f0 mean
        consts.Float(CSource.Float(db[5]));                   // f0 stddev
        consts.Float(CSource.Float(db[22]));                  // frame advance
        consts.Int(Eval(27));                                 // do_mlpg
        consts.Float(CSource.Float(db[30]));                  // mlsa alpha
        consts.Float(CSource.Float(db[31]));                  // mlsa beta
        consts.Int(Eval(32));                                 // multimodel
        consts.Int(Eval(33));                                 // mixed excitation
        consts.Int(Eval(34));                                 // number of mixed excitation filters
        consts.Int(Eval(35));                                 // their order
        consts.Int(Eval(37));                                 // spamf0
        consts.Float(CSource.Float(db[38]));                  // gain
        consts.Int(channels);
        consts.Int(frames);
        consts.Int(numTypes);
        consts.Int(Eval(29));                                 // size of the dynamic window
        consts.Int(1);                                        // model shape

        SectionWriter typeSection = new();
        typeSection.TextList(types);

        float[] min = Floats(CSource.Body(cg, $"{prefix}_model_min[] ="));
        float[] range = Floats(CSource.Body(cg, $"{prefix}_model_range[] ="));
        Check(min.Length == channels && range.Length == channels, "The model min and range do not have one value per channel.");
        float[] dynwin = Floats(CSource.Body(cg, $"{prefix}_dynwin[] ="));
        Check(dynwin.Length == Eval(29), "The dynamic window has the wrong size.");

        int meNum = Eval(34), meOrder = Eval(35);
        SectionWriter filters = new();
        for (int i = 0; i < meNum; i++)
        {
            string[] values = [.. CSource.Items(CSource.Body(cg, $"{prefix}_me_filter_{i}[] ="))];
            Check(values.Length == meOrder, $"Mixed excitation filter {i} has {values.Length} taps, expected {meOrder}.");
            foreach (string value in values) filters.Raw(BitConverter.GetBytes(CSource.Double(value)));
        }

        return
        [
            new("consts", consts.ToArray()), new("types", typeSection.ToArray()), new("min", FloatBytes(min)), new("range", FloatBytes(range)),
            new("dynwin", FloatBytes(dynwin)), new("mefilters", filters.ToArray()), new("params", Params(root, voice, frames, channels)),
            new("f0trees", Trees(root, voice, "cg_f0_trees", $"{prefix}_f0_", $"{prefix}_single_f0_carts[] =", types)),
            new("mceptrees", Trees(root, voice, "cg_single_mcep_trees", $"{prefix}_single_mcep_", $"{prefix}_single_mcep_carts[] =", types)),
            new("durcart", DurationCart(root, voice)), new("durstats", DurationStats(root, voice)), new("states", PhoneStates(root, voice, types))
        ];
    }

    private static float[] Floats(string body) => [.. CSource.Items(body).Select(CSource.Float)];

    private static byte[] FloatBytes(float[] values)
    {
        SectionWriter writer = new();
        foreach (float value in values) writer.Float(value);
        return writer.ToArray();
    }

    /// <summary>The cluster vectors: <paramref name="frames"/> rows of <paramref name="channels"/> unsigned 16 bit values, in the order of the pointer table of the file.</summary>
    private static byte[] Params(string root, string voice, int frames, int channels)
    {
        string prefix = $"cmu_us_{voice}_single_";
        string text = Cs(root, voice, $"cmu_us_{voice}_cg_single_params.c");
        MatchCollection rows = Regex.Matches(text, $@"static const unsigned short {prefix}param_frame_(\d+)\[\] = \{{([^}}]*)\}};", RegexOptions.CultureInvariant);
        Check(rows.Count == frames, $"The file has {rows.Count} frames, the voice says {frames}.");
        byte[] bytes = new byte[frames * channels * 2];
        for (int f = 0; f < rows.Count; f++)
        {
            Check(int.Parse(rows[f].Groups[1].Value, CultureInfo.InvariantCulture) == f, $"Frame {f} is out of order.");
            string[] values = rows[f].Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Check(values.Length == channels, $"Frame {f} has {values.Length} values, expected {channels}.");
            for (int c = 0; c < channels; c++)
            {
                int value = int.Parse(values[c], NumberStyles.Integer, CultureInfo.InvariantCulture);
                Check(value is >= 0 and <= 65535, "A parameter value is out of range.");
                bytes[(f * channels + c) * 2] = (byte)value;
                bytes[((f * channels + c) * 2) + 1] = (byte)(value >> 8);
            }
        }

        // the pointer table says which frame is which: it must be the frames in order
        string[] table = [.. CSource.Items(CSource.Body(text, $"{prefix}model_vectors[] ="))];
        Check(table.Length == frames, $"The model vector table has {table.Length} entries.");
        for (int f = 0; f < table.Length; f++) Check(table[f] == $"{prefix}param_frame_{f}", $"The model vector table is out of order at {f}.");
        return bytes;
    }

    /// <summary>The trees of the voice in the order of the table of the file (which must be the order of the voice's types): a count, then the bytes of each tree.</summary>
    private static byte[] Trees(string root, string voice, string file, string treePrefix, string tableMarker, string[] types)
    {
        string stem = $"lang/cmu_us_{voice}/cmu_us_{voice}_{file}";
        string header = CSource.Strip(CSource.Read(Path.Combine(root, (stem + ".h").Replace('/', Path.DirectorySeparatorChar))));
        string code = CSource.Strip(CSource.Read(Path.Combine(root, (stem + ".c").Replace('/', Path.DirectorySeparatorChar))));
        Dictionary<string, string> defines = CSource.Defines(header);
        Dictionary<string, (int Kind, string Text)> values = Extract.CartValues(header);
        Dictionary<string, string> nodeBlocks = new(StringComparer.Ordinal), featureBlocks = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(code, @"static const cst_cart_node (\w+)_cart_nodes\[\] = \{(.*?)\}\};", RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            nodeBlocks[match.Groups[1].Value] = match.Groups[2].Value + "}";
        }

        foreach (Match match in Regex.Matches(code, @"static const char \* const (\w+)_feat_table\[\] = \{(.*?)NULL \};", RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            featureBlocks[match.Groups[1].Value] = match.Groups[2].Value;
        }

        string[] order = [.. CSource.Items(CSource.Body(code, tableMarker)).Where(item => !CSource.IsNull(item))];
        Check(order.Length == types.Length, $"The tree table has {order.Length} trees, the voice has {types.Length} types.");
        SectionWriter section = new();
        section.Int(order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            string name = order[i].TrimStart('&');
            Check(name.StartsWith(treePrefix, StringComparison.Ordinal) && name.EndsWith("_cart", StringComparison.Ordinal), $"Unexpected tree name {name}.");
            string symbol = name[..^"_cart".Length];
            Check(symbol[treePrefix.Length..] == types[i], $"Tree {i} is {symbol[treePrefix.Length..]} but the type is {types[i]}.");
            section.Bytes(Extract.CartBytes(nodeBlocks[symbol], defines, values, CSource.Items(featureBlocks[symbol] + "NULL")));
        }

        return section.ToArray();
    }

    private static byte[] DurationCart(string root, string voice)
    {
        string stem = $"cmu_us_{voice}_cg_durmodel";
        string header = Cs(root, voice, stem + ".h"), code = Cs(root, voice, stem + ".c");
        string nodes = CSource.Body(code, $"cmu_us_{voice}_dur_cart_nodes[] =");
        string featureTable = $"cmu_us_{voice}_dur_feat_table[] =";
        return Extract.CartBytes(nodes, CSource.Defines(header), Extract.CartValues(header), CSource.Items(CSource.Body(code, featureTable)));
    }

    /// <summary>The per state means and standard deviations of the duration (a <c>dur_stat</c> is a name and two floats), in the order of the table of the file.</summary>
    private static byte[] DurationStats(string root, string voice)
    {
        string code = Cs(root, voice, $"cmu_us_{voice}_cg_durmodel.c");
        Dictionary<string, (string Phone, string Mean, string Stddev)> stats = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(code, @"static const dur_stat (dur_state_\w+) = \{ ""(\w+)"", ([\d.eE+-]+), ([\d.eE+-]+) \};", RegexOptions.CultureInvariant))
        {
            stats[match.Groups[1].Value] = (match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
        }

        string[] order = [.. CSource.Items(CSource.Body(code, $"cmu_us_{voice}_dur_stats[] =")).Where(item => !CSource.IsNull(item))];
        Check(order.Length == stats.Count, $"The duration table has {order.Length} entries for {stats.Count} statistics.");
        SectionWriter section = new();
        section.Int(order.Length);
        foreach (string item in order)
        {
            (string phone, string mean, string stddev) = stats[item.TrimStart('&')];
            section.Text(phone);
            section.Float(CSource.Float(mean));
            section.Float(CSource.Float(stddev));
        }

        return section.ToArray();
    }

    /// <summary>The states of each phone: the phone name, then the names of its states, in the order of the table of the file.</summary>
    private static byte[] PhoneStates(string root, string voice, string[] types)
    {
        string code = Cs(root, voice, $"cmu_us_{voice}_cg_phonestate.c");
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(code, @"const char \* const (cmu_us_\w+_ps)\[\] = \{([^}]*)\};", RegexOptions.CultureInvariant))
        {
            rows[match.Groups[1].Value] = Names(match.Groups[2].Value);
        }

        string[] order = [.. CSource.Items(CSource.Body(code, $"cmu_us_{voice}_phone_states[] =")).Where(item => !CSource.IsNull(item))];
        Check(order.Length == rows.Count, $"The phone state table has {order.Length} entries for {rows.Count} phones.");
        SectionWriter section = new();
        section.Int(order.Length);
        foreach (string item in order)
        {
            string[] states = rows[item];
            Check(states.Length >= 2 && states.Skip(1).All(state => types.Contains(state)), $"The phone {states[0]} has unknown states.");
            section.TextList(states);
        }

        return section.ToArray();
    }
}
