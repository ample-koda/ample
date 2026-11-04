using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The demo project <c>demo-projects/components</c>: every song builds without a problem and makes the notes its chain should make.</summary>
[Trait("Category", "Modules")]
public sealed class ComponentDemoProjectTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "demo-projects", "components"));

    private static AmpleDocument Expand(string song)
    {
        using ModuleService service = new(Root, (Ample.Storage.Library.Library?)null);
        return service.ParseExpanded(File.ReadAllText(Path.Combine(Root, song)), EngineRig.Spec);
    }

    private static int Notes(AmpleDocument doc, string block) => doc.Blocks.First(b => b.Name == block).Parameters.First(p => p.Name == "notes").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    [Theory]
    [InlineData("01-basic.amplet", "introTriple", 6)]
    [InlineData("02-chain.amplet", "triple", 8)]
    [InlineData("02-chain.amplet", "echo", 16)]
    [InlineData("02-chain.amplet", "layers", 32)]
    [InlineData("02-chain.amplet", "mirror", 64)]
    [InlineData("03-versions.amplet", "oldWay", 6)]
    [InlineData("03-versions.amplet", "newWay", 4)]
    [InlineData("12-nested-notes.amplet", "fanfare", 18)]
    [InlineData("04-local-notes.amplet", "alone", 6)]
    [InlineData("05-embedded.amplet", "introTriple", 6)]
    public void A_song_makes_the_notes_its_chain_should_make(string song, string block, int count)
    {
        AmpleDocument doc = Expand(song);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(count, Notes(doc, block));
    }

    [Theory]
    [InlineData("06-audio-dream-echo.amplet", "space__delay1", "space__reverb1")]
    [InlineData("07-audio-wet-dry.amplet", "blend__mix1", "blend__gain2")]
    [InlineData("08-audio-sidechain.amplet", "pump__compressor1", "pump__compressor1")]
    [InlineData("09-audio-nested.amplet", "room__dreamEcho1_delay1", "room__gain1")]
    [InlineData("10-mixed-dream-piano.amplet", "voice__piano1", "voice__reverb1")]
    [InlineData("11-mixed-llama-choir.amplet", "choir__delay-llama1", "choir__reverb1")]
    public void An_audio_song_becomes_ordinary_blocks_without_a_problem(string song, string first, string second)
    {
        AmpleDocument doc = Expand(song);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(doc.Blocks, b => b.Name == first);
        Assert.Contains(doc.Blocks, b => b.Name == second);
        Assert.DoesNotContain(doc.Blocks, b => b.Type.StartsWith("koda.", StringComparison.Ordinal));   // no instance is left
    }

    [Fact]
    public void The_limiter_after_the_wet_dry_component_reads_its_bus()
    {
        AmpleDocument doc = Expand("07-audio-wet-dry.amplet");
        BlockNode limiter = doc.Blocks.First(b => b.Name == "safety");
        Assert.Equal("blend__mix1", Assert.Single(limiter.Froms).Block);
    }

    [Fact]
    public void The_embedded_song_and_the_imported_song_make_the_same_notes()
    {
        Assert.Equal(Notes(Expand("01-basic.amplet"), "introTriple"), Notes(Expand("05-embedded.amplet"), "introTriple"));
        Assert.Single(Parser.Parse(File.ReadAllText(Path.Combine(Root, "05-embedded.amplet")), EngineRig.Spec).EmbeddedModules);
    }

    [Fact]
    public void Migrating_the_old_version_to_the_new_one_is_equivalent_and_keeps_the_notes()
    {
        string text = File.ReadAllText(Path.Combine(Root, "03-versions.amplet"));
        using ModuleService service = new(Root, (Ample.Storage.Library.Library?)null);
        (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(text, EngineRig.Spec);
        MigrationPlan plan = ComponentMigrations.Plan(text, imported, "oldWay", "2.0.0", EngineRig.Spec);
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(MigrationImpact.Equivalent, plan.Impact);
        Assert.False(plan.NeedsAcknowledgement);
        Assert.Contains("repeats 3", plan.Text, StringComparison.Ordinal);
        Assert.Contains("gap 1bar", plan.Text, StringComparison.Ordinal);
    }
}
