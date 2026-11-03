using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;

namespace Ample.Plugins;

/// <summary>What a plugin needs from the machine.</summary>
/// <param name="Gpu">Needs the GPU: the host runs only one such job at a time.</param>
/// <param name="Network">Needs the network (the host and the project can refuse).</param>
/// <param name="DiskMb">Scratch disk in megabytes.</param>
/// <param name="Secrets">Names of secrets it needs; the host passes only these, as environment variables.</param>
public sealed record PluginNeeds(bool Gpu, bool Network, int DiskMb, IReadOnlyList<string> Secrets);

/// <summary>A plugin manifest (<c>plugin.json</c>), protocol version 1.</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Version">Plugin version.</param>
/// <param name="Title">Display name.</param>
/// <param name="Kind"><c>voice</c>, <c>analysis</c> or <c>effect</c>.</param>
/// <param name="ProtocolVersion">Protocol major version (1).</param>
/// <param name="Experimental">True until the protocol is frozen.</param>
/// <param name="Command">The program to start.</param>
/// <param name="Args">Its arguments (relative paths are relative to the plugin folder).</param>
/// <param name="Platforms">Supported platforms: windows, linux, macos.</param>
/// <param name="Blocks">Block types the plugin renders.</param>
/// <param name="Determinism"><c>exact</c>, <c>machine-exact</c> or <c>unverified</c>.</param>
/// <param name="ToolprintFields">Which facts the plugin puts into its toolprint.</param>
/// <param name="Needs">Resource needs.</param>
/// <param name="SettingsSchemaJson">JSON Schema (a small subset) of the plugin settings; drives the settings editor.</param>
/// <param name="Analyses">For an <c>analysis</c> plugin: the analyses it can make (tempo, key, loudness, spectrum, pitch, melody, ...), answered by the <c>analyze</c> request.</param>
public sealed record PluginManifest(string Id, string Version, string Title, string Kind, int ProtocolVersion, bool Experimental, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Platforms, IReadOnlyList<string> Blocks, string Determinism, IReadOnlyList<string> ToolprintFields, PluginNeeds Needs, string SettingsSchemaJson, IReadOnlyList<string>? Analyses = null)
{
    /// <summary>The newest protocol version this host speaks.</summary>
    public const int SupportedProtocol = 1;

    private static readonly string[] Kinds = ["voice", "analysis", "effect"];
    private static readonly string[] DeterminismValues = ["exact", "machine-exact", "unverified"];
    private static readonly string[] PlatformNames = ["windows", "linux", "macos"];

    /// <summary>Reads and validates a manifest; collects every problem instead of stopping at the first.</summary>
    /// <exception cref="AmpleException">AMP5270 when invalid, AMP5271 when the protocol is newer than this host.</exception>
    public static PluginManifest Parse(string json)
    {
        List<string> errors = [];
        PluginManifest? manifest = TryParse(json, errors);
        if (errors.Count > 0 || manifest is null)
        {
            string text = string.Join("; ", errors);
            throw new AmpleException(errors.Any(e => e.StartsWith("protocolVersion", StringComparison.Ordinal) && e.Contains("newer", StringComparison.Ordinal)) ? "AMP5271" : "AMP5270", "The plugin manifest is not valid: " + text);
        }

        return manifest;
    }

    /// <summary>Gets the problems of a manifest text (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        List<string> errors = [];
        TryParse(json, errors);
        return errors;
    }

    /// <summary>Determines whether the plugin runs on the current operating system.</summary>
    public bool SupportsThisPlatform => Platforms.Contains(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos");

    private static PluginManifest? TryParse(string json, List<string> errors)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            errors.Add("not valid JSON: " + ex.Message);
            return null;
        }

        using (doc)
        {
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
            {
                errors.Add("the manifest must be a JSON object");
                return null;
            }

            string? Str(string name, string? fallback = null) => r.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : fallback;
            string id = Str("id") ?? string.Empty;
            string version = Str("version") ?? string.Empty;
            string kind = Str("kind") ?? string.Empty;
            string determinism = Str("determinism") ?? string.Empty;
            if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]*$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                errors.Add("id must be lower-case letters, digits and dashes, starting with a letter");
            }

            if (!Regex.IsMatch(version, "^[0-9]+(\\.[0-9]+)*([-+][0-9A-Za-z.-]+)?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                errors.Add("version must be dotted numbers");
            }

            if (!Kinds.Contains(kind))
            {
                errors.Add("kind must be one of " + string.Join(", ", Kinds));
            }

            if (!DeterminismValues.Contains(determinism))
            {
                errors.Add("determinism must be one of " + string.Join(", ", DeterminismValues));
            }

            int protocol = r.TryGetProperty("protocolVersion", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;
            if (protocol > SupportedProtocol)
            {
                errors.Add($"protocolVersion {protocol} is newer than this host supports ({SupportedProtocol})");
            }
            else if (protocol < 1)
            {
                errors.Add("protocolVersion must be 1");
            }

            string command = string.Empty;
            List<string> args = [];
            if (r.TryGetProperty("entry", out JsonElement entry) && entry.ValueKind == JsonValueKind.Object)
            {
                command = entry.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                if (entry.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
                {
                    args = [.. a.EnumerateArray().Select(x => x.GetString() ?? string.Empty)];
                }
            }

            if (command.Length == 0)
            {
                errors.Add("entry.command is required");
            }

            List<string> platforms = Strings(r, "platforms");
            if (platforms.Count == 0 || platforms.Any(x => !PlatformNames.Contains(x)))
            {
                errors.Add("platforms must list windows, linux and/or macos");
            }

            List<string> blocks = Strings(r, "blocks");
            if (blocks.Count == 0 && kind != "analysis")
            {
                errors.Add("blocks must list at least one block type");
            }

            JsonElement needsEl = r.TryGetProperty("needs", out JsonElement n) && n.ValueKind == JsonValueKind.Object ? n : default;
            bool Flag(string name) => needsEl.ValueKind == JsonValueKind.Object && needsEl.TryGetProperty(name, out JsonElement f) && f.ValueKind == JsonValueKind.True;
            PluginNeeds needs = new(Flag("gpu"), Flag("network"), needsEl.ValueKind == JsonValueKind.Object && needsEl.TryGetProperty("diskMb", out JsonElement d) && d.TryGetInt32(out int mb) ? mb : 0, needsEl.ValueKind == JsonValueKind.Object ? Strings(needsEl, "secrets") : []);
            string schema = r.TryGetProperty("settings", out JsonElement s) && s.TryGetProperty("schema", out JsonElement sc) ? sc.GetRawText() : JsonSerializer.Serialize(new { type = "object", properties = new { } });
            return errors.Count > 0
                ? null
                : new PluginManifest(id, version, Str("title", id)!, kind, protocol, r.TryGetProperty("experimental", out JsonElement ex) && ex.ValueKind == JsonValueKind.True, command, args, platforms, blocks, determinism, Strings(r, "toolprintFields"), needs, schema, Strings(r, "analyses"));
        }
    }

    private static List<string> Strings(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : [];
}

/// <summary>Validates plugin settings against the small JSON Schema subset the manifests use (type, enum, minimum, maximum, pattern, required, default, additionalProperties).</summary>
public static class SettingsValidator
{
    /// <summary>Turns settings written as text (a song's settings line) into JSON typed by the schema: booleans, whole numbers, numbers, arrays as JSON, everything else text.</summary>
    public static string FromStrings(string schemaJson, IReadOnlyDictionary<string, string> values)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        JsonElement props = schema.RootElement.TryGetProperty("properties", out JsonElement p) ? p : default;
        Dictionary<string, object?> typed = [];
        foreach ((string name, string text) in values)
        {
            string kind = props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out JsonElement rule) && rule.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "string" : "string";
            typed[name] = kind switch
            {
                "boolean" => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("on", StringComparison.OrdinalIgnoreCase),
                "integer" when long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long n) => n,
                "number" when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) => d,
                "array" => JsonSerializer.Deserialize<object>(text),
                _ => text,
            };
        }

        return JsonSerializer.Serialize(typed);
    }

    /// <summary>Checks the values and returns the problems and the values with defaults filled in.</summary>
    public static (IReadOnlyList<string> Errors, string FilledJson) Validate(string schemaJson, string valuesJson)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        using JsonDocument values = JsonDocument.Parse(string.IsNullOrWhiteSpace(valuesJson) ? "{}" : valuesJson);
        List<string> errors = [];
        Dictionary<string, object?> filled = [];
        JsonElement props = schema.RootElement.TryGetProperty("properties", out JsonElement p) ? p : default;
        bool closed = schema.RootElement.TryGetProperty("additionalProperties", out JsonElement ap) && ap.ValueKind == JsonValueKind.False;
        foreach (JsonProperty v in values.RootElement.EnumerateObject())
        {
            if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty(v.Name, out JsonElement rule))
            {
                if (closed)
                {
                    errors.Add($"'{v.Name}' is not a setting of this plugin");
                }

                filled[v.Name] = JsonSerializer.Deserialize<object>(v.Value.GetRawText());
                continue;
            }

            Check(v.Name, v.Value, rule, errors);
            filled[v.Name] = JsonSerializer.Deserialize<object>(v.Value.GetRawText());
        }

        if (props.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty rule in props.EnumerateObject().Where(x => x.Value.TryGetProperty("default", out _) && !filled.ContainsKey(x.Name)))
            {
                filled[rule.Name] = JsonSerializer.Deserialize<object>(rule.Value.GetProperty("default").GetRawText());
            }
        }

        if (schema.RootElement.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
        {
            errors.AddRange(req.EnumerateArray().Select(x => x.GetString()!).Where(name => !filled.ContainsKey(name)).Select(name => $"'{name}' is required"));
        }

        return (errors, JsonSerializer.Serialize(filled));
    }

    private static void Check(string name, JsonElement value, JsonElement rule, List<string> errors)
    {
        string type = rule.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        bool typeOk = type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => true,
        };
        if (!typeOk)
        {
            errors.Add($"'{name}' must be a {type}");
            return;
        }

        if (rule.TryGetProperty("enum", out JsonElement e) && e.ValueKind == JsonValueKind.Array && !e.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText()))
        {
            errors.Add($"'{name}' must be one of {string.Join(", ", e.EnumerateArray().Select(x => x.ToString()))}");
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (rule.TryGetProperty("minimum", out JsonElement min) && value.GetDouble() < min.GetDouble())
            {
                errors.Add($"'{name}' must be at least {min.GetRawText()}");
            }

            if (rule.TryGetProperty("maximum", out JsonElement max) && value.GetDouble() > max.GetDouble())
            {
                errors.Add($"'{name}' must be at most {max.GetRawText()}");
            }
        }

        if (value.ValueKind == JsonValueKind.String && rule.TryGetProperty("pattern", out JsonElement pat) && !Regex.IsMatch(value.GetString()!, pat.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            errors.Add($"'{name}' does not match {pat.GetString()}");
        }
    }
}
