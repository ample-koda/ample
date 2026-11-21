using System.Security.Cryptography;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Tests;

/// <summary>Level 0 of the Flite port (plan/speech/flite/PLAN.md): the converted data equals the data the compiled C program holds, and its own counts and invariants hold.</summary>
public sealed class FliteDataTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Every_table_equals_the_digest_of_what_the_compiled_C_program_holds()
    {
        IReadOnlyDictionary<string, byte[]> canon = DataCanon.All();
        string[] lines = File.ReadAllLines(Fixture("flite-data-digests.txt"));
        Assert.Equal(canon.Count, lines.Length);   // no table without a digest, no digest without a table
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ');
            Assert.Equal(3, parts.Length);
            Assert.True(canon.TryGetValue(parts[0], out byte[]? bytes), $"The loaders expose no table '{parts[0]}'.");
            Assert.Equal(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), bytes.LongLength);
            Assert.Equal(parts[2], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    [Fact]
    public void The_embedded_files_have_the_hash_of_the_generated_identity()
    {
        Assert.Equal(FliteDataIdentity.DataSha256, FliteDataFiles.ComputeSha256());
        Assert.Equal(FliteDataIdentity.Value, "flite/cmu_us_kal16/6c9f20dc+" + FliteDataIdentity.DataSha256[..16]);
        Assert.Equal("6c9f20dc915b17f5619340069889db0aa007fcdc", FliteDataIdentity.SourceCommit);
    }

    [Fact]
    public void The_voice_has_the_counts_and_the_order_the_C_data_states()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        Assert.Equal(18950, voice.NumFrames);
        Assert.Equal(16, voice.NumChannels);
        Assert.Equal(16000, voice.SampleRate);
        Assert.Equal(voice.NumFrames * voice.NumChannels, voice.Lpc.Length);
        Assert.Equal(voice.NumFrames + 1, voice.ResidualOffsets.Length);
        Assert.Equal(0u, voice.ResidualOffsets.Span[0]);
        Assert.Equal((uint)voice.Residual.Length, voice.ResidualOffsets.Span[^1]);
        for (int frame = 0; frame < voice.NumFrames; frame++) Assert.True(voice.ResidualSize(frame) > 0, $"frame {frame}");
        Assert.Equal(1623, voice.Diphones.Count);
        for (int i = 1; i < voice.Diphones.Count; i++) Assert.True(string.CompareOrdinal(voice.Diphones[i - 1].Name, voice.Diphones[i].Name) < 0, voice.Diphones[i].Name);
        Assert.Equal(0, voice.Diphones[0].StartPm);
        Assert.All(voice.Diphones, d => Assert.True(d.StartPm + d.PbPm + d.EndPm <= voice.NumFrames, d.Name));
        Assert.Equal(1.1f, voice.FloatParameter("duration_stretch"));
        Assert.Equal(95f, voice.FloatParameter("int_f0_target_mean"));
        Assert.Equal(11f, voice.FloatParameter("int_f0_target_stddev"));
        Assert.Equal("modified_lpc", voice.StringParameters["join_type"]);
        Assert.Equal("fixed", voice.StringParameters["resynth_type"]);
    }

    [Fact]
    public void Diphones_are_found_by_name_like_Flite_searches_them()
    {
        FliteVoiceData voice = FliteVoiceData.Instance;
        for (int i = 0; i < voice.Diphones.Count; i++) Assert.Equal(i, voice.FindDiphone(voice.Diphones[i].Name));
        Assert.Equal(-1, voice.FindDiphone("aa-zz-not"));
        Assert.Equal(-1, voice.FindDiphone(string.Empty));
        Assert.True(voice.FindDiphone("pau-hh") >= 0);
    }

    [Fact]
    public void The_lexicon_blob_holds_one_marker_per_entry_and_every_phone_code_decodes()
    {
        FliteLexiconData lexicon = FliteLexiconData.Instance;
        Assert.Equal(36963, lexicon.NumEntries);
        Assert.Equal(410522, lexicon.NumBytes);
        Assert.Equal(lexicon.NumBytes, lexicon.Data.Length);
        Assert.Equal(0, lexicon.Data.Span[0]);
        Assert.Equal(lexicon.NumEntries, lexicon.Data.Span.Count((byte)255));
        Assert.Equal(56, lexicon.PhoneTable.Count);
        Assert.Equal(FliteLexiconData.HuffmanSlots, lexicon.PhoneHuffman.Count);
        Assert.Equal(FliteLexiconData.HuffmanSlots, lexicon.EntryHuffman.Count);
        Assert.Null(lexicon.PhoneHuffman[0]);
        Assert.Null(lexicon.EntryHuffman[0]);
        Assert.Equal(42, lexicon.Addenda.Count);
        Assert.All(lexicon.PhoneHuffman.Where(code => code is not null), code => Assert.All(code!, b => Assert.InRange(b, 1, lexicon.PhoneTable.Count)));
        Assert.All(lexicon.Addenda, list => Assert.True(list.Count >= 1 && list[0].Length >= 2, "an addendum starts with its pos letter and word"));
    }

    [Fact]
    public void The_letter_to_sound_trees_stay_inside_their_rows()
    {
        FliteLtsData lts = FliteLtsData.Instance;
        Assert.Equal(25505, lts.RowCount);
        Assert.Equal(lts.RowCount * 6, lts.Model.Length);
        Assert.Equal(4, lts.ContextWindowSize);
        Assert.Equal(1, lts.ContextExtraFeatures);
        Assert.Equal(27, lts.LetterIndex.Count);
        Assert.Equal(0, lts.LetterIndex[0]);
        for (int i = 1; i < 26; i++) Assert.True(lts.LetterIndex[i] > lts.LetterIndex[i - 1], $"letter {i}");
        for (int letter = 0; letter < 26; letter++)
        {
            // the addresses are rows of the whole table and stay inside the tree of their letter
            int first = lts.LetterIndex[letter], end = letter == 25 ? lts.RowCount - 1 : lts.LetterIndex[letter + 1];
            for (int row = first; row < end; row++)
            {
                FliteLtsRule rule = lts.Rule(row);
                if (rule.Feature == 255)
                {
                    Assert.True(rule.Value < lts.PhoneTable.Count, $"leaf {row} names phone {rule.Value}");
                }
                else
                {
                    Assert.True(rule.True >= first && rule.True < end && rule.False >= first && rule.False < end, $"row {row} of letter {letter} jumps outside its tree");
                }
            }
        }

        Assert.Equal(default, lts.Rule(lts.RowCount - 1));
    }

    [Fact]
    public void Each_classification_tree_is_closed_and_its_features_exist()
    {
        FliteEnglishData english = FliteEnglishData.Instance;
        string[] names = ["us_durz_cart", "us_int_accent_cart", "us_int_tone_cart", "us_nums_cart", "us_phrasing_cart", "us_pos_cart"];
        Assert.Equal(names, english.CartNames.Order(StringComparer.Ordinal));
        int[] expected = [876, 150, 96, 98, 16, 368];
        int index = 0;
        foreach (string name in names)
        {
            FliteCart cart = english.Cart(name);
            Assert.Equal(expected[index++], cart.Nodes.Count);
            FliteCartNode last = cart.Nodes[^1];
            Assert.True(last.Feature == 255 && last.Kind == FliteCartValueKind.None, $"{name} does not end with its closing node");
            for (int i = 0; i < cart.Nodes.Count - 1; i++)
            {
                FliteCartNode node = cart.Nodes[i];
                Assert.NotEqual(FliteCartValueKind.None, node.Kind);
                if (node.Feature == 255)
                {
                    Assert.Equal(255, node.Operator);
                }
                else
                {
                    Assert.True(node.Feature < cart.Features.Count, $"{name} row {i} tests feature {node.Feature}");
                    Assert.True(node.NoNode > i && node.NoNode < cart.Nodes.Count, $"{name} row {i} has its no branch at {node.NoNode}");
                    Assert.True(node.Operator is 0 or 2, $"{name} row {i} uses operator {node.Operator}");
                }
            }
        }
    }

    [Fact]
    public void The_front_end_tables_have_the_sizes_the_C_files_state()
    {
        FliteEnglishData english = FliteEnglishData.Instance;
        Assert.Equal(50, english.Phoneset.NumPhones);
        Assert.Equal("pau", english.Phoneset.Silence);
        Assert.Equal(8, english.Phoneset.FeatureNames.Count);
        Assert.All(english.Phoneset.Rows.Take(english.Phoneset.NumPhones), row => Assert.Equal(-1, row[^1]));
        Assert.Equal(55, english.F0Terms.Count);
        Assert.Equal(41, english.DurationStats.Count);
        Assert.Equal(9, english.WordLists.Count);
        Assert.Equal(17, english.Regexes.Count);
        Assert.All(english.Regexes, regex => Assert.Equal(0x9C, regex.Program.Span[0]));   // the magic byte of Spencer's programs (octal 0234)
        Assert.Equal(203, english.WordStartMachine.Transitions.Count);
        Assert.Equal(454, english.WordEndMachine.Transitions.Count);
        Assert.Equal(128, english.WordStartMachine.VocabularySize);
    }
}
