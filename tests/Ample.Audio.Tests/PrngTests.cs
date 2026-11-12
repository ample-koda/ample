using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Audio.Random;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

[Trait("Category", "Prng")]
public sealed class PrngTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec-tests")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("AMPLE folder not found");
    }

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "spec-tests", "prng", "vectors.json")));

    private static ulong U(string s) => ulong.Parse(s, CultureInfo.InvariantCulture);

    private static object[] Path_(JsonElement path) => [.. path.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? (object)p.GetString()! : U(p.GetProperty("n").GetString()!))];

    [Fact]
    public void The_version_is_one()
    {
        using JsonDocument v = Vectors();
        Assert.Equal(RngVersion.Current, v.RootElement.GetProperty("rngVersion").GetInt32());
        Assert.Equal(1, RngVersion.Current);
    }

    [Fact]
    public void SplitMix64_equals_the_published_reference_outputs()
    {
        using JsonDocument v = Vectors();
        JsonElement sm = v.RootElement.GetProperty("splitmix64");
        SplitMix64 g = new(U(sm.GetProperty("seed").GetString()!));
        foreach (JsonElement expected in sm.GetProperty("outputs").EnumerateArray())
        {
            Assert.Equal(U(expected.GetString()!), g.Next());
        }
    }

    [Fact]
    public void SipHash24_equals_the_paper_vector()
    {
        using JsonDocument v = Vectors();
        JsonElement s = v.RootElement.GetProperty("siphash24");
        byte[] input = [.. Enumerable.Range(0, 15).Select(i => (byte)i)];
        Assert.Equal(s.GetProperty("expectedHex").GetString(), SipHash24.Hash(U(s.GetProperty("k0").GetString()!), U(s.GetProperty("k1").GetString()!), input).ToString("x16", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Xoshiro_outputs_and_digests_equal_the_vectors()
    {
        using JsonDocument v = Vectors();
        foreach (JsonElement e in v.RootElement.GetProperty("xoshiro256ss").EnumerateArray())
        {
            ulong seed = U(e.GetProperty("seed").GetString()!);
            StableRng rng = new(seed);
            foreach (JsonElement expected in e.GetProperty("first64").EnumerateArray())
            {
                Assert.Equal(U(expected.GetString()!), rng.Next());
            }

            StableRng again = new(seed);
            byte[] buffer = new byte[8 * 100000];
            for (int i = 0; i < 100000; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(i * 8), again.Next());
            }

            Assert.Equal(e.GetProperty("sha256Of100000").GetString(), Convert.ToHexStringLower(SHA256.HashData(buffer)));
        }
    }

    [Fact]
    public void Bounded_values_unit_bits_shuffle_and_derivation_equal_the_vectors()
    {
        using JsonDocument v = Vectors();
        foreach (JsonElement e in v.RootElement.GetProperty("nextUInt").EnumerateArray())
        {
            StableRng rng = new(U(e.GetProperty("seed").GetString()!));
            ulong bound = U(e.GetProperty("bound").GetString()!);
            foreach (JsonElement expected in e.GetProperty("first32").EnumerateArray())
            {
                Assert.Equal(U(expected.GetString()!), rng.NextUInt(bound));
            }
        }

        JsonElement unit = v.RootElement.GetProperty("nextUnitBits");
        StableRng unitRng = new(U(unit.GetProperty("seed").GetString()!));
        foreach (JsonElement expected in unit.GetProperty("first16").EnumerateArray())
        {
            Assert.Equal(U(expected.GetString()!), unitRng.NextUnitBits());
        }

        JsonElement shuffle = v.RootElement.GetProperty("shuffle");
        List<int> items = [.. Enumerable.Range(0, shuffle.GetProperty("n").GetInt32())];
        new StableRng(U(shuffle.GetProperty("seed").GetString()!)).Shuffle(items);
        Assert.Equal(shuffle.GetProperty("result").EnumerateArray().Select(x => x.GetInt32()), items);

        foreach (JsonElement d in v.RootElement.GetProperty("derive").EnumerateArray())
        {
            ulong master = U(d.GetProperty("master").GetString()!);
            object[] path = Path_(d.GetProperty("path"));
            Assert.Equal(U(d.GetProperty("u64").GetString()!), SeedDeriver.Derive(master, path));
            Assert.Equal(uint.Parse(d.GetProperty("u32").GetString()!, CultureInfo.InvariantCulture), SeedDeriver.DeriveSeed32(master, path));
        }
    }

    [Fact]
    public void Bounded_values_are_unbiased_and_a_zero_bound_is_refused()
    {
        StableRng rng = new(2026);
        int[] buckets = new int[16];
        const int Samples = 320000;
        for (int i = 0; i < Samples; i++)
        {
            buckets[(int)rng.NextUInt(16)]++;
        }

        double expected = Samples / 16.0;
        double chi = buckets.Sum(b => (b - expected) * (b - expected) / expected);
        Assert.True(chi < 50, $"chi-square {chi:F1} for 15 degrees of freedom is too high");
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextUInt(0));
        for (int i = 0; i < 2000; i++)
        {
            ulong x = rng.NextUInt(3);
            Assert.True(x < 3);
            double u = rng.NextUnit();
            Assert.True(u >= 0 && u < 1);
        }
    }

    [Fact]
    public void Derived_seeds_do_not_collide_and_depend_on_every_part()
    {
        HashSet<ulong> seen = [];
        for (int i = 0; i < 200000; i++)
        {
            Assert.True(seen.Add(SeedDeriver.Derive(1, "block", "b" + i.ToString(CultureInfo.InvariantCulture), "seed", "lm_seed")), "collision at " + i);
        }

        ulong baseSeed = SeedDeriver.Derive(5, "a", "b");
        Assert.NotEqual(baseSeed, SeedDeriver.Derive(6, "a", "b"));
        Assert.NotEqual(baseSeed, SeedDeriver.Derive(5, "b", "a"));
        Assert.NotEqual(SeedDeriver.Derive(5, "ab", "c"), SeedDeriver.Derive(5, "a", "bc"));
        Assert.NotEqual(SeedDeriver.Derive(5, "1"), SeedDeriver.Derive(5, 1));
        Assert.Throws<ArgumentException>(() => SeedDeriver.Derive(5, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => SeedDeriver.Derive(5, -1));
    }

    [Fact]
    public void Seed_sets_are_locked_by_default_keep_a_history_and_round_trip_as_canonical_json()
    {
        FixedClock clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        SeedSet set = SeedSet.Derive(424242, "block-1", ["seed", "lm_seed"]);
        Assert.All(set.Entries, e => Assert.True(e.Locked));
        Assert.Equal(SeedDeriver.Derive(424242, "block", "block-1", "seed", "seed"), set.Get("seed")!.Value);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => set.Set("seed", 5, "try", clock));
        Assert.Contains("locked", refused.Message, StringComparison.Ordinal);

        set.Unlock("seed");
        set.Set("seed", 5, "test a different take", clock);
        set.Lock("seed");
        SeedEntry entry = set.Get("seed")!;
        Assert.Equal(5UL, entry.Value);
        Assert.Equal(SeedOrigin.UserSet, entry.Origin);
        Assert.Single(entry.History);
        Assert.Equal("test a different take", entry.History[0].Reason);

        string json = set.ToJson();
        Assert.Equal(json, SeedSet.FromJson(json).ToJson());
        Assert.True(json.IndexOf("\"lm_seed\"", StringComparison.Ordinal) < json.IndexOf("\"name\":\"seed\"", StringComparison.Ordinal), "entries are in name order");
        Assert.Throws<KeyNotFoundException>(() => set.Unlock("nope"));
    }

    [Fact]
    public void Seed_and_derivation_code_has_no_floating_point_and_core_never_uses_system_random()
    {
        foreach (string file in new[] { "SplitMix64.cs", "StableRng.cs", "SipHash24.cs", "SeedDeriver.cs", "SeedSet.cs" })
        {
            string text = string.Join('\n', File.ReadAllLines(Path.Combine(Root, "src", "Ample.Audio", "Random", file)).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotMatch("\\b(double|float|decimal|Math\\.)", text);
        }

        string[] assemblies = ["Ample.Audio", "Ample.Storage", "Ample.Engine"];
        // the layers above Audio are not always present (the public repository grows layer by layer): scan the ones that are
        foreach (string file in assemblies.Select(a => Path.Combine(Root, "src", a)).Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("System.Random", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Random.Shared", text, StringComparison.Ordinal);
            Assert.DoesNotContain("new Random(", text, StringComparison.Ordinal);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
