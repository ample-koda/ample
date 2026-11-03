using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Storage.History;

/// <summary>
/// The safety copy a history clean-up makes of the content of dropped lines before it removes anything: <c>.ample/history-archive/&lt;stamp&gt;/</c> holds
/// <c>manifest.json</c> (line names, revision ids, files) and <c>blobs/&lt;hash&gt;</c> (the exact bytes). A folder is built as <c>&lt;stamp&gt;.partial</c>, every blob is
/// read back and hashed, and only then is it renamed; the clean-up itself never deletes an archive.
/// </summary>
internal static class HistoryArchive
{
    internal const string FolderName = "history-archive";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    internal static string Folder(string root) => Path.Combine(root, ".ample", FolderName);

    /// <summary>All blob files in finished archives (also used by the storage scan, so takes named in archived revisions stay protected).</summary>
    internal static IEnumerable<string> BlobFiles(string root)
    {
        string folder = Folder(root);
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (string archive in Directory.EnumerateDirectories(folder).Where(d => !d.EndsWith(".partial", StringComparison.Ordinal)))
        {
            string blobs = Path.Combine(archive, "blobs");
            if (Directory.Exists(blobs))
            {
                foreach (string f in Directory.EnumerateFiles(blobs))
                {
                    yield return f;
                }
            }
        }
    }

    /// <summary>Finds an archived copy of a blob (<c>sha256:...</c>), or null.</summary>
    internal static string? Find(string root, string blobId)
    {
        string hex = blobId.StartsWith("sha256:", StringComparison.Ordinal) ? blobId["sha256:".Length..] : blobId;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? BlobFiles(root).FirstOrDefault(f => string.Equals(Path.GetFileName(f), hex, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>Total bytes of all finished archives.</summary>
    internal static long TotalBytes(string root) => BlobFiles(root).Sum(f => new FileInfo(f).Length);

    /// <summary>Writes and verifies an archive. Throws AMP5247 (and leaves nothing behind) when anything fails.</summary>
    internal static string Write(string root, DateTimeOffset at, string reason, IReadOnlyList<(string Line, string Head, IReadOnlyList<(string Id, string Message, IReadOnlyDictionary<string, string> Files)> Revisions)> lines, IReadOnlyList<string> blobs, Func<string, Stream> open)
    {
        string stamp = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string parent = Folder(root);
        string final = Path.Combine(parent, stamp);
        for (int i = 2; Directory.Exists(final) || Directory.Exists(final + ".partial"); i++)
        {
            final = Path.Combine(parent, stamp + "-" + i.ToString(CultureInfo.InvariantCulture));
        }

        string partial = final + ".partial";
        try
        {
            string blobFolder = Path.Combine(partial, "blobs");
            Directory.CreateDirectory(blobFolder);
            foreach (string id in blobs)
            {
                string hex = id["sha256:".Length..];
                string target = Path.Combine(blobFolder, hex);
                using (Stream input = open(id))
                using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }

                using FileStream check = File.OpenRead(target);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(check)), hex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("verification failed");
                }
            }

            var manifest = new
            {
                version = 1,
                at = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                reason,
                lines = lines.Select(l => new { name = l.Line, head = l.Head, revisions = l.Revisions.Select(r => new { id = r.Id, message = r.Message, files = r.Files }) }),
                blobs = blobs.Select(b => new { id = b, bytes = new FileInfo(Path.Combine(blobFolder, b["sha256:".Length..])).Length }),
            };
            string manifestPath = Path.Combine(partial, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Pretty));
            using (JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
            }

            Directory.Move(partial, final);
            return final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or AmpleException)
        {
            try
            {
                if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // the half-written copy stays under its .partial name; it is never used
            }

            throw new AmpleException("AMP5247", "The safety copy of the dropped lines could not be written or verified, so the clean-up removed nothing. Check the free disk space and the folder .ample/history-archive.");
        }
    }
}
