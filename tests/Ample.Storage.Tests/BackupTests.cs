using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Backup;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Testing;
using Microsoft.Data.Sqlite;

namespace Ample.Core.Tests;

[Trait("Category", "Backup")]
public sealed class BackupTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-backup-").FullName;
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private string Path2(params string[] parts) => Path.Combine([folder, .. parts]);

    private static (Guid Id, string Name) Identity(string root)
    {
        ProjectManifest m = ProjectFile.Parse(File.ReadAllText(Path.Combine(root, ".ample", "project.json")));
        return (m.Id, m.Name);
    }

    private static void Put(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static Dictionary<string, string> Tree(string root, bool takes = true) => BackupStore.ProjectFiles(root, takes).Where(f => !f.Rel.EndsWith(".db", StringComparison.Ordinal)).ToDictionary(f => f.Rel, f => BackupStore.HashFile(f.Full));

    private async Task<string> ProjectWithFilesAsync(string name = "proj")
    {
        string root = Path2(name);
        using OpenProject p = await ProjectStore.CreateAsync(new PhysicalFileSystem(), clock, new GuidV7Generator(), root, name);
        Put(root, "song.amplet", "composition \"x\" { ample 1  tempo 92  master-seed 1 }\n");
        Put(root, "lyrics/chorus.lyr", "la la la");
        Put(root, ".ample/data/ab/blob1", "audio bytes one");
        Put(root, ".ample/data/cd/blob2", "audio bytes two");
        Put(root, ".ample/vault/key", "TOP SECRET");
        Put(root, "notes.secret", "also secret");
        Put(root, ".ample/tmp/scratch", "scratch");
        Put(root, ".ample/locks/writer", "lock");
        return root;
    }

    [Fact]
    public async Task Destroy_and_restore_gives_an_identical_project_without_secrets_locks_or_temp_files()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        BackupReport report = store.Create(root, id, name);
        Assert.True(store.Verify(report.Id).Ok);
        Dictionary<string, string> before = Tree(root);
        Assert.DoesNotContain(before.Keys, k => k.Contains("vault", StringComparison.Ordinal) || k.EndsWith(".secret", StringComparison.Ordinal) || k.Contains("/tmp/", StringComparison.Ordinal) || k.Contains("/locks/", StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
        string restored = Path2("restored");
        store.RestoreToFolder(report.Id, restored);
        Dictionary<string, string> after = Tree(restored);
        Assert.Equal(before, after);
        Assert.Equal(id, Identity(restored).Id);
        Assert.False(File.Exists(Path.Combine(restored, ".ample", "vault", "key")));
        Assert.False(File.Exists(Path.Combine(restored, "notes.secret")));
        Assert.Equal("AMP5301", Assert.Throws<AmpleException>(() => store.RestoreToFolder(report.Id, restored)).DiagnosticCode);
    }

    [Fact]
    public async Task The_database_is_copied_consistently_while_the_project_is_open()
    {
        using HistoryRig rig = await HistoryRig.CreateAsync(("song.amplet", "one"));
        await rig.History.ForkAsync("idea");
        (Guid id, string name) = Identity(rig.Root);
        BackupStore store = new(Path2("target"), clock);
        BackupReport report = store.Create(rig.Root, id, name);
        string restored = Path2("restored");
        store.RestoreToFolder(report.Id, restored);
        using SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(restored, ".ample", "history.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT group_concat(name) FROM h_lines ORDER BY name";
        Assert.Contains("idea", (string)cmd.ExecuteScalar()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_backup_stores_only_what_changed_and_the_takes_can_be_left_out()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        BackupReport first = store.Create(root, id, name);
        clock.Advance(TimeSpan.FromHours(1));
        BackupReport second = store.Create(root, id, name);
        Assert.Equal(0, second.NewObjects);
        Put(root, "lyrics/chorus.lyr", "la la la, new words");
        clock.Advance(TimeSpan.FromHours(1));
        BackupReport third = store.Create(root, id, name);
        Assert.Equal(1, third.NewObjects);
        Assert.True(third.NewBytes < first.NewBytes);
        BackupReport light = store.Create(root, id, name, includeTakes: false);
        Assert.DoesNotContain(store.Get(light.Id).Entries, e => e.Path.StartsWith(".ample/data/", StringComparison.Ordinal));
        Assert.False(store.Get(light.Id).IncludesTakes);
    }

    [Fact]
    public async Task Verify_finds_a_damaged_and_a_missing_object_and_a_restore_refuses_to_write_damaged_data()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        BackupReport r = store.Create(root, id, name);
        BackupManifest m = store.Get(r.Id);
        string lyrics = m.Entries.Single(e => e.Path == "lyrics/chorus.lyr").Hash;
        string blob = m.Entries.Single(e => e.Path.EndsWith("blob1", StringComparison.Ordinal)).Hash;
        File.WriteAllText(store.ObjectPath(lyrics), "tampered");
        File.Delete(store.ObjectPath(blob));
        BackupVerifyReport v = store.Verify(r.Id);
        Assert.False(v.Ok);
        Assert.Equal(["lyrics/chorus.lyr"], v.Corrupt);
        Assert.Contains(v.Missing, p => p.EndsWith("blob1", StringComparison.Ordinal));
        Assert.Null(store.LastGood());
        string dest = Path2("restored");
        Assert.Equal("AMP5301", Assert.Throws<AmpleException>(() => store.RestoreToFolder(r.Id, dest)).DiagnosticCode);
    }

    [Fact]
    public async Task A_backup_that_stopped_before_its_manifest_is_finished_by_the_next_run()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        BackupReport r = store.Create(root, id, name);
        BackupManifest m = store.Get(r.Id);
        File.Delete(Path2("target", "backups", r.Id + ".json"));
        foreach (string hash in m.Entries.Select(e => e.Hash).Distinct().Take(2))
        {
            File.Delete(store.ObjectPath(hash));
        }

        File.WriteAllText(store.ObjectPath(m.Entries[0].Hash) + ".part", "half");
        Assert.Empty(store.List());
        BackupReport again = store.Create(root, id, name);
        Assert.InRange(again.NewObjects, 1, 2);
        Assert.True(store.Verify(again.Id).Ok);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task A_target_without_room_is_refused_before_anything_is_written()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock, _ => 10);
        Assert.Equal("AMP5300", Assert.Throws<AmpleException>(() => store.Create(root, id, name)).DiagnosticCode);
        Assert.Empty(Directory.GetFiles(Path2("target", "objects"), "*", SearchOption.AllDirectories));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Prune_is_a_dangerous_act_with_a_dry_run_and_never_removes_the_last_good_backup()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        List<string> ids = [];
        for (int i = 0; i < 4; i++)
        {
            Put(root, "lyrics/chorus.lyr", "version " + i);
            ids.Add(store.Create(root, id, name).Id);
            clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.True(store.Verify(ids[1]).Ok);
        PrunePlan plan = store.PlanPrune(new PruneRules(1, 0));
        Assert.Equal([ids[0], ids[2]], plan.Remove.Order().ToArray());
        Assert.DoesNotContain(ids[1], plan.Remove);
        Assert.Contains(ids[3], plan.Keep);
        Assert.Contains(ids[1], plan.Keep);
        Assert.Equal(2, plan.Remove.Count);
        Assert.True(plan.ObjectsFreed >= 2);
        Assert.Equal(4, store.List().Count);

        using HistoryRig h = await HistoryRig.CreateAsync(("song.amplet", "x"));
        PruneBackupsAct act = new(store, new PruneRules(1, 0));
        DryRunResult dry = await act.DryRunAsync(h.Context, CancellationToken.None);
        Assert.Contains("2 backup(s) are removed", dry.Impact[0], StringComparison.Ordinal);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(h.Journal, h.Context, "user", "yes", "x"))).DiagnosticCode);
        Assert.Equal(4, store.List().Count);
        await act.RunAsync(h.Journal, h.Context, "user", act.Acceptance, "old backups");
        Assert.Equal(2, store.List().Count);
        Assert.True(store.Verify(ids[1]).Ok);
        Assert.True(store.Verify(ids[3]).Ok);

        PrunePlan zero = store.PlanPrune(new PruneRules(0, 0));
        Assert.Equal([ids[3]], zero.Keep);
        Assert.Equal([ids[1]], zero.Remove);
    }

    [Fact]
    public async Task Export_and_import_round_trip_check_every_hash_and_can_give_a_new_id()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        string file = Path2("song.ample");
        BackupManifest m = Archive.Export(root, id, name, file, includeTakes: true, clock);
        Assert.True(File.Exists(file));
        Assert.Equal(m.Entries.Count, Archive.ReadManifest(file).Entries.Count);
        using (ZipArchive zip = ZipFile.OpenRead(file))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("files/song.amplet"));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("vault", StringComparison.Ordinal) || e.FullName.EndsWith(".secret", StringComparison.Ordinal));
        }

        string same = Path2("imported-same");
        Archive.Import(file, same, newId: false);
        Assert.Equal(Tree(root), Tree(same));
        Assert.Equal(id, Identity(same).Id);
        string fresh = Path2("imported-new");
        Archive.Import(file, fresh, newId: true);
        Assert.NotEqual(id, Identity(fresh).Id);
        Assert.Equal(name, Identity(fresh).Name);
        Assert.Equal("AMP5301", Assert.Throws<AmpleException>(() => Archive.Import(file, same, false)).DiagnosticCode);

        string light = Path2("light.ample");
        Archive.Export(root, id, name, light, includeTakes: false, clock);
        Assert.DoesNotContain(Archive.ReadManifest(light).Entries, e => e.Path.StartsWith(".ample/data/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_changed_or_foreign_archive_is_refused_and_leaves_nothing_behind()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        string file = Path2("song.ample");
        Archive.Export(root, id, name, file, true, clock);
        using (ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = zip.GetEntry("files/lyrics/chorus.lyr")!;
            entry.Delete();
            ZipArchiveEntry replacement = zip.CreateEntry("files/lyrics/chorus.lyr");
            using StreamWriter w = new(replacement.Open());
            w.Write("changed after export");
        }

        string dest = Path2("never");
        Assert.Equal("AMP5302", Assert.Throws<AmpleException>(() => Archive.Import(file, dest, false)).DiagnosticCode);
        Assert.False(Directory.Exists(dest));
        Assert.Empty(Directory.GetDirectories(folder, "never*"));

        string notAmple = Path2("other.zip");
        using (ZipArchive zip = ZipFile.Open(notAmple, ZipArchiveMode.Create))
        {
            zip.CreateEntry("hello.txt");
        }

        Assert.Equal("AMP5302", Assert.Throws<AmpleException>(() => Archive.ReadManifest(notAmple)).DiagnosticCode);

        string evil = Path2("evil.ample");
        using (ZipArchive zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using StreamWriter w = new(zip.CreateEntry("manifest.json").Open());
            w.Write(JsonSerializer.Serialize(new { format = 1, projectId = Guid.NewGuid(), projectName = "x", created = "2026-10-03T00:00:00+00:00", includesTakes = true, entries = new[] { new { path = "../escape.txt", hash = "sha256:00", size = 1 } } }));
        }

        Assert.ThrowsAny<AmpleException>(() => Archive.Import(evil, Path2("evil-out"), false));
        Assert.False(File.Exists(Path2("escape.txt")));
    }

    [Fact]
    public async Task An_in_place_restore_is_a_dangerous_act_that_replaces_adds_and_moves_extra_files_to_a_trash_folder()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        BackupStore store = new(Path2("target"), clock);
        string backup = store.Create(root, id, name).Id;
        Put(root, "song.amplet", "ruined");
        File.Delete(Path.Combine(root, "lyrics", "chorus.lyr"));
        Put(root, "extra.txt", "made after the backup");

        using HistoryRig h = await HistoryRig.CreateAsync(("song.amplet", "x"));
        RestoreInPlaceAct act = new(store, backup, root);
        DryRunResult dry = await act.DryRunAsync(h.Context, CancellationToken.None);
        Assert.Contains("1 file(s) are replaced, 1 added", dry.Impact[0], StringComparison.Ordinal);
        Assert.Contains("1 file(s) that are not in the backup", dry.Impact[1], StringComparison.Ordinal);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.RunAsync(h.Journal, h.Context, "user", "restore", "x"))).DiagnosticCode);
        Assert.Equal("ruined", File.ReadAllText(Path.Combine(root, "song.amplet")));
        await act.RunAsync(h.Journal, h.Context, "user", act.Acceptance, "the song was ruined");
        Assert.Equal("composition \"x\" { ample 1  tempo 92  master-seed 1 }\n", File.ReadAllText(Path.Combine(root, "song.amplet")));
        Assert.Equal("la la la", File.ReadAllText(Path.Combine(root, "lyrics", "chorus.lyr")));
        Assert.False(File.Exists(Path.Combine(root, "extra.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, ".ample", "tmp"), "extra.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_opt_in_hook_makes_a_backup_before_a_dangerous_act_and_a_failing_backup_stops_the_act()
    {
        using HistoryRig h = await HistoryRig.CreateAsync(("song.amplet", "x"));
        await h.History.ForkAsync("idea");
        await h.History.ForkAsync("other");
        (Guid id, string name) = Identity(h.Root);
        BackupStore store = new(Path2("target"), clock);
        int calls = 0;
        bool fail = false;
        DangerousAct.BeforeExecute = (context, ct) =>
        {
            if (context.ProjectRoot != h.Root)
            {
                return ValueTask.CompletedTask;
            }

            calls++;
            if (fail)
            {
                throw new AmpleException("AMP5305", "The backup before the act failed, so the act did not run.");
            }

            store.Create(h.Root, id, name, cancellationToken: ct);
            return ValueTask.CompletedTask;
        };
        try
        {
            DropLineAct first = new(h.History, "idea");
            await first.RunAsync(h.Journal, h.Context, "user", first.Acceptance, "test");
            Assert.Equal(1, calls);
            Assert.Single(store.List());
            Assert.True(h.History.LineOf("idea").Archived);

            fail = true;
            DropLineAct second = new(h.History, "other");
            Assert.Equal("AMP5305", (await Assert.ThrowsAsync<AmpleException>(async () => await second.RunAsync(h.Journal, h.Context, "user", second.Acceptance, "test"))).DiagnosticCode);
            Assert.False(h.History.LineOf("other").Archived);
        }
        finally
        {
            DangerousAct.BeforeExecute = null;
        }
    }

    [Fact]
    public async Task The_project_remembers_its_last_backup_for_the_health_page_and_the_act_suggestion()
    {
        string root = await ProjectWithFilesAsync();
        Assert.Null(BackupPolicy.Read(root));
        Assert.Contains("no backup of this project yet", BackupPolicy.Suggestion(root, clock), StringComparison.Ordinal);
        BackupPolicy.Remember(root, Path2("target"), "20261003-090000-abc123", clock.UtcNow);
        clock.Advance(TimeSpan.FromDays(3.5));
        BackupStatus s = BackupPolicy.Read(root, clock)!;
        Assert.Equal(3.5, s.AgeDays, 2);
        Assert.False(s.Auto);
        Assert.Contains("3 day(s) old", BackupPolicy.Suggestion(root, clock), StringComparison.Ordinal);
        BackupPolicy.SetAuto(root, Path2("target"), true);
        Assert.True(BackupPolicy.Read(root, clock)!.Auto);
        Assert.Equal("20261003-090000-abc123", BackupPolicy.Read(root, clock)!.Id);
    }

    [Fact]
    public async Task Throughput_is_measured_and_printed()
    {
        string root = await ProjectWithFilesAsync();
        (Guid id, string name) = Identity(root);
        byte[] big = new byte[40 * 1024 * 1024];
        new System.Random(7).NextBytes(big);
        Directory.CreateDirectory(Path.Combine(root, ".ample", "data", "ef"));
        File.WriteAllBytes(Path.Combine(root, ".ample", "data", "ef", "big.wav"), big);
        BackupStore store = new(Path2("target"), clock);
        Stopwatch sw = Stopwatch.StartNew();
        BackupReport r = store.Create(root, id, name);
        long createMs = sw.ElapsedMilliseconds;
        sw.Restart();
        Assert.True(store.Verify(r.Id).Ok);
        long verifyMs = sw.ElapsedMilliseconds;
        sw.Restart();
        store.RestoreToFolder(r.Id, Path2("restored"));
        long restoreMs = sw.ElapsedMilliseconds;
        double mb = r.TotalBytes / 1048576.0;
        Console.WriteLine($"backup throughput ({mb:F0} MB, {r.Files} files): create {createMs} ms ({mb / Math.Max(1, createMs) * 1000:F0} MB/s), verify {verifyMs} ms, restore {restoreMs} ms");
        Assert.True(r.NewBytes >= big.Length);
    }
}
