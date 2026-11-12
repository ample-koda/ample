using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;
using Ample.Testing;

namespace Ample.Core.Tests;

[Trait("Category", "Project")]
public sealed class ProjectPathTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ample-root"));

    [Theory]
    [InlineData("song.amplet")]
    [InlineData("lyrics/chorus.amplelrc")]
    [InlineData("a\\b\\c.txt")]
    [InlineData("./a/../b.txt")]
    [InlineData("dir/")]
    [InlineData("name.with.dots.txt")]
    public void Valid_paths_resolve_inside_the_root(string relative)
    {
        string full = ProjectPath.Resolve(Root, relative);
        Assert.True(ProjectPath.IsInside(Root, full), full);
    }

    [Theory]
    [InlineData("../x", "AMP5201")]
    [InlineData("a/../../x", "AMP5201")]
    [InlineData("/etc/passwd", "AMP5201")]
    [InlineData("//server/share", "AMP5201")]
    [InlineData("C:\\x", "AMP5202")]
    [InlineData("con.txt", "AMP5202")]
    [InlineData("a/NUL", "AMP5202")]
    [InlineData("a/com1.log", "AMP5202")]
    [InlineData("a/b.", "AMP5202")]
    [InlineData("a/b ", "AMP5202")]
    [InlineData("a?b", "AMP5202")]
    [InlineData("a\0b", "AMP5202")]
    [InlineData("", "AMP5202")]
    [InlineData("   ", "AMP5202")]
    public void Unsafe_paths_are_refused_with_a_code(string relative, string code)
    {
        AmpleException ex = Assert.Throws<AmpleException>(() => ProjectPath.Resolve(Root, relative));
        Assert.Equal(code, ex.DiagnosticCode);
    }

    [Fact]
    public void Links_that_lead_outside_are_refused_unless_registered()
    {
        InMemoryFileSystem fs = new();
        string outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ample-outside"));
        fs.AddLink(Path.Combine(Root, "escape"), outside);
        fs.AddLink(Path.Combine(Root, "inside"), Path.Combine(Root, "real"));

        Assert.Equal("AMP5203", Assert.Throws<AmpleException>(() => ProjectPath.Resolve(fs, Root, "escape/file.txt")).DiagnosticCode);
        Assert.StartsWith(Root, ProjectPath.Resolve(fs, Root, "inside/file.txt"), StringComparison.Ordinal);
        Assert.StartsWith(Root, ProjectPath.Resolve(fs, Root, "escape/file.txt", [outside]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sibling_folder_with_the_same_prefix_is_not_inside()
    {
        Assert.False(ProjectPath.IsInside(Root, Root + "-other"));
        Assert.True(ProjectPath.IsInside(Root, Root));
    }
}

[Trait("Category", "Project")]
public sealed class AtomicFileTests
{
    private static readonly string Target = Path.GetFullPath("/p/project.json");

    [Fact]
    public async Task A_failure_at_any_step_leaves_the_old_or_the_new_file_complete_and_no_leftover()
    {
        for (int failAt = 1; failAt <= 5; failAt++)
        {
            InMemoryFileSystem memory = new();
            await memory.WriteAllTextAsync(Target, "old content", CancellationToken.None);
            FaultyFileSystem fs = new(memory, failAt);
            bool threw = false;
            try
            {
                await AtomicFile.WriteTextAsync(fs, Target, "NEW CONTENT");
            }
            catch (IOException)
            {
                threw = true;
            }

            string now = await memory.ReadAllTextAsync(Target, CancellationToken.None);
            Assert.True(now is "old content" or "NEW CONTENT", $"failAt {failAt}: partial content '{now}'");
            Assert.DoesNotContain(memory.AllFiles, f => f.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal));
            Assert.Equal(failAt <= 3, threw);
            if (failAt is 1 or 2)
            {
                Assert.Equal("old content", now);
            }
            else
            {
                Assert.Equal("NEW CONTENT", now);
            }
        }
    }

    [Fact]
    public async Task The_real_file_system_replaces_a_file_and_leaves_no_temporary_file()
    {
        string folder = Directory.CreateTempSubdirectory("ample-atomic-").FullName;
        try
        {
            PhysicalFileSystem fs = new();
            string path = Path.Combine(folder, "a.json");
            await AtomicFile.WriteTextAsync(fs, path, "one");
            await AtomicFile.WriteTextAsync(fs, path, "two");
            Assert.Equal("two", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

[Trait("Category", "Project")]
public sealed class ProjectStoreTests
{
    private static readonly string Root = Path.GetFullPath("/proj");
    private static readonly Guid Id = Guid.Parse("01234567-89ab-7cde-8f01-23456789abcd");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static (InMemoryFileSystem Fs, FakeClock Clock) Fresh() => (new InMemoryFileSystem(), new FakeClock(Now));

    private static SequentialIdGenerator Ids() => new([Id]);

    private static string ProjectJson(string root) => Path.Combine(root, ".ample", "project.json");

    private const string V0 = "{\"id\":\"01234567-89ab-7cde-8f01-23456789abcd\",\"name\":\"old song\",\"created\":\"2026-01-01T00:00:00.0000000Z\",\"seed\":12345}";

    [Fact]
    public async Task Create_makes_the_mandatory_structure_and_a_canonical_project_file()
    {
        (InMemoryFileSystem fs, FakeClock clock) = Fresh();
        using OpenProject project = await ProjectStore.CreateAsync(fs, clock, Ids(), Root, " My Song ");

        Assert.False(project.IsReadOnly);
        Assert.Equal("My Song", project.Manifest.Name);
        Assert.Equal(Id, project.Manifest.Id);
        foreach (string folder in ProjectLayout.RequiredFolders)
        {
            Assert.True(fs.DirectoryExists(Path.Combine(Root, ".ample", folder)), folder);
        }

        string json = await fs.ReadAllTextAsync(ProjectJson(Root), CancellationToken.None);
        Assert.Equal(json, ProjectFile.Serialize(ProjectFile.Parse(json)));
        Assert.Equal(json, ProjectFile.Serialize(project.Manifest));
        Assert.True(project.Manifest.MasterSeed <= uint.MaxValue);
        Assert.Equal(project.Manifest.MasterSeed, ProjectFile.CreateNew(Id, "x", Now, null).MasterSeed);

        AmpleException again = await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.CreateAsync(fs, clock, Ids(), Root, "again"));
        Assert.Equal("AMP5208", again.DiagnosticCode);
    }

    [Fact]
    public async Task A_second_writer_is_refused_with_the_holder_and_readers_still_work()
    {
        (InMemoryFileSystem fs, FakeClock clock) = Fresh();
        using (OpenProject first = await ProjectStore.CreateAsync(fs, clock, Ids(), Root, "song"))
        {
            AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root));
            Assert.Equal("AMP5204", ex.DiagnosticCode);
            Assert.Contains("process", ex.Message, StringComparison.Ordinal);

            using OpenProject reader = await ProjectStore.OpenAsync(fs, clock, Root, readOnly: true);
            Assert.True(reader.IsReadOnly);
            Assert.Equal(ProjectFile.Serialize(first.Manifest), ProjectFile.Serialize(reader.Manifest));
        }

        using OpenProject second = await ProjectStore.OpenAsync(fs, clock, Root);
        Assert.False(second.IsReadOnly);
    }

    [Fact]
    public async Task Opening_something_that_is_not_a_project_or_is_broken_gives_codes()
    {
        (InMemoryFileSystem fs, FakeClock clock) = Fresh();
        Assert.Equal("AMP5210", (await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root))).DiagnosticCode);

        await fs.WriteAllTextAsync(ProjectJson(Root), "{ not json", CancellationToken.None);
        Assert.Equal("AMP5205", (await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root))).DiagnosticCode);

        await fs.WriteAllTextAsync(ProjectJson(Root), "{\"formatVersion\":99}", CancellationToken.None);
        Assert.Equal("AMP5206", (await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root))).DiagnosticCode);

        await fs.WriteAllTextAsync(ProjectJson(Root), "{\"formatVersion\":1,\"id\":\"x\"}", CancellationToken.None);
        Assert.Equal("AMP5205", (await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root))).DiagnosticCode);
    }

    [Fact]
    public async Task An_old_format_is_upgraded_on_a_writer_open_after_a_backup_and_refused_read_only()
    {
        (InMemoryFileSystem fs, FakeClock clock) = Fresh();
        await fs.WriteAllTextAsync(ProjectJson(Root), V0, CancellationToken.None);

        Assert.Equal("AMP5209", (await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, Root, readOnly: true))).DiagnosticCode);

        using (OpenProject project = await ProjectStore.OpenAsync(fs, clock, Root))
        {
            Assert.True(project.Migrated);
            Assert.Equal(1, project.Manifest.FormatVersion);
            Assert.Equal(12345UL, project.Manifest.MasterSeed);
            Assert.Equal("old song", project.Manifest.Name);
            Assert.Equal(ProjectSettings.Default.BlackBox, project.Manifest.Settings.BlackBox);
        }

        string backup = Assert.Single(fs.AllFiles, f => f.Contains("/backups/project.v0.", StringComparison.Ordinal));
        Assert.Equal(V0, await fs.ReadAllTextAsync(backup, CancellationToken.None));
        using OpenProject again = await ProjectStore.OpenAsync(fs, clock, Root);
        Assert.False(again.Migrated);
    }

    [Fact]
    public async Task A_failure_at_any_step_of_the_upgrade_keeps_the_project_openable_and_the_backup_intact()
    {
        for (int failAt = 1; failAt <= 14; failAt++)
        {
            (InMemoryFileSystem memory, FakeClock clock) = Fresh();
            await memory.WriteAllTextAsync(ProjectJson(Root), V0, CancellationToken.None);
            FaultyFileSystem faulty = new(memory, failAt);
            try
            {
                using OpenProject project = await ProjectStore.OpenAsync(faulty, clock, Root);
            }
            catch (IOException)
            {
            }

            string current = await memory.ReadAllTextAsync(ProjectJson(Root), CancellationToken.None);
            Assert.True(current == V0 || ProjectFile.ReadFormatVersion(current) == 1, $"failAt {failAt}: project.json is neither the old nor the new file");
            Assert.DoesNotContain(memory.AllFiles, f => f.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal));
            foreach (string backup in memory.AllFiles.Where(f => f.Contains("/backups/", StringComparison.Ordinal)))
            {
                Assert.Equal(V0, await memory.ReadAllTextAsync(backup, CancellationToken.None));
            }

            using OpenProject recovered = await ProjectStore.OpenAsync(memory, clock, Root);
            Assert.Equal(1, recovered.Manifest.FormatVersion);
            Assert.Equal(12345UL, recovered.Manifest.MasterSeed);
        }
    }

    [Fact]
    public async Task The_real_file_system_creates_opens_locks_and_releases()
    {
        string folder = Path.Combine(Directory.CreateTempSubdirectory("ample-proj-").FullName, "my song");
        try
        {
            PhysicalFileSystem fs = new();
            SystemClock clock = new();
            using (OpenProject created = await ProjectStore.CreateAsync(fs, clock, new GuidV7Generator(), folder, "my song"))
            {
                Assert.True(File.Exists(created.InAmple("project.json")));
                AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await ProjectStore.OpenAsync(fs, clock, folder));
                Assert.Equal("AMP5204", ex.DiagnosticCode);
                LockInfo? info = await InstanceLock.ReadInfoAsync(fs, folder);
                Assert.Equal(Environment.ProcessId, info?.ProcessId);
            }

            using OpenProject reopened = await ProjectStore.OpenAsync(fs, clock, folder);
            Assert.Equal("my song", reopened.Manifest.Name);
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, "a.amplet")), reopened.Resolve("a.amplet"));
            Assert.Throws<AmpleException>(() => reopened.Resolve("../outside.txt"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void A_real_link_that_leads_outside_is_refused_when_links_can_be_created()
    {
        string folder = Directory.CreateTempSubdirectory("ample-link-").FullName;
        string outside = Directory.CreateTempSubdirectory("ample-outside-").FullName;
        try
        {
            string link = Path.Combine(folder, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Equal("AMP5203", Assert.Throws<AmpleException>(() => ProjectPath.Resolve(new PhysicalFileSystem(), folder, "escape/file.txt")).DiagnosticCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}

[Trait("Category", "Project")]
public sealed class ProjectDoctorTests
{
    private static readonly string Root = Path.GetFullPath("/proj");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemoryFileSystem Fs, FakeClock Clock)> Healthy()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(Now);
        using OpenProject project = await ProjectStore.CreateAsync(fs, clock, new SequentialIdGenerator([Guid.NewGuid()]), Root, "song");
        return (fs, clock);
    }

    private static async Task<IReadOnlyList<string>> Codes(InMemoryFileSystem fs, FakeClock clock) =>
        [.. (await ProjectDoctor.CheckAsync(fs, clock, Root)).Select(f => f.Code)];

    [Fact]
    public async Task A_healthy_closed_project_has_only_the_old_heartbeat_note()
    {
        (InMemoryFileSystem fs, FakeClock clock) = await Healthy();
        Assert.Equal(["AMP5215"], await Codes(fs, clock));
    }

    [Fact]
    public async Task Missing_pieces_leftovers_unknown_entries_and_low_disk_are_reported()
    {
        (InMemoryFileSystem fs, FakeClock clock) = await Healthy();
        fs.DeleteDirectory(Path.Combine(Root, ".ample", "takes"), recursive: true);
        await fs.WriteAllTextAsync(Path.Combine(Root, ".ample", "project.json.abc" + AtomicFile.TempSuffix), "x", CancellationToken.None);
        await fs.WriteAllTextAsync(Path.Combine(Root, ".ample", "mystery.bin"), "x", CancellationToken.None);
        fs.FreeSpace = 1024;

        IReadOnlyList<string> codes = await Codes(fs, clock);
        Assert.Contains("AMP5211", codes);
        Assert.Contains("AMP5213", codes);
        Assert.Contains("AMP5212", codes);
        Assert.Contains("AMP5216", codes);
    }

    [Fact]
    public async Task A_held_lock_a_broken_file_and_a_missing_project_are_reported()
    {
        (InMemoryFileSystem fs, FakeClock clock) = await Healthy();
        using (OpenProject open = await ProjectStore.OpenAsync(fs, clock, Root))
        {
            Assert.Contains("AMP5214", await Codes(fs, clock));
        }

        await fs.WriteAllTextAsync(Path.Combine(Root, ".ample", "project.json"), "{ broken", CancellationToken.None);
        Assert.Contains("AMP5205", await Codes(fs, clock));

        Assert.Equal(["AMP5210"], [.. (await ProjectDoctor.CheckAsync(new InMemoryFileSystem(), clock, Root)).Select(f => f.Code)]);
    }

    [Fact]
    public async Task An_old_format_is_reported_as_a_warning()
    {
        InMemoryFileSystem fs = new();
        FakeClock clock = new(Now);
        await fs.WriteAllTextAsync(Path.Combine(Root, ".ample", "project.json"), "{\"id\":\"01234567-89ab-7cde-8f01-23456789abcd\",\"name\":\"o\",\"created\":\"2026-01-01T00:00:00Z\",\"seed\":1}", CancellationToken.None);
        Assert.Contains("AMP5209", await Codes(fs, clock));
    }
}
