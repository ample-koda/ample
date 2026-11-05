using System.Globalization;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.Takes;

namespace Ample.Server;

/// <summary>
/// The game score of a project: read the definition, build the song, cut the stems and write them with the manifest to <c>renders/game/</c>.
/// A build is a deliberate act like a final: estimate and disk check first, and only one build runs at a time.
/// </summary>
internal static class GameScoreApi
{
    /// <summary>The score file used when a request does not name one.</summary>
    internal const string DefaultScore = "game/score.json";

    /// <summary>The project folder the stems and manifests are written to.</summary>
    internal const string Folder = "renders/game";

    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    /// <summary>Checks the path of a score file and gives the default when there is none.</summary>
    /// <param name="raw">What the request said.</param>
    /// <exception cref="AmpleException">AMP5285 when the path is not a .json file inside the project.</exception>
    internal static string ScorePath(string? raw)
    {
        string path = string.IsNullOrWhiteSpace(raw) ? DefaultScore : raw.Trim().Replace('\\', '/');
        if (path.Length > 200 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5285", "The score is a .json file inside the project, for example game/score.json.");
        }

        return path;
    }

    /// <summary>Checks the name of a stem or manifest file that is asked for: lowercase letters, digits, - and . only, ending in .wav or .manifest.json.</summary>
    /// <param name="file">The file name.</param>
    internal static bool ValidFile(string? file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 120 || !char.IsAsciiLetterOrDigit(file[0]) || file.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (!file.EndsWith(".wav", StringComparison.Ordinal) && !file.EndsWith(".manifest.json", StringComparison.Ordinal))
        {
            return false;
        }

        return file.All(ch => ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch is '-' or '.');
    }

    /// <summary>Reads and checks the definition of a score.</summary>
    /// <param name="s">The project.</param>
    /// <param name="path">The score file.</param>
    /// <param name="text">The text of the file.</param>
    /// <exception cref="AmpleException">AMP5243 when the file does not exist; AMP5285 when the definition has problems.</exception>
    internal static GameScoreDefinition Load(ProjectSession s, string path, out string text)
    {
        text = s.ReadProjectText(path) ?? throw new AmpleException("AMP5243", $"There is no score file '{path}' in the project.");
        if (!GameScoreDefinition.TryParse(text, out GameScoreDefinition? definition, out IReadOnlyList<string> problems))
        {
            throw new AmpleException("AMP5285", "The score has problems: " + string.Join(" ", problems));
        }

        return definition!;
    }

    /// <summary>Describes a score for the Studio and the game: its definition in short, its problems, and what is built.</summary>
    /// <param name="s">The project.</param>
    /// <param name="path">The score file.</param>
    internal static object Describe(ProjectSession s, string path)
    {
        string? text = s.ReadProjectText(path);
        if (text is null)
        {
            return new { ok = false, path, exists = false, problems = new[] { $"There is no score file '{path}' in the project." } };
        }

        if (!GameScoreDefinition.TryParse(text, out GameScoreDefinition? d, out IReadOnlyList<string> problems))
        {
            return new { ok = false, path, exists = true, problems };
        }

        List<string?> variants = [null, .. d!.Variants.Select(v => v.Id)];
        return new
        {
            ok = true,
            path,
            exists = true,
            problems,
            name = d.Name,
            song = d.Song,
            loop = new { fromBar = d.LoopFromBar, bars = d.LoopBars },
            initialState = d.InitialState,
            layers = d.Layers.Select(l => new { l.Id, l.Block }),
            stingers = d.Stingers.Select(x => new { x.Id, x.Block, x.FromBar, x.Bars }),
            states = d.States.Select(x => x.Id),
            variants = d.Variants.Select(v => new { v.Id, v.Song }),
            built = variants.Select(v => new { variant = v, revision = ManifestRevision(s, d.Name, v) }),
        };
    }

    /// <summary>Gets the text of a built manifest, or null when the score (or variant) was not built yet.</summary>
    /// <param name="s">The project.</param>
    /// <param name="id">The score name.</param>
    /// <param name="variant">The variant, or null.</param>
    internal static string? ReadManifest(ProjectSession s, string id, string? variant)
    {
        if (!GameScoreDefinition.IsId(id) || (variant is { Length: > 0 } && !GameScoreDefinition.IsId(variant)))
        {
            throw new AmpleException("AMP5285", "The score and variant names are lowercase letters, digits and -.");
        }

        return s.ReadProjectText(Folder + "/" + GameScoreBuilder.Prefix(id, variant) + ".manifest.json");
    }

    /// <summary>Gets the full path of a built file, or null when it does not exist.</summary>
    /// <param name="s">The project.</param>
    /// <param name="file">The file name (checked with <see cref="ValidFile"/>).</param>
    /// <exception cref="AmpleException">AMP5285 when the name is not valid.</exception>
    internal static string? FilePath(ProjectSession s, string file)
    {
        if (!ValidFile(file))
        {
            throw new AmpleException("AMP5285", "That is not the name of a stem or manifest file.");
        }

        string full = ProjectPath.Resolve(s.Fs, s.Root, Folder + "/" + file);
        return File.Exists(full) ? full : null;
    }

    /// <summary>Builds the song of a score (or one variant), cuts the stems and writes them with the manifest.</summary>
    /// <param name="s">The project.</param>
    /// <param name="scorePath">The score file.</param>
    /// <param name="variant">The variant, or null (or "default") for the main song.</param>
    /// <param name="publish">Publishes an event to everyone listening.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="AmpleException">AMP5287 when the song cannot give the stems; AMP5604 when the disk is too full.</exception>
    internal static async Task<object> BuildAsync(ProjectSession s, string scorePath, string? variant, Action<string, object> publish, CancellationToken ct)
    {
        if (!await BuildGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new AmpleException("AMP5287", "A game score is being built already; wait for it to finish.");
        }

        try
        {
            return await BuildCoreAsync(s, scorePath, variant, publish, ct).ConfigureAwait(false);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    private static async Task<object> BuildCoreAsync(ProjectSession s, string scorePath, string? variant, Action<string, object> publish, CancellationToken ct)
    {
        GameScoreDefinition d = Load(s, scorePath, out string definitionText);
        string? variantId = string.IsNullOrEmpty(variant) || variant == "default" ? null : variant;
        string song = d.Song;
        if (variantId is not null)
        {
            song = (d.Variants.FirstOrDefault(v => v.Id == variantId) ?? throw new AmpleException("AMP5285", $"The score has no variant '{variantId}'.")).Song;
        }

        DocSession doc = await s.DocAsync(song, ct).ConfigureAwait(false);
        AmpleDocument parsed = s.ParseExpanded(doc);
        if (parsed.Diagnostics.Any(x => x.Severity == Severity.Error))
        {
            throw new AmpleException("AMP5287", $"The song '{song}' has errors; fix them first.");
        }

        IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
        Ample.Engine.Control.ControlService control = s.NewControl(parsed, renderers);
        BuildEstimateReport estimate = await StorageApi.EstimateAsync(s, parsed, renderers, control, "bake", null, null, ct).ConfigureAwait(false);
        if (!estimate.Fits)
        {
            throw new AmpleException("AMP5604", $"This score needs about {StorageApi.Size(estimate.Estimate.Bytes)} and the drive of the takebank has {StorageApi.Size(estimate.FreeBytes)} free. Free space first.");
        }

        publish("build.estimate", new { estimate.Estimate.Blocks, estimate.Estimate.Bytes, estimate.Estimate.Seconds, estimate.Estimate.Basis, estimate.Estimate.Builds, estimate.FreeBytes });
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, (block, status) => publish("build.progress", new { block, status }), control, null, null), ct).ConfigureAwait(false);
        StorageApi.RecordBuild(s, parsed, report, clock.Elapsed.TotalSeconds);

        Dictionary<string, StereoBuffer> audio = new(StringComparer.Ordinal);
        List<GameScoreTake> takes = [];
        int? rate = null;
        foreach (string block in GameScoreBuilder.Blocks(d))
        {
            BlockResult result = report.Blocks.FirstOrDefault(b => b.Block == block) ?? throw new AmpleException("AMP5287", $"The song '{song}' has no block '{block}'.");
            if (result.TakeId is null)
            {
                throw new AmpleException("AMP5287", $"The block '{block}' made no audio ({result.Status}{(string.IsNullOrEmpty(result.Message) ? string.Empty : ": " + result.Message)}).");
            }

            TakeRecord take = s.Bank.GetTake(result.TakeId);
            string dataId = Routes.AudioDataId(s, take) ?? throw new AmpleException("AMP5287", $"The block '{block}' has no WAV audio.");
            byte[] wav;
            await using (Stream input = s.Bank.OpenData(dataId))
            {
                using MemoryStream copy = new();
                await input.CopyToAsync(copy, ct).ConfigureAwait(false);
                wav = copy.ToArray();
            }

            (StereoBuffer buffer, int blockRate) = GameScoreBuilder.Decode(wav);
            if (rate is not null && rate != blockRate)
            {
                throw new AmpleException("AMP5287", $"The block '{block}' has another sample rate than the other blocks.");
            }

            rate = blockRate;
            audio[block] = buffer;
            takes.Add(new GameScoreTake(block, take.TakeId, take.PlanId));
        }

        string songText;
        lock (doc)
        {
            songText = doc.Text;
        }

        string version = typeof(Routes).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0";
        GameScoreProvenance provenance = new(version, Convert.ToString(s.Spec.Version, CultureInfo.InvariantCulture) ?? string.Empty, (parsed.Project?.MasterSeed ?? 0).ToString(CultureInfo.InvariantCulture), song, ProjectSession.DiskSha(songText), ProjectSession.DiskSha(definitionText), takes);
        ProjectInfo project = parsed.Project ?? throw new AmpleException("AMP5287", "The song has no composition line.");
        GameScoreBuild build = GameScoreBuilder.Build(d, variantId, audio, rate ?? 44100, project.Tempo, project.MeterNumerator, project.MeterDenominator, provenance);

        List<string> written = [];
        foreach (GameScoreFile file in build.Files)
        {
            if (await WriteAsync(s, file.Name, file.Data, ct).ConfigureAwait(false))
            {
                written.Add(Folder + "/" + file.Name);
            }
        }

        if (await WriteAsync(s, build.ManifestFile, System.Text.Encoding.UTF8.GetBytes(build.ManifestJson), ct).ConfigureAwait(false))
        {
            written.Add(Folder + "/" + build.ManifestFile);
        }

        if (written.Count > 0)
        {
            publish("files.changed", new { paths = written });
        }

        publish("game.score.ready", new { id = d.Name, variant = variantId, revision = build.Revision, manifest = Folder + "/" + build.ManifestFile });
        return new
        {
            ok = true,
            id = d.Name,
            variant = variantId,
            build.Revision,
            manifest = Folder + "/" + build.ManifestFile,
            files = build.Files.Select(f => new { f.Name, f.Role, f.Id, f.Frames, bytes = f.Data.Length, f.Sha256 }),
            build.Warnings,
            written = written.Count,
        };
    }

    /// <summary>Writes a file to the score folder through the command bus, unless it is already there with the same content.</summary>
    private static async Task<bool> WriteAsync(ProjectSession s, string name, byte[] data, CancellationToken ct)
    {
        string relative = Folder + "/" + name;
        string full = ProjectPath.Resolve(s.Fs, s.Root, relative);
        FileInfo existing = new(full);
        if (existing.Exists && existing.Length == data.Length && (await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(data))
        {
            return false;
        }

        await s.Bus.ExecuteAsync(new ImportFileCommand(relative, data), ct).ConfigureAwait(false);
        return true;
    }

    private static string? ManifestRevision(ProjectSession s, string id, string? variant)
    {
        string? text = ReadManifest(s, id, variant);
        if (text is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("revision", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
