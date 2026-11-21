using System.Globalization;
using System.Security.Cryptography;
using Ample.Speech.Flite.Cg;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Level 0 of the clustergen voices (plan/speech/flite/cg-architecture.md, milestone C1): the converted data equals the data the compiled C program holds, and its own counts hold.</summary>
public sealed class CgDataTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static readonly Dictionary<string, FliteCgVoiceData> Voices = LoadAll();

    private static Dictionary<string, FliteCgVoiceData> LoadAll()
    {
        Dictionary<string, FliteCgVoiceData> voices = new(StringComparer.Ordinal);
        foreach (string voice in new[] { "slt", "rms", "awb" }) voices[voice] = FliteCgVoiceData.Load(FixturePath(Path.Combine("cg", $"flite-cg-{voice}.bin")));
        return voices;
    }

    [Fact]
    public void Every_table_of_every_voice_equals_the_digest_of_what_the_compiled_C_program_holds()
    {
        Dictionary<string, byte[]> canon = new(StringComparer.Ordinal);
        foreach ((string voice, FliteCgVoiceData data) in Voices)
        {
            foreach (KeyValuePair<string, byte[]> table in CgDataCanon.All(voice, data)) canon[table.Key] = table.Value;
        }

        string[] lines = File.ReadAllLines(FixturePath("flite-cg-data-digests.txt"));
        Assert.Equal(canon.Count, lines.Length);   // no table without a digest, no digest without a table
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ');
            Assert.Equal(3, parts.Length);
            Assert.True(canon.TryGetValue(parts[0], out byte[]? bytes), $"The loader exposes no table '{parts[0]}'.");
            Assert.Equal(long.Parse(parts[1], CultureInfo.InvariantCulture), bytes.LongLength);
            Assert.Equal(parts[2], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("slt", 8873, 5186, 3952, 17746, 172f, 27f, false)]
    [InlineData("rms", 10446, 5202, 4700, 20892, 98f, 24f, true)]
    [InlineData("awb", 8855, 5176, 3900, 17710, 132f, 25f, false)]
    public void The_counts_are_those_that_the_C_files_state_about_themselves(string voice, int frames, int durNodes, int f0Nodes, int mcepNodes, float f0Mean, float f0Stddev, bool spamf0)
    {
        FliteCgVoiceData data = Voices[voice];
        Assert.Equal("cmu_us_" + voice, data.Name);
        Assert.Equal(16000, data.SampleRate);
        Assert.Equal(114, data.NumChannels);
        Assert.Equal(frames, data.NumFrames);
        Assert.Equal(frames * 114, data.Parameters.Length);
        Assert.Equal(124, data.Types.Count);
        Assert.Equal(124, data.F0Trees.Count);
        Assert.Equal(124, data.McepTrees.Count);
        Assert.Equal(durNodes, data.DurationCart.Nodes.Count);
        Assert.Equal(f0Nodes, data.F0Trees.Sum(tree => tree.Nodes.Count));
        Assert.Equal(mcepNodes, data.McepTrees.Sum(tree => tree.Nodes.Count));
        Assert.Equal(f0Mean, data.F0Mean);
        Assert.Equal(f0Stddev, data.F0Stddev);
        Assert.Equal(spamf0, data.Spamf0);
        Assert.Equal(0.005f, data.FrameAdvance);
        Assert.True(data.DoMlpg);
        Assert.True(data.MixedExcitation);
        Assert.False(data.MultiModel);
        Assert.Equal(0.42f, data.MlsaAlpha);
        Assert.Equal(0.4f, data.MlsaBeta);
        Assert.Equal(1.5f, data.Gain);
        Assert.Equal(5, data.MixedExcitationFilterCount);
        Assert.Equal(48, data.MixedExcitationOrder);
        Assert.Equal(new[] { -0.5f, 0f, 0.5f }, data.DynamicWindow.ToArray());
        Assert.Equal(1, data.ModelShape);
    }

    [Fact]
    public void The_trees_follow_the_order_of_the_types_and_the_states_belong_to_phones()
    {
        foreach (FliteCgVoiceData data in Voices.Values)
        {
            for (int i = 0; i < data.Types.Count; i++)
            {
                Assert.Equal(data.Types[i], data.F0Trees[i].Name);
                Assert.Equal(data.Types[i], data.McepTrees[i].Name);
            }

            // every cluster number that a tree can answer exists
            foreach (Data.FliteCart tree in data.McepTrees)
            {
                foreach (Data.FliteCartNode node in tree.Nodes.Where(n => n.Operator == 255 && n.Kind == Data.FliteCartValueKind.Number))
                {
                    Assert.InRange(node.Number, 0f, data.NumFrames - 1);
                    Assert.Equal(MathF.Floor(node.Number), node.Number);
                }
            }

            // each phone has its name and then states that are types; the duration statistics cover every state
            HashSet<string> stats = [.. data.DurationStats.Select(stat => stat.Phone)];
            foreach (IReadOnlyList<string> phone in data.PhoneStates)
            {
                Assert.True(phone.Count >= 2);
                foreach (string state in phone.Skip(1))
                {
                    Assert.Contains(state, data.Types);
                    Assert.Contains(state, stats);
                }
            }
        }
    }

    [Fact]
    public void The_files_have_the_hash_of_the_generated_identity_and_are_not_part_of_the_assembly()
    {
        Assert.Equal("6c9f20dc915b17f5619340069889db0aa007fcdc", FliteCgDataIdentity.SourceCommit);
        foreach (string voice in Voices.Keys)
        {
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath(Path.Combine("cg", $"flite-cg-{voice}.bin"))))).ToLowerInvariant();
            Assert.Equal(FliteCgDataIdentity.Sha256[voice], hash);
        }

    }

    [Fact]
    public void The_licence_of_the_recordings_is_in_the_licence_folder_with_its_three_holders_and_the_voices_are_named_as_modified()
    {
        string folder = Path.GetDirectoryName(typeof(CgDataTests).Assembly.Location)!;
        while (folder is not null && !File.Exists(Path.Combine(folder, "THIRD-PARTY-NOTICES.md"))) folder = Path.GetDirectoryName(folder)!;
        Assert.NotNull(folder);
        string licence = File.ReadAllText(Path.Combine(folder!, "LICENSES", "cmu-arctic.txt"));
        foreach (string holder in new[] { "Carnegie Mellon University", "Alan W Black", "Kevin A. Lenzo", "John Kominek" }) Assert.Contains(holder, licence, StringComparison.Ordinal);
        foreach (string voice in new[] { "slt", "rms", "awb" }) Assert.Contains(voice, licence, StringComparison.Ordinal);
        Assert.Contains("All voice talents have signed a waiver", licence, StringComparison.Ordinal);
        Assert.Contains("rms is covered by the Flite distribution terms", licence, StringComparison.Ordinal);
        Assert.Contains("not endorsed by Carnegie Mellon University", licence, StringComparison.Ordinal);
        Assert.Contains("cmu-arctic.txt", File.ReadAllText(Path.Combine(folder!, "THIRD-PARTY-NOTICES.md")), StringComparison.Ordinal);
    }
}
