using System.Globalization;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Storage.Library;

namespace Ample.Engine.Library;

/// <summary>One line of <c>ample.lock</c>.</summary>
/// <param name="Section">asset, pack, plugin or module.</param>
/// <param name="Id">Asset uuid, or pack/plugin name and version.</param>
/// <param name="Name">Name as the song writes it.</param>
/// <param name="Hash">Pinned content hash.</param>
/// <param name="Size">Size in bytes (assets).</param>
/// <param name="MountUuid">Catalog uuid (assets).</param>
public sealed record LockEntry(string Section, string Id, string Name, string Hash, long Size, string MountUuid);

/// <summary>The verdict for one lock entry.</summary>
/// <param name="Entry">The entry.</param>
/// <param name="Status">ok, changed, missing or offline.</param>
/// <param name="Message">One plain sentence for the problems.</param>
public sealed record LockCheck(LockEntry Entry, string Status, string Message);

/// <summary>
/// <c>ample.lock</c>: the assets, packs and plugins a project depends on, pinned by content hash. The song text shows names;
/// the lock holds ids and hashes, so renaming an asset or moving a folder changes nothing here. Written in a fixed order, so
/// the same project always gives the same bytes.
/// </summary>
public static class LockFile
{
    /// <summary>The file name.</summary>
    public const string FileName = "ample.lock";

    /// <summary>Writes entries in the canonical text form.</summary>
    public static string Serialize(IEnumerable<LockEntry> entries)
    {
        StringBuilder sb = new();
        sb.Append("# ample.lock - managed by 'ample lock', not by hand.\nversion 1\n");
        foreach (string section in new[] { "asset", "pack", "plugin", "module", "component" })
        {
            List<LockEntry> list = [.. entries.Where(e => e.Section == section).OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
            if (list.Count == 0)
            {
                continue;
            }

            sb.Append('\n').Append(section).Append("s\n");
            foreach (LockEntry e in list)
            {
                sb.Append("  ").Append(e.Id).Append(' ').Append(JsonSerializer.Serialize(e.Name)).Append(" mount ").Append(e.MountUuid.Length == 0 ? "-" : e.MountUuid).Append(' ').Append(e.Hash).Append(" size ").Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Reads the canonical text form.</summary>
    /// <exception cref="AmpleException">AMP5296 when the file is damaged.</exception>
    public static IReadOnlyList<LockEntry> Parse(string text)
    {
        List<LockEntry> entries = [];
        string? section = null;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("version ", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith(' '))
            {
                section = line switch { "assets" => "asset", "packs" => "pack", "plugins" => "plugin", "modules" => "module", "components" => "component", _ => throw new AmpleException("AMP5296", $"ample.lock has an unknown section '{line}'.") };
                continue;
            }

            string t = line.TrimStart();
            int sp = t.IndexOf(' ', StringComparison.Ordinal);
            if (section is null || sp < 0)
            {
                throw new AmpleException("AMP5296", $"ample.lock has a damaged line: {line.Trim()}");
            }

            string id = t[..sp];
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(t[(sp + 1)..]));
            string rest;
            string name;
            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    throw new AmpleException("AMP5296", $"ample.lock has a damaged line: {line.Trim()}");
                }

                name = reader.GetString()!;
                rest = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(t[(sp + 1)..]).AsSpan((int)reader.BytesConsumed)).Trim();
            }
            catch (JsonException)
            {
                throw new AmpleException("AMP5296", $"ample.lock has a damaged line: {line.Trim()}");
            }

            string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0] != "mount" || parts[3] != "size" || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            {
                throw new AmpleException("AMP5296", $"ample.lock has a damaged line: {line.Trim()}");
            }

            entries.Add(new LockEntry(section, id, name, parts[2], size, parts[1] == "-" ? string.Empty : parts[1]));
        }

        return entries;
    }

    /// <summary>
    /// Works out what the songs use: catalog assets named by block parameters, and packs. Plugins are pinned by their
    /// manifest file when a plugins folder is given.
    /// </summary>
    public static IReadOnlyList<LockEntry> Collect(Ample.Storage.Library.Library library, IEnumerable<AmpleDocument> songs, string? pluginsFolder = null, IEnumerable<Ample.Language.ModuleUsage>? modules = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        List<LockEntry> entries = [];
        HashSet<string> seen = [];
        foreach (AmpleDocument doc in songs)
        {
            foreach (AssetUse u in library.Uses(doc))
            {
                AssetRecord a = library.Asset(u.AssetUuid)!;
                if (a.Hash is null || !seen.Add(a.Uuid))
                {
                    continue;
                }

                Mount m = library.GetMount(a.MountId);
                entries.Add(new LockEntry(a.Kind == AssetKind.Pack ? "pack" : "asset", a.Kind == AssetKind.Pack ? a.Name : a.Uuid, a.Name, a.Hash, a.Size, m.Uuid));
            }
        }

        if (pluginsFolder is not null && Directory.Exists(pluginsFolder))
        {
            HashSet<string> needed = [.. songs.SelectMany(s => s.Blocks).Select(b => b.Type)];
            foreach (string manifest in Directory.EnumerateFiles(pluginsFolder, "plugin.json", SearchOption.AllDirectories))
            {
                try
                {
                    using JsonDocument d = JsonDocument.Parse(File.ReadAllText(manifest));
                    string id = d.RootElement.GetProperty("id").GetString()!;
                    string version = d.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString()! : "0";
                    bool used = d.RootElement.TryGetProperty("blocks", out JsonElement blocks) && blocks.EnumerateArray().Any(b => needed.Contains(b.GetString() ?? string.Empty));
                    if (used)
                    {
                        entries.Add(new LockEntry("plugin", $"{id}@{version}", id, Ample.Storage.Library.Library.HashFile(manifest), new FileInfo(manifest).Length, string.Empty));
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
                {
                    // A damaged manifest is reported by the plugin host, not here.
                }
            }
        }

        if (modules is not null)
        {
            entries.AddRange(Modules.ModuleService.Pins(modules));
        }

        return entries;
    }

    /// <summary>Works out what the songs use and writes <c>ample.lock</c> in the project folder, as a command of the black box when a bus is given.</summary>
    public static async Task<string> UpdateAsync(string projectRoot, Ample.Storage.Library.Library library, IEnumerable<AmpleDocument> songs, string? pluginsFolder = null, Ample.Storage.BlackBox.CommandBus? bus = null, IEnumerable<Ample.Language.ModuleUsage>? modules = null, CancellationToken cancellationToken = default)
    {
        string text = Serialize(Collect(library, songs, pluginsFolder, modules));
        if (bus is not null)
        {
            await bus.ExecuteAsync(new Ample.Storage.BlackBox.WriteFileCommand(FileName, text), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Ample.Audio.IO.RawFiles.WriteTextAsync(Path.Combine(projectRoot, FileName), text, cancellationToken).ConfigureAwait(false);
        }

        return text;
    }

    /// <summary>Checks every entry against the catalog and the plugins: ok, changed (another hash), missing or offline.</summary>
    public static IReadOnlyList<LockCheck> Verify(string projectRoot, Ample.Storage.Library.Library library, string? pluginsFolder = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        string file = Path.Combine(projectRoot, FileName);
        if (!File.Exists(file))
        {
            throw new AmpleException("AMP5296", "There is no ample.lock in this project. Run 'ample lock update'.");
        }

        List<LockCheck> checks = [];
        foreach (LockEntry e in Parse(File.ReadAllText(file)))
        {
            if (e.Section == "module")
            {
                checks.AddRange(new Modules.ModuleService(projectRoot, library).VerifyPins([e]));
                continue;
            }

            if (e.Section == "component")
            {
                // a component is checked when a song that uses it is parsed (AMP2203 says when its code changed since the pin)
                checks.Add(new LockCheck(e, "ok", string.Empty));
                continue;
            }

            if (e.Section == "plugin")
            {
                string? manifest = pluginsFolder is null || !Directory.Exists(pluginsFolder) ? null : Directory.EnumerateFiles(pluginsFolder, "plugin.json", SearchOption.AllDirectories).FirstOrDefault(m => Ample.Storage.Library.Library.HashFile(m) == e.Hash || m.Contains(Path.DirectorySeparatorChar + e.Name + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                checks.Add(manifest is null ? new LockCheck(e, "missing", $"Plugin {e.Id} is not installed.") : Ample.Storage.Library.Library.HashFile(manifest) == e.Hash ? new LockCheck(e, "ok", string.Empty) : new LockCheck(e, "changed", $"Plugin {e.Id} is not the pinned version."));
                continue;
            }

            AssetRecord? a = e.Section == "pack" ? library.Assets(kind: AssetKind.Pack).FirstOrDefault(x => x.Name == e.Name) : library.Asset(e.Id);
            if (a is null)
            {
                checks.Add(new LockCheck(e, "missing", $"'{e.Name}' is not in the catalog. Link the folder that holds it."));
                continue;
            }

            Mount m = library.GetMount(a.MountId);
            if (m.State != "online")
            {
                checks.Add(new LockCheck(e, "offline", $"'{e.Name}' is in '{m.Name}', which is not reachable."));
                continue;
            }

            string full = Path.Combine(m.Path, a.RelPath);
            if (a.Missing || !File.Exists(full))
            {
                checks.Add(new LockCheck(e, "missing", $"'{e.Name}' is gone from '{m.Name}'."));
                continue;
            }

            checks.Add(Ample.Storage.Library.Library.HashFile(full) == e.Hash ? new LockCheck(e, "ok", string.Empty) : new LockCheck(e, "changed", $"'{e.Name}' has changed since it was pinned. Accept the change ('ample lock update') or restore the pinned file."));
        }

        return checks;
    }
}
