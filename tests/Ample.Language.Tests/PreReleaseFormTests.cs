namespace Ample.Language.Tests;

/// <summary>The v1 block header and the conversion of the pre-release form (<c>stable@1.1</c>) by <see cref="AmpleMigration"/>.</summary>
public sealed class PreReleaseFormTests
{
    private const string Head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n";

    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec);

    [Fact]
    public void A_v1_header_has_no_class_and_follows_the_definition()
    {
        AmpleDocument doc = Parse(Head + "midi-clip a at 1 for 1 {\n  notes 1:0:1:60:100\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        BlockNode a = Assert.Single(doc.Blocks);
        Assert.Equal("stable", a.Class);
        Assert.Equal(Corpus.Spec.Templates["midi-clip"].Version, a.Version);
        Assert.Single(a.Parts);
    }

    [Fact]
    public void A_v1_header_may_be_only_type_and_name()
    {
        AmpleDocument doc = Parse(Head + "midi-clip a {\n  notes 1:0:1:60:100\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "AMP1102" or "AMP1103" or "AMP1105");
        Assert.Equal("a", Assert.Single(doc.Blocks).Name);
    }

    [Fact]
    public void A_pinned_block_keeps_its_version_in_the_header()
    {
        AmpleDocument doc = Parse(Head + "midi-clip a pinned@${V} at 1 for 1 {\n  notes 1:0:1:60:100\n}\n".Replace("${V}", Corpus.Spec.Templates["midi-clip"].Version, StringComparison.Ordinal));
        BlockNode a = Assert.Single(doc.Blocks);
        Assert.Equal("pinned", a.Class);
        Assert.Equal(Corpus.Spec.Templates["midi-clip"].Version, a.Version);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "L006" or "AMP1103");
    }

    [Fact]
    public void Another_class_word_with_a_number_is_not_a_v1_header()
    {
        Assert.Contains(Parse(Head + "midi-clip a fixed@1.0 at 1 for 1 {\n}\n").Diagnostics, d => d.Code == "AMP1103");
    }

    [Fact]
    public void The_pre_release_form_still_reads_but_is_an_error_that_names_ample_migrate()
    {
        AmpleDocument doc = Parse(Head + "midi-clip a stable@1.1 at 1 for 1 {\n  notes 1:0:1:60:100\n}\n");
        Diagnostic old = Assert.Single(doc.Diagnostics, d => d.Code == "L011");
        Assert.Equal(Severity.Error, old.Severity);
        Assert.Contains("ample migrate", old.Hint, StringComparison.Ordinal);
        Assert.Equal("1.1", Assert.Single(doc.Blocks).Version);
    }

    [Theory]
    [InlineData("midi-clip a stable@1.1 at 1 for 4 {", "midi-clip a at 1 for 4 {")]
    [InlineData("ace-music theme live@0.4 at 1 for 4 {", "ace-music theme at 1 for 4 {")]
    [InlineData("midi-clip \"my clip\" stable@1.1 {", "midi-clip \"my clip\" {")]
    [InlineData("piano keys pinned@2.1 at 1 for 8 {", "piano keys pinned@2.1 at 1 for 8 {")]
    [InlineData("koda.lofi.loopBars loop stable at 1 for 36 {", "koda.lofi.loopBars loop at 1 for 36 {")]
    [InlineData("koda.lofi.loopBars loop stable@^1.2 at 1 for 36 {", "koda.lofi.loopBars loop pinned@^1.2 at 1 for 36 {")]
    [InlineData("midi-clip a at 1 for 4 {", "midi-clip a at 1 for 4 {")]
    public void The_converter_drops_the_class_and_the_number_and_keeps_a_pin(string before, string after)
    {
        string converted = AmpleMigration.ConvertPreRelease(Head + before + "\n}\n", Corpus.Spec);
        Assert.Equal(Head + after + "\n}\n", converted);
    }

    [Fact]
    public void The_converter_writes_composition_for_project()
    {
        string converted = AmpleMigration.ConvertPreRelease("# a song\nproject \"x\" { ample 1  tempo 100  master-seed 1 }\n", Corpus.Spec);
        Assert.Equal("# a song\ncomposition \"x\" { ample 1  tempo 100  master-seed 1 }\n", converted);
    }

    [Fact]
    public void A_migration_shows_the_converted_headers_and_a_second_one_changes_nothing()
    {
        const string old = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\nmidi-clip a stable@1.1 at 1 for 1 {\n  notes 1:0:1:60:100\n}\n";
        SongMigrationPlan plan = AmpleMigration.Plan(old, Corpus.Spec);
        LineChange change = Assert.Single(plan.Changes);
        Assert.Equal(2, change.Line);
        Assert.Equal("midi-clip a at 1 for 1 {", change.After);
        Assert.False(AmpleMigration.Plan(plan.Text, Corpus.Spec).Changed);
    }

    [Fact]
    public void The_converter_renames_the_old_port_names_to_the_stream_names()
    {
        string old = Head + "drums d at 1 for 4 {\n}\nvolume v {\n  from d.out -> in\n}\nvolume w {\n  from v.out -> in\n}\n";
        string converted = AmpleMigration.ConvertPreRelease(old, Corpus.Spec);
        string probe = converted.Replace(Head, Head + "lint { L010 warning }\n", StringComparison.Ordinal);
        Assert.DoesNotContain(Parser.Parse(probe, Corpus.Spec).Diagnostics, d => d.Code == "L010");
        Assert.DoesNotContain("-> in\n", converted, StringComparison.Ordinal);
        Assert.Equal(converted, AmpleMigration.ConvertPreRelease(converted, Corpus.Spec));   // nothing left to rename
    }
}
