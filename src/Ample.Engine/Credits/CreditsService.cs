using System.Globalization;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.Library;
using Ample.Storage.Takes;
using Ample.Language;
using Microsoft.Data.Sqlite;

namespace Ample.Engine.Credits;

/// <summary>A block of the song and what made its newest take.</summary>
/// <param name="Block">Block name.</param>
/// <param name="Plugin">Plugin or built-in renderer id.</param>
/// <param name="Version">Its version.</param>
/// <param name="TakeId">The newest take.</param>
/// <param name="Voice">The voice setting, when the block has one.</param>
/// <param name="ToolprintJson">What identifies the tool and its models.</param>
public sealed record CreditBlock(string Block, string Plugin, string Version, string TakeId, string? Voice, string ToolprintJson);

/// <summary>A library asset the song uses.</summary>
/// <param name="Name">Asset name.</param>
/// <param name="Kind">sample, voice, pack, lyrics...</param>
/// <param name="License">The licence text kept in the library (may be empty).</param>
/// <param name="Credit">The credit line kept in the library.</param>
/// <param name="Hash">Content hash.</param>
/// <param name="UsedBy">Blocks and parameters that use it.</param>
/// <param name="LicenseClass"><c>open</c>, <c>non-commercial</c>, <c>no-derivatives</c> or <c>unknown</c>.</param>
public sealed record CreditAsset(string Name, string Kind, string License, string Credit, string Hash, IReadOnlyList<string> UsedBy, string LicenseClass);

/// <summary>A human contributor, written down in <c>.ample/credits.json</c>.</summary>
/// <param name="Name">Name.</param>
/// <param name="Role">Role (words, melody, mixing...).</param>
/// <param name="Note">Free note.</param>
public sealed record CreditPerson(string Name, string Role, string Note);

/// <summary>A licence or credit warning. Advisory, not legal advice.</summary>
/// <param name="Code">AMP5332.</param>
/// <param name="Severity">warning or info.</param>
/// <param name="Message">One plain sentence.</param>
public sealed record CreditWarning(string Code, string Severity, string Message);

/// <summary>The credit sheet of a project, made from the plans of the newest takes, the modules and library metadata that the songs use, and the people written down by hand.</summary>
public sealed record CreditSheet(string Project, DateTimeOffset Generated, bool ReleaseContext, IReadOnlyList<CreditBlock> Blocks, IReadOnlyList<CreditAsset> Assets, IReadOnlyList<ModuleUsage> Modules, IReadOnlyList<CreditPerson> People, IReadOnlyList<CreditWarning> Warnings)
{
    /// <summary>The disclaimer that goes with every sheet.</summary>
    public const string Disclaimer = "Generated from what the project used. Licence information is only as good as the metadata kept in the library; this is advisory and not legal advice.";
}

/// <summary>Builds the credit sheet and its Markdown and JSON forms.</summary>
public static class CreditsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The file with the hand-written contributors.</summary>
    public static string PeopleFile(string projectRoot) => Path.Combine(projectRoot, ".ample", "credits.json");

    /// <summary>Classifies a licence text.</summary>
    public static string Classify(string license)
    {
        if (string.IsNullOrWhiteSpace(license))
        {
            return "unknown";
        }

        string l = license.ToLowerInvariant();
        return l.Contains("non-commercial", StringComparison.Ordinal) || l.Contains("noncommercial", StringComparison.Ordinal) || l.Contains("-nc", StringComparison.Ordinal) || l.Contains(" nc", StringComparison.Ordinal) || l.EndsWith("nc", StringComparison.Ordinal) && l.Contains("by", StringComparison.Ordinal)
            ? "non-commercial"
            : l.Contains("-nd", StringComparison.Ordinal) || l.Contains("no-deriv", StringComparison.Ordinal) || l.Contains("noderiv", StringComparison.Ordinal) ? "no-derivatives" : "open";
    }

    /// <summary>Builds the sheet.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="projectName">The project name.</param>
    /// <param name="bank">The take bank (plans say what made each take).</param>
    /// <param name="library">The library with the licence metadata, or null.</param>
    /// <param name="songs">The songs with their modules expanded.</param>
    /// <param name="modules">The modules that were used.</param>
    /// <param name="clock">The clock.</param>
    public static CreditSheet Build(string projectRoot, string projectName, Takebank bank, Ample.Storage.Library.Library? library, IEnumerable<AmpleDocument> songs, IEnumerable<ModuleUsage> modules, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(clock);
        List<CreditBlock> blocks = [];
        foreach (IGrouping<string, TakeRecord> g in bank.ListTakes().Where(t => !t.Tombstoned).GroupBy(t => bank.GetPlan(t.PlanId).Plan.BlockPath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            TakeRecord newest = g.OrderByDescending(t => t.Created).First();
            PlanRecord plan = bank.GetPlan(newest.PlanId).Plan;
            blocks.Add(new CreditBlock(g.Key, plan.PluginId, plan.PluginVersion, newest.TakeId, VoiceOf(plan.SettingsJson), plan.ToolprintJson));
        }

        Dictionary<string, (AssetRecord Asset, List<string> Uses)> assets = [];
        if (library is not null)
        {
            foreach (AmpleDocument doc in songs)
            {
                foreach (AssetUse u in library.Uses(doc))
                {
                    AssetRecord? a = library.Asset(u.AssetUuid);
                    if (a is null)
                    {
                        continue;
                    }

                    if (!assets.TryGetValue(a.Uuid, out (AssetRecord Asset, List<string> Uses) entry))
                    {
                        entry = (a, []);
                        assets[a.Uuid] = entry;
                    }

                    string use = $"{u.Block}.{u.Parameter}";
                    if (!entry.Uses.Contains(use))
                    {
                        entry.Uses.Add(use);
                    }
                }
            }
        }

        List<CreditAsset> assetList = [.. assets.Values.OrderBy(a => a.Asset.Name, StringComparer.Ordinal).Select(a => new CreditAsset(a.Asset.Name, a.Asset.Kind.ToString().ToLowerInvariant(), a.Asset.License, a.Asset.Credit, a.Asset.Hash ?? string.Empty, a.Uses, Classify(a.Asset.License)))];
        List<CreditPerson> people = ReadPeople(projectRoot, out bool commercial);
        bool release = commercial || TagCount(projectRoot) > 0;
        List<CreditWarning> warnings = [];
        foreach (CreditAsset a in assetList)
        {
            if (a.LicenseClass == "non-commercial" && release)
            {
                warnings.Add(new CreditWarning("AMP5332", "warning", $"'{a.Name}' has a non-commercial licence ({a.License}) but this project has a release (a tag or usage \"commercial\"). Check that the use is allowed."));
            }
            else if (a.LicenseClass == "no-derivatives")
            {
                warnings.Add(new CreditWarning("AMP5332", "info", $"'{a.Name}' has a no-derivatives licence ({a.License}); using it in a mix may count as a derivative."));
            }
            else if (a.LicenseClass == "unknown")
            {
                warnings.Add(new CreditWarning("AMP5332", "info", $"'{a.Name}' has no licence recorded in the library. Add it (tags, licence and credit are in the library manager)."));
            }

            if (a.Credit.Length == 0 && a.LicenseClass != "unknown")
            {
                warnings.Add(new CreditWarning("AMP5332", "info", $"'{a.Name}' has no credit line."));
            }
        }

        return new CreditSheet(projectName, clock.UtcNow, release, blocks, assetList, [.. modules.GroupBy(m => m.Name).Select(g => g.First()).OrderBy(m => m.Name, StringComparer.Ordinal)], people, warnings);
    }

    /// <summary>The sheet as JSON.</summary>
    public static string ToJson(CreditSheet sheet) => JsonSerializer.Serialize(new { sheet.Project, Generated = sheet.Generated.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), sheet.ReleaseContext, sheet.Blocks, sheet.Assets, sheet.Modules, sheet.People, sheet.Warnings, Disclaimer = CreditSheet.Disclaimer }, Options) + "\n";

    /// <summary>The sheet as Markdown.</summary>
    public static string ToMarkdown(CreditSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        StringBuilder sb = new();
        sb.Append("# Credits: ").Append(sheet.Project).Append("\n\n");
        sb.Append('_').Append(CreditSheet.Disclaimer).Append("_\n\n");
        if (sheet.People.Count > 0)
        {
            sb.Append("## People\n\n");
            foreach (CreditPerson p in sheet.People)
            {
                sb.Append("- ").Append(p.Name).Append(p.Role.Length > 0 ? " (" + p.Role + ")" : string.Empty).Append(p.Note.Length > 0 ? ": " + p.Note : string.Empty).Append('\n');
            }

            sb.Append('\n');
        }

        sb.Append("## Made with\n\n");
        foreach (CreditBlock b in sheet.Blocks)
        {
            sb.Append("- **").Append(b.Block).Append("**: ").Append(b.Plugin).Append(' ').Append(b.Version).Append(b.Voice is null ? string.Empty : ", voice " + b.Voice).Append(", take ").Append(b.TakeId).Append('\n');
        }

        if (sheet.Blocks.Count == 0)
        {
            sb.Append("Nothing has been built yet.\n");
        }

        if (sheet.Modules.Count > 0)
        {
            sb.Append("\n## Modules\n\n");
            foreach (ModuleUsage m in sheet.Modules)
            {
                sb.Append("- ").Append(m.Name).Append(' ').Append(m.Version).Append('\n');
            }
        }

        if (sheet.Assets.Count > 0)
        {
            sb.Append("\n## Samples, voices and packs\n\n");
            foreach (CreditAsset a in sheet.Assets)
            {
                sb.Append("- ").Append(a.Name).Append(" (").Append(a.Kind).Append("): ").Append(a.License.Length == 0 ? "no licence recorded" : a.License).Append(a.Credit.Length > 0 ? ", credit: " + a.Credit : string.Empty).Append("; used by ").Append(string.Join(", ", a.UsedBy)).Append('\n');
            }
        }

        if (sheet.Warnings.Count > 0)
        {
            sb.Append("\n## Warnings\n\n");
            foreach (CreditWarning w in sheet.Warnings)
            {
                sb.Append("- ").Append(w.Severity).Append(' ').Append(w.Code).Append(": ").Append(w.Message).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Makes the sheet from what the project has now and keeps it in <c>.ample/credits/</c>, so that an export or a backup carries a sheet that cannot be older than the takes.
    /// Best effort: a project without songs or takes gets a sheet with nothing in it.
    /// </summary>
    public static void Refresh(string projectRoot, string projectName, SpecData spec, IClock clock, Ample.Storage.Library.Library? library)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using Ample.Engine.Modules.ModuleService modules = new(projectRoot, library);
        List<AmpleDocument> songs = [];
        List<ModuleUsage> used = [];
        foreach (string f in Directory.EnumerateFiles(projectRoot, "*.amplet", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ample" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            ExpandedSong e = modules.Expand(File.ReadAllText(f), spec);
            songs.Add(Parser.Parse(e.Text, spec));
            used.AddRange(e.Used);
        }

        WriteSheet(projectRoot, Build(projectRoot, projectName, Takebank.Open(new Ample.Audio.IO.PhysicalFileSystem(), clock, projectRoot), library, songs, used, clock));
    }

    /// <summary>Writes the sheet into <c>.ample/credits/</c> (credits.json and CREDITS.md), where backups and exports pick it up.</summary>
    public static void WriteSheet(string projectRoot, CreditSheet sheet)
    {
        string folder = Path.Combine(projectRoot, ".ample", "credits");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "credits.json"), ToJson(sheet));
        File.WriteAllText(Path.Combine(folder, "CREDITS.md"), ToMarkdown(sheet));
    }

    private static string? VoiceOf(string settingsJson)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(settingsJson);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("voice", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<CreditPerson> ReadPeople(string projectRoot, out bool commercial)
    {
        commercial = false;
        string file = PeopleFile(projectRoot);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            commercial = d.RootElement.TryGetProperty("usage", out JsonElement u) && u.GetString() == "commercial";
            return d.RootElement.TryGetProperty("people", out JsonElement p) ? [.. p.EnumerateArray().Select(x => new CreditPerson(x.GetProperty("name").GetString()!, x.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty, x.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty))] : [];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AmpleException("AMP5332", "The credits file (.ample/credits.json) is damaged: " + ex.Message);
        }
    }

    private static int TagCount(string projectRoot)
    {
        string db = Path.Combine(projectRoot, ".ample", "history.db");
        if (!File.Exists(db))
        {
            return 0;
        }

        try
        {
            using SqliteConnection c = new(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM h_tags";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }
}
