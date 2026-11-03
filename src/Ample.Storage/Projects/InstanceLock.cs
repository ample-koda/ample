using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.IO;

namespace Ample.Storage.Projects;

/// <summary>Who holds (or last held) the project lock, from the heartbeat file.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="Machine">Machine name.</param>
/// <param name="Started">When the lock was taken.</param>
/// <param name="Updated">Last heartbeat.</param>
public sealed record LockInfo(int ProcessId, string Machine, DateTimeOffset Started, DateTimeOffset Updated);

/// <summary>One writer per project: an operating-system lock on <c>.ample/locks/instance.lock</c> plus a heartbeat file that says who holds it. A held lock is never broken automatically; taking over is a dangerous act decided by the user.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly IFileSystem fs;
    private readonly IClock clock;
    private readonly string infoPath;
    private readonly IDisposable handle;
    private readonly DateTimeOffset started;

    private InstanceLock(IFileSystem fs, IClock clock, string infoPath, IDisposable handle)
    {
        this.fs = fs;
        this.clock = clock;
        this.infoPath = infoPath;
        this.handle = handle;
        started = clock.UtcNow;
    }

    /// <summary>Takes the lock or throws AMP5204 saying who holds it.</summary>
    public static async ValueTask<InstanceLock> AcquireAsync(IFileSystem fs, IClock clock, string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(clock);
        string locks = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "locks");
        fs.CreateDirectory(locks);
        string infoPath = Path.Combine(locks, "instance.json");
        IDisposable? handle = fs.TryLockFile(Path.Combine(locks, "instance.lock"));
        if (handle is null)
        {
            LockInfo? holder = await ReadInfoAsync(fs, projectRoot, cancellationToken).ConfigureAwait(false);
            string who = holder is null ? "another AMPLE instance" : $"process {holder.ProcessId.ToString(CultureInfo.InvariantCulture)} on {holder.Machine} (since {holder.Started:u})";
            throw new AmpleException("AMP5204", $"The project is open in {who}. Open it read-only, or close the other instance.");
        }

        InstanceLock result = new(fs, clock, infoPath, handle);
        try
        {
            await result.TouchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return result;
    }

    /// <summary>Reads the heartbeat file, or returns null when there is none or it cannot be read.</summary>
    public static async ValueTask<LockInfo?> ReadInfoAsync(IFileSystem fs, string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        string path = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "locks", "instance.json");
        if (!fs.FileExists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(await fs.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            JsonElement r = doc.RootElement;
            return new LockInfo(r.GetProperty("pid").GetInt32(), r.GetProperty("machine").GetString() ?? string.Empty, DateTimeOffset.Parse(r.GetProperty("started").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(r.GetProperty("updated").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes a fresh heartbeat.</summary>
    public async ValueTask TouchAsync(CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(new { pid = Environment.ProcessId, machine = Environment.MachineName, started = started.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), updated = clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        await AtomicFile.WriteTextAsync(fs, infoPath, json + "\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the lock (the heartbeat file stays; its age tells the doctor the holder is gone).</summary>
    public void Dispose() => handle.Dispose();
}
