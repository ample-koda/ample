using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Storage.Package;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>What the user chose for an import.</summary>
/// <param name="Package">The package, in the project or at an absolute path.</param>
/// <param name="Mode"><c>anyway</c> (use this environment and keep a record of the differences), <c>convert</c> (apply the deterministic conversions) or <c>new-project</c> (a new project made from the package).</param>
/// <param name="TargetFolder">An existing project folder to import into (default: the open project); not used by <c>new-project</c>.</param>
/// <param name="NewFolder">The folder of the new project (for <c>new-project</c>).</param>
/// <param name="ProjectName">The name of the new project.</param>
/// <param name="SongName">The file name of the imported song (without extension).</param>
/// <param name="PreferEmbedded">Use the files in the package even when an installed pack has the same content.</param>
/// <param name="Overrides">Where the user wants a file extracted instead of the suggestion (package path to project path).</param>
internal sealed record TxImportRequest(string Package, string Mode, string? TargetFolder, string? NewFolder, string? ProjectName, string? SongName, bool PreferEmbedded, IReadOnlyDictionary<string, string>? Overrides);

/// <summary>What an import does with one file of the package.</summary>
internal sealed class TxImportItem
{
    public TxEntry Entry { get; init; } = null!;

    /// <summary><c>same</c>, <c>relink</c>, <c>pack-installed</c>, <c>extract</c> or <c>relocate</c> (the wanted place is taken by another file).</summary>
    public string Action { get; set; } = "extract";

    /// <summary>Gets the project-relative place the song will use afterwards.</summary>
    public string Destination { get; set; } = string.Empty;

    public string? FoundAt { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>What a reference to this file in the song looked like, and what it must become (empty when nothing changes).</summary>
    public string OldReference { get; set; } = string.Empty;
}

/// <summary>The import side of <c>.ampletx</c>: opening a package read-only, comparing it with this environment, matching its files by SHA-256 and making an editable project of it.</summary>
internal static class TxImport
{
    /// <summary>Opens a package (a plain <c>.ampletx</c> or one in a zip) without changing it.</summary>
    public static TxPackage Open(ProjectSession s, string path)
    {
        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : ProjectPath.Resolve(s.Root, path);
        if (!File.Exists(full))
        {
            throw new AmpleException("AMP5441", $"There is no package at {path}.");
        }

        string folder = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "tmp", "tx-open-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..10]);
        return TxPackage.Open(TxPackage.Unwrap(full, folder));
    }

    /// <summary>The read-only view of a package: what it is, what is in it, whether it has a master.</summary>
    public static JsonObject Inspect(TxPackage p)
    {
        JsonObject m = p.Manifest;
        TxEntry? song = p.Entries.FirstOrDefault(e => e.Role == AmpleTx.Roles.Song);
        TxEntry? master = p.Entries.FirstOrDefault(e => e.Role == AmpleTx.Roles.Master);
        return new JsonObject
        {
            ["readOnly"] = true,
            ["file"] = Path.GetFileName(p.FilePath),
            ["packageSha256"] = p.FileSha256(),
            ["packageSize"] = new FileInfo(p.FilePath).Length,
            ["format"] = p.Version,
            ["kind"] = p.Kind,
            ["created"] = m["created"]?.DeepClone(),
            ["application"] = m["application"]?.DeepClone(),
            ["project"] = m["project"]?.DeepClone(),
            ["settings"] = m["settings"]?.DeepClone(),
            ["settingsFingerprint"] = m["settingsFingerprint"]?.DeepClone(),
            ["dependencies"] = m["dependencies"]?.DeepClone(),
            ["derived"] = m["derived"]?.DeepClone(),
            ["warnings"] = m["warnings"]?.DeepClone(),
            ["master"] = m["master"]?.DeepClone(),
            ["masterAvailable"] = master is not null,
            ["song"] = song?.Path,
            ["songText"] = song is null ? null : p.ReadText(song),
            ["entries"] = new JsonArray([.. p.Entries.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["role"] = e.Role, ["size"] = e.Size, ["sha256"] = e.Sha256, ["mediaType"] = e.MediaType, ["originalName"] = e.OriginalName, ["originalPath"] = e.OriginalPath, ["encoding"] = e.Encoding, ["info"] = e.Info?.DeepClone() })]),
        };
    }

    /// <summary>Takes the embedded master out of the package into a temporary file, so it can be played without building anything.</summary>
    public static string MasterFile(ProjectSession s, TxPackage p)
    {
        TxEntry master = p.Entries.FirstOrDefault(e => e.Role == AmpleTx.Roles.Master) ?? throw new AmpleException("AMP5442", "This package has no master preview.");
        string folder = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "tmp", "tx-master");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, master.Sha256 + ".wav");
        if (!File.Exists(file))
        {
            string part = file + ".part";
            using (FileStream target = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                p.CopyTo(master, target);
            }

            File.Move(part, file, true);
        }

        return file;
    }

    // ---- the comparison with this environment ----

    private static JsonObject Diff(string code, string severity, string category, string message, string impact) => new() { ["code"] = code, ["severity"] = severity, ["category"] = category, ["message"] = message, ["impact"] = impact };

    /// <summary>What differs between the package and this environment, in plain words, with what it can mean for the result.</summary>
    public static JsonArray Compare(ProjectSession s, TxPackage p, string targetRoot)
    {
        JsonArray list = [];
        JsonObject m = p.Manifest;
        if (p.Version > AmpleTx.FormatVersion)
        {
            list.Add(Diff("format-newer", "error", "format", $"The package uses .ampletx format {p.Version}; this AMPLE reads format {AmpleTx.FormatVersion}.", "It cannot be read reliably. Open it with a newer AMPLE."));
        }
        else if (p.Version < AmpleTx.FormatVersion)
        {
            list.Add(Diff("format-older", "info", "format", $"The package uses the older .ampletx format {p.Version}.", "It is read as it is."));
        }

        int amplet = m["format"]?["amplet"]?.GetValue<int>() ?? 0;
        if (amplet != ProjectLayout.CurrentFormatVersion)
        {
            list.Add(Diff("amplet-format", amplet > ProjectLayout.CurrentFormatVersion ? "error" : "warning", "format", $"The song format of the package is {amplet}, this AMPLE uses {ProjectLayout.CurrentFormatVersion}.", "The song text may use things this version does not understand."));
        }

        string spec = m["format"]?["spec"]?.GetValue<string>() ?? string.Empty;
        if (spec.Length > 0 && spec != s.Spec.Version)
        {
            list.Add(Diff("spec-version", "warning", "language", $"The language data of the package is version {spec}, this AMPLE has {s.Spec.Version}.", "Block parameters or ports may have been added or changed."));
        }

        if (m["dependencies"]?["blocks"] is JsonArray blocks)
        {
            foreach (IGrouping<string, JsonNode?> group in blocks.GroupBy(b => b!["type"]!.GetValue<string>(), StringComparer.Ordinal))
            {
                string recorded = group.First()!["templateVersion"]?.GetValue<string>() ?? string.Empty;
                if (!s.Spec.Templates.TryGetValue(group.Key, out TemplateSpec? current))
                {
                    list.Add(Diff("block-missing", "error", "blocks", $"The block type {group.Key} (used by {string.Join(", ", group.Select(b => b!["name"]!.GetValue<string>()))}) does not exist in this AMPLE.", "Those blocks cannot be built."));
                }
                else if (recorded.Length > 0 && recorded != current.Class + "@" + current.Version)
                {
                    list.Add(Diff("template-version", "warning", "blocks", $"The block type {group.Key} was {recorded} in the package and is {current.Class}@{current.Version} here.", "Its parameters or behaviour may have changed."));
                }
            }
        }

        if (m["dependencies"]?["renderers"] is JsonArray renderers)
        {
            foreach (JsonNode? r in renderers)
            {
                string id = r!["id"]!.GetValue<string>(), version = r["version"]?.GetValue<string>() ?? string.Empty;
                IBlockRenderer? current = BuiltInRenderers.All.FirstOrDefault(x => x.Id == id);
                if (current is null && !id.StartsWith("builtin.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (current is null)
                {
                    list.Add(Diff("renderer-missing", "warning", "renderer", $"The renderer {id} {version} of the package is not part of this AMPLE.", "Blocks that used it will not render the same."));
                }
                else if (current.Version != version)
                {
                    list.Add(Diff("renderer-version", "warning", "renderer", $"The renderer {id} was version {version} in the package and is version {current.Version} here.", "The audio of the blocks that use it can differ from the package."));
                }
            }
        }

        if (m["dependencies"]?["plugins"] is JsonArray plugins && plugins.Count > 0)
        {
            (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> found, _) = Ample.Plugins.PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins"), Path.Combine(s.Root, "plugins"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
            foreach (JsonNode? pl in plugins)
            {
                string id = pl!["id"]!.GetValue<string>(), version = pl["version"]?.GetValue<string>() ?? string.Empty;
                Ample.Plugins.DiscoveredPlugin? have = found.FirstOrDefault(x => x.Manifest.Id == id);
                if (have is null)
                {
                    list.Add(Diff("plugin-missing", "error", "plugins", $"The plugin {id} {version} is not installed.", "The blocks that use it cannot be built until it is installed."));
                }
                else if (have.Manifest.Version != version)
                {
                    list.Add(Diff("plugin-version", "warning", "plugins", $"The plugin {id} was {version} in the package and is {have.Manifest.Version} here.", "Its output can differ."));
                }
            }
        }

        int rate = m["settings"]?["render"]?["sampleRate"]?.GetValue<int>() ?? 44100;
        if (rate != 44100)
        {
            list.Add(Diff("sample-rate", "warning", "settings", $"The package was rendered at {rate} Hz; this AMPLE renders at 44100 Hz.", "Timing and pitch of resampled material can differ slightly."));
        }

        string targetSettings = Path.Combine(targetRoot, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
        if (File.Exists(targetSettings))
        {
            ProjectManifest pm = ProjectFile.Parse(File.ReadAllText(targetSettings));
            string packageSeed = m["settings"]?["project"]?["masterSeed"]?.GetValue<string>() ?? string.Empty;
            if (packageSeed.Length > 0 && packageSeed != pm.MasterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                list.Add(Diff("project-seed", "info", "settings", "The master seed of the target project is not the one of the project the package came from.", "Blocks without a seed of their own follow the master seed written in the song itself, which the package keeps."));
            }
        }

        if (m["dependencies"]?["models"] is JsonArray models)
        {
            foreach (JsonNode? model in models)
            {
                string reference = model!["reference"]?.GetValue<string>() ?? string.Empty;
                string sha = model["subsetOf"]?.GetValue<string>() ?? model["sha256"]!.GetValue<string>();
                string? installed = reference.StartsWith("pack:", StringComparison.Ordinal) ? FindPack(s, targetRoot, reference["pack:".Length..], model["file"]!.GetValue<string>()) : null;
                if (installed is not null && Sha(installed) != sha)
                {
                    list.Add(Diff("model-differs", "warning", "models", $"The installed {reference} is not the same file as the one in the package (the SHA-256 differs).", "Sounds made with it will not be identical. The package has its own copy, which can be extracted."));
                }
            }
        }

        if (p.Kind == "derived")
        {
            list.Add(Diff("derived", "info", "package", "This is a Derived package: parts of the original graph are baked into audio inputs.", "The editable project starts at those audio inputs; the original blocks before them are not in the package and are not made up."));
        }

        return list;
    }

    private static string? FindPack(ProjectSession s, string targetRoot, string reference, string entryPath)
    {
        string rel = entryPath.StartsWith("packs/", StringComparison.Ordinal) ? entryPath["packs/".Length..] : reference;
        PackLookup lookup = AmpleHome.LookupPackFile(AmpleHome.DefaultPath(), s.AmpleRoot, targetRoot, rel);
        return lookup.Path;
    }

    private static string Sha(string file)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // ---- matching the files ----

    /// <summary>Works out what to do with every file of the package: nothing (identical file in the same place), relink (identical file elsewhere or in an installed pack), or extract (to a place that is free).</summary>
    public static List<TxImportItem> PlanItems(ProjectSession s, TxPackage p, string targetRoot, bool targetExists, TxImportRequest r)
    {
        Dictionary<long, List<string>> bySize = [];
        if (targetExists)
        {
            foreach (string file in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(targetRoot, file).Replace('\\', '/');
                if (relative.StartsWith(ProjectLayout.AmpleFolder + "/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("out/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("exports/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long size = new FileInfo(file).Length;
                if (!bySize.TryGetValue(size, out List<string>? list))
                {
                    bySize[size] = list = [];
                }

                list.Add(file);
            }
        }

        Dictionary<string, string> shaCache = new(StringComparer.OrdinalIgnoreCase);
        string HashOf(string file) => shaCache.TryGetValue(file, out string? v) ? v : shaCache[file] = Sha(file);
        string baseName = Path.GetFileNameWithoutExtension(p.FilePath).Replace(".ampletx", string.Empty, StringComparison.Ordinal);
        List<TxImportItem> items = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (TxEntry e in p.Entries.Where(x => x.Role is not (AmpleTx.Roles.Song or AmpleTx.Roles.Master)))
        {
            TxImportItem item = new() { Entry = e };
            string logical = e.Info?["decodedPath"]?.GetValue<string>() ?? e.Path;   // a FLAC entry becomes the WAV the song refers to
            string logicalSha = e.Info?["wavSha256"]?.GetValue<string>() ?? e.Sha256;
            long logicalSize = e.Info?["wavSize"]?.GetValue<long>() ?? e.Size;
            string kind = e.Info?["kind"]?.GetValue<string>() ?? (e.Path.StartsWith("packs/", StringComparison.Ordinal) ? "pack" : e.Path.StartsWith("external/", StringComparison.Ordinal) ? "external" : "project");
            bool pack = kind == "pack";
            string wanted = e.Role == AmpleTx.Roles.Settings ? ProjectLayout.AmpleFolder + "/plugin-settings.json" : pack ? PackDestination(e) : logical;
            if (r.Overrides is not null && r.Overrides.TryGetValue(e.Path, out string? chosen))
            {
                wanted = chosen;
            }

            item.Destination = wanted;
            item.OldReference = pack ? e.OriginalPath : logical;
            string? installed = pack && !r.PreferEmbedded ? FindPack(s, targetRoot, e.OriginalPath.StartsWith("pack:", StringComparison.Ordinal) ? e.OriginalPath["pack:".Length..] : e.OriginalPath, logical) : null;
            string full = targetExists ? Path.Combine(targetRoot, wanted.Replace('/', Path.DirectorySeparatorChar)) : Path.Combine(targetRoot, "x", wanted);
            if (installed is not null && HashOf(installed) == (e.Info?["subsetOf"]?.GetValue<string>() ?? logicalSha))
            {
                item.Action = "pack-installed";
                item.Destination = e.OriginalPath;
                item.FoundAt = "an installed pack";
                item.Note = e.Info?["subsetOf"] is not null ? "The SoundFont this subset was made from is installed as a pack; the song keeps using the pack, nothing is written." : "The same file is installed as a pack; the song keeps using the pack, nothing is written.";
                item.OldReference = string.Empty;
            }
            else if (targetExists && File.Exists(full) && HashOf(full) == logicalSha)
            {
                item.Action = "same";
                item.FoundAt = wanted;
                item.Note = "The identical file is already in the project at this path.";
                item.OldReference = pack ? e.OriginalPath : (wanted != logical ? logical : string.Empty);
            }
            else if (targetExists && File.Exists(full))
            {
                string alternative = $"imported/{baseName}/{logical}";
                item.Action = "relocate";
                item.Destination = alternative;
                item.Note = $"There is another file at {wanted}; the file of the package goes to {alternative} and the song is changed to use it. Nothing is overwritten.";
            }
            else if (targetExists && bySize.TryGetValue(logicalSize, out List<string>? same) && same.FirstOrDefault(f => HashOf(f) == logicalSha) is { } match)
            {
                item.Action = "relink";
                item.FoundAt = Path.GetRelativePath(targetRoot, match).Replace('\\', '/');
                item.Destination = item.FoundAt;
                item.Note = $"The identical file exists at {item.FoundAt}; the song is changed to use it instead of writing a copy.";
            }
            else
            {
                item.Action = "extract";
                item.Note = pack ? $"Not installed as a pack: extracted to {wanted} and the song is changed to use it." : "Extracted from the package.";
            }

            if (item.Action is "extract" or "relocate")
            {
                taken.Add(item.Destination);
            }

            if (item.Action == "extract")
            {
                item.OldReference = pack ? e.OriginalPath : (wanted != e.Path ? e.Path : string.Empty);
            }

            items.Add(item);
        }

        return items;
    }

    private static string PackDestination(TxEntry e) => (e.Path.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase) || e.Path.EndsWith(".sf3", StringComparison.OrdinalIgnoreCase)) ? "soundfonts/" + Path.GetFileName(e.Path) : e.Path;

    /// <summary>The changes to the song text that follow from relinking and relocating files: what the song said, and what it must say now.</summary>
    public static List<(string Old, string New)> References(IEnumerable<TxImportItem> items)
    {
        List<(string Old, string New)> map = [];
        foreach (TxImportItem i in items.Where(x => x.OldReference.Length > 0 && x.OldReference != x.Destination && x.Action is "relink" or "relocate" or "extract"))
        {
            map.Add((i.OldReference, i.Destination));
            string? oldDir = i.OldReference.Contains('/', StringComparison.Ordinal) ? i.OldReference[..i.OldReference.LastIndexOf('/')] : null;
            string? newDir = i.Destination.Contains('/', StringComparison.Ordinal) ? i.Destination[..i.Destination.LastIndexOf('/')] : null;
            if (oldDir is not null && newDir is not null && Path.GetFileName(i.OldReference) == Path.GetFileName(i.Destination))
            {
                map.Add((oldDir, newDir));
            }
        }

        return [.. map.Distinct().OrderByDescending(x => x.Old.Length)];
    }

    /// <summary>Applies the reference changes to a text. Comment lines (starting with #) are left as they are: a comment is not a reference.</summary>
    public static string Rewrite(string text, IReadOnlyList<(string Old, string New)> changes)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                continue;
            }

            foreach ((string old, string now) in changes)
            {
                lines[i] = Regex.Replace(lines[i], "(?<![A-Za-z0-9_./:-])" + Regex.Escape(old) + "(?![A-Za-z0-9_-])", now.Replace("$", "$", StringComparison.Ordinal), RegexOptions.CultureInvariant);
            }
        }

        return string.Join('\n', lines);
    }

    // ---- the plan ----

    /// <summary>The impact of an import, before anything is written.</summary>
    public static JsonObject Plan(ProjectSession s, TxPackage p, TxImportRequest r)
    {
        (string targetRoot, bool exists) = Target(s, r);
        JsonArray differences = exists ? Compare(s, p, targetRoot) : Compare(s, p, s.Root);
        List<TxImportItem> items = PlanItems(s, p, targetRoot, exists, r);
        List<(string Old, string New)> references = References(items);
        bool blocked = differences.Any(d => d!["severity"]!.GetValue<string>() == "error");
        JsonArray conversions = new([.. references.Select(c => (JsonNode)new JsonObject { ["kind"] = "reference", ["description"] = $"{c.Old} becomes {c.New} in the song" })]);
        TxEntry? song = p.Entries.First(e => e.Role == AmpleTx.Roles.Song);
        string songName = r.SongName ?? SongTarget(targetRoot, exists, song.Path, r.Mode == "new-project");
        JsonObject o = new()
        {
            ["package"] = Path.GetFileName(p.FilePath),
            ["kind"] = p.Kind,
            ["target"] = exists ? targetRoot : r.NewFolder,
            ["song"] = new JsonObject { ["from"] = song.Path, ["to"] = songName },
            ["differences"] = differences,
            ["compatible"] = !blocked,
            ["assets"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject
            {
                ["path"] = i.Entry.Path,
                ["role"] = i.Entry.Role,
                ["size"] = i.Entry.Size,
                ["sha256"] = i.Entry.Sha256,
                ["originalName"] = i.Entry.OriginalName,
                ["originalPath"] = i.Entry.OriginalPath,
                ["action"] = i.Action,
                ["destination"] = i.Destination,
                ["foundAt"] = i.FoundAt,
                ["note"] = i.Note,
            })]),
            ["conversions"] = conversions,
            ["options"] = new JsonObject
            {
                ["anyway"] = !blocked && !items.Any(i => i.Action == "relocate"),
                ["convert"] = !blocked && references.Count > 0,
                ["newProject"] = true,
                ["doNotImport"] = true,
            },
            ["summary"] = new JsonObject
            {
                ["same"] = items.Count(i => i.Action == "same"),
                ["relink"] = items.Count(i => i.Action is "relink" or "pack-installed"),
                ["extract"] = items.Count(i => i.Action is "extract" or "relocate"),
                ["extractBytes"] = items.Where(i => i.Action is "extract" or "relocate").Sum(i => i.Entry.Size),
            },
        };
        return o;
    }

    private static (string Root, bool Exists) Target(ProjectSession s, TxImportRequest r)
    {
        if (r.Mode == "new-project")
        {
            string folder = Path.GetFullPath(r.NewFolder ?? throw new AmpleException("AMP5285", "A new project needs a folder."));
            return (folder, false);
        }

        string root = r.TargetFolder is { Length: > 0 } ? Path.GetFullPath(r.TargetFolder) : s.Root;
        if (!File.Exists(Path.Combine(root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)))
        {
            throw new AmpleException("AMP5443", $"{root} is not an AMPLE project folder.");
        }

        return (root, true);
    }

    private static string SongTarget(string root, bool exists, string songPath, bool fresh)
    {
        if (fresh || !exists)
        {
            return songPath;
        }

        string folder = songPath.Contains('/', StringComparison.Ordinal) ? songPath[..(songPath.LastIndexOf('/') + 1)] : string.Empty;
        string name = Path.GetFileNameWithoutExtension(songPath), ext = Path.GetExtension(songPath);
        string candidate = songPath;
        for (int i = 1; File.Exists(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar))); i++)
        {
            candidate = folder + name + (i == 1 ? "-imported" : $"-imported-{i}") + ext;
        }

        return candidate;
    }

    // ---- the import ----

    /// <summary>Does the import. The package is only read.</summary>
    public static async Task<JsonObject> ApplyAsync(ProjectSession s, TxPackage p, TxImportRequest r, CancellationToken ct)
    {
        if (r.Mode is not ("anyway" or "convert" or "new-project"))
        {
            throw new AmpleException("AMP5285", "The import mode must be anyway, convert or new-project.");
        }

        if (p.Entries.Any(e => e.Info?["codec"]?.GetValue<string>() == "flac"))
        {
            await TxFlac.EnsureAvailableAsync(ct).ConfigureAwait(false);   // before anything is written
        }

        JsonObject plan = Plan(s, p, r);
        if (!plan["compatible"]!.GetValue<bool>())
        {
            throw new AmpleException("AMP5444", "The package cannot be imported into this environment: " + string.Join("; ", plan["differences"]!.AsArray().Where(d => d!["severity"]!.GetValue<string>() == "error").Select(d => d!["message"]!.GetValue<string>())));
        }

        (string targetRoot, bool exists) = Target(s, r);
        List<TxImportItem> items = PlanItems(s, p, targetRoot, exists, r);
        if (r.Mode == "anyway" && items.Any(i => i.Action == "relocate"))
        {
            throw new AmpleException("AMP5445", "A file of the package would overwrite a different file of the project. Use Try to convert (the package file goes to another place) or Create a new project.");
        }

        bool rewrite = r.Mode is "convert" or "new-project";
        List<(string Old, string New)> references = rewrite ? References(items) : [];
        if (!exists)
        {
            using OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), targetRoot, r.ProjectName ?? ((p.Manifest["project"]?["name"]?.GetValue<string>() ?? "Imported") + " (imported)"), ulong.TryParse(p.Manifest["settings"]?["project"]?["masterSeed"]?.GetValue<string>(), out ulong seed) ? seed : null, ct).ConfigureAwait(false);
        }

        List<string> written = [], relinked = [], same = [], differing = [];
        foreach (TxImportItem item in items)
        {
            if (item.Action is "same")
            {
                same.Add(item.Destination);
                continue;
            }

            if (item.Action is "pack-installed" or "relink")
            {
                relinked.Add($"{item.Entry.Path} -> {item.FoundAt}");
                continue;
            }

            if (item.Entry.Role == AmpleTx.Roles.Settings && exists && File.Exists(Path.Combine(targetRoot, item.Destination)))
            {
                continue;
            }

            string target = Path.Combine(targetRoot, item.Destination.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                throw new AmpleException("AMP5445", $"{item.Destination} already exists and is not the file of the package; nothing was overwritten.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string part = target + ".part";
            bool text = rewrite && references.Count > 0 && item.Entry.MediaType == "text/plain" && item.Entry.Encoding == "text";
            if (text)
            {
                await File.WriteAllTextAsync(part, Rewrite(p.ReadText(item.Entry), references), new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
            else if (item.Entry.Info?["codec"]?.GetValue<string>() == "flac")
            {
                // FLAC audio input: decoded back to the WAV the song refers to; whether it equals the original is checked, not assumed
                string flacTemp = part + ".flac";
                try
                {
                    using (FileStream stream = new(flacTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        p.CopyTo(item.Entry, stream);
                    }

                    await TxFlac.DecodeAsync(flacTemp, part, item.Entry.Info?["bitDepth"]?.GetValue<int>() ?? 16, ct).ConfigureAwait(false);
                }
                finally
                {
                    File.Delete(flacTemp);
                }

                if (Sha(part) != item.Entry.Info?["wavSha256"]?.GetValue<string>())
                {
                    differing.Add(item.Destination + (TxFlac.DataSha(part) == item.Entry.Info?["samplesSha256"]?.GetValue<string>() ? " (only the file header differs)" : string.Empty));
                }
            }
            else
            {
                using FileStream stream = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                p.CopyTo(item.Entry, stream);
            }

            File.Move(part, target, false);
            written.Add(item.Destination);
        }

        TxEntry songEntry = p.Entries.First(e => e.Role == AmpleTx.Roles.Song);
        string songTarget = plan["song"]!["to"]!.GetValue<string>();
        string songText = p.ReadText(songEntry);
        if (rewrite && references.Count > 0)
        {
            songText = Rewrite(songText, references);
        }

        string songFile = Path.Combine(targetRoot, songTarget.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(songFile))
        {
            throw new AmpleException("AMP5445", $"{songTarget} already exists; nothing was overwritten.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(songFile)!);
        await File.WriteAllTextAsync(songFile, songText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        written.Add(songTarget);
        string stamp = s.Clock.UtcNow.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        JsonObject record = new()
        {
            ["importedFrom"] = new JsonObject { ["file"] = Path.GetFileName(p.FilePath), ["sha256"] = p.FileSha256(), ["kind"] = p.Kind, ["created"] = p.Manifest["created"]?.DeepClone(), ["application"] = p.Manifest["application"]?["version"]?.DeepClone() },
            ["importedAt"] = s.Clock.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["mode"] = r.Mode,
            ["song"] = songTarget,
            ["differences"] = plan["differences"]!.DeepClone(),
            ["written"] = new JsonArray([.. written.Select(x => (JsonNode)x)]),
            ["relinked"] = new JsonArray([.. relinked.Select(x => (JsonNode)x)]),
            ["identicalAlreadyThere"] = new JsonArray([.. same.Select(x => (JsonNode)x)]),
            ["flacDifferingFromOriginal"] = new JsonArray([.. differing.Select(x => (JsonNode)x)]),
            ["referenceChanges"] = new JsonArray([.. references.Select(c => (JsonNode)new JsonObject { ["from"] = c.Old, ["to"] = c.New })]),
            ["derivedNote"] = p.Kind == "derived" ? "The graph before the audio inputs was baked when the package was made; it is not in this project." : null,
        };
        string recordFolder = Path.Combine(targetRoot, ProjectLayout.AmpleFolder, "imports");
        Directory.CreateDirectory(recordFolder);
        string recordFile = Path.Combine(recordFolder, $"{stamp}-{Path.GetFileNameWithoutExtension(songTarget)}.json");
        await File.WriteAllTextAsync(recordFile, record.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["project"] = targetRoot,
            ["newProject"] = !exists,
            ["song"] = songTarget,
            ["mode"] = r.Mode,
            ["written"] = new JsonArray([.. written.Select(x => (JsonNode)x)]),
            ["writtenCount"] = written.Count,
            ["relinked"] = new JsonArray([.. relinked.Select(x => (JsonNode)x)]),
            ["identicalAlreadyThere"] = same.Count,
            ["flacDifferingFromOriginal"] = new JsonArray([.. differing.Select(x => (JsonNode)x)]),
            ["referenceChanges"] = new JsonArray([.. references.Select(c => (JsonNode)new JsonObject { ["from"] = c.Old, ["to"] = c.New })]),
            ["differences"] = plan["differences"]!.DeepClone(),
            ["record"] = Path.GetRelativePath(targetRoot, recordFile).Replace('\\', '/'),
        };
    }
}
