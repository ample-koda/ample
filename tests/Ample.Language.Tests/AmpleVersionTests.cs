namespace Ample.Language.Tests;

/// <summary>The AMPLE version a song (or a block) says it was written for, and the registry of upgrade steps.</summary>
public sealed class AmpleVersionTests
{
    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec);

    [Fact]
    public void A_song_without_a_version_is_read_as_the_current_one_and_gets_an_info_with_a_quick_fix()
    {
        AmpleDocument doc = Parse("composition \"x\" { tempo 100  master-seed 1 }\n");
        Assert.Null(doc.Project!.Ample);
        Diagnostic hint = Assert.Single(doc.Diagnostics, d => d.Code == "AMP1118");
        Assert.Equal(Severity.Info, hint.Severity);
        Assert.NotNull(hint.Fix);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void A_song_that_states_the_current_version_has_no_hint()
    {
        AmpleDocument doc = Parse("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n");
        Assert.Equal(1, doc.Project!.Ample);
        Assert.Equal(100, doc.Project.Tempo);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code is "AMP1118" or "AMP1119");
    }

    [Theory]
    [InlineData("999")]
    [InlineData("0")]
    public void A_version_this_AMPLE_does_not_know_is_an_error(string stated)
    {
        AmpleDocument doc = Parse($"composition \"x\" {{ ample {stated}  tempo 100  master-seed 1 }}\n");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP1119" && d.Severity == Severity.Error);
    }

    [Fact]
    public void The_old_word_project_still_reads_the_same_with_a_warning_and_a_quick_fix()
    {
        AmpleDocument doc = Parse("project \"x\" { ample 1  tempo 100  master-seed 1 }\n");
        Assert.Equal("x", doc.Project!.Name);
        Diagnostic warning = Assert.Single(doc.Diagnostics, d => d.Code == "AMP1117");
        Assert.Equal(Severity.Error, warning.Severity);   // an error since v1; the quick fix writes the new word
        Assert.Equal("composition", warning.Fix!.Text);
    }

    [Fact]
    public void A_block_in_a_song_cannot_claim_a_version_only_its_definition_says_since_when_it_is_stable()
    {
        const string head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n";
        AmpleDocument doc = Parse(head + "midi-clip a at 1 for 1 {\n  ample 1\n  notes \"1:0:1:60:100\"\n}\n");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2003");   // "ample" is not a parameter of the block
    }

    [Fact]
    public void The_definitions_of_the_built_in_blocks_say_since_which_version_they_are_stable()
    {
        Assert.All(Corpus.Spec.Templates.Values, t => Assert.True(t.StableSince is >= 1 and <= AmpleVersions.Current, t.Id));
        Assert.Empty(AmpleVersions.StaleTypes(Corpus.Spec, AmpleVersions.Current));
        // a definition that changed after a song's version makes its blocks stale
        UpgradeStep step = new(1, "x", new HashSet<string>(StringComparer.Ordinal) { "synth" }, t => t);
        Assert.Contains("synth", AmpleVersions.StaleTypes(Corpus.Spec, 1, [step]));
        Assert.DoesNotContain("synth", AmpleVersions.StaleTypes(Corpus.Spec, 2, [step]));
    }

    [Fact]
    public void The_format_keeps_the_version_and_the_word_the_author_wrote()
    {
        string once = Formatter.Format("composition   \"x\"{tempo 100 master-seed 1}\n", Corpus.Spec);
        Assert.Equal("composition \"x\" { tempo 100  master-seed 1 }\n", once);
        string stated = Formatter.Format("composition \"x\" {ample 1 tempo 100 master-seed 1}\n", Corpus.Spec);
        Assert.Equal("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n", stated);
        Assert.StartsWith("project ", Formatter.Format("project \"x\" { tempo 100  master-seed 1 }\n", Corpus.Spec), StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_carries_a_song_through_its_steps_in_order_and_names_the_block_types_they_touch()
    {
        HashSet<string> synth = new(StringComparer.Ordinal) { "synth" };
        HashSet<string> delay = new(StringComparer.Ordinal) { "delay" };
        UpgradeStep one = new(1, "synth gain was renamed", synth, t => t.Replace("level", "gain", StringComparison.Ordinal));
        UpgradeStep two = new(2, "delay keeps its old feedback", delay, t => t + "# step two\n");
        UpgradeStep[] steps = [two, one];

        (string text, IReadOnlyList<UpgradeStep> applied) = AmpleVersions.Upgrade("level 3\n", 1, steps);
        Assert.Equal("gain 3\n# step two\n", text);
        Assert.Equal([1, 2], applied.Select(s => s.From));

        (string fromTwo, IReadOnlyList<UpgradeStep> onlyTwo) = AmpleVersions.Upgrade("level 3\n", 2, steps);
        Assert.Equal("level 3\n# step two\n", fromTwo);
        Assert.Single(onlyTwo);

        Assert.Equal(["delay", "synth"], AmpleVersions.TouchedTypes(1, steps).Order());
        Assert.Equal(["delay"], AmpleVersions.TouchedTypes(2, steps).Order());
        Assert.Empty(AmpleVersions.TouchedTypes(3, steps));
    }

    [Fact]
    public void Nothing_is_upgraded_for_a_song_that_already_is_current()
    {
        (string text, IReadOnlyList<UpgradeStep> applied) = AmpleVersions.Upgrade("anything\n", AmpleVersions.Current);
        Assert.Equal("anything\n", text);
        Assert.Empty(applied);
    }

    [Fact]
    public void A_migration_stamps_the_current_version_and_shows_what_changes_before_anything_is_written()
    {
        const string old = "composition \"x\" { tempo 100  master-seed 1 }\n# keep me\n";
        SongMigrationPlan plan = AmpleMigration.Plan(old, Corpus.Spec);
        Assert.Equal(AmpleVersions.Current, plan.To);
        Assert.Equal("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n# keep me\n", plan.Text);
        LineChange change = Assert.Single(plan.Changes);
        Assert.Equal(1, change.Line);
        Assert.Contains("# keep me", plan.Text, StringComparison.Ordinal);

        SongMigrationPlan again = AmpleMigration.Plan(plan.Text, Corpus.Spec);
        Assert.False(again.Changed);
    }

    [Fact]
    public void A_migration_runs_the_steps_from_the_version_of_the_song()
    {
        UpgradeStep step = new(1, "level is called gain now", new HashSet<string>(StringComparer.Ordinal) { "synth" }, t => t.Replace("level", "gain", StringComparison.Ordinal));
        const string old = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n# level\n";
        SongMigrationPlan plan = AmpleMigration.Plan(old, Corpus.Spec, [step]);
        Assert.Equal([1], plan.Steps.Select(s => s.From));
        Assert.Contains("# gain", plan.Text, StringComparison.Ordinal);
        Assert.Single(plan.Changes);
    }
}
