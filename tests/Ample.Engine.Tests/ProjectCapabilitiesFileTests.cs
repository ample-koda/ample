using Ample.Audio.IO;
using Ample.Engine.Modules;
using Ample.Language;
using Ample.Storage.Projects;

namespace Ample.Core.Tests;

/// <summary>The capabilities of a project are kept in .ample/project.json (settings.capabilities): read and written without losing anything else, and used by the CLI as by the server (docs/language/16).</summary>
public sealed class ProjectCapabilitiesFileTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-project-caps-").FullName;

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

    [Fact]
    public void The_capabilities_round_trip_with_every_other_setting()
    {
        ProjectManifest plain = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
        Assert.DoesNotContain("\"capabilities\"", ProjectFile.Serialize(plain), StringComparison.Ordinal);   // nothing set: nothing written, as before
        ProjectManifest set = plain with
        {
            Settings = plain.Settings with
            {
                Capabilities = new Dictionary<string, string> { ["environment"] = "on", ["environment.allow"] = "TZ, AMPLE_*", ["network"] = "off" },
                Observation = new Dictionary<string, string> { ["logging.level"] = "debug" },
                BuildParallelism = 2,
            },
        };
        string json = ProjectFile.Serialize(set);
        ProjectManifest back = ProjectFile.Parse(json);
        Assert.Equal(set.Settings.Capabilities.OrderBy(p => p.Key), back.Settings.Capabilities.OrderBy(p => p.Key));
        Assert.Equal("debug", back.Settings.Observation["logging.level"]);
        Assert.Equal(2, back.Settings.BuildParallelism);
        Assert.Equal(set.Settings.BlackBox, back.Settings.BlackBox);
        Assert.Equal(json, ProjectFile.Serialize(back));   // a second write changes nothing
    }

    [Fact]
    public void Keys_this_AMPLE_does_not_know_survive_a_read_and_a_write()
    {
        string json = ProjectFile.Serialize(ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1))
            .Replace("\"settings\": {", "\"futureTop\": [1, 2, {\"a\": true}],\n  \"settings\": {\n    \"futureSetting\": {\"x\": 1.5, \"y\": \"z\"},", StringComparison.Ordinal);
        ProjectManifest read = ProjectFile.Parse(json);
        Assert.Equal("[1, 2, {\"a\": true}]", read.Unknown["futureTop"]);
        Assert.Equal("{\"x\": 1.5, \"y\": \"z\"}", read.Settings.Unknown["futureSetting"]);
        // a write that changes something else (here the master seed) keeps them, and so does every write after it
        string written = ProjectFile.Serialize(read with { MasterSeed = 42 });
        ProjectManifest again = ProjectFile.Parse(written);
        Assert.Equal(read.Unknown["futureTop"], again.Unknown["futureTop"]);
        Assert.Equal(read.Settings.Unknown["futureSetting"], again.Settings.Unknown["futureSetting"]);
        Assert.Equal(42UL, again.MasterSeed);
        Assert.Equal(written, ProjectFile.Serialize(again));
    }

    [Fact]
    public void Every_key_this_AMPLE_writes_is_one_it_knows()
    {
        // a key written but not listed as known would come back as unknown and be written twice
        ProjectManifest plain = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
        ProjectManifest full = plain with
        {
            Settings = plain.Settings with
            {
                Capabilities = new Dictionary<string, string> { ["network"] = "off" },
                Observation = new Dictionary<string, string> { ["logging.level"] = "info" },
                BuildParallelism = 2,
                Koda = new KodaSettings("nl", "m", "low"),
                FirstRun = FirstRunSettings.Default.Completed(DateTimeOffset.UtcNow),
                DefaultEngine = "x",
            },
        };
        ProjectManifest read = ProjectFile.Parse(ProjectFile.Serialize(full));
        Assert.Empty(read.Unknown);
        Assert.Empty(read.Settings.Unknown);
    }

    [Fact]
    public void A_value_that_is_not_a_text_is_kept_so_the_check_can_report_it()
    {
        string json = ProjectFile.Serialize(ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1))
            .Replace("\"settings\": {", "\"settings\": {\n    \"capabilities\": { \"environment\": true },", StringComparison.Ordinal);
        Assert.Equal("true", ProjectFile.Parse(json).Settings.Capabilities["environment"]);
    }

    [Fact]
    public void The_cli_reads_the_capabilities_of_the_project_file()
    {
        Directory.CreateDirectory(Path.Combine(folder, ProjectLayout.AmpleFolder));
        ProjectManifest m = ProjectFile.CreateNew(Guid.NewGuid(), "p", DateTimeOffset.UtcNow, 1);
        File.WriteAllText(Path.Combine(folder, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile),
            ProjectFile.Serialize(m with { Settings = m.Settings with { Capabilities = new Dictionary<string, string> { ["environment"] = "on" } } }));
        using ModuleService service = ModuleService.WithUserLibrary(folder, new SystemClock());
        AmpleDocument doc = service.ParseExpanded("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n", EngineRig.Spec);
        Assert.True(doc.Capabilities.Single(c => c.Name == "environment").Effective);
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2097");
    }

    [Fact]
    public void Without_a_project_file_or_with_one_that_cannot_be_read_nothing_changes()
    {
        using ModuleService none = ModuleService.WithUserLibrary(folder, new SystemClock());
        Assert.False(none.ParseExpanded("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n", EngineRig.Spec).Capabilities.Single(c => c.Name == "environment").Effective);
        Directory.CreateDirectory(Path.Combine(folder, ProjectLayout.AmpleFolder));
        File.WriteAllText(Path.Combine(folder, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile), "not json");
        using ModuleService broken = ModuleService.WithUserLibrary(folder, new SystemClock());
        Assert.False(broken.ParseExpanded("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n", EngineRig.Spec).Capabilities.Single(c => c.Name == "environment").Effective);
    }
}
