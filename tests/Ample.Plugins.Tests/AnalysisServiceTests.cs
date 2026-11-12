using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Plugins.Analysis;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Plugins.Tests;

[Trait("Category", "Analysis")]
public sealed class AnalysisServiceTests : IDisposable
{
    private static readonly string[] AllPlatforms = ["windows", "linux", "macos"];
    private static readonly string[] PitchAnalyses = ["pitch"];

    private readonly string folder = Directory.CreateTempSubdirectory("ample-analysis-").FullName;
    private readonly PluginHost host = new();

    public void Dispose()
    {
        host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static byte[] Wave(float[] x) => Wav.Encode([.. x.Select(s => (short)Math.Clamp(Math.Round(s * 32767), -32768, 32767))], Synth.Rate);

    private async Task<(Takebank Bank, string Root, Guid ProjectId)> RigAsync()
    {
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        using OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "analysis");
        return (Takebank.Open(fs, new SystemClock(), root), root, project.Manifest.Id);
    }

    private static async Task<string> TakeAsync(Takebank bank, byte[] wav, string block = "vox")
    {
        PlanRecord plan = new(Guid.NewGuid(), "u-" + block, block, "h" + Guid.NewGuid().ToString("N"), [], [], 1, "test", "1", "{}", "{}", "bake", null);
        TakeRecord take = await (await bank.BeginAsync(plan)).CommitAsync([("audio", new MemoryStream(wav))]);
        return take.TakeId;
    }

    [Fact]
    public async Task A_result_is_computed_once_linked_to_the_take_and_checked_against_its_schema()
    {
        (Takebank bank, string root, _) = await RigAsync();
        string take = await TakeAsync(bank, Wave(Synth.Clicks(120, 10)));
        AnalysisService service = new(bank, root);

        AnalysisRecord first = service.Analyze(take, "tempo");
        Assert.False(first.FromCache);
        Assert.InRange(JsonDocument.Parse(first.Json).RootElement.GetProperty("bpm").GetDouble(), 119, 121);
        AnalysisRecord second = service.Analyze(take, "tempo");
        Assert.True(second.FromCache);
        Assert.Equal(first.Key, second.Key);
        Assert.Equal(first.Json, second.Json);

        Assert.NotEqual(first.Key, service.Analyze(take, "loudness").Key);
        Assert.NotEqual(service.Analyze(take, "melody", "{\"tempo\":100}").Key, service.Analyze(take, "melody", "{\"tempo\":120}").Key);
        Assert.Equal(service.Analyze(take, "melody", "{\"tempo\":100,\"x\":1}").Key, service.Analyze(take, "melody", "{\"x\":1,\"tempo\":100}").Key);
        Assert.Equal(["loudness", "melody", "tempo"], service.ForTake(take).Select(r => r.Analyzer).Distinct().Order());

        // Another take with the same audio shares the stored result; the take itself never changed.
        string twin = await TakeAsync(bank, Wave(Synth.Clicks(120, 10)), "other");
        Assert.True(service.Analyze(twin, "tempo").FromCache);
        Assert.Equal(["audio"], bank.GetTake(take).Data.Select(d => d.Role));
        Assert.Empty(bank.FindOrphans());
    }

    [Fact]
    public async Task Unknown_analysers_takes_without_readable_audio_and_bad_results_are_refused()
    {
        (Takebank bank, string root, _) = await RigAsync();
        string good = await TakeAsync(bank, Wave(Synth.Tone(440, 2)));
        string bad = await TakeAsync(bank, new byte[100], "weird");
        AnalysisService service = new(bank, root);
        Assert.Equal("AMP5341", Assert.Throws<AmpleException>(() => service.Analyze(good, "nothing")).DiagnosticCode);
        Assert.Equal("AMP5342", Assert.Throws<AmpleException>(() => service.Analyze(bad, "tempo")).DiagnosticCode);

        AnalysisService broken = new(bank, root, [new BrokenAnalyzer()]);
        Assert.Equal("AMP5343", Assert.Throws<AmpleException>(() => broken.Analyze(good, "tempo")).DiagnosticCode);
    }

    private sealed class BrokenAnalyzer : IAnalyzer
    {
        public string Id => "tempo";

        public string Version => "0";

        public string Description => "gives a result that does not fit";

        public string Analyze(AnalysisInput input) => "{\"bpm\":999,\"confidence\":2,\"beatSeconds\":[],\"firstBeatSeconds\":0}";
    }

    [Fact]
    public void The_schemas_accept_what_the_analysers_make_and_name_what_is_wrong()
    {
        foreach (IAnalyzer a in BuiltInAnalyzers.All)
        {
            Assert.Empty(AnalysisSchema.Validate(a.Id, a.Analyze(new AnalysisInput(Synth.Join(Synth.Tone(440, 2), Synth.Clicks(120, 6)), Synth.Rate))));
            Assert.Empty(AnalysisSchema.Validate(a.Id, a.Analyze(new AnalysisInput(new float[100], Synth.Rate))));
        }

        Assert.Contains("missing \"bpm\"", string.Join(" | ", AnalysisSchema.Validate("tempo", "{\"confidence\":1,\"beatSeconds\":[],\"firstBeatSeconds\":0}")), StringComparison.Ordinal);
        Assert.Contains("above 180", string.Join(" | ", AnalysisSchema.Validate("tempo", "{\"bpm\":300,\"confidence\":1,\"beatSeconds\":[],\"firstBeatSeconds\":0}")), StringComparison.Ordinal);
        Assert.Contains("expected", string.Join(" | ", AnalysisSchema.Validate("melody", "{\"schema\":\"ample-melody/1\",\"tempo\":120,\"notes\":[{\"pitch\":\"C\",\"start\":0,\"length\":1,\"bar\":1}]}")), StringComparison.Ordinal);
        Assert.Contains("not one of", string.Join(" | ", AnalysisSchema.Validate("melody", "{\"schema\":\"other\",\"tempo\":120,\"notes\":[]}")), StringComparison.Ordinal);
        Assert.Contains("no schema", string.Join(" | ", AnalysisSchema.Validate("nothing", "{}")), StringComparison.Ordinal);

        const string perStemMidi = "{\"tool\":\"test\",\"toolVersion\":\"1\",\"model\":\"generated\",\"sampleRate\":44100,\"sourceSamples\":44100,\"warnings\":[],\"midiArtifacts\":[{\"role\":\"backing-midi-bass-01\",\"label\":\"bass\"}]}";
        Assert.Empty(AnalysisSchema.Validate("transcription", perStemMidi));
        Assert.NotEmpty(AnalysisSchema.Validate("transcription", perStemMidi.Replace("\"label\":\"bass\"", "\"label\":\"private-title\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_analysis_plugin_answers_the_analyze_request_and_its_result_is_checked()
    {
        DiscoveredPlugin fake = Paths.Find("analysis-fake");
        Assert.Equal("analysis", fake.Manifest.Kind);
        Assert.Equal(["tempo", "key", "transcription"], fake.Manifest.Analyses);
        string job = Path.Combine(folder, "job");
        Directory.CreateDirectory(Path.Combine(job, "in"));
        await File.WriteAllBytesAsync(Path.Combine(job, "in", "audio.wav"), Wave(Synth.Tone(440, 1)));

        string tempo = await host.AnalyzeAsync(fake, new AnalysisJob(job, "tempo", "{}", TimeSpan.FromSeconds(30), null));
        Assert.Equal(120, JsonDocument.Parse(tempo).RootElement.GetProperty("bpm").GetDouble());
        Assert.True(JsonDocument.Parse(tempo).RootElement.GetProperty("inputBytes").GetInt64() > 40);

        AmpleException invalid = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(fake, new AnalysisJob(job, "key", "{}", TimeSpan.FromSeconds(30), null)));
        Assert.Equal("AMP5343", invalid.DiagnosticCode);
        AmpleException unknown = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(fake, new AnalysisJob(job, "pitch", "{}", TimeSpan.FromSeconds(30), null)));
        Assert.Equal("AMP5344", unknown.DiagnosticCode);
        AmpleException voice = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(Paths.Find("echo"), new AnalysisJob(job, "tempo", "{}", TimeSpan.FromSeconds(30), null)));
        Assert.Equal("AMP5344", voice.DiagnosticCode);
    }

    private DiscoveredPlugin Heavy(bool gpu, bool network)
    {
        string dir = Path.Combine(folder, "heavy-" + gpu + network);
        Directory.CreateDirectory(dir);
        string manifest = JsonSerializer.Serialize(new { id = "heavy", version = "1.0.0", title = "Model based analyser", kind = "analysis", protocolVersion = 1, entry = new { command = "ample-no-such-program-here", args = Array.Empty<string>() }, platforms = AllPlatforms, blocks = Array.Empty<string>(), analyses = PitchAnalyses, determinism = "unverified", toolprintFields = Array.Empty<string>(), needs = new { gpu, network, diskMb = 500, secrets = Array.Empty<string>() } });
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);
        return new DiscoveredPlugin(PluginManifest.Parse(manifest), dir);
    }

    [Theory]
    [InlineData(true, false, "the GPU")]
    [InlineData(false, true, "the network")]
    [InlineData(true, true, "the GPU and the network")]
    public async Task A_plugin_that_wants_the_gpu_or_the_network_is_not_started_without_a_yes(bool gpu, bool network, string what)
    {
        DiscoveredPlugin heavy = Heavy(gpu, network);
        AnalysisJob job = new(folder, "pitch", "{}", TimeSpan.FromSeconds(5), null);
        AmpleException none = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(heavy, job));
        Assert.Equal("AMP5340", none.DiagnosticCode);

        List<string> asked = [];
        AmpleException no = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(heavy, job, q => { asked.Add(q); return false; }));
        Assert.Equal("AMP5340", no.DiagnosticCode);
        Assert.Single(asked);
        Assert.Contains(what, asked[0], StringComparison.Ordinal);

        // With a yes the host goes on and only then fails, because the program of this fake does not exist.
        AmpleException yes = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeAsync(heavy, job, _ => true));
        Assert.NotEqual("AMP5340", yes.DiagnosticCode);
    }

    private sealed class PinnedMelody(string block, string dataId) : IBuildControl
    {
        public ulong? SeedFor(string name) => null;

        public string? SelectedTake(string name) => null;

        public string? PinnedMelodyDataId(string name) => name == block ? dataId : null;
    }

    [Fact]
    public async Task A_melody_plan_made_from_audio_feeds_a_re_sing_through_the_freeze_plan_level()
    {
        (Takebank bank, string root, Guid projectId) = await RigAsync();
        float[] melody = Synth.Join(Synth.Tone(261.63, 0.5, 0.5, 15), Synth.Tone(329.63, 0.5, 0.5, 15), Synth.Tone(392.0, 0.5, 0.5, 15));
        string sung = await TakeAsync(bank, Wave(melody), "sung");
        AnalysisRecord plan = new AnalysisService(bank, root).Analyze(sung, "melody", "{\"tempo\":120}");
        Assert.Equal([60, 64, 67], JsonDocument.Parse(plan.Json).RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("pitch").GetInt32()));

        string melodyId = await bank.PutDataAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(plan.Json)));
        IReadOnlyList<IBlockRenderer> renderers = await PluginRenderer.CreateAsync(host, [Paths.Find("echo")], Path.Combine(root, ".ample", "tmp", "jobs"));
        BuildEngine engine = new(bank, Paths.Spec, projectId, root);
        string song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nlyrics words {\n  lang nl\n}\nai-voice vox at 1 for 2 freeze plan {\n  voice anna\n  from words.words -> lyrics\n}\n";
        BuildReport report = await engine.BuildAsync(Parser.Parse(song, Paths.Spec), new BuildOptions(ExtraRenderers: renderers, Control: new PinnedMelody("vox", melodyId)));
        BlockResult vox = report.Blocks.Single(b => b.Block == "vox");
        Assert.Equal("rendered", vox.Status);
        StoredPlan stored = bank.GetPlan(bank.GetTake(vox.TakeId!).PlanId);
        Assert.Contains(stored.Plan.Inputs, i => i.Kind == "data" && i.Id == melodyId);
    }
}
