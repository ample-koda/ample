using Ample.Audio.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Server;

/// <summary>
/// What this server allows projects to use of Codex. These are capability switches only: they say what a project MAY choose, not what it uses. A project that is set to something
/// the server no longer allows is shown as unavailable; nothing falls back to another identity or mode by itself.
/// </summary>
internal sealed record AiPolicy(bool CodexEnabled, bool AllowServerAccount, bool AllowDeviceId, bool AllowInheritance, bool AllowManual, bool AllowAuto, bool AllowUnsafe, string? CodexPath, string? Model = null, string? Effort = null, bool UnsafeNoSandbox = false)
{
    /// <summary>Gets the defaults: everything allowed.</summary>
    public static AiPolicy Default { get; } = new(true, true, true, true, true, true, true, null);

    private static string FileOf() => Path.Combine(AmpleHome.DefaultPath(), "ai-policy.json");

    /// <summary>Reads the policy of this server (the defaults when there is no file).</summary>
    public static AiPolicy Read() => FromFile() with { Model = FromFile().Model ?? Environment.GetEnvironmentVariable("AMPLE_CODEX_MODEL") ?? DefaultModel, Effort = FromFile().Effort ?? Environment.GetEnvironmentVariable("AMPLE_CODEX_EFFORT") };

    /// <summary>The model that is used when nothing else is chosen (fast and affordable). <c>default</c> as the model means: let Codex choose its own.</summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The model to hand to Codex, or null to let Codex use its own default.</summary>
    public string? ModelToUse => string.IsNullOrWhiteSpace(Model) || string.Equals(Model, "default", StringComparison.OrdinalIgnoreCase) ? null : Model;

    /// <summary>The reasoning effort to hand to Codex, or null for the default of the model.</summary>
    public string? EffortToUse => string.IsNullOrWhiteSpace(Effort) || string.Equals(Effort, "default", StringComparison.OrdinalIgnoreCase) ? null : Effort;

    private static AiPolicy FromFile()
    {
        try
        {
            JsonObject o = JsonNode.Parse(File.ReadAllText(FileOf()))!.AsObject();
            bool Flag(string name, bool fallback) => o[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
            return new AiPolicy(Flag("codexEnabled", true), Flag("allowServerAccount", true), Flag("allowDeviceId", true), Flag("allowInheritance", true), Flag("allowManual", true), Flag("allowAuto", true), Flag("allowUnsafe", true), o["codexPath"]?.GetValue<string>(), o["model"]?.GetValue<string>(), o["effort"]?.GetValue<string>(), Flag("unsafeNoSandbox", false));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    /// <summary>Writes the policy.</summary>
    public void Write()
    {
        Directory.CreateDirectory(AmpleHome.DefaultPath());
        File.WriteAllText(FileOf(), ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>The policy as JSON.</summary>
    /// <summary>The path of the policy file, which only the administrator of this machine edits to allow more.</summary>
    public static string FilePath => FileOf();

    /// <summary>
    /// What a request over the API may NOT do: the page of a project can switch capabilities off, but never on, set the sandbox off or choose which program is started as Codex.
    /// Whoever holds the token of a project is not the administrator of the machine; allowing more is done in the policy file.
    /// </summary>
    public string? Loosening(AiPolicy next)
    {
        bool[] before = [CodexEnabled, AllowServerAccount, AllowDeviceId, AllowInheritance, AllowManual, AllowAuto, AllowUnsafe, UnsafeNoSandbox];
        bool[] after = [next.CodexEnabled, next.AllowServerAccount, next.AllowDeviceId, next.AllowInheritance, next.AllowManual, next.AllowAuto, next.AllowUnsafe, next.UnsafeNoSandbox];
        string[] names = ["codexEnabled", "allowServerAccount", "allowDeviceId", "allowInheritance", "allowManual", "allowAuto", "allowUnsafe", "unsafeNoSandbox"];
        for (int i = 0; i < names.Length; i++)
        {
            if (after[i] && !before[i])
            {
                return names[i];
            }
        }

        return string.Equals(CodexPath, next.CodexPath, StringComparison.Ordinal) ? null : "codexPath";
    }

    public JsonObject ToJson() => new() { ["codexEnabled"] = CodexEnabled, ["allowServerAccount"] = AllowServerAccount, ["allowDeviceId"] = AllowDeviceId, ["allowInheritance"] = AllowInheritance, ["allowManual"] = AllowManual, ["allowAuto"] = AllowAuto, ["allowUnsafe"] = AllowUnsafe, ["codexPath"] = CodexPath, ["model"] = Model, ["effort"] = Effort, ["unsafeNoSandbox"] = UnsafeNoSandbox };

    /// <summary>Applies the fields of a request on top of this policy.</summary>
    public AiPolicy With(JsonElement b)
    {
        bool Flag(string name, bool current) => b.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : current;
        return new AiPolicy(Flag("codexEnabled", CodexEnabled), Flag("allowServerAccount", AllowServerAccount), Flag("allowDeviceId", AllowDeviceId), Flag("allowInheritance", AllowInheritance), Flag("allowManual", AllowManual), Flag("allowAuto", AllowAuto), Flag("allowUnsafe", AllowUnsafe), b.TryGetProperty("codexPath", out JsonElement p) && p.ValueKind == JsonValueKind.String ? (p.GetString() is { Length: > 0 } s ? s : null) : CodexPath, Text(b, "model", Model), Text(b, "effort", Effort), Flag("unsafeNoSandbox", UnsafeNoSandbox));
    }

    private static string? Text(JsonElement b, string name, string? current) => b.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? (p.GetString() is { Length: > 0 } t ? t : null) : current;

    /// <summary>Whether a project may use this assistant.</summary>
    public bool AllowsAssistant(string assistant) => assistant switch
    {
        "none" => true,
        "codex-server" => CodexEnabled && AllowServerAccount,
        "codex-device" => CodexEnabled && AllowDeviceId,
        _ => false,
    };

    /// <summary>Whether a project may use this execution mode.</summary>
    public bool AllowsMode(string mode) => mode switch { "manual" => AllowManual, "auto" => AllowAuto, "unsafe" => AllowUnsafe, _ => false };
}
