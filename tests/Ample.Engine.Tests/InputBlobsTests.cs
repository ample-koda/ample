using Ample.Engine.Engine;

namespace Ample.Core.Tests;

[Trait("Category", "Engine")]
public sealed class InputBlobsTests
{
    [Fact]
    public async Task Three_blocks_that_read_the_same_take_at_once_load_it_once()
    {
        InputBlobs blobs = new(1024);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int opened = 0;
        async Task<byte[]> Load()
        {
            Interlocked.Increment(ref opened);
            await release.Task;
            return [1, 2, 3];
        }

        Task<byte[]>[] readers = [blobs.GetAsync("take-a", Load), blobs.GetAsync("take-a", Load), blobs.GetAsync("take-a", Load)];
        release.SetResult();
        byte[][] results = await Task.WhenAll(readers);

        Assert.Equal(1, opened);
        Assert.All(results, bytes => Assert.Equal([1, 2, 3], bytes));
    }

    [Fact]
    public async Task Every_reader_gets_its_own_copy_so_one_renderer_cannot_change_what_another_reads()
    {
        InputBlobs blobs = new(1024);
        byte[] first = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([1, 2, 3]));
        first[0] = 99;

        byte[] second = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([7, 7, 7]));

        Assert.Equal([1, 2, 3], second);   // served from the bytes that were read, unchanged
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Different_takes_are_loaded_separately()
    {
        InputBlobs blobs = new(1024);

        byte[] a = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([1]));
        byte[] b = await blobs.GetAsync("take-b", () => Task.FromResult<byte[]>([2]));

        Assert.Equal([1], a);
        Assert.Equal([2], b);
        Assert.Equal(2, blobs.LoadsMade);
    }

    [Fact]
    public async Task A_take_over_the_budget_is_not_kept_but_the_readers_that_waited_still_get_it()
    {
        InputBlobs blobs = new(4);

        byte[] big = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([1, 2, 3, 4, 5, 6]));
        byte[] again = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([1, 2, 3, 4, 5, 6]));

        Assert.Equal(6, big.Length);
        Assert.Equal(6, again.Length);
        Assert.Equal(2, blobs.LoadsMade);   // too big to keep: read again
    }

    [Fact]
    public async Task A_failed_load_is_not_kept()
    {
        InputBlobs blobs = new(1024);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await blobs.GetAsync("take-a", () => throw new InvalidOperationException("disk")));
        byte[] ok = await blobs.GetAsync("take-a", () => Task.FromResult<byte[]>([5]));

        Assert.Equal([5], ok);
    }
}
