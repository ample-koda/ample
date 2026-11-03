using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Ample.Audio.Engine;

/// <summary>
/// The sha256 of a file, remembered while the file stays the same (same length and same last-write time).
/// A build plan names the hash of every file a block uses, and the plan is made again for every status request and every build:
/// without this a 200 MB SoundFont was read and hashed once for every block that uses it. The hash is exactly the sha256 of the bytes;
/// a file that changes in place without a new length or last-write time is the one case that is not noticed.
/// </summary>
public static class FileHashCache
{
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Hash)> Known = new(StringComparer.Ordinal);

    /// <summary>Gets the lower-case hex sha256 of the file's bytes.</summary>
    /// <param name="path">The file.</param>
    public static string Sha256Hex(string path)
    {
        string key = Path.GetFullPath(path);
        FileInfo before = new(key);
        long length = before.Length, ticks = before.LastWriteTimeUtc.Ticks;
        if (Known.TryGetValue(key, out (long Length, long Ticks, string Hash) known) && known.Length == length && known.Ticks == ticks)
        {
            return known.Hash;
        }

        string hash;
        using (FileStream stream = new(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        // a file that changed while it was being read is not remembered
        FileInfo after = new(key);
        if (after.Length == length && after.LastWriteTimeUtc.Ticks == ticks)
        {
            Known[key] = (length, ticks, hash);
        }

        return hash;
    }
}
