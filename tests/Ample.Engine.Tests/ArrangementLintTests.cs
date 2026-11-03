using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class ArrangementLintTests
{
    private static JsonObject Lint(string blocks) => ArrangementLint.Lint(EngineRig.Parse("composition \"t\" { ample 1  tempo 120  master-seed 1 }\n" + blocks));

    private static bool Has(JsonObject result, string code) => result["findings"]!.AsArray().Any(f => f!["code"]!.GetValue<string>() == code);

    [Fact]
    public void Silent_bars_between_two_parts_are_found_with_their_range_and_the_density_string_shows_them()
    {
        JsonObject result = Lint("drums a at 1 for 4 {\n  pattern beat-A\n}\ndrums b at 9 for 4 {\n  pattern beat-A\n}\n");
        Assert.Equal(12, result["bars"]!.GetValue<int>());
        Assert.Equal("111100001111", result["density"]!.GetValue<string>());
        Assert.Contains(result["findings"]!.AsArray(), f => f!["code"]!.GetValue<string>() == "silent-bars" && f["bars"]!.GetValue<string>() == "5-8");
    }

    [Fact]
    public void Harmony_without_a_bass_and_a_short_song_are_pointed_out_and_a_bass_clears_the_first()
    {
        string piano = "piano pn pinned@2.0 at 1 for 12 {\n  set felt-piano\n}\n";
        JsonObject without = Lint(piano + "drums d at 1 for 12 {\n  pattern beat-A\n}\n");
        Assert.True(Has(without, "no-bass"));
        Assert.True(Has(without, "short"));
        JsonObject with = Lint(piano + "bass bs pinned@2.0 at 1 for 12 {\n  set felt-piano\n}\n");
        Assert.False(Has(with, "no-bass"));
    }

    [Fact]
    public void A_song_that_never_changes_and_starts_full_gets_no_build_and_same_layers_advice()
    {
        string blocks = "drums d at 1 for 32 {\n  pattern beat-A\n}\npiano p pinned@2.0 at 1 for 32 {\n  set felt-piano\n}\nbass b pinned@2.0 at 1 for 32 {\n  set felt-piano\n}\n";
        JsonObject result = Lint(blocks);
        Assert.True(Has(result, "no-build"));
        Assert.True(Has(result, "same-layers"));
        Assert.True(Has(result, "same-start-same-length"));
        Assert.Single(result["sections"]!.AsArray());
    }
}
