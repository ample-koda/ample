using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;

namespace Ample.Plugins;

/// <summary>Renders through the local ACE plugin while holding a <see cref="FaerSpark"/> lease, so the local server runs for exactly as long as the block needs it.</summary>
public sealed class SparkLeaseRenderer(IBlockRenderer inner, FaerSpark spark) : IBlockRenderer, IConfigurableRenderer
{
    /// <inheritdoc />
    public string BlockType => inner.BlockType;
    /// <inheritdoc />
    public string Id => inner.Id;
    /// <inheritdoc />
    public string Version => inner.Version;
    /// <inheritdoc />
    public string Determinism => inner.Determinism;
    /// <inheritdoc />
    public string ToolprintJson => inner.ToolprintJson;
    /// <inheritdoc />
    public string SettingsKey => inner is IConfigurableRenderer c ? c.SettingsKey : string.Empty;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (!spark.Serves(request.Settings))
        {
            return await inner.RenderAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // The song's own settings are checked first (they can refuse the build without a server), then the files they name against what the server has, before any block work.
        string? songSettings = inner is PluginRenderer plugin ? plugin.SongSettings(request) : null;
        bool started = false;
        await using IAsyncDisposable lease = await spark.AcquireAsync(message =>
        {
            started = true;
            request.Report?.Invoke(new ProgressReport(null, message));
        }, cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            request.Report?.Invoke(new ProgressReport(null, "Using the local ACE-Step server"));
        }

        if (songSettings is not null && await spark.ModelFilesAsync(cancellationToken).ConfigureAwait(false) is { } have)
        {
            CheckModelFiles(songSettings, have);
        }

        return await inner.RenderAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static readonly string[] ModelSettings = ["synthModelFile", "lmModelFile", "vocalExtractionModel", "cliLmModel"];

    private static void CheckModelFiles(string songSettings, IReadOnlyList<string> have)
    {
        using JsonDocument doc = JsonDocument.Parse(songSettings);
        foreach (string setting in ModelSettings)
        {
            if (!doc.RootElement.TryGetProperty(setting, out JsonElement value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } file
                || have.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string family = file.Contains("5Hz-lm", StringComparison.OrdinalIgnoreCase) ? "5Hz-lm" : "acestep-v15-";
            string near = string.Join(", ", have.Where(h => h.Contains(family, StringComparison.OrdinalIgnoreCase)).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).Take(8));
            throw new AmpleException("AMP5382", $"The local ACE-Step server has no model file '{file}' (setting acestep.{setting})." + (near.Length > 0 ? $" It has: {near}." : string.Empty) + " Change the setting in this song, route the song to Faer, or add the file to the server's models folder.");
        }
    }
}
