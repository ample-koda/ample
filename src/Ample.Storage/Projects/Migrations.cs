using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Storage.Projects;

/// <summary>Upgrades <c>project.json</c> by one format version. Pure text to text, so it can be tested and run on a copy.</summary>
public interface IMigration
{
    /// <summary>Gets the version this migration reads.</summary>
    int FromVersion { get; }

    /// <summary>Gets the version it produces.</summary>
    int ToVersion { get; }

    /// <summary>Transforms the file content.</summary>
    string Apply(string projectJson);
}

/// <summary>The list of migrations, oldest first.</summary>
public static class Migrations
{
    /// <summary>Gets all known migrations.</summary>
    public static IReadOnlyList<IMigration> All { get; } = [new V0ToV1()];

    /// <summary>Upgrades a project file to the current version, one step at a time.</summary>
    public static string UpgradeToCurrent(string json, IReadOnlyList<IMigration>? migrations = null)
    {
        int version = ProjectFile.ReadFormatVersion(json);
        string text = json;
        while (version < ProjectLayout.CurrentFormatVersion)
        {
            IMigration step = (migrations ?? All).FirstOrDefault(m => m.FromVersion == version)
                ?? throw new AmpleException("AMP5207", $"There is no migration from project format {version}.");
            text = step.Apply(text);
            version = step.ToVersion;
        }

        return text;
    }

    /// <summary>Version 0 (early drafts) had a numeric <c>seed</c>, no settings and no format version.</summary>
    private sealed class V0ToV1 : IMigration
    {
        public int FromVersion => 0;

        public int ToVersion => 1;

        public string Apply(string projectJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(projectJson);
                JsonElement root = doc.RootElement;
                ulong seed = root.TryGetProperty("seed", out JsonElement s) && s.TryGetUInt64(out ulong v) ? v : 0;
                ProjectManifest manifest = new(
                    Guid.Parse(root.GetProperty("id").GetString()!),
                    root.GetProperty("name").GetString()!,
                    DateTimeOffset.Parse(root.GetProperty("created").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    1,
                    seed,
                    ProjectSettings.Default);
                return ProjectFile.Serialize(manifest);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                throw new AmpleException("AMP5205", "project.json (format 0) could not be migrated: " + ex.Message);
            }
        }
    }
}
