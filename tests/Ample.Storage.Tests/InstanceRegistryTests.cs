using System.Security.AccessControl;
using System.Security.Principal;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

public sealed class InstanceRegistryTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-registry-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProbe probe = new();
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly string projectId = Guid.NewGuid().ToString("D");
    private static readonly string[] ServeArgs = { "serve", "--port", "54321" };

    [Fact]
    public void Register_writes_schema_record_and_private_launch_file()
    {
        InstanceRegistry registry = new(home, clock, probe);

        InstanceRecord record = registry.Register(projectId, "demo", home, 42, clock.UtcNow.AddMinutes(-1), 54321, "0.1.0", "cli", null, null, "http://127.0.0.1:54321/#token=secret-marker");

        Assert.Equal(1, record.Schema);
        string folder = Path.Combine(home, "instances");
        string json = File.ReadAllText(Path.Combine(folder, projectId + ".json"));
        Assert.Contains("\"schema\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-marker", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"agent\"", json, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:54321/#token=secret-marker", registry.ReadLaunchUrl(projectId));
        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = FileSystemAclExtensions.GetAccessControl(new FileInfo(Path.Combine(folder, projectId + ".launch")));
            Assert.True(security.AreAccessRulesProtected);
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()) Assert.Equal(currentUser, rule.IdentityReference);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, projectId + ".launch")));
        }
    }

    [Fact]
    public void Relaunch_recipe_and_the_no_hub_flag_round_trip_and_the_launch_token_stays_out_of_the_record()
    {
        InstanceRegistry registry = new(home, clock, probe);

        registry.Register(projectId, "demo", home, 42, clock.UtcNow, 54321, "0.1.0", "cli", null, null, "http://127.0.0.1:54321/#token=secret-marker", new InstanceRelaunch("C:/ample/ample.exe", ServeArgs), true);

        InstanceRecord read = registry.Find(projectId)!;
        Assert.Equal("C:/ample/ample.exe", read.Relaunch!.Exe);
        Assert.Equal(ServeArgs, read.Relaunch.Args);
        Assert.True(read.NoHub);
        Assert.DoesNotContain("secret-marker", File.ReadAllText(Path.Combine(home, "instances", projectId + ".json")), StringComparison.Ordinal);
    }

    [Fact]
    public void An_older_record_without_a_recipe_still_reads_and_the_hub_may_manage_it()
    {
        InstanceRegistry registry = new(home, clock, probe);
        registry.Register(projectId, "demo", home, 42, clock.UtcNow, 54321, "0.1.0", "cli", null, null, "http://127.0.0.1:54321/#token=x");

        InstanceRecord read = registry.Find(projectId)!;
        Assert.Null(read.Relaunch);
        Assert.False(read.NoHub);
    }

    [Fact]
    public void Agent_record_requires_attribution_and_round_trips_it()
    {
        InstanceRegistry registry = new(home, clock, probe);

        Assert.Throws<ArgumentException>(() => registry.Register(projectId, "demo", home, 42, clock.UtcNow, 54321, "0.1.0", "agent", null, null, "http://127.0.0.1:54321/#token=x"));
        InstanceRecord record = registry.Register(projectId, "demo", home, 42, clock.UtcNow, 54321, "0.1.0", "agent", new("Codex", "run tests", null), null, "http://127.0.0.1:54321/#token=x");

        Assert.Equal("Codex", registry.Find(projectId)!.Agent!.Name);
        Assert.Equal(record, registry.Find(home));
    }

    [Fact]
    public async Task List_filters_inactive_and_cleanup_removes_only_stale_records()
    {
        InstanceRegistry registry = new(home, clock, probe);
        string deadId = Guid.NewGuid().ToString("D");
        string reusedId = Guid.NewGuid().ToString("D");
        string silentId = Guid.NewGuid().ToString("D");
        registry.Register(projectId, "active", home, 41, clock.UtcNow, 54321, "0.1.0", "cli", null, null, "http://127.0.0.1:54321/#token=a");
        registry.Register(deadId, "dead", home, 42, clock.UtcNow, 54322, "0.1.0", "cli", null, null, "http://127.0.0.1:54322/#token=b");
        registry.Register(reusedId, "reused", home, 43, clock.UtcNow, 54323, "0.1.0", "cli", null, null, "http://127.0.0.1:54323/#token=c");
        registry.Register(silentId, "silent", home, 44, clock.UtcNow, 54324, "0.1.0", "cli", null, null, "http://127.0.0.1:54324/#token=d");
        probe.ActiveProcesses[41] = clock.UtcNow;
        probe.ActiveProcesses[43] = clock.UtcNow.AddSeconds(1);
        probe.ActiveProcesses[44] = clock.UtcNow;
        probe.ActivePorts.Add(54321);
        probe.ActivePorts.Add(54323);

        Assert.Equal("active", Assert.Single(await registry.ListAsync(CancellationToken.None)).Name);
        Assert.Equal("active", (await registry.FindAsync(projectId, CancellationToken.None))!.Name);
        Assert.Null(await registry.FindAsync(deadId, CancellationToken.None));
        Assert.Null(await registry.FindAsync(reusedId, CancellationToken.None));
        Assert.Null(await registry.FindAsync(silentId, CancellationToken.None));
        Assert.Equal(3, await registry.CleanupAsync(CancellationToken.None));
        Assert.NotNull(registry.Find(projectId));
        Assert.Null(registry.Find(deadId));
        Assert.Null(registry.Find(reusedId));
        Assert.Null(registry.Find(silentId));
        Assert.False(File.Exists(Path.Combine(home, "instances", deadId + ".launch")));
    }

    [Fact]
    public async Task Concurrent_atomic_updates_leave_a_complete_record()
    {
        InstanceRegistry registry = new(home, clock, probe);
        registry.Register(projectId, "demo", home, 41, clock.UtcNow, 54321, "0.1.0", "cli", null, null, "http://127.0.0.1:54321/#token=a");

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => registry.Update(projectId, null))));

        InstanceRecord? read = registry.Find(projectId);
        Assert.NotNull(read);
        Assert.Equal(projectId, read.ProjectId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(home, "instances"), "*.ample-tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }

    private sealed class FakeProbe : IInstanceProbe
    {
        public Dictionary<int, DateTimeOffset> ActiveProcesses { get; } = [];
        public HashSet<int> ActivePorts { get; } = [];
        public bool IsCurrentProcess(int pid, DateTimeOffset processStart) => ActiveProcesses.TryGetValue(pid, out DateTimeOffset start) && start == processStart;
        public Task<bool> IsServerAliveAsync(InstanceRecord instance, CancellationToken cancellationToken) => Task.FromResult(ActivePorts.Contains(instance.Port));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
