using Ample.Engine.Modules;
using Ample.Language;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

/// <summary>plan/diagnostics WP8: the project layer of the logging and assertion options (settings.observation of .ample/project.json) sits between the server and the song; the song, an instance and the session win over it.</summary>
public sealed class ProjectObservationLayerTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-project-layer-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // a locked file on Windows must not fail the run
        }
    }

    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Checked = "helper checked(Notes n) -> Notes {\n  assert 1 > 2, \"never true\"\n  return n\n}\n\n" +
        "midi-clip melody at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip checkedclip at 2 for auto {\n  notes melody.notes |> checked()\n}\n";

    private const string Audio = "component koda.fx.echo version 1.0.0 current {\n  id \"demo.koda.fx.echo\"\n  input audio: Audio\n  output audio: Audio\n" +
        "  process {\n    trace \"building\"\n    emit audio\n  }\n}\n\n" +
        "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\nkoda.fx.echo space {\n  from lead.audio -> audio\n}\n";

    private ModuleService Service(Dictionary<string, string>? project = null, Dictionary<string, string>? session = null) =>
        new(folder, (Ample.Storage.Library.Library?)null) { ObservationProject = project ?? [], ObservationSession = session ?? [] };

    private static bool Fails(AmpleDocument doc) => doc.Diagnostics.Any(d => d.Code == "AMP6004");

    [Fact]
    public void The_project_switches_assertions_on_and_the_song_and_the_session_win_over_it()
    {
        Dictionary<string, string> on = new() { ["diagnostics.assertions"] = "on" };
        Assert.False(Fails(Service().ParseExpanded(Header + Checked, EngineRig.Spec)));
        using ModuleService project = Service(on);
        Assert.True(Fails(project.ParseExpanded(Header + Checked, EngineRig.Spec)));
        Assert.False(Fails(project.ParseExpanded(Header + "settings { diagnostics.assertions off }\n\n" + Checked, EngineRig.Spec)));
        using ModuleService session = Service(on, new() { ["diagnostics.assertions"] = "off" });
        Assert.False(Fails(session.ParseExpanded(Header + Checked, EngineRig.Spec)));
    }

    [Fact]
    public void The_project_level_reaches_the_events_of_an_audio_process()
    {
        Assert.DoesNotContain(Service().ParseExpanded(Header + Audio, EngineRig.Spec).Observations, e => e.Message == "building");
        using ModuleService traced = Service(new() { ["logging.level"] = "trace" });
        Assert.Contains(traced.ParseExpanded(Header + Audio, EngineRig.Spec).Observations, e => e.Message == "building" && e.Level == EventLevel.Trace);
    }

    [Fact]
    public void The_project_file_keeps_the_options_and_writes_them_only_when_set()
    {
        ProjectManifest plain = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
        Assert.DoesNotContain("\"observation\"", ProjectFile.Serialize(plain), StringComparison.Ordinal);
        Assert.Empty(ProjectFile.Parse(ProjectFile.Serialize(plain)).Settings.Observation);
        ProjectManifest set = plain with { Settings = plain.Settings with { Observation = new Dictionary<string, string> { ["logging.level"] = "debug", ["diagnostics.assertions"] = "on" } } };
        IReadOnlyDictionary<string, string> back = ProjectFile.Parse(ProjectFile.Serialize(set)).Settings.Observation;
        Assert.Equal("debug", back["logging.level"]);
        Assert.Equal("on", back["diagnostics.assertions"]);
    }
}
