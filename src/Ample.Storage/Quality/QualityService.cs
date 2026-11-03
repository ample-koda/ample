using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.Takes;

namespace Ample.Storage.Quality;

/// <summary>One rule of <c>spec/quality-rules.json</c>.</summary>
/// <param name="Id">Q001...</param>
/// <param name="Name">Short name.</param>
/// <param name="Title">Title.</param>
/// <param name="Metric">The measured value it looks at.</param>
/// <param name="Op"><c>gt</c> (fails when the value is above the threshold) or <c>requires</c> (needs a plugin that is not there).</param>
/// <param name="Threshold">The limit.</param>
/// <param name="Severity">error, warning or info.</param>
/// <param name="AppliesTo"><c>audio</c> (every take), <c>master</c> (the master block) or <c>final</c> (not previews).</param>
/// <param name="Explanation">Plain sentence.</param>
public sealed record QualityRule(string Id, string Name, string Title, string Metric, string Op, double Threshold, string Severity, string AppliesTo, string Explanation);

/// <summary>What a rule found on a take.</summary>
/// <param name="RuleId">The rule.</param>
/// <param name="Name">Rule name.</param>
/// <param name="Severity">The rule's severity.</param>
/// <param name="Status"><c>passed</c>, <c>failed</c> or <c>not-run</c>.</param>
/// <param name="Message">Plain sentence with the numbers.</param>
public sealed record QualityFinding(string RuleId, string Name, string Severity, string Status, string Message);

/// <summary>The quality result of a take. Advisory: it never blocks saving.</summary>
/// <param name="TakeId">The take.</param>
/// <param name="Block">The block it belongs to.</param>
/// <param name="Checked">When.</param>
/// <param name="Metrics">The measurements, or null when the audio could not be read.</param>
/// <param name="Findings">One per rule.</param>
/// <param name="Error">AMP5331 text when the audio could not be read.</param>
public sealed record QualityResult(string TakeId, string Block, DateTimeOffset Checked, AudioMetrics? Metrics, IReadOnlyList<QualityFinding> Findings, string? Error)
{
    /// <summary>True when no rule failed (rules that did not run do not count).</summary>
    public bool Passed => Error is null && Findings.All(f => f.Status != "failed");
}

/// <summary>The project's quality policy (<c>.ample/quality.json</c>): thresholds, switched-off rules and which rules must pass before a tag or a promote.</summary>
/// <param name="TargetLufs">The loudness target of the master.</param>
/// <param name="ToleranceLu">How far from it is acceptable.</param>
/// <param name="MasterBlock">The block whose takes are the song.</param>
/// <param name="Thresholds">Per rule threshold overrides.</param>
/// <param name="Disabled">Rules that are switched off.</param>
/// <param name="Require">For <c>tag</c> and <c>promote</c>: the rules that must pass.</param>
public sealed record QualityPolicy(double TargetLufs, double ToleranceLu, string MasterBlock, IReadOnlyDictionary<string, double> Thresholds, IReadOnlyList<string> Disabled, IReadOnlyDictionary<string, IReadOnlyList<string>> Require)
{
    /// <summary>The default policy: -14 LUFS within 1.5 LU, nothing required.</summary>
    public static QualityPolicy Default { get; } = new(-14, 1.5, "master", new Dictionary<string, double>(), [], new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>The file of a project.</summary>
    public static string FileOf(string projectRoot) => Path.Combine(projectRoot, ".ample", "quality.json");

    /// <summary>Reads the policy of a project; null when it has none.</summary>
    /// <exception cref="AmpleException">AMP5331 when the file is damaged.</exception>
    public static QualityPolicy? Read(string projectRoot)
    {
        string file = FileOf(projectRoot);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement r = d.RootElement;
            Dictionary<string, double> th = r.TryGetProperty("thresholds", out JsonElement t) ? t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble()) : [];
            List<string> disabled = r.TryGetProperty("disabled", out JsonElement dis) ? [.. dis.EnumerateArray().Select(x => x.GetString()!)] : [];
            Dictionary<string, IReadOnlyList<string>> req = [];
            if (r.TryGetProperty("require", out JsonElement rq))
            {
                foreach (JsonProperty p in rq.EnumerateObject())
                {
                    req[p.Name] = [.. p.Value.EnumerateArray().Select(x => x.GetString()!)];
                }
            }

            return new QualityPolicy(r.TryGetProperty("targetLufs", out JsonElement tl) ? tl.GetDouble() : -14, r.TryGetProperty("toleranceLu", out JsonElement tol) ? tol.GetDouble() : 1.5, r.TryGetProperty("masterBlock", out JsonElement mb) ? mb.GetString()! : "master", th, disabled, req);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new AmpleException("AMP5331", "The quality policy (.ample/quality.json) is damaged: " + ex.Message);
        }
    }
}

/// <summary>Runs the quality rules on takes, keeps the results beside the project (<c>.ample/quality/</c>) and enforces the policy before tags and promotes.</summary>
public sealed class QualityService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Takebank bank;
    private readonly IClock clock;
    private readonly string root;

    /// <summary>Creates the service.</summary>
    public QualityService(Takebank bank, IClock clock, string projectRoot, IReadOnlyList<QualityRule>? rules = null)
    {
        this.bank = bank;
        this.clock = clock;
        root = projectRoot;
        Rules = rules ?? BuiltInRules();
    }

    /// <summary>Gets the rules in use.</summary>
    public IReadOnlyList<QualityRule> Rules { get; }

    /// <summary>The rules of <c>spec/quality-rules.json</c>, built into the assembly.</summary>
    public static IReadOnlyList<QualityRule> BuiltInRules()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("quality-rules.json") ?? throw new InvalidOperationException("quality-rules.json is not built in.");
        return ParseRules(new StreamReader(s).ReadToEnd());
    }

    /// <summary>Reads a rules file.</summary>
    public static IReadOnlyList<QualityRule> ParseRules(string json)
    {
        using JsonDocument d = JsonDocument.Parse(json);
        return [.. d.RootElement.GetProperty("rules").EnumerateArray().Select(r => new QualityRule(r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!, r.GetProperty("title").GetString()!, r.GetProperty("metric").GetString()!, r.GetProperty("op").GetString()!, r.GetProperty("threshold").GetDouble(), r.GetProperty("severity").GetString()!, r.GetProperty("appliesTo").GetString()!, r.GetProperty("explanation").GetString()!))];
    }

    /// <summary>Measures a take and applies the rules; the result is stored.</summary>
    public QualityResult Check(string takeId)
    {
        QualityPolicy policy = QualityPolicy.Read(root) ?? QualityPolicy.Default;
        TakeRead read = bank.ReadTake(bank.ResolveTakeId(takeId));
        string block = read.Plan.Plan.BlockPath;
        QualityResult result;
        string? dataId = AudioDataId(read.Take);
        if (dataId is null)
        {
            result = new QualityResult(read.Take.TakeId, block, clock.UtcNow, null, [], "AMP5331: this take has no audio to measure.");
        }
        else
        {
            try
            {
                byte[] bytes;
                using (Stream data = bank.OpenData(dataId))
                using (MemoryStream ms = new())
                {
                    data.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                (float[] samples, int rate) = AudioAnalysis.ReadWav(bytes);
                AudioMetrics m = AudioAnalysis.Measure(samples, rate);
                result = new QualityResult(read.Take.TakeId, block, clock.UtcNow, m, Evaluate(m, block, read.Plan.Plan.Intent, policy), null);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                result = new QualityResult(read.Take.TakeId, block, clock.UtcNow, null, [], "AMP5331: the audio of this take cannot be measured: " + ex.Message);
            }
        }

        Store(result);
        return result;
    }

    /// <summary>The stored result of a take, or null.</summary>
    public QualityResult? Result(string takeId)
    {
        string file = Path.Combine(root, ".ample", "quality", Safe(takeId) + ".json");
        return File.Exists(file) ? JsonSerializer.Deserialize<QualityResult>(File.ReadAllText(file), Options) : null;
    }

    /// <summary>Checks the newest take of every block that has audio.</summary>
    public IReadOnlyList<QualityResult> CheckLatest()
    {
        List<QualityResult> all = [];
        foreach (IGrouping<string, TakeRecord> g in bank.ListTakes().Where(t => !t.Tombstoned).GroupBy(t => bank.GetPlan(t.PlanId).Plan.BlockUuid))
        {
            TakeRecord newest = g.OrderByDescending(t => t.Created).First();
            if (AudioDataId(newest) is not null)
            {
                all.Add(Check(newest.TakeId));
            }
        }

        return all;
    }

    /// <summary>Applies the policy before a tag or a promote: the newest take of the master block must pass the rules the policy requires.</summary>
    /// <exception cref="AmpleException">AMP5333 when a required rule fails or there is nothing to check.</exception>
    public static void Enforce(string projectRoot, Takebank bank, IClock clock, string action)
    {
        ArgumentNullException.ThrowIfNull(bank);
        QualityPolicy? policy = QualityPolicy.Read(projectRoot);
        if (policy is null || !policy.Require.TryGetValue(action, out IReadOnlyList<string>? required) || required.Count == 0)
        {
            return;
        }

        TakeRecord? take = bank.ListTakes().Where(t => !t.Tombstoned && bank.GetPlan(t.PlanId).Plan.BlockPath == policy.MasterBlock && AudioDataId(t) is not null).OrderByDescending(t => t.Created).FirstOrDefault();
        if (take is null)
        {
            throw new AmpleException("AMP5333", $"The quality policy wants {string.Join(", ", required)} to pass before a {action}, but there is no take of '{policy.MasterBlock}' to measure. Build it first.");
        }

        QualityResult r = new QualityService(bank, clock, projectRoot).Check(take.TakeId);
        List<QualityFinding> failed = [.. r.Findings.Where(f => required.Contains(f.RuleId) && f.Status == "failed")];
        if (r.Error is not null || failed.Count > 0)
        {
            throw new AmpleException("AMP5333", $"The quality policy stops this {action}: {(r.Error ?? string.Join(" ", failed.Select(f => $"{f.RuleId} {f.Name}: {f.Message}")))} Fix the sound, or change .ample/quality.json.");
        }
    }

    /// <summary>The data blob of a take that holds a WAV.</summary>
    public static string? AudioDataId(TakeRecord take)
    {
        ArgumentNullException.ThrowIfNull(take);
        return take.Data.FirstOrDefault(d => d.Role is "audio" or "mix" or "master" or "vocal")?.DataId ?? (take.Data.Count > 0 ? take.Data[0].DataId : null);
    }

    private List<QualityFinding> Evaluate(AudioMetrics m, string block, string intent, QualityPolicy policy)
    {
        bool isMaster = block == policy.MasterBlock;
        bool preview = intent == "preview";
        List<QualityFinding> list = [];
        foreach (QualityRule rule in Rules.Where(r => !policy.Disabled.Contains(r.Id)))
        {
            double threshold = policy.Thresholds.TryGetValue(rule.Id, out double t) ? t : rule.Id == "Q003" ? policy.ToleranceLu : rule.Threshold;
            if (rule.AppliesTo == "master" && !isMaster || rule.AppliesTo == "final" && preview)
            {
                continue;
            }

            if (rule.Op == "requires")
            {
                list.Add(new QualityFinding(rule.Id, rule.Name, rule.Severity, "not-run", rule.Title + ": needs an analysis plugin, none is installed."));
                continue;
            }

            (double? value, string text) = rule.Metric switch
            {
                "clippedRuns" => ((double?)m.ClippedRuns, $"{m.ClippedRuns.ToString(CultureInfo.InvariantCulture)} clipped run(s)."),
                "truePeakDb" => (m.TruePeakDb, $"true peak {m.TruePeakDb.ToString("0.0", CultureInfo.InvariantCulture)} dBTP (limit {threshold.ToString("0.0", CultureInfo.InvariantCulture)})."),
                "lufsDistance" => (m.Lufs is { } l ? Math.Abs(l - policy.TargetLufs) : null, m.Lufs is { } l2 ? $"{l2.ToString("0.0", CultureInfo.InvariantCulture)} LUFS, target {policy.TargetLufs.ToString("0.0", CultureInfo.InvariantCulture)} within {threshold.ToString("0.0", CultureInfo.InvariantCulture)} LU." : "too short or too quiet to measure."),
                "longestSilenceSeconds" => (m.LongestSilenceSeconds, $"longest silence {m.LongestSilenceSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s (limit {threshold.ToString("0.0", CultureInfo.InvariantCulture)})."),
                "dcOffset" => (Math.Abs(m.DcOffset), $"DC offset {m.DcOffset.ToString("0.0000", CultureInfo.InvariantCulture)}."),
                "sampleRateMismatch" => (m.SampleRate == 44100 ? 0 : 1, $"{m.SampleRate.ToString(CultureInfo.InvariantCulture)} Hz."),
                _ => ((double?)null, "unknown metric."),
            };
            bool failed = value is { } v && v > threshold;
            list.Add(new QualityFinding(rule.Id, rule.Name, rule.Severity, value is null ? "not-run" : failed ? "failed" : "passed", $"{rule.Title}: {text}{(failed ? " " + rule.Explanation : string.Empty)}"));
        }

        return list;
    }

    private static string Safe(string id) => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private void Store(QualityResult r)
    {
        string folder = Path.Combine(root, ".ample", "quality");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Safe(r.TakeId) + ".json");
        File.WriteAllText(file + ".part", JsonSerializer.Serialize(r, Options) + "\n");
        File.Move(file + ".part", file, true);
    }
}
