using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.Quality;
using Ample.Storage.Takes;

namespace Ample.Plugins.Analysis;

/// <summary>Checks an analysis result against <c>spec/schema/analysis-&lt;kind&gt;.schema.json</c> (the small subset the other schemas use: type, enum, minimum, maximum, required, properties, items).</summary>
public static class AnalysisSchema
{
    /// <summary>Gets the problems of a result (empty when it fits).</summary>
    public static IReadOnlyList<string> Validate(string kind, string resultJson)
    {
        string name = $"analysis-{kind}.schema.json";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return [$"there is no schema for the analysis '{kind}'"];
        }

        using JsonDocument schema = JsonDocument.Parse(new StreamReader(stream).ReadToEnd());
        using JsonDocument value = JsonDocument.Parse(resultJson);
        List<string> problems = [];
        Check(value.RootElement, schema.RootElement, "result", problems);
        return problems;
    }

    private static void Check(JsonElement value, JsonElement schema, string where, List<string> problems)
    {
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            string[] types = type.ValueKind == JsonValueKind.Array ? [.. type.EnumerateArray().Select(t => t.GetString()!)] : [type.GetString()!];
            if (!types.Any(t => TypeIs(value, t)))
            {
                problems.Add($"{where}: expected {string.Join(" or ", types)}, found {value.ValueKind.ToString().ToLowerInvariant()}");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out JsonElement allowed) && !allowed.EnumerateArray().Any(a => a.GetRawText() == value.GetRawText()))
        {
            problems.Add($"{where}: {value.GetRawText()} is not one of {allowed.GetRawText()}");
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            double d = value.GetDouble();
            if (schema.TryGetProperty("minimum", out JsonElement min) && d < min.GetDouble())
            {
                problems.Add($"{where}: {d.ToString(CultureInfo.InvariantCulture)} is below {min.GetDouble().ToString(CultureInfo.InvariantCulture)}");
            }

            if (schema.TryGetProperty("maximum", out JsonElement max) && d > max.GetDouble())
            {
                problems.Add($"{where}: {d.ToString(CultureInfo.InvariantCulture)} is above {max.GetDouble().ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                problems.AddRange(required.EnumerateArray().Select(r => r.GetString()!).Where(r => !value.TryGetProperty(r, out _)).Select(r => $"{where}: missing \"{r}\""));
            }

            if (schema.TryGetProperty("properties", out JsonElement props))
            {
                foreach (JsonProperty p in value.EnumerateObject().Where(p => props.TryGetProperty(p.Name, out _)))
                {
                    Check(p.Value, props.GetProperty(p.Name), $"{where}.{p.Name}", problems);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int i = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (problems.Count > 20)
                {
                    break;
                }

                Check(item, items, $"{where}[{i++.ToString(CultureInfo.InvariantCulture)}]", problems);
            }
        }
    }

    private static bool TypeIs(JsonElement v, string type) => type switch
    {
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => false,
    };
}

/// <summary>A stored analysis of a take.</summary>
/// <param name="TakeId">The take.</param>
/// <param name="Analyzer">The analyser id.</param>
/// <param name="Version">Its version.</param>
/// <param name="Key">The cache key (audio content, analyser, version, options).</param>
/// <param name="Json">The result.</param>
/// <param name="FromCache">True when it was found, not computed now.</param>
public sealed record AnalysisRecord(string TakeId, string Analyzer, string Version, string Key, string Json, bool FromCache);

/// <summary>
/// Runs analysers on takes and keeps the results next to the project (<c>.ample/analysis/</c>, one file per result named by its cache key, plus an
/// index that links results to takes). A result is computed once for the same audio, analyser, version and options; it never changes the take.
/// (The results are not stored as take data because the take bank counts data that no take owns as orphans.)
/// </summary>
public sealed class AnalysisService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Takebank bank;
    private readonly string folder;
    private readonly IReadOnlyList<IAnalyzer> analyzers;

    /// <summary>Creates the service.</summary>
    public AnalysisService(Takebank bank, string projectRoot, IReadOnlyList<IAnalyzer>? analyzers = null)
    {
        this.bank = bank;
        folder = Path.Combine(projectRoot, ".ample", "analysis");
        this.analyzers = analyzers ?? BuiltInAnalyzers.All;
    }

    /// <summary>Analyses a take (or returns the stored result).</summary>
    /// <exception cref="AmpleException">AMP5340 for an unknown analyser, a take without readable audio or a result that does not fit its schema.</exception>
    public AnalysisRecord Analyze(string takeId, string analyzerId, string optionsJson = "{}")
    {
        IAnalyzer analyzer = analyzers.FirstOrDefault(a => string.Equals(a.Id, analyzerId, StringComparison.OrdinalIgnoreCase)) ?? throw new AmpleException("AMP5341", $"There is no analyser '{analyzerId}'. Known: {string.Join(", ", analyzers.Select(a => a.Id))}.");
        TakeRecord take = bank.GetTake(bank.ResolveTakeId(takeId));
        string dataId = Ample.Storage.Quality.QualityService.AudioDataId(take) ?? throw new AmpleException("AMP5342", "This take has no audio to analyse.");
        string key = Key(dataId, analyzer, optionsJson);
        string file = Path.Combine(folder, key + ".json");
        if (File.Exists(file))
        {
            Link(take.TakeId, analyzer, key);
            return new AnalysisRecord(take.TakeId, analyzer.Id, analyzer.Version, key, File.ReadAllText(file), true);
        }

        byte[] bytes;
        using (Stream data = bank.OpenData(dataId))
        using (MemoryStream ms = new())
        {
            data.CopyTo(ms);
            bytes = ms.ToArray();
        }

        float[] samples;
        int rate;
        try
        {
            (samples, rate) = AudioAnalysis.ReadWav(bytes);
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5342", "The audio of this take cannot be analysed: " + ex.Message);
        }

        string json = analyzer.Analyze(new AnalysisInput(samples, rate, optionsJson));
        IReadOnlyList<string> problems = AnalysisSchema.Validate(analyzer.Id, json);
        if (problems.Count > 0)
        {
            throw new AmpleException("AMP5343", $"The analyser '{analyzer.Id}' gave a result that does not fit its schema: {problems[0]}");
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(file + ".part", json);
        File.Move(file + ".part", file, true);
        Link(take.TakeId, analyzer, key);
        return new AnalysisRecord(take.TakeId, analyzer.Id, analyzer.Version, key, json, false);
    }

    /// <summary>The analyses linked to a take.</summary>
    public IReadOnlyList<AnalysisRecord> ForTake(string takeId)
    {
        string resolved = bank.ResolveTakeId(takeId);
        return [.. ReadIndex().Where(e => e.TakeId == resolved).Select(e => new AnalysisRecord(e.TakeId, e.Analyzer, e.Version, e.Key, File.ReadAllText(Path.Combine(folder, e.Key + ".json")), true)).Where(r => r.Json.Length > 0)];
    }

    private static string Key(string dataId, IAnalyzer analyzer, string optionsJson)
    {
        string canonical = JsonSerializer.Serialize(JsonDocument.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson).RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.GetRawText()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{dataId}|{analyzer.Id}|{analyzer.Version}|{canonical}")))[..32];
    }

    private List<IndexEntry> ReadIndex()
    {
        string index = Path.Combine(folder, "index.json");
        return File.Exists(index) ? JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(index), Options) ?? [] : [];
    }

    private void Link(string takeId, IAnalyzer analyzer, string key)
    {
        List<IndexEntry> entries = ReadIndex();
        if (entries.Any(e => e.TakeId == takeId && e.Key == key))
        {
            return;
        }

        entries.Add(new IndexEntry(takeId, analyzer.Id, analyzer.Version, key));
        Directory.CreateDirectory(folder);
        string index = Path.Combine(folder, "index.json");
        File.WriteAllText(index + ".part", JsonSerializer.Serialize(entries, Options) + "\n");
        File.Move(index + ".part", index, true);
    }

    private sealed record IndexEntry(string TakeId, string Analyzer, string Version, string Key);
}
