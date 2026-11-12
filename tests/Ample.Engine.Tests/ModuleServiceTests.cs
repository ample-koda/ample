using Ample.Core.Tests.Instruments;
using Ample.Engine.Engine;
using Ample.Engine.Library;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "Modules")]
public sealed class ModuleServiceTests : IDisposable
{
    private const string Groove = "module groove version 1.2\nparam kit kit/tape\ninput chords\noutput audio mix.mix\n\nbass bass {\n  from in.chords -> notes\n}\ndrums drums {\n  kit $kit\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n}\n";
    private const string Song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\nuse groove@1.0 as intro {\n  from chords.notes -> chords\n}\nuse groove as outro {\n  kit kit/808\n  from chords.notes -> chords\n}\nmixer final {\n  from intro.audio -> audio1\n  from outro.audio -> audio2\n}\nmaster master {\n  from final.audio -> audio\n}\n";

    private readonly string folder = Directory.CreateTempSubdirectory("ample-modules-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private ModuleService Service()
    {
        Directory.CreateDirectory(Path.Combine(folder, "modules"));
        File.WriteAllText(Path.Combine(folder, "modules", "groove.module"), Groove);
        return new ModuleService(folder, (Ample.Storage.Library.Library?)null);
    }

    [Fact]
    public void Modules_are_found_in_the_project_listed_and_expanded_for_building()
    {
        using ModuleService service = Service();
        Assert.Equal(["groove"], service.ProjectModules().Select(m => m.Name));
        Assert.Null(service.Find("nothing"));
        Assert.Null(service.Find("../escape"));
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(doc.Blocks, b => b.Name == "intro/drums");
        Assert.Contains(doc.Blocks, b => b.Name == "outro/drums");
        Assert.Equal("kit/808", doc.Blocks.First(b => b.Name == "outro/drums").Parameters.Single(p => p.Name == "kit").Value);
        Assert.Equal("kit/tape", doc.Blocks.First(b => b.Name == "intro/drums").Parameters.Single(p => p.Name == "kit").Value);
    }

    [Fact]
    public void A_missing_module_is_an_error_at_the_use_line()
    {
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Diagnostic d = doc.Diagnostics.First(x => x.Code == "AMP5320");
        Assert.Equal(5, d.Line);
    }

    [Fact]
    public async Task Building_a_song_with_modules_is_stable_and_every_instance_gets_its_own_seeds()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        using ModuleService service = Service();
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        BuildReport first = await rig.Engine.BuildAsync(doc);
        Assert.DoesNotContain(first.Blocks, b => b.Status == "failed");
        BuildReport second = await rig.Engine.BuildAsync(doc);
        Assert.True(second.Blocks.All(b => b.Status == "cached"), string.Join(",", second.Blocks.Select(b => b.Block + ":" + b.Status)));
        Assert.Equal(first.MasterTakeId, second.MasterTakeId);
        string intro = first.Blocks.Single(b => b.Block == "intro/drums").TakeId!;
        string outro = first.Blocks.Single(b => b.Block == "outro/drums").TakeId!;
        Assert.NotEqual(intro, outro);
    }

    [Fact]
    public async Task Build_uses_unsaved_module_events_and_invalidates_instrument_cache()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "modules"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "event-test.sf2"), Sf2Fixture.Tiny());
        string module = "module playback version 1.0\noutput audio mix.mix\n# midi-source version=1 format=0 ppq=480\nmidi-clip track-001 at 1 for 1 {\n  ppq 480\n  notes 1:0:480:60:96:0:0:5:0\n}\ngm instrument-001-gm-00000-005-ch00 at 1 for 1 {\n  file event-test.sf2\n  bank 0\n  program 5\n  channel 0\n  from track-001.notes -> notes\n}\nmixer mix {\n  from instrument-001-gm-00000-005-ch00.audio -> audio1\n}\n# source-event track=0 tick=0 kind=ControlChange channel=0 data1=7 data2=127 meta=none payload=\n";
        await File.WriteAllTextAsync(Path.Combine(rig.ProjectRoot, "modules", "playback.module"), module);
        using ModuleService service = new(rig.ProjectRoot, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded("composition \"demo\" { ample 1  tempo 100  master-seed 7 }\nuse playback@1.0 as imported {\n}\nmaster master {\n  from imported.audio -> audio\n}\n", EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        string workingCopy = module.Replace("data2=127", "data2=0", StringComparison.Ordinal);
        BuildOptions changed = new() { ModuleSources = new Dictionary<string, string>(StringComparer.Ordinal) { ["modules/playback.module"] = workingCopy } };

        BuildReport saved = await rig.Engine.BuildAsync(doc);
        BuildReport edited = await rig.Engine.BuildAsync(doc, changed);
        BuildReport repeated = await rig.Engine.BuildAsync(doc, changed);

        Assert.Equal("rendered", saved.Blocks.Single(b => b.Block == "imported/instrument-001-gm-00000-005-ch00").Status);
        Assert.Equal("rendered", edited.Blocks.Single(b => b.Block == "imported/instrument-001-gm-00000-005-ch00").Status);
        Assert.Equal("cached", repeated.Blocks.Single(b => b.Block == "imported/instrument-001-gm-00000-005-ch00").Status);
    }

    [Fact]
    public async Task The_lock_file_pins_modules_and_a_changed_module_is_reported()
    {
        using ModuleService service = Service();
        ExpandedSong expanded = service.Expand(Song, EngineRig.Spec);
        IReadOnlyList<LockEntry> pins = ModuleService.Pins(expanded.Used);
        string text = LockFile.Serialize(pins);
        Assert.Contains("modules", text, StringComparison.Ordinal);
        Assert.Equal("module", LockFile.Parse(text).Single().Section);
        Assert.All(service.VerifyPins(pins), c => Assert.Equal("ok", c.Status));

        await File.WriteAllTextAsync(Path.Combine(folder, "modules", "groove.module"), Groove + "# changed\n");
        Assert.Equal("changed", service.VerifyPins(pins).Single().Status);
        await File.WriteAllTextAsync(Path.Combine(folder, LockFile.FileName), text);
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP5328" && d.Severity == Severity.Warning);
        File.Delete(Path.Combine(folder, "modules", "groove.module"));
        Assert.Equal("missing", service.VerifyPins(pins).Single().Status);
    }

    [Fact]
    public void A_song_without_modules_parses_as_it_always_did()
    {
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        string plain = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n}\n";
        Assert.Equal(Parser.Parse(plain, EngineRig.Spec).Blocks.Count, service.ParseExpanded(plain, EngineRig.Spec).Blocks.Count);
    }
}
