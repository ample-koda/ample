using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Core.Abstractions;

namespace Ample.Storage.Backup;

/// <summary>
/// The export format: one zip file. <c>manifest.json</c> (format 1: tool, project id and name, time, whether takes are
/// included, and path, sha256 and size of every file) and the files under <c>files/&lt;path&gt;</c>. A third party can read it with any
/// zip tool and <c>sha256sum</c>. Import checks every hash and refuses a folder that is not empty.
/// </summary>
public static class Archive
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Writes the project into an archive (the database as a consistent copy, no secrets, no locks).</summary>
    public static BackupManifest Export(string projectRoot, Guid projectId, string projectName, string file, bool includeTakes, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        string temp = Path.Combine(Path.GetTempPath(), "ample-export-" + Guid.NewGuid().ToString("N")[..8]);
        BackupStore staging = new(temp, clock, _ => long.MaxValue);
        try
        {
            BackupReport report = staging.CreatePlain(projectRoot, projectId, projectName, includeTakes, CancellationToken.None);
            BackupManifest manifest = staging.Get(report.Id);
            string partial = file + ".part";
            using (ZipArchive zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                ZipArchiveEntry m = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (StreamWriter w = new(m.Open()))
                {
                    w.Write(JsonSerializer.Serialize(new { format = 1, tool = "ample", projectId, projectName, created = manifest.Created, includesTakes = includeTakes, entries = manifest.Entries }, Indented) + "\n");
                }

                foreach (BackupEntry e in manifest.Entries)
                {
                    zip.CreateEntryFromFile(staging.ObjectPath(e.Hash), "files/" + e.Path, CompressionLevel.Optimal);
                }
            }

            File.Move(partial, file, true);
            return manifest;
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    /// <summary>Reads the manifest of an archive without extracting.</summary>
    /// <exception cref="AmpleException">AMP5302 when it is not an AMPLE archive.</exception>
    public static BackupManifest ReadManifest(string file)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(file);
            ZipArchiveEntry entry = zip.GetEntry("manifest.json") ?? throw new AmpleException("AMP5302", "The archive has no manifest.json.");
            using StreamReader r = new(entry.Open());
            using JsonDocument d = JsonDocument.Parse(r.ReadToEnd());
            JsonElement root = d.RootElement;
            if (root.GetProperty("format").GetInt32() != 1)
            {
                throw new AmpleException("AMP5302", "This archive has a format version this AMPLE does not read.");
            }

            List<BackupEntry> entries = [.. root.GetProperty("entries").EnumerateArray().Select(e => new BackupEntry(e.GetProperty("path").GetString()!, e.GetProperty("hash").GetString()!, e.GetProperty("size").GetInt64()))];
            return new BackupManifest(1, "archive", root.GetProperty("projectId").GetGuid(), root.GetProperty("projectName").GetString()!, root.GetProperty("created").GetDateTimeOffset(), root.GetProperty("includesTakes").GetBoolean(), entries);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AmpleException("AMP5302", "This is not an AMPLE archive: " + ex.Message);
        }
    }

    /// <summary>Extracts an archive into a new (empty or missing) folder after checking every hash; optionally gives the project a new id.</summary>
    /// <exception cref="AmpleException">AMP5302 when a file does not match its hash, AMP5301 when the folder is not empty.</exception>
    public static BackupManifest Import(string file, string folder, bool newId)
    {
        BackupManifest manifest = ReadManifest(file);
        string root = Path.GetFullPath(folder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new AmpleException("AMP5301", $"The folder '{root}' is not empty. An import goes into a new folder.");
        }

        string staging = root + ".importing-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(file);
            foreach (BackupEntry e in manifest.Entries)
            {
                ZipArchiveEntry entry = zip.GetEntry("files/" + e.Path) ?? throw new AmpleException("AMP5302", $"The archive is missing '{e.Path}'.");
                string dest = ProjectPath.Resolve(staging, e.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
                if (BackupStore.HashFile(dest) != e.Hash)
                {
                    throw new AmpleException("AMP5302", $"'{e.Path}' in the archive does not match its recorded hash: the archive is damaged or was changed. Nothing was imported.");
                }
            }

            if (newId)
            {
                string pf = Path.Combine(staging, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile);
                if (File.Exists(pf))
                {
                    ProjectManifest p = ProjectFile.Parse(File.ReadAllText(pf));
                    Ample.Audio.IO.RawFiles.WriteText(pf, ProjectFile.Serialize(p with { Id = Guid.NewGuid() }));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(root) ?? ".");
            if (Directory.Exists(root))
            {
                Directory.Delete(root);
            }

            Directory.Move(staging, root);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            throw;
        }
    }

    /// <summary>Describes an archive in one line.</summary>
    public static string Describe(BackupManifest m) => string.Create(CultureInfo.InvariantCulture, $"{m.ProjectName} ({m.ProjectId}), {m.Entries.Count} files, {m.Entries.Sum(e => e.Size)} bytes, {(m.IncludesTakes ? "with" : "without")} takes, {m.Created:u}");
}
