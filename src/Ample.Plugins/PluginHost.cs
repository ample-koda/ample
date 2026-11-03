using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;

namespace Ample.Plugins;

/// <summary>Supplies the value of a named secret to plugins that declared the need. The vault (chunk 92) implements it; until then the default knows none.</summary>
public interface ISecretProvider
{
    /// <summary>Returns the secret, or null when it is not available.</summary>
    string? Lookup(string name);
}

/// <summary>A plugin found on disk.</summary>
/// <param name="Manifest">Its manifest.</param>
/// <param name="Folder">The plugin folder.</param>
public sealed record DiscoveredPlugin(PluginManifest Manifest, string Folder);

/// <summary>An analysis job for a plugin of kind <c>analysis</c>: the audio is in <c>in/audio.wav</c> of the job folder.</summary>
/// <param name="JobDir">The job folder.</param>
/// <param name="Analysis">What to analyse (tempo, key, loudness, spectrum, pitch, melody, ...).</param>
/// <param name="OptionsJson">Options as JSON.</param>
/// <param name="Timeout">How long it may take.</param>
/// <param name="OnProgress">Receives plugin progress, if supplied.</param>
public sealed record AnalysisJob(string JobDir, string Analysis, string OptionsJson, TimeSpan Timeout, Action<Ample.Audio.Engine.ProgressReport>? OnProgress);

/// <summary>A schema-validated plugin analysis and any bounded artifacts it wrote inside the private job directory.</summary>
/// <param name="Json">The validated analysis result.</param>
/// <param name="Outputs">Role and absolute path of each validated artifact.</param>
public sealed record AnalysisPluginResult(string Json, IReadOnlyList<(string Role, string Path)> Outputs);

/// <summary>A job for a plugin.</summary>
/// <param name="JobDir">The job's private folder; inputs are in <c>in/</c>, outputs must be written inside it.</param>
/// <param name="ParamsJson">The render parameters as JSON.</param>
/// <param name="Timeout">How long the render may take.</param>
/// <param name="OnProgress">Called with what the plugin reports about its progress (null: nobody listens).</param>
public sealed record PluginJob(string JobDir, string ParamsJson, TimeSpan Timeout, Action<Ample.Audio.Engine.ProgressReport>? OnProgress = null);

/// <summary>The result of a plugin render.</summary>
/// <param name="Outputs">Role and path (inside the job folder) of each output.</param>
/// <param name="MetaJson">Free metadata the plugin returned.</param>
/// <param name="Logs">Captured log lines, secrets redacted.</param>
public sealed record PluginResult(IReadOnlyList<(string Role, string Path)> Outputs, string MetaJson, IReadOnlyList<string> Logs);

/// <summary>Finds plugins and runs jobs in separate processes: a clean environment (no inherited secrets, only declared ones), a private job folder, timeouts, cancellation, crash containment, size limits, redacted logs, and one GPU job at a time.</summary>
public sealed class PluginHost(ISecretProvider? secrets = null, long maxMessageBytes = 64L * 1024 * 1024, long maxOutputBytes = 512L * 1024 * 1024) : IDisposable
{
    private static readonly string[] PassedEnvironment = ["PATH", "SystemRoot", "SYSTEMROOT", "windir", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LANG"];
    private readonly SemaphoreSlim gpu = new(1, 1);

    /// <inheritdoc />
    public void Dispose()
    {
        ReleaseWarm("the host stopped");
        pressureTimer?.Dispose();
        gpu.Dispose();
    }

    /// <summary>Gets or sets the listener for what the host does with kept-warm plugin processes (kept, reused, released and why). Only the plugin id, an event word and a reason; nothing about other processes.</summary>
    public Action<string>? OnWarmEvent { get; set; }

    /// <summary>The ids of the plugins whose process is currently kept warm, with the seconds it was idle.</summary>
    public IReadOnlyList<(string PluginId, double IdleSeconds)> WarmPlugins()
    {
        lock (warmLock)
        {
            return [.. warm.Select(w => (w.Key, (DateTime.UtcNow - w.Value.Since).TotalSeconds))];
        }
    }

    /// <summary>Stops the kept-warm plugin processes now (all of them, or one), for example when the owner asks to free the GPU memory.</summary>
    public int ReleaseWarm(string reason, string? pluginId = null)
    {
        List<(string Id, WarmProcess Entry)> taken = [];
        lock (warmLock)
        {
            foreach (string id in warm.Keys.Where(k => pluginId is null || k == pluginId).ToList())
            {
                taken.Add((id, warm[id]));
                warm.Remove(id);
            }
        }

        foreach ((string id, WarmProcess entry) in taken)
        {
            entry.Idle.Dispose();
            OnWarmEvent?.Invoke($"{id}: released ({reason})");
            _ = entry.Process.DisposeAsync().AsTask();
        }

        return taken.Count;
    }

    private sealed class WarmProcess(JsonRpcProcess process, Timer idle, DateTime since, long baselineUsedMb, int minFreeMb, int pressureMb)
    {
        public JsonRpcProcess Process { get; } = process;

        public Timer Idle { get; } = idle;

        public DateTime Since { get; } = since;

        /// <summary>Gets or sets the lowest device memory use seen since the process was kept; growth above it while idle is somebody else.</summary>
        public long BaselineUsedMb { get; set; } = baselineUsedMb;

        public int MinFreeMb { get; } = minFreeMb;

        public int PressureMb { get; } = pressureMb;
    }

    private Timer? pressureTimer;

    /// <summary>Reads the memory totals of the GPU (free and total), or null when there is no NVIDIA GPU or driver. Totals only.</summary>
    public GpuTotals? DeviceTotals() => ReadGpu();

    /// <summary>Gets or sets how often the device totals are looked at while a plugin process is kept (five seconds by default).</summary>
    public TimeSpan PressureInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the reader of the device totals (the NVML/nvidia-smi reader by default; tests replace it).</summary>
    public Func<GpuTotals?> ReadGpu { get; set; } = GpuMemory.Read;

    private readonly object warmLock = new();
    private readonly Dictionary<string, WarmProcess> warm = new(StringComparer.Ordinal);

    // The seconds the plugin asked to be kept warm (its setting keepWarmS, which only a plugin that can use it declares). 0 means start fresh every job.
    private static int KeepWarmSeconds(string paramsJson) => Math.Clamp(SettingInt(paramsJson, "keepWarmS", 0), 0, 86400);

    private static int SettingInt(string paramsJson, string name, int fallback)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(paramsJson);
            return doc.RootElement.TryGetProperty("settings", out JsonElement settings)
                && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number)
                ? number
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private JsonRpcProcess? TakeWarm(string id)
    {
        WarmProcess? entry;
        lock (warmLock)
        {
            if (!warm.Remove(id, out entry))
            {
                return null;
            }
        }

        entry.Idle.Dispose();
        if (!entry.Process.IsRunning)
        {
            _ = entry.Process.DisposeAsync().AsTask();
            return null;
        }

        OnWarmEvent?.Invoke($"{id}: reused the kept process");
        entry.Process.ClearLogs();
        return entry.Process;
    }

    private void KeepWarm(string id, JsonRpcProcess process, int seconds, string paramsJson)
    {
        Timer idle = new(_ => ReleaseIfSame(id, process, $"idle for {seconds} s"), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        WarmProcess? replaced;
        lock (warmLock)
        {
            warm.Remove(id, out replaced);
            warm[id] = new WarmProcess(process, idle, DateTime.UtcNow, ReadGpu()?.UsedMb ?? 0, Math.Max(0, SettingInt(paramsJson, "minFreeMb", 1000)), Math.Max(1, SettingInt(paramsJson, "pressureMb", 1024)));
            pressureTimer ??= new Timer(_ => CheckPressure(), null, PressureInterval, PressureInterval);
        }

        if (replaced is not null)
        {
            replaced.Idle.Dispose();
            _ = replaced.Process.DisposeAsync().AsTask();
        }

        OnWarmEvent?.Invoke($"{id}: kept for up to {seconds} s");
    }

    // While a process is kept, the device totals are looked at every few seconds: our own idle processes do not grow, so memory use that goes up
    // is another program. Only totals are read; nothing about which program it is, and nothing is logged about it.
    private void CheckPressure()
    {
        List<(string Id, WarmProcess Entry)> kept;
        lock (warmLock)
        {
            kept = [.. warm.Select(w => (w.Key, w.Value))];
            if (kept.Count == 0)
            {
                pressureTimer?.Dispose();
                pressureTimer = null;
                return;
            }
        }

        GpuTotals? totals = ReadGpu();
        if (totals is null)
        {
            return;
        }

        foreach ((string id, WarmProcess entry) in kept)
        {
            entry.BaselineUsedMb = entry.BaselineUsedMb == 0 ? totals.UsedMb : Math.Min(entry.BaselineUsedMb, totals.UsedMb);
            if (totals.FreeMb < entry.MinFreeMb)
            {
                ReleaseIfSame(id, entry.Process, $"free video memory is {totals.FreeMb} MB, below {entry.MinFreeMb} MB");
            }
            else if (totals.UsedMb - entry.BaselineUsedMb > entry.PressureMb)
            {
                ReleaseIfSame(id, entry.Process, $"video memory use grew by {totals.UsedMb - entry.BaselineUsedMb} MB while idle (limit {entry.PressureMb} MB)");
            }
        }
    }

    private void ReleaseIfSame(string id, JsonRpcProcess process, string reason)
    {
        WarmProcess? entry;
        lock (warmLock)
        {
            if (!warm.TryGetValue(id, out entry) || !ReferenceEquals(entry.Process, process))
            {
                return;
            }

            warm.Remove(id);
        }

        entry.Idle.Dispose();
        OnWarmEvent?.Invoke($"{id}: released ({reason})");
        _ = process.DisposeAsync().AsTask();
    }

    /// <summary>What the plugins accept in a song's <c>settings</c> line, read from their settings schemas (name, description, list of values, default).</summary>
    /// <param name="plugins">The plugins found.</param>
    public static IReadOnlyList<Ample.Language.SettingHint> SettingHints(IEnumerable<DiscoveredPlugin> plugins)
    {
        List<Ample.Language.SettingHint> hints = [];
        foreach (DiscoveredPlugin plugin in plugins)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(plugin.Manifest.SettingsSchemaJson);
                if (!doc.RootElement.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty property in properties.EnumerateObject())
                {
                    JsonElement p = property.Value;
                    string detail = p.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : property.Name;
                    int stop = detail.IndexOf(". ", StringComparison.Ordinal);
                    detail = stop > 0 ? detail[..(stop + 1)] : detail;
                    List<string> options = p.TryGetProperty("enum", out JsonElement e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.ToString())] : [];
                    if (options.Count == 0 && p.TryGetProperty("type", out JsonElement t) && t.GetString() == "boolean")
                    {
                        options = ["true", "false"];
                    }

                    string def = p.TryGetProperty("default", out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : string.Empty;
                    hints.Add(new Ample.Language.SettingHint(plugin.Manifest.Id + "." + property.Name, detail, options, def));
                }
            }
            catch (JsonException)
            {
                // a plugin with a broken schema is reported when it is discovered; here it just proposes nothing
            }
        }

        return hints;
    }

    /// <summary>Finds plugins in the folders (each plugin is a subfolder with a <c>plugin.json</c>); problems are returned, not thrown.</summary>
    public static (IReadOnlyList<DiscoveredPlugin> Plugins, IReadOnlyList<string> Problems) Discover(IEnumerable<string> folders)
    {
        List<DiscoveredPlugin> found = [];
        List<string> problems = [];
        foreach (string root in folders.Where(Directory.Exists))
        {
            foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                string file = Path.Combine(dir, "plugin.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    found.Add(new DiscoveredPlugin(PluginManifest.Parse(File.ReadAllText(file)), dir));
                }
                catch (AmpleException ex)
                {
                    problems.Add($"{dir}: {ex.DiagnosticCode} {ex.Message}");
                }
            }
        }

        return (found, problems);
    }

    /// <summary>Starts the plugin, does the handshake and asks for its description (toolprint and determinism).</summary>
    public async Task<JsonElement> DescribeAsync(DiscoveredPlugin plugin, CancellationToken cancellationToken = default)
    {
        await using JsonRpcProcess p = await StartAsync(plugin, cancellationToken).ConfigureAwait(false);
        return (await p.RequestAsync("describe", new { }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).Clone();
    }

    /// <summary>
    /// Runs one analysis in a fresh plugin process of kind <c>analysis</c> (request <c>analyze</c>, answer <c>{ "result": ... }</c>). A plugin that needs the GPU
    /// or the network is not started unless <paramref name="confirm"/> says yes to the question it is given (AMP5340 otherwise). The result must fit
    /// <c>spec/schema/analysis-&lt;kind&gt;.schema.json</c> (AMP5343).
    /// </summary>
    public async Task<string> AnalyzeAsync(DiscoveredPlugin plugin, AnalysisJob job, Func<string, bool>? confirm = null, CancellationToken cancellationToken = default)
        => (await AnalyzeWithOutputsAsync(plugin, job, confirm, false, cancellationToken).ConfigureAwait(false)).Json;

    /// <summary>Runs an analysis plugin and validates its result and any declared output artifacts inside the job folder.</summary>
    public async Task<AnalysisPluginResult> AnalyzeWithOutputsAsync(DiscoveredPlugin plugin, AnalysisJob job, Func<string, bool>? confirm, bool requireOutputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(job);
        PluginManifest m = plugin.Manifest;
        if (m.Kind != "analysis" || m.Analyses is null || !m.Analyses.Contains(job.Analysis))
        {
            throw new AmpleException("AMP5344", $"The plugin '{m.Id}' does not offer the analysis '{job.Analysis}'.");
        }

        if (!m.SupportsThisPlatform)
        {
            throw new AmpleException("AMP5277", $"The plugin '{m.Id}' does not support this operating system.");
        }

        if (m.Needs.Gpu || m.Needs.Network)
        {
            string what = string.Join(" and ", new[] { m.Needs.Gpu ? "the GPU" : null, m.Needs.Network ? "the network" : null }.OfType<string>());
            if (confirm is null || !confirm($"The analysis plugin '{m.Id}' wants to use {what}. Allow it for this analysis?"))
            {
                throw new AmpleException("AMP5340", $"The analysis plugin '{m.Id}' needs {what} and was not allowed to. Nothing was started.");
            }
        }

        if (m.Needs.Gpu)
        {
            await gpu.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using JsonRpcProcess p = await StartAsync(plugin, cancellationToken).ConfigureAwait(false);
            p.OnReport = job.OnProgress;
            JsonElement answer = await p.RequestAsync("analyze", new { jobDir = job.JobDir, analysis = job.Analysis, input = "in/audio.wav", options = JsonSerializer.Deserialize<object>(string.IsNullOrWhiteSpace(job.OptionsJson) ? "{}" : job.OptionsJson) }, job.Timeout, cancellationToken).ConfigureAwait(false);
            if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("result", out JsonElement result))
            {
                throw new AmpleException("AMP5344", $"The plugin '{m.Id}' did not answer with a result.");
            }

            string json = result.GetRawText();
            IReadOnlyList<string> problems = Analysis.AnalysisSchema.Validate(job.Analysis, json);
            if (problems.Count > 0) throw new AmpleException("AMP5343", $"The plugin '{m.Id}' gave a result that does not fit the schema of '{job.Analysis}': {problems[0]}");
            List<(string Role, string Path)> outputs = [];
            HashSet<string> roles = new(StringComparer.Ordinal);
            long totalOutputBytes = 0;
            if (answer.TryGetProperty("outputs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                string jobFull = Path.GetFullPath(job.JobDir);
                foreach (JsonElement output in list.EnumerateArray())
                {
                    if (outputs.Count >= 32) throw new AmpleException("AMP5274", "The analysis plugin returned too many output artifacts.");
                    string role = output.TryGetProperty("role", out JsonElement roleValue) && roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString() ?? string.Empty : string.Empty;
                    string relative = output.TryGetProperty("path", out JsonElement pathValue) && pathValue.ValueKind == JsonValueKind.String ? pathValue.GetString() ?? string.Empty : string.Empty;
                    string full = Path.GetFullPath(Path.Combine(jobFull, relative));
                    if (role.Length is < 1 or > 48 || role.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) || !roles.Add(role) || Path.IsPathRooted(relative) || !Ample.Audio.IO.ProjectPath.IsInside(jobFull, full) || full == jobFull)
                        throw new AmpleException("AMP5272", "The analysis plugin returned an invalid output path or role.");
                    if (!File.Exists(full)) throw new AmpleException("AMP5272", "The analysis plugin named an output that it did not write.");
                    for (string? current = full; current is not null && current != jobFull; current = Path.GetDirectoryName(current))
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AmpleException("AMP5272", "The analysis plugin output follows a link outside the job folder.");
                    FileInfo info = new(full);
                    totalOutputBytes = checked(totalOutputBytes + info.Length);
                    if (info.Length > maxOutputBytes || totalOutputBytes > maxOutputBytes) throw new AmpleException("AMP5274", "The analysis plugin output exceeds its size limit.");
                    outputs.Add((role, full));
                }
            }
            if (requireOutputs && outputs.Count == 0) throw new AmpleException("AMP5272", "The analysis plugin finished without output artifacts.");
            return new AnalysisPluginResult(json, outputs);
        }
        finally
        {
            if (m.Needs.Gpu)
            {
                gpu.Release();
            }
        }
    }

    /// <summary>Runs one render job in a fresh plugin process.</summary>
    public async Task<PluginResult> RenderAsync(DiscoveredPlugin plugin, PluginJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(job);
        if (!plugin.Manifest.SupportsThisPlatform)
        {
            throw new AmpleException("AMP5277", $"The plugin '{plugin.Manifest.Id}' does not support this operating system.");
        }

        bool needsGpu = plugin.Manifest.Needs.Gpu;
        if (needsGpu)
        {
            await gpu.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            int keepSeconds = needsGpu ? KeepWarmSeconds(job.ParamsJson) : 0;
            JsonRpcProcess p = (keepSeconds > 0 ? TakeWarm(plugin.Manifest.Id) : null) ?? await StartAsync(plugin, cancellationToken).ConfigureAwait(false);
            bool keep = false;
            try
            {
                p.OnReport = job.OnProgress;
                JsonElement result = await p.RequestAsync("render", JsonSerializer.Deserialize<object>(job.ParamsJson), job.Timeout, cancellationToken).ConfigureAwait(false);
                string jobFull = Path.GetFullPath(job.JobDir);
                List<(string, string)> outputs = [];
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("outputs", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement o in list.EnumerateArray())
                    {
                        string role = o.GetProperty("role").GetString() ?? string.Empty;
                        string full = Path.GetFullPath(Path.Combine(jobFull, o.GetProperty("path").GetString() ?? string.Empty));
                        if (!Ample.Audio.IO.ProjectPath.IsInside(jobFull, full) || full == jobFull)
                        {
                            throw new AmpleException("AMP5272", $"The plugin named an output outside its job folder ('{o.GetProperty("path").GetString()}').");
                        }

                        if (!File.Exists(full))
                        {
                            throw new AmpleException("AMP5272", $"The plugin named the output '{o.GetProperty("path").GetString()}' but did not write it.");
                        }

                        if (new FileInfo(full).Length > maxOutputBytes)
                        {
                            throw new AmpleException("AMP5274", $"The output '{role}' is larger than the limit of {maxOutputBytes / (1024 * 1024)} MiB.");
                        }

                        outputs.Add((role, full));
                    }
                }

                if (outputs.Count == 0)
                {
                    throw new AmpleException("AMP5272", "The plugin finished without any output.");
                }

                string meta = result.TryGetProperty("meta", out JsonElement m) ? m.GetRawText() : "{}";
                string[] logs;
                lock (p.Logs)
                {
                    logs = [.. p.Logs];
                }

                keep = keepSeconds > 0 && p.IsRunning;
                return new PluginResult(outputs, meta, logs);
            }
            finally
            {
                p.OnReport = null;
                if (keep)
                {
                    KeepWarm(plugin.Manifest.Id, p, keepSeconds, job.ParamsJson);
                }
                else
                {
                    await p.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (needsGpu)
            {
                gpu.Release();
            }
        }
    }

    private async Task<JsonRpcProcess> StartAsync(DiscoveredPlugin plugin, CancellationToken cancellationToken)
    {
        PluginManifest m = plugin.Manifest;
        Dictionary<string, string> env = [];
        foreach (string name in PassedEnvironment)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                env[name] = value;
            }
        }

        env["AMPLE_PLUGIN_ID"] = m.Id;
        env["AMPLE_PROTOCOL"] = m.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        List<string> secretValues = [];
        foreach (string secret in m.Needs.Secrets)
        {
            string? value = secrets?.Lookup(secret);
            if (value is not null)
            {
                env[secret] = value;
                secretValues.Add(value);
            }
        }

        JsonRpcProcess process = JsonRpcProcess.Start(m.Command, m.Args, plugin.Folder, env, maxMessageBytes, secretValues);
        try
        {
            JsonElement init = await process.RequestAsync("initialize", new { protocol = PluginManifest.SupportedProtocol, host = "ample" }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (init.ValueKind != JsonValueKind.Object || !init.TryGetProperty("protocol", out JsonElement proto) || proto.GetInt32() != PluginManifest.SupportedProtocol)
            {
                throw new AmpleException("AMP5271", $"The plugin '{m.Id}' does not speak protocol version {PluginManifest.SupportedProtocol}.");
            }

            return process;
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
