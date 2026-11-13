using System.Globalization;
using System.Text.Json;
using Ample.Audio.Engine;

namespace Ample.Plugins;

/// <summary>Adapts a plugin to the build engine: a block of one of the plugin's types is rendered in a plugin process, its outputs become the take, and the plugin's toolprint and determinism go into the plan.</summary>
public sealed class PluginRenderer : IBlockRenderer, IConfigurableRenderer
{
    private readonly PluginHost host;
    private readonly DiscoveredPlugin plugin;
    private readonly string jobsFolder;
    private readonly string settingsJson;
    private readonly TimeSpan? timeout;

    private PluginRenderer(PluginHost host, DiscoveredPlugin plugin, string blockType, string toolprintJson, string jobsFolder, string settingsJson, TimeSpan? timeout)
    {
        this.host = host;
        this.plugin = plugin;
        BlockType = blockType;
        ToolprintJson = toolprintJson;
        this.jobsFolder = jobsFolder;
        this.settingsJson = settingsJson;
        this.timeout = timeout;
    }

    /// <inheritdoc />
    public string SettingsKey => plugin.Manifest.Id;

    /// <inheritdoc />
    public string BlockType { get; }

    /// <inheritdoc />
    public string Id => "plugin." + plugin.Manifest.Id;

    /// <inheritdoc />
    public string Version => plugin.Manifest.Version;

    /// <inheritdoc />
    public string Determinism => plugin.Manifest.Determinism;

    /// <inheritdoc />
    public string ToolprintJson { get; }

    /// <summary>Creates one renderer per block type of each plugin. Each plugin is asked once for its description to learn its toolprint.</summary>
    public static async Task<IReadOnlyList<IBlockRenderer>> CreateAsync(PluginHost host, IEnumerable<DiscoveredPlugin> plugins, string jobsFolder, string settingsJson = "{}", TimeSpan? timeout = null, Func<string, string>? settingsFor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        List<IBlockRenderer> list = [];
        foreach (DiscoveredPlugin p in plugins)
        {
            JsonElement d = await host.DescribeAsync(p, cancellationToken).ConfigureAwait(false);
            JsonElement toolprint = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("toolprint", out JsonElement t) ? t : JsonSerializer.SerializeToElement(new { });
            (IReadOnlyList<string> errors, string filled) = SettingsValidator.Validate(p.Manifest.SettingsSchemaJson, settingsFor?.Invoke(p.Manifest.Id) ?? settingsJson);
            if (errors.Count > 0)
            {
                throw new Ample.Core.Abstractions.AmpleException("AMP5279", $"The settings of plugin '{p.Manifest.Id}' are not valid: {string.Join("; ", errors)}");
            }

            foreach (string block in p.Manifest.Blocks)
            {
                list.Add(new PluginRenderer(host, p, block, JsonSerializer.Serialize(new { plugin = p.Manifest.Id, version = p.Manifest.Version, reported = toolprint, settingsSchema = JsonSerializer.Deserialize<JsonElement>(p.Manifest.SettingsSchemaJson) }), jobsFolder, filled, timeout));
            }
        }

        return list;
    }

    // The settings of the song, typed by the plugin's schema, with the plugin's own defaults filled in. The project-level defaults are only a starting point for new songs: they are not read here.
    internal string SongSettings(RenderRequest request)
    {
        // Settings the plugin wants every song to state itself ("x-songRequired" in its schema): the plugin default is not enough, so an old song is told what to add.
        using (JsonDocument schemaDoc = JsonDocument.Parse(plugin.Manifest.SettingsSchemaJson))
        {
            if (schemaDoc.RootElement.TryGetProperty("x-songRequired", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (string name in required.EnumerateArray().Select(x => x.GetString()!))
                {
                    if (request.Settings is null || !request.Settings.ContainsKey(name))
                    {
                        using JsonDocument project = JsonDocument.Parse(settingsJson);
                        string value = project.RootElement.TryGetProperty(name, out JsonElement v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : string.Empty;
                        throw new Ample.Core.Abstractions.AmpleException("AMP5370", $"This song does not say yet how the sound is made: the setting {plugin.Manifest.Id}.{name} is required in every song (the project value is only the default for new songs)." + (value.Length > 0 ? $" Use: {plugin.Manifest.Id}.{name} {value}  (the project default)" : " Set it in the plugin settings of this song."));
                    }
                }
            }
        }

        string typed = SettingsValidator.FromStrings(plugin.Manifest.SettingsSchemaJson, request.Settings ?? new Dictionary<string, string>());
        (IReadOnlyList<string> errors, string filled) = SettingsValidator.Validate(plugin.Manifest.SettingsSchemaJson, typed);
        string? missing = errors.FirstOrDefault(e => e.EndsWith("is required", StringComparison.Ordinal));
        if (missing is not null)
        {
            string name = missing.Split('\'')[1];
            using JsonDocument project = JsonDocument.Parse(settingsJson);
            string value = project.RootElement.TryGetProperty(name, out JsonElement v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : string.Empty;
            throw new Ample.Core.Abstractions.AmpleException("AMP5370", $"This song has no value for the required setting {plugin.Manifest.Id}.{name} of the plugin (it is required, so the plugin cannot know what to do without it)." + (value.Length > 0 ? $" Use: {plugin.Manifest.Id}.{name} {value}  (the project default)" : $" The project has no default for it: set it in the plugin settings of this song."));
        }

        return errors.Count > 0
            ? throw new Ample.Core.Abstractions.AmpleException("AMP5279", $"The settings of plugin '{plugin.Manifest.Id}' in this song are not valid: {string.Join("; ", errors)}")
            : filled;
    }

    private static readonly TimeSpan LongRunWarning = TimeSpan.FromMinutes(5);

    // The host must never stop a job before the plugin's own limit: a plugin that states timeoutS in its settings (ACE-Step does, 900 s by default, up to 3600) gets that
    // plus a minute, so its own clearer message comes first. Without it, or when a caller gave a limit, the old 10 minutes apply.
    private TimeSpan LimitFor(string songSettings)
    {
        if (timeout is { } given)
        {
            return given;
        }

        using JsonDocument doc = JsonDocument.Parse(songSettings);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("timeoutS", out JsonElement seconds) && seconds.TryGetInt32(out int value) && value > 0
            ? TimeSpan.FromSeconds(Math.Max(600, value + 60))
            : TimeSpan.FromMinutes(10);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string job = Path.Combine(jobsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(job, "in"));
        try
        {
            Dictionary<string, string> inputs = [];
            foreach ((string name, byte[] data) in request.Inputs)
            {
                string rel = "in/" + name + ".bin";
                await File.WriteAllBytesAsync(Path.Combine(job, "in", name + ".bin"), data, cancellationToken).ConfigureAwait(false);
                inputs[name] = rel;
            }

            string songSettings = SongSettings(request);
            string parameters = JsonSerializer.Serialize(new
            {
                jobDir = job,
                block = new { type = request.Block.Type, name = request.Block.Name, parameters = request.Block.Parameters.ToDictionary(p => p.Name, p => p.Value) },
                seed = request.Seed.ToString(CultureInfo.InvariantCulture),
                sampleRate = request.SampleRate,
                samplesPerBar = request.SamplesPerBar,
                beatsPerBar = request.BeatsPerBar,
                firstBar = request.FirstBar,
                barCount = request.BarCount,
                activeBars = request.ActiveBars.Order().ToArray(),
                inputs,
                settings = JsonSerializer.Deserialize<object>(songSettings),
            });
            TimeSpan limit = LimitFor(songSettings);
            using CancellationTokenSource finished = new();
            _ = Task.Delay(LongRunWarning, finished.Token).ContinueWith(
                t => Console.Error.WriteLine($"AMP5288: block '{request.Block.Name}' ({plugin.Manifest.Id}) has been rendering for over {LongRunWarning.TotalMinutes:0} minutes; it is stopped after {limit.TotalSeconds:0} seconds."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            PluginResult result;
            try
            {
                result = await host.RenderAsync(plugin, new PluginJob(job, parameters, limit, request.Report), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await finished.CancelAsync().ConfigureAwait(false);
            }

            List<RenderOutput> outputs = [];
            foreach ((string role, string path) in result.Outputs)
            {
                outputs.Add(new RenderOutput(role, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) { MetadataJson = result.MetaJson });
            }

            return outputs;
        }
        finally
        {
            try
            {
                Directory.Delete(job, recursive: true);
            }
            catch (IOException)
            {
                // A leftover job folder is scratch space.
            }
        }
    }
}
