using Ample.Audio.Engine;

namespace Ample.Plugins;

/// <summary>
/// "Faer, else local": renders on Faer when it is connected and has the capability ready, otherwise on the local renderer. Only the choice of place is made here: a Faer job that fails after it started is reported, never silently repeated locally.
/// The place is decided once and then kept for the whole build: the identity read for the plan and the renderer that makes the audio always agree. The decision is kept while a render is in flight and for a minute after
/// the last use (a build keeps using the renderer, a later build after a pause decides again); a new renderer instance (made when the settings change) decides again at once. A Faer that drops out mid-build gives an error, not a fallback.
/// </summary>
public sealed class FaerElseLocalRenderer(IBlockRenderer faer, IBlockRenderer local, FaerProfile profile, string capability) : IBlockRenderer, IConfigurableRenderer
{
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(1);
    private readonly Lock gate = new();
    private bool? useFaer;
    private DateTime lastUse = DateTime.MinValue;
    private int inFlight;
    private Task<bool>? probe;

    /// <inheritdoc />
    public string BlockType => local.BlockType;
    /// <inheritdoc />
    public string Id => Pick().Id;
    /// <inheritdoc />
    public string Version => Pick().Version;
    /// <inheritdoc />
    public string Determinism => Pick().Determinism;
    /// <inheritdoc />
    public string ToolprintJson => Pick().ToolprintJson;
    /// <inheritdoc />
    public string SettingsKey => local is IConfigurableRenderer c ? c.SettingsKey : string.Empty;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        IBlockRenderer chosen = await PickAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            inFlight++;
        }

        try
        {
            request.Report?.Invoke(new ProgressReport(null, chosen == faer ? "Rendering on Faer" : "Faer is not available: rendering locally"));
            return await chosen.RenderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                inFlight--;
                lastUse = DateTime.UtcNow;
            }
        }
    }

    // The getters cannot be async: the probe runs on the pool (no sync-context deadlock) and is shared with everyone asking at the same time.
    private IBlockRenderer Pick() => Task.Run(() => PickAsync(CancellationToken.None)).GetAwaiter().GetResult();

    private async Task<IBlockRenderer> PickAsync(CancellationToken cancellationToken)
    {
        Task<bool> running;
        lock (gate)
        {
            if (useFaer is { } decided && (inFlight > 0 || DateTime.UtcNow - lastUse < Hold))
            {
                lastUse = DateTime.UtcNow;
                return decided ? faer : local;
            }

            running = probe ??= ProbeAsync();
        }

        bool ready = await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ready ? faer : local;
    }

    // One probe at a time; its result becomes the decision of this build.
    private async Task<bool> ProbeAsync()
    {
        bool ready;
        try
        {
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(4));
            using FaerClient client = profile.CreateClient();
            System.Text.Json.JsonElement status = await client.StatusAsync(limit.Token).ConfigureAwait(false);
            ready = status.GetProperty("capabilities").GetProperty(capability).GetProperty("ready").GetBoolean();
        }
        catch (Exception)
        {
            // A missing or damaged profile, a key that cannot be decrypted, no network, a timeout or an unexpected answer all mean "Faer is not ready".
            ready = false;
        }

        lock (gate)
        {
            useFaer = ready;
            lastUse = DateTime.UtcNow;
            probe = null;
        }

        return ready;
    }
}
