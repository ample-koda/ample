namespace Ample.Server.Lite;

/// <summary>
/// What a Lite build may take and give. One record, one place: the page asks for these numbers (<c>GET /api/remix/limits</c>) and keeps to them first, the server checks them again.
/// A public host later only tightens them and adds rate limits (plan/lite/PLAN.md, D9).
/// </summary>
/// <param name="MaxFiles">The most files in a project.</param>
/// <param name="MaxDepth">The deepest folder level.</param>
/// <param name="MaxNameLength">The longest name of one path segment.</param>
/// <param name="MaxTextBytes">The largest text file (source, component, notes).</param>
/// <param name="MaxAudioBytes">The largest audio file (wav, flac, mid).</param>
/// <param name="MaxSoundFontBytes">The largest SoundFont (sf2, sfz).</param>
/// <param name="MaxProjectBytes">The largest total size of a project.</param>
/// <param name="BuildSeconds">The wall clock time of one build.</param>
/// <param name="MaxOutputSeconds">The longest audio a build may give.</param>
/// <param name="ConcurrentBuilds">The builds that may run at the same time on the server.</param>
/// <param name="OutputMinutes">How long a finished build is kept for download.</param>
/// <param name="MaxKeptOutputs">The most finished builds kept at once.</param>
/// <param name="BuildsPerMinute">How many builds one client may start in a minute (a client is a cookie and an address, both count).</param>
/// <param name="BuildsPerHour">How many builds one client may start in an hour.</param>
/// <param name="DownloadsPerMinute">How many MP3/FLAC downloads one client may ask for in a minute.</param>
/// <param name="DownloadsPerHour">How many MP3/FLAC downloads one client may ask for in an hour.</param>
/// <param name="EncodedMegabytesPerHour">The encoded audio one client may download in an hour.</param>
/// <param name="QueueWaiting">How many builds may wait for a free build slot; more are refused at once.</param>
/// <param name="QueueWaitSeconds">How long a build waits in the queue before it is refused.</param>
/// <param name="ConcurrentEncodes">How many MP3/FLAC encodings run at the same time.</param>
/// <param name="EncodeWaiting">How many encodings may wait for the encoder.</param>
/// <param name="EncodeSeconds">The wall clock time of one encoding.</param>
/// <param name="MaxEncodedBytes">The largest encoded file.</param>
/// <param name="MaxKeptBytes">The most bytes of builds and their encoded versions kept at once (the oldest go first).</param>
/// <param name="ChecksPerMinute">How many language checks (component check and completion calls) one client may make in a minute.</param>
/// <param name="MaxSockets">How many language-server connections one browser (cookie) may hold open; one network address (several people behind one router) may hold three times as many.</param>
/// <param name="BlobCacheMegabytes">The most megabytes of uploaded files that the server keeps so that the next build does not have to receive them again (demo files are always known and do not count).</param>
/// <param name="BlobCacheMinutes">How many minutes an uploaded file is kept after it was last used.</param>
/// <param name="SharesPerMinute">How many shares one client may make in a minute.</param>
/// <param name="SharesPerHour">How many shares one client may make in an hour.</param>
/// <param name="ShareStoreMegabytes">The megabytes that live shares may hold together; what does not fit is "no longer available", the oldest first (nothing is deleted from disk yet).</param>
/// <param name="ShareCeilingMegabytes">The most megabytes of shared files on disk, expired shares included; above it no new share is accepted until a cleanup has run.</param>
/// <param name="ShareMegabytesPerIpPerHour">The megabytes of own files that one address may put in shares in an hour.</param>
/// <param name="BuildMaxFiles">Grandfathering: the most files a build still accepts when it is higher than MaxFiles (0 = no extra). Raise this to the old MaxFiles when lowering MaxFiles, so existing projects keep working and new files are refused.</param>
/// <param name="BuildMaxProjectBytes">The same for the total size of a project.</param>
/// <param name="MaxBuildMemoryMegabytes">The memory a build in its own process may use before it is stopped (with isolation).</param>
public sealed record LiteLimits(
    int MaxFiles = 200,
    int MaxDepth = 6,
    int MaxNameLength = 80,
    long MaxTextBytes = 512 * 1024,
    long MaxAudioBytes = 25L * 1024 * 1024,
    long MaxSoundFontBytes = 40L * 1024 * 1024,
    long MaxProjectBytes = 64L * 1024 * 1024,
    int BuildSeconds = 120,
    int MaxOutputSeconds = 8 * 60,
    int ConcurrentBuilds = 2,
    int OutputMinutes = 10,
    int MaxKeptOutputs = 16,
    int BuildsPerMinute = 6,
    int BuildsPerHour = 60,
    int DownloadsPerMinute = 8,
    int DownloadsPerHour = 60,
    long EncodedMegabytesPerHour = 400,
    int QueueWaiting = 6,
    int QueueWaitSeconds = 90,
    int ConcurrentEncodes = 1,
    int EncodeWaiting = 3,
    int EncodeSeconds = 60,
    long MaxEncodedBytes = 100L * 1024 * 1024,
    long MaxKeptBytes = 256L * 1024 * 1024,
    int ChecksPerMinute = 60,
    int MaxSockets = 4,
    int BlobCacheMegabytes = 256,
    int BlobCacheMinutes = 60,
    int SharesPerMinute = 3,
    int SharesPerHour = 10,
    int ShareStoreMegabytes = 2048,
    int ShareCeilingMegabytes = 4096,
    int ShareMegabytesPerIpPerHour = 128,
    int MaxBuildMemoryMegabytes = 3000,
    int BuildMaxFiles = 0,
    long BuildMaxProjectBytes = 0)
{
    /// <summary>The most files a build still accepts: <see cref="MaxFiles"/>, or <see cref="BuildMaxFiles"/> when that is higher. Lower MaxFiles later and set BuildMaxFiles to the old value: projects that exist keep building, new files cannot be added past MaxFiles.</summary>
    public int FilesAccepted => Math.Max(MaxFiles, BuildMaxFiles);

    /// <summary>The most bytes a build still accepts: <see cref="MaxProjectBytes"/>, or <see cref="BuildMaxProjectBytes"/> when that is higher (same idea as <see cref="FilesAccepted"/>).</summary>
    public long BytesAccepted => Math.Max(MaxProjectBytes, BuildMaxProjectBytes);

    /// <summary>The defaults.</summary>
    public static LiteLimits Default { get; } = new();

    /// <summary>
    /// The limits of the public Lite (<c>ample serve --lite-only</c>): anybody may use it, so less of everything. Four builds a minute and a few more downloads, small projects, short builds,
    /// a small cache, a short queue.
    /// </summary>
    public static LiteLimits Public { get; } = new(
        MaxFiles: 200,
        MaxTextBytes: 256 * 1024,
        MaxAudioBytes: 8L * 1024 * 1024,
        MaxSoundFontBytes: 16L * 1024 * 1024,
        MaxProjectBytes: 64L * 1024 * 1024,
        BuildSeconds: 60,
        MaxOutputSeconds: 240,
        OutputMinutes: 5,
        MaxKeptOutputs: 8,
        BuildsPerMinute: 4,
        BuildsPerHour: 30,
        DownloadsPerMinute: 5,
        DownloadsPerHour: 30,
        EncodedMegabytesPerHour: 150,
        QueueWaiting: 4,
        QueueWaitSeconds: 60,
        EncodeSeconds: 45,
        MaxEncodedBytes: 60L * 1024 * 1024,
        MaxKeptBytes: 96L * 1024 * 1024,
        ChecksPerMinute: 60,
        MaxSockets: 6,
        BlobCacheMegabytes: 96,
        BlobCacheMinutes: 60,
        MaxBuildMemoryMegabytes: 2500);

    /// <summary>
    /// The defaults with the rate limits of the environment on top (<c>AMPLE_LITE_BUILDS_PER_MINUTE</c>, <c>AMPLE_LITE_BUILDS_PER_HOUR</c>, <c>AMPLE_LITE_DOWNLOADS_PER_MINUTE</c>,
    /// <c>AMPLE_LITE_DOWNLOADS_PER_HOUR</c>, <c>AMPLE_LITE_ENCODED_MB_PER_HOUR</c>, <c>AMPLE_LITE_QUEUE_WAITING</c>): a test run or an owner who wants it stricter or looser sets them.
    /// A value that is not a positive number is ignored.
    /// </summary>
    /// <returns>The limits.</returns>
    public static LiteLimits FromEnvironment() => FromEnvironment(Default);

    /// <summary>The same on top of a given profile (for example <see cref="Public"/>).</summary>
    /// <param name="profile">The limits to start from.</param>
    /// <returns>The limits.</returns>
    public static LiteLimits FromEnvironment(LiteLimits profile)
    {
        static int Number(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value > 0 ? value : fallback;
        LiteLimits d = profile;
        return d with
        {
            BuildsPerMinute = Number("AMPLE_LITE_BUILDS_PER_MINUTE", d.BuildsPerMinute),
            BuildsPerHour = Number("AMPLE_LITE_BUILDS_PER_HOUR", d.BuildsPerHour),
            SharesPerMinute = Number("AMPLE_LITE_SHARES_PER_MINUTE", d.SharesPerMinute),
            SharesPerHour = Number("AMPLE_LITE_SHARES_PER_HOUR", d.SharesPerHour),
            DownloadsPerMinute = Number("AMPLE_LITE_DOWNLOADS_PER_MINUTE", d.DownloadsPerMinute),
            DownloadsPerHour = Number("AMPLE_LITE_DOWNLOADS_PER_HOUR", d.DownloadsPerHour),
            EncodedMegabytesPerHour = Number("AMPLE_LITE_ENCODED_MB_PER_HOUR", (int)d.EncodedMegabytesPerHour),
            QueueWaiting = Number("AMPLE_LITE_QUEUE_WAITING", d.QueueWaiting),
        };
    }
}
