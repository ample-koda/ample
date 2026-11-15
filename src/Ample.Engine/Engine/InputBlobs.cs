using System.Collections.Concurrent;

namespace Ample.Engine.Engine;

/// <summary>
/// The bytes of the takes that blocks of one build read as input, kept for a short while so that several blocks that read the same take at the same time
/// (one stem into three effects) open and expand it once (issue 295). A take is identified by its data id, which is its content: nothing can be stale.
/// Loads that run at the same time are coalesced and the bytes kept are bounded. The readers share the one array: a renderer only reads its inputs (checked by a test that hashes them before and after a render), so no copy is made (issue 295).
/// </summary>
internal sealed class InputBlobs(long budgetBytes)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> loads = new(StringComparer.Ordinal);
    private long held;

    /// <summary>How many loads were really made (the rest were served from the bytes already read).</summary>
    public int LoadsMade { get; private set; }

    /// <summary>The bytes of a take, read with <paramref name="load"/> when no other block is reading or has just read them. The array is shared: read it, never write into it.</summary>
    public async Task<byte[]> GetAsync(string dataId, Func<Task<byte[]>> load)
    {
        ArgumentNullException.ThrowIfNull(dataId);
        ArgumentNullException.ThrowIfNull(load);
        Lazy<Task<byte[]>> entry = loads.GetOrAdd(dataId, _ => new Lazy<Task<byte[]>>(() => Remember(dataId, load), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            loads.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(dataId, entry));   // a failed or cancelled load is not kept
            throw;
        }
    }

    private async Task<byte[]> Remember(string dataId, Func<Task<byte[]>> load)
    {
        LoadsMade++;
        byte[] bytes = await load().ConfigureAwait(false);
        if (Interlocked.Add(ref held, bytes.Length) > budgetBytes)
        {
            // over the budget: the readers that are waiting still get these bytes, a later block reads them again
            loads.TryRemove(dataId, out _);
            Interlocked.Add(ref held, -bytes.Length);
        }

        return bytes;
    }

    /// <summary>Lets go of everything (the build is over).</summary>
    public void Clear()
    {
        loads.Clear();
        Interlocked.Exchange(ref held, 0);
    }
}
