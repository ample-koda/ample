using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The demo of the brief (spec-tests/components): import, build, seal, embed, extract and use the template again, with the same result every time.</summary>
[Trait("Category", "Modules")]
public sealed class ComponentFixtureTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-fixture-").FullName;

    public ComponentFixtureTests()
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "spec-tests", "components"));
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.Copy(Path.Combine(source, "triple-notes.amplet"), Path.Combine(folder, "song.amplet"));
        File.Copy(Path.Combine(source, "components", "triple-notes.amplec"), Path.Combine(folder, "components", "triple-notes.amplec"));
    }

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

    private string SongText => File.ReadAllText(Path.Combine(folder, "song.amplet"));

    private static int Notes(AmpleDocument doc) => doc.Blocks.First(b => b.Name == "introTriple").Parameters.First(p => p.Name == "notes").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void The_demo_song_builds_into_six_notes()
    {
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded(SongText, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(6, Notes(doc));
    }

    [Fact]
    public void Embedding_then_using_the_template_again_does_not_change_the_result()
    {
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        string fileText = File.ReadAllText(Path.Combine(folder, "components", "triple-notes.amplec"));
        ComponentEditResult sealedFile = ComponentEdits.Seal(fileText, "koda.notes.tripleNotes", null, EngineRig.Spec);
        Assert.True(sealedFile.Ok, sealedFile.Message);
        File.WriteAllText(Path.Combine(folder, "components", "triple-notes.amplec"), sealedFile.Text);
        (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(SongText, EngineRig.Spec);
        ComponentEditResult embedded = ComponentEmbedding.Embed(SongText, ComponentEmbedding.Closure(imported, imported), "triple", null, null, EngineRig.Spec);
        Assert.True(embedded.Ok, embedded.Message);
        AmpleDocument viaEmbedded = service.ParseExpanded(embedded.Text, EngineRig.Spec);
        Assert.Equal(6, Notes(viaEmbedded));
        ComponentEditResult back = ComponentEmbedding.UseTemplate(embedded.Text, imported[0], EngineRig.Spec);
        Assert.True(back.Ok, back.Message);
        Assert.Equal(6, Notes(service.ParseExpanded(back.Text, EngineRig.Spec)));
    }
}
