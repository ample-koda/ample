using System.Globalization;
using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>The seed model (docs/language/13-randomness.md): the golden keys and draws shared with the JavaScript twin, and the properties that make it stateless and addressed.</summary>
public sealed class NoteRandomTests
{
    private static ulong KeyOf(string master, IEnumerable<string> path)
    {
        ulong? key = null;
        foreach (string step in path)
        {
            int colon = step.IndexOf(':', StringComparison.Ordinal);
            string kind = step[..colon], value = step[(colon + 1)..];
            key = kind switch
            {
                "stable" => NoteRandom.Stable(ulong.Parse(master, CultureInfo.InvariantCulture), value),
                "explicit" => NoteRandom.Explicit(System.Numerics.BigInteger.Parse(value, CultureInfo.InvariantCulture)),
                _ => NoteRandom.Fork(key!.Value, value),
            };
        }

        return key!.Value;
    }

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Root, "spec-tests", "note-pipelines", "seeds.json")));

    [Fact]
    public void The_roots_and_keys_are_the_golden_ones()
    {
        using JsonDocument doc = Vectors();
        int checkedCount = 0;
        foreach (JsonElement k in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            string master = k.GetProperty("master").GetString()!;
            if (k.TryGetProperty("root", out JsonElement root)) Assert.Equal(root.GetString(), NoteRandom.Hex(NoteRandom.Root(ulong.Parse(master, CultureInfo.InvariantCulture))));
            else Assert.Equal(k.GetProperty("key").GetString(), NoteRandom.Hex(KeyOf(master, k.GetProperty("path").EnumerateArray().Select(x => x.GetString()!))));
            checkedCount++;
        }

        Assert.True(checkedCount > 30);
    }

    [Fact]
    public void The_draws_are_the_golden_ones()
    {
        using JsonDocument doc = Vectors();
        int checkedCount = 0;
        foreach (JsonElement d in doc.RootElement.GetProperty("draws").EnumerateArray())
        {
            ulong key = KeyOf(d.GetProperty("master").GetString()!, d.GetProperty("path").EnumerateArray().Select(x => x.GetString()!));
            Assert.Equal(d.GetProperty("bits").GetString(), NoteRandom.Hex(NoteRandom.Draw(key, d.GetProperty("purpose").GetString()!, d.GetProperty("address").GetString()!)));
            checkedCount++;
        }

        Assert.True(checkedCount > 200);
    }

    [Fact]
    public void A_key_depends_only_on_its_path_and_never_on_what_was_asked_before()
    {
        ulong first = NoteRandom.Stable(424242, "verse");
        _ = NoteRandom.Draw(NoteRandom.Fork(first, "x"), "random", "0");   // an unrelated draw in between
        Assert.Equal(first, NoteRandom.Stable(424242, "verse"));
        Assert.Equal(NoteRandom.Fork(first, "chords"), NoteRandom.Fork(NoteRandom.Stable(424242, "verse"), "chords"));
    }

    [Fact]
    public void Forks_are_independent_names_numbers_and_strings_differ_and_the_master_matters_only_for_stable()
    {
        ulong verse = NoteRandom.Stable(1, "verse");
        string[] labels = ["chords", "timing", "fills", "#3", "3"];
        Assert.Equal(labels.Length, labels.Select(l => NoteRandom.Fork(verse, l)).Distinct().Count());
        Assert.NotEqual(NoteRandom.Fork(NoteRandom.Fork(verse, "a"), "b"), NoteRandom.Fork(NoteRandom.Fork(verse, "b"), "a"));
        Assert.NotEqual(NoteRandom.Stable(1, "verse"), NoteRandom.Stable(2, "verse"));
        Assert.Equal(NoteRandom.Explicit(7), NoteRandom.Explicit(7));
        Assert.NotEqual(NoteRandom.Explicit(7), NoteRandom.Explicit(8));
    }

    [Fact]
    public void Different_rounds_and_purposes_differ_and_the_same_ones_do_not()
    {
        ulong key = NoteRandom.Stable(424242, "verse");
        Assert.Equal(NoteRandom.Draw(key, "random", "0"), NoteRandom.Draw(key, "random", "0"));
        Assert.Equal(20, Enumerable.Range(0, 20).Select(i => NoteRandom.Draw(key, "random", i.ToString(CultureInfo.InvariantCulture))).Distinct().Count());
        Assert.NotEqual(NoteRandom.Draw(key, "random", "0"), NoteRandom.Draw(key, "choose", "0"));
    }

    [Fact]
    public void Draws_are_spread_evenly_enough_for_a_song()
    {
        ulong key = NoteRandom.Stable(7, "spread");
        int[] buckets = new int[10];
        for (int i = 0; i < 5000; i++) buckets[(int)NoteRandom.Below(NoteRandom.Draw(key, "random", i.ToString(CultureInfo.InvariantCulture)), 10)]++;
        Assert.All(buckets, b => Assert.InRange(b, 400, 600));
    }

    [Fact]
    public void The_scale_table_is_the_one_of_the_spec()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Root, "spec", "scales.json")));
        foreach (JsonProperty scale in doc.RootElement.GetProperty("scales").EnumerateObject())
        {
            if (!NoteTheory.Scales.TryGetValue(scale.Name, out int[]? ours)) continue;
            Assert.Equal(scale.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray(), ours);
        }
    }

    [Theory]
    [InlineData("Am", 9, "0,3,7")]
    [InlineData("F", 5, "0,4,7")]
    [InlineData("G7", 7, "0,4,7,10")]
    [InlineData("C#m7", 1, "0,3,7,10")]
    [InlineData("Bbmaj7", 10, "0,4,7,11")]
    [InlineData("Esus4", 4, "0,5,7")]
    [InlineData("Ebm7b5", 3, "0,3,6,10")]
    public void Chord_symbols_are_read(string symbol, int root, string intervals)
    {
        Assert.True(NoteTheory.TryChord(symbol, out int r, out int[] i));
        Assert.Equal(root, r);
        Assert.Equal(intervals, string.Join(',', i));
    }

    [Theory]
    [InlineData("H")]
    [InlineData("Amaj9x")]
    [InlineData("")]
    public void Wrong_chord_symbols_are_refused(string symbol) => Assert.False(NoteTheory.TryChord(symbol, out _, out _));
}
