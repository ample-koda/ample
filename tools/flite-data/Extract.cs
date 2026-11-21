using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FliteData;

/// <summary>Parses the generated C files of the pinned Flite source into the sections of the four .bin files.
/// Every table is counted and checked against what the C files state about themselves.</summary>
internal static class Extract
{
    private static readonly string[] CartNames = ["us_durz_cart", "us_int_accent_cart", "us_int_tone_cart", "us_nums_cart", "us_phrasing_cart", "us_pos_cart"];

    private static string Cs(string root, string relative) => CSource.Strip(CSource.Read(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    private static int[] ToArray(List<int> list) => [.. list];

    public static List<KeyValuePair<string, byte[]>> Voice(string root)
    {
        string diphone = Cs(root, "lang/cmu_us_kal16/cmu_us_kal16_diphone.c");
        Match header = Regex.Match(diphone, @"#endif\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\}", RegexOptions.CultureInvariant);
        Check(header.Success, "The sts header was not found.");
        int frames = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture), channels = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
        int rate = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
        SectionWriter sts = new();
        sts.Int(frames);
        sts.Int(channels);
        sts.Int(rate);
        sts.Float(CSource.Float(header.Groups[4].Value));
        sts.Float(CSource.Float(header.Groups[5].Value));

        List<int> lpc = CSource.Ints(CSource.Body(Cs(root, "lang/cmu_us_kal16/cmu_us_kal16_lpc.c"), "cmu_us_kal16_lpc[] ="));
        Check(lpc[^1] == 0, "The lpc array does not end with its zero.");
        lpc.RemoveAt(lpc.Count - 1);
        Check(lpc.Count == frames * channels, $"lpc has {lpc.Count} values, expected {frames * channels}.");
        SectionWriter lpcSection = new();
        foreach (int value in lpc)
        {
            Check(value is >= 0 and <= 65535, "An lpc value is out of range.");
            lpcSection.UShort((ushort)value);
        }

        List<int> resi = CSource.Ints(CSource.Body(Cs(root, "lang/cmu_us_kal16/cmu_us_kal16_residx.c"), "cmu_us_kal16_resi[] ="));
        Check(resi.Count == frames + 1, $"resi has {resi.Count} offsets, expected {frames + 1}.");
        SectionWriter resiSection = new();
        for (int i = 0; i < resi.Count; i++)
        {
            Check(i == 0 ? resi[0] == 0 : resi[i] > resi[i - 1], "The residual offsets do not grow.");
            resiSection.UInt((uint)resi[i]);
        }

        List<int> res = CSource.Ints(CSource.Body(Cs(root, "lang/cmu_us_kal16/cmu_us_kal16_res.c"), "cmu_us_kal16_res[] ="));
        Check(res[^1] == 0, "The residual array does not end with its zero.");
        res.RemoveAt(res.Count - 1);
        Check(res.Count == resi[^1], $"The residual has {res.Count} bytes, the index ends at {resi[^1]}.");
        byte[] resBytes = new byte[res.Count];
        for (int i = 0; i < resBytes.Length; i++)
        {
            Check(res[i] is >= 0 and <= 255, "A residual byte is out of range.");
            resBytes[i] = (byte)res[i];
        }

        string entryPattern = "\\{\\s*\"([^\"]*)\"\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*\\}";
        MatchCollection entries = Regex.Matches(CSource.Body(diphone, "cmu_us_kal16_index[] ="), entryPattern, RegexOptions.CultureInvariant);
        Match count = Regex.Match(diphone, "cmu_us_kal16_db = \\{\\s*\"cmu_us_kal16\"\\s*,\\s*(\\d+)", RegexOptions.CultureInvariant);
        Check(count.Success && entries.Count == int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture), $"The diphone index has {entries.Count} entries.");
        SectionWriter index = new();
        index.Int(entries.Count);
        string? previous = null;
        foreach (Match entry in entries)
        {
            string name = entry.Groups[1].Value;
            Check(previous is null || string.CompareOrdinal(previous, name) < 0, "The diphone names are not sorted by byte order.");
            previous = name;
            index.Text(name);
            index.UShort(ushort.Parse(entry.Groups[2].Value, CultureInfo.InvariantCulture));
            index.Byte(byte.Parse(entry.Groups[3].Value, CultureInfo.InvariantCulture));
            index.Byte(byte.Parse(entry.Groups[4].Value, CultureInfo.InvariantCulture));
        }

        SectionWriter parameters = new();
        List<(string Name, int Kind, string Value)> found = [];
        foreach (Match set in Regex.Matches(Cs(root, "lang/cmu_us_kal16/cmu_us_kal16.c"), @"feat_set_(float|int|string)\(v->features,""(\w+)"",\s*([^)]+?)\s*\)", RegexOptions.CultureInvariant))
        {
            string value = set.Groups[3].Value;
            int kind = set.Groups[1].Value == "float" ? 0 : set.Groups[1].Value == "int" ? 1 : 2;
            if (kind != 2 && !Regex.IsMatch(value, @"^-?[\d.]+$", RegexOptions.CultureInvariant)) continue;
            found.Add((set.Groups[2].Value, kind, value));
        }

        parameters.Int(found.Count);
        foreach ((string name, int kind, string value) in found)
        {
            parameters.Text(name);
            parameters.Byte((byte)kind);
            if (kind == 0) parameters.Float(CSource.Float(value));
            else if (kind == 1) parameters.Int(CSource.Number(value));
            else parameters.Text(CSource.DecodeText(value));
        }

        return
        [
            new("sts", sts.ToArray()), new("lpc", lpcSection.ToArray()), new("res", resBytes), new("resi", resiSection.ToArray()),
            new("index", index.ToArray()), new("params", parameters.ToArray())
        ];
    }

    public static List<KeyValuePair<string, byte[]>> Lexicon(string root)
    {
        string data = Cs(root, "lang/cmulex/cmu_lex_data_raw.c");
        List<int> bytes = CSource.Ints(data);
        int numBytes = int.Parse(Regex.Match(CSource.Read(Path.Combine(root, "lang", "cmulex", "cmu_lex_num_bytes.c")), @"\d+", RegexOptions.CultureInvariant).Value, CultureInfo.InvariantCulture);
        Check(bytes.Count + 1 == numBytes, $"The lexicon data has {bytes.Count + 1} bytes, the file says {numBytes}.");
        byte[] blob = new byte[numBytes];
        for (int i = 0; i < bytes.Count; i++)
        {
            Check(bytes[i] is >= 0 and <= 255, "A lexicon byte is out of range.");
            blob[i + 1] = (byte)bytes[i];
        }

        string entries = Cs(root, "lang/cmulex/cmu_lex_entries.c");
        int numEntries = int.Parse(Regex.Match(entries, @"cmu_lex_num_entries\s*=\s*(\d+)", RegexOptions.CultureInvariant).Groups[1].Value, CultureInfo.InvariantCulture);
        List<string> phones = [.. CSource.Items(CSource.Body(entries, "cmu_lex_phone_table[57] =")).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)];
        SectionWriter info = new();
        info.Int(numEntries);
        info.Int(numBytes);
        SectionWriter phoneTable = new();
        phoneTable.TextList(phones);

        SectionWriter Huffman(string file)
        {
            List<string> items = CSource.Items(Cs(root, file));
            Check(items.Count <= 255, "A Huffman table is too long.");
            SectionWriter section = new();
            section.Int(items.Count);
            foreach (string item in items) section.Bytes(CSource.DecodeStrings(item));
            return section;
        }

        SectionWriter addenda = new();
        SortedDictionary<int, List<string>> lists = [];
        foreach (Match match in Regex.Matches(Cs(root, "lang/cmulex/cmu_lex.c"), @"static const char \* const addenda(\d+)\[\] = \{(.*?)\};", RegexOptions.CultureInvariant))
        {
            lists[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = [.. CSource.Items(match.Groups[2].Value).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)];
        }

        Check(lists.Count == 42 && lists.Keys.SequenceEqual(Enumerable.Range(0, 42)), "The addenda are not the 42 expected lists.");
        addenda.Int(lists.Count);
        foreach (List<string> list in lists.Values) addenda.TextList(list);
        return
        [
            new("data", blob), new("info", info.ToArray()), new("phones", phoneTable.ToArray()),
            new("phone-huff", Huffman("lang/cmulex/cmu_lex_phones_huff_table.c").ToArray()), new("entry-huff", Huffman("lang/cmulex/cmu_lex_entries_huff_table.c").ToArray()),
            new("addenda", addenda.ToArray())
        ];
    }

    public static List<KeyValuePair<string, byte[]>> Lts(string root)
    {
        string rules = Cs(root, "lang/cmulex/cmu_lts_rules.c");
        List<string> phones = [.. CSource.Items(CSource.Body(rules, "cmu_lts_phone_table[76] =")).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)];
        List<string> letters = [.. CSource.Items(CSource.Body(rules, "cmu_lts_letter_table[30] =")).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)];
        List<int> letterIndex = CSource.Ints(CSource.Body(rules, "cmu_lts_letter_index[27] ="));
        Check(phones.Count == 75 && letters.Count == 29 && letterIndex.Count == 27, "An LTS table has the wrong size.");

        Dictionary<string, string> defines = CSource.Defines(Cs(root, "lang/cmulex/cmu_lts_model.h"));
        List<byte> model = [];
        foreach (string item in CSource.Items(CSource.Body(Cs(root, "lang/cmulex/cmu_lts_model.c"), "cmu_lts_model[] =")))
        {
            if (defines.TryGetValue(item, out string? macro))
            {
                foreach (string part in CSource.Items(macro)) model.Add(checked((byte)CSource.Number(part)));
            }
            else
            {
                model.Add(checked((byte)CSource.Number(item)));
            }
        }

        Check(model.Count % 6 == 0, "The LTS model is not made of 6-byte rows.");
        Check(model.Skip(model.Count - 6).All(b => b == 0), "The LTS model does not end with its zero row.");
        Match window = Regex.Match(Cs(root, "lang/cmulex/cmu_lex.c"), @"context_window_size = (\d+);\s*cmu_lts_rules\.context_extra_feats = (\d+);", RegexOptions.CultureInvariant);
        Check(window.Success, "The LTS window was not found.");
        SectionWriter info = new();
        info.Int(model.Count / 6);
        info.Int(int.Parse(window.Groups[1].Value, CultureInfo.InvariantCulture));
        info.Int(int.Parse(window.Groups[2].Value, CultureInfo.InvariantCulture));
        SectionWriter index = new();
        index.Int(letterIndex.Count);
        foreach (int value in letterIndex) index.UShort(checked((ushort)value));
        SectionWriter phoneSection = new(), letterSection = new();
        phoneSection.TextList(phones);
        letterSection.TextList(letters);
        return [new("info", info.ToArray()), new("model", [.. model]), new("letter-index", index.ToArray()), new("phones", phoneSection.ToArray()), new("letters", letterSection.ToArray())];
    }

    /// <summary>The CART kinds of a node value: 0 none, 1 string (an index into the string pool of the tree), 2 float (the bits).</summary>
    private static byte[] Cart(string root, string name)
    {
        string stem = $"lang/usenglish/{name}";
        string header = Cs(root, stem + ".h"), code = Cs(root, stem + ".c");
        string features = Regex.Match(code, @"(\w+)_feat_table\[\] =", RegexOptions.CultureInvariant).Value;
        return CartBytes(code, CSource.Defines(header), CartValues(header), CSource.Items(CSource.Body(code, features)));
    }

    /// <summary>The named values of a tree file: <c>DEF_STATIC_CONST_VAL_STRING</c> and <c>_FLOAT</c> lines of its header (kind 1 string, kind 2 float, with the literal text).</summary>
    internal static Dictionary<string, (int Kind, string Text)> CartValues(string header)
    {
        Dictionary<string, (int Kind, string Text)> values = [];
        foreach (Match match in Regex.Matches(header, @"DEF_STATIC_CONST_VAL_(STRING|FLOAT)\((val_\d+),(.+)\);", RegexOptions.CultureInvariant))
        {
            values[match.Groups[2].Value] = (match.Groups[1].Value == "STRING" ? 1 : 2, match.Groups[3].Value.Trim());
        }

        return values;
    }

    /// <summary>One tree as bytes: the node rows (feature, operator, no branch, value kind and payload), the pool of the string values and the names of the features.
    /// <paramref name="nodesText"/> holds the <c>{ feature, CST_CART_OP_x, no, value }</c> rows of that one tree.</summary>
    internal static byte[] CartBytes(string nodesText, IReadOnlyDictionary<string, string> defines, IReadOnlyDictionary<string, (int Kind, string Text)> values, IReadOnlyList<string> featureItems)
    {
        List<string> pool = [];
        Dictionary<string, int> poolIndex = new(StringComparer.Ordinal);
        SectionWriter nodes = new();
        MatchCollection matches = Regex.Matches(nodesText, @"\{\s*(\d+),\s*CST_CART_OP_(\w+),\s*(\w+),\s*(?:\(cst_val \*\)&(val_\d+)|0)\s*\}", RegexOptions.CultureInvariant);
        if (matches.Count == 0) throw new InvalidDataException("A tree has no nodes.");
        nodes.Int(matches.Count);
        foreach (Match node in matches)
        {
            nodes.Byte(byte.Parse(node.Groups[1].Value, CultureInfo.InvariantCulture));
            string op = node.Groups[2].Value;
            nodes.Byte(op switch { "IS" => 0, "IN" => 1, "LESS" => 2, "GREATER" => 3, "MATCHES" => 4, "EQUALS" => 5, "NONE" => 255, _ => throw new InvalidDataException($"CART op {op}.") });
            nodes.UShort(checked((ushort)CSource.Eval(node.Groups[3].Value, defines)));
            if (!node.Groups[4].Success)
            {
                nodes.Byte(0);
                nodes.UInt(0);
                continue;
            }

            (int kind, string text) = values[node.Groups[4].Value];
            nodes.Byte((byte)kind);
            if (kind == 1)
            {
                string value = CSource.DecodeText(text);
                if (!poolIndex.TryGetValue(value, out int at))
                {
                    at = pool.Count;
                    pool.Add(value);
                    poolIndex[value] = at;
                }

                nodes.UInt((uint)at);
            }
            else
            {
                nodes.UInt(BitConverter.SingleToUInt32Bits(CSource.Float(text)));
            }
        }

        SectionWriter result = new();
        result.Raw(nodes.ToArray());
        result.TextList(pool);
        result.TextList([.. featureItems.Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)]);
        return result.ToArray();
    }

    public static List<KeyValuePair<string, byte[]>> English(string root)
    {
        List<KeyValuePair<string, byte[]>> sections = [];
        foreach (string name in CartNames) sections.Add(new("cart:" + name, Cart(root, name)));

        string phoneset = Cs(root, "lang/usenglish/us_phoneset.c");
        Dictionary<string, string> featvals = [];
        foreach (Match match in Regex.Matches(phoneset, @"DEF_STATIC_CONST_VAL_STRING\((featval_\d+),(.+)\);", RegexOptions.CultureInvariant))
        {
            featvals[match.Groups[1].Value] = CSource.DecodeText(match.Groups[2].Value.Trim());
        }

        Dictionary<string, int[]> rows = [];
        foreach (Match match in Regex.Matches(phoneset, @"static const int (us_fv_\d+)\[\] = \{([^}]*)\};", RegexOptions.CultureInvariant))
        {
            rows[match.Groups[1].Value] = ToArray(CSource.Ints(match.Groups[2].Value));
        }

        SectionWriter ps = new();
        ps.TextList([.. CSource.Items(CSource.Body(phoneset, "us_featnames[] =")).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)]);
        ps.TextList([.. CSource.Items(CSource.Body(phoneset, "us_phonenames[] =")).Where(item => !CSource.IsNull(item)).Select(CSource.DecodeText)]);
        List<string> valueNames = [.. CSource.Items(CSource.Body(phoneset, "us_featvals[] =")).Where(item => !CSource.IsNull(item))];
        ps.TextList([.. valueNames.Select(item => featvals[Regex.Match(item, @"&(featval_\d+)", RegexOptions.CultureInvariant).Groups[1].Value])]);
        List<string> order = [.. CSource.Items(CSource.Body(phoneset, "us_fvtable[] =")).Where(item => !CSource.IsNull(item))];
        ps.Int(order.Count);
        foreach (string row in order)
        {
            ps.Int(rows[row].Length);
            foreach (int value in rows[row]) ps.Int(value);
        }

        Match set = Regex.Match(phoneset, @"us_phoneset = \{\s*""us"",\s*us_featnames,\s*us_featvals,\s*us_phonenames,\s*""(\w+)"",\s*(\d+),", RegexOptions.CultureInvariant);
        Check(set.Success, "The phoneset struct was not found.");
        ps.Text(set.Groups[1].Value);
        ps.Int(int.Parse(set.Groups[2].Value, CultureInfo.InvariantCulture));
        sections.Add(new("phoneset", ps.ToArray()));

        SectionWriter f0 = new();
        List<string> terms = [.. CSource.Items(CSource.Body(Cs(root, "lang/usenglish/us_f0lr.c"), "f0_lr_terms[] ="))];
        terms.RemoveAt(terms.Count - 1);   // { 0, 0, 0, 0, 0 }
        f0.Int(terms.Count);
        foreach (string term in terms)
        {
            List<string> parts = CSource.Items(term[1..^1]);
            Check(parts.Count == 5, "An F0 term does not have 5 fields.");
            f0.Text(CSource.IsNull(parts[0]) ? null : CSource.DecodeText(parts[0]));
            f0.Float(CSource.Float(parts[1]));
            f0.Float(CSource.Float(parts[2]));
            f0.Float(CSource.Float(parts[3]));
            f0.Text(CSource.IsNull(parts[4]) ? null : CSource.DecodeText(parts[4]));
        }

        sections.Add(new("f0lr", f0.ToArray()));

        string durText = Cs(root, "lang/usenglish/us_dur_stats.c");
        Dictionary<string, (string Phone, float Mean, float Stddev)> stats = [];
        foreach (Match match in Regex.Matches(durText, @"static const dur_stat (dur_stat_\w+) = \{ ""(\w+)"", ([\d.]+), ([\d.]+) \};", RegexOptions.CultureInvariant))
        {
            stats[match.Groups[1].Value] = (match.Groups[2].Value, CSource.Float(match.Groups[3].Value), CSource.Float(match.Groups[4].Value));
        }

        List<string> statOrder = [.. CSource.Items(CSource.Body(durText, "us_dur_stats[] =")).Where(item => !CSource.IsNull(item)).Select(item => item.TrimStart('&'))];
        SectionWriter dur = new();
        dur.Int(statOrder.Count);
        foreach (string key in statOrder)
        {
            dur.Text(stats[key].Phone);
            dur.Float(stats[key].Mean);
            dur.Float(stats[key].Stddev);
        }

        sections.Add(new("dur-stats", dur.ToArray()));

        string gposText = Cs(root, "lang/usenglish/us_gpos.c");
        Dictionary<string, string> words = [];
        foreach (Match match in Regex.Matches(gposText, @"DEF_STATIC_CONST_VAL_STRING\((gpos_\w+),(.+)\);", RegexOptions.CultureInvariant))
        {
            words[match.Groups[1].Value] = CSource.DecodeText(match.Groups[2].Value.Trim());
        }

        SectionWriter gpos = new();
        List<string> listNames = [.. CSource.Items(CSource.Body(gposText, "us_gpos[] =")).Where(item => !CSource.IsNull(item))];
        gpos.Int(listNames.Count);
        foreach (string listName in listNames)
        {
            List<string> refs = [.. CSource.Items(CSource.Body(gposText, $"{listName}[] =")).Where(item => !CSource.IsNull(item))];
            List<string> members = [.. refs.Select(item => words[Regex.Match(item, @"&(gpos_\w+)", RegexOptions.CultureInvariant).Groups[1].Value])];
            gpos.Text(listName);
            gpos.TextList(members);
        }

        sections.Add(new("gpos", gpos.ToArray()));
        sections.Add(new("regex", Regexes(root)));
        sections.Add(new("aswd", Aswd(root)));
        return sections;
    }

    private static byte[] Regexes(string root)
    {
        string header = Cs(root, "lang/usenglish/us_regexes.h");
        Dictionary<string, string> patterns = [];
        string makeText = CSource.Read(Path.Combine(root, "lang", "usenglish", "make_us_regexes"));
        foreach (Match match in Regex.Matches(makeText, @"^\$COMPILE_REGEX (\w+) ""(.*)""\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            // the shell reads the double quotes: \\ \$ \" \` lose one backslash, every other backslash stays
            patterns[match.Groups[1].Value] = Regex.Replace(match.Groups[2].Value, @"\\([\\$""`])", "$1", RegexOptions.CultureInvariant);
        }

        MatchCollection found = Regex.Matches(
            header,
            @"static const unsigned char (\w+)_rxprog\[\] = \{([^}]*)\};\s*static const cst_regex \w+_rx = \{\s*(\d+),\s*(\d+),\s*NULL,\s*(\d+),\s*(\d+),",
            RegexOptions.CultureInvariant);
        SectionWriter section = new();
        section.Int(found.Count);
        foreach (Match match in found)
        {
            string name = match.Groups[1].Value;
            List<int> program = CSource.Ints(match.Groups[2].Value);
            Check(program.Count == int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture), $"Regex {name} has {program.Count} program bytes.");
            section.Text(name);
            section.Text(patterns[name]);
            section.Int(int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
            section.Int(int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
            section.Int(int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture));
            section.Bytes([.. program.Select(value => checked((byte)value))]);
        }

        return section.ToArray();
    }

    private static byte[] Aswd(string root)
    {
        string text = Cs(root, "lang/usenglish/us_aswd.c");
        Dictionary<string, string> defines = CSource.Defines(text);
        SectionWriter section = new();
        foreach (string fsm in new[] { "fsm_aswdP", "fsm_aswdS" })
        {
            Match shape = Regex.Match(text, $@"static const cst_fsm {fsm} = \{{\s*(\d+),\s*(\d+),", RegexOptions.CultureInvariant);
            Check(shape.Success, $"The {fsm} struct was not found.");
            List<string> transitions = [.. CSource.Items(CSource.Body(text, $"{fsm}_trans[")).Where(item => !CSource.IsNull(item))];
            Check(transitions.Count == int.Parse(shape.Groups[2].Value, CultureInfo.InvariantCulture), $"{fsm} has {transitions.Count} transitions.");
            section.Int(int.Parse(shape.Groups[1].Value, CultureInfo.InvariantCulture));
            section.Int(transitions.Count);
            foreach (string transition in transitions) section.UShort(checked((ushort)CSource.Eval(defines[transition], defines)));
        }

        return section.ToArray();
    }

    public static string Describe(IEnumerable<KeyValuePair<string, byte[]>> sections) => string.Join(", ", sections.Select(s => $"{s.Key} {s.Value.Length.ToString(CultureInfo.InvariantCulture)}"));

    public static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
