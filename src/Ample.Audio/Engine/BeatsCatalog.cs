using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// The beats catalogue: original, CC0 groove entries (content/beats/beats.json, embedded) with enough metadata for an assistant to understand what an entry is:
/// a human "say" form, tempo range with fast/slow context, feel, a novelty rating, tips, how to bring it in, and which kinds of instruments suit it (resolved from named pairing profiles).
/// The catalogue is large, so every answer is small by default: an overview with counts, short summaries with a limit and paging, one entry in parts, similar entries,
/// and several queries in one call.
/// </summary>
public static class BeatsCatalog
{
    private const int DefaultLimit = 12;
    private const int MaxLimit = 40;
    private const int MaxQueries = 8;

    private static readonly string[] Parts = ["say", "tempo", "feel", "tips", "howToBringIn", "fast", "slow", "drums", "bass", "chords", "sections", "pairsWith", "avoid", "genres", "tags", "mood", "families"];

    private static readonly Lazy<JsonObject> Data = new(Load);

    // the whole entry as lower-case text, once, for the free-text search
    private static readonly Lazy<Dictionary<string, string>> Blobs = new(() => Data.Value["beats"]!.AsArray().ToDictionary(b => b!["id"]!.GetValue<string>(), b => b!.ToJsonString().ToLowerInvariant(), StringComparer.Ordinal));

    private static JsonObject Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("beats.json")!;
        JsonObject root = JsonNode.Parse(stream)!.AsObject();
        JsonObject profiles = root["profiles"]!.AsObject();
        foreach (JsonNode? entry in root["beats"]!.AsArray())
        {
            JsonObject beat = entry!.AsObject();
            JsonArray pairs = [];
            foreach (JsonNode? name in beat["pairs"]?.AsArray() ?? [])
            {
                foreach (JsonNode? item in profiles[name!.GetValue<string>()]?.AsArray() ?? []) pairs.Add(item!.DeepClone());
            }

            beat["families"] = beat["pairs"]?.DeepClone() ?? new JsonArray();
            beat.Remove("pairs");
            beat["form"] ??= "loop";
            beat["bars"] ??= 1;
            beat["pairsWith"] = pairs;
        }

        return root;
    }

    /// <summary>Every entry id (for tests and diagnostics).</summary>
    public static IReadOnlyList<string> Ids() => [.. Data.Value["beats"]!.AsArray().Select(b => b!["id"]!.GetValue<string>())];

    /// <summary>The entry with this id, fully resolved, or null.</summary>
    public static JsonObject? Get(string id) =>
        Data.Value["beats"]!.AsArray().FirstOrDefault(b => string.Equals(b!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))?.DeepClone().AsObject();

    /// <summary>
    /// One query as a small object: <c>overview</c>, <c>similar</c>=id, <c>id</c> (with <c>parts</c>), or a search by genre, family, mood, kind, form, bpm, novelty, energy and text.
    /// <c>queries</c> (an array of such objects) runs several in one call. Numbers and booleans may arrive as text.
    /// </summary>
    public static JsonObject Run(JsonObject query)
    {
        if (query["queries"] is JsonArray many)
        {
            JsonArray results = [];
            foreach (JsonNode? item in many.Take(MaxQueries))
            {
                JsonObject one = item as JsonObject ?? [];
                JsonObject answer = RunOne(one, DefaultLimit / 2 + 2);
                answer["query"] = Echo(one);
                results.Add(answer);
            }

            return new JsonObject { ["count"] = results.Count, ["skipped"] = Math.Max(0, many.Count - MaxQueries), ["results"] = results };
        }

        return RunOne(query, DefaultLimit);
    }

    private static JsonObject Echo(JsonObject query)
    {
        JsonObject echo = [];
        foreach ((string key, JsonNode? value) in query)
        {
            if (value is JsonValue v && !string.IsNullOrWhiteSpace(v.ToString())) echo[key] = v.ToString();
        }

        return echo;
    }

    private static string Text(JsonObject q, string key) => q[key] is JsonValue v ? v.ToString().Trim() : string.Empty;

    private static int Whole(JsonObject q, string key) => int.TryParse(Text(q, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static bool Flag(JsonObject q, string key) => Text(q, key).ToLowerInvariant() is "true" or "1" or "yes";

    private static JsonObject RunOne(JsonObject q, int defaultLimit)
    {
        if (Flag(q, "overview")) return Overview();
        int limit = Math.Clamp(Whole(q, "limit") is var l and > 0 ? l : defaultLimit, 1, MaxLimit);
        string similar = Text(q, "similar");
        if (similar.Length > 0) return Similar(similar, Whole(q, "limit") > 0 ? limit : 6);
        string id = Text(q, "id");
        if (id.Length > 0)
        {
            JsonObject? entry = Get(id);
            if (entry is null) return new JsonObject { ["error"] = $"There is no beat '{id}'. Search without id, or use overview=true.", ["didYouMean"] = new JsonArray([.. Ids().Where(i => i.Contains(id, StringComparison.OrdinalIgnoreCase) || id.Contains(i, StringComparison.OrdinalIgnoreCase)).Take(5).Select(i => (JsonNode)JsonValue.Create(i)!)]) };
            string parts = Text(q, "parts");
            return parts.Length == 0 ? entry : Pick(entry, parts);
        }

        double.TryParse(Text(q, "bpm"), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm);
        return Search(Text(q, "text"), Text(q, "genre"), Text(q, "mood"), Text(q, "kind"), bpm, Whole(q, "maxNovelty"), Whole(q, "minNovelty"), Whole(q, "energy"), Text(q, "form"), Text(q, "family"), Text(q, "sort"), limit, Math.Max(0, Whole(q, "offset")));
    }

    // one entry cut down to the parts that were asked for (comma separated), so a long entry does not have to be read in full
    private static JsonObject Pick(JsonObject entry, string parts)
    {
        JsonObject result = new() { ["id"] = entry["id"]!.DeepClone(), ["name"] = entry["name"]!.DeepClone(), ["form"] = entry["form"]!.DeepClone(), ["bars"] = entry["bars"]!.DeepClone() };
        List<string> unknown = [];
        foreach (string part in parts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = Parts.FirstOrDefault(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (name.Length == 0) { unknown.Add(part); continue; }
            if (entry[name] is { } node) result[name] = node.DeepClone();
        }

        if (unknown.Count > 0) result["unknownParts"] = $"{string.Join(", ", unknown)} (known: {string.Join(", ", Parts)})";
        return result;
    }

    /// <summary>Counts per family, genre, form, mood and novelty, with the tempo span: what there is, so a search can be aimed.</summary>
    public static JsonObject Overview()
    {
        JsonArray beats = Data.Value["beats"]!.AsArray();
        static JsonObject Counts(IEnumerable<string> values) => new([.. values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))]);
        IEnumerable<string> All(string field) => beats.SelectMany(b => b![field]!.AsArray().Select(x => x!.GetValue<string>()));
        return new JsonObject
        {
            ["count"] = beats.Count,
            ["forms"] = Counts(beats.Select(b => b!["form"]!.GetValue<string>())),
            ["families"] = Counts(All("families")),
            ["genres"] = Counts(All("genres")),
            ["moods"] = Counts(All("mood")),
            ["novelty"] = Counts(beats.Select(b => b!["novelty"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture))),
            ["bpm"] = new JsonObject { ["min"] = beats.Min(b => b!["tempo"]!["min"]!.GetValue<double>()), ["max"] = beats.Max(b => b!["tempo"]!["max"]!.GetValue<double>()) },
            ["use"] = "Aim a search with genre=, family=, mood=, form=, bpm=, maxNovelty=, energy=, text=, limit=, offset=. id=<id> gives an entry (parts=say,drums,tips to cut it down); similar=<id> gives neighbours; queries=[...] runs several searches in one call. novelty 1 = common, safe as a background bed .. 5 = rare. form: loop (1 bar), phrase (2-4 bars), arrangement (8-32 bars, LONG).",
        };
    }

    /// <summary>Entries like this one: shared genres and families weigh most, then mood, then a close tempo and the same form.</summary>
    public static JsonObject Similar(string id, int limit)
    {
        JsonObject? source = Get(id);
        if (source is null) return new JsonObject { ["error"] = $"There is no beat '{id}'." };
        static HashSet<string> Set(JsonObject b, string f) => [.. b[f]!.AsArray().Select(x => x!.GetValue<string>().ToLowerInvariant())];
        HashSet<string> genres = Set(source, "genres"), families = Set(source, "families"), moods = Set(source, "mood");
        double typical = source["tempo"]!["typical"]!.GetValue<double>();
        List<(double Score, JsonObject Item)> found = [];
        foreach (JsonNode? node in Data.Value["beats"]!.AsArray())
        {
            JsonObject beat = node!.AsObject();
            if (string.Equals(beat["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)) continue;
            double score = (3 * Set(beat, "genres").Intersect(genres).Count()) + (2 * Set(beat, "families").Intersect(families).Count()) + Set(beat, "mood").Intersect(moods).Count()
                + Math.Max(0, 1 - (Math.Abs(beat["tempo"]!["typical"]!.GetValue<double>() - typical) / 40)) + (beat["form"]!.GetValue<string>() == source["form"]!.GetValue<string>() ? 0.5 : 0);
            if (score > 0) found.Add((score, Summary(beat, null)));
        }

        JsonArray items = [.. found.OrderByDescending(f => f.Score).ThenBy(f => f.Item["id"]!.GetValue<string>(), StringComparer.Ordinal).Take(limit).Select(f => (JsonNode)f.Item)];
        return new JsonObject { ["like"] = id, ["count"] = items.Count, ["beats"] = items };
    }

    private static JsonObject Summary(JsonObject beat, string? tempoMatch)
    {
        string say = beat["say"]!.GetValue<string>();
        JsonObject item = new()
        {
            ["id"] = beat["id"]!.DeepClone(),
            ["name"] = beat["name"]!.DeepClone(),
            ["form"] = beat["form"]!.GetValue<string>() == "loop" ? "loop" : $"{beat["form"]!.GetValue<string>()} {beat["bars"]!.GetValue<int>()} bars",
            ["bpm"] = $"{beat["tempo"]!["min"]!.GetValue<double>():0}-{beat["tempo"]!["max"]!.GetValue<double>():0} ({beat["tempo"]!["typical"]!.GetValue<double>():0})",
            ["novelty"] = beat["novelty"]!.DeepClone(),
            ["energy"] = beat["feel"]!["energy"]!.DeepClone(),
            ["genres"] = new JsonArray([.. beat["genres"]!.AsArray().Take(3).Select(g => (JsonNode)JsonValue.Create(g!.GetValue<string>())!)]),
            ["say"] = say.Length > 90 ? say[..90] + "..." : say,
        };
        if (tempoMatch is not null && tempoMatch != "in range") item["tempoMatch"] = tempoMatch;
        return item;
    }

    /// <summary>
    /// Short summaries of the entries that match every given filter, closest to <paramref name="bpm"/> first, at most <paramref name="limit"/> of them from <paramref name="offset"/> on.
    /// A bpm also matches an entry whose range holds half or double that tempo (noted as <c>tempoMatch</c>: half-time or double-time feel).
    /// <paramref name="text"/> needs every word to occur somewhere in the entry. <paramref name="sort"/>: tempo, novelty, energy or name.
    /// </summary>
    public static JsonObject Search(string? text, string? genre, string? mood, string? kind, double bpm, int maxNovelty, int minNovelty, int energy, string? form = null, string? family = null, string? sort = null, int limit = DefaultLimit, int offset = 0)
    {
        string[] words = (text ?? string.Empty).ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        List<(double Score, JsonObject Item, JsonObject Beat)> found = [];
        foreach (JsonNode? node in Data.Value["beats"]!.AsArray())
        {
            JsonObject beat = node!.AsObject();
            if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(beat["kind"]!.GetValue<string>(), kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(form) && !string.Equals(beat["form"]!.GetValue<string>(), form, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(family) && !Has(beat["families"], family)) continue;
            if (!string.IsNullOrWhiteSpace(genre) && !Has(beat["genres"], genre) && !Has(beat["tags"], genre) && !Has(beat["families"], genre)) continue;
            if (!string.IsNullOrWhiteSpace(mood) && !Has(beat["mood"], mood)) continue;
            int novelty = beat["novelty"]!.GetValue<int>();
            if (maxNovelty > 0 && novelty > maxNovelty) continue;
            if (minNovelty > 0 && novelty < minNovelty) continue;
            if (energy > 0 && Math.Abs(beat["feel"]!["energy"]!.GetValue<int>() - energy) > 1) continue;
            if (words.Length > 0 && !words.All(w => Blobs.Value[beat["id"]!.GetValue<string>()].Contains(w, StringComparison.Ordinal))) continue;
            double min = beat["tempo"]!["min"]!.GetValue<double>(), max = beat["tempo"]!["max"]!.GetValue<double>(), typical = beat["tempo"]!["typical"]!.GetValue<double>();
            string match = "any";
            double score = 0;
            if (bpm > 0)
            {
                if (bpm >= min && bpm <= max) { match = "in range"; score = Math.Abs(bpm - typical); }
                else if (bpm * 2 >= min && bpm * 2 <= max) { match = "double-time feel (play it as " + (bpm * 2).ToString("0", CultureInfo.InvariantCulture) + " bpm, half the hits)"; score = 1000 + Math.Abs(bpm * 2 - typical); }
                else if (bpm / 2 >= min && bpm / 2 <= max) { match = "half-time feel (play it as " + (bpm / 2).ToString("0", CultureInfo.InvariantCulture) + " bpm)"; score = 1000 + Math.Abs(bpm / 2 - typical); }
                else continue;
            }

            found.Add((score, Summary(beat, bpm > 0 ? match : null), beat));
        }

        IOrderedEnumerable<(double Score, JsonObject Item, JsonObject Beat)> ordered = (sort ?? string.Empty).ToLowerInvariant() switch
        {
            "tempo" => found.OrderBy(f => f.Beat["tempo"]!["typical"]!.GetValue<double>()),
            "novelty" => found.OrderBy(f => f.Beat["novelty"]!.GetValue<int>()),
            "energy" => found.OrderBy(f => f.Beat["feel"]!["energy"]!.GetValue<int>()),
            "name" => found.OrderBy(f => f.Beat["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase),
            _ => found.OrderBy(f => f.Score),
        };
        List<JsonNode> page = [.. ordered.ThenBy(f => f.Item["id"]!.GetValue<string>(), StringComparer.Ordinal).Skip(offset).Take(limit).Select(f => (JsonNode)f.Item)];
        JsonObject result = new() { ["count"] = found.Count, ["shown"] = page.Count };
        if (offset + page.Count < found.Count) result["next"] = $"offset={offset + page.Count}";
        result["beats"] = new JsonArray([.. page]);
        if (found.Count == 0) result["hint"] = "Nothing matches all filters; drop one (bpm, mood or novelty first) or call with overview=true to see what exists.";
        return result;
    }

    private static bool Has(JsonNode? list, string value) =>
        list is JsonArray array && array.Any(x => x is not null && x.GetValue<string>().Contains(value, StringComparison.OrdinalIgnoreCase));
}
