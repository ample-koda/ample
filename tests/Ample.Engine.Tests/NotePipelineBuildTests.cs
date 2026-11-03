using Ample.Engine.Engine;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A note pipeline in a song gives the build exactly the notes the same literal would give (the midi-clip renderer is not changed, so cached takes stay valid).</summary>
[Trait("Category", "Engine")]
public sealed class NotePipelineBuildTests
{
    private const string Head = "composition \"demo\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static async Task<string> NotesData(string song)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(Parser.Parse(song, EngineRig.Spec, new ParseOptions { ApplyNoteFlow = true, NoteLimit = NotePipeline.DefaultBuildLimit }));
        BlockResult b = report.Blocks.Single(x => x.Block == "b");
        Assert.NotNull(b.TakeId);
        return string.Join(',', rig.Bank.GetTake(b.TakeId!).Data.Select(d => d.DataId));
    }

    [Fact]
    public async Task A_pipeline_and_the_literal_it_makes_build_the_same_data()
    {
        string literal = Head + "midi-clip b at 1 for 1 {\n  notes \"1:0:1:63:100 1:1:1:65:100\"\n}\n";
        string pipeline = Head + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 1 for 1 {\n  notes a.notes\n    |> transpose(+3)\n}\n";
        Assert.Equal(await NotesData(literal), await NotesData(pipeline));
    }

    [Fact]
    public async Task A_baked_pipeline_builds_the_stored_notes()
    {
        string pipeline = Head + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 1 for 1 {\n  notes a.notes\n    |> transpose(+3)\n}\n";
        string baked = NoteBake.Bake(pipeline, EngineRig.Spec, "b", null, null, null, 100_000).Text;
        string literal = Head + "midi-clip b at 1 for 1 {\n  notes \"1:0:1:63:100 1:1:1:65:100\"\n}\n";
        Assert.Equal(await NotesData(literal), await NotesData(baked));
    }

    [Fact]
    public async Task The_module_service_builds_a_pipeline_with_a_chosen_bake_file()
    {
        string pipeline = Head + "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 1 for 1 {\n  notes a.notes\n    |> transpose(+3)\n}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        BakeOutcome outcome = NoteBake.Bake(pipeline, EngineRig.Spec, "b", null, "bakes/b.notes", null, 100_000);
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "bakes"));
        await File.WriteAllTextAsync(Path.Combine(rig.ProjectRoot, "bakes", "b.notes"), outcome.FileContent);
        using ModuleService modules = new(rig.ProjectRoot, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = modules.ParseExpanded(outcome.Text, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("1:0:1:63:100", doc.Blocks.Single(x => x.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
    }

    [Fact]
    public async Task A_midi_file_block_is_a_source_of_notes()
    {
        // format 0, 480 ticks per quarter: middle C for one beat, then the E above it for one beat
        byte[] track = [0x00, 0x90, 60, 100, 0x83, 0x60, 0x80, 60, 0, 0x00, 0x90, 64, 90, 0x83, 0x60, 0x80, 64, 0, 0x00, 0xFF, 0x2F, 0x00];
        List<byte> file = [.. "MThd"u8.ToArray(), 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0, .. "MTrk"u8.ToArray(), 0, 0, 0, (byte)track.Length, .. track];
        using EngineRig rig = await EngineRig.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(rig.ProjectRoot, "tune.mid"), [.. file]);
        string song = Head + "midi tune at 1 for 1 {\n  file tune.mid\n}\n\nmidi-clip b at 1 for 1 {\n  notes tune.notes\n    |> transpose(+12)\n}\n";
        using ModuleService modules = new(rig.ProjectRoot, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = modules.ParseExpanded(song, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("1:0:1:72:100 1:1:1:76:90", doc.Blocks.Single(x => x.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
    }
}
