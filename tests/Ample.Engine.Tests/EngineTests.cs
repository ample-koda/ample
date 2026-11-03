using System.Security.Cryptography;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Core.Tests;

internal sealed class EngineRig : IDisposable
{
    public const string Hello = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\npiano piano pinned@2.0 at 1 for 4 {\n  from chords.notes -> notes\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n  from piano.audio -> audio3\n}\nmaster master bake {\n  from mix.audio -> audio\n}\n";

    private static readonly Lazy<SpecData> LazySpec = new(() =>
    {
        static string? FindSpecRoot(string? root)
        {
            while (root is not null && !Directory.Exists(Path.Combine(root, "spec")))
            {
                root = Path.GetDirectoryName(root);
            }

            return root;
        }

        string root = FindSpecRoot(AppContext.BaseDirectory)
            ?? FindSpecRoot(Environment.GetEnvironmentVariable("AMPLE_REPO_ROOT"))
            ?? FindSpecRoot(Directory.GetCurrentDirectory())
            ?? throw new DirectoryNotFoundException("Could not find the AMPLE spec directory.");

        string spec = Path.Combine(root, "spec");
        return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
    });

    private readonly string folder;
    public readonly string ProjectRoot;
    public readonly Guid ProjectId;
    public readonly Takebank Bank;
    public readonly BuildEngine Engine;

    private EngineRig(string folder, string projectRoot, Guid projectId, Takebank bank, BuildEngine engine)
    {
        this.folder = folder;
        ProjectRoot = projectRoot;
        ProjectId = projectId;
        Bank = bank;
        Engine = engine;
    }

    public static SpecData Spec => LazySpec.Value;

    public static async Task<EngineRig> CreateAsync()
    {
        string folder = Directory.CreateTempSubdirectory("ample-engine-").FullName;
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        using OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "engine");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        return new EngineRig(folder, root, project.Manifest.Id, bank, new BuildEngine(bank, Spec, project.Manifest.Id, root));
    }

    public static AmpleDocument Parse(string text) => Parser.Parse(text, Spec);

    public string MasterSha(BuildReport report) => Convert.ToHexStringLower(SHA256.HashData(Master(report)));

    public byte[] Master(BuildReport report)
    {
        using Stream s = Bank.OpenData(Bank.GetTake(report.MasterTakeId!).Data[0].DataId);
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}

[Trait("Category", "Engine")]
public sealed class EngineTests
{
    private sealed class CustomRenderer(string type, Func<RenderRequest, IReadOnlyList<RenderOutput>> render) : IBlockRenderer
    {
        public string BlockType => type;

        public string Id => "test." + type;

        public string Version => "1";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(render(request));
    }

    private sealed class AsyncRenderer(string type, Func<RenderRequest, CancellationToken, ValueTask<IReadOnlyList<RenderOutput>>> render) : IBlockRenderer
    {
        public string BlockType => type;
        public string Id => "test." + type;
        public string Version => "1";
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => render(request, cancellationToken);
    }

    [Fact]
    public async Task A_finished_block_reports_progress_before_its_slow_sibling_finishes()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> chordsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncRenderer slowDrums = new("drums", async (request, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return [new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate))];
        });
        Task<BuildReport> build = r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions(ExtraRenderers: [slowDrums], Progress: (block, status) =>
        {
            if (block == "chords") chordsDone.TrySetResult(status);
        }));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("rendered", await chordsDone.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(build.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        BuildReport report = await build;
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "drums").Status);
    }

    [Fact]
    public async Task A_previous_indeterminate_progress_step_finishes_when_the_next_step_starts()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BlockProgress? latest = null;
        CustomRenderer renderer = new("drums", request =>
        {
            request.Report?.Invoke(new ProgressReport(null, "Faer preparing"));
            request.Report?.Invoke(new ProgressReport(null, "Faer planning", 0.06));
            request.Report?.Invoke(new ProgressReport(null, "Faer synthesis", 0.19));
            return [new RenderOutput("audio", Wav.Encode(new short[request.SamplesPerBar * request.BarCount], request.SampleRate))];
        });

        await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions(ExtraRenderers: [renderer], Detail: progress =>
        {
            if (progress.Block == "drums") latest = progress;
        }));

        Assert.NotNull(latest);
        Assert.Equal(1, latest.Steps.Single(step => step.Name == "Faer preparing").Fraction);
        Assert.Equal(1, latest.Steps.Single(step => step.Name == "Faer planning").Fraction);
        Assert.Equal(0.19, latest.Steps.Single(step => step.Name == "Faer synthesis").Fraction);
    }

    [Fact]
    public async Task The_hello_song_renders_the_same_bytes_every_time_and_on_every_machine()
    {
        using EngineRig a = await EngineRig.CreateAsync();
        using EngineRig b = await EngineRig.CreateAsync();
        BuildReport ra = await a.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        BuildReport rb = await b.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions(Parallelism: 1));
        Assert.All(ra.Blocks, x => Assert.Equal("rendered", x.Status));
        Assert.Equal(a.MasterSha(ra), b.MasterSha(rb));

        (short[] samples, int rate) = Wav.Decode(a.Master(ra));
        Assert.Equal(44100, rate);
        Assert.Equal(4 * 88200, samples.Length);
        Assert.True(AudioMeter.PeakDbfs(samples) < 0 && AudioMeter.PeakDbfs(samples) > -40);

        string root = Environment.GetEnvironmentVariable("AMPLE_REPO_ROOT") ?? AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "spec-tests")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string golden = Path.Combine(root, "spec-tests", "engine", "hello-master.sha256");
        if (!File.Exists(golden))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, a.MasterSha(ra) + "\n");
            Assert.Fail("The golden hash did not exist and was created: review it, commit it and run the test again.");
        }

        Assert.Equal(File.ReadAllText(golden).Trim(), a.MasterSha(ra));
    }

    [Fact]
    public async Task A_second_build_is_fully_cached_and_a_change_rebuilds_only_the_block_and_what_depends_on_it()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport first = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        int attempts = r.Bank.ListAttempts().Count;

        BuildReport second = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        Assert.Equal(6, second.Hits);
        Assert.Equal(0, second.Misses);
        Assert.Equal(attempts, r.Bank.ListAttempts().Count);
        Assert.Equal(first.MasterTakeId, second.MasterTakeId);

        BuildReport changed = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello.Replace("beat-A", "beat-B", StringComparison.Ordinal)));
        Assert.Equal(["drums", "master", "mix"], changed.Blocks.Where(x => x.Status == "rendered").Select(x => x.Block).Order());
        Assert.Equal(["bass", "chords", "piano"], changed.Blocks.Where(x => x.Status == "cached").Select(x => x.Block).Order());
        Assert.NotEqual(first.MasterTakeId, changed.MasterTakeId);
    }

    [Fact]
    public async Task A_read_only_project_cache_seeds_a_separate_proposal_bank_by_exact_plan()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport original = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        int sourceAttempts = r.Bank.ListAttempts().Count;
        string stage = Path.Combine(r.ProjectRoot, ".ai-stage");
        PhysicalFileSystem fs = new();
        using (OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), stage, "proposal")) { }
        Takebank stageBank = Takebank.Open(fs, new SystemClock(), stage);
        Takebank sourceCache = Takebank.OpenReadOnly(fs, new SystemClock(), r.ProjectRoot);
        BuildEngine proposal = new(stageBank, EngineRig.Spec, r.ProjectId, r.ProjectRoot, null, sourceCache);

        BuildReport reused = await proposal.BuildAsync(EngineRig.Parse(EngineRig.Hello));

        Assert.Equal(6, reused.Hits);
        Assert.Equal(0, reused.Misses);
        Assert.Equal(original.MasterTakeId, reused.MasterTakeId);
        Assert.Equal(sourceAttempts, r.Bank.ListAttempts().Count);
        Assert.Equal(6, stageBank.ListAttempts().Count);
    }

    [Fact]
    public async Task Changing_a_lyrics_file_creates_a_new_plan_instead_of_using_the_cached_take()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        string lyricsFolder = Path.Combine(r.ProjectRoot, "lyrics");
        Directory.CreateDirectory(lyricsFolder);
        string lyricsFile = Path.Combine(lyricsFolder, "chorus.lyr");
        await File.WriteAllTextAsync(lyricsFile, "[bar 1] first words\n");
        AmpleDocument doc = EngineRig.Parse("composition \"lyrics\" { ample 1  tempo 120 master-seed 7 }\nlyrics words {\n  file lyrics/chorus.lyr\n}\n");

        BuildReport first = await r.Engine.BuildAsync(doc);
        Assert.Equal("rendered", first.Blocks.Single().Status);
        string firstTake = first.Blocks.Single().TakeId!;
        Assert.Contains(r.Bank.GetPlan(r.Bank.GetTake(firstTake).PlanId).Plan.Inputs, input => input.Kind == "file" && input.Toolprint == "lyrics/chorus.lyr");

        Assert.Equal("cached", (await r.Engine.BuildAsync(doc)).Blocks.Single().Status);
        await File.WriteAllTextAsync(lyricsFile, "[bar 1] changed words\n");
        BuildReport changed = await r.Engine.BuildAsync(doc);
        Assert.Equal("rendered", changed.Blocks.Single().Status);
        Assert.NotEqual(firstTake, changed.Blocks.Single().TakeId);
    }

    [Fact]
    public async Task Changing_a_sample_file_creates_a_new_plan_instead_of_using_the_cached_take()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        string kit = Path.Combine(r.ProjectRoot, "samples", "kit");
        Directory.CreateDirectory(kit);
        await File.WriteAllBytesAsync(Path.Combine(kit, "kick.wav"), Wav.Encode([1000, -1000], 44100));
        await File.WriteAllBytesAsync(Path.Combine(kit, "snare.wav"), Wav.Encode([900, -900], 44100));
        await File.WriteAllBytesAsync(Path.Combine(kit, "hat.wav"), Wav.Encode([800, -800], 44100));
        AmpleDocument doc = EngineRig.Parse("composition \"samples\" { ample 1  tempo 120 master-seed 7 }\ndrums beat at 1 for 1 {\n  kit samples/kit\n  pattern beat-A\n}\n");

        BuildReport first = await r.Engine.BuildAsync(doc);
        string firstTake = first.Blocks.Single().TakeId!;
        Assert.Contains(r.Bank.GetPlan(r.Bank.GetTake(firstTake).PlanId).Plan.Inputs, input => input.Kind == "sample" && input.Toolprint == "samples/kit/kick.wav");

        Assert.Equal("cached", (await r.Engine.BuildAsync(doc)).Blocks.Single().Status);
        await File.WriteAllBytesAsync(Path.Combine(kit, "kick.wav"), Wav.Encode([2000, -2000], 44100));
        BuildReport changed = await r.Engine.BuildAsync(doc);
        Assert.Equal("rendered", changed.Blocks.Single().Status);
        Assert.NotEqual(firstTake, changed.Blocks.Single().TakeId);
    }

    [Fact]
    public async Task Every_take_has_its_plan_and_failed_or_cancelled_renders_keep_a_plan_and_no_take()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildOptions failing = new(ExtraRenderers: [new CustomRenderer("bass", _ => throw new InvalidOperationException("synth exploded")), new CustomRenderer("drums", _ => throw new OperationCanceledException())]);
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), failing);

        Assert.Equal("failed", report.Blocks.Single(b => b.Block == "bass").Status);
        Assert.Equal("cancelled", report.Blocks.Single(b => b.Block == "drums").Status);
        Assert.Equal("skipped", report.Blocks.Single(b => b.Block == "mix").Status);
        Assert.Equal(["cancelled", "failed"], r.Bank.ListAttempts().Where(a => a.Outcome != "completed").Select(a => a.Outcome).Order());
        foreach (TakeRecord t in r.Bank.ListTakes())
        {
            Assert.Equal(t.PlanId, r.Bank.GetPlan(t.PlanId).PlanId);
        }

        Assert.DoesNotContain(r.Bank.ListTakes(), t => r.Bank.GetPlan(t.PlanId).Plan.BlockPath is "bass" or "drums");
        BuildReport again = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        Assert.Equal("rendered", again.Blocks.Single(b => b.Block == "bass").Status);
        Assert.Equal("cached", again.Blocks.Single(b => b.Block == "chords").Status);
    }

    [Fact]
    public async Task Blocks_without_a_renderer_are_skipped_with_a_code_and_so_is_what_depends_on_them()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        string text = EngineRig.Hello + "ai-voice vox {\n}\nmixer extra {\n  from vox.vocal -> audio1\n}\n";
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(text));
        Assert.Contains("AMP5251", report.Blocks.Single(b => b.Block == "vox").Message, StringComparison.Ordinal);
        Assert.Equal("skipped", report.Blocks.Single(b => b.Block == "extra").Status);
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "master").Status);
    }

    [Fact]
    public async Task Disabled_blocks_produce_no_take_and_required_consumers_are_implicitly_disabled_with_a_warning()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        string song = EngineRig.Hello.Replace("midi-clip chords", "midi-clip chords disabled", StringComparison.Ordinal);
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(song));
        Assert.Equal("disabled", report.Blocks.Single(b => b.Block == "chords").Status);
        Assert.Null(report.Blocks.Single(b => b.Block == "chords").TakeId);
        Assert.Equal("disabled", report.Blocks.Single(b => b.Block == "piano").Status);
        Assert.Contains("AMP5253", report.Blocks.Single(b => b.Block == "piano").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_effect_with_no_input_is_skipped_with_a_warning_and_the_rest_still_renders()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello + "duck loose {\n}\n"));
        BlockResult loose = report.Blocks.Single(b => b.Block == "loose");
        Assert.Equal("skipped", loose.Status);
        Assert.Contains("AMP5255", loose.Message, StringComparison.Ordinal);
        Assert.Equal("rendered", report.Blocks.Single(b => b.Block == "master").Status);
    }

    [Fact]
    public async Task A_preview_is_cheaper_covers_only_the_range_and_is_a_different_plan_from_the_bake()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport baked = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello));
        BuildReport preview = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions("preview", new BarRange(2, 2)));
        (short[] samples, int rate) = Wav.Decode(r.Master(preview));
        Assert.Equal(BuildEngine.PreviewRate, rate);
        Assert.Equal(2 * 2 * BuildEngine.PreviewRate, samples.Length);   // two bars of two seconds
        Assert.NotEqual(baked.MasterTakeId, preview.MasterTakeId);
        Assert.Equal("preview", r.Bank.GetPlan(r.Bank.GetTake(preview.MasterTakeId!).PlanId).Plan.Intent);
        Assert.Equal("rendered", preview.Blocks.Single(b => b.Block == "chords").Status);
    }

    [Fact]
    public async Task Baking_marks_the_source_and_changes_no_cache_key()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        AmpleDocument doc = EngineRig.Parse(EngineRig.Hello);
        await r.Engine.BuildAsync(doc);

        EditResult edit = Edits.AddFlag(doc, "bass", "bake");
        Assert.True(edit.Ok);
        string baked = Edits.Apply(doc.Text, edit.Edits);
        AmpleDocument after = EngineRig.Parse(baked);
        Assert.Contains("bake", after.Blocks.Single(b => b.Name == "bass").Flags);
        Assert.Contains("bass bass at 1 for 4 bake {", baked, StringComparison.Ordinal);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Empty(Edits.AddFlag(after, "bass", "bake").Edits);

        BuildReport again = await r.Engine.BuildAsync(after);
        Assert.Equal(6, again.Hits);
        Assert.Equal(0, again.Misses);
        Assert.False(Edits.AddFlag(doc, "nothing", "bake").Ok);
    }

    [Fact]
    public async Task Building_only_one_block_builds_what_it_needs()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(EngineRig.Hello), new BuildOptions(Only: "bass"));
        Assert.Equal(["bass", "chords"], report.Blocks.Select(b => b.Block).Order());
    }

    [Fact]
    public async Task Two_hundred_blocks_build_and_a_rebuild_is_fully_cached()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        string text = "composition \"big\" { ample 1  tempo 100  master-seed 1 }\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"midi-clip c{i} at 1 for 2 {{\n  chords Am F\n}}\n"));
        AmpleDocument doc = EngineRig.Parse(text);
        BuildReport first = await r.Engine.BuildAsync(doc);
        Assert.Equal(200, first.Misses);
        BuildReport second = await r.Engine.BuildAsync(doc);
        Assert.Equal(200, second.Hits);
        Assert.Equal(0, second.Misses);
    }

    [Fact]
    public void Fixed_point_helpers_are_exact()
    {
        Assert.Equal(32768, FixedAudio.GainFromDb(0));
        Assert.Equal(16423, FixedAudio.GainFromDb(-6));
        Assert.Equal(65381, FixedAudio.GainFromDb(6));
        Assert.Equal(88200, FixedAudio.SamplesPerBar(44100, 120000));
        Assert.Equal(FixedAudio.Increment(69, 44100) * 2, FixedAudio.Increment(81, 44100), 2.0);
        Assert.Equal([57, 60, 64], BuiltInRenderers.Triad("Am"));
        Assert.Equal([53, 57, 60], BuiltInRenderers.Triad("F"));
    }
}
