using System.Runtime.CompilerServices;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Storage.Takes;

namespace Ample.Server;

/// <summary>The storage overview and the clean-up for the studio: dry run first, then a background run with progress events that can be cancelled.</summary>
internal static class StorageApi
{
    private static readonly ConditionalWeakTable<ProjectSession, Runner> Runners = [];
    private static readonly ConditionalWeakTable<ProjectSession, OverviewMemo> OverviewMemos = [];
    private const long OverviewMemoMilliseconds = 15_000;

    private sealed class OverviewMemo
    {
        public object? Result;
        public long At;
        public string State = string.Empty;
        public bool Refreshing;
    }

    /// <summary>The state of the single clean-up that may run at a time.</summary>
    private sealed class Runner
    {
        public readonly object Lock = new();
        public CancellationTokenSource? Cancel;
        public Task? Task;
        public string State = "idle";
        public StorageProgress Progress = new(0, 0, 0, 0);
        public string? Error;
        public string Fingerprint = string.Empty;
    }

    private static StorageService Service(ProjectSession s) => new(s.Bank, s.Root, Path.Combine(s.Root, ".ample", "history.db"), s.Clock);

    /// <summary>Formats a number of bytes (12.4 GB).</summary>
    public static string Size(long b) => b >= GbBytes ? $"{b / (double)GbBytes:0.0} GB" : b >= 1024L * 1024 ? $"{b / (1024.0 * 1024):0} MB" : $"{Math.Max(0, b / 1024)} KB";

    private const long GbBytes = 1024L * 1024 * 1024;

    /// <summary>How many blocks a build of this project renders at once.</summary>
    private static int Workers(ProjectSession s) => Ample.Engine.Engine.BuildParallelism.Effective(0, Ample.Engine.Engine.BuildParallelism.OfProject(s.Root));

    /// <summary>Works out the estimate of a build (a plan-only pass that lists the blocks that have to render) and whether it fits on the drive.</summary>
    public static async Task<BuildEstimateReport> EstimateAsync(ProjectSession s, Ample.Language.AmpleDocument parsed, IReadOnlyList<Ample.Audio.Engine.IBlockRenderer> renderers, Ample.Engine.Control.ControlService control, string intent, Ample.Engine.Engine.BarRange? range, string? only, CancellationToken ct)
    {
        Ample.Engine.Engine.BuildReport plan = await s.Engine.BuildAsync(parsed, new Ample.Engine.Engine.BuildOptions(intent, range, only, 0, renderers, null, control, null, null) { PlanOnly = true }, ct).ConfigureAwait(false);
        HashSet<string> rendering = [.. plan.Blocks.Where(b => BuildStats.WillRender(b.Status)).Select(b => b.Block)];   // a waiting block renders after its stale input does
        int waiting = plan.Blocks.Count(b => b.Status == "waiting");
        IReadOnlyList<RenderStep> steps = BuildStats.StepsOf(parsed.Blocks.Select(b => (b.Name, b.Type, (IReadOnlyList<string>)[.. b.Froms.Select(f => f.Block)])), rendering);
        double seconds = s.Engine.SongSeconds(parsed);
        if (range is { } r)
        {
            seconds = Math.Min(seconds, r.Bars * 4 * 60.0 / (parsed.Project?.Tempo ?? 92));
        }

        BuildEstimate estimate = BuildStats.Load(s.Root).Estimate(steps, seconds, Workers(s));
        StorageService service = Service(s);
        long free = 0;
        try
        {
            string folder = service.Settings.DataPath ?? Path.Combine(s.Root, ".ample");
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            free = long.MaxValue;   // an unknown drive is not a reason to refuse
        }

        // a small build never needs the check; a big one needs its bytes and a margin
        bool fits = estimate.Bytes < 50L * 1024 * 1024 || free >= (long)(estimate.Bytes * 1.2);
        long reclaimable = fits ? 0 : service.Overview().ReclaimableBytes;
        string text = estimate.Blocks == 0
            ? "Nothing to render: every block is cached."
            : $"{estimate.Blocks} block(s) to render: about {Size(estimate.Bytes)} and {BuildStats.Pretty(estimate.Seconds)} ({(estimate.Basis == "learned" ? $"from {estimate.Builds} earlier build(s)" : "a first guess, it gets better with every build")}).";
        // a waiting block renders only if the block before it comes out different: the plan cannot know, so the numbers are the worst case
        if (waiting > 0 && estimate.Blocks > 0)
        {
            text += $" {waiting} of them only render if the block before them changes the sound, so this is the most it can be.";
        }

        return new BuildEstimateReport(estimate, free, reclaimable, fits, text);
    }

    /// <summary>Learns from a finished build: the real size of every block that rendered and the time the build took.</summary>
    public static void RecordBuild(ProjectSession s, Ample.Language.AmpleDocument parsed, Ample.Engine.Engine.BuildReport report, double elapsedSeconds)
    {
        try
        {
            Dictionary<string, string> types = [];
            foreach (Ample.Language.BlockNode block in parsed.Blocks)
            {
                types[block.Name] = block.Type;
            }

            List<(string Type, long Bytes)> rendered = [.. report.Blocks.Where(b => b.Status == "rendered" && b.TakeId is not null).Select(b => (types.GetValueOrDefault(b.Block, "mixer"), s.Bank.SizeOfTake(b.TakeId!)))];
            BuildStats stats = BuildStats.Load(s.Root);
            HashSet<string> done = [.. report.Blocks.Where(b => b.Status == "rendered").Select(b => b.Block)];
            IReadOnlyList<RenderStep> steps = BuildStats.StepsOf(parsed.Blocks.Select(b => (b.Name, b.Type, (IReadOnlyList<string>)[.. b.Froms.Select(f => f.Block)])), done);
            stats.Record(rendered, Math.Max(1, s.Engine.SongSeconds(parsed)), elapsedSeconds, stats.Concurrency(steps, Workers(s)));   // how many blocks really ran at once decides how much work the elapsed time stands for
        }
        catch (Exception ex) when (ex is IOException or AmpleException)
        {
            // learning is optional
        }
    }

    /// <summary>Gets the overview, the settings and the state of a running clean-up.</summary>
    public static object Overview(ProjectSession s)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        OverviewMemo memo = OverviewMemos.GetOrCreateValue(s);
        lock (memo)
        {
            // adding up a big takebank takes seconds and the page asks at every start: the answer is kept for a short while, and dropped when a clean-up changes state or the options are saved
            long now = Environment.TickCount64;
            if (memo.Result is not null && memo.State == runner.State && runner.State != "running")
            {
                if (now - memo.At < OverviewMemoMilliseconds) return memo.Result;

                // an older answer is given at once and made again in the background, so the page never waits for the adding up after the first time
                if (!memo.Refreshing)
                {
                    memo.Refreshing = true;
                    _ = Task.Run(() => { try { Refresh(s, memo); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmpleException) { } finally { lock (memo) memo.Refreshing = false; } });
                }

                return memo.Result;
            }

            StorageService service = Service(s);
            IReadOnlyList<(string DataId, long Bytes)> plain = service.PlainBlobs();
            object result = new { overview = service.Overview(), settings = service.Settings, clean = State(runner), plain = new { files = plain.Count, bytes = plain.Sum(p => p.Bytes) } };
            memo.Result = result;
            memo.At = now;
            memo.State = runner.State;
            return result;
        }
    }

    /// <summary>Adds up the takebank once in the background when the project opens, so the first request of the page is not the one that waits.</summary>
    public static void Warm(ProjectSession s) => _ = Task.Run(() => { try { Overview(s); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmpleException) { } });

    private static void Refresh(ProjectSession s, OverviewMemo memo)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        StorageService service = Service(s);
        IReadOnlyList<(string DataId, long Bytes)> plain = service.PlainBlobs();
        object result = new { overview = service.Overview(), settings = service.Settings, clean = State(runner), plain = new { files = plain.Count, bytes = plain.Sum(p => p.Bytes) } };
        lock (memo)
        {
            memo.Result = result;
            memo.At = Environment.TickCount64;
            memo.State = runner.State;
        }
    }

    /// <summary>Forgets the kept overview (the options changed, so the next answer is made again).</summary>
    public static void ForgetOverview(ProjectSession s)
    {
        OverviewMemo memo = OverviewMemos.GetOrCreateValue(s);
        lock (memo)
        {
            memo.Result = null;
        }
    }

    /// <summary>Makes the dry run: exactly what a clean-up would do, with its fingerprint.</summary>
    public static object Plan(ProjectSession s, IReadOnlySet<string>? reasons, bool includeTemp)
    {
        StorageService service = Service(s);
        List<(string Path, long Bytes)> temp = includeTemp ? TempFolders(s) : [];
        StorageCleanPlan plan = service.Plan(reasons, temp);
        return new { plan = new { plan.Bytes, plan.ByReason, plan.ByBlock, plan.ProtectedBytes, plan.FreeBefore, plan.FreeAfter, plan.TempBytes, plan.Fingerprint, takes = plan.TakeIds.Count, blobs = plan.DataIds.Count, tempItems = plan.TempFolders.Count } };
    }

    /// <summary>Starts the clean-up of a dry run (the fingerprint must still match, so what was shown is what is done).</summary>
    public static object Start(ProjectSession s, IReadOnlySet<string>? reasons, bool includeTemp, string fingerprint, Action<string, object> publish)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        lock (runner.Lock)
        {
            if (runner.State == "running")
            {
                throw new AmpleException("AMP5602", "A clean-up is already running.");
            }

            StorageService service = Service(s);
            List<(string Path, long Bytes)> temp = includeTemp ? TempFolders(s) : [];
            StorageCleanPlan plan = service.Plan(reasons, temp);
            if (!string.Equals(plan.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new AmpleException("AMP5602", "What would be cleaned up changed since the dry run. Review the dry run again, then accept it.");
            }

            runner.Cancel = new CancellationTokenSource();
            runner.State = "running";
            runner.Error = null;
            runner.Fingerprint = fingerprint;
            runner.Progress = new StorageProgress(0, plan.TakeIds.Count + plan.DataIds.Count + temp.Count, 0, plan.Bytes + plan.TempBytes);
            CancellationToken token = runner.Cancel.Token;
            runner.Task = Task.Run(async () =>
            {
                try
                {
                    Progress<StorageProgress> progress = new(p =>
                    {
                        runner.Progress = p;
                        publish("storage.progress", new { p.Done, p.Total, p.Bytes, p.TotalBytes });
                    });
                    StorageProgress result = await service.RunAsync(plan, progress, token).ConfigureAwait(false);
                    long tempFreed = 0;
                    foreach ((string path, long bytes) in temp)
                    {
                        token.ThrowIfCancellationRequested();
                        if (StorageTemp.Delete(s.Root, path))
                        {
                            tempFreed += bytes;
                        }
                    }

                    runner.Progress = new StorageProgress(result.Total + temp.Count, result.Total + temp.Count, result.Bytes + tempFreed, plan.Bytes + plan.TempBytes);
                    runner.State = "done";
                }
                catch (OperationCanceledException)
                {
                    runner.State = "cancelled";
                }
                catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
                {
                    runner.State = "failed";
                    runner.Error = ex.Message;
                }

                publish("storage.done", new { state = runner.State, runner.Progress.Bytes, error = runner.Error });
            });
        }

        return new { started = true };
    }

    /// <summary>Starts compressing the plain WAV takes in place (lossless) in the background, with the same progress and cancel as a clean-up.</summary>
    public static object StartCompress(ProjectSession s, Action<string, object> publish)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        lock (runner.Lock)
        {
            if (runner.State == "running")
            {
                throw new AmpleException("AMP5602", "A clean-up is already running.");
            }

            StorageService service = Service(s);
            IReadOnlyList<(string DataId, long Bytes)> plain = service.PlainBlobs();
            runner.Cancel = new CancellationTokenSource();
            runner.State = "running";
            runner.Error = null;
            runner.Progress = new StorageProgress(0, plain.Count, 0, plain.Sum(p => p.Bytes));
            CancellationToken token = runner.Cancel.Token;
            runner.Task = Task.Run(async () =>
            {
                try
                {
                    Progress<StorageProgress> progress = new(p =>
                    {
                        runner.Progress = p;
                        publish("storage.progress", new { p.Done, p.Total, p.Bytes, p.TotalBytes });
                    });
                    runner.Progress = await service.CompressAsync(progress, token).ConfigureAwait(false);
                    runner.State = "done";
                }
                catch (OperationCanceledException)
                {
                    runner.State = "cancelled";
                }
                catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
                {
                    runner.State = "failed";
                    runner.Error = ex.Message;
                }

                publish("storage.done", new { state = runner.State, runner.Progress.Bytes, error = runner.Error });
            });
        }

        return new { started = true, files = Service(s).PlainBlobs().Count };
    }

    /// <summary>Moves the take data to another folder or drive in the background (empty path: back into the project).</summary>
    public static object StartMove(ProjectSession s, string? path, Action<string, object> publish)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        lock (runner.Lock)
        {
            if (runner.State == "running")
            {
                throw new AmpleException("AMP5602", "A clean-up is already running.");
            }

            StorageService service = Service(s);
            runner.Cancel = new CancellationTokenSource();
            runner.State = "running";
            runner.Error = null;
            runner.Progress = new StorageProgress(0, 0, 0, 0);
            CancellationToken token = runner.Cancel.Token;
            runner.Task = Task.Run(async () =>
            {
                try
                {
                    Progress<StorageProgress> progress = new(p =>
                    {
                        runner.Progress = p;
                        publish("storage.progress", new { p.Done, p.Total, p.Bytes, p.TotalBytes });
                    });
                    runner.Progress = await service.MoveAsync(path, progress, token).ConfigureAwait(false);
                    runner.State = "done";
                }
                catch (OperationCanceledException)
                {
                    runner.State = "cancelled";
                }
                catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
                {
                    runner.State = "failed";
                    runner.Error = ex.Message;
                }

                publish("storage.done", new { state = runner.State, runner.Progress.Bytes, error = runner.Error });
            });
        }

        return new { started = true };
    }

    /// <summary>Asks the running clean-up to stop (between items, leaving a consistent store).</summary>
    public static object Cancel(ProjectSession s)
    {
        Runner runner = Runners.GetOrCreateValue(s);
        runner.Cancel?.Cancel();
        return State(runner);
    }

    /// <summary>Gets the state of the clean-up.</summary>
    public static object Status(ProjectSession s) => State(Runners.GetOrCreateValue(s));

    private static object State(Runner r) => new { r.State, r.Progress.Done, r.Progress.Total, r.Progress.Bytes, r.Progress.TotalBytes, r.Error };

    /// <summary>Temporary working folders that may go: temporary files and the files of finished AI tasks, when untouched for a day (see <see cref="StorageTemp"/>).</summary>
    private static List<(string Path, long Bytes)> TempFolders(ProjectSession s)
    {
        HashSet<string> open = [];
        bool unknown = false;
        try
        {
            AssistantSessionStore store = new(s.Root);
            foreach (AssistantSession session in store.List())
            {
                foreach (AssistantTaskSnapshot task in store.SavedTasks(session.Id))
                {
                    // an unapplied proposal can still be resumed or applied: its working folder stays until it is applied, rejected or cancelled
                    if (!task.Applied && task.Status is not ("accepted" or "rejected" or "cancelled" or "deleted"))
                    {
                        open.Add(task.Id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or AmpleException or InvalidOperationException)
        {
            unknown = true;   // cannot tell which tasks are open: leave every AI folder alone
        }

        return StorageTemp.List(s.Root, DateTime.UtcNow, name => unknown || open.Any(id => name.StartsWith(id, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>The estimate of a build together with the room on the drive.</summary>
/// <param name="Estimate">Blocks, bytes and seconds.</param>
/// <param name="FreeBytes">Free space of the drive of the takebank.</param>
/// <param name="ReclaimableBytes">What a clean-up could give back (only worked out when the build does not fit).</param>
/// <param name="Fits">Whether it fits (a small build always does).</param>
/// <param name="Text">A sentence for the person.</param>
internal sealed record BuildEstimateReport(BuildEstimate Estimate, long FreeBytes, long ReclaimableBytes, bool Fits, string Text);
