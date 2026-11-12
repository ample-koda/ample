using System.Globalization;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;

namespace Ample.Plugins;

/// <summary>Routes an ACE plugin block through Faer while preserving AMPLE's normal plan and local take path.</summary>
public sealed class FaerPluginRenderer(PluginRenderer local, FaerProfile profile, string jobsFolder) : IBlockRenderer, IConfigurableRenderer
{
    /// <inheritdoc />
    public string BlockType => local.BlockType;
    /// <inheritdoc />
    public string Id => "faer." + local.Id;
    /// <inheritdoc />
    public string Version => local.Version;
    /// <inheritdoc />
    public string Determinism => local.Determinism;
    /// <inheritdoc />
    public string SettingsKey => local.SettingsKey;
    /// <inheritdoc />
    public string ToolprintJson => JsonSerializer.Serialize(new { bridge = "faer", protocol = 1, local = JsonSerializer.Deserialize<JsonElement>(local.ToolprintJson) });

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.Inputs.ContainsKey("reference") && BlockType is "ace-music" or "ai-voice") throw new AmpleException("AMP5384", "A reference recording (the reference input or voice_sample) is only supported by the local ACE server; choose engine local for this block.");
        using FaerClient client = profile.CreateClient();
        string capability = BlockType switch { "ace-music" => "song", "ai-voice" => "voice-ace", "voice-clone" => "voice-clone", _ => throw new InvalidOperationException("Unsupported Faer plugin block") };
        JsonElement status = await client.StatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.GetProperty("capabilities").GetProperty(capability).GetProperty("ready").GetBoolean()) throw new InvalidOperationException("The selected Faer capability is unavailable");
        string jobFolder = Path.Combine(jobsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobFolder);
        string? jobId = null;
        try
        {
            List<string> roles = [];
            List<string> hashes = [];
            foreach ((string role, byte[] data) in request.Inputs.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                string file = Path.Combine(jobFolder, role + ".bin");
                await File.WriteAllBytesAsync(file, data, cancellationToken).ConfigureAwait(false);
                roles.Add(role);
                hashes.Add(await client.UploadFileAsync(file, (done, total) => request.Report?.Invoke(new ProgressReport(null, $"Faer uploading {role}: {done} / {total} bytes", (double)done / total)), cancellationToken).ConfigureAwait(false));
            }

            JsonElement render = JsonSerializer.SerializeToElement(new
            {
                block = new { type = request.Block.Type, parameters = request.Block.Parameters.ToDictionary(p => p.Name, p => p.Value) },
                seed = request.Seed.ToString(CultureInfo.InvariantCulture),
                sampleRate = request.SampleRate,
                samplesPerBar = request.SamplesPerBar,
                firstBar = request.FirstBar,
                barCount = request.BarCount,
                activeBars = request.ActiveBars.Order().ToArray(),
                inputRoles = roles,
                settings = JsonSerializer.Deserialize<JsonElement>(local.SongSettings(request)),
                renderer = JsonSerializer.Deserialize<JsonElement>(local.ToolprintJson),
            });
            jobId = Guid.NewGuid().ToString("D");
            FaerJobStatus job = await client.SubmitAsync(new FaerJobRequest(capability, hashes, render) { Id = jobId, ForceRender = request.ForceRender }, cancellationToken).ConfigureAwait(false);
            if (job.CacheHit) request.Report?.Invoke(new ProgressReport(1, "Faer exact render cache hit", 1));
            while (job.State is "queued" or "running")
            {
                request.Report?.Invoke(new ProgressReport(null, job.State == "queued" ? "Faer queued" : "Faer " + (job.Progress?.Stage ?? "preparing"), job.Progress?.Fraction));
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                job = await client.JobAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            if (job.State != "done" || job.Result is null)
            {
                string code = job.Failure?.Code is "FAER_GPU_BUSY" or "FAER_GPU_OOM" or "FAER_GPU_UNAVAILABLE" or "FAER_ACE_FAILED" or "FAER_ARTIFACT_INVALID" or "FAER_WORKER_FAILED" ? job.Failure.Code : "FAER_WORKER_FAILED";
                string stage = job.Failure?.Stage is "preparing" or "planning" or "synthesis" or "isolation" or "voice" or "separation" or "verifying" ? job.Failure.Stage : "unknown";
                string reason = job.Failure?.Reason ?? "Faer returned no failure reason";
                string details = string.IsNullOrWhiteSpace(job.Failure?.Details) ? string.Empty : Environment.NewLine + job.Failure.Details;
                throw new InvalidOperationException($"Faer failed this block ({code} at {stage}): {reason}{details}");
            }
            List<RenderOutput> outputs = [];
            string? metadata = null;
            foreach (FaerOutput output in job.Result.Outputs)
            {
                string file = Path.Combine(jobFolder, output.Role + ".bin");
                await client.DownloadFileAsync(output, file, (done, total) => request.Report?.Invoke(new ProgressReport(null, $"Faer downloading {output.Role}", total > 0 ? Math.Min(1, (double)done / total) : null)), cancellationToken).ConfigureAwait(false);
                if (output.Role == "render-recipe") { metadata = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false); continue; }
                outputs.Add(new RenderOutput(output.Role, await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)));
            }
            request.Report?.Invoke(new ProgressReport(1, "Faer verifying", 1));
            return outputs.Select(output => output with { MetadataJson = metadata }).ToArray();
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
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }
}
