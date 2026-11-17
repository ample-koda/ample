using System.Globalization;

namespace Ample.Language;

/// <summary>The layers that can set a logging or assertion option, lowest precedence first (plan D3): the server default, the project, the song, the component instances (outermost first) and the session (editor, CLI, API).</summary>
public enum ConfigLayer
{
    /// <summary>The built-in defaults.</summary>
    Server,

    /// <summary>The settings of the project (<c>project.json</c>).</summary>
    Project,

    /// <summary>The <c>settings { }</c> of the song.</summary>
    Song,

    /// <summary>A component instance.</summary>
    Instance,

    /// <summary>A temporary override: the editor, the CLI or a build API call.</summary>
    Session,
}

/// <summary>Where a value comes from, so that the question "why is trace on" has an answer.</summary>
/// <param name="Layer">The layer.</param>
/// <param name="Origin">The file or the name of the instance, or null.</param>
/// <param name="Line">The 1-based line, or 0.</param>
public sealed record ConfigSource(ConfigLayer Layer, string? Origin = null, int Line = 0)
{
    /// <summary>Gets the words that say it.</summary>
    public override string ToString() => Layer switch
    {
        ConfigLayer.Server => "the default",
        ConfigLayer.Project => "the project settings",
        ConfigLayer.Song => $"the settings of the song{At}",
        ConfigLayer.Instance => $"the instance {Origin}{At}",
        _ => $"this session ({Origin ?? "override"})",
    };

    private string At => Line > 0 ? $" (line {Line})" : string.Empty;
}

/// <summary>One layer of options: the keys it sets (a key it does not set, or sets to <c>inherit</c>, is inherited).</summary>
/// <param name="Source">Where the layer comes from.</param>
/// <param name="Values">The keys with their text and the line they stand on (0 when unknown).</param>
public sealed record ConfigLayerInput(ConfigSource Source, IReadOnlyDictionary<string, (string Value, int Line)> Values);

/// <summary>An effective option and the layer that decided it.</summary>
/// <param name="Value">The value as text.</param>
/// <param name="Source">Where it was decided.</param>
public sealed record EffectiveValue(string Value, ConfigSource Source);

/// <summary>
/// The logging and assertion options of an evaluation, resolved from the layers (plan D3). <c>inherit</c> (or no value) takes the value of the layer below; <c>off</c> is a value that wins over what is below it; every value
/// carries the layer that decided it. A key that is invalid or over its cap is reported, never silently changed. Logging and assertions never enter a cache key, a hash or a recipe.
/// </summary>
public sealed class ObservationConfig
{
    /// <summary>The keys that the layers may set, with their default.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["logging.level"] = "warning",
        ["logging.stack"] = "errors",
        ["logging.stackMaxDepth"] = "24",
        ["logging.maxPerSite"] = "3",
        ["logging.maxTotal"] = "200",
        ["logging.traceMaxPerSite"] = "100",
        ["logging.traceMaxTotal"] = "5000",
        ["logging.exactLast"] = "off",
        ["logging.timeBudget"] = "2000",
        ["logging.fuel"] = "200000",
        ["logging.maxContextBytes"] = "4096",
        ["diagnostics.assertions"] = "off",
    };

    private static readonly Dictionary<string, long> Caps = new(StringComparer.Ordinal)
    {
        ["logging.stackMaxDepth"] = 64,
        ["logging.maxPerSite"] = 1000,
        ["logging.maxTotal"] = 10_000,
        ["logging.traceMaxPerSite"] = 10_000,
        ["logging.traceMaxTotal"] = 100_000,
        ["logging.timeBudget"] = 2_000,
        ["logging.fuel"] = 200_000,
        ["logging.maxContextBytes"] = 4096,
    };

    private readonly Dictionary<string, EffectiveValue> values;

    private ObservationConfig(Dictionary<string, EffectiveValue> values, IReadOnlyList<string> problems)
    {
        this.values = values;
        Problems = problems;
    }

    /// <summary>Gets what was wrong in the layers (a bad value or a value over its cap), as messages with the place.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Gets the level of the log view.</summary>
    public LogLevel Level => Enum.Parse<LogLevel>(this.values["logging.level"].Value, ignoreCase: true);

    /// <summary>Gets whether assertions run.</summary>
    public bool Assertions => this.values["diagnostics.assertions"].Value == "on";

    internal bool ExactLast => this.values["logging.exactLast"].Value == "on";

    internal int ObservationFuel => Int("logging.fuel");

    internal TimeSpan ObservationTime => TimeSpan.FromMilliseconds(Int("logging.timeBudget"));

    internal int ContextBytes => Int("logging.maxContextBytes");

    internal int StackMaxDepth => Int("logging.stackMaxDepth");

    internal string StackMode => this.values["logging.stack"].Value;

    /// <summary>Gets the budgets of the sink.</summary>
    public ObservationBudgets Budgets => new(
        MaxPerSite: Int("logging.maxPerSite"), MaxTotal: Int("logging.maxTotal"), TraceMaxPerSite: Int("logging.traceMaxPerSite"), TraceMaxTotal: Int("logging.traceMaxTotal"), StackMaxDepth: Int("logging.stackMaxDepth"));

    /// <summary>Whether a key is one of the options.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True for <c>logging.level</c> and the others of <see cref="Defaults"/>.</returns>
    public static bool IsOption(string key) => Defaults.ContainsKey(key);

    /// <summary>Checks one value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value as written.</param>
    /// <returns>What is wrong with it, or null.</returns>
    public static string? Validate(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        string v = value.Trim().Trim('"');
        if (v == "inherit") return null;
        switch (key)
        {
            case "logging.level": return v is "off" or "warning" or "info" or "debug" or "trace" ? null : $"{key} is off, warning, info, debug, trace or inherit, not \"{v}\".";
            case "logging.stack": return v is "off" or "errors" or "warnings" or "all" ? null : $"{key} is off, errors, warnings, all or inherit, not \"{v}\".";
            case "diagnostics.assertions" or "logging.exactLast": return v is "on" or "off" ? null : $"{key} is on, off or inherit, not \"{v}\".";
            default:
                if (!Caps.TryGetValue(key, out long cap)) return $"{key} is not a logging or assertion option.";
                if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 1) return $"{key} is a whole number of at least 1, not \"{v}\".";
                return n > cap ? $"{key} is at most {cap} (this is {n})." : null;
        }
    }

    /// <summary>Resolves the layers.</summary>
    /// <param name="layers">The layers from the lowest precedence to the highest; the built-in defaults are always below them.</param>
    /// <returns>The effective options.</returns>
    public static ObservationConfig Resolve(IReadOnlyList<ConfigLayerInput> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Dictionary<string, EffectiveValue> result = Defaults.ToDictionary(d => d.Key, d => new EffectiveValue(d.Value, new ConfigSource(ConfigLayer.Server)), StringComparer.Ordinal);
        List<string> problems = [];
        foreach (ConfigLayerInput layer in layers)
        {
            foreach ((string key, (string raw, int line)) in layer.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (!Defaults.ContainsKey(key)) continue;
                string value = raw.Trim().Trim('"');
                if (value == "inherit") continue;
                if (Validate(key, value) is { } problem)
                {
                    problems.Add($"{problem} ({layer.Source}{(line > 0 ? $", line {line}" : string.Empty)})");
                    continue;
                }

                result[key] = new EffectiveValue(value, layer.Source with { Line = line > 0 ? line : layer.Source.Line });
            }
        }

        return new ObservationConfig(result, problems);
    }

    /// <summary>Says why an option has its value.</summary>
    /// <param name="key">The key.</param>
    /// <returns>For example <c>logging.level is trace: set by the instance up (line 12)</c>.</returns>
    public string Explain(string key)
    {
        EffectiveValue v = this.values[key];
        return $"{key} is {v.Value}: set by {v.Source}";
    }

    /// <summary>Gets the effective value of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value with its source.</returns>
    public EffectiveValue Get(string key) => this.values[key];

    private int Int(string key) => int.Parse(this.values[key].Value, CultureInfo.InvariantCulture);
}
