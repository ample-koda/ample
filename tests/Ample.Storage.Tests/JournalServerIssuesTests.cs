using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Testing;

namespace Ample.Core.Tests;

[Trait("Category", "Blackbox")]
public sealed class JournalServerIssuesTests
{
    private static readonly string Root = Path.GetFullPath("/proj");
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static string Artifact(string hash) => Path.Combine(Root, ".ample", "blackbox", "artifacts", hash[..2], hash + ".txt");

    private static string Hash(char c) => new(c, 64);

    private static async Task Put(InMemoryFileSystem fs, char hash, int bytes)
    {
        string file = Artifact(Hash(hash));
        fs.CreateDirectory(Path.GetDirectoryName(file)!.Replace('\\', '/'));
        await fs.WriteAllBytesDurableAsync(file, new byte[bytes], CancellationToken.None);
    }

    private static async Task<Journal> Open(IFileSystem fs, JournalOptions options)
        => await Journal.OpenAsync(fs, new FakeClock(T0), Root, "pid-1", options);

    private static async Task Task_(Journal journal, string correlation, string key, char hash)
        => await journal.AppendAsync("assistant", "assistant.task", "done", correlation, JsonSerializer.Serialize(new Dictionary<string, object> { [key] = new { sha256 = Hash(hash) } }));

    [Fact]
    public async Task Compaction_removes_the_artifacts_that_only_thinned_segments_referred_to()
    {
        InMemoryFileSystem fs = new();
        using (Journal journal = await Open(fs, new JournalOptions(SegmentBytes: 1)))
        {
            await Task_(journal, "t1", "after", 'a');
            await Task_(journal, "t2", "after", 'b');
            await Task_(journal, "t3", "after", 'c');
        }

        foreach (char c in "abc")
        {
            await Put(fs, c, 3);
        }

        await JournalRetention.CompactAsync(fs, Root, T0.AddDays(120), new BlackBoxSettings(1, 90, 365));

        Assert.False(fs.FileExists(Artifact(Hash('a'))), "its segment was thinned: nothing refers to it");
        Assert.False(fs.FileExists(Artifact(Hash('b'))));
        Assert.True(fs.FileExists(Artifact(Hash('c'))), "the newest segment is kept, so is its artifact");
    }

    [Fact]
    public async Task An_artifact_that_a_kept_segment_still_names_stays()
    {
        InMemoryFileSystem fs = new();
        using (Journal journal = await Open(fs, new JournalOptions(SegmentBytes: 1)))
        {
            await Task_(journal, "t1", "after", 'a');
            await Task_(journal, "t2", "after", 'b');
            await Task_(journal, "t3", "before", 'a');
        }

        await Put(fs, 'a', 1);
        await Put(fs, 'b', 2);

        await JournalRetention.CompactAsync(fs, Root, T0.AddDays(120), new BlackBoxSettings(1, 90, 365));

        Assert.True(fs.FileExists(Artifact(Hash('a'))), "the kept record still names it");
        Assert.False(fs.FileExists(Artifact(Hash('b'))));
    }

    [Fact]
    public async Task An_artifact_that_no_record_names_is_left_alone()
    {
        InMemoryFileSystem fs = new();
        using (Journal journal = await Open(fs, new JournalOptions(SegmentBytes: 1)))
        {
            await journal.AppendAsync("ui", "test.kind", "ok", "c1", "{}");
            await journal.AppendAsync("ui", "test.kind", "ok", "c2", "{}");
            await journal.AppendAsync("ui", "test.kind", "ok", "c3", "{}");
        }

        await Put(fs, 'd', 1);   // written, its record is not there yet

        await JournalRetention.CompactAsync(fs, Root, T0.AddDays(120), new BlackBoxSettings(1, 90, 365));

        Assert.True(fs.FileExists(Artifact(Hash('d'))));
    }

    [Fact]
    public async Task The_compaction_preview_counts_the_artifacts_it_will_free()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await Open(fs, new JournalOptions(SegmentBytes: 1));
        await Task_(journal, "t1", "after", 'a');
        await Task_(journal, "t2", "after", 'b');
        await Task_(journal, "t3", "after", 'c');
        foreach (char c in "abc")
        {
            await Put(fs, c, 500);
        }

        BlackBoxSettings settings = new(1, 90, 365);
        CompactionPreview preview = await JournalRetention.PreviewAsync(fs, Root, T0.AddDays(120), settings, CancellationToken.None);
        await JournalRetention.CompactAsync(fs, Root, T0.AddDays(120), settings);
        long after = await JournalRetention.StorageBytesAsync(fs, Root, CancellationToken.None);

        Assert.Equal(preview.BytesAfter, after);
    }

    private static async Task<Journal> SixRecords(InMemoryFileSystem fs)
    {
        Journal journal = await Open(fs, new JournalOptions(SegmentBytes: 1));
        for (int i = 0; i < 6; i++)
        {
            await journal.AppendAsync("ui", "test.kind", "ok", "c" + i, "{}");
        }

        return journal;
    }

    [Fact]
    public async Task A_page_skips_segments_that_start_after_the_newest_wanted_record()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await SixRecords(fs);

        IReadOnlyList<JournalRecord> page = await journal.ReadPageAsync(3, long.MaxValue, _ => true, 2, CancellationToken.None);

        Assert.Equal([3L, 2L], page.Select(record => record.Seq).ToArray());
    }

    [Fact]
    public async Task A_page_before_a_cursor_continues_where_the_last_one_ended()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await SixRecords(fs);

        IReadOnlyList<JournalRecord> first = await journal.ReadPageAsync(long.MaxValue, long.MaxValue, _ => true, 2, CancellationToken.None);
        IReadOnlyList<JournalRecord> second = await journal.ReadPageAsync(long.MaxValue, first[^1].Seq, _ => true, 2, CancellationToken.None);
        IReadOnlyList<JournalRecord> last = await journal.ReadPageAsync(long.MaxValue, second[^1].Seq, _ => true, 5, CancellationToken.None);

        Assert.Equal([6L, 5L], first.Select(record => record.Seq).ToArray());
        Assert.Equal([4L, 3L], second.Select(record => record.Seq).ToArray());
        Assert.Equal([2L, 1L], last.Select(record => record.Seq).ToArray());
    }

    [Fact]
    public async Task A_page_search_lets_a_writer_in_while_it_runs()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await SixRecords(fs);

        int seen = 0;
        Task<IReadOnlyList<JournalRecord>> search = journal.ReadPageAsync(long.MaxValue, long.MaxValue, _ =>
        {
            seen++;
            return false;   // nothing matches: every segment is read
        }, 5, CancellationToken.None).AsTask();
        Task<JournalRecord> write = journal.AppendAsync("ui", "test.kind", "ok", "later", "{}").AsTask();

        await Task.WhenAll(search, write);

        Assert.Equal(7, (await write).Seq);
        Assert.InRange(seen, 6, 7);   // the records that were there when it began, and perhaps the new one
    }
}
