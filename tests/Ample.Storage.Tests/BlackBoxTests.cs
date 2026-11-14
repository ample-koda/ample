using System.Diagnostics;
using System.Text.Json;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Testing;

namespace Ample.Core.Tests;

[Trait("Category", "Blackbox")]
public sealed class BlackBoxTests
{
    private static readonly string Root = Path.GetFullPath("/proj");
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static string Seg(int n = 1) => Path.Combine(Root, ".ample", "blackbox", n.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".seg");

    private static async Task<Journal> Open(IFileSystem fs, FakeClock? clock = null, JournalOptions? options = null) => await Journal.OpenAsync(fs, clock ?? new FakeClock(T0), Root, "pid-1", options);

    private static async Task Add(Journal j, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await j.AppendAsync("user", "test.kind", i % 2 == 0 ? "start" : "ok", "c" + (i / 2), JsonSerializer.Serialize(new { i }), i == 0 ? "{\"pre\":true}" : null);
        }
    }

    [Fact]
    public void Redux_references_have_fixed_and_compact_forms_and_parse_only_ascii_digits()
    {
        Assert.Equal("AR-0000000000000001", ReduxRecordReference.Padded(1));
        Assert.Equal("AR-1", ReduxRecordReference.Compact(1));
        Assert.Equal("AR-9999999999999999", ReduxRecordReference.Padded(ReduxRecordReference.MaximumSequence));
        Assert.True(ReduxRecordReference.TryParse("AR-0000000000000001", out long padded));
        Assert.True(ReduxRecordReference.TryParse("ar-1", out long compact));
        Assert.Equal(1, padded);
        Assert.Equal(1, compact);
        Assert.False(ReduxRecordReference.TryParse("AR-١", out _));
        Assert.False(ReduxRecordReference.TryParse("AR-10000000000000000", out _));
    }

    [Fact]
    public async Task Redux_references_are_unique_under_concurrent_appends_to_the_project_writer()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await Open(fs);
        await Task.WhenAll(Enumerable.Range(0, 48).Select(i => journal.AppendAsync("ui", "test.concurrent", "ok", "c" + i, "{}").AsTask()));

        IReadOnlyList<JournalRecord> records = await journal.ReadAllAsync();
        Assert.Equal(Enumerable.Range(1, 48).Select(i => (long)i), records.Select(record => record.Seq).Order());
        Assert.Equal(48, records.Select(record => record.ArId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("AR-1", records[0].CompactArId);
        Assert.Equal("AR-0000000000000048", records[^1].ArId);
    }

    [Fact]
    public async Task Redux_storage_accounting_includes_nested_content_addressed_artifacts()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await Open(fs);
        await journal.AppendAsync("ui", "assistant.task", "done", "task", "{}");
        byte[] artifact = [1, 2, 3, 4, 5];
        await fs.WriteAllBytesDurableAsync(Path.Combine(Root, ".ample", "blackbox", "artifacts", "ab", "artifact.txt"), artifact, CancellationToken.None);

        long used = await JournalRetention.StorageBytesAsync(fs, Root, CancellationToken.None);

        int segmentBytes = (await fs.ReadAllBytesAsync(Seg(), CancellationToken.None)).Length;
        Assert.True(used >= segmentBytes + artifact.Length);
    }

    [Fact]
    public async Task Redux_compaction_preview_matches_the_explicit_compaction_impact()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await Open(fs, options: new JournalOptions(SegmentBytes: 1));
        await Add(journal, 8);
        BlackBoxSettings settings = new(1, 90, 365);
        CompactionPreview preview = await journal.PreviewCompactionAsync(settings, CancellationToken.None);
        CompactionResult result = await journal.CompactAsync(settings, CancellationToken.None);
        long after = await JournalRetention.StorageBytesAsync(fs, Root, CancellationToken.None);

        Assert.True(preview.SegmentsToThin > 0);
        Assert.Equal(preview.SegmentsToThin, result.Thinned);
        Assert.Equal(preview.BytesAfter, after);
    }

    [Fact]
    public async Task Redux_compaction_refuses_a_preview_after_new_records_arrive()
    {
        InMemoryFileSystem fs = new();
        using Journal journal = await Open(fs);
        await Add(journal, 2);
        CompactionPreview preview = await journal.PreviewCompactionAsync(new BlackBoxSettings(1, 90, 365), CancellationToken.None);
        await journal.AppendAsync("ui", "new.record", "ok", "later", "{}");

        await Assert.ThrowsAsync<AmpleException>(() => journal.CompactAsync(new BlackBoxSettings(1, 90, 365), preview.ThroughSequence, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Redux_sequence_survives_compaction_and_reopen_without_reusing_a_reference()
    {
        InMemoryFileSystem fs = new();
        using (Journal journal = await Open(fs, options: new JournalOptions(SegmentBytes: 1)))
        {
            await Add(journal, 8);
        }

        await JournalRetention.CompactAsync(fs, Root, T0.AddDays(120), new BlackBoxSettings(1, 90, 365));
        using Journal reopened = await Open(fs);
        JournalRecord next = await reopened.AppendAsync("ui", "after.compaction", "ok", "new", "{}");

        Assert.Equal(9, next.Seq);
        Assert.Equal("AR-0000000000000009", next.ArId);
        Assert.Equal(9, (await reopened.ReadAllAsync())[^1].Seq);
    }

    private sealed class ThrowingCommand : ICommand
    {
        public string Kind => "test.fail";

        public string PayloadJson => "{}";

        public ValueTask<string?> PreImageAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public ValueTask ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class DeleteFileAct(string path) : DangerousAct
    {
        public override string Title => "Delete a file";

        public override string Acceptance => "delete " + path;

        protected override string Kind => "file.delete";

        public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken) => ValueTask.FromResult(new DryRunResult([path + " would be deleted"], "Make a backup first."));

        protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
        {
            context.Fs.DeleteFile(ProjectPath.Resolve(context.ProjectRoot, path));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Records_form_a_hash_chain_that_verifies_and_notices_a_changed_record()
    {
        InMemoryFileSystem fs = new();
        using Journal j = await Open(fs);
        await Add(j, 5);
        VerifyReport ok = await j.VerifyAsync();
        Assert.Equal(5, ok.Records);
        Assert.Null(ok.BadSeq);

        string text = await fs.ReadAllTextAsync(Seg(), CancellationToken.None);
        await fs.WriteAllTextAsync(Seg(), text.Replace("\\u0022i\\u0022:2", "\\u0022i\\u0022:9", StringComparison.Ordinal).Replace("\"i\\\":2", "\"i\\\":9", StringComparison.Ordinal), CancellationToken.None);
        Assert.Equal(3, (await j.VerifyAsync()).BadSeq);
        Assert.Equal(["user"], (await j.ReadAllAsync()).Select(r => r.Actor).Distinct());
    }

    [Fact]
    public async Task A_torn_last_line_is_detected_dropped_and_the_chain_continues_for_every_cut()
    {
        InMemoryFileSystem source = new();
        using (Journal j = await Open(source))
        {
            await Add(j, 3);
        }

        string text = await source.ReadAllTextAsync(Seg(), CancellationToken.None);
        int lastStart = text.TrimEnd('\n').LastIndexOf('\n') + 1;
        for (int cut = lastStart; cut < text.Length; cut++)
        {
            InMemoryFileSystem fs = new();
            await fs.WriteAllTextAsync(Seg(), text[..cut], CancellationToken.None);
            using Journal j = await Open(fs);
            int kept = cut >= text.Length - 1 ? 3 : 2;
            Assert.Equal(kept, (await j.ReadAllAsync()).Count);
            await j.AppendAsync("user", "after", "ok", "x", "{}");
            VerifyReport report = await j.VerifyAsync();
            Assert.Null(report.BadSeq);
            Assert.Equal(kept + 1, report.Records);
            Assert.False(report.TornTail);
        }
    }

    [Fact]
    public async Task The_bus_journals_before_applying_keeps_the_pre_image_and_records_failures()
    {
        InMemoryFileSystem fs = new();
        CommandContext context = new(fs, Root, new FakeClock(T0));
        using Journal j = await Open(fs);
        CommandBus bus = new(j, context, "user");
        await bus.ExecuteAsync(new WriteFileCommand("a.txt", "one"));
        await bus.ExecuteAsync(new WriteFileCommand("a.txt", "two"));
        Assert.Equal("two", await fs.ReadAllTextAsync(Path.Combine(Root, "a.txt"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await bus.ExecuteAsync(new ThrowingCommand()));

        IReadOnlyList<JournalRecord> all = await j.ReadAllAsync();
        Assert.Equal(["start", "ok", "start", "ok", "start", "failed"], all.Select(r => r.Phase));
        Assert.Contains("\"text\":\"one\"", all[2].PreImageJson, StringComparison.Ordinal);
        Assert.Equal("boom", all[5].Error);
        Assert.Empty(await j.FindInterruptedAsync());
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_blocks_the_command()
    {
        InMemoryFileSystem memory = new();
        FaultyFileSystem fs = new(memory, 4);
        using Journal j = await Open(fs);
        CommandBus bus = new(j, new CommandContext(fs, Root, new FakeClock(T0)), "user");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await bus.ExecuteAsync(new WriteFileCommand("a.txt", "x")));
        Assert.Equal("AMP5230", ex.DiagnosticCode);
        Assert.False(memory.FileExists(Path.Combine(Root, "a.txt")));
    }

    [Fact]
    public async Task Binary_file_import_keeps_a_restorable_pre_image()
    {
        InMemoryFileSystem fs = new();
        CommandContext context = new(fs, Root, new FakeClock(T0));
        using Journal j = await Open(fs);
        CommandBus bus = new(j, context, "user");
        byte[] original = [0, 1, 2, 255];
        byte[] replacement = [8, 9, 10];
        await bus.ExecuteAsync(new ImportFileCommand("assets/sample.wav", original));
        await bus.ExecuteAsync(new ImportFileCommand("assets/sample.wav", replacement));
        JournalRecord overwritten = (await j.ReadAllAsync()).Where(record => record.Kind == "file.import" && record.Phase == "start").Last();

        await bus.ExecuteAsync(new RestorePreImageCommand(overwritten));
        Assert.Equal(original, await fs.ReadAllBytesAsync(Path.Combine(Root, "assets", "sample.wav"), CancellationToken.None));
    }

    [Fact]
    public async Task Folder_and_move_pre_images_restore_path_changes()
    {
        InMemoryFileSystem fs = new();
        fs.CreateDirectory(Root);
        CommandContext context = new(fs, Root, new FakeClock(T0));
        using Journal journal = await Open(fs);
        CommandBus bus = new(journal, context, "user");
        await bus.ExecuteAsync(new CreateFolderCommand("source"));
        await bus.ExecuteAsync(new ImportFileCommand("source/clip.wav", [1, 2, 3]));
        await bus.ExecuteAsync(new MoveProjectPathCommand("source", "moved", directory: true));
        JournalRecord moved = (await journal.ReadAllAsync()).Last(record => record.Kind == "folder.move" && record.Phase == "start");
        await bus.ExecuteAsync(new RestorePreImageCommand(moved));
        Assert.True(fs.FileExists(Path.Combine(Root, "source", "clip.wav")));

        await bus.ExecuteAsync(new CreateFolderCommand("empty"));
        await bus.ExecuteAsync(new DeleteFolderCommand("empty"));
        JournalRecord deleted = (await journal.ReadAllAsync()).Last(record => record.Kind == "folder.delete" && record.Phase == "start");
        await bus.ExecuteAsync(new RestorePreImageCommand(deleted));
        Assert.True(fs.DirectoryExists(Path.Combine(Root, "empty")));
    }

    [Fact]
    public async Task Interrupted_commands_are_found_with_their_pre_image_and_can_be_restored_or_resolved()
    {
        InMemoryFileSystem fs = new();
        CommandContext context = new(fs, Root, new FakeClock(T0));
        using Journal j = await Open(fs);
        CommandBus bus = new(j, context, "user");
        await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "original"));
        await bus.ExecuteAsync(new WriteFileCommand("song.amplet", "changed"));
        JournalRecord changeStart = (await j.ReadAllAsync()).Where(r => r.Phase == "start").Last();

        await bus.ExecuteAsync(new RestorePreImageCommand(changeStart));
        Assert.Equal("original", await fs.ReadAllTextAsync(Path.Combine(Root, "song.amplet"), CancellationToken.None));
        JournalRecord firstStart = (await j.ReadAllAsync()).First(r => r.Phase == "start");
        await bus.ExecuteAsync(new RestorePreImageCommand(firstStart));
        Assert.False(fs.FileExists(Path.Combine(Root, "song.amplet")));

        await j.AppendAsync("user", "file.write", "start", "crashed", "{\"path\":\"x\"}", "{\"path\":\"x\",\"existed\":false}");
        InterruptedCommand interrupted = Assert.Single(await j.FindInterruptedAsync());
        Assert.Equal("crashed", interrupted.Start.Correlation);
        await bus.ResolveAsync(interrupted, "abandoned");
        Assert.Empty(await j.FindInterruptedAsync());
    }

    [Fact]
    public async Task A_damaged_record_is_never_repaired_away_and_new_records_go_to_a_new_segment()
    {
        InMemoryFileSystem fs = new();
        using (Journal j = await Open(fs))
        {
            await Add(j, 4);
        }

        string text = await fs.ReadAllTextAsync(Seg(), CancellationToken.None);
        string damaged = text.Replace("\\u0022i\\u0022:1", "\\u0022i\\u0022:7", StringComparison.Ordinal).Replace("\"i\\\":1", "\"i\\\":7", StringComparison.Ordinal);
        Assert.NotEqual(text, damaged);
        await fs.WriteAllTextAsync(Seg(), damaged, CancellationToken.None);

        using Journal reopened = await Open(fs);
        Assert.Equal(damaged, await fs.ReadAllTextAsync(Seg(), CancellationToken.None));
        Assert.Equal(2, (await reopened.VerifyAsync()).BadSeq);
        await reopened.AppendAsync("user", "after", "ok", "x", "{}");
        Assert.Equal(damaged, await fs.ReadAllTextAsync(Seg(), CancellationToken.None));
        Assert.Equal(2, fs.GetFiles(Path.Combine(Root, ".ample", "blackbox")).Count(path => path.EndsWith(".seg", StringComparison.Ordinal)));
        Assert.Equal(2, (await reopened.VerifyAsync()).BadSeq);
        Assert.True((await reopened.ReadAllAsync())[^1].Seq > 4);
    }

    [Fact]
    public async Task Segments_rotate_and_reading_and_verifying_span_them()
    {
        InMemoryFileSystem fs = new();
        using Journal j = await Open(fs, options: new JournalOptions(SegmentBytes: 400));
        await Add(j, 12);
        Assert.True(fs.GetFiles(Path.Combine(Root, ".ample", "blackbox")).Count > 2);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), (await j.ReadAllAsync()).Select(r => r.Seq));
        Assert.Null((await j.VerifyAsync()).BadSeq);
        using Journal reopened = await Open(fs, options: new JournalOptions(SegmentBytes: 400));
        await reopened.AppendAsync("user", "more", "ok", "z", "{}");
        Assert.Equal(13, (await reopened.VerifyAsync()).Records);
    }

    [Fact]
    public async Task The_mirror_keeps_the_tail_outside_the_project_folder()
    {
        InMemoryFileSystem fs = new();
        string mirror = Path.GetFullPath("/mnt/ample-mirror");
        using Journal j = await Open(fs, options: new JournalOptions(MirrorRoot: mirror, MirrorRecords: 3));
        await Add(j, 8);
        fs.DeleteDirectory(Path.Combine(Root, ".ample"), recursive: true);
        IReadOnlyList<JournalRecord> tail = Journal.ReadMirror(fs, mirror, "pid-1");
        Assert.Equal(3, tail.Count);
        Assert.Equal(8, tail[^1].Seq);
    }

    [Fact]
    public async Task Compaction_keeps_a_segment_with_an_unresolved_start_and_recovery_still_finds_it()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(T0.AddDays(-600));
        using Journal j = await Open(fs, clock, new JournalOptions(SegmentBytes: 1));
        await j.AppendAsync("user", "cmd.open", "start", "open-1", "{}");
        clock.Advance(TimeSpan.FromDays(100));
        for (int i = 0; i < 4; i++)
        {
            await j.AppendAsync("user", "test.kind", "ok", "c" + i, "{}");
            clock.Advance(TimeSpan.FromDays(100));
        }

        BlackBoxSettings settings = new(10_000_000, 90, 365);
        CompactionPreview preview = await j.PreviewCompactionAsync(settings, CancellationToken.None);
        Assert.Equal(1, preview.ProtectedSegments);
        Assert.True(preview.ProtectedBytes > 0);
        CompactionResult result = await j.CompactAsync(settings, CancellationToken.None);
        Assert.Equal(preview.SegmentsToThin, result.Thinned);
        Assert.Equal(preview.BytesAfter, await JournalRetention.StorageBytesAsync(fs, Root, CancellationToken.None));
        Assert.True(result.Thinned >= 1);
        Assert.Contains(await j.FindInterruptedAsync(), c => c.Start.Correlation == "open-1");
        Assert.Contains(fs.GetFiles(Path.Combine(Root, ".ample", "blackbox")), f => f.EndsWith("0000000001.seg", StringComparison.Ordinal));

        // a resolved command (terminal record in a later segment) does not protect anything and is thinned like the rest
        await j.AppendAsync("user", "cmd.open", "resolved", "open-1", "{}");
        await j.AppendAsync("user", "test.kind", "ok", "tail", "{}");
        CompactionResult again = await j.CompactAsync(settings, CancellationToken.None);
        Assert.Empty(await j.FindInterruptedAsync());
        Assert.True(again.Thinned >= 1);
        IReadOnlyList<string> files = fs.GetFiles(Path.Combine(Root, ".ample", "blackbox"));
        Assert.DoesNotContain(files, f => f.EndsWith("0000000001.seg", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(50, true)]
    [InlineData(80, true)]
    [InlineData(100, true)]
    public async Task The_warning_threshold_is_one_fifth_of_the_budget_for_status_and_compaction(int percentUsed, bool warns)
    {
        InMemoryFileSystem fs = new();
        using Journal j = await Open(fs);
        await Add(j, 3);
        long used = await JournalRetention.StorageBytesAsync(fs, Root, CancellationToken.None);
        long budget = used * 100 / percentUsed;

        CompactionResult result = await JournalRetention.CompactAsync(fs, Root, T0, new BlackBoxSettings(budget, 90, 365));
        Assert.Equal(used, result.TotalBytes);
        Assert.Equal(warns, result.BudgetWarning);
        Assert.Equal(warns, used >= JournalRetention.WarnAtBytes(budget));
    }

    [Fact]
    public async Task Retention_thins_old_segments_removes_expired_summaries_and_honours_the_budget()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(T0.AddDays(-600));
        using Journal j = await Open(fs, clock, new JournalOptions(SegmentBytes: 1));
        for (int day = 0; day < 6; day++)
        {
            await j.AppendAsync("user", "test.kind", "ok", "c" + day, "{}");
            clock.Advance(TimeSpan.FromDays(100));
        }

        BlackBoxSettings settings = new(10_000_000, 90, 365);
        CompactionResult result = await JournalRetention.CompactAsync(fs, Root, T0, settings);
        Assert.True(result.Thinned >= 3);
        Assert.True(result.Deleted >= 1);
        Assert.Contains(fs.GetFiles(Path.Combine(Root, ".ample", "blackbox")), f => f.EndsWith(".seg", StringComparison.Ordinal));
        Assert.Null((await j.VerifyAsync()).BadSeq);

        CompactionResult tiny = await JournalRetention.CompactAsync(fs, Root, T0, new BlackBoxSettings(1, 90, 365));
        Assert.True(tiny.BudgetWarning);
    }

    [Fact]
    public async Task A_dangerous_act_needs_the_typed_acceptance_and_a_reason_and_leaves_a_tombstone()
    {
        InMemoryFileSystem fs = new();
        CommandContext context = new(fs, Root, new FakeClock(T0));
        using Journal j = await Open(fs);
        await new WriteFileCommand("x.txt", "keep me?").ExecuteAsync(context, CancellationToken.None);
        DeleteFileAct act = new("x.txt");

        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.ExecuteAsync(j, context, "user", "yes", "why"))).DiagnosticCode);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.ExecuteAsync(j, context, "user", act.Acceptance, "  "))).DiagnosticCode);
        Assert.True(fs.FileExists(Path.Combine(Root, "x.txt")));
        Assert.Contains("x.txt would be deleted", (await act.DryRunAsync(context, CancellationToken.None)).Impact);

        await act.ExecuteAsync(j, context, "user", act.Acceptance, "not needed any more");
        Assert.False(fs.FileExists(Path.Combine(Root, "x.txt")));
        Assert.Equal(["start", "ok", "tombstone"], (await j.ReadAllAsync()).Select(r => r.Phase));
    }

    [Fact]
    public async Task Two_settings_saves_made_from_the_same_settings_keep_each_others_groups()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(T0);
        using OpenProject project = await ProjectStore.CreateAsync(fs, clock, new SequentialIdGenerator([Guid.NewGuid()]), Root, "song");
        using Journal j = await Open(fs, clock);
        CommandBus bus = new(j, new CommandContext(fs, Root, clock), "ui");
        ProjectSettings basis = project.Manifest.Settings;   // both saves started from this
        await bus.ExecuteAsync(new SetSettingsCommand(basis with { BlackBox = new BlackBoxSettings(123, 30, 60), BuildParallelism = 2 }, basis));
        await bus.ExecuteAsync(new SetSettingsCommand(basis with { DefaultEngine = "turbo", Koda = basis.Koda with { Language = "nl" } }, basis));   // must not put the first save back to the basis
        ProjectSettings now = ProjectFile.Parse(await fs.ReadAllTextAsync(Path.Combine(Root, ".ample", "project.json"), CancellationToken.None)).Settings;
        Assert.Equal(123, now.BlackBox.BudgetBytes);
        Assert.Equal(2, now.BuildParallelism);
        Assert.Equal("turbo", now.DefaultEngine);
        Assert.Equal("nl", now.Koda.Language);

        // the same group saved twice: the later save wins; a group a save did not change is never written from its stale copy
        await bus.ExecuteAsync(new SetSettingsCommand(basis with { BlackBox = new BlackBoxSettings(456, 30, 60) }, basis));
        now = ProjectFile.Parse(await fs.ReadAllTextAsync(Path.Combine(Root, ".ample", "project.json"), CancellationToken.None)).Settings;
        Assert.Equal(456, now.BlackBox.BudgetBytes);
        Assert.Equal("turbo", now.DefaultEngine);
        Assert.Equal(2, now.BuildParallelism);

        // without a basis the whole settings are written, as before
        await bus.ExecuteAsync(new SetSettingsCommand(basis));
        Assert.Equal(basis.BlackBox.BudgetBytes, ProjectFile.Parse(await fs.ReadAllTextAsync(Path.Combine(Root, ".ample", "project.json"), CancellationToken.None)).Settings.BlackBox.BudgetBytes);
    }

    [Fact]
    public async Task Project_settings_changes_go_through_the_bus_and_can_be_restored()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(T0);
        using OpenProject project = await ProjectStore.CreateAsync(fs, clock, new SequentialIdGenerator([Guid.NewGuid()]), Root, "song");
        using Journal j = await Open(fs, clock);
        CommandBus bus = new(j, new CommandContext(fs, Root, clock), "ui");
        await bus.ExecuteAsync(new SetSettingsCommand(project.Manifest.Settings with { BlackBox = new BlackBoxSettings(123, 30, 60) }));
        ProjectManifest changed = ProjectFile.Parse(await fs.ReadAllTextAsync(Path.Combine(Root, ".ample", "project.json"), CancellationToken.None));
        Assert.Equal(123, changed.Settings.BlackBox.BudgetBytes);

        await bus.ExecuteAsync(new RestorePreImageCommand((await j.ReadAllAsync()).First(r => r.Phase == "start")));
        Assert.Equal(ProjectSettings.Default.BlackBox.BudgetBytes, ProjectFile.Parse(await fs.ReadAllTextAsync(Path.Combine(Root, ".ample", "project.json"), CancellationToken.None)).Settings.BlackBox.BudgetBytes);
    }

    [Fact]
    public void Only_the_infrastructure_folders_write_files_directly()
    {
        string src = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(src, "src", "Ample.Storage")))
        {
            src = Path.GetDirectoryName(src)!;
        }

        string[] allowed = ["IO", "Projects", "Takes", "BlackBox", "Backup", "Security", "Quality", "Credits"];
        string[] assemblies = ["Ample.Audio", "Ample.Storage", "Ample.Engine"];
        // the layers above Storage are not always present (the public repository grows layer by layer): scan the ones that are
        foreach ((string assembly, string file) in assemblies.Where(a => Directory.Exists(Path.Combine(src, "src", a))).SelectMany(a => Directory.GetFiles(Path.Combine(src, "src", a), "*.cs", SearchOption.AllDirectories).Select(f => (a, f))))
        {
            string folder = Path.GetRelativePath(Path.Combine(src, "src", assembly), file).Split(Path.DirectorySeparatorChar)[0];
            // BuildStats and Finals keep a derived cache next to the takes: a temp file and a move, not project data. HistoryArchive writes a whole archive into a partial folder and
            // moves the folder into place (a half-written archive is never seen), so the manifest inside the partial folder needs no atomic write of its own
            if (allowed.Contains(folder) || Path.GetFileName(file) is "BuildStats.cs" or "Finals.cs" or "HistoryArchive.cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);
            Assert.DoesNotContain("AtomicFile.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteAllText", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateDurableWriteStream", text, StringComparison.Ordinal);
        }
    }

    // Disabled permanently (owner's request, 2026-10-16): a real-disk wall-clock budget runs flaky under any
    // concurrent load on the machine (observed failing at 367 ms/command with a Playwright run going at the same
    // time; passes alone). PerformanceTests.cs is the real home for this kind of measurement - its own tests are
    // explicitly documented to "run alone," gated against docs/perf-budgets.json, not a single hard-coded figure.
    [Fact(Skip = "Flaky under concurrent system load (real disk wall-clock); disabled permanently per owner request 2026-10-16 - see PerformanceTests.cs for the supported way to measure this.")]
    public async Task Journaling_ten_commands_on_a_real_disk_is_fast_enough()
    {
        string folder = Directory.CreateTempSubdirectory("ample-bb-").FullName;
        try
        {
            PhysicalFileSystem fs = new();
            SystemClock clock = new();
            using Journal j = await Journal.OpenAsync(fs, clock, folder, "perf");
            CommandBus bus = new(j, new CommandContext(fs, folder, clock), "perf");
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                await bus.ExecuteAsync(new WriteFileCommand("f.txt", "content " + i));
            }

            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / 10;
            Console.WriteLine($"journaled file write: {ms:F2} ms per command (10 commands, real disk)");
            Assert.True(ms < 250, $"{ms:F1} ms per command");
            Assert.Null((await j.VerifyAsync()).BadSeq);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
