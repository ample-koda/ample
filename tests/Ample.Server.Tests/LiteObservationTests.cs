using System.Text;
using System.Text.Json;
using Ample.Language;
using Ample.Server.Lite;

namespace Ample.Server.Tests;

/// <summary>Remix and the log view (plan/diagnostics WP8): a build may ask for a log level and for assertions for itself only; the answer carries the events of the log view;
/// none of it changes the music; the same holds through the isolated worker.</summary>
public sealed class LiteObservationTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ample-lite-observe-" + Guid.NewGuid().ToString("N"));

    private static readonly string Root = FindRoot();

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private const string Song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n\n"
        + "helper checked(Notes n) -> Notes {\n  log \"checking\", context={count: 2}\n  debug \"inner\"\n  assert 1 > 2, \"never true\"\n  return n\n}\n\n"
        + "midi-clip melody at 1 for 2 {\n  notes \"1:0:1:60:100 1:1:1:64:100 2:0:2:67:100\"\n}\n\n"
        + "midi-clip tune at 1 for 2 {\n  notes melody.notes |> checked()\n}\n\n"
        + "synth lead at 1 for 2 {\n  wave square\n  from tune.notes -> notes\n}\n\n"
        + "master out {\n  from lead.audio -> audio\n}\n";

    public void Dispose()
    {
        try
        {
            Directory.Delete(home, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("AMPLE root not found.");
    }

    private static SpecData Spec => Ample.Docs.SpecRoot.At(Root).LoadSpecData();

    private static List<LiteFile> Files => [new LiteFile("song.amplet", Encoding.UTF8.GetBytes(Song))];

    private async Task<(LiteBuildResult Result, byte[]? Wav)> Build(Dictionary<string, string>? options)
    {
        LiteOutputs outputs = new();
        LiteBuildResult result = await LiteBuilder.BuildAsync(Spec, home, "song.amplet", Files, LiteLimits.Default, outputs, CancellationToken.None, null, options);
        return (result, result.OutputId is null ? null : outputs.Get(result.OutputId));
    }

    [Fact]
    public async Task A_log_level_for_one_build_brings_the_events_and_never_changes_the_music()
    {
        (LiteBuildResult plain, byte[]? plainWav) = await Build(null);
        Assert.True(plain.Ok, string.Join("; ", plain.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Empty(plain.Observations ?? []);   // the default level keeps no log
        (LiteBuildResult logged, byte[]? loggedWav) = await Build(new() { ["logging.level"] = "debug" });
        Assert.True(logged.Ok);
        Assert.Equal(["info", "debug"], logged.Observations!.Select(o => o.Level).Distinct());
        LiteObservation first = logged.Observations!.First(o => o.Message == "checking");
        Assert.Equal("song", first.Origin);
        Assert.True(first.Line > 0);
        Assert.Equal(2, first.Context!.Value.GetProperty("count").GetInt32());
        Assert.Equal(plainWav, loggedWav);   // the same audio, byte for byte
    }

    [Fact]
    public async Task Assertions_for_one_build_check_the_assert_and_a_wrong_option_is_refused()
    {
        (LiteBuildResult checkedBuild, _) = await Build(new() { ["diagnostics.assertions"] = "on" });
        Assert.False(checkedBuild.Ok);
        Assert.Contains(checkedBuild.Diagnostics, d => d.Code == "AMP6004");
        using JsonDocument bad = JsonDocument.Parse("{\"logging.level\": \"loud\"}");
        Assert.Equal("AMP6030", Assert.Throws<LiteRejectedException>(() => LiteBuilder.ObservationOptions(bad.RootElement)).Code);
        using JsonDocument unknown = JsonDocument.Parse("{\"capabilities.environment\": \"on\"}");
        Assert.Throws<LiteRejectedException>(() => LiteBuilder.ObservationOptions(unknown.RootElement));   // only logging and diagnostics options, never a capability
        Assert.Empty(LiteBuilder.ObservationOptions(null));
    }

    [Fact]
    public async Task The_isolated_worker_carries_the_options_and_the_events()
    {
        using MemoryStream request = new();
        await LiteWorker.WriteRequestAsync(request, "song.amplet", Files, LiteLimits.Default, CancellationToken.None, new Dictionary<string, string> { ["logging.level"] = "info" });
        using MemoryStream input = new(request.ToArray());
        using MemoryStream output = new();
        Directory.CreateDirectory(home);
        Assert.Equal(0, await LiteWorker.RunAsync(input, output, Root, home, CancellationToken.None));
        string resultLine = Encoding.UTF8.GetString(output.ToArray()).Split('\n').Last(l => l.Contains("\"t\":\"result\"", StringComparison.Ordinal));
        using JsonDocument doc = JsonDocument.Parse(resultLine);
        LiteBuildResult result = doc.RootElement.GetProperty("result").Deserialize<LiteBuildResult>(WebJson)!;
        Assert.Contains(result.Observations!, o => o.Level == "info" && o.Message == "checking");
        Assert.DoesNotContain(result.Observations!, o => o.Level == "debug");   // info, not debug
    }

    [Fact]
    public async Task The_diagnostics_demo_explains_itself_at_every_level_and_always_sounds_the_same()
    {
        string library = Ample.Storage.Library.Library.DefaultHome();
        if (!Directory.Exists(Path.Combine(library, "audio-packs", "licensed", "musescore-general"))) return;   // the demo plays the shared sound bank
        List<LiteFile> files = [new LiteFile("song.amplet", File.ReadAllBytes(Path.Combine(Root, "web", "remix", "demos", "watch-it-think", "song.amplet")))];
        async Task<(LiteBuildResult Result, byte[]? Wav)> Run(Dictionary<string, string>? options)
        {
            LiteOutputs outputs = new();
            LiteBuildResult result = await LiteBuilder.BuildAsync(Spec, library, "song.amplet", files, LiteLimits.Default, outputs, CancellationToken.None, null, options);
            Assert.True(result.Ok, string.Join("; ", result.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Code + " " + d.Message)));
            return (result, outputs.Get(result.OutputId!));
        }

        (LiteBuildResult plain, byte[]? plainWav) = await Run(null);
        Assert.Contains(plain.Diagnostics, d => d.Code == "AMP6001");   // warn is always kept
        Assert.All(plain.Observations ?? [], o => Assert.Equal("warning", o.Level));
        (LiteBuildResult traced, byte[]? tracedWav) = await Run(new() { ["logging.level"] = "trace", ["diagnostics.assertions"] = "on" });
        Assert.Equal(["debug", "info", "trace", "warning"], traced.Observations!.Select(o => o.Level).Distinct().Order(StringComparer.Ordinal));   // every level shows something
        Assert.Contains(traced.Observations!, o => o.Origin == "component:koda.watch.room");   // the audio component reports too
        Assert.DoesNotContain(traced.Diagnostics, d => d.Code == "AMP6004");   // every assert of the demo holds
        Assert.Equal(plainWav, tracedWav);   // what is shown never changes what is heard
    }
}
