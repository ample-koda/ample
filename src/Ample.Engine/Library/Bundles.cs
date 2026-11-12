using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;

namespace Ample.Engine.Library;

/// <summary>A parameter of a bundled block that names a file or a sample folder: the part a user may swap after placing the bundle.</summary>
/// <param name="Block">The block name inside the bundle.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="Value">The value it had when the bundle was saved.</param>
public sealed record BundleSlot(string Block, string Param, string Value);

/// <summary>A project file the bundle carries.</summary>
/// <param name="Path">The project path (forward slashes).</param>
/// <param name="Sha256">Its hash, lower case hex.</param>
public sealed record BundleFileRef(string Path, string Sha256);

/// <summary>The audio of one output of one block, as saved with the bundle.</summary>
/// <param name="Block">The block name inside the bundle.</param>
/// <param name="Role">The output role (audio, mix, lane1...).</param>
/// <param name="File">The file under the bundle folder.</param>
/// <param name="Sha256">Its hash.</param>
public sealed record BundleTakeRef(string Block, string Role, string File, string Sha256);

/// <summary>The description of a bundle (<c>bundle.json</c>).</summary>
public sealed record BundleInfo(int Version, string Id, string Name, string Description, string Created, IReadOnlyList<string> Blocks, IReadOnlyList<BundleSlot> Slots, IReadOnlyList<BundleFileRef> Files, IReadOnlyList<BundleTakeRef> Takes, IReadOnlyList<string> OpenInputs, IReadOnlyDictionary<string, ulong> Seeds);

/// <summary>A file the placing has to write into the project.</summary>
/// <param name="Path">The project path.</param>
/// <param name="Data">The bytes.</param>
public sealed record BundleFileWrite(string Path, byte[] Data);

/// <summary>What placing a bundle in a song needs: the text to insert, the files to write and the blocks that may be given their saved audio.</summary>
/// <param name="Text">The block text with the new names.</param>
/// <param name="Names">The bundle's block names to the names they get in the song.</param>
/// <param name="Files">Files of the bundle that are not in the project yet.</param>
/// <param name="Conflicts">Project files that exist with other content than the bundle's.</param>
/// <param name="Adoptable">The new names of the blocks whose saved audio is still the audio of their text (no changed slot, no conflicting file).</param>
/// <param name="OpenInputs">Connections that came from blocks outside the bundle and were dropped (block.input).</param>
/// <param name="Seeds">The seed each new block has to be given (a block name in the song to its seed): the seed it had when it was saved.</param>
public sealed record BundlePlacement(string Text, IReadOnlyDictionary<string, string> Names, IReadOnlyList<BundleFileWrite> Files, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Adoptable, IReadOnlyList<string> OpenInputs, IReadOnlyDictionary<string, ulong> Seeds);

/// <summary>
/// Library bundles (plan bundles BU01, BU02): blocks saved with the files they read, the seeds they had and the audio of their takes, in a folder of the library home.
/// Placing a bundle adds the blocks under new names; the saved audio is handed to the build (<see cref="BuildOptions.Adopt"/>), so a generative block sounds exactly as saved.
/// </summary>
public static class Bundles
{
    /// <summary>The folder that holds the bundles.</summary>
    public static string Root(string home) => Path.Combine(home, "bundles");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Lists the bundles, by name.</summary>
    public static IReadOnlyList<BundleInfo> List(string home)
    {
        string root = Root(home);
        if (!Directory.Exists(root)) return [];
        List<BundleInfo> found = [];
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            try { found.Add(Read(folder)); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* a folder that is not a bundle is not listed */ }
        }

        return [.. found.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Reads one bundle.</summary>
    /// <exception cref="AmpleException">AMP5391 when there is no such bundle.</exception>
    public static BundleInfo Get(string home, string id)
    {
        string folder = FolderOf(home, id);
        if (!File.Exists(Path.Combine(folder, "bundle.json"))) throw new AmpleException("AMP5391", $"There is no bundle '{id}' in the library.");
        return Read(folder);
    }

    private static BundleInfo Read(string folder) => JsonSerializer.Deserialize<BundleInfo>(File.ReadAllText(Path.Combine(folder, "bundle.json")), Json) ?? throw new JsonException("empty bundle.json");

    private static string FolderOf(string home, string id)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new AmpleException("AMP5391", $"'{id}' is not a bundle id.");
        return Path.Combine(Root(home), id);
    }

    /// <summary>Removes a bundle from the library.</summary>
    public static void Delete(string home, string id)
    {
        string folder = FolderOf(home, id);
        if (!Directory.Exists(folder)) throw new AmpleException("AMP5391", $"There is no bundle '{id}' in the library.");
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Saves blocks of a song as a bundle.</summary>
    /// <param name="home">The library home.</param>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="ampleRoot">The AMPLE root (for packs), or null.</param>
    /// <param name="doc">The parsed song.</param>
    /// <param name="spec">The block templates.</param>
    /// <param name="blocks">The block names to save.</param>
    /// <param name="name">The name of the bundle.</param>
    /// <param name="description">A line about it.</param>
    /// <param name="seedOf">The seed a block really has (typed, locked or derived).</param>
    /// <param name="takeOf">The saved audio of a block (role and bytes), or null when it has no take.</param>
    /// <param name="created">The creation time text.</param>
    public static BundleInfo Save(string home, string projectRoot, string? ampleRoot, AmpleDocument doc, SpecData spec, IReadOnlyList<string> blocks, string name, string description, Func<string, ulong> seedOf, Func<string, IReadOnlyList<(string Role, byte[] Data)>?> takeOf, string created)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(spec);
        if (blocks.Count == 0) throw new AmpleException("AMP5390", "Choose at least one block to save as a bundle.");
        if (string.IsNullOrWhiteSpace(name)) throw new AmpleException("AMP5390", "A bundle needs a name.");
        List<BlockNode> chosen = [];
        foreach (string block in blocks.Distinct(StringComparer.Ordinal))
        {
            BlockNode node = doc.Blocks.FirstOrDefault(b => b.Name == block) ?? throw new AmpleException("AMP5390", $"The song has no block '{block}'.");
            if (node.Name.Contains('/', StringComparison.Ordinal)) throw new AmpleException("AMP5390", $"'{block}' comes from a module and cannot be saved on its own.");
            if (node.CloseLine < 0 || node.Skipped) throw new AmpleException("AMP5390", $"The block '{block}' is not complete; fix it first.");
            chosen.Add(node);
        }

        HashSet<string> names = [.. chosen.Select(b => b.Name)];
        string id = Slug(name);
        string root = Root(home);
        Directory.CreateDirectory(root);
        for (int n = 2; Directory.Exists(Path.Combine(root, id)); n++) id = Slug(name) + "-" + n.ToString(CultureInfo.InvariantCulture);
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        try
        {
            StringBuilder text = new();
            List<string> open = [];
            List<BundleSlot> slots = [];
            SortedDictionary<string, string> files = new(StringComparer.Ordinal);
            List<BundleTakeRef> takes = [];
            Dictionary<string, ulong> seeds = [];
            foreach (BlockNode block in chosen.OrderBy(b => b.Line))
            {
                List<string> lines = [];
                for (int i = block.Line; i <= block.CloseLine; i++) lines.Add(doc.Lines[i]);
                // from lines of blocks outside the bundle are dropped; they are listed so the user can connect them again
                foreach (FromLine from in block.Froms.OrderByDescending(f => f.Line))
                {
                    if (names.Contains(from.Block)) continue;
                    lines.RemoveAt(from.Line - block.Line);
                    open.Add(block.Name + "." + from.Input);
                }

                if (!BuildEngine.IsSeedFree(block.Type) && !block.Parameters.Any(p => p.Name == "seed" && ulong.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    // the seed the block had stays with it (it is given to the block again when the bundle is placed), so a new name does not change the sound
                    seeds[block.Name] = seedOf(block.Name);
                }

                text.Append(string.Join('\n', lines)).Append("\n\n");
                spec.Templates.TryGetValue(block.Type, out TemplateSpec? template);
                foreach (Parameter p in block.Parameters)
                {
                    string kind = template?.ParameterSpecs.GetValueOrDefault(p.Name)?.Kind ?? "text";
                    if (kind is "file" or "sampleset") slots.Add(new BundleSlot(block.Name, p.Name, p.Value.Trim().Trim('"')));
                }

                foreach (string full in BlockFiles.Of(projectRoot, ampleRoot, doc, block))
                {
                    string relative = Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;   // a pack or a library file stays a reference
                    if (files.ContainsKey(relative)) continue;
                    string target = Path.Combine(folder, "files", relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(full, target);
                    files[relative] = Hash(File.ReadAllBytes(full));
                }

                if (takeOf(block.Name) is { } outputs)
                {
                    foreach ((string role, byte[] data) in outputs)
                    {
                        string relative = "takes/" + block.Name + "/" + role + ".bin";
                        string target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, data);
                        takes.Add(new BundleTakeRef(block.Name, role, relative, Hash(data)));
                    }
                }
            }

            File.WriteAllBytes(Path.Combine(folder, "blocks.amplet"), Encoding.UTF8.GetBytes(text.ToString().TrimEnd() + "\n"));   // the library home is not a project: no command bus
            BundleInfo info = new(1, id, name.Trim(), description.Trim(), created, [.. chosen.OrderBy(b => b.Line).Select(b => b.Name)], slots, [.. files.Select(f => new BundleFileRef(f.Key, f.Value))], takes, [.. open.Distinct(StringComparer.Ordinal)], seeds);
            File.WriteAllBytes(Path.Combine(folder, "bundle.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, Json)));
            return info;
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>Works out what placing a bundle in a song needs. Nothing is written.</summary>
    /// <param name="home">The library home.</param>
    /// <param name="id">The bundle id.</param>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="spec">The block templates.</param>
    /// <param name="existing">The block names the song already has.</param>
    /// <param name="prefix">A prefix for every block name (empty keeps the names).</param>
    /// <param name="newNames">A new name for single blocks, by their name in the bundle.</param>
    /// <param name="slotValues">New values for slots, as <c>block.param</c> to value.</param>
    /// <param name="useExisting">Whether a project file with other content than the bundle's may stay (the blocks that read it are not given the saved audio).</param>
    public static BundlePlacement Place(string home, string id, string projectRoot, SpecData spec, IReadOnlyCollection<string> existing, string prefix, IReadOnlyDictionary<string, string>? newNames, IReadOnlyDictionary<string, string>? slotValues, bool useExisting)
    {
        ArgumentNullException.ThrowIfNull(spec);
        BundleInfo info = Get(home, id);
        string folder = FolderOf(home, id);
        AmpleDocument source = Parser.Parse(File.ReadAllText(Path.Combine(folder, "blocks.amplet")), spec);
        Dictionary<string, string> map = [];
        HashSet<string> taken = [.. existing];
        foreach (string block in info.Blocks)
        {
            string wanted = newNames is not null && newNames.TryGetValue(block, out string? chosen) && chosen.Length > 0 ? chosen : prefix + block;
            if (wanted.Length == 0 || wanted.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) throw new AmpleException("AMP5392", $"'{wanted}' is not a block name (letters, digits, - and _).");
            if (!taken.Add(wanted)) throw new AmpleException("AMP5392", $"The song already has a block called '{wanted}'. Give the bundle another prefix or name.");
            map[block] = wanted;
        }

        // the files: new ones are written, identical ones are shared, different ones are a conflict
        List<BundleFileWrite> writes = [];
        HashSet<string> conflicts = new(StringComparer.Ordinal);
        foreach (BundleFileRef file in info.Files)
        {
            string target = ProjectPath.Resolve(projectRoot, file.Path);
            if (File.Exists(target))
            {
                if (!string.Equals(Hash(File.ReadAllBytes(target)), file.Sha256, StringComparison.Ordinal)) conflicts.Add(file.Path);
                continue;
            }

            writes.Add(new BundleFileWrite(file.Path, File.ReadAllBytes(Path.Combine(folder, "files", file.Path.Replace('/', Path.DirectorySeparatorChar)))));
        }

        if (conflicts.Count > 0 && !useExisting) throw new AmpleException("AMP5393", $"The project already has other versions of: {string.Join(", ", conflicts.Take(5))}{(conflicts.Count > 5 ? ", ..." : string.Empty)}. Place the bundle again and keep the project's files, or rename them first.");

        List<string> lines = [.. source.Lines];
        List<string> open = [];
        foreach (BlockNode block in source.Blocks)
        {
            if (!map.TryGetValue(block.Name, out string? renamed)) continue;
            lines[block.Line] = ReplaceFirst(lines[block.Line], block.RawName, Edits.Quote(renamed), block.Type);
            foreach (FromLine from in block.Froms.Where(f => map.ContainsKey(f.Block))) lines[from.Line] = ReplaceFirst(lines[from.Line], from.RawBlock, Edits.Quote(map[from.Block]), "from");
            foreach (Parameter p in block.Parameters)
            {
                if (slotValues is null || !slotValues.TryGetValue(block.Name + "." + p.Name, out string? value)) continue;
                lines[p.Line] = ReplaceFirst(lines[p.Line], p.Value, value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value, p.Name);
            }
        }

        // a block may be given its saved audio when nothing it reads or says has changed since: no swapped slot, no other file of the same path, nothing that feeds it differs
        HashSet<string> changed = [.. (slotValues ?? new Dictionary<string, string>()).Keys.Select(k => k[..k.IndexOf('.', StringComparison.Ordinal)])];
        List<string> adoptable = [];
        foreach (BlockNode block in source.Blocks.Where(b => map.ContainsKey(b.Name)))
        {
            bool saved = info.Takes.Any(t => t.Block == block.Name);
            bool unchanged = !changed.Contains(block.Name);
            bool clean = conflicts.Count == 0 || !info.Slots.Any(s => s.Block == block.Name && conflicts.Any(c => c.StartsWith(s.Value.TrimEnd('/') + "/", StringComparison.Ordinal) || c == s.Value));
            if (saved && unchanged && clean) adoptable.Add(map[block.Name]);
        }

        // a block whose input is not given the saved audio renders, and so does everything after it
        HashSet<string> good = [.. adoptable];
        bool shrink = true;
        while (shrink)
        {
            shrink = false;
            foreach (BlockNode block in source.Blocks.Where(b => map.ContainsKey(b.Name)))
            {
                if (good.Contains(map[block.Name]) && block.Froms.Any(f => map.TryGetValue(f.Block, out string? up) && !good.Contains(up))) { good.Remove(map[block.Name]); shrink = true; }
            }
        }

        return new BundlePlacement(string.Join('\n', lines).TrimEnd() + "\n", map, writes, [.. conflicts], [.. adoptable.Where(good.Contains)], info.OpenInputs, info.Seeds.Where(x => map.ContainsKey(x.Key)).ToDictionary(x => map[x.Key], x => x.Value));
    }

    /// <summary>Loads the saved audio of the blocks of a bundle, by the name the blocks have in the song.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<RenderOutput>> LoadTakes(string home, string id, IReadOnlyDictionary<string, string> names)
    {
        BundleInfo info = Get(home, id);
        string folder = FolderOf(home, id);
        Dictionary<string, IReadOnlyList<RenderOutput>> loaded = new(StringComparer.Ordinal);
        foreach (IGrouping<string, BundleTakeRef> group in info.Takes.GroupBy(t => t.Block))
        {
            if (!names.TryGetValue(group.Key, out string? renamed)) continue;
            List<RenderOutput> outputs = [];
            foreach (BundleTakeRef take in group)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(folder, take.File.Replace('/', Path.DirectorySeparatorChar)));
                if (!string.Equals(Hash(data), take.Sha256, StringComparison.Ordinal)) throw new AmpleException("AMP5391", $"The saved audio of '{group.Key}' in the bundle '{id}' is damaged (its hash is different).");
                outputs.Add(new RenderOutput(take.Role, data));
            }

            loaded[renamed] = outputs;
        }

        return loaded;
    }

    // replaces the first occurrence of a word after a marker word (the type of a block, the word from, a parameter name), so a block called like its type keeps its type
    private static string ReplaceFirst(string line, string from, string to, string after)
    {
        int start = line.IndexOf(after, StringComparison.Ordinal);
        start = start < 0 ? 0 : start + after.Length;
        int at = from.Length == 0 ? -1 : line.IndexOf(from, start, StringComparison.Ordinal);
        return at < 0 ? line : line[..at] + to + line[(at + from.Length)..];
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Slug(string name)
    {
        string slug = new([.. name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "bundle" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}
