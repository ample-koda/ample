using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The warn of an audio component reaches the document of the engine twice, as a diagnostic of the instance and as an event of the log view (plan R1.7, WP8 closing item).</summary>
[Trait("Category", "Modules")]
public sealed class AudioWarnObservationTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-audio-warn-").FullName;

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

    private const string Song =
        "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n" +
        "component koda.fx.echo version 1.0.0 current {\n  id \"demo.koda.fx.echo\"\n  input audio: Audio\n  property room: Percent = 40%\n  output audio: Audio\n" +
        "  process {\n    warn \"The room is large\", code=\"ROOM_LARGE\"\n    emit audio\n      |> reverb(size=room)\n  }\n}\n\n" +
        "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\n" +
        "koda.fx.echo space {\n  from lead.audio -> audio\n}\n";

    [Fact]
    public void The_warning_is_a_diagnostic_and_an_event_of_the_log_view()
    {
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Diagnostic diagnostic = Assert.Single(doc.Diagnostics, d => d.Code == "AMP6001");
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        LogEvent entry = Assert.Single(doc.Observations, e => e.Code == "ROOM_LARGE");
        Assert.Equal("component:koda.fx.echo", entry.Site!.Origin);
        Assert.Equal("space", Assert.Single(entry.Stack).Instance);
    }
}
