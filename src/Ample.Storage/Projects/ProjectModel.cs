using System.Globalization;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Random;

namespace Ample.Storage.Projects;

/// <summary>Settings of the black box journal.</summary>
/// <param name="BudgetBytes">Total size budget (default 10 GiB).</param>
/// <param name="FullDetailDays">Days kept in full detail (default 90).</param>
/// <param name="ThinnedDays">Days kept thinned after that (default 365).</param>
public sealed record BlackBoxSettings(long BudgetBytes, int FullDetailDays, int ThinnedDays);

/// <summary>Cleanup policies.</summary>
/// <param name="ArchivedLeafDays">Automatic age limit for archived lines, or null for only on request.</param>
public sealed record CleanupSettings(int? ArchivedLeafDays);

/// <summary>A project-wide Project panel column and its pixel width.</summary>
public sealed record ProjectPanelColumn(string Id, int Width);

/// <summary>Shared Project panel layout stored in the project manifest.</summary>
public sealed record ProjectPanelSettings(string View, int TileSize, string SortColumn, bool SortDescending, IReadOnlyList<ProjectPanelColumn> Columns)
{
    /// <summary>The initial layout used by projects that predate this setting.</summary>
    public static ProjectPanelSettings Default { get; } = new("tiles", 2, "name", false, [new("name", 260), new("kind", 90), new("size", 100), new("modified", 180)]);
}

/// <summary>Safe saves: a checkpoint around every save, and an autosave of unsaved changes every <paramref name="Minutes"/> minutes.</summary>
/// <param name="Enabled">Whether the setting is on.</param>
/// <param name="Minutes">The autosave interval; one of <see cref="Steps"/>.</param>
public sealed record AutoCheckpointSettings(bool Enabled, int Minutes)
{
    /// <summary>The choices of the interval slider, in minutes.</summary>
    public static IReadOnlyList<int> Steps { get; } = [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240];

    /// <summary>Gets the default: off, every 10 minutes when turned on.</summary>
    public static AutoCheckpointSettings Default { get; } = new(false, 10);
}

/// <summary>The AI assistant of a project: which Codex identity it uses, how much freedom Codex has while it works, and what the user has acknowledged. Secrets are never stored here.</summary>
/// <param name="Assistant"><c>none</c>, <c>codex-server</c> (the Codex account of the user the server runs as) or <c>codex-device</c> (a Codex identity of this project, linked with the device flow).</param>
/// <param name="Mode"><c>manual</c>, <c>auto</c> or <c>unsafe</c>: Codex action approval while it prepares a proposal.</param>
/// <param name="Acknowledged">When the risks of an AI coding agent were acknowledged for this project.</param>
/// <param name="UnsafeAcknowledged">When the extra risks of the unsafe mode were acknowledged.</param>
/// <param name="LinkedAccount">A label of the linked Codex account (not a secret), for the device identity.</param>
/// <param name="LinkedAt">When the device identity was linked.</param>
public sealed record AiAssistantSettings(string Assistant, string Mode, DateTimeOffset? Acknowledged, DateTimeOffset? UnsafeAcknowledged, string? LinkedAccount, DateTimeOffset? LinkedAt)
{
    /// <summary>Whether AI proposal builds may reuse exact-plan audio from the active project's takebank through a read-only cache view.</summary>
    public bool ExposeTakebankCache { get; init; } = true;
    /// <summary>Gets the assistants a project can choose.</summary>
    public static IReadOnlyList<string> Assistants { get; } = ["none", "codex-server", "codex-device"];

    /// <summary>Gets the execution modes a project can choose.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["manual", "auto", "unsafe"];

    /// <summary>Gets the default: no assistant, and automatic mode once one is chosen.</summary>
    public static AiAssistantSettings Default { get; } = new("none", "auto", null, null, null, null);
}

/// <summary>Koda, the helper of the project: the language they speak ("en" or "nl"). In Dutch Koda gives the English term of AMPLE next to the Dutch one, to learn the terms.</summary>
/// <param name="Language">en or nl.</param>
/// <param name="Model">The model Koda uses by default (empty: the model of the assistant).</param>
/// <param name="Effort">The thinking level Koda uses by default (empty: the setting of the assistant).</param>
public sealed record KodaSettings(string Language, string Model = "", string Effort = "")
{
    /// <summary>Gets the default: English.</summary>
    public static KodaSettings Default { get; } = new("en");

    /// <summary>Gets whether a language is one Koda speaks.</summary>
    public static bool IsKnown(string? language) => language is "en" or "nl";
}

/// <summary>Independent execution routes for expensive audio capabilities.</summary>
/// <param name="Song">Song generation route.</param>
/// <param name="Voice">Voice generation route.</param>
/// <param name="Separation">Stem separation route.</param>
public sealed record FaerRoutes(string Song, string Voice, string Separation)
{
    /// <summary>Routes every capability to the local machine.</summary>
    public static FaerRoutes Default { get; } = new("local", "local", "local");

    /// <summary>Gets the default ACE model for <c>ace-music</c> blocks (a family keyword or a .gguf file name), or empty: the song's own settings and the plugin default decide.</summary>
    public string SongModel { get; init; } = string.Empty;

    /// <summary>Gets the default ACE model for <c>ai-voice</c> blocks, or empty.</summary>
    public string VoiceModel { get; init; } = string.Empty;
}

/// <summary>The first-time setup wizard of a project (plan/first-run-wizard.md). The wizard shows while <see cref="DoneVersion"/> is lower than <see cref="WizardVersion"/>,
/// so a later AMPLE can raise the version and every project shows the wizard again (with only the pages that are new since the version it has done).</summary>
/// <param name="Origin"><c>new</c> for a project made by AMPLE since the wizard exists, <c>existing</c> for a project that was there before (and for a file without the field).</param>
/// <param name="DoneVersion">The number of the last wizard that was walked through; 0 is never.</param>
/// <param name="DoneAt">When it was walked through (UTC), or null.</param>
public sealed record FirstRunSettings(string Origin, int DoneVersion, DateTimeOffset? DoneAt)
{
    /// <summary>The number of the wizard that is current in this program.</summary>
    public const int WizardVersion = 1;

    /// <summary>A project that was there before the wizard, or whose file has no such field: the wizard has not been done.</summary>
    public static FirstRunSettings Default { get; } = new("existing", 0, null);

    /// <summary>A project that is made now: its wizard is still to be done and ends with the demo song.</summary>
    public static FirstRunSettings NewProject { get; } = new("new", 0, null);

    /// <summary>Gets whether the wizard still has to be walked through.</summary>
    public bool Pending => DoneVersion < WizardVersion;

    /// <summary>Gets whether a text is an origin of a project.</summary>
    public static bool IsKnownOrigin(string? origin) => origin is "new" or "existing";

    /// <summary>Gets this state after the wizard was walked through at a moment.</summary>
    public FirstRunSettings Completed(DateTimeOffset now) => this with { DoneVersion = WizardVersion, DoneAt = now };
}

/// <summary>Project settings stored in <c>project.json</c>.</summary>
/// <param name="BlackBox">Black box journal settings.</param>
/// <param name="Cleanup">Cleanup policies.</param>
/// <param name="Mounts">Ids of linked folders (filled by the library, chunk 85).</param>
/// <param name="DefaultEngine">Default engine or plugin id, or null.</param>
/// <param name="ProjectPanel">Shared Project panel layout.</param>
/// <param name="AutoCheckpoint">Checkpoints around every save and the autosave.</param>
/// <param name="Ai">The AI assistant of the project.</param>
public sealed record ProjectSettings(BlackBoxSettings BlackBox, CleanupSettings Cleanup, IReadOnlyList<string> Mounts, string? DefaultEngine, ProjectPanelSettings ProjectPanel, AutoCheckpointSettings AutoCheckpoint, AiAssistantSettings Ai)
{
    /// <summary>Independent execution routes for expensive audio capabilities.</summary>
    public FaerRoutes Faer { get; init; } = FaerRoutes.Default;
    /// <summary>Gets how many blocks a build of this project may render at the same time, or null for what the server allows (one per core). A project can only ask for fewer than the server allows, never more.</summary>
    public int? BuildParallelism { get; init; }
    /// <summary>What the project says about capabilities (clock, os-random, environment ... to on or off; environment.allow to a list of names): the layer between the server and the song. A project can only narrow what the server allows, a song only what the project allows.</summary>
    public IReadOnlyDictionary<string, string> Capabilities { get; init; } = new Dictionary<string, string>();
    /// <summary>The helper Koda: its language.</summary>
    public KodaSettings Koda { get; init; } = KodaSettings.Default;
    /// <summary>The first-time setup wizard of the project: where the project came from and which wizard version was walked through. A project file without it has never done the wizard.</summary>
    public FirstRunSettings FirstRun { get; init; } = FirstRunSettings.Default;
    /// <summary>Gets the default settings (10 GiB budget, 90 days full detail, a year thinned, no automatic cleanup).</summary>
    public static ProjectSettings Default { get; } = new(new BlackBoxSettings(10L * 1024 * 1024 * 1024, 90, 365), new CleanupSettings(null), [], null, ProjectPanelSettings.Default, AutoCheckpointSettings.Default, AiAssistantSettings.Default);
}

/// <summary>The content of <c>.ample/project.json</c>.</summary>
/// <param name="Id">Stable project id; survives moves and renames.</param>
/// <param name="Name">Project name.</param>
/// <param name="Created">When the project was created (UTC).</param>
/// <param name="FormatVersion">Project format version.</param>
/// <param name="MasterSeed">The master seed.</param>
/// <param name="Settings">Settings.</param>
public sealed record ProjectManifest(Guid Id, string Name, DateTimeOffset Created, int FormatVersion, ulong MasterSeed, ProjectSettings Settings);

/// <summary>Names of the folders and files of a project.</summary>
public static class ProjectLayout
{
    /// <summary>The current project format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The mandatory folder that makes a folder a project.</summary>
    public const string AmpleFolder = ".ample";

    /// <summary>The project file inside <see cref="AmpleFolder"/>.</summary>
    public const string ProjectFile = "project.json";

    /// <summary>The folders every project has inside <see cref="AmpleFolder"/>.</summary>
    public static IReadOnlyList<string> RequiredFolders { get; } = ["takes", "data", "blackbox", "locks", "tmp", "backups"];

    /// <summary>Entries inside <see cref="AmpleFolder"/> that AMPLE itself creates (others are reported as unknown by the doctor).</summary>
    public static IReadOnlyList<string> KnownEntries { get; } = [ProjectFile, "history.db", "history.db-wal", "history.db-shm", .. RequiredFolders, "previews"];
}

/// <summary>Reads and writes <c>project.json</c> in a canonical form (fixed property order, indented, LF).</summary>
public static class ProjectFile
{
    /// <summary>Writes the manifest.</summary>
    public static string Serialize(ProjectManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("formatVersion", m.FormatVersion);
            w.WriteString("id", m.Id.ToString("D"));
            w.WriteString("name", m.Name);
            w.WriteString("created", m.Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("masterSeed", m.MasterSeed.ToString(CultureInfo.InvariantCulture));
            w.WriteStartObject("settings");
            w.WriteStartObject("blackBox");
            w.WriteNumber("budgetBytes", m.Settings.BlackBox.BudgetBytes);
            w.WriteNumber("fullDetailDays", m.Settings.BlackBox.FullDetailDays);
            w.WriteNumber("thinnedDays", m.Settings.BlackBox.ThinnedDays);
            w.WriteEndObject();
            w.WriteStartObject("cleanup");
            if (m.Settings.Cleanup.ArchivedLeafDays is int days)
            {
                w.WriteNumber("archivedLeafDays", days);
            }
            else
            {
                w.WriteNull("archivedLeafDays");
            }

            w.WriteEndObject();
            w.WriteStartArray("mounts");
            foreach (string mount in m.Settings.Mounts)
            {
                w.WriteStringValue(mount);
            }

            w.WriteEndArray();
            if (m.Settings.DefaultEngine is string engine)
            {
                w.WriteString("defaultEngine", engine);
            }
            else
            {
                w.WriteNull("defaultEngine");
            }

            w.WriteStartObject("projectPanel");
            w.WriteString("view", m.Settings.ProjectPanel.View);
            w.WriteNumber("tileSize", m.Settings.ProjectPanel.TileSize);
            w.WriteString("sortColumn", m.Settings.ProjectPanel.SortColumn);
            w.WriteBoolean("sortDescending", m.Settings.ProjectPanel.SortDescending);
            w.WriteStartArray("columns");
            foreach (ProjectPanelColumn column in m.Settings.ProjectPanel.Columns)
            {
                w.WriteStartObject(); w.WriteString("id", column.Id); w.WriteNumber("width", column.Width); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("autoCheckpoint");
            w.WriteBoolean("enabled", m.Settings.AutoCheckpoint.Enabled);
            w.WriteNumber("minutes", m.Settings.AutoCheckpoint.Minutes);
            w.WriteEndObject();

            w.WriteStartObject("ai");
            w.WriteString("assistant", m.Settings.Ai.Assistant);
            w.WriteString("mode", m.Settings.Ai.Mode);
            WriteOptional(w, "acknowledged", m.Settings.Ai.Acknowledged);
            WriteOptional(w, "unsafeAcknowledged", m.Settings.Ai.UnsafeAcknowledged);
            if (m.Settings.Ai.LinkedAccount is string account)
            {
                w.WriteString("linkedAccount", account);
            }

            WriteOptional(w, "linkedAt", m.Settings.Ai.LinkedAt);
            if (!m.Settings.Ai.ExposeTakebankCache) w.WriteBoolean("exposeTakebankCache", false);
            w.WriteEndObject();

            w.WriteStartObject("faer");
            w.WriteString("song", m.Settings.Faer.Song);
            w.WriteString("voice", m.Settings.Faer.Voice);
            w.WriteString("separation", m.Settings.Faer.Separation);
            if (m.Settings.Faer.SongModel.Length > 0) w.WriteString("songModel", m.Settings.Faer.SongModel);
            if (m.Settings.Faer.VoiceModel.Length > 0) w.WriteString("voiceModel", m.Settings.Faer.VoiceModel);
            w.WriteEndObject();

            if (m.Settings.BuildParallelism is int parallelism)
            {
                w.WriteStartObject("build");
                w.WriteNumber("parallelism", parallelism);
                w.WriteEndObject();
            }

            if (m.Settings.Koda != KodaSettings.Default)
            {
                w.WriteStartObject("koda");
                w.WriteString("language", m.Settings.Koda.Language);
                if (m.Settings.Koda.Model.Length > 0) w.WriteString("model", m.Settings.Koda.Model);
                if (m.Settings.Koda.Effort.Length > 0) w.WriteString("effort", m.Settings.Koda.Effort);
                w.WriteEndObject();
            }

            if (m.Settings.FirstRun != FirstRunSettings.Default)
            {
                w.WriteStartObject("firstRun");
                w.WriteString("origin", m.Settings.FirstRun.Origin);
                w.WriteNumber("doneVersion", m.Settings.FirstRun.DoneVersion);
                if (m.Settings.FirstRun.DoneAt is DateTimeOffset doneAt) w.WriteString("doneAt", doneAt.ToString("O", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is DateTimeOffset at)
        {
            w.WriteString(name, at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private static DateTimeOffset? ReadOptional(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    /// <summary>Reads the format version without reading anything else (0 when missing).</summary>
    public static int ReadFormatVersion(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("formatVersion", out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5205", "project.json is not valid JSON: " + ex.Message);
        }
    }

    /// <summary>Reads a manifest of the current format.</summary>
    /// <exception cref="AmpleException">AMP5205 when invalid, AMP5206 when made by a newer AMPLE.</exception>
    public static ProjectManifest Parse(string json)
    {
        int version = ReadFormatVersion(json);
        if (version > ProjectLayout.CurrentFormatVersion)
        {
            throw new AmpleException("AMP5206", $"This project uses format version {version}, newer than this AMPLE understands ({ProjectLayout.CurrentFormatVersion}). Open it with a newer AMPLE.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement settings = root.GetProperty("settings");
            JsonElement bb = settings.GetProperty("blackBox");
            JsonElement cleanup = settings.GetProperty("cleanup");
            JsonElement leaf = cleanup.GetProperty("archivedLeafDays");
            JsonElement engine = settings.GetProperty("defaultEngine");
            ProjectPanelSettings projectPanel = settings.TryGetProperty("projectPanel", out JsonElement panel)
                ? new ProjectPanelSettings(panel.GetProperty("view").GetString()!, panel.GetProperty("tileSize").GetInt32(), panel.GetProperty("sortColumn").GetString()!, panel.GetProperty("sortDescending").GetBoolean(), [.. panel.GetProperty("columns").EnumerateArray().Select(column => new ProjectPanelColumn(column.GetProperty("id").GetString()!, column.GetProperty("width").GetInt32()))])
                : ProjectPanelSettings.Default;
            AutoCheckpointSettings autoCheckpoint = settings.TryGetProperty("autoCheckpoint", out JsonElement auto)
                ? new AutoCheckpointSettings(auto.GetProperty("enabled").GetBoolean(), auto.GetProperty("minutes").GetInt32())
                : AutoCheckpointSettings.Default;
            AiAssistantSettings ai = settings.TryGetProperty("ai", out JsonElement aiJson)
                ? new AiAssistantSettings(aiJson.GetProperty("assistant").GetString()!, aiJson.GetProperty("mode").GetString()!, ReadOptional(aiJson, "acknowledged"), ReadOptional(aiJson, "unsafeAcknowledged"), aiJson.TryGetProperty("linkedAccount", out JsonElement account) ? account.GetString() : null, ReadOptional(aiJson, "linkedAt")) { ExposeTakebankCache = !aiJson.TryGetProperty("exposeTakebankCache", out JsonElement expose) || expose.GetBoolean() }
                : AiAssistantSettings.Default;
            FaerRoutes faer = settings.TryGetProperty("faer", out JsonElement faerJson)
                ? new FaerRoutes(faerJson.GetProperty("song").GetString()!, faerJson.GetProperty("voice").GetString()!, faerJson.GetProperty("separation").GetString()!)
                {
                    SongModel = faerJson.TryGetProperty("songModel", out JsonElement songModel) ? songModel.GetString() ?? string.Empty : string.Empty,
                    VoiceModel = faerJson.TryGetProperty("voiceModel", out JsonElement voiceModel) ? voiceModel.GetString() ?? string.Empty : string.Empty,
                }
                : FaerRoutes.Default;
            KodaSettings koda = settings.TryGetProperty("koda", out JsonElement kodaJson) && kodaJson.TryGetProperty("language", out JsonElement kodaLanguage) && KodaSettings.IsKnown(kodaLanguage.GetString())
                ? new KodaSettings(kodaLanguage.GetString()!, kodaJson.TryGetProperty("model", out JsonElement kodaModel) && kodaModel.ValueKind == JsonValueKind.String ? kodaModel.GetString() ?? string.Empty : string.Empty, kodaJson.TryGetProperty("effort", out JsonElement kodaEffort) && kodaEffort.ValueKind == JsonValueKind.String ? kodaEffort.GetString() ?? string.Empty : string.Empty)
                : KodaSettings.Default;
            FirstRunSettings firstRun = settings.TryGetProperty("firstRun", out JsonElement firstRunJson) && firstRunJson.ValueKind == JsonValueKind.Object
                ? new FirstRunSettings(
                    firstRunJson.TryGetProperty("origin", out JsonElement originJson) && FirstRunSettings.IsKnownOrigin(originJson.GetString()) ? originJson.GetString()! : "existing",
                    firstRunJson.TryGetProperty("doneVersion", out JsonElement doneVersionJson) && doneVersionJson.ValueKind == JsonValueKind.Number && doneVersionJson.TryGetInt32(out int doneVersion) && doneVersion > 0 ? doneVersion : 0,
                    firstRunJson.TryGetProperty("doneAt", out JsonElement doneAtJson) && doneAtJson.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(doneAtJson.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset doneAt) ? doneAt : null)
                : FirstRunSettings.Default;
            string name = root.GetProperty("name").GetString() ?? throw new FormatException("name");
            return new ProjectManifest(
                Guid.Parse(root.GetProperty("id").GetString()!),
                name,
                DateTimeOffset.Parse(root.GetProperty("created").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                version,
                ulong.Parse(root.GetProperty("masterSeed").GetString()!, CultureInfo.InvariantCulture),
                new ProjectSettings(
                    new BlackBoxSettings(bb.GetProperty("budgetBytes").GetInt64(), bb.GetProperty("fullDetailDays").GetInt32(), bb.GetProperty("thinnedDays").GetInt32()),
                    new CleanupSettings(leaf.ValueKind == JsonValueKind.Null ? null : leaf.GetInt32()),
                    [.. settings.GetProperty("mounts").EnumerateArray().Select(e => e.GetString()!)],
                    engine.ValueKind == JsonValueKind.Null ? null : engine.GetString(),
                    projectPanel,
                    autoCheckpoint,
                    ai)
                { Faer = faer, Koda = koda, FirstRun = firstRun, BuildParallelism = settings.TryGetProperty("build", out JsonElement buildJson) && buildJson.TryGetProperty("parallelism", out JsonElement parallelismJson) && parallelismJson.ValueKind == JsonValueKind.Number && parallelismJson.TryGetInt32(out int parallelism) && parallelism > 0 ? parallelism : null });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new AmpleException("AMP5205", "project.json is not valid: " + ex.Message);
        }
    }

    /// <summary>Creates the manifest of a new project with a master seed derived from the id when none is given.</summary>
    public static ProjectManifest CreateNew(Guid id, string name, DateTimeOffset created, ulong? masterSeed) =>
        new(id, name, created, ProjectLayout.CurrentFormatVersion, masterSeed ?? SeedDeriver.Derive(0, "project", id.ToString("D")) & 0xFFFFFFFFUL, ProjectSettings.Default with { FirstRun = FirstRunSettings.NewProject });
}
