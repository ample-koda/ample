using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ample.Server;

internal sealed record BraveUsageEstimate(int Limit, int Used, int Remaining, string Month);

internal static class BraveUsageTracker
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    internal static async Task<BraveUsageEstimate> ReadAsync(string libraryHome, string apiKey, int limit, DateTimeOffset now, CancellationToken ct)
    {
        string directory = Path.Combine(libraryHome, "web-search-usage");
        Directory.CreateDirectory(directory);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        string statePath = Path.Combine(directory, fingerprint + ".json");
        string lockPath = Path.Combine(directory, fingerprint + ".lock");
        string month = now.ToUniversalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await ProcessGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using FileStream lease = await AcquireLeaseAsync(lockPath, ct).ConfigureAwait(false);
            UsageState? state = File.Exists(statePath) ? JsonSerializer.Deserialize<UsageState>(await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false)) : null;
            int used = state?.Month == month ? state.Used : 0;
            return new BraveUsageEstimate(limit, used, Math.Max(0, limit - used), month);
        }
        finally { ProcessGate.Release(); }
    }

    internal static async Task<BraveUsageEstimate> ReserveAsync(string libraryHome, string apiKey, int limit, DateTimeOffset now, CancellationToken ct)
    {
        string directory = Path.Combine(libraryHome, "web-search-usage");
        Directory.CreateDirectory(directory);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        string statePath = Path.Combine(directory, fingerprint + ".json");
        string lockPath = Path.Combine(directory, fingerprint + ".lock");
        string month = now.ToUniversalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        await ProcessGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using FileStream lease = await AcquireLeaseAsync(lockPath, ct).ConfigureAwait(false);
            UsageState state = File.Exists(statePath)
                ? JsonSerializer.Deserialize<UsageState>(await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false)) ?? new UsageState(month, 0)
                : new UsageState(month, 0);
            int used = state.Month == month ? state.Used : 0;
            if (used >= limit) return new BraveUsageEstimate(limit, used, 0, month);
            used++;
            string temporaryPath = Path.Combine(directory, fingerprint + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new UsageState(month, used)), ct).ConfigureAwait(false);
            File.Move(temporaryPath, statePath, true);
            return new BraveUsageEstimate(limit, used, Math.Max(0, limit - used), month);
        }
        finally { ProcessGate.Release(); }
    }

    private static async Task<FileStream> AcquireLeaseAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 600) { await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); }
        }
    }

    private sealed record UsageState(string Month, int Used);
}
