using System.Collections.Concurrent;

namespace Ample.Server.Lite;

/// <summary>
/// The audio of finished Lite builds, kept in memory for a few minutes so the page can fetch it (and fetch it again to play or to download), never on disk. The encoded versions (MP3, FLAC)
/// of a build are kept next to it and go with it. The cache is small on purpose: a count, a time and a total size; when the size is passed the oldest builds go first.
/// </summary>
public sealed class LiteOutputs
{
    private sealed class Entry(byte[] wav, DateTime expires)
    {
        public byte[] Wav { get; } = wav;

        public DateTime Expires { get; } = expires;

        public ConcurrentDictionary<string, byte[]> Variants { get; } = new(StringComparer.Ordinal);

        public long Bytes => Wav.LongLength + Variants.Values.Sum(v => v.LongLength);
    }

    private readonly ConcurrentDictionary<string, Entry> kept = new(StringComparer.Ordinal);

    /// <summary>Keeps a result.</summary>
    /// <param name="wav">The WAV bytes.</param>
    /// <param name="limits">The limits (how long, how many, how large).</param>
    /// <returns>The id to fetch it with.</returns>
    public string Put(byte[] wav, LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(limits);
        Sweep();
        while (kept.Count >= limits.MaxKeptOutputs && Oldest() is { } oldest) kept.TryRemove(oldest, out _);
        string id = Guid.CreateVersion7().ToString("N");
        kept[id] = new Entry(wav, DateTime.UtcNow.AddMinutes(limits.OutputMinutes));
        Trim(limits.MaxKeptBytes, id);
        return id;
    }

    /// <summary>Looks a result up.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The bytes, or null when it is unknown or has expired.</returns>
    public byte[]? Get(string id)
    {
        Sweep();
        return kept.TryGetValue(id, out Entry? found) ? found.Wav : null;
    }

    /// <summary>Looks up an encoded version of a result.</summary>
    /// <param name="id">The id.</param>
    /// <param name="format">mp3 or flac.</param>
    /// <returns>The bytes, or null when it was not made (or the build is gone).</returns>
    public byte[]? GetVariant(string id, string format)
    {
        Sweep();
        return kept.TryGetValue(id, out Entry? found) && found.Variants.TryGetValue(format, out byte[]? data) ? data : null;
    }

    /// <summary>Keeps an encoded version next to its build.</summary>
    /// <param name="id">The id of the build.</param>
    /// <param name="format">mp3 or flac.</param>
    /// <param name="data">The encoded bytes.</param>
    /// <param name="limits">The limits (the total size).</param>
    public void PutVariant(string id, string format, byte[] data, LiteLimits limits)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(limits);
        if (kept.TryGetValue(id, out Entry? found))
        {
            found.Variants[format] = data;
            Trim(limits.MaxKeptBytes, id);
        }
    }

    /// <summary>Drops a result.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(string id) => kept.TryRemove(id, out _);

    /// <summary>Gets how many results are kept.</summary>
    public int Count => kept.Count;

    /// <summary>Gets how many bytes are kept (builds and their encoded versions).</summary>
    public long Bytes => kept.Values.Sum(e => e.Bytes);

    private string? Oldest() => kept.OrderBy(k => k.Value.Expires).Select(k => k.Key).FirstOrDefault();

    // the oldest builds go first until the total fits; the one that was just stored (keep) stays unless it is the only one
    private void Trim(long maxBytes, string keep)
    {
        while (Bytes > maxBytes && kept.OrderBy(k => k.Value.Expires).Select(k => k.Key).FirstOrDefault(k => k != keep) is { } oldest) kept.TryRemove(oldest, out _);
    }

    private void Sweep()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, Entry> item in kept.Where(k => k.Value.Expires <= now)) kept.TryRemove(item.Key, out _);
    }
}
