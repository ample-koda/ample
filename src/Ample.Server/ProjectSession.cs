using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Storage.BlackBox;
using Ample.Storage.History;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Docs;
using Ample.Language;
using Ample.Plugins;

namespace Ample.Server;

/// <summary>An open document: the text the editor works on (not yet saved) and its version.</summary>
public sealed class DocSession
{
    internal DocSession(string path, string text)
    {
        Path = path;
        Text = text;
        Saved = text;
    }

    /// <summary>Gets the project-relative path.</summary>
    public string Path { get; }

    /// <summary>Gets or sets the working text.</summary>
    public string Text { get; internal set; }

    /// <summary>Gets the text last written to disk.</summary>
    public string Saved { get; internal set; }

    /// <summary>Gets the version, increased by every change.</summary>
    public int Version { get; internal set; } = 1;
}

internal sealed class ModuleDocState(DocSession document, string source)
{
    internal DocSession Document { get; } = document;
    internal string Source { get; set; } = source;
}

/// <summary>An act waiting for its typed acceptance (the second step of the two-step contract).</summary>
internal sealed record PendingAct(string Title, string Acceptance, DateTimeOffset Expires, Func<string, string, Task> Run);

/// <summary>Everything the server holds for the one open project: the writer lock, journal, takebank, history, control, engine and document sessions.</summary>
public sealed class ProjectSession : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DocSession> docs = new(StringComparer.Ordinal);
    private WorkingTexts? texts;

    /// <summary>The songs whose last build was a preview build (it used unsaved text, or left out blocks with errors): its takes cannot be frozen, locked or promoted until a clean build.</summary>
    internal ConcurrentDictionary<string, bool> PreviewBuilds { get; } = new(StringComparer.Ordinal);

    /// <summary>The working copies of the text files that are not songs (components, lyrics, sequences, text).</summary>
    internal WorkingTexts Texts => texts ??= new WorkingTexts(this);
    private readonly ConcurrentDictionary<string, ModuleDocState> moduleDocs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingAct> acts = new();
    private readonly PhysicalFileSystem fs = new();
    private readonly SystemClock clock = new();
    private readonly OpenProject project;
    private readonly PluginHost pluginHost;
    private IReadOnlyList<IBlockRenderer>? pluginRenderers;
    private Func<CommandContext, CancellationToken, ValueTask>? backupHook;

    private ProjectSession(OpenProject project, Journal journal, CommandContext context, CommandBus bus, Takebank bank, SpecData spec, HistoryService history, BuildEngine engine, ControlService control, string ampleRoot, EventBus events, Ample.Storage.Library.Library library, ISecretProvider? secrets)
    {
        pluginHost = new PluginHost(secrets);
        pluginHost.OnWarmEvent = LogGpu;
        this.project = project;
        Library = library;
        Journal = journal;
        Context = context;
        Bus = bus;
        Bank = bank;
        Spec = spec;
        History = history;
        Engine = engine;
        Control = control;
        AmpleRoot = ampleRoot;
        Events = events;
    }

    internal Ample.Storage.Library.Library Library { get; }

    internal AutoCheckpoint? AutoCheckpoint { get; set; }

    private AiService? ai;

    /// <summary>The AI assistant of this project (Codex proposes, AMPLE validates, the user decides).</summary>
    internal AiService Ai => ai ??= new AiService(this);

    /// <summary>The open songs with changes that are not saved.</summary>
    internal IReadOnlyList<DocSession> DirtySongs() => docs.Values.Where(d => d.Text != d.Saved).ToList();

    /// <summary>When the project opted in, a backup is made before every dangerous act and a failing backup stops the act.</summary>
    internal void InstallBackupHook()
    {
        Ample.Storage.Backup.BackupStatus? status = Ample.Storage.Backup.BackupPolicy.Read(Root, clock);
        if (status is not { Auto: true })
        {
            return;
        }

        Ample.Language.ServerCapabilities.Project = Manifest.Settings.Capabilities is { Count: > 0 } projectCapabilities ? projectCapabilities : null;
        string root = Root;
        backupHook = (c, ct) =>
        {
            if (c.ProjectRoot != root)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                Ample.Storage.Backup.BackupReport r = new Ample.Storage.Backup.BackupStore(status.Target, clock).Create(root, Manifest.Id, Manifest.Name, true, ct);
                Ample.Storage.Backup.BackupPolicy.Remember(root, status.Target, r.Id, clock.UtcNow);
                return ValueTask.CompletedTask;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmpleException)
            {
                throw new AmpleException("AMP5305", "The backup before the act failed, so the act did not run: " + ex.Message);
            }
        };
        Ample.Storage.BlackBox.DangerousAct.BeforeExecute = backupHook;
    }

    internal Journal Journal { get; }

    internal CommandContext Context { get; }

    internal CommandBus Bus { get; }

    internal Takebank Bank { get; }

    internal SpecData Spec { get; }

    internal HistoryService History { get; }

    internal BuildEngine Engine { get; }

    internal ControlService Control { get; }

    internal EventBus Events { get; }

    internal string AmpleRoot { get; }

    internal string Root => project.Root;

    internal ProjectManifest Manifest => project.Manifest;

    internal PhysicalFileSystem Fs => fs;

    internal SystemClock Clock => clock;

    internal static async Task<ProjectSession> OpenAsync(ServerOptions options, EventBus events, CancellationToken cancellationToken)
    {
        PhysicalFileSystem fs = new();
        SystemClock clock = new();
        OpenProject project = await ProjectStore.OpenAsync(fs, clock, options.ProjectRoot, readOnly: false, cancellationToken).ConfigureAwait(false);
        try
        {
            SpecData spec = SpecRoot.At(options.AmpleRoot).LoadSpecData();
            Journal journal = await Journal.OpenAsync(fs, clock, project.Root, project.Manifest.Id.ToString("D"), cancellationToken: cancellationToken).ConfigureAwait(false);
            CommandContext context = new(fs, project.Root, clock);
            CommandBus bus = new(journal, context, "ui");
            Takebank bank = Takebank.Open(fs, clock, project.Root);
            HistoryService history = await HistoryService.OpenAsync(fs, clock, project.Root, bank, bus, cancellationToken).ConfigureAwait(false);
            BuildEngine engine = new(bank, spec, project.Manifest.Id, project.Root, options.AmpleRoot);
            ControlService control = new(clock, project.Root, bank, engine, bus, project.Manifest.MasterSeed);
            ProjectSession session = new(project, journal, context, bus, bank, spec, history, engine, control, options.AmpleRoot, events, Ample.Storage.Library.Library.Open(options.LibraryHome ?? Ample.Storage.Library.Library.DefaultHome(), clock), VaultSecretProvider.FromEnvironment(options.LibraryHome ?? Ample.Storage.Library.Library.DefaultHome()));
            session.InstallBackupHook();
            session.AutoCheckpoint = new AutoCheckpoint(session);
            return session;
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    internal async Task<DocSession> DocAsync(string relative, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(Root, relative);
        if (docs.TryGetValue(relative, out DocSession? existing))
        {
            return existing;
        }

        if (!File.Exists(full))
        {
            throw new AmpleException("AMP5243", $"There is no file '{relative}' in the project.");
        }

        return docs.GetOrAdd(relative, new DocSession(relative, await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>The clock (the instant currentTime() read) of the last build of each song, so its dynamic values can be baked afterwards.</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastBuildAt { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads a text file of the project (a bake stage keeps its notes in one), or null when it is not there.</summary>
    internal string? ReadProjectText(string relative)
    {
        try
        {
            string full = ProjectPath.Resolve(Root, relative);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch (AmpleException)
        {
            return null;
        }
    }

    internal AmpleDocument Parse(DocSession session)
    {
        ParseOptions options = ParseOptions.Editor;
        options.ReadFile = ReadProjectText;
        options.Preflight = (blocks, lines) => Ample.Audio.Engine.DrumPreflight.Check(Root, blocks, lines);
        // the components the song imports are block types of the editors too (their problems are shown at the import line)
        (IReadOnlyList<ComponentDefinition> components, IReadOnlyList<Diagnostic> extra) = new Ample.Engine.Modules.ModuleService(Root, () => Library, Manifest.Id.ToString("D")).ComponentsForEditors(session.Text, Spec);
        options.Components = components;
        options.ExtraDiagnostics = extra;
        return Parser.Parse(session.Text, Spec, options);
    }

    internal async Task<ModuleDocState> ModuleDocAsync(string relative, CancellationToken cancellationToken)
    {
        relative = relative.Replace('\\', '/');
        if (!relative.StartsWith("modules/", StringComparison.Ordinal) || !relative.EndsWith(Ample.Engine.Modules.ModuleService.Extension, StringComparison.Ordinal) || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new AmpleException("AMP5320", "Module editing is limited to project modules/*.module files.");
        if (moduleDocs.TryGetValue(relative, out ModuleDocState? cached)) return cached;
        string full = ProjectPath.Resolve(Root, relative);
        if (!File.Exists(full)) throw new AmpleException("AMP5320", "The project module is missing.");
        string source = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        ModuleDefinition definition = ModuleDefinition.Parse(source);
        if (definition.Problems.Count > 0) throw new AmpleException("AMP5326", "The project module source is malformed.");
        ModuleDocState created = new(new DocSession(relative, definition.Body), source);
        return moduleDocs.GetOrAdd(relative, created);
    }

    internal static void UpdateModuleDoc(ModuleDocState state, string body)
    {
        // The body is the file with its declaration lines (module, param, input, output) blanked, so line numbers stay: the new source is the new body with
        // those declaration lines put back on their own line numbers. (Joining a prefix and the rest with an extra blank line moved the blocks down by one
        // line at every update, and the next update then cut the first block header off.)
        string[] original = state.Source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] blanked = ModuleDefinition.Parse(state.Source).Body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> merged = [.. body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        for (int i = 0; i < original.Length && i < blanked.Length; i++)
        {
            if (blanked[i].Trim().Length > 0 || original[i].Trim().Length == 0)
            {
                continue;
            }

            while (merged.Count <= i)
            {
                merged.Add(string.Empty);
            }

            merged[i] = original[i];
        }

        state.Source = string.Join('\n', merged);
        state.Document.Text = body;
        state.Document.Version++;
    }

    internal AmpleDocument ParseModule(DocSession session) => Parser.Parse(session.Text, Spec, new ParseOptions { RequireProjectLine = false });

    internal IReadOnlyDictionary<string, string> ModuleSources()
    {
        Dictionary<string, string> sources = new(StringComparer.Ordinal);
        foreach ((string path, ModuleDocState state) in moduleDocs)
        {
            lock (state) sources[path] = state.Source;
        }
        return sources;
    }

    /// <summary>Parses a song for building: module instances are expanded into their blocks.</summary>
    internal AmpleDocument ParseExpanded(DocSession session) => new Ample.Engine.Modules.ModuleService(Root, () => Library, Manifest.Id.ToString("D")).ParseExpanded(session.Text, Spec);

    internal void ForgetDocs() => docs.Clear();

    /// <summary>What comparing an open song with its file on disk found: same, reloaded (the working copy had no changes, so it took the file), merged (both changed, in different places: the working copy now has both), conflict (both changed the same place: the person chooses), gone (the file is deleted).</summary>
    internal sealed record DiskSync(string State, string? DiskText);

    /// <summary>Brings the working copy in line with a change of the file on disk, so a later save cannot silently overwrite it. Only a real conflict is left for the person.</summary>
    internal async Task<DiskSync> SyncDiskAsync(DocSession d, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(Root, d.Path);
        if (!File.Exists(full))
        {
            return new DiskSync("gone", null);
        }

        string disk = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        lock (d)
        {
            if (disk == d.Saved)
            {
                return new DiskSync("same", disk);
            }

            if (d.Text == d.Saved)
            {
                d.Text = disk;
                d.Saved = disk;
                d.Version++;
                return new DiskSync("reloaded", disk);
            }

            string? merged = AiDiff.Merge(d.Saved, d.Text, disk, out _);
            if (merged is null)
            {
                return new DiskSync("conflict", disk);
            }

            d.Saved = disk;
            d.Text = merged;
            d.Version++;
            return new DiskSync("merged", disk);
        }
    }

    /// <summary>The person's answer to a conflict: the file on disk is now the base (as long as it is still the one they saw), the working copy is their composed text, ready to save.</summary>
    internal async Task ResolveDiskAsync(DocSession d, string text, string diskSha, CancellationToken cancellationToken)
    {
        string full = ProjectPath.Resolve(Root, d.Path);
        string disk = File.Exists(full) ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false) : string.Empty;
        if (!string.Equals(DiskSha(disk), diskSha, StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5307", "The file on disk changed again while you were choosing; look at the differences again.");
        }

        lock (d)
        {
            d.Saved = disk;
            d.Text = text;
            d.Version++;
        }
    }

    internal static string DiskSha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Whether a song or module of the project has changes that are not saved (an open working copy that differs from the file).</summary>
    /// <summary>The unsaved text of an open song, or null when it is not open or has no unsaved changes.</summary>
    internal string? DirtyDocText(string relative) => docs.TryGetValue(relative, out DocSession? d) && d.Text != d.Saved ? d.Text : null;

    internal bool IsDocDirty(string relative) => (docs.TryGetValue(relative, out DocSession? d) && d.Text != d.Saved) || (moduleDocs.TryGetValue(relative, out ModuleDocState? m) && m.Document.Text != m.Document.Saved);

    /// <summary>All songs and modules of the project that have unsaved changes in an open working copy.</summary>
    internal IReadOnlyList<string> DirtyDocs() => [.. docs.Where(x => x.Value.Text != x.Value.Saved).Select(x => x.Key).Concat(moduleDocs.Where(x => x.Value.Document.Text != x.Value.Document.Saved).Select(x => x.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Drops the open working copy of a file that was changed on disk by a refactor, so the next open reads the new text.</summary>
    internal void ForgetDoc(string relative) { docs.TryRemove(relative, out _); moduleDocs.TryRemove(relative, out _); }

    internal async Task<IReadOnlyList<IBlockRenderer>> RenderersAsync(CancellationToken cancellationToken)
    {
        // A settings file that changed on disk (by hand or by the CLI) is picked up without restarting the server.
        string settingsPath = Path.Combine(Root, ".ample", "plugin-settings.json");
        string projectPath = Path.Combine(Root, ".ample", "project.json");
        DateTime stamp = new[] { File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : DateTime.MinValue, File.GetLastWriteTimeUtc(projectPath) }.Max();
        if (pluginRenderers is not null && stamp == settingsStamp)
        {
            return pluginRenderers;
        }

        (IReadOnlyList<DiscoveredPlugin> plugins, _) = PluginHost.Discover([Path.Combine(AmpleRoot, "plugins"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
        try
        {
            string jobsFolder = Path.Combine(Root, ".ample", "tmp", "jobs");
            IReadOnlyList<IBlockRenderer> local = await PluginRenderer.CreateAsync(pluginHost, plugins.Where(p => p.Manifest.Id is not ("flaky" or "echo")), jobsFolder, cancellationToken: cancellationToken, settingsFor: PluginSettingsJson).ConfigureAwait(false);
            FaerRoutes routes = ProjectFile.Parse(File.ReadAllText(projectPath)).Settings.Faer;
            pluginRenderers = FaerRouting.Apply(local, routes, jobsFolder);
            settingsStamp = stamp;
            return pluginRenderers;
        }
        catch (AmpleException)
        {
            if (ProjectFile.Parse(File.ReadAllText(projectPath)).Settings.Faer != FaerRoutes.Default) throw;
            settingsStamp = stamp;
            return pluginRenderers = [];
        }
    }

    internal Task<AnalysisPluginResult> AnalyzeTranscriptionAsync(DiscoveredPlugin plugin, AnalysisJob job, bool allowGpu, CancellationToken cancellationToken)
    {
        if (plugin.Manifest.Needs.Network) throw new AmpleException("AMP5340", "Audio transcription plugins that need network access are not allowed.");
        using JsonDocument options = JsonDocument.Parse(string.IsNullOrWhiteSpace(job.OptionsJson) ? "{}" : job.OptionsJson);
        bool useGpu = options.RootElement.ValueKind != JsonValueKind.Object || !options.RootElement.TryGetProperty("computeDevice", out JsonElement device) || device.ValueKind != JsonValueKind.String || device.GetString() != "cpu";
        DiscoveredPlugin selectedPlugin = plugin with { Manifest = plugin.Manifest with { Needs = plugin.Manifest.Needs with { Gpu = useGpu } } };
        return pluginHost.AnalyzeWithOutputsAsync(selectedPlugin, job, question => !useGpu || allowGpu, true, cancellationToken);
    }

    internal const string PluginSettingsFile = ".ample/plugin-settings.json";

    /// <summary>Appends what the host does with kept-warm GPU plugins to <c>.ample/logs/gpu.log</c>: time, plugin and a reason. Nothing about other programs.</summary>
    private void LogGpu(string message)
    {
        try
        {
            string folder = Path.Combine(Root, ".ample", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "gpu.log"), DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + " " + message + "\n");
        }
        catch (IOException)
        {
            // The log is best effort.
        }
    }

    /// <summary>The GPU memory totals and the plugin processes that are kept warm, for the studio.</summary>
    internal object GpuStatus()
    {
        Ample.Plugins.GpuTotals? totals = pluginHost.DeviceTotals();
        return new
        {
            device = totals is null ? null : new { freeMb = totals.FreeMb, totalMb = totals.TotalMb, usedMb = totals.UsedMb, source = totals.Source },
            kept = pluginHost.WarmPlugins().Select(w => new { plugin = w.PluginId, idleSeconds = (int)w.IdleSeconds }).ToArray(),
        };
    }

    /// <summary>Stops all kept-warm plugin processes now and returns how many there were.</summary>
    internal int ReleaseGpu() => pluginHost.ReleaseWarm("released by the owner");

    /// <summary>The stored settings of a plugin as JSON (an empty object when there are none).</summary>
    internal string PluginSettingsJson(string pluginId)
    {
        string file = Path.Combine(Root, ".ample", "plugin-settings.json");
        try
        {
            if (File.Exists(file))
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                if (d.RootElement.TryGetProperty(pluginId, out JsonElement v))
                {
                    return v.GetRawText();
                }
            }
        }
        catch (JsonException)
        {
            // Damaged file: no settings.
        }

        return "{}";
    }

    private DateTime settingsStamp = DateTime.MinValue;

    internal void ForgetRenderers() => pluginRenderers = null;

    private readonly List<RunningServer> children = [];

    /// <summary>Opens another project in a server of its own (another port, another token) and gives its launch address.</summary>
    internal async Task<string> OpenChildAsync(string folder, CancellationToken cancellationToken)
    {
        RunningServer child = await AmpleServer.StartAsync(new ServerOptions(folder, AmpleRoot, 0, null, false, Library.Home), cancellationToken).ConfigureAwait(false);
        lock (children)
        {
            children.Add(child);
        }

        return child.LaunchUrl;
    }

    internal ControlService NewControl(AmpleDocument doc, IReadOnlyList<IBlockRenderer> renderers) =>
        new(clock, Root, Bank, Engine, Bus, (ulong)(doc.Project?.MasterSeed ?? 0), renderers);

    internal string AddAct(string title, string acceptance, Func<string, string, Task> run)
    {
        foreach (string old in acts.Where(a => a.Value.Expires < clock.UtcNow).Select(a => a.Key).ToList())
        {
            acts.TryRemove(old, out _);
        }

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        acts[token] = new PendingAct(title, acceptance, clock.UtcNow.AddMinutes(10), run);
        return token;
    }

    internal async Task ConfirmActAsync(string token, string typed, string reason)
    {
        if (!acts.TryGetValue(token, out PendingAct? act) || act.Expires < clock.UtcNow)
        {
            throw new AmpleException("AMP5281", "There is no open dry run with that token (it was used or has expired). Run the dry run again.");
        }

        if (!string.Equals(typed, act.Acceptance, StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5232", $"To accept, type exactly: {act.Acceptance}");
        }

        await act.Run(typed, reason).ConfigureAwait(false);
        acts.TryRemove(token, out _);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (RunningServer child in children)
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }

        if (backupHook is not null && ReferenceEquals(Ample.Storage.BlackBox.DangerousAct.BeforeExecute, backupHook))
        {
            Ample.Storage.BlackBox.DangerousAct.BeforeExecute = null;
        }

        AutoCheckpoint?.Dispose();
        if (ai is not null)
        {
            await ai.DisposeAsync().ConfigureAwait(false);
        }

        pluginHost.Dispose();
        Library.Dispose();
        Journal.Dispose();
        project.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static JsonElement Body(JsonElement? element) => element ?? JsonDocument.Parse("{}").RootElement;

    private IReadOnlyList<Ample.Language.SettingHint>? settingHints;

    /// <summary>Gets what the plugins accept in a song's <c>settings</c> line, for the editor's completion (read once per session: the plugins do not change while the server runs).</summary>
    public IReadOnlyList<Ample.Language.SettingHint> SettingHints()
    {
        if (settingHints is not null)
        {
            return settingHints;
        }

        (IReadOnlyList<DiscoveredPlugin> plugins, _) = PluginHost.Discover([Path.Combine(AmpleRoot, "plugins"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
        return settingHints = PluginHost.SettingHints(plugins.Where(p => p.Manifest.Id is not ("flaky" or "echo")));
    }
}
