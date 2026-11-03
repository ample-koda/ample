using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class PatternGeneratorsTests
{
    private static JsonObject Run(params (string Key, string Value)[] pairs)
    {
        JsonObject query = [];
        foreach ((string key, string value) in pairs) query[key] = value;
        return PatternGenerators.Run(query);
    }

    [Fact]
    public void Euclid_makes_the_tresillo_and_keeps_the_number_of_hits()
    {
        Assert.Equal("x..x..x.", Run(("kind", "euclid"), ("steps", "8"), ("hits", "3"))["grid"]!.GetValue<string>());
        string grid = Run(("kind", "euclid"), ("steps", "16"), ("hits", "5"), ("rotate", "3"))["grid"]!.GetValue<string>();
        Assert.Equal(16, grid.Length);
        Assert.Equal(5, grid.Count(c => c == 'x'));
    }

    [Fact]
    public void Grooves_are_deterministic_per_seed_and_pastable_as_a_drums_block()
    {
        JsonObject a = Run(("kind", "groove"), ("style", "funk"), ("variation", "1"), ("seed", "7"));
        JsonObject again = Run(("kind", "groove"), ("style", "funk"), ("variation", "1"), ("seed", "7"));
        JsonObject other = Run(("kind", "groove"), ("style", "funk"), ("variation", "1"), ("seed", "8"));
        Assert.Equal(a["pattern"]!.GetValue<string>(), again["pattern"]!.GetValue<string>());
        Assert.NotEqual(a["pattern"]!.GetValue<string>(), other["pattern"]!.GetValue<string>());
        foreach (string voice in a["pattern"]!.GetValue<string>().Split(' ')) Assert.Equal(16, voice[(voice.IndexOf(':', StringComparison.Ordinal) + 1)..].Length);
        Assert.Contains("drums beat", a["drumsBlock"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_arpeggio_of_a_minor_starts_a3_c4_e4_and_a_bassline_gives_one_chord_per_bar()
    {
        JsonObject arp = Run(("kind", "arpeggio"), ("chord", "Am"), ("rate", "2"));
        Assert.StartsWith("a3:0.5 c4:0.5 e4:0.5 a3:0.5", arp["melody"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, arp["bars"]!.GetValue<int>());
        JsonObject bass = Run(("kind", "bassline"), ("progression", "Am F"));
        Assert.Equal(2, bass["bars"]!.GetValue<int>());
        Assert.StartsWith("a2:0.75", bass["melody"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("f2:", bass["melody"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_request_answers_with_an_error_and_the_list_of_what_exists()
    {
        JsonObject bad = Run(("kind", "arpeggio"), ("chord", "Hm"));
        Assert.NotNull(bad["error"]);
        Assert.NotNull(bad["see"]);
        Assert.NotNull(Run()["kinds"]);
    }
}
