using System.Runtime;

namespace Ample.Server;

/// <summary>
/// Gives memory back after heavy work. Renders and the log of a long AI task allocate large arrays (large object heap), which .NET only collects in a full collection and does not
/// compact on its own, so the server process keeps growing. A forced, compacting collection runs after a measuring call finishes and, while the managed heap is large, once a minute.
/// </summary>
internal static class MemoryRelief
{
    private const long ThresholdBytes = 512L * 1024 * 1024;
    private static readonly Timer Watch = new(_ => CollectWhenLarge(1024L * 1024 * 1024), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    private static long last;

    /// <summary>Collects (blocking, compacting the large object heap once) when the managed heap is large and the last collection was a while ago.</summary>
    public static void CollectWhenLarge(long threshold = ThresholdBytes)
    {
        _ = Watch;
        if (GC.GetTotalMemory(forceFullCollection: false) < threshold) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref last) < 15_000) return;
        Interlocked.Exchange(ref last, now);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
