using System.IO.Compression;
using Ample.Core.Abstractions;

namespace Ample.Audio.IO;

/// <summary>
/// Reads zip files that come from outside (a downloaded pack, an uploaded or imported file) with limits: a zip can say it holds a few kilobytes and
/// expand to many gigabytes (a "zip bomb"), or name an entry such as "../x" to leave its folder. Every limit is counted on the bytes that are really
/// produced, not on the size the zip claims.
/// </summary>
public static class SafeZip
{
    /// <summary>The most bytes of a text part (MusicXML) that is read into memory.</summary>
    public const long TextLimit = 64L * 1024 * 1024;

    /// <summary>Reads the text of an entry; refuses an entry that expands to more than <paramref name="maxBytes"/>.</summary>
    public static string ReadText(ZipArchiveEntry entry, long maxBytes = TextLimit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using MemoryStream memory = new();
        CopyLimited(entry, memory, maxBytes);
        memory.Position = 0;
        using StreamReader reader = new(memory);
        return reader.ReadToEnd();
    }

    /// <summary>Extracts one entry to a file; refuses an entry that expands to more than <paramref name="maxBytes"/> (the partial file is removed).</summary>
    public static void ExtractEntry(ZipArchiveEntry entry, string target, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            using FileStream file = File.Create(target);
            CopyLimited(entry, file, maxBytes);
        }
        catch (AmpleException)
        {
            try { File.Delete(target); } catch (IOException) { /* the caller removes its staging folder */ }
            throw;
        }
    }

    /// <summary>
    /// Extracts a whole zip into a folder: no path may leave the folder (absolute, "..", a drive or stream colon), at most <paramref name="maxEntries"/>
    /// entries and <paramref name="maxTotalBytes"/> bytes in all. The folder is left as it is when something is refused; the caller deletes its staging folder.
    /// </summary>
    public static void ExtractAll(string zipPath, string folder, long maxTotalBytes, int maxEntries)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > maxEntries) throw new AmpleException("AMP5285", $"The archive holds more than {maxEntries} files.");
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(folder);
        long remaining = maxTotalBytes;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName;
            string[] parts = name.Split('/', '\\');
            if (name.Length == 0 || name[0] is '/' or '\\' || name.Contains(':', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal) || parts.Contains(".."))
            {
                throw new AmpleException("AMP5285", $"The archive holds an unsafe path ('{name}').");
            }

            string target = Path.GetFullPath(Path.Combine(folder, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new AmpleException("AMP5285", $"The archive holds a path outside its folder ('{name}').");
            if (name.EndsWith('/') || name.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream file = File.Create(target);
            remaining -= CopyLimited(entry, file, remaining);
        }
    }

    private static long CopyLimited(ZipArchiveEntry entry, Stream to, long limit)
    {
        if (entry.Length > limit) throw TooLarge(entry.FullName);
        using Stream from = entry.Open();
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit) throw TooLarge(entry.FullName);
            to.Write(buffer, 0, read);
        }

        return total;
    }

    private static AmpleException TooLarge(string name) => new("AMP5285", $"The archive part '{name}' expands to more than the allowed size.");
}
