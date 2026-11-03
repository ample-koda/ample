using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ample.Core.Abstractions;
using Ample.Audio.IO;

namespace Ample.Storage.Projects;

/// <summary>Agent attribution stored with an instance that an agent started.</summary>
public sealed record InstanceAgent(string Name, string Reason, string? Session);

/// <summary>Last known ACE-Step health, when available.</summary>
public sealed record InstanceAce(string Url, bool Ok, DateTimeOffset Checked);

/// <summary>A running AMPLE server, without its secret launch URL.</summary>
public sealed record InstanceRecord(int Schema, string ProjectId, string Name, string Folder, int Pid, DateTimeOffset ProcessStart, int Port, DateTimeOffset Started, DateTimeOffset Updated, string Version, string Host, string StartedBy, InstanceAgent? Agent, InstanceAce? Ace);

/// <summary>Checks whether a recorded process still owns the same process identity and answers the local ping endpoint.</summary>
public interface IInstanceProbe
{
    /// <summary>Returns true only when the pid exists and its start time matches.</summary>
    bool IsCurrentProcess(int pid, DateTimeOffset processStart);

    /// <summary>Returns true when the recorded server responds with its project id.</summary>
    Task<bool> IsServerAliveAsync(InstanceRecord instance, CancellationToken cancellationToken);
}

/// <summary>Reads and maintains the per-user AMPLE server registry.</summary>
public sealed class InstanceRegistry
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string instancesFolder;
    private readonly IClock clock;
    private readonly IInstanceProbe probe;
    private readonly object writeGate = new();

    /// <summary>Creates a registry in the AMPLE home with the system process and HTTP probe.</summary>
    public InstanceRegistry(string home, IClock clock, IInstanceProbe probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(probe);
        instancesFolder = Path.Combine(Path.GetFullPath(home), "instances");
        this.clock = clock;
        this.probe = probe;
    }

    /// <summary>Creates a registry using AMPLE_HOME or the user's local application data folder.</summary>
    public static InstanceRegistry ForCurrentUser(IClock clock) => new(Ample.Storage.Library.Library.DefaultHome(), clock, new SystemInstanceProbe());

    /// <summary>Registers a server after it has started listening.</summary>
    public InstanceRecord Register(string projectId, string name, string folder, int pid, DateTimeOffset processStart, int port, string version, string startedBy, InstanceAgent? agent, InstanceAce? ace, string launchUrl)
    {
        if (!Guid.TryParseExact(projectId, "D", out Guid projectGuid)) throw new ArgumentException("The project id must be a UUID.", nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfLessThan(pid, 1);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (startedBy is not ("hub" or "cli" or "agent" or "other")) throw new ArgumentException("The start source is not supported.", nameof(startedBy));
        if (startedBy == "agent" && (agent is null || string.IsNullOrWhiteSpace(agent.Name) || string.IsNullOrWhiteSpace(agent.Reason))) throw new ArgumentException("Agent starts need a name and reason.", nameof(agent));
        if (startedBy != "agent" && agent is not null) throw new ArgumentException("Only agent starts may have agent attribution.", nameof(agent));
        if (!Uri.TryCreate(launchUrl, UriKind.Absolute, out Uri? launch) || launch.Scheme != Uri.UriSchemeHttp || launch.Port != port || !IPAddressLoopback(launch.Host) || launch.UserInfo.Length != 0 || launch.Query.Length != 0 || !launch.Fragment.StartsWith("#token=", StringComparison.Ordinal) || launch.Fragment.Length == 7) throw new ArgumentException("The launch URL must keep its token in the fragment of the registered loopback port.", nameof(launchUrl));

        Directory.CreateDirectory(instancesFolder);
        DateTimeOffset now = clock.UtcNow.ToUniversalTime();
        projectId = projectGuid.ToString("D");
        InstanceRecord record = new(1, projectId, name, Path.GetFullPath(folder), pid, processStart.ToUniversalTime(), port, now, now, version, Environment.MachineName, startedBy, agent, ace);
        try
        {
            WriteLaunch(projectId, launchUrl);
            WriteRecord(record);
        }
        catch
        {
            try { Unregister(projectId); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
        return record;
    }

    /// <summary>Refreshes the timestamp and optional ACE status for a registered instance.</summary>
    public InstanceRecord Update(string projectId, InstanceAce? ace)
    {
        lock (writeGate)   // read and write together: another update may be replacing the file right now
        {
            InstanceRecord record = Read(projectId) ?? throw new InvalidOperationException("The instance is not registered.");
            InstanceRecord updated = record with { Updated = clock.UtcNow.ToUniversalTime(), Ace = ace };
            WriteRecord(updated);
            return updated;
        }
    }

    /// <summary>Refreshes the update timestamp while preserving the last known ACE status.</summary>
    public InstanceRecord Touch(string projectId)
    {
        InstanceRecord record = Read(projectId) ?? throw new InvalidOperationException("The instance is not registered.");
        return Update(projectId, record.Ace);
    }

    /// <summary>Removes the registry and secret launch files on a clean shutdown.</summary>
    public void Unregister(string projectId)
    {
        string json = RecordPath(projectId);
        string launch = LaunchPath(projectId);
        if (File.Exists(json)) File.Delete(json);
        if (File.Exists(launch)) File.Delete(launch);
    }

    /// <summary>Lists records that pass process identity and server health checks.</summary>
    public async Task<IReadOnlyList<InstanceRecord>> ListAsync(CancellationToken cancellationToken)
    {
        List<InstanceRecord> active = [];
        foreach (InstanceRecord instance in ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probe.IsCurrentProcess(instance.Pid, instance.ProcessStart) && await probe.IsServerAliveAsync(instance, cancellationToken).ConfigureAwait(false)) active.Add(instance);
        }

        return active.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.ProjectId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Finds a record by project id or normalized project folder.</summary>
    public InstanceRecord? Find(string projectIdOrFolder) => Guid.TryParseExact(projectIdOrFolder, "D", out _) ? Read(projectIdOrFolder) : ReadAll().FirstOrDefault(i => string.Equals(i.Folder, Path.GetFullPath(projectIdOrFolder), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a project instance only when its process identity and server health are current.</summary>
    public async Task<InstanceRecord?> FindAsync(string projectIdOrFolder, CancellationToken cancellationToken)
    {
        InstanceRecord? instance = Find(projectIdOrFolder);
        return instance is not null && probe.IsCurrentProcess(instance.Pid, instance.ProcessStart) && await probe.IsServerAliveAsync(instance, cancellationToken).ConfigureAwait(false) ? instance : null;
    }

    /// <summary>Removes records whose process identity or server health check is stale.</summary>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        int removed = 0;
        foreach (InstanceRecord instance in ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool active = probe.IsCurrentProcess(instance.Pid, instance.ProcessStart) && await probe.IsServerAliveAsync(instance, cancellationToken).ConfigureAwait(false);
            if (!active)
            {
                Unregister(instance.ProjectId);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Gets the launch URL from the user-only launch file for a project.</summary>
    public string? ReadLaunchUrl(string projectId) => File.Exists(LaunchPath(projectId)) ? File.ReadAllText(LaunchPath(projectId)).TrimEnd('\r', '\n') : null;

    private string RecordPath(string id) => Path.Combine(instancesFolder, SafeId(id) + ".json");
    private string LaunchPath(string id) => Path.Combine(instancesFolder, SafeId(id) + ".launch");
    private static string SafeId(string id) => Guid.TryParseExact(id, "D", out Guid parsed) ? parsed.ToString("D") : throw new ArgumentException("The project id must be a UUID.", nameof(id));

    private InstanceRecord? Read(string projectId)
    {
        string id = SafeId(projectId);
        string path = RecordPath(id);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return ReadFile(path, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(10 * (attempt + 1));   // the file is being replaced by a writer; wait a moment and read again
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return null;
    }

    private static InstanceRecord? ReadFile(string path, string id)
    {
        InstanceRecord? instance = JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(path), Json);
        return instance is { Schema: 1, Pid: > 0, Port: >= 1 and <= 65535 } && !string.IsNullOrWhiteSpace(instance.Name) && !string.IsNullOrWhiteSpace(instance.Folder) && !string.IsNullOrWhiteSpace(instance.Version) && instance.StartedBy is "hub" or "cli" or "agent" or "other" && (instance.StartedBy == "agent" ? instance.Agent is { Name.Length: > 0, Reason.Length: > 0 } : instance.Agent is null) && Guid.TryParseExact(instance.ProjectId, "D", out Guid recorded) && recorded.ToString("D") == id ? instance : null;
    }

    private List<InstanceRecord> ReadAll()
    {
        if (!Directory.Exists(instancesFolder)) return [];
        List<InstanceRecord> found = [];
        foreach (string file in Directory.EnumerateFiles(instancesFolder, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "D", out _)) continue;
            InstanceRecord? instance = Read(id);
            if (instance is not null) found.Add(instance);
        }

        return found;
    }

    private void WriteRecord(InstanceRecord record)
    {
        lock (writeGate) AtomicFile.WriteTextAsync(new PhysicalFileSystem(), RecordPath(record.ProjectId), JsonSerializer.Serialize(record, Json) + "\n").AsTask().GetAwaiter().GetResult();
    }

    private void WriteLaunch(string projectId, string launchUrl)
    {
        string path = LaunchPath(projectId);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + AtomicFile.TempSuffix;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileSecurity security = new();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("The current user has no Windows SID.");
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                using FileStream stream = FileSystemAclExtensions.Create(new FileInfo(temp), FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
                using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false));
                writer.Write(launchUrl + "\n");
            }
            else
            {
                using FileStream stream = new(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false));
                writer.Write(launchUrl + "\n");
            }

            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool IPAddressLoopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

}

/// <summary>System-backed process and local HTTP probe for registry readers.</summary>
public sealed class SystemInstanceProbe : IInstanceProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <inheritdoc />
    public bool IsCurrentProcess(int pid, DateTimeOffset processStart)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) == processStart.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsServerAliveAsync(InstanceRecord instance, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync($"http://127.0.0.1:{instance.Port}/api/ping", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return body.RootElement.TryGetProperty("id", out JsonElement id) && id.GetString() == instance.ProjectId && body.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }
}
