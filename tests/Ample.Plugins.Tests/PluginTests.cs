using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Plugins.Tests;

internal sealed class FixedSecrets : ISecretProvider
{
    public string? Lookup(string name) => name == "TEST_TOKEN" ? "hunter2-secret" : null;
}

internal static class Paths
{
    public static string AmpleRoot
    {
        get
        {
            static string? FindAmpleRoot(string? dir)
            {
                while (dir is not null && !Directory.Exists(Path.Combine(dir, "plugins", "echo")))
                {
                    dir = Path.GetDirectoryName(dir);
                }

                return dir;
            }

            return FindAmpleRoot(AppContext.BaseDirectory)
                ?? FindAmpleRoot(Environment.GetEnvironmentVariable("AMPLE_REPO_ROOT"))
                ?? FindAmpleRoot(Directory.GetCurrentDirectory())
                ?? throw new DirectoryNotFoundException("AMPLE folder not found");
        }
    }

    public static string PluginFolder => Path.Combine(AmpleRoot, "plugins");

    public static SpecData Spec
    {
        get
        {
            string spec = Path.Combine(AmpleRoot, "spec");
            return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
        }
    }

    public static DiscoveredPlugin Find(string id) => PluginHost.Discover([PluginFolder]).Plugins.Single(p => p.Manifest.Id == id);

    private static readonly int[] ActiveBars = [1, 2];

    public static string Params(string mode, string jobDir) => JsonSerializer.Serialize(new { jobDir, block = new { type = "ai-voice", name = "vox", parameters = new { voice = mode } }, seed = "42", sampleRate = 8000, samplesPerBar = 800, firstBar = 1, barCount = 2, activeBars = ActiveBars, inputs = new { }, settings = new { } });
}

[Trait("Category", "Plugins")]
public sealed class ManifestTests
{
    [Fact]
    public void The_reference_plugins_are_found_and_valid()
    {
        (IReadOnlyList<DiscoveredPlugin> plugins, IReadOnlyList<string> problems) = PluginHost.Discover([Paths.PluginFolder]);
        Assert.Empty(problems);
        Assert.Equal(["acestep", "analysis-fake", "analysis-local-transcription", "echo", "flaky", "voice-clone"], plugins.Select(p => p.Manifest.Id).Order());
        PluginManifest echo = plugins.Single(p => p.Manifest.Id == "echo").Manifest;
        Assert.Equal("exact", echo.Determinism);
        Assert.False(echo.Needs.Gpu);
        Assert.True(plugins.Single(p => p.Manifest.Id == "flaky").Manifest.Needs.Gpu);
        Assert.True(echo.SupportsThisPlatform);
    }

    [Theory]
    [InlineData("{}", "id must be")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{\"id\":\"Bad Id\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{\"command\":\"x\"},\"platforms\":[\"linux\"],\"blocks\":[\"a\"],\"determinism\":\"exact\"}", "id must be")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"kind\":\"magic\",\"protocolVersion\":1,\"entry\":{\"command\":\"x\"},\"platforms\":[\"linux\"],\"blocks\":[\"a\"],\"determinism\":\"exact\"}", "kind must be")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{},\"platforms\":[\"linux\"],\"blocks\":[\"a\"],\"determinism\":\"exact\"}", "entry.command is required")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{\"command\":\"x\"},\"platforms\":[\"amiga\"],\"blocks\":[\"a\"],\"determinism\":\"exact\"}", "platforms must list")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{\"command\":\"x\"},\"platforms\":[\"linux\"],\"blocks\":[],\"determinism\":\"exact\"}", "blocks must list")]
    [InlineData("{\"id\":\"x\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{\"command\":\"x\"},\"platforms\":[\"linux\"],\"blocks\":[\"a\"],\"determinism\":\"sometimes\"}", "determinism must be")]
    public void Broken_manifests_say_what_is_wrong(string json, string expected)
    {
        Assert.Contains(PluginManifest.Validate(json), e => e.Contains(expected, StringComparison.Ordinal));
        Assert.Equal("AMP5270", Assert.Throws<AmpleException>(() => PluginManifest.Parse(json)).DiagnosticCode);
    }

    [Fact]
    public void A_newer_protocol_is_refused_with_its_own_code()
    {
        string json = "{\"id\":\"x\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":2,\"entry\":{\"command\":\"x\"},\"platforms\":[\"linux\"],\"blocks\":[\"a\"],\"determinism\":\"exact\"}";
        Assert.Equal("AMP5271", Assert.Throws<AmpleException>(() => PluginManifest.Parse(json)).DiagnosticCode);
    }

    [Fact]
    public void Discovery_reports_a_broken_plugin_instead_of_failing()
    {
        string folder = Directory.CreateTempSubdirectory("ample-plugins-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "bad"));
            File.WriteAllText(Path.Combine(folder, "bad", "plugin.json"), "{}");
            (IReadOnlyList<DiscoveredPlugin> plugins, IReadOnlyList<string> problems) = PluginHost.Discover([folder, Path.Combine(folder, "missing")]);
            Assert.Empty(plugins);
            Assert.Contains("AMP5270", Assert.Single(problems), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Settings_are_checked_against_the_schema_and_defaults_are_filled()
    {
        string schema = Paths.Find("echo").Manifest.SettingsSchemaJson;
        (IReadOnlyList<string> ok, string filled) = SettingsValidator.Validate(schema, "{}");
        Assert.Empty(ok);
        Assert.Contains("\"pitchOffset\":0", filled, StringComparison.Ordinal);
        Assert.Contains("\"style\":\"airy\"", filled, StringComparison.Ordinal);
        Assert.Contains(SettingsValidator.Validate(schema, "{\"pitchOffset\":99}").Errors, e => e.Contains("at most 12", StringComparison.Ordinal));
        Assert.Contains(SettingsValidator.Validate(schema, "{\"pitchOffset\":\"high\"}").Errors, e => e.Contains("must be a integer", StringComparison.Ordinal));
        Assert.Contains(SettingsValidator.Validate(schema, "{\"style\":\"loud\"}").Errors, e => e.Contains("must be one of", StringComparison.Ordinal));
        Assert.Contains(SettingsValidator.Validate(schema, "{\"nope\":1}").Errors, e => e.Contains("not a setting", StringComparison.Ordinal));
    }
}

[Trait("Category", "Plugins")]
public sealed class HostTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-host-").FullName;
    private readonly PluginHost host = new(new FixedSecrets(), maxMessageBytes: 1024 * 1024);

    public void Dispose()
    {
        host.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private async Task<PluginResult> Render(string plugin, string mode, TimeSpan? timeout = null, CancellationToken token = default)
    {
        string job = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        return await host.RenderAsync(Paths.Find(plugin), new PluginJob(job, Paths.Params(mode, job), timeout ?? TimeSpan.FromSeconds(30)), token);
    }

    [Fact]
    public async Task The_echo_plugin_does_the_handshake_describes_itself_and_renders()
    {
        JsonElement d = await host.DescribeAsync(Paths.Find("echo"));
        Assert.Equal("echo", d.GetProperty("toolprint").GetProperty("plugin").GetString());

        PluginResult result = await Render("echo", "any");
        Assert.Equal(["vocal", "melody"], result.Outputs.Select(o => o.Role));
        Assert.All(result.Outputs, o => Assert.True(File.Exists(o.Path)));
        Assert.Equal(44 + 2 * 1600, new FileInfo(result.Outputs[0].Path).Length);
        Assert.Contains(result.Logs, l => l.Contains("rendered 1600 samples", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_audio_analysis_plugin_returns_bounded_stem_artifacts_in_its_job_folder()
    {
        string job = Path.Combine(folder, "transcription");
        Directory.CreateDirectory(Path.Combine(job, "in"));
        await File.WriteAllBytesAsync(Path.Combine(job, "in", "audio.wav"), [1, 2, 3, 4], CancellationToken.None);
        List<ProgressReport> progress = [];
        AnalysisPluginResult result = await host.AnalyzeWithOutputsAsync(Paths.Find("analysis-fake"), new AnalysisJob(job, "transcription", "{}", TimeSpan.FromSeconds(30), progress.Add), null, true, CancellationToken.None);
        Assert.Equal("analysis-fake", JsonDocument.Parse(result.Json).RootElement.GetProperty("tool").GetString());
        Assert.Equal(["vocals", "backing"], result.Outputs.Select(o => o.Role));
        Assert.All(result.Outputs, output => Assert.True(ProjectPath.IsInside(job, output.Path) && File.Exists(output.Path)));
        Assert.Contains(progress, report => report.Step == "separation" && report.Fraction == 0.5);
    }

    [Fact]
    public async Task An_audio_analysis_plugin_cannot_return_an_artifact_outside_its_job_folder()
    {
        string job = Path.Combine(folder, "transcription-escape");
        Directory.CreateDirectory(Path.Combine(job, "in"));
        await File.WriteAllBytesAsync(Path.Combine(job, "in", "audio.wav"), [1, 2, 3, 4], CancellationToken.None);
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await host.AnalyzeWithOutputsAsync(Paths.Find("analysis-fake"), new AnalysisJob(job, "transcription", "{\"escape\":true}", TimeSpan.FromSeconds(30), null), null, true, CancellationToken.None));
        Assert.Equal("AMP5272", error.DiagnosticCode);
    }

    [Fact]
    public async Task The_plugin_gets_a_clean_environment_and_only_the_secrets_it_declared()
    {
        Environment.SetEnvironmentVariable("AMPLE_TEST_SECRET", "leak-me");
        try
        {
            PluginResult result = await Render("echo", "any");
            using JsonDocument meta = JsonDocument.Parse(result.MetaJson);
            Assert.False(meta.RootElement.GetProperty("hasTestSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("token").ValueKind);
            Assert.Contains(meta.RootElement.GetProperty("envKeys").EnumerateArray().Select(e => e.GetString()), k => k == "AMPLE_PLUGIN_ID");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMPLE_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task A_declared_secret_reaches_the_plugin_and_is_redacted_from_its_logs()
    {
        PluginResult result = await Render("flaky", "logsecret");
        Assert.DoesNotContain(result.Logs, l => l.Contains("hunter2-secret", StringComparison.Ordinal));
        Assert.Contains(result.Logs, l => l.Contains("using token [REDACTED]", StringComparison.Ordinal));   // the log call's line; the stderr line travels on another pipe and may arrive after the answer
        Assert.Contains(result.Logs, l => l.Contains("using token [REDACTED]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("crash", "AMP5275")]
    [InlineData("garbage", "AMP5273")]
    [InlineData("huge", "AMP5274")]
    [InlineData("escape", "AMP5272")]
    [InlineData("error", "AMP5278")]
    public async Task A_misbehaving_plugin_is_contained_with_a_stable_code(string mode, string code)
    {
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render("flaky", mode));
        Assert.Equal(code, ex.DiagnosticCode);
    }

    [Fact]
    public async Task A_plugin_that_hangs_is_stopped_at_the_timeout_and_cancellation_stops_it_at_once()
    {
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await Render("flaky", "hang", TimeSpan.FromSeconds(1)));
        Assert.Equal("AMP5276", ex.DiagnosticCode);

        using CancellationTokenSource cts = new();
        cts.CancelAfter(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Render("flaky", "hang", TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Gpu_plugins_run_one_job_at_a_time()
    {
        PluginResult[] results = await Task.WhenAll(Render("flaky", "slow"), Render("flaky", "slow"));
        long[][] spans = [.. results.Select(r =>
        {
            using JsonDocument d = JsonDocument.Parse(r.MetaJson);
            return new[] { d.RootElement.GetProperty("start").GetInt64(), d.RootElement.GetProperty("end").GetInt64() };
        }).OrderBy(s => s[0])];
        Assert.True(spans[0][1] <= spans[1][0], "the two GPU jobs overlapped");
    }

    [Fact]
    public async Task A_missing_program_is_reported()
    {
        string dir = Path.Combine(folder, "ghost");
        Directory.CreateDirectory(dir);
        PluginManifest m = PluginManifest.Parse("{\"id\":\"ghost\",\"version\":\"1\",\"kind\":\"voice\",\"protocolVersion\":1,\"entry\":{\"command\":\"no-such-program-ample\"},\"platforms\":[\"windows\",\"linux\",\"macos\"],\"blocks\":[\"ai-voice\"],\"determinism\":\"exact\"}");
        AmpleException ex = await Assert.ThrowsAsync<AmpleException>(async () => await host.DescribeAsync(new DiscoveredPlugin(m, dir)));
        Assert.Equal("AMP5277", ex.DiagnosticCode);
    }
}

[Trait("Category", "Plugins")]
public sealed class EngineIntegrationTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-pluginengine-").FullName;
    private readonly PluginHost host = new(new FixedSecrets(), maxMessageBytes: 1024 * 1024);

    public void Dispose()
    {
        host.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private static string Song(string mode) => $"composition \"demo\" {{ ample 1  tempo 120  master-seed 7 }}\nlyrics words {{\n  lang nl\n}}\nai-voice vox at 1 for 2 {{\n  voice {mode}\n  from words.words -> lyrics\n}}\n";

    private async Task<(Takebank Bank, BuildEngine Engine, IReadOnlyList<IBlockRenderer> Renderers)> Rig(string plugin)
    {
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        using OpenProject project = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "plugins");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        IReadOnlyList<IBlockRenderer> renderers = await PluginRenderer.CreateAsync(host, [Paths.Find(plugin)], Path.Combine(root, ".ample", "tmp", "jobs"));
        return (bank, new BuildEngine(bank, Paths.Spec, project.Manifest.Id, root), renderers);
    }

    [Fact]
    public async Task A_plugin_block_is_built_cached_and_recorded_with_its_toolprint()
    {
        (Takebank bank, BuildEngine engine, IReadOnlyList<IBlockRenderer> renderers) = await Rig("echo");
        AmpleDocument doc = Parser.Parse(Song("anna"), Paths.Spec);
        BuildReport first = await engine.BuildAsync(doc, new BuildOptions(ExtraRenderers: renderers));
        BlockResult vox = first.Blocks.Single(b => b.Block == "vox");
        Assert.Equal("rendered", vox.Status);

        TakeRecord take = bank.GetTake(vox.TakeId!);
        Assert.Equal(["melody", "render-recipe", "vocal"], take.Data.Select(d => d.Role));
        PlanRecord plan = bank.GetPlan(take.PlanId).Plan;
        Assert.Equal("plugin.echo", plan.PluginId);
        Assert.Contains("echo-saw-1", plan.ToolprintJson, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(Path.Combine(folder, "proj", ".ample", "tmp", "jobs")));

        BuildReport second = await engine.BuildAsync(doc, new BuildOptions(ExtraRenderers: renderers));
        Assert.Equal("cached", second.Blocks.Single(b => b.Block == "vox").Status);
    }

    [Theory]
    [InlineData("crash", "AMP5275")]
    [InlineData("garbage", "AMP5273")]
    public async Task A_crashing_plugin_gives_a_failed_attempt_and_no_take_and_the_next_build_recovers(string mode, string code)
    {
        (Takebank bank, BuildEngine engine, IReadOnlyList<IBlockRenderer> renderers) = await Rig("flaky");
        BuildReport report = await engine.BuildAsync(Parser.Parse(Song(mode), Paths.Spec), new BuildOptions(ExtraRenderers: renderers));
        BlockResult vox = report.Blocks.Single(b => b.Block == "vox");
        Assert.Equal("failed", vox.Status);
        Assert.Contains(code, vox.Message, StringComparison.Ordinal);
        Assert.Single(bank.ListAttempts("failed"));
        Assert.DoesNotContain(bank.ListTakes(), t => bank.GetPlan(t.PlanId).Plan.BlockPath == "vox");

        BuildReport fixedUp = await engine.BuildAsync(Parser.Parse(Song("fine"), Paths.Spec), new BuildOptions(ExtraRenderers: renderers));
        Assert.Equal("rendered", fixedUp.Blocks.Single(b => b.Block == "vox").Status);
    }
}

[Trait("Category", "Vault")]
public sealed class VaultSecretTests : IDisposable
{
    private const string Canary = "CANARY-vault-9c1e-do-not-leak";
    private readonly string folder = Directory.CreateTempSubdirectory("ample-vault-plugin-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public async Task A_secret_from_the_vault_reaches_the_plugin_that_declared_it_and_never_its_logs_or_job_folder()
    {
        string home = Path.Combine(folder, "home");
        Ample.Storage.Security.PassphraseVault.Create(home, "a long enough passphrase", 1000).Set("TEST_TOKEN", Canary);
        using PluginHost host = new(new VaultSecretProvider(Ample.Storage.Security.PassphraseVault.Open(home, "a long enough passphrase")), maxMessageBytes: 1024 * 1024);
        string job = Path.Combine(folder, "job");
        Directory.CreateDirectory(job);
        PluginResult result = await host.RenderAsync(Paths.Find("flaky"), new PluginJob(job, Paths.Params("logsecret", job), TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.DoesNotContain(result.Logs, l => l.Contains(Canary, StringComparison.Ordinal));
        Assert.Contains(result.Logs, l => l.Contains("using token [REDACTED]", StringComparison.Ordinal));   // the log call's line; the stderr line travels on another pipe and may arrive after the answer
        Assert.DoesNotContain(Directory.EnumerateFiles(job, "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public void Without_the_passphrase_in_the_environment_plugins_get_no_secrets()
    {
        string home = Path.Combine(folder, "home2");
        Ample.Storage.Security.PassphraseVault.Create(home, "a long enough passphrase", 1000).Set("TEST_TOKEN", Canary);
        Assert.Null(VaultSecretProvider.FromEnvironment(home));
    }
}
