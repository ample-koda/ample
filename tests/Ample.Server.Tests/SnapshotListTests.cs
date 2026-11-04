namespace Ample.Server.Tests;

/// <summary>The lists of an AI task are read by other requests while they are rebuilt: that is a snapshot and never "Collection was modified".</summary>
public sealed class SnapshotListTests
{
    [Fact]
    public void Changing_the_list_while_it_is_enumerated_does_not_throw_and_the_reader_sees_what_it_started_with()
    {
        SnapshotList<int> list = new();
        list.AddRange([1, 2, 3]);
        List<int> seen = [];
        foreach (int item in list)
        {
            seen.Add(item);
            list.Clear();
            list.Add(99);
        }

        Assert.Equal([1, 2, 3], seen);
        Assert.Equal([99], [.. list]);
    }

    [Fact]
    public async Task Replace_puts_the_new_content_in_at_once_and_readers_in_other_threads_never_see_it_half_built()
    {
        SnapshotList<int> list = new();
        list.Replace([1, 2, 3, 4]);
        bool bad = false;
        Task reader = Task.Run(() => { for (int i = 0; i < 20000; i++) { int count = list.Count; if (count is not (4 or 6)) bad = true; foreach (int _ in list) { } } });
        for (int i = 0; i < 20000; i++) list.Replace(i % 2 == 0 ? [1, 2, 3, 4, 5, 6] : [1, 2, 3, 4]);
        await reader;
        Assert.False(bad);
    }
}
