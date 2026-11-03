using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// MT01, mix starting points for an assistant: for each instrument kind (and a genre) the first volume, pan, filter, EQ, compression and send to try, written as the parameters of
/// AMPLE's own blocks (volume gain, pan, filter highpass, eq, compressor, reverb, delay, duck, limiter). Data only (content/beats/mix.json, embedded); nothing is measured here, so
/// the numbers are where to begin, not where to end.
/// </summary>
public static class MixHints
{
    private static readonly Lazy<JsonObject> Data = new(() =>
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("mix.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>
    /// A query with <c>instruments</c> (comma separated; a name or an alias such as "electric piano" or "808") and optionally <c>genre</c>, or <c>overview</c>=true for what exists.
    /// </summary>
    public static JsonObject Run(JsonObject q)
    {
        string Text(string key) => q[key] is JsonValue v ? v.ToString().Trim() : string.Empty;
        JsonObject data = Data.Value;
        if (Text("overview").Equals("true", StringComparison.OrdinalIgnoreCase) || Text("instruments").Length == 0)
        {
            return new JsonObject
            {
                ["instruments"] = new JsonArray([.. data["instruments"]!.AsArray().Select(i => (JsonNode)JsonValue.Create(i!["id"]!.GetValue<string>() + " (" + string.Join(", ", i["aka"]!.AsArray().Take(3).Select(a => a!.GetValue<string>())) + ")")!)]),
                ["genres"] = new JsonArray([.. data["genres"]!.AsArray().Select(g => (JsonNode)JsonValue.Create(g!["id"]!.GetValue<string>() + " (" + string.Join(", ", g["aka"]!.AsArray().Select(a => a!.GetValue<string>())) + ")")!)]),
                ["use"] = "Call with instruments=kick,bass,piano,pad,vocal-lead and genre=techno: you get a starting level, pan, filter, EQ, compression and sends per instrument as the parameters of AMPLE's own blocks, plus the master and the order to mix in.",
                ["units"] = data["units"]!.DeepClone(),
            };
        }

        JsonObject? genre = Find(data["genres"]!.AsArray(), Text("genre"));
        JsonObject deltas = genre?["levelDelta"]?.AsObject() ?? [];
        double reverbScale = genre?["reverbScale"]?.GetValue<double>() ?? 1;
        JsonArray rows = [];
        List<string> unknown = [];
        foreach (string name in Text("instruments").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? item = Find(data["instruments"]!.AsArray(), name);
            if (item is null) { unknown.Add(name); continue; }
            string id = item["id"]!.GetValue<string>();
            double level = item["level"]!.GetValue<double>() + (deltas[id]?.GetValue<double>() ?? 0);
            JsonObject row = new()
            {
                ["instrument"] = id,
                ["role"] = item["role"]!.DeepClone(),
                ["volume"] = $"gain {Num(level)} dB",
                ["pan"] = item["pan"]!.DeepClone(),
                ["filter"] = $"type highpass, cutoff {item["highpass"]!.GetValue<int>()} Hz",
            };
            if (item["eq"] is JsonObject eq) row["eq"] = $"low {Num(eq["low"]!.GetValue<double>())} dB, mid {Num(eq["mid"]!.GetValue<double>())} dB, high {Num(eq["high"]!.GetValue<double>())} dB, low_hz {eq["low_hz"]!.GetValue<int>()} Hz, high_hz {eq["high_hz"]!.GetValue<int>()} Hz";
            if (item["comp"] is JsonObject comp) row["compressor"] = $"threshold {comp["threshold"]!.GetValue<int>()} dB, ratio {Num(comp["ratio"]!.GetValue<double>())}";
            double reverb = Math.Round(item["reverb"]!.GetValue<double>() * reverbScale);
            if (reverb > 0) row["reverb"] = $"mix {reverb.ToString("0", CultureInfo.InvariantCulture)}%";
            if (item["delay"]!.GetValue<double>() > 0) row["delay"] = $"mix {item["delay"]!.GetValue<int>()}%";
            row["notes"] = item["notes"]!.DeepClone();
            row["chain"] = $"{id} -> filter(highpass {item["highpass"]!.GetValue<int>()} Hz) -> eq -> {(item["comp"] is null ? string.Empty : "compressor -> ")}volume({Num(level)} dB) -> pan({item["pan"]!.GetValue<int>()})";
            rows.Add(row);
        }

        JsonObject result = new() { ["genre"] = genre?["id"]?.DeepClone(), ["rows"] = rows, ["units"] = data["units"]!.DeepClone() };
        if (Text("genre").Length > 0 && genre is null) result["genreNote"] = $"Unknown genre '{Text("genre")}': the instrument defaults are used (see overview=true for the genres).";
        if (unknown.Count > 0) result["unknown"] = $"{string.Join(", ", unknown)}: not a known instrument (see overview=true).";
        JsonObject master = data["master"]!.DeepClone().AsObject();
        if (genre is not null)
        {
            master["targetLufs"] = genre["lufs"]!.DeepClone();
            master["width"] = genre["width"]!.DeepClone();
            if (genre["sidechain"] is JsonObject sc) result["sidechain"] = $"duck block fed by the kick: depth {sc["depth"]!.GetValue<int>()} dB, release {sc["release"]!.GetValue<int>()} ms, pattern {sc["pattern"]!.GetValue<string>()}; put it on the bass, pads and chords so the kick keeps its space.";
            result["genreNotes"] = genre["notes"]!.DeepClone();
        }

        result["master"] = master;
        result["order"] = data["order"]!.DeepClone();
        return result;
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // by id or alias, ignoring case; a word that contains the alias ("my electric piano") also finds it
    private static JsonObject? Find(JsonArray list, string name)
    {
        if (name.Length == 0) return null;
        JsonObject? exact = list.Select(i => i!.AsObject()).FirstOrDefault(i => string.Equals(i["id"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase) || i["aka"]!.AsArray().Any(a => string.Equals(a!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)));
        return exact ?? list.Select(i => i!.AsObject()).FirstOrDefault(i => name.Contains(i["id"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase) || i["aka"]!.AsArray().Any(a => name.Contains(a!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)));
    }
}
