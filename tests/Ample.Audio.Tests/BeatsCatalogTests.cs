using System.Text.Json.Nodes;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class BeatsCatalogTests
{
    [Fact]
    public void Every_entry_is_complete_has_unique_id_and_16_step_grids_and_says_which_instruments_fit()
    {
        IReadOnlyList<string> ids = BeatsCatalog.Ids();
        Assert.True(ids.Count >= 20);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (string id in ids)
        {
            JsonObject beat = BeatsCatalog.Get(id)!;
            foreach (string field in new[] { "name", "kind", "say", "fast", "slow", "genres", "mood", "tips", "howToBringIn", "avoid", "pairsWith", "drums", "tempo", "feel", "novelty" }) Assert.True(beat[field] is not null, $"{id}: {field}");
            int novelty = beat["novelty"]!.GetValue<int>();
            Assert.InRange(novelty, 1, 5);
            Assert.True(beat["tempo"]!["min"]!.GetValue<double>() <= beat["tempo"]!["typical"]!.GetValue<double>() && beat["tempo"]!["typical"]!.GetValue<double>() <= beat["tempo"]!["max"]!.GetValue<double>(), id);
            Assert.NotEmpty(beat["pairsWith"]!.AsArray());
            foreach (JsonNode? pair in beat["pairsWith"]!.AsArray()) Assert.True(pair!["kinds"]!.AsArray().Count > 0 && pair["gm"]!.AsArray().Count > 0 && pair["why"] is not null, id);
            foreach (string voice in beat["drums"]!["grid"]!.GetValue<string>().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(v => v.Contains(':', StringComparison.Ordinal)))
            {
                string steps = voice[(voice.IndexOf(':', StringComparison.Ordinal) + 1)..];
                Assert.True(steps.Length is 12 or 16, $"{id}: {voice}");
            }
        }
    }

    [Fact]
    public void Every_family_has_at_least_two_options_to_choose_from()
    {
        foreach (string family in new[] { "house", "hiphop", "lofi", "trap", "rock", "funk", "disco", "reggae", "dnb", "latin", "afro", "jazz", "blues", "ambient", "techno", "country", "ballad", "reggaeton" })
        {
            int count = BeatsCatalog.Ids().Count(id => BeatsCatalog.Get(id)!["families"]!.AsArray().Any(f => f!.GetValue<string>() == family));
            Assert.True(count >= 2, $"{family}: {count}");
        }
    }

    [Fact]
    public void Every_genre_has_at_least_two_options_to_choose_from()
    {
        IEnumerable<IGrouping<string, string>> genres = BeatsCatalog.Ids().SelectMany(id => BeatsCatalog.Get(id)!["genres"]!.AsArray().Select(g => (Genre: g!.GetValue<string>(), Id: id))).GroupBy(x => x.Genre, x => x.Id);
        foreach (IGrouping<string, string> genre in genres) Assert.True(genre.Count() >= 2, $"{genre.Key}: {genre.Count()}");
    }

    [Fact]
    public void A_tempo_search_finds_in_range_first_and_half_time_fits_and_the_novelty_filter_keeps_common_beds()
    {
        JsonObject result = BeatsCatalog.Search(null, null, null, null, 124, 0, 0, 0);
        Assert.Null(result["beats"]![0]!["tempoMatch"]);
        Assert.Contains(result["beats"]!.AsArray(), b => b!["id"]!.GetValue<string>() == "house-four-on-floor");
        JsonObject slow = BeatsCatalog.Search(null, null, null, null, 70, 0, 0, 0);
        Assert.Contains(BeatsCatalog.Search("halftime", "trap", null, null, 70, 0, 0, 0, limit: 40)["beats"]!.AsArray(), b => b!["tempoMatch"]?.GetValue<string>().StartsWith("double-time", StringComparison.Ordinal) == true && b["id"]!.GetValue<string>() == "trap-halftime");
        Assert.NotEmpty(slow["beats"]!.AsArray());
        JsonObject beds = BeatsCatalog.Search(null, "lofi", null, null, 0, 1, 0, 0);
        Assert.All(beds["beats"]!.AsArray(), b => Assert.Equal(1, b!["novelty"]!.GetValue<int>()));
        Assert.Null(BeatsCatalog.Get("nope"));
    }

    [Fact]
    public void Exploring_stays_small_overview_paging_parts_similar_and_several_queries_in_one_call()
    {
        JsonObject overview = BeatsCatalog.Run(new JsonObject { ["overview"] = "true" });
        Assert.True(overview["count"]!.GetValue<int>() >= 100);
        Assert.True(overview["genres"]!.AsObject().Count > 20 && overview["families"]!.AsObject().Count >= 18 && overview["forms"]!["arrangement"] is not null);

        JsonObject page = BeatsCatalog.Run(new JsonObject { ["genre"] = "rap", ["limit"] = "3" });
        Assert.Equal(3, page["shown"]!.GetValue<int>());
        Assert.StartsWith("offset=3", page["next"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(page["beats"]![0]!.ToJsonString().Length < 600);
        JsonObject second = BeatsCatalog.Run(new JsonObject { ["genre"] = "rap", ["limit"] = "3", ["offset"] = "3" });
        Assert.NotEqual(page["beats"]![0]!["id"]!.GetValue<string>(), second["beats"]![0]!["id"]!.GetValue<string>());

        JsonObject parts = BeatsCatalog.Run(new JsonObject { ["id"] = "rap-storyteller-bed", ["parts"] = "say,drums" });
        Assert.NotNull(parts["say"]);
        Assert.NotNull(parts["drums"]);
        Assert.Null(parts["tips"]);
        Assert.Equal("beats", BeatsCatalog.Run(new JsonObject { ["id"] = "rap-storyteller" })["didYouMean"] is null ? "none" : "beats");

        JsonObject like = BeatsCatalog.Run(new JsonObject { ["similar"] = "country-honky-tonk-shuffle" });
        Assert.NotEmpty(like["beats"]!.AsArray());
        Assert.DoesNotContain(like["beats"]!.AsArray(), b => b!["id"]!.GetValue<string>() == "country-honky-tonk-shuffle");

        JsonObject many = BeatsCatalog.Run(new JsonObject { ["queries"] = new JsonArray(new JsonObject { ["genre"] = "country", ["form"] = "arrangement" }, new JsonObject { ["genre"] = "singer-songwriter", ["maxNovelty"] = "2" }, new JsonObject { ["text"] = "lullaby waltz" }) });
        Assert.Equal(3, many["results"]!.AsArray().Count);
        Assert.All(many["results"]!.AsArray(), r => Assert.True(r!["count"]!.GetValue<int>() > 0));
    }
}
