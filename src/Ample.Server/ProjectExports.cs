using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Ample.Server;

/// <summary>Cancelable temporary project exports; source files are never modified.</summary>
internal static class ProjectExports
{
    private sealed class Job(string root, string source, string format)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Root { get; } = root;
        public string Source { get; } = source;
        public string Format { get; } = format;
        public string Name { get; } = (format == "zip" ? Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(source)) + "." + format;
        public string Temp { get; } = Path.Combine(Path.GetTempPath(), "ample-export-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Temp, "export." + Format);
        public CancellationTokenSource Cancel { get; } = new();
        public object Sync { get; } = new();
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "running";
        public string Step { get; set; } = "Preparing export";
        public long Bytes { get; set; }
        public long Total { get; set; }
        public int Files { get; set; }
        public int FileCount { get; set; }
        public double Seconds { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Job> Jobs = new(StringComparer.Ordinal);
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".ogg", ".aif", ".aiff", ".m4a", ".aac"];

    public static string Source(string root, string relative)
    {
        if (relative.Replace('\\', '/').Split('/').Any(part => part.Equals(".ample", StringComparison.OrdinalIgnoreCase))) throw new AmpleException("AMP5285", "Private project files cannot be exported.");
        string full = ProjectPath.Resolve(root, relative);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new AmpleException("AMP5243", "The export source does not exist.");
        for (string? path = full; path is not null && !path.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new AmpleException("AMP5243", "Export does not follow project links.");
        return full;
    }

    public static object Start(string root, string relative, string format)
    {
        string source = Source(root, relative);
        bool folder = Directory.Exists(source);
        if (folder ? format != "zip" : format is not ("wav" or "mp3" or "flac") || !AudioExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new AmpleException("AMP5285", "Folders export as ZIP; audio exports as WAV, MP3 or FLAC.");
        Job job = new(root, source, format);
        Directory.CreateDirectory(job.Temp);
        Jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job));
        return Snapshot(job);
    }

    private static Job Get(string root, string id) => Jobs.TryGetValue(id, out Job? job) && job.Root == root ? job : throw new AmpleException("AMP5243", "Export job not found.");
    public static object Status(string root, string id) => Snapshot(Get(root, id));
    public static object Cancel(string root, string id)
    {
        Job job = Get(root, id);
        bool discard = false;
        lock (job.Sync)
        {
            if (job.State == "running") { job.Step = "Cancelling export"; job.Cancel.Cancel(); }
            else if (job.State == "ready") { job.State = "cancelled"; job.Step = "Export cancelled"; discard = true; }
        }
        if (discard) DeleteTemp(job);
        return Snapshot(job);
    }

    public static IResult Download(string root, string id)
    {
        Job job = Get(root, id);
        lock (job.Sync)
        {
            if (job.State != "ready") throw new AmpleException("AMP5285", "The export is not ready.");
            return Results.File(job.Output, job.Format switch { "zip" => "application/zip", "wav" => "audio/wav", "mp3" => "audio/mpeg", _ => "audio/flac" }, job.Name, enableRangeProcessing: true);
        }
    }

    private static object Snapshot(Job job)
    {
        lock (job.Sync) return new { id = job.Id, name = job.Name, state = job.State, step = job.Step, bytes = job.Bytes, totalBytes = job.Total, files = job.Files, totalFiles = job.FileCount, audioSeconds = job.Seconds, elapsedSeconds = (DateTimeOffset.UtcNow - job.Started).TotalSeconds, fraction = job.Total > 0 ? (double?)Math.Min(1, (double)job.Bytes / job.Total) : null };
    }

    private static async Task RunAsync(Job job)
    {
        try
        {
            if (job.Format == "zip") await ZipAsync(job).ConfigureAwait(false);
            else await ConvertAsync(job).ConfigureAwait(false);
            job.Cancel.Token.ThrowIfCancellationRequested();
            lock (job.Sync) { job.State = "ready"; job.Step = "Ready to download"; }
        }
        catch (OperationCanceledException) { lock (job.Sync) { job.State = "cancelled"; job.Step = "Export cancelled"; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (job.Sync) { job.State = "failed"; job.Step = ex is System.ComponentModel.Win32Exception ? "Audio conversion needs ffmpeg; install it or set AMPLE_FFMPEG." : "Export failed: " + ex.Message; }
        }
        finally
        {
            if (job.State != "ready") DeleteTemp(job);
            _ = ExpireAsync(job);
        }
    }

    private static async Task ZipAsync(Job job)
    {
        List<string> files = [], folders = [];
        Queue<string> pending = new(); pending.Enqueue(job.Source);
        while (pending.TryDequeue(out string? folder))
        {
            job.Cancel.Token.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(folder))
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(path).Equals(".ample", StringComparison.OrdinalIgnoreCase)) continue;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Enqueue(path); folders.Add(path); }
                else { files.Add(path); lock (job.Sync) { job.Total += new FileInfo(path).Length; job.FileCount++; job.Step = "Counting files"; } }
            }
        }
        await using FileStream output = new(job.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using ZipArchive zip = new(output, ZipArchiveMode.Create, true);
        foreach (string folder in folders) { job.Cancel.Token.ThrowIfCancellationRequested(); zip.CreateEntry(Path.GetRelativePath(job.Source, folder).Replace('\\', '/') + "/"); }
        byte[] buffer = new byte[65536];
        foreach (string file in files)
        {
            job.Cancel.Token.ThrowIfCancellationRequested();
            lock (job.Sync) job.Step = "Compressing ZIP";
            await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
            await using Stream entry = zip.CreateEntry(Path.GetRelativePath(job.Source, file).Replace('\\', '/'), CompressionLevel.Optimal).Open();
            int read;
            while ((read = await input.ReadAsync(buffer, job.Cancel.Token).ConfigureAwait(false)) > 0)
            {
                await entry.WriteAsync(buffer.AsMemory(0, read), job.Cancel.Token).ConfigureAwait(false);
                lock (job.Sync) job.Bytes += read;
            }
            lock (job.Sync) job.Files++;
        }
        lock (job.Sync) job.Step = "Finalizing ZIP";
    }

    private static async Task ConvertAsync(Job job)
    {
        ProcessStartInfo info = new(Environment.GetEnvironmentVariable("AMPLE_FFMPEG") is { Length: > 0 } exe ? exe : "ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-nostats", "-progress", "pipe:1", "-y", "-i", job.Source, "-map", "0:a:0", "-vn", "-c:a", job.Format switch { "mp3" => "libmp3lame", "flac" => "flac", _ => "pcm_s24le" }, job.Output }) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not start.");
        using CancellationTokenRegistration stop = job.Cancel.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { /* already exited */ } });
        Task<string> errors = process.StandardError.ReadToEndAsync();
        lock (job.Sync) job.Step = "Converting audio";
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(job.Cancel.Token).ConfigureAwait(false)) is not null)
        {
            string[] pair = line.Split('=', 2);
            if (pair.Length != 2) continue;
            lock (job.Sync)
            {
                if (pair[0] == "out_time_us" && long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time)) job.Seconds = time / 1_000_000d;
                if (pair[0] == "total_size" && long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) job.Bytes = size;
            }
        }
        await process.WaitForExitAsync(job.Cancel.Token).ConfigureAwait(false);
        _ = await errors.ConfigureAwait(false);
        job.Cancel.Token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || !File.Exists(job.Output)) throw new InvalidOperationException("ffmpeg could not convert this audio file.");
    }

    private static void DeleteTemp(Job job)
    {
        try { if (Directory.Exists(job.Temp)) Directory.Delete(job.Temp, true); }
        catch (IOException) { /* a download can still have the temporary file open */ }
    }

    private static async Task ExpireAsync(Job job)
    {
        await Task.Delay(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        Jobs.TryRemove(job.Id, out _); DeleteTemp(job); job.Cancel.Dispose();
    }
}
