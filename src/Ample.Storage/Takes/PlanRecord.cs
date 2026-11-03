using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Storage.Takes;

/// <summary>An input of a plan: a stored data blob or another take, optionally with the toolprint that made it.</summary>
/// <param name="Kind"><c>data</c>, <c>take</c>, <c>sample</c> (a WAV file of the project) or <c>file</c> (a text file the block reads, such as lyrics), the last two by content hash with the project path in the toolprint field.</param>
/// <param name="Id">The data id or take id.</param>
/// <param name="Toolprint">Toolprint of the producer, or null.</param>
public sealed record PlanInput(string Kind, string Id, string? Toolprint);

/// <summary>A named seed used by a plan.</summary>
/// <param name="Name">Seed name.</param>
/// <param name="Value">Seed value.</param>
public sealed record PlanSeed(string Name, ulong Value);

/// <summary>The complete recipe of a render: everything needed to understand (and, when the plugin allows it, reproduce) a take. Stored before the take exists (the Always rule). Its identity is the hash of its canonical JSON and does not include the time.</summary>
/// <param name="ProjectId">Project id.</param>
/// <param name="BlockUuid">Stable block uuid.</param>
/// <param name="BlockPath">Block path or name at the time.</param>
/// <param name="SourceHash">Hash of the block's source text.</param>
/// <param name="Inputs">Inputs (data and take ids).</param>
/// <param name="Seeds">Seeds, any order (canonical form sorts them by name).</param>
/// <param name="RngVersion">The stable random version.</param>
/// <param name="PluginId">Plugin or built-in renderer id.</param>
/// <param name="PluginVersion">Plugin version.</param>
/// <param name="ToolprintJson">Toolprint as JSON text (plugin, model hashes, coarse hardware class); canonicalised.</param>
/// <param name="SettingsJson">Render settings as JSON text; canonicalised.</param>
/// <param name="Intent"><c>preview</c>, <c>bake</c>, <c>variant</c>, <c>freeze</c> or <c>verify</c>.</param>
/// <param name="ParentPlanId">The plan this one regenerates or derives from, or null.</param>
public sealed record PlanRecord(
    Guid ProjectId,
    string BlockUuid,
    string BlockPath,
    string SourceHash,
    IReadOnlyList<PlanInput> Inputs,
    IReadOnlyList<PlanSeed> Seeds,
    int RngVersion,
    string PluginId,
    string PluginVersion,
    string ToolprintJson,
    string SettingsJson,
    string Intent,
    string? ParentPlanId);

/// <summary>The canonical form of a plan and its identity. FROZEN: the canonical text of a plan never changes, otherwise plan ids of existing takes would change. A new form needs a new schema tag.</summary>
public static class PlanCanonical
{
    /// <summary>The schema tag written into every canonical plan.</summary>
    public const string Schema = "ample-plan/1";

    /// <summary>Gets the allowed intents.</summary>
    public static IReadOnlyList<string> Intents { get; } = ["preview", "bake", "variant", "freeze", "verify"];

    /// <summary>Writes the canonical JSON: fixed property order, seeds sorted by name, other objects sorted by key, no white space, non-ASCII escaped.</summary>
    public static string Serialize(PlanRecord plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Intents.Contains(plan.Intent))
        {
            throw new AmpleException("AMP5225", $"Unknown plan intent '{plan.Intent}'.");
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteNumber("rngVersion", plan.RngVersion);
            w.WriteString("projectId", plan.ProjectId.ToString("D"));
            w.WriteString("blockUuid", plan.BlockUuid);
            w.WriteString("blockPath", plan.BlockPath);
            w.WriteString("sourceHash", plan.SourceHash);
            w.WriteStartArray("inputs");
            foreach (PlanInput i in plan.Inputs)
            {
                w.WriteStartObject();
                w.WriteString("kind", i.Kind);
                w.WriteString("id", i.Id);
                if (i.Toolprint is null)
                {
                    w.WriteNull("toolprint");
                }
                else
                {
                    w.WriteString("toolprint", i.Toolprint);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("seeds");
            foreach (PlanSeed s in plan.Seeds.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteString("value", s.Value.ToString(CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("plugin");
            w.WriteString("id", plan.PluginId);
            w.WriteString("version", plan.PluginVersion);
            w.WriteEndObject();
            w.WritePropertyName("toolprint");
            WriteCanonical(w, plan.ToolprintJson);
            w.WritePropertyName("settings");
            WriteCanonical(w, plan.SettingsJson);
            w.WriteString("intent", plan.Intent);
            if (plan.ParentPlanId is null)
            {
                w.WriteNull("parentPlanId");
            }
            else
            {
                w.WriteString("parentPlanId", plan.ParentPlanId);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Gets the plan id: <c>sha256:</c> plus the hash of the canonical JSON.</summary>
    public static string PlanId(PlanRecord plan) => IdOf(Serialize(plan));

    /// <summary>Gets the id of a canonical plan text.</summary>
    public static string IdOf(string canonicalJson) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    /// <summary>Reads a canonical plan back.</summary>
    public static PlanRecord Parse(string canonicalJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(canonicalJson);
            JsonElement r = doc.RootElement;
            if (r.GetProperty("schema").GetString() != Schema)
            {
                throw new AmpleException("AMP5225", "Unknown plan schema.");
            }

            JsonElement plugin = r.GetProperty("plugin");
            JsonElement parent = r.GetProperty("parentPlanId");
            return new PlanRecord(
                Guid.Parse(r.GetProperty("projectId").GetString()!),
                r.GetProperty("blockUuid").GetString()!,
                r.GetProperty("blockPath").GetString()!,
                r.GetProperty("sourceHash").GetString()!,
                [.. r.GetProperty("inputs").EnumerateArray().Select(i => new PlanInput(i.GetProperty("kind").GetString()!, i.GetProperty("id").GetString()!, i.GetProperty("toolprint").ValueKind == JsonValueKind.Null ? null : i.GetProperty("toolprint").GetString()))],
                [.. r.GetProperty("seeds").EnumerateArray().Select(s => new PlanSeed(s.GetProperty("name").GetString()!, ulong.Parse(s.GetProperty("value").GetString()!, CultureInfo.InvariantCulture)))],
                r.GetProperty("rngVersion").GetInt32(),
                plugin.GetProperty("id").GetString()!,
                plugin.GetProperty("version").GetString()!,
                r.GetProperty("toolprint").GetRawText(),
                r.GetProperty("settings").GetRawText(),
                r.GetProperty("intent").GetString()!,
                parent.ValueKind == JsonValueKind.Null ? null : parent.GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new AmpleException("AMP5225", "The stored plan is not valid: " + ex.Message);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter w, string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            WriteElement(w, doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5225", "Plan settings or toolprint are not valid JSON: " + ex.Message);
        }
    }

    private static void WriteElement(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (JsonProperty p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteElement(w, p.Value);
                }

                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    WriteElement(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
