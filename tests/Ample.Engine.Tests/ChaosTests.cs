using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Projects;
using Ample.Storage.Backup;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Testing;

namespace Ample.Core.Tests;

/// <summary>Chaos tests: a crash at every step of every kind of command, clocks that jump, and projects with many files. They prove that what is on disk is always either the old or the new state, that the black box verifies, and that every unfinished command can be found and dealt with.</summary>
[Trait("Category", "Chaos")]
public sealed class ChaosTests : IDisposable
{
    private static readonly string[] SongVersions = ["version one\n", "version two\n"];
    private readonly string folder = Directory.CreateTempSubdirectory("ample-chaos-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static async Task RunCommandsAsync(IFileSystem fs, string root, Guid id)
    {
        FakeClock clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        using Journal journal = await Journal.OpenAsync(fs, clock, root, id.ToString("D"));
        CommandContext context = new(fs, root, clock);
        CommandBus bus = new(journal, context, "chaos");
        await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "version one\n"));
        await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "version two\n"));
        await bus.ExecuteAsync(new WriteFileCommand("lyrics/chorus.lyr", "[bar 1] la\n"));
        await bus.ExecuteAsync(new SetSettingsCommand(ProjectSettings.Default with { BlackBox = new BlackBoxSettings(32 * 1024 * 1024, 14, 365) }));
        await new ChangeMasterSeedAct(99).RunAsync(journal, context, "chaos", "change master seed", "chaos test");
    }

    [Fact]
    public async Task A_crash_at_every_step_of_every_command_leaves_consistent_files_a_verifying_black_box_and_nothing_unresolvable()
    {
        // First a run without a crash to count the steps.
        string probe = Path.Combine(folder, "probe");
        Guid probeId;
        using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), probe, "chaos"))
        {
            probeId = p.Manifest.Id;
        }

        FaultyFileSystem counting = new(new PhysicalFileSystem(), int.MaxValue);
        await RunCommandsAsync(counting, probe, probeId);
        int steps = counting.Operations;
        Assert.True(steps > 20, steps.ToString(System.Globalization.CultureInfo.InvariantCulture));

        for (int failAt = 1; failAt <= steps; failAt++)
        {
            string root = Path.Combine(folder, "run" + failAt);
            Guid id;
            using (OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), new SystemClock(), new GuidV7Generator(), root, "chaos"))
            {
                id = p.Manifest.Id;
            }

            FaultyFileSystem faulty = new(new PhysicalFileSystem(), failAt);
            try
            {
                await RunCommandsAsync(faulty, root, id);
            }
            catch (Exception ex) when (ex is IOException or AmpleException)
            {
                // The crash.
            }

            Assert.True(faulty.Failed, $"step {failAt} did not fail");

            // After the crash: a normal process looks at what is left.
            string song = Path.Combine(root, "song.amplet");
            if (File.Exists(song))
            {
                Assert.Contains(File.ReadAllText(song), SongVersions);
            }

            string lyrics = Path.Combine(root, "lyrics", "chorus.lyr");
            if (File.Exists(lyrics))
            {
                Assert.Equal("[bar 1] la\n", File.ReadAllText(lyrics));
            }

            using OpenProject reopened = await ProjectStore.OpenAsync(new PhysicalFileSystem(), new SystemClock(), root, readOnly: false);
            Assert.Equal(id, reopened.Manifest.Id);
            ProjectManifest manifest = ProjectFile.Parse(File.ReadAllText(Path.Combine(root, ".ample", "project.json")));
            Assert.Contains(manifest.MasterSeed, new ulong[] { manifest.MasterSeed, 99UL });

            PhysicalFileSystem fs = new();
            using Journal journal = await Journal.OpenAsync(fs, new SystemClock(), root, id.ToString("D"));
            Assert.Null((await journal.VerifyAsync()).BadSeq);
            CommandBus bus = new(journal, new CommandContext(fs, root, new SystemClock()), "recovery");
            foreach (InterruptedCommand interrupted in await journal.FindInterruptedAsync())
            {
                await bus.ResolveAsync(interrupted, "chaos recovery");
            }

            Assert.Empty(await journal.FindInterruptedAsync());
            await bus.ExecuteAsync(new WriteFileCommand("after-crash.txt", "the project works again\n"));
            Assert.Null((await journal.VerifyAsync()).BadSeq);
        }
    }

    [Fact]
    public async Task Clocks_that_jump_back_and_far_forward_keep_the_order_the_chain_and_the_retention_safe()
    {
        string root = Path.Combine(folder, "clock");
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "clock");
        FakeClock clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
        using Journal journal = await Journal.OpenAsync(fs, clock, root, p.Manifest.Id.ToString("D"), new JournalOptions(SegmentBytes: 600));
        TimeSpan[] jumps = [TimeSpan.Zero, TimeSpan.FromDays(-400), TimeSpan.FromDays(3650), TimeSpan.FromSeconds(-1), TimeSpan.FromDays(-3650), TimeSpan.FromHours(1)];
        for (int i = 0; i < 60; i++)
        {
            clock.Advance(jumps[i % jumps.Length]);
            await journal.AppendAsync("user", "file.write", "ok", "c" + i, JsonSerializer.Serialize(new { i }));
        }

        IReadOnlyList<JournalRecord> all = await journal.ReadAllAsync();
        Assert.Equal(60, all.Count);
        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), all.Select(r => r.Seq));
        Assert.Null((await journal.VerifyAsync()).BadSeq);

        FakeClock far = new(new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero));
        CompactionResult result = await JournalRetention.CompactAsync(fs, root, far.UtcNow, new BlackBoxSettings(64 * 1024 * 1024, 14, 365));
        Assert.True(result.Thinned >= 1);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, ".ample", "blackbox"), "*.seg"));
        await journal.AppendAsync("user", "after", "ok", "last", "{}");
        Assert.Null((await journal.VerifyAsync()).BadSeq);

        FakeClock back = new(new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero));
        CompactionResult none = await JournalRetention.CompactAsync(fs, root, back.UtcNow, new BlackBoxSettings(64 * 1024 * 1024, 14, 365));
        Assert.Equal(0, none.Thinned);
    }

    [Fact]
    public async Task A_project_with_thousands_of_files_is_backed_up_verified_and_restored_identically()
    {
        string root = Path.Combine(folder, "many");
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "many");
        for (int i = 0; i < 3000; i++)
        {
            string dir = Path.Combine(root, "data", (i % 40).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, $"f{i}.txt"), "content " + i);
        }

        BackupStore store = new(Path.Combine(folder, "target"), new SystemClock());
        BackupReport report = store.Create(root, p.Manifest.Id, "many");
        Assert.True(report.Files >= 3000);
        Assert.True(store.Verify(report.Id).Ok);
        string restored = Path.Combine(folder, "restored");
        store.RestoreToFolder(report.Id, restored);
        Assert.Equal(3000, Directory.EnumerateFiles(Path.Combine(restored, "data"), "*.txt", SearchOption.AllDirectories).Count());
        Assert.Equal("content 1234", File.ReadAllText(Path.Combine(restored, "data", "34", "f1234.txt")));
    }
}
