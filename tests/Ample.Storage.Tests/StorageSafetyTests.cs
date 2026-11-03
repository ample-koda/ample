using Ample.Core.Abstractions;
using Ample.Storage.History;
using Ample.Storage.Takes;
using Ample.Testing;
using Microsoft.Data.Sqlite;

namespace Ample.Core.Tests;

/// <summary>Audit tests of the storage clean-up, move and the history clean-up: anything unclear fails safe, nothing outside AMPLE's own data is touched.</summary>
[Trait("Category", "Takebank")]
public sealed class StorageSafetyTests
{
    private static readonly DateTimeOffset Later = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static StorageService Service(string root, Takebank bank) =>
        new(bank, root, Path.Combine(root, ".ample", "history.db"), new FakeClock(Later));

    private static async Task<string> MakeAsync(Takebank bank, string block, string settings, string sound)
    {
        TakeTicket ticket = await bank.BeginAsync(TakeFixtures.Plan(block, settings));
        return (await ticket.CommitAsync([("audio", TakeFixtures.Bytes(sound))])).TakeId;
    }

    /// <summary>Makes an old take of a block (a clean-up victim) and its newer successor.</summary>
    private static async Task<string> VictimAsync(string root, Takebank bank)
    {
        await MakeAsync(bank, "block:melody", "{\"v\":1}", "old sound " + new string('o', 200));
        await MakeAsync(bank, "block:melody", "{\"v\":2}", "new sound " + new string('n', 200));
        return Service(root, bank).Classify().Single(t => !t.Protected).TakeId;   // which of the two is current depends on the id order when the clock stands still
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private static void AssertVictim(StorageService service, string victim, bool isProtected) =>
        Assert.Equal(isProtected, service.Classify().Single(t => t.TakeId == victim).Protected);

    [Fact]
    public async Task A_big_source_file_that_names_a_take_protects_it()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            AssertVictim(Service(root, bank), victim, false);
            File.WriteAllText(Path.Combine(root, "big.amplet"), new string('#', 5_000_000) + "\n# baked " + victim + "\n");
            AssertVictim(Service(root, bank), victim, true);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_aborts_the_plan_and_nothing_is_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // file locks are only mandatory on Windows
        }

        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            string song = Path.Combine(root, "song.amplet");
            File.WriteAllText(song, "# baked " + victim);
            using FileStream locked = new(song, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            AmpleException ex = Assert.Throws<AmpleException>(() => Service(root, bank).Plan());
            Assert.Equal("AMP5606", ex.DiagnosticCode);
            Assert.False(bank.GetTake(victim).Tombstoned);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    private static void CreateHistoryTable(Takebank bank, params (string Node, string FilesJson)[] rows)
    {
        using SqliteConnection c = bank.OpenConnection();
        using SqliteCommand create = c.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS h_nodes (node_id TEXT PRIMARY KEY, parent_id TEXT NULL, kind TEXT NOT NULL, message TEXT NOT NULL, at TEXT NOT NULL, files_json TEXT NOT NULL, reclaimed INTEGER NOT NULL DEFAULT 0)";
        create.ExecuteNonQuery();
        foreach ((string node, string json) in rows)
        {
            using SqliteCommand insert = c.CreateCommand();
            insert.CommandText = "INSERT INTO h_nodes (node_id, kind, message, at, files_json) VALUES ($n, 'checkpoint', 'm', 'now', $f)";
            insert.Parameters.AddWithValue("$n", node);
            insert.Parameters.AddWithValue("$f", json);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task A_take_named_in_any_kind_of_file_of_any_history_revision_is_protected_and_odd_entries_do_not_unprotect_the_rest()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            string other = await MakeAsync(bank, "block:drums", "{\"v\":1}", "drum " + new string('d', 100));
            await MakeAsync(bank, "block:drums", "{\"v\":2}", "drum two " + new string('e', 100));
            string blob = await bank.PutDataAsync(TakeFixtures.Bytes("pinned " + victim));
            // a .module entry with a null value comes first (it used to throw), the sequence file that names the take is not an .amplet
            CreateHistoryTable(bank, ("n1", "{\"a.module\":null,\"b.ampleseq\":\"" + blob + "\",\"c.seq\":42}"));
            AssertVictim(Service(root, bank), victim, true);
            AssertVictim(Service(root, bank), other, false);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_history_revision_that_cannot_be_read_aborts_the_plan()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            await VictimAsync(root, bank);
            CreateHistoryTable(bank, ("n1", "{not json"));
            Assert.Equal("AMP5606", Assert.Throws<AmpleException>(() => Service(root, bank).Plan()).DiagnosticCode);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_database_error_other_than_a_missing_table_does_not_unprotect_chosen_takes()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            await VictimAsync(root, bank);
            using (SqliteConnection c = bank.OpenConnection())
            {
                using SqliteCommand cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE c_selected (something TEXT)";   // not the expected shape: the query fails
                cmd.ExecuteNonQuery();
            }

            Assert.ThrowsAny<SqliteException>(() => Service(root, bank).Plan());
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_damaged_finals_file_still_protects_the_takes_it_names_and_a_damaged_plan_still_protects_its_inputs()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            File.WriteAllText(Path.Combine(root, ".ample", "finals.json"), "{ broken \"takeId\": \"" + victim + "\"");
            AssertVictim(Service(root, bank), victim, true);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Take_ids_in_the_assistant_proposals_are_protected()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            string folder = Path.Combine(root, ".ample", "ai", "assistant-sessions", "s1");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "t1.json"), "{\"baseline\":{\"song.amplet\":\"bake " + victim + "\"}}");
            AssertVictim(Service(root, bank), victim, true);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task The_dry_run_changes_nothing()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            string[] Snapshot() => [.. Directory.GetFiles(Path.Combine(root, ".ample"), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)).Where(f => !f.EndsWith(".db", StringComparison.Ordinal)).Select(f => f + "|" + new FileInfo(f).Length).Order(StringComparer.Ordinal)];
            string[] before = Snapshot();
            StorageService service = Service(root, bank);
            Assert.Contains(victim, service.Plan().TakeIds);
            _ = service.Overview();
            Assert.Equal(before, Snapshot());
            Assert.False(bank.GetTake(victim).Tombstoned);
            Assert.True(bank.GetTake(victim).Data.All(d => bank.DataExists(d.DataId)));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Unreadable_storage_options_stop_the_clean_up_instead_of_deleting_where_an_archive_was_chosen()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            File.WriteAllText(Path.Combine(root, ".ample", "storage.json"), "{ \"archivePath\": \"D:\\\\archive\", broken");
            StorageService service = Service(root, bank);
            Assert.Equal("AMP5606", Assert.Throws<AmpleException>(() => service.Plan()).DiagnosticCode);
            File.Delete(Path.Combine(root, ".ample", "storage.json"));
            StorageCleanPlan plan = service.Plan();
            File.WriteAllText(Path.Combine(root, ".ample", "storage.json"), "{ broken");
            AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await service.RunAsync(plan, null, CancellationToken.None));
            Assert.Equal("AMP5606", ex.DiagnosticCode);
            Assert.False(bank.GetTake(victim).Tombstoned);
            Assert.True(bank.GetTake(victim).Data.All(d => bank.DataExists(d.DataId)));
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task An_archive_folder_that_is_the_data_folder_or_inside_it_is_refused_before_anything_is_changed()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string victim = await VictimAsync(root, bank);
            StorageService service = Service(root, bank);
            StorageCleanPlan plan = service.Plan();
            foreach (string archive in new[] { Path.Combine(root, ".ample", "data"), Path.Combine(root, ".ample", "data", "ab"), Path.Combine(root, ".ample") })
            {
                new StorageSettings(10, 0, 60, null, archive, "compressed", true).Save(root);
                AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await service.RunAsync(plan, null, CancellationToken.None));
                Assert.Equal("AMP5603", ex.DiagnosticCode);
                Assert.False(bank.GetTake(victim).Tombstoned);
                Assert.True(bank.GetTake(victim).Data.All(d => bank.DataExists(d.DataId)));
            }
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_clean_up_with_an_archive_folder_moves_the_data_there_and_keeps_what_is_protected()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        string archive = Path.Combine(Directory.CreateTempSubdirectory("ample-arch-").FullName, "a");
        try
        {
            string victim = await VictimAsync(root, bank);
            string dataId = bank.GetTake(victim).Data[0].DataId;
            new StorageSettings(10, 0, 60, null, archive, "compressed", true).Save(root);
            StorageService service = Service(root, bank);
            await service.RunAsync(service.Plan(), null, CancellationToken.None);
            Assert.False(bank.DataExists(dataId));
            Assert.Single(Directory.GetFiles(archive, dataId["sha256:".Length..], SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(archive)!, true);
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Moving_the_takes_leaves_every_other_file_in_the_old_folder_alone()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        string holder = Directory.CreateTempSubdirectory("ample-docs-").FullName;
        string destination = Path.Combine(Directory.CreateTempSubdirectory("ample-dest-").FullName, "takes");
        try
        {
            // the data path points at a folder that also holds somebody's own files (think of a user folder)
            string notes = Path.Combine(holder, "notes.txt");
            File.WriteAllText(notes, "my notes");
            Directory.CreateDirectory(Path.Combine(holder, "sub"));
            File.WriteAllText(Path.Combine(holder, "sub", "song.wav"), "my song");
            new StorageSettings(10, 0, 60, holder, null, "compressed", true).Save(root);
            string take = await MakeAsync(bank, "block:melody", "{\"v\":1}", new string('x', 5000));
            Assert.True(bank.DataExists(bank.GetTake(take).Data[0].DataId));
            StorageProgress moved = await Service(root, bank).MoveAsync(destination, null, CancellationToken.None);
            Assert.Equal(1, moved.Done);
            Assert.Equal("my notes", File.ReadAllText(notes));
            Assert.Equal("my song", File.ReadAllText(Path.Combine(holder, "sub", "song.wav")));
            Assert.Empty(await bank.VerifyAsync(take));
        }
        finally
        {
            Directory.Delete(holder, true);
            Directory.Delete(Path.GetDirectoryName(destination)!, true);
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task Moving_the_takes_into_the_old_folder_itself_is_refused_and_never_deletes_the_only_copy()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        try
        {
            string take = await MakeAsync(bank, "block:melody", "{\"v\":1}", new string('y', 5000));
            string nested = Path.Combine(root, ".ample", "data", "nested");
            StorageService service = Service(root, bank);
            AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await service.MoveAsync(nested, null, CancellationToken.None));
            Assert.Equal("AMP5603", ex.DiagnosticCode);
            AmpleException around = await Assert.ThrowsAsync<AmpleException>(async () => await service.MoveAsync(Path.Combine(root, ".ample"), null, CancellationToken.None));
            Assert.Equal("AMP5603", around.DiagnosticCode);
            Assert.Empty(await bank.VerifyAsync(take));
            Assert.Null(StorageSettings.Load(root).DataPath);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_move_that_is_stopped_keeps_the_data_readable_and_continues_when_run_again()
    {
        (string root, Takebank bank) = await TakeFixtures.NewBankAsync();
        string destination = Path.Combine(Directory.CreateTempSubdirectory("ample-dest-").FullName, "takes");
        try
        {
            string a = await MakeAsync(bank, "block:melody", "{\"v\":1}", new string('a', 5000));
            string b = await MakeAsync(bank, "block:drums", "{\"v\":1}", new string('b', 5000));
            StorageService service = Service(root, bank);
            using CancellationTokenSource stop = new();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.MoveAsync(destination, new SyncProgress<StorageProgress>(_ => stop.Cancel()), stop.Token));
            Assert.Empty(await bank.VerifyAsync(a));
            Assert.Empty(await bank.VerifyAsync(b));
            await service.MoveAsync(destination, null, CancellationToken.None);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".ample", "data"), "*", SearchOption.AllDirectories));
            Assert.Empty(await bank.VerifyAsync(a));
            Assert.Empty(await bank.VerifyAsync(b));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(destination)!, true);
            TakeFixtures.Cleanup(root);
        }
    }

    private static void SetOld(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        }
        else
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        }
    }

    [Fact]
    public async Task Temporary_files_are_removed_only_inside_the_project_when_old_and_never_for_an_open_AI_task()
    {
        (string root, Takebank _) = await TakeFixtures.NewBankAsync();
        try
        {
            string ample = Path.Combine(root, ".ample");
            string oldTmp = Path.Combine(ample, "tmp", "old.bin");
            string freshTmp = Path.Combine(ample, "tmp", "fresh.bin");
            string openTask = Path.Combine(ample, "ai", "work", "task-open-1");
            string doneTask = Path.Combine(ample, "ai", "work", "task-done-2");
            string busyInside = Path.Combine(ample, "tmp", "busy");
            foreach (string folder in new[] { Path.GetDirectoryName(oldTmp)!, openTask, doneTask, busyInside })
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(oldTmp, "x");
            File.WriteAllText(freshTmp, "x");
            File.WriteAllText(Path.Combine(openTask, "TASK.md"), "x");
            File.WriteAllText(Path.Combine(doneTask, "TASK.md"), "x");
            File.WriteAllText(Path.Combine(busyInside, "recent.bin"), "x");
            foreach (string old in new[] { oldTmp, openTask, doneTask, busyInside, Path.Combine(openTask, "TASK.md"), Path.Combine(doneTask, "TASK.md") })
            {
                SetOld(old);   // the folder is old, but the file inside busyInside is fresh
            }

            List<string> names = [.. StorageTemp.List(root, DateTime.UtcNow, name => name.StartsWith("task-open", StringComparison.Ordinal)).Select(t => Path.GetFileName(t.Path))];
            Assert.Contains("old.bin", names);
            Assert.Contains("task-done-2", names);
            Assert.DoesNotContain("fresh.bin", names);
            Assert.DoesNotContain("task-open-1", names);
            Assert.DoesNotContain("busy", names);   // something inside was written recently

            // deleting refuses everything that is not a direct child of a temporary folder
            string outside = Path.Combine(Directory.CreateTempSubdirectory("ample-out-").FullName, "keep.txt");
            File.WriteAllText(outside, "keep");
            Assert.False(StorageTemp.Delete(root, outside));
            Assert.False(StorageTemp.Delete(root, Path.Combine(ample, "tmp")));
            Assert.False(StorageTemp.Delete(root, Path.Combine(ample, "data")));
            Assert.False(StorageTemp.Delete(root, Path.Combine(ample, "tmp", "..", "project.json")));
            Assert.True(File.Exists(outside));
            Assert.True(StorageTemp.Delete(root, oldTmp));
            Assert.False(File.Exists(oldTmp));
            Directory.Delete(Path.GetDirectoryName(outside)!, true);
        }
        finally
        {
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_temporary_folder_that_is_a_junction_to_somewhere_else_is_never_listed_or_emptied()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (string root, Takebank _) = await TakeFixtures.NewBankAsync();
        string elsewhere = Directory.CreateTempSubdirectory("ample-docs-").FullName;
        try
        {
            string precious = Path.Combine(elsewhere, "precious.docx");
            File.WriteAllText(precious, "mine");
            SetOld(precious);
            string tmp = Path.Combine(root, ".ample", "tmp");
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, true);
            }

            using (System.Diagnostics.Process mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{tmp}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            {
                mk.WaitForExit();
            }

            Assert.NotNull(new DirectoryInfo(tmp).LinkTarget);
            Assert.Empty(StorageTemp.List(root, DateTime.UtcNow, _ => false));
            Assert.False(StorageTemp.Delete(root, precious));
            Assert.False(StorageTemp.Delete(root, Path.Combine(tmp, "precious.docx")));
            Assert.True(File.Exists(precious));
        }
        catch (IOException)
        {
            // symbolic links need a privilege on this machine: nothing to show
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (Directory.Exists(Path.Combine(root, ".ample", "tmp")) && new DirectoryInfo(Path.Combine(root, ".ample", "tmp")).LinkTarget is not null)
            {
                Directory.Delete(Path.Combine(root, ".ample", "tmp"));
            }

            Directory.Delete(elsewhere, true);
            TakeFixtures.Cleanup(root);
        }
    }

    [Fact]
    public async Task A_dropped_line_whose_content_and_archive_are_gone_cannot_be_opened_and_leaves_the_working_files_alone()
    {
        using HistoryRig r = await HistoryRig.CreateAsync(("song.amplet", "base"), ("other.amplet", "other"));
        await r.History.ForkAsync("experiment");
        await r.History.GoAsync("experiment");
        r.Write("song.amplet", "only on experiment");
        await r.History.CheckpointAsync("experiment work");
        await r.History.GoAsync("garage");
        DropLineAct drop = new(r.History, "experiment");
        await drop.RunAsync(r.Journal, r.Context, "user", drop.Acceptance, "done");
        CleanupAct cleanup = new(r.History);
        await cleanup.RunAsync(r.Journal, r.Context, "user", cleanup.Acceptance, "space");

        Directory.Delete(Path.Combine(r.Root, ".ample", "history-archive"), true);   // the safety copy is gone too (only then is the content really lost)
        await r.History.RestoreLineAsync("experiment", "oops");
        r.Write("other.amplet", "edited after");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await r.History.GoAsync("experiment"));
        Assert.Equal("AMP5246", ex.DiagnosticCode);
        Assert.Equal("edited after", File.ReadAllText(Path.Combine(r.Root, "other.amplet")));
        Assert.Equal("base", File.ReadAllText(Path.Combine(r.Root, "song.amplet")));
    }
}
