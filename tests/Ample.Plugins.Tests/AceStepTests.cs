using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Plugins.Tests;

internal sealed class StubServer : IDisposable
{
    private readonly Process process;

    private StubServer(Process process, int port)
    {
        this.process = process;
        Port = port;
    }

    public int Port { get; }

    public string Endpoint => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);

    public static async Task<StubServer> StartAsync(string mode)
    {
        ProcessStartInfo info = new("node", ["stub-server.mjs", mode]) { WorkingDirectory = Path.Combine(Paths.PluginFolder, "acestep"), RedirectStandardOutput = true, UseShellExecute = false };
        Process p = Process.Start(info)!;
        string? line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return new StubServer(p, int.Parse(line!["PORT ".Length..], CultureInfo.InvariantCulture));
    }

    public async Task<JsonElement> RequestsAsync()
    {
        using HttpClient client = new();
        return JsonDocument.Parse(await client.GetStringAsync(Endpoint + "/requests")).RootElement.Clone();
    }

    public async Task<JsonElement> CancelledAsync()
    {
        using HttpClient client = new();
        return JsonDocument.Parse(await client.GetStringAsync(Endpoint + "/cancelled")).RootElement.Clone();
    }

    public void Dispose()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
    }
}

[Trait("Category", "AceStep")]
public sealed class AceStepTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-acestep-").FullName;
    private readonly PluginHost host = new();

    public void Dispose()
    {
        host.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private static string Settings(string endpoint, int? timeoutSeconds)
    {
        Dictionary<string, object> settings = new() { ["endpoint"] = endpoint, ["pollMs"] = 20 };
        if (timeoutSeconds is not null) settings["timeoutS"] = timeoutSeconds.Value;
        return JsonSerializer.Serialize(settings);
    }

    private static readonly int[] Bars = [1, 2];

    private static string JobParams(
        string job, string settings, string seed = "42", string lyrics = "secret words of the song", string voice = "anna",
        double? beatsPerBar = null, int[]? bars = null)
    {
        Directory.CreateDirectory(Path.Combine(job, "in"));
        File.WriteAllText(Path.Combine(job, "in", "lyrics.bin"), lyrics);
        return JsonSerializer.Serialize(new
        {
            jobDir = job,
            block = new { type = "ai-voice", name = "vox", parameters = new { voice, steps = "6" } },
            seed, sampleRate = 8000, samplesPerBar = 4000, firstBar = 1, barCount = 2, activeBars = bars ?? Bars, beatsPerBar,
            inputs = new { lyrics = "in/lyrics.bin" },
            settings = JsonDocument.Parse(settings).RootElement,
        });
    }

    private async Task<PluginResult> Render(string settings, string seed = "42", double? beatsPerBar = null, int[]? bars = null)
    {
        string job = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string jobParams = JobParams(job, settings, seed, beatsPerBar: beatsPerBar, bars: bars);
        return await host.RenderAsync(Paths.Find("acestep"), new PluginJob(job, jobParams, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task The_plugin_maps_the_block_to_a_request_and_returns_mono_pcm_of_the_right_length()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        PluginResult result = await Render(Settings(server.Endpoint, null));
        (string role, string path) = Assert.Single(result.Outputs);
        Assert.Equal("vocal", role);
        byte[] wav = await File.ReadAllBytesAsync(path);
        Assert.Equal(44 + 8000 * 2, wav.Length);
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(8000, BitConverter.ToInt32(wav, 24));
        Assert.Contains(Enumerable.Range(0, 100).Select(i => BitConverter.ToInt16(wav, 44 + i * 2)), v => v != 0);

        JsonElement request = (await server.RequestsAsync())[0];
        Assert.Equal("isolated a cappella lead vocal only, no backing music, no accompaniment, anna", request.GetProperty("caption").GetString());
        Assert.Equal("secret words of the song", request.GetProperty("lyrics").GetString());
        Assert.Equal(42, request.GetProperty("seed").GetInt32());
        Assert.Equal("en", request.GetProperty("vocal_language").GetString());
        Assert.Equal(6, request.GetProperty("inference_steps").GetInt32());
        Assert.Equal(1.0, request.GetProperty("duration").GetDouble(), 3);
        JsonElement requests = await server.RequestsAsync();
        Assert.Equal(3, requests.GetArrayLength());
        Assert.Equal(1, requests[1].GetProperty("guidance_scale").GetDouble());
        JsonElement extraction = requests[2];
        Assert.Equal("extract", extraction.GetProperty("task_type").GetString());
        Assert.Equal("vocals", extraction.GetProperty("track").GetString());
        Assert.Equal("acestep-v15-base-Q8_0.gguf", extraction.GetProperty("synth_model").GetString());
        using JsonDocument meta = JsonDocument.Parse(result.MetaJson);
        Assert.Equal("none", meta.RootElement.GetProperty("melodyPlan").GetString());
        Assert.Equal("vocal-only", meta.RootElement.GetProperty("mode").GetString());
        Assert.Equal("extract", meta.RootElement.GetProperty("vocalIsolation").GetString());
    }

    [Fact]
    public async Task Music_can_be_instrumental_and_receives_linked_style_and_melody()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string job = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(job, "in"));
        await File.WriteAllTextAsync(Path.Combine(job, "in", "style.bin"), "late-night city glow");
        await File.WriteAllTextAsync(Path.Combine(job, "in", "melody.bin"), "{\"notes\":[{\"pitch\":60}]}");
        string parameters = JsonSerializer.Serialize(new { jobDir = job, block = new { type = "ace-music", name = "music", parameters = new { music_direction = "slow electronic pulse", steps = "6", guidance = "4.2" } }, seed = "42", sampleRate = 8000, samplesPerBar = 4000, firstBar = 1, barCount = 2, activeBars = Bars, inputs = new { style = "in/style.bin", melody = "in/melody.bin" }, settings = JsonDocument.Parse(JsonSerializer.Serialize(new { endpoint = server.Endpoint, pollMs = 20, synthModel = "sft" })).RootElement });

        PluginResult result = await host.RenderAsync(Paths.Find("acestep"), new PluginJob(job, parameters, TimeSpan.FromSeconds(60)));

        Assert.Equal("audio", Assert.Single(result.Outputs).Role);
        JsonElement request = (await server.RequestsAsync())[0];
        Assert.Equal("late-night city glow, slow electronic pulse", request.GetProperty("caption").GetString());
        Assert.Equal(string.Empty, request.GetProperty("lyrics").GetString());
        JsonElement synthesis = (await server.RequestsAsync())[1];
        Assert.Equal(4.2, synthesis.GetProperty("guidance_scale").GetDouble(), 3);
        Assert.Equal(60, request.GetProperty("melody").GetProperty("notes")[0].GetProperty("pitch").GetInt32());
        using JsonDocument meta = JsonDocument.Parse(result.MetaJson);
        Assert.Equal("requested", meta.RootElement.GetProperty("melodyPlan").GetString());
        Assert.Equal("music", meta.RootElement.GetProperty("mode").GetString());
        Assert.Equal(4.2, meta.RootElement.GetProperty("guidance").GetDouble(), 3);
    }

    [Fact]
    public async Task An_explicit_server_model_is_attempted_even_when_props_omits_it()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string settings = JsonSerializer.Serialize(new { endpoint = server.Endpoint, pollMs = 20, synthModel = "xl-sft", synthModelFile = "acestep-v15-xl-sft-BF16.gguf" });

        await Render(settings);

        Assert.Contains((await server.RequestsAsync()).EnumerateArray(), x => x.TryGetProperty("synth_model", out JsonElement model) && model.GetString() == "acestep-v15-xl-sft-BF16.gguf");
    }

    [Theory]
    [InlineData("xl-base", "acestep-v15-xl-base-Q5_K_M.gguf")]
    [InlineData("xl-sft", "acestep-v15-xl-sft-BF16.gguf")]
    [InlineData("default", "acestep-v15-xl-sft-BF16.gguf")]
    public async Task Base_and_sft_models_use_cfg_seven_unless_explicitly_overridden(string family, string file)
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string settings = JsonSerializer.Serialize(new { endpoint = server.Endpoint, pollMs = 20, synthModel = family, synthModelFile = file });

        await Render(settings);

        Assert.Equal(7, (await server.RequestsAsync())[1].GetProperty("guidance_scale").GetDouble());
    }

    [Fact]
    public async Task Explicit_server_model_failure_includes_the_raw_ace_reason()
    {
        using StubServer server = await StubServer.StartAsync("rejectmodel");
        string settings = JsonSerializer.Serialize(new { endpoint = server.Endpoint, pollMs = 20, synthModel = "xl-sft", synthModelFile = "acestep-v15-xl-sft-BF16.gguf" });

        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(settings));

        Assert.Contains("synthetic BF16 rejection detail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_audio_and_another_seed_gives_other_audio()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        byte[] a = await File.ReadAllBytesAsync((await Render(Settings(server.Endpoint, null), "7")).Outputs[0].Path);
        byte[] b = await File.ReadAllBytesAsync((await Render(Settings(server.Endpoint, null), "7")).Outputs[0].Path);
        byte[] c = await File.ReadAllBytesAsync((await Render(Settings(server.Endpoint, null), "8")).Outputs[0].Path);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task Lyrics_and_prompts_never_reach_the_logs()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        PluginResult result = await Render(Settings(server.Endpoint, null));
        Assert.NotEmpty(result.Logs);
        Assert.DoesNotContain(result.Logs, l => l.Contains("secret words", StringComparison.Ordinal) || l.Contains("airy", StringComparison.Ordinal) || l.Contains("anna", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("error500", "HTTP 500")]
    [InlineData("malformed", "not JSON")]
    [InlineData("noaudio", "holds no audio part")]
    [InlineData("jobfailed", "synthetic ACE worker failure detail")]
    public async Task Server_problems_become_plain_plugin_errors(string mode, string expected)
    {
        using StubServer server = await StubServer.StartAsync(mode);
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings(server.Endpoint, null)));
        Assert.Equal("AMP5278", ex.DiagnosticCode);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_server_and_a_server_that_never_finishes_are_reported_not_hung()
    {
        AmpleException down = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings("http://127.0.0.1:9", null)));
        Assert.Contains("could not be reached", down.Message, StringComparison.Ordinal);

        using StubServer slow = await StubServer.StartAsync("neverdone");
        AmpleException timeout = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings(slow.Endpoint, 5)));
        Assert.Contains("did not finish within 5 seconds", timeout.Message, StringComparison.Ordinal);
    }

[Theory]
    [InlineData(2.0, "2", 240)]
    [InlineData(3.0, "3", 360)]
    [InlineData(4.0, "4", 480)]
    public async Task Supported_meters_are_sent_with_a_bpm_from_the_bar_length(double beats, string signature, int bpm)
    {
        using StubServer server = await StubServer.StartAsync("ok");
        await Render(Settings(server.Endpoint, null), beatsPerBar: beats);
        JsonElement request = (await server.RequestsAsync())[0];
        Assert.Equal(signature, request.GetProperty("timesignature").GetString());
        Assert.Equal(bpm, request.GetProperty("bpm").GetInt32());
    }

    [Theory]
    [InlineData(3.5)]
    [InlineData(5.0)]
    [InlineData(6.0)]
    public async Task Unsupported_meters_are_refused_before_any_server_request(double beats)
    {
        using StubServer server = await StubServer.StartAsync("ok");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(
            async () => await Render(Settings(server.Endpoint, null), beatsPerBar: beats));
        Assert.Contains("2/4, 3/4 and 4/4", ex.Message, StringComparison.Ordinal);
        Assert.Empty((await server.RequestsAsync()).EnumerateArray());
    }

    [Fact]
    public async Task A_gap_between_active_bars_is_refused_but_duplicate_bars_are_not_a_gap()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(
            async () => await Render(Settings(server.Endpoint, null), bars: [1, 3]));
        Assert.Contains("gaps between its active bars", ex.Message, StringComparison.Ordinal);
        Assert.Empty((await server.RequestsAsync()).EnumerateArray());

        await Render(Settings(server.Endpoint, null), bars: [1, 1, 2]);
        Assert.Equal(1.0, (await server.RequestsAsync())[0].GetProperty("duration").GetDouble(), 3);
    }

    [Theory]
    [InlineData("stall:lm", "plan", true)]
    [InlineData("hangresult:lm", "plan", false)]
    [InlineData("stall:synth", "synthesis", true)]
    [InlineData("hangresult:synth", "synthesis", false)]
    [InlineData("stall:extract", "vocal extraction", true)]
    [InlineData("hangresult:extract", "vocal extraction", false)]
    public async Task The_one_deadline_stops_every_server_stage_and_names_it(string mode, string stage, bool jobRuns)
    {
        using StubServer server = await StubServer.StartAsync(mode);
        Stopwatch clock = Stopwatch.StartNew();
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings(server.Endpoint, 2)));
        Assert.Contains("did not finish within 2 seconds (stage: " + stage + ")", ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
        if (jobRuns) Assert.Equal(1, (await server.CancelledAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_non_numeric_timeout_falls_back_to_the_default_instead_of_never_expiring()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string settings = JsonSerializer.Serialize(new { endpoint = server.Endpoint, pollMs = 20, timeoutS = "soon" });
        PluginResult result = await Render(settings);
        Assert.Single(result.Outputs);
    }

    [Theory]
    [InlineData("no-such-python-for-ample", "was not found")]
    [InlineData("node", "Demucs failed (exit code")]
    public async Task Demucs_failures_are_told_apart_and_only_a_missing_module_gets_the_install_hint(string python, string expected)
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string settings = JsonSerializer.Serialize(
            new { endpoint = server.Endpoint, pollMs = 20, vocalIsolation = "demucs", demucsPython = python });
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(settings));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_endpoint_is_refused_unless_it_is_allowed()
    {
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings("http://example.invalid:8085", null)));
        Assert.Contains("not local", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AI_voice_requires_a_base_or_sft_model_for_vocal_isolation()
    {
        using StubServer server = await StubServer.StartAsync("nobase");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render(Settings(server.Endpoint, null)));
        Assert.Contains("Base or SFT", ex.Message, StringComparison.Ordinal);
        Assert.Empty((await server.RequestsAsync()).EnumerateArray());
    }

    [Fact]
    public async Task An_ai_voice_block_builds_with_the_stub_through_the_engine_and_the_second_build_is_cached()
    {
        using StubServer server = await StubServer.StartAsync("ok");
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        using OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "ace");
        Directory.CreateDirectory(Path.Combine(root, "lyrics"));
        File.WriteAllText(Path.Combine(root, "lyrics", "words.lyr"), "[Verse]\n[bar 1] la la\n");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        IReadOnlyList<IBlockRenderer> renderers = await PluginRenderer.CreateAsync(host, [Paths.Find("acestep")], Path.Combine(root, ".ample", "tmp", "jobs"), Settings(server.Endpoint, null));
        BuildEngine engine = new(bank, Paths.Spec, project.Manifest.Id, root);
        string song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nsettings { acestep.backend server acestep.endpoint " + server.Endpoint + " acestep.pollMs 20 }\nlyrics words at 1 for 1 {\n  lang en\n  file lyrics/words.lyr\n}\nai-voice vox at 1 for 1 {\n  voice anna\n  from words.words -> lyrics\n}\n";
        AmpleDocument doc = Parser.Parse(song, Paths.Spec);

        BuildReport first = await engine.BuildAsync(doc, new BuildOptions(ExtraRenderers: renderers));
        BlockResult vox = first.Blocks.Single(b => b.Block == "vox");
        Assert.True(vox.Status == "rendered", vox.Message);
        PlanRecord plan = bank.GetPlan(bank.GetTake(vox.TakeId!).PlanId).Plan;
        Assert.Equal("plugin.acestep", plan.PluginId);
        Assert.Contains("ace-server-lm-synth-job", plan.ToolprintJson, StringComparison.Ordinal);
        using JsonDocument provenance = JsonDocument.Parse(bank.GetTake(vox.TakeId!).MetaJson);
        Assert.Equal(1, provenance.RootElement.GetProperty("vocal").GetProperty("recipe").GetProperty("synthRequest").GetProperty("guidance_scale").GetInt32());
        // the provenance names the adapter by plugin.mjs and the file it starts processes with: a change in either shows
        JsonElement software = provenance.RootElement.GetProperty("vocal").GetProperty("recipe").GetProperty("software");
        string adapter = software.GetProperty("adapterSha256").GetString()!;
        string Sha(string file) =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Paths.PluginFolder, "acestep", file))));
        byte[] both = Encoding.ASCII.GetBytes(Sha("plugin.mjs") + Sha("run-process.mjs"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(both)), adapter);
        Assert.Contains("templateDefaults", plan.SettingsJson, StringComparison.Ordinal);
        Assert.Contains("pluginSettings", plan.SettingsJson, StringComparison.Ordinal);
        Assert.Equal("cached", (await engine.BuildAsync(doc, new BuildOptions(ExtraRenderers: renderers))).Blocks.Single(b => b.Block == "vox").Status);
        Assert.Equal("unverified", renderers[0].Determinism);

    }
}
