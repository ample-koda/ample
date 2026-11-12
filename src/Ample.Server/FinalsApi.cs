using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.Takes;

namespace Ample.Server;

/// <summary>The finals of a song: list, define, and render one on purpose (a full build, then the kept bars with their fades, exported).</summary>
internal static class FinalsApi
{
    /// <summary>Lists the finals of a song with what their last render made.</summary>
    public static object List(ProjectSession s, string song) => new
    {
        song,
        finals = new FinalsStore(s.Root).List(song).Select(f => new { name = f.Spec.Name, fromBar = f.Spec.FromBar, bars = f.Spec.Bars, fadeInBeats = f.Spec.FadeInBeats, fadeOutBars = f.Spec.FadeOutBars, format = f.Spec.Format, takeId = f.TakeId, path = f.Path, rendered = f.Rendered, bytes = f.Bytes }),
    };

    /// <summary>Adds or changes a final.</summary>
    public static object Upsert(ProjectSession s, string song, JsonElement body)
    {
        string name = body.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } v ? v.Trim() : throw new AmpleException("AMP5285", "A final needs a name.");
        if (name.Length > 60 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ' ')))
        {
            throw new AmpleException("AMP5285", "The name of a final is letters, digits, spaces, - and _ (up to 60 characters).");
        }

        double Num(string key, double fallback) => body.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : fallback;
        string format = body.TryGetProperty("format", out JsonElement f) && f.GetString() == "wav" ? "wav" : "mp3";
        FinalSpec spec = new(name, (int)Math.Clamp(Num("fromBar", 1), 1, 100_000), (int)Math.Clamp(Num("bars", 0), 0, 100_000), Math.Clamp(Num("fadeInBeats", 0), 0, 64), Math.Clamp(Num("fadeOutBars", 0), 0, 64), format);
        new FinalsStore(s.Root).Upsert(song, spec);
        return List(s, song);
    }

    /// <summary>Removes a final (its exported file stays).</summary>
    public static object Remove(ProjectSession s, string song, string name)
    {
        new FinalsStore(s.Root).Remove(song, name);
        return List(s, song);
    }

    /// <summary>
    /// Renders a final on purpose: the estimate and the disk check first (like any full build), the build, then the kept bars of the master with the fades,
    /// exported to <c>renders/&lt;song&gt;-&lt;final&gt;.&lt;format&gt;</c>. The master take is recorded and so protected from clean-up.
    /// </summary>
    public static async Task<object> RenderAsync(ProjectSession s, string song, string name, Action<string, object> publish, Func<Stream, CancellationToken, Task<(byte[] Data, string Encoder)>> encodeMp3, CancellationToken ct)
    {
        FinalsStore store = new(s.Root);
        FinalInfo info = store.List(song).FirstOrDefault(f => f.Spec.Name == name) ?? throw new AmpleException("AMP5605", $"The song has no final called '{name}'.");
        DocSession d = await s.DocAsync(song, ct).ConfigureAwait(false);
        AmpleDocument parsed = s.ParseExpanded(d);
        if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
        {
            throw new AmpleException("AMP5287", "The song has errors; fix them first.");
        }

        IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
        Ample.Engine.Control.ControlService control = s.NewControl(parsed, renderers);
        BuildEstimateReport estimate = await StorageApi.EstimateAsync(s, parsed, renderers, control, "bake", null, null, ct).ConfigureAwait(false);
        if (!estimate.Fits)
        {
            throw new AmpleException("AMP5604", $"This final needs about {StorageApi.Size(estimate.Estimate.Bytes)} and the drive of the takebank has {StorageApi.Size(estimate.FreeBytes)} free. Free space (Projects, Storage can give back about {StorageApi.Size(estimate.ReclaimableBytes)}) or move the takebank.");
        }

        publish("build.estimate", new { estimate.Estimate.Blocks, estimate.Estimate.Bytes, estimate.Estimate.Seconds, estimate.Estimate.Basis, estimate.Estimate.Builds, estimate.FreeBytes });
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, (block, status) => publish("build.progress", new { block, status }), control, null, null), ct).ConfigureAwait(false);
        StorageApi.RecordBuild(s, parsed, report, clock.Elapsed.TotalSeconds);
        string takeId = report.MasterTakeId ?? throw new AmpleException("AMP5287", "The song has no master to cut a final from.");
        TakeRecord take = s.Bank.GetTake(takeId);
        string[] preferred = ["master", "mix", "audio", "out"];
        TakeDataRef data = take.Data.OrderBy(x => { int i = Array.IndexOf(preferred, x.Role); return i < 0 ? preferred.Length : i; }).First();
        byte[] wav;
        await using (Stream input = s.Bank.OpenData(data.DataId))
        {
            using MemoryStream copy = new();
            await input.CopyToAsync(copy, ct).ConfigureAwait(false);
            wav = copy.ToArray();
        }

        (StereoBuffer buffer, int rate) = StereoWav.Decode(wav);
        StereoBuffer cut = FinalsStore.Cut(buffer, rate, parsed.Project?.Tempo ?? 92, info.Spec);
        byte[] cutWav = StereoWav.Encode(cut, rate);
        byte[] output = cutWav;
        string encoder = "none";
        if (info.Spec.Format == "mp3")
        {
            (output, encoder) = await encodeMp3(new MemoryStream(cutWav), ct).ConfigureAwait(false);
        }

        string relative = "renders/" + Path.GetFileNameWithoutExtension(song) + "-" + name.Replace(' ', '-') + "." + info.Spec.Format;
        string folder = Path.GetDirectoryName(ProjectPath.Resolve(s.Fs, s.Root, relative))!;
        if (!s.Fs.DirectoryExists(folder))
        {
            await s.Bus.ExecuteAsync(new CreateFolderCommand("renders"), ct).ConfigureAwait(false);
        }

        await s.Bus.ExecuteAsync(new ImportFileCommand(relative, output), ct).ConfigureAwait(false);
        publish("files.changed", new { paths = new[] { relative } });
        store.Record(song, info.Spec, takeId, relative, output.Length, DateTimeOffset.UtcNow);
        return new { name, path = relative, bytes = output.Length, seconds = cut.Length / (double)rate, encoder, takeId };
    }
}
