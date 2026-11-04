using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>The project setting that makes every save safe: a checkpoint of the files as they were, the save, and a checkpoint of what was saved, plus an autosave every few minutes.</summary>
internal sealed partial class AutoCheckpoint : IDisposable
{
    private readonly ProjectSession session;
    private readonly Timer timer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly double secondsPerMinute = double.TryParse(Environment.GetEnvironmentVariable("AMPLE_AUTOSAVE_SECONDS_PER_MINUTE"), System.Globalization.CultureInfo.InvariantCulture, out double unit) && unit > 0 ? unit : 60;   // tests shorten the minute
    private DateTimeOffset lastRun = DateTimeOffset.UtcNow;

    internal AutoCheckpoint(ProjectSession session)
    {
        this.session = session;
        TimeSpan tick = TimeSpan.FromSeconds(Math.Min(20, secondsPerMinute / 2));
        timer = new Timer(_ => _ = TickAsync(), null, tick, tick);
    }

    /// <summary>The setting as it is in project.json (off when the file cannot be read).</summary>
    internal static AutoCheckpointSettings Read(string root)
    {
        try
        {
            return ProjectFile.Parse(File.ReadAllText(Path.Combine(root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile))).Settings.AutoCheckpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmpleException)
        {
            return AutoCheckpointSettings.Default;
        }
    }

    /// <summary>Saves a song. With the setting on: a checkpoint of the files as they are on disk (only when they differ from the last one), the save, and a checkpoint named after what changed.</summary>
    internal async Task SaveAsync(DocSession d, string reason, bool suppressCheckpoint, CancellationToken cancellationToken)
    {
        bool on = Read(session.Root).Enabled && !suppressCheckpoint;
        string text = d.Text, before = d.Saved;
        if (on)
        {
            await session.History.CheckpointAsync("Before " + reason.ToLowerInvariant() + " of " + d.Path, cancellationToken).ConfigureAwait(false);
        }

        await session.Bus.ExecuteAsync(new WriteFileCommand(d.Path, text), cancellationToken).ConfigureAwait(false);
        d.Saved = text;
        if (on)
        {
            await session.History.CheckpointAsync(NameOf(reason, d.Path, before, text), cancellationToken).ConfigureAwait(false);
            session.Events.Publish("history.changed", new { kind = "checkpoint" });
        }
    }

    /// <summary>A name that says what changed: the blocks that were added or removed, otherwise the number of lines.</summary>
    internal static string NameOf(string reason, string path, string before, string after)
    {
        HashSet<string> a = Blocks(before), b = Blocks(after);
        List<string> parts = [];
        string[] added = b.Except(a).Order().ToArray(), removed = a.Except(b).Order().ToArray();
        if (added.Length > 0) parts.Add("added " + Join(added));
        if (removed.Length > 0) parts.Add("removed " + Join(removed));
        if (parts.Count == 0)
        {
            int changed = Lines(before).Zip(Lines(after), (x, y) => x != y).Count(x => x) + Math.Abs(Lines(before).Length - Lines(after).Length);
            parts.Add(changed == 0 ? "no changes" : changed == 1 ? "1 line changed" : changed + " lines changed");
        }

        return $"{reason} {path}: {string.Join(", ", parts)}";
    }

    private static string[] Lines(string text) => text.Split('\n');

    private static string Join(string[] names) => names.Length <= 3 ? string.Join(" ", names) : string.Join(" ", names.Take(3)) + $" +{names.Length - 3}";

    private static HashSet<string> Blocks(string text) => Lines(text).Select(l => BlockHeader().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    // v1 header: <type> <name> [pinned@x.y] then a placement, a flag, a freeze or the opening brace (a pre-release class word is skipped)
    [GeneratedRegex(@"^[A-Za-z0-9_-]+ ([A-Za-z0-9_-]+)(?: (?:stable|pinned|live)(?:@[\d.]+)?)?(?= (?:at|bake|lock|refresh|disabled|freeze)(?![\w-])| *\{)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockHeader();

    private async Task TickAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            AutoCheckpointSettings config = Read(session.Root);
            if (!config.Enabled || DateTimeOffset.UtcNow - lastRun < TimeSpan.FromSeconds(config.Minutes * secondsPerMinute))
            {
                return;
            }

            lastRun = DateTimeOffset.UtcNow;
            foreach (DocSession d in session.DirtySongs())
            {
                await SaveAsync(d, "Autosave", false, CancellationToken.None).ConfigureAwait(false);
                d.Version++;
                session.Events.Publish("doc.changed", new { d.Path, d.Version });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmpleException)
        {
            // the next round tries again; a failing autosave must not stop the server
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => timer.Dispose();
}
