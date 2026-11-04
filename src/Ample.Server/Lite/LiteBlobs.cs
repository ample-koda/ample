using System.Security.Cryptography;

namespace Ample.Server.Lite;

/// <summary>
/// The files that a Lite build does not have to be sent again. Two sources, both found by the SHA-256 of the content (never by a path that the page names):
/// the files of the built-in demos and their sounds (<c>web/remix/demos</c> and <c>web/remix/assets</c>, indexed when the server starts), and the files that earlier
/// builds uploaded, which are kept for a while (an hour since they were last used) up to a size limit, the oldest going first.
/// </summary>
public sealed class LiteBlobs
{
    private const long LargestDemoFile = 64L * 1024 * 1024;

    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Kept> kept = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly long maxBytes;
    private readonly TimeSpan lifetime;
    private readonly Func<DateTime> now;
    private long total;

    /// <summary>Makes the store.</summary>
    /// <param name="webRoot">The <c>web</c> folder of AMPLE, or null for no demo files.</param>
    /// <param name="maxBytes">The most bytes of uploaded files that are kept at once.</param>
    /// <param name="lifetime">How long a file is kept after it was last used.</param>
    /// <param name="now">The clock (a test gives its own).</param>
    public LiteBlobs(string? webRoot, long maxBytes, TimeSpan lifetime, Func<DateTime>? now = null)
    {
        this.maxBytes = maxBytes;
        this.lifetime = lifetime;
        this.now = now ?? (() => DateTime.UtcNow);
        if (webRoot is null) return;
        foreach (string folder in new[] { "demos", "assets" })
        {
            string root = Path.Combine(webRoot, "remix", folder);
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(file).Length > LargestDemoFile) continue;
                    known.TryAdd(Hash(File.ReadAllBytes(file)), file);
                }
                catch (IOException)
                {
                    // a file that cannot be read is simply not known
                }
            }
        }
    }

    /// <summary>Gets or sets a second place that knows files by their hash (the stored shares): whether it has one.</summary>
    public Func<string, bool>? ExtraHas { get; set; }

    /// <summary>Gets or sets a second place that knows files by their hash (the stored shares): the bytes of one.</summary>
    public Func<string, byte[]?>? ExtraGet { get; set; }

    /// <summary>True when the content is a file of a built-in demo (those are not stored in shares, the server has them).</summary>
    /// <param name="hash">The SHA-256.</param>
    /// <returns>True for a demo file.</returns>
    public bool IsDemo(string hash) => known.ContainsKey(hash);

    /// <summary>Gets how many demo files are known.</summary>
    public int KnownFiles => known.Count;

    /// <summary>Gets how many bytes of uploaded files are kept now.</summary>
    public long KeptBytes
    {
        get
        {
            lock (sync) return total;
        }
    }

    /// <summary>The SHA-256 of some bytes as 64 lower case hexadecimal characters.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>True when the text is a SHA-256 as <see cref="Hash"/> writes it.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for 64 lower case hexadecimal characters.</returns>
    public static bool IsHash(string? text) => text is { Length: 64 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>True when the server has this content (a demo file, or an upload that is still kept).</summary>
    /// <param name="hash">The SHA-256.</param>
    /// <returns>True when <see cref="TryGet"/> would find it.</returns>
    public bool Has(string hash)
    {
        if (known.ContainsKey(hash)) return true;
        lock (sync)
        {
            Expire();
            if (kept.ContainsKey(hash)) return true;
        }

        return ExtraHas?.Invoke(hash) ?? false;
    }

    /// <summary>Gets the content of a file by its hash; a kept upload counts as used again.</summary>
    /// <param name="hash">The SHA-256.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>True when the server has it.</returns>
    public bool TryGet(string hash, out byte[] data)
    {
        data = [];
        if (!IsHash(hash)) return false;
        if (known.TryGetValue(hash, out string? path))
        {
            try
            {
                data = File.ReadAllBytes(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        lock (sync)
        {
            Expire();
            if (kept.TryGetValue(hash, out Kept? found))
            {
                found.Used = now();
                data = found.Data;
                return true;
            }
        }

        byte[]? shared = ExtraGet?.Invoke(hash);
        if (shared is null) return false;
        data = shared;
        return true;
    }

    /// <summary>Keeps the content of an uploaded file for a while (a demo file is not kept again; a file larger than the whole store is not kept).</summary>
    /// <param name="data">The bytes.</param>
    public void Put(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.LongLength > maxBytes) return;
        string hash = Hash(data);
        if (known.ContainsKey(hash)) return;
        lock (sync)
        {
            Expire();
            if (kept.TryGetValue(hash, out Kept? existing))
            {
                existing.Used = now();
                return;
            }

            kept[hash] = new Kept(data, now());
            total += data.LongLength;
            while (total > maxBytes && kept.Count > 1)
            {
                KeyValuePair<string, Kept> oldest = kept.OrderBy(p => p.Value.Used).First();
                if (oldest.Key == hash) break;
                kept.Remove(oldest.Key);
                total -= oldest.Value.Data.LongLength;
            }
        }
    }

    private void Expire()
    {
        DateTime limit = now() - lifetime;
        foreach (string hash in kept.Where(p => p.Value.Used < limit).Select(p => p.Key).ToList())
        {
            total -= kept[hash].Data.LongLength;
            kept.Remove(hash);
        }
    }

    private sealed class Kept(byte[] data, DateTime used)
    {
        public byte[] Data { get; } = data;

        public DateTime Used { get; set; } = used;
    }
}
