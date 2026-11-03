using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Plugins;

/// <summary>
/// The local manager of the ACE-Step server on this machine ("Faer, the local spark"): a build takes a lease, the server is started when no one answers on its port,
/// and it is stopped again a while after the last lease ended. It only stops a server it started itself, found by the process it got, never by name or command line.
/// Hosts on this machine list their leases as small files (with their process id) in one folder, so the host that started the server does not stop it while another live host still uses it.
/// </summary>
public sealed class FaerSpark : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<TimeSpan> linger;
    private readonly string leaseFolder;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly EventHandler exitHandler;
    private int leases;
    private bool exitHooked;
    private bool disposed;
    private Process? owned;
    private CancellationTokenSource? stopTimer;

    /// <summary>Initializes a manager for one executable, models folder and port.</summary>
    /// <param name="exe">The ace-server executable, or empty when this machine has none configured.</param>
    /// <param name="models">The folder with the model files.</param>
    /// <param name="port">The loopback port.</param>
    /// <param name="linger">How long the server stays after the last lease ended.</param>
    /// <param name="leaseFolder">The folder where hosts on this machine list their leases, or null for the <c>spark</c> folder in the AMPLE home.</param>
    public FaerSpark(string exe, string models, int port, Func<TimeSpan>? linger = null, string? leaseFolder = null)
    {
        Exe = exe;
        Models = models;
        Port = port;
        this.linger = linger ?? (() => TimeSpan.FromMinutes(2));
        this.leaseFolder = leaseFolder ?? Path.Combine(AmpleHome(), "spark");
        exitHandler = (_, _) => StopOwnedUnlessUsed();
    }

    /// <summary>Gets the manager of this machine, configured from ACE_EXE, ACE_MODELS and ACE_PORT or from <c>settings.json</c> (ace.exe, ace.models, ace.port) in the AMPLE home.</summary>
    public static FaerSpark Local { get; } = FromEnvironment();

    /// <summary>Gets the executable that is started.</summary>
    public string Exe { get; }

    /// <summary>Gets the folder with the model files.</summary>
    public string Models { get; }

    /// <summary>Gets the loopback port.</summary>
    public int Port { get; }

    /// <summary>Gets a value indicating whether this machine can start a server (an existing executable and models folder are configured).</summary>
    public bool CanStart => Exe.Length > 0 && File.Exists(Exe) && Directory.Exists(Models);

    /// <summary>Gets a value indicating whether a server that this manager started is running.</summary>
    public bool Owns => owned is { HasExited: false };

    /// <summary>Gets the address of the server this manager keeps.</summary>
    public Uri Endpoint => new($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}");

    private string LeaseFile => Path.Combine(leaseFolder, $"lease-{Port.ToString(CultureInfo.InvariantCulture)}-{id}.json");

    /// <summary>Tells whether a build setting says the song uses the local server on this port (a missing endpoint or backend means the plugin default).</summary>
    /// <param name="settings">The settings of the song, as written in its settings line.</param>
    public bool Serves(IReadOnlyDictionary<string, string>? settings)
    {
        string backend = settings is not null && settings.TryGetValue("backend", out string? b) ? b : "server";
        if (backend == "cli")
        {
            return false;
        }

        return settings is null || !settings.TryGetValue("endpoint", out string? e) || (Uri.TryCreate(e, UriKind.Absolute, out Uri? uri) && uri.IsLoopback && uri.Port == Port);
    }

    /// <summary>Takes a lease: the server is running when this returns. A server that answers already (started by someone else) is used and never stopped.</summary>
    /// <param name="cancellationToken">Cancels the wait for the server to come up.</param>
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) => AcquireAsync(null, cancellationToken);

    /// <summary>Takes a lease like <see cref="AcquireAsync(CancellationToken)"/> and tells through <paramref name="report"/> when it really starts (or waits for) a server.</summary>
    /// <param name="cancellationToken">Cancels the wait for the server to come up.</param>
    /// <param name="report">Receives a short status line when a server is started or waited for.</param>
    public async Task<IAsyncDisposable> AcquireAsync(Action<string>? report, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stopTimer?.Cancel();
            stopTimer?.Dispose();
            stopTimer = null;
            // The lease is written before the server is looked at, so a host that owns the server sees this one and does not stop it under it.
            WriteLeaseFile();
            try
            {
                if (!await AnswersAsync(cancellationToken).ConfigureAwait(false))
                {
                    await StartOrJoinAsync(report, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (leases == 0)
                {
                    DeleteLeaseFile();
                }

                throw;
            }

            leases++;
            return new Lease(this);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        stopTimer?.Cancel();
        stopTimer?.Dispose();
        stopTimer = null;
        StopOwnedUnlessUsed();
        DeleteLeaseFile();
        gate.Dispose();
    }

    /// <summary>Lists the model files the running server offers (its <c>/props</c>), or null when it does not say.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<IReadOnlyList<string>?> ModelFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument props = JsonDocument.Parse(await Http.GetStringAsync(new Uri(Endpoint, "/props"), cancellationToken).ConfigureAwait(false));
            return props.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Object
                ? [.. models.EnumerateObject().Where(k => k.Value.ValueKind == JsonValueKind.Array).SelectMany(k => k.Value.EnumerateArray()).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Tells whether another live holder (a lease of another manager, in this or another process) uses the server on this port; leases of dead processes are removed.</summary>
    public bool OthersHoldLease()
    {
        try
        {
            if (!Directory.Exists(leaseFolder))
            {
                return false;
            }

            bool any = false;
            string mine = Path.GetFileName(LeaseFile);
            foreach (string file in Directory.EnumerateFiles(leaseFolder, $"lease-{Port.ToString(CultureInfo.InvariantCulture)}-*.json"))
            {
                if (string.Equals(Path.GetFileName(file), mine, StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsAlive(file))
                {
                    any = true;
                }
                else
                {
                    TryDelete(file);
                }
            }

            return any;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAlive(string leaseFile)
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(leaseFile).Trim(), CultureInfo.InvariantCulture, out int pid))
            {
                return false;
            }

            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another host removed it or it is in use: a stale file is ignored by its dead process id anyway.
        }
    }

    private static string AmpleHome() =>
        Environment.GetEnvironmentVariable("AMPLE_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ample");

    private static FaerSpark FromEnvironment()
    {
        string exe = Environment.GetEnvironmentVariable("ACE_EXE") ?? string.Empty, models = Environment.GetEnvironmentVariable("ACE_MODELS") ?? string.Empty;
        int port = int.TryParse(Environment.GetEnvironmentVariable("ACE_PORT"), CultureInfo.InvariantCulture, out int p) ? p : 0;
        string settings = Path.Combine(AmpleHome(), "settings.json");
        if (File.Exists(settings))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("ace", out JsonElement ace) && ace.ValueKind == JsonValueKind.Object)
                {
                    exe = exe.Length > 0 ? exe : ace.TryGetProperty("exe", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : string.Empty;
                    models = models.Length > 0 ? models : ace.TryGetProperty("models", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
                    port = port > 0 ? port : ace.TryGetProperty("port", out JsonElement q) && q.TryGetInt32(out int n) ? n : 0;
                }
            }
            catch (JsonException)
            {
                // A settings file that cannot be read means nothing is configured.
            }
        }

        return new FaerSpark(exe, models, port > 0 ? port : 8085);
    }

    // Called with the gate held and nothing answering: waits for the server this manager already started (still loading), else starts one. Any failure stops what was started.
    private async Task StartOrJoinAsync(Action<string>? report, CancellationToken cancellationToken)
    {
        if (owned is not { HasExited: false })
        {
            if (!CanStart)
            {
                throw new AmpleException("AMP5381", "No ACE-Step server answers on port " + Port.ToString(CultureInfo.InvariantCulture) + ", and this machine has none to start: set ace.exe and ace.models in the AMPLE settings.json (or ACE_EXE and ACE_MODELS).");
            }

            report?.Invoke("Starting the local ACE-Step server");
            StopOwned();
            owned = Process.Start(new ProcessStartInfo(Exe, $"--models \"{Models}\" --host 127.0.0.1 --port {Port.ToString(CultureInfo.InvariantCulture)}") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Exe)! })
                ?? throw new AmpleException("AMP5381", "The ACE-Step server could not be started.");
            // A host that ends (the command line after its build, or the server being closed) takes the server it started with it, unless another live host still uses it; a lingering timer would not survive it.
            if (!exitHooked)
            {
                exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += exitHandler;
            }
        }
        else
        {
            report?.Invoke("Waiting for the local ACE-Step server that is still starting");
        }

        try
        {
            DateTime until = DateTime.UtcNow.AddMinutes(3);
            while (!await AnswersAsync(cancellationToken).ConfigureAwait(false))
            {
                Process? process = owned;
                if (process is null || process.HasExited || DateTime.UtcNow > until)
                {
                    throw new AmpleException("AMP5381", process is null || process.HasExited ? "The ACE-Step server stopped while it was starting." : "The ACE-Step server did not answer within 3 minutes of starting.");
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Nobody holds a lease on a server that failed to come up, and nobody else would stop it.
            StopOwned();
            throw;
        }
    }

    private async Task ReleaseAsync()
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (--leases > 0)
            {
                return;
            }

            DeleteLeaseFile();
            if (Owns)
            {
                ArmStop();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // Called with the gate held: stops the owned server after the linger, unless a lease came in or another live host still uses it (then it waits another round).
    private void ArmStop()
    {
        stopTimer?.Cancel();
        stopTimer?.Dispose();
        stopTimer = new CancellationTokenSource();
        CancellationToken token = stopTimer.Token;
        TimeSpan wait = linger();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (leases == 0 && !token.IsCancellationRequested)
                    {
                        if (OthersHoldLease())
                        {
                            ArmStop();
                        }
                        else
                        {
                            StopOwned();
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // A new lease came in before the wait ended (the server stays), or the manager was disposed (which stopped the server).
            }
        }, CancellationToken.None);
    }

    private void StopOwnedUnlessUsed()
    {
        if (!OthersHoldLease())
        {
            StopOwned();
        }
    }

    private void StopOwned()
    {
        Process? process = Interlocked.Exchange(ref owned, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It ended by itself.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void WriteLeaseFile()
    {
        try
        {
            Directory.CreateDirectory(leaseFolder);
            File.WriteAllText(LeaseFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file other hosts cannot see this lease; the build itself still works.
        }
    }

    private void DeleteLeaseFile() => TryDelete(LeaseFile);

    // A refused connection means down. A probe that times out means something holds the port but is busy (a server loading a model): after a few tries that counts as up, never as down.
    private async Task<bool> AnswersAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(new Uri(Endpoint, "/props"), cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out: unknown, ask again.
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Lease(FaerSpark spark) : IAsyncDisposable
    {
        private int released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                await spark.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }
}
