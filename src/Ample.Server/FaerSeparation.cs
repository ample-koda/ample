using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Plugins;
using Ample.Plugins.Analysis;

namespace Ample.Server;

/// <summary>Runs project stem separation on Faer while keeping validated results in the local analysis cache.</summary>
internal static class FaerSeparation
{
    public static async Task<AnalysisPluginResult> RunAsync(AnalysisJob analysis, string input, AudioInputInfo source, CancellationToken cancellationToken)
    {
        if (source.Channels is not (1 or 2)) throw new InvalidDataException("Faer separation accepts mono or stereo WAV audio.");
        FaerProfile profile = FaerProfile.LoadLocal() ?? throw new InvalidOperationException("This machine has no Faer connection profile.");
        using FaerClient client = profile.CreateClient();
        JsonElement status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.GetProperty("capabilities").GetProperty("separation").GetProperty("ready").GetBoolean()) throw new InvalidOperationException("Faer stem separation is unavailable.");

        void Report(string step, double? fraction) => analysis.OnProgress?.Invoke(new ProgressReport(fraction, step, fraction));
        Report("Faer preparing audio", null);
        string? flac = source.Format == 1 && source.BitsPerSample == 16 ? await TryFlacAsync(input, cancellationToken).ConfigureAwait(false) : null;
        string upload = flac ?? input;
        string format = flac is null ? "wav" : "flac";
        string? jobId = null;
        try
        {
            string hash = await client.UploadFileAsync(upload, (done, total) => Report($"Faer uploading audio: {done} / {total} bytes", (double)done / total), cancellationToken).ConfigureAwait(false);
            if (flac is not null) File.Delete(flac);
            jobId = Guid.NewGuid().ToString("D");
            JsonElement render = JsonSerializer.SerializeToElement(new { format, sampleRate = source.SampleRate, frames = source.Frames, channels = source.Channels });
            FaerJobStatus job = await client.SubmitAsync(new FaerJobRequest("separation", [hash], render) { Id = jobId }, cancellationToken).ConfigureAwait(false);
            while (job.State is "queued" or "running")
            {
                Report(job.State == "queued" ? "Faer queued" : "Faer " + (job.Progress?.Stage ?? "separation"), job.Progress?.Fraction);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                job = await client.JobAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            if (job.State != "done" || job.Result is null) throw new InvalidOperationException("Faer stem separation did not complete.");
            string outputFolder = Path.Combine(analysis.JobDir, "out");
            Directory.CreateDirectory(outputFolder);
            List<(string Role, string Path)> outputs = [];
            foreach (FaerOutput output in job.Result.Outputs)
            {
                if (output.Role is not ("vocals" or "backing") || outputs.Any(item => item.Role == output.Role)) throw new InvalidDataException("Faer returned an unexpected stem.");
                string file = Path.Combine(outputFolder, output.Role + ".wav");
                await client.DownloadFileAsync(output, file, (done, total) => Report($"Faer downloading {output.Role}: {done} / {total} bytes", (double)done / total), cancellationToken).ConfigureAwait(false);
                outputs.Add((output.Role, file));
            }
            if (outputs.Count != 2) throw new InvalidDataException("Faer did not return both stems.");
            Report("Faer verifying stems", 1);
            return new AnalysisPluginResult("{\"provider\":\"faer\",\"model\":\"htdemucs\"}", outputs);
        }
        catch (OperationCanceledException)
        {
            if (jobId is not null)
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(5));
                try { await client.CancelAsync(jobId, limit.Token).ConfigureAwait(false); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
            }
            throw;
        }
    }

    private static async Task<string?> TryFlacAsync(string input, CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetDirectoryName(input)!, "audio.flac");
        ProcessStartInfo start = new("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-map_metadata", "-1", "-c:a", "flac", output }) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { return null; }
        if (process is null) return null;
        using (process)
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            _ = await errors.ConfigureAwait(false);
            return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length < new FileInfo(input).Length ? output : null;
        }
    }
}
