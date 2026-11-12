using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Storage.BlackBox;

namespace Ample.Storage.Library;

public sealed partial class Library
{
    /// <summary>
    /// Resolves an asset in the fixed order: the project folder, the project's linked folders, the user's linked folders,
    /// installed packs. A missing one says which code and why (AMP5290 its folder is offline, AMP5291 it is not there,
    /// AMP5294 it is there but its content differs from the pinned hash).
    /// </summary>
    public Resolution Resolve(AssetRef reference, string? projectRoot = null, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string rel = reference.Name.Replace('\\', '/');
        if (projectRoot is not null)
        {
            foreach (string candidate in new[] { rel, "assets/" + rel })
            {
                string? path = FindFile(projectRoot, candidate);
                if (path is not null)
                {
                    return Check(reference, "project", path, null);
                }
            }
        }

        lock (gate)
        {
            IReadOnlyList<Mount> all = Mounts(projectId);
            bool sawOffline = false;
            foreach (Mount m in all.Where(x => x.Scope == "project").Concat(all.Where(x => x.Scope == "global")))
            {
                AssetRecord? asset = reference.Uuid is not null
                    ? AssetsInternal("SELECT * FROM assets WHERE mount_id = $m AND uuid = $u", ("$m", m.Id), ("$u", reference.Uuid)).FirstOrDefault()
                    : AssetsInternal("SELECT * FROM assets WHERE mount_id = $m AND (name = $n OR rel_path = $n)", ("$m", m.Id), ("$n", rel)).FirstOrDefault();
                if (asset is null || asset.Kind == AssetKind.Pack)
                {
                    continue;
                }

                if (m.State != "online")
                {
                    sawOffline = true;
                    continue;
                }

                string full = Path.Combine(m.Path, asset.RelPath);
                if (asset.Missing || !File.Exists(full))
                {
                    return new Resolution("missing", $"mount:{m.Id}", full, asset, "AMP5291", $"'{reference.Name}' is in the linked folder '{m.Name}' but the file is gone. Relocate the folder or restore the file.");
                }

                return Check(reference, $"mount:{m.Id}", full, asset);
            }

            AssetRecord? pack = AssetsInternal("SELECT * FROM assets WHERE kind = 'pack' AND missing = 0 AND (name = $n OR name LIKE $p)", ("$n", reference.Name), ("$p", reference.Name + "@%")).FirstOrDefault();
            if (pack is not null && all.FirstOrDefault(x => x.Id == pack.MountId) is { State: "online" } pm)
            {
                return Check(reference, "pack", Path.Combine(pm.Path, pack.RelPath), pack);
            }

            return sawOffline
                ? new Resolution("offline", null, null, null, "AMP5290", $"'{reference.Name}' is in a linked folder that is not reachable right now. Connect the drive or relocate the folder.")
                : new Resolution("missing", null, null, null, "AMP5291", $"'{reference.Name}' was not found in the project, its linked folders, the user library or the packs. Link the folder that holds it, or relocate.");
        }
    }

    /// <summary>
    /// Checks the assets of a mount against their files by hashing them again: ok, changed (the content is not the one the
    /// catalog knows), missing, or offline (the folder is not reachable). Changes are not accepted here: a scan does that.
    /// </summary>
    public IReadOnlyList<(AssetRecord Asset, string Status)> Verify(string mountId)
    {
        Mount m = GetMount(mountId);
        List<(AssetRecord, string)> result = [];
        foreach (AssetRecord a in Assets(mountId))
        {
            string full = Path.Combine(m.Path, a.RelPath);
            result.Add((a, m.State != "online" ? "offline" : a.Missing || !File.Exists(full) ? "missing" : a.Hash is not null && HashFile(full) != a.Hash ? "changed" : "ok"));
        }

        return result;
    }

    /// <summary>Finds a file by the name a song writes (the relative path, with or without its extension).</summary>
    private static string? FindFile(string root, string relative)
    {
        string path;
        try
        {
            path = Ample.Audio.IO.ProjectPath.Resolve(root, relative);
        }
        catch (AmpleException)
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        string? dir = Path.GetDirectoryName(path);
        return dir is not null && Directory.Exists(dir) ? Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*").Where(f => KindOf(f) != AssetKind.Other).Order(StringComparer.Ordinal).FirstOrDefault() : null;
    }

    private static Resolution Check(AssetRef reference, string where, string path, AssetRecord? asset)
    {
        if (reference.Hash is not null && HashFile(path) != reference.Hash)
        {
            return new Resolution("changed", where, path, asset, "AMP5294", $"'{reference.Name}' is there but its content is not the one that was pinned. Accept the change or restore the pinned file.");
        }

        return new Resolution("found", where, path, asset, null, null);
    }

    /// <summary>
    /// Finds where the blocks of a song use catalog assets: a parameter value that is the name of an asset (or starts with it
    /// followed by <c>/</c>, as in <c>piano/close</c> for the pack <c>piano</c>) is a use.
    /// </summary>
    public IReadOnlyList<AssetUse> Uses(AmpleDocument doc, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        IReadOnlyList<AssetRecord> assets = Assets().Where(a => !a.Missing).ToList();
        List<AssetUse> uses = [];
        foreach (BlockNode block in doc.Blocks.Where(b => !b.Skipped))
        {
            foreach (Parameter p in block.Parameters)
            {
                string value = Parser.Unquote(p.Value.Trim());
                foreach (AssetRecord a in assets)
                {
                    string baseName = a.Kind == AssetKind.Pack ? a.Name.Split('@')[0] : a.Name;
                    if (string.Equals(value, a.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(value, a.RelPath, StringComparison.OrdinalIgnoreCase) || string.Equals(value, baseName, StringComparison.OrdinalIgnoreCase) || value.StartsWith(baseName + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        uses.Add(new AssetUse(a.Uuid, block.Name, p.Name, p.Line));
                    }
                }
            }
        }

        _ = projectId;
        return uses;
    }

    /* ---------- packs ---------- */

    /// <summary>Validates <c>pack.json</c> in a folder.</summary>
    /// <exception cref="AmpleException">AMP5295 when the manifest is missing or not valid.</exception>
    public static PackManifest ValidatePack(string folder)
    {
        string file = Path.Combine(folder, "pack.json");
        if (!File.Exists(file))
        {
            throw new AmpleException("AMP5295", $"There is no pack.json in '{folder}'.");
        }

        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement r = d.RootElement;
            string Need(string key) => r.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0 ? v.GetString()! : throw new AmpleException("AMP5295", $"pack.json needs a text field '{key}'.");
            string name = Need("name");
            string version = Need("version");
            if (name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            {
                throw new AmpleException("AMP5295", "The pack name may only use letters, digits, '-', '_' and '.'.");
            }

            return new PackManifest(name, version, r.TryGetProperty("license", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : string.Empty, r.TryGetProperty("format", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : string.Empty);
        }
        catch (JsonException ex)
        {
            throw new AmpleException("AMP5295", "pack.json is not valid JSON: " + ex.Message);
        }
    }

    /// <summary>Installs a pack into the library: copied into the user's packs folder, or linked where it is (read-only).</summary>
    public Mount InstallPack(string folder, bool link)
    {
        PackManifest manifest = ValidatePack(folder);
        string target = folder;
        if (!link)
        {
            target = Path.Combine(Home, "packs", $"{manifest.Name}@{manifest.Version}");
            if (Directory.Exists(target))
            {
                throw new AmpleException("AMP5295", $"The pack {manifest.Name}@{manifest.Version} is already installed.");
            }

            CopyDirectory(folder, target);
        }

        Mount m = LinkMount($"{manifest.Name}@{manifest.Version}", target, "global", null, true);
        Scan(m.Id);
        return m;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }

        foreach (string d in Directory.EnumerateDirectories(from).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    /* ---------- import into a project ---------- */

    /// <summary>
    /// Copies an asset into the project (<c>assets/</c>) with a provenance file next to it, as an alternative to linking.
    /// The copy is a command of the black box when a bus is given. The copy gets a new uuid the next time it is scanned.
    /// </summary>
    /// <returns>The project-relative path of the copy.</returns>
    public async Task<string> ImportAsync(string assetUuid, string projectRoot, CommandBus? bus = null, CancellationToken cancellationToken = default)
    {
        AssetRecord asset = Asset(assetUuid) ?? throw new AmpleException("AMP5291", $"There is no asset '{assetUuid}'.");
        Mount mount = GetMount(asset.MountId);
        if (mount.State != "online")
        {
            throw new AmpleException("AMP5290", $"The linked folder '{mount.Name}' is not reachable.");
        }

        string source = Path.Combine(mount.Path, asset.RelPath);
        if (!File.Exists(source))
        {
            throw new AmpleException("AMP5291", $"'{asset.Name}' is gone from '{mount.Name}'.");
        }

        string relative = "assets/" + asset.RelPath;
        string destination = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, relative);
        if (File.Exists(destination))
        {
            throw new AmpleException("AMP5201", $"'{relative}' already exists in the project.");
        }

        async ValueTask CopyAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temp = destination + ".tmp";
            File.Copy(source, temp, true);
            File.Move(temp, destination);
            string provenance = JsonSerializer.Serialize(new { importedFrom = new { assetUuid = asset.Uuid, mount = mount.Name, mountUuid = mount.Uuid, path = asset.RelPath }, hash = HashFile(destination), importedAt = clock.UtcNow }, Indented);
            await Ample.Audio.IO.RawFiles.WriteTextAsync(destination + ".provenance.json", provenance + "\n", cancellationToken).ConfigureAwait(false);
        }

        if (bus is null)
        {
            await CopyAsync().ConfigureAwait(false);
        }
        else
        {
            await bus.RunAsync("library.import", JsonSerializer.Serialize(new { asset = asset.Uuid, name = asset.Name, to = relative }), CopyAsync, cancellationToken).ConfigureAwait(false);
        }

        return relative;
    }
}

/// <summary>Unlinking a folder from the library: the folder and its assets leave the catalog; no file is touched.</summary>
public sealed class UnlinkMountAct(Library library, string mountId) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => $"Unlink '{library.GetMount(mountId).Name}'";

    /// <inheritdoc />
    public override string Acceptance => "unlink " + library.GetMount(mountId).Name;

    /// <inheritdoc />
    protected override string Kind => "library.unlink";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Mount m = library.GetMount(mountId);
        int count = library.Assets(mountId).Count;
        return ValueTask.FromResult(new DryRunResult([$"'{m.Name}' ({m.Path}) leaves the catalog with its {count.ToString(CultureInfo.InvariantCulture)} asset(s).", "No file is touched. Songs that use these assets will report them as missing until the folder is linked again."], "Nothing is lost by unlinking; link the folder again and scan to get the same asset ids back only if the folder carries its marker."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        library.Unlink(mountId);
        return ValueTask.CompletedTask;
    }
}
