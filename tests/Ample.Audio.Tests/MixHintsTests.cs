using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class MixHintsTests
{
    private static JsonObject Run(string instruments, string genre)
    {
        JsonObject query = new() { ["instruments"] = instruments };
        if (genre.Length > 0) query["genre"] = genre;
        return MixHints.Run(query);
    }

    [Fact]
    public void Names_and_aliases_find_the_instruments_and_a_genre_moves_the_levels()
    {
        JsonObject techno = Run("kick, electric piano, 808", "techno");
        JsonArray rows = techno["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal("epiano", rows[1]!["instrument"]!.GetValue<string>());
        Assert.Equal("sub", rows[2]!["instrument"]!.GetValue<string>());
        Assert.Equal("gain -3 dB", rows[0]!["volume"]!.GetValue<string>());
        JsonObject ballad = Run("kick", "ballad");
        Assert.Equal("gain -7 dB", ballad["rows"]![0]!["volume"]!.GetValue<string>());
        Assert.NotNull(techno["sidechain"]);
        Assert.Null(ballad["sidechain"]);
    }

    [Fact]
    public void An_unknown_instrument_is_reported_and_no_instruments_lists_what_exists()
    {
        JsonObject result = Run("kick, theremin", string.Empty);
        Assert.Single(result["rows"]!.AsArray());
        Assert.Contains("theremin", result["unknown"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject all = Run(string.Empty, string.Empty);
        Assert.True(all["instruments"]!.AsArray().Count >= 20);
        Assert.True(all["genres"]!.AsArray().Count >= 8);
    }

    [Fact]
    public void Every_instrument_has_a_level_a_pan_and_a_filter_in_the_ranges_the_blocks_accept()
    {
        JsonObject all = Run(string.Empty, string.Empty);
        foreach (string entry in all["instruments"]!.AsArray().Select(x => x!.GetValue<string>().Split(' ')[0]))
        {
            JsonObject row = Run(entry, string.Empty)["rows"]![0]!.AsObject();
            Assert.Matches(@"^gain -?\d+(\.\d)? dB$", row["volume"]!.GetValue<string>());
            Assert.InRange(row["pan"]!.GetValue<int>(), -32, 32);
            Assert.Contains("highpass", row["filter"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }
}
