namespace Ample.Language.Tests;

/// <summary>Deprecation and migration of components (docs/language/19-components.md): the chain, the impact, the typed bounded expressions, and that nothing impactful is applied without a look.</summary>
public sealed class ComponentMigrationTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\n";

    private const string V1 = "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n  property copies: Int = 2\n  property spacing: Duration = 1bar\n  output notes: Notes\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * spacing)\n    }\n  }\n}\n\n";

    private static string V2(string statements = "impact equivalent\n    rename copies -> repeats\n    set gap = old.spacing * 2") =>
        $"component demo.fx.twice version 2.0.0 {{\n  id \"com.demo.twice\"\n  input source: Notes\n  property repeats: Int = 2\n  property gap: Duration = 2bars\n  output notes: Notes\n  migrate 1.0.0 -> 2.0.0 {{\n    {statements}\n    message \"copies is now repeats\"\n  }}\n  process {{\n    for i in 0..repeats {{\n      emit source |> shift(i * gap)\n    }}\n  }}\n}}\n\n";

    private const string Instance = "demo.fx.twice doubled pinned@1.0 at 2 for 3 {\n  copies 3\n  spacing 2bars\n  from intro.notes -> source\n}\n";

    private static string Song(params string[] components) => Header + string.Concat(components) + Intro + Instance;

    private static MigrationPlan Plan(string song, string target) => ComponentMigrations.Plan(song, [], "doubled", target, Corpus.Spec);

    [Fact]
    public void An_equivalent_migration_renames_and_calculates_and_needs_no_acknowledgement()
    {
        MigrationPlan plan = Plan(Song(V1, V2()), "2.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(MigrationImpact.Equivalent, plan.Impact);
        Assert.False(plan.NeedsAcknowledgement);
        Assert.Contains("repeats 3", plan.Text, StringComparison.Ordinal);
        Assert.Contains("gap 4bars", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("copies 3", plan.Text, StringComparison.Ordinal);
        Assert.Contains("doubled pinned@2.0.0 at 2 for 3 {", plan.Text, StringComparison.Ordinal);   // v1: the chosen version is a pin
        Assert.Equal(["copies is now repeats"], plan.Messages);
        AmpleDocument after = Parser.Parse(plan.Text, Corpus.Spec);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void A_migration_that_can_change_the_notes_must_be_acknowledged()
    {
        MigrationPlan plan = Plan(Song(V1, V2("impact musicalChange\n    rename copies -> repeats\n    set repeats = old.copies + 1\n    set gap = old.spacing")), "2.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(MigrationImpact.MusicalChange, plan.Impact);
        Assert.True(plan.NeedsAcknowledgement);
        Assert.Contains("repeats 4", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_migration_without_a_stated_impact_counts_as_a_musical_change()
    {
        MigrationPlan plan = Plan(Song(V1, V2("rename copies -> repeats\n    set gap = old.spacing")), "2.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(MigrationImpact.MusicalChange, plan.Impact);
        Assert.Contains(plan.Warnings, w => w.Contains("does not say its impact", StringComparison.Ordinal));
    }

    [Fact]
    public void A_property_the_new_version_does_not_know_is_removed_and_that_is_destructive()
    {
        MigrationPlan plan = Plan(Song(V1, V2("impact equivalent\n    rename copies -> repeats")), "2.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(MigrationImpact.Destructive, plan.Impact);
        Assert.True(plan.NeedsAcknowledgement);
        Assert.Contains(plan.Changes, c => c.Kind == "drop-unmapped" && c.Target == "spacing");
        Assert.DoesNotContain("spacing 2bars", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_migration_there_is_no_path_and_the_song_is_not_touched()
    {
        string noMigration = V2().Replace("migrate 1.0.0 -> 2.0.0 {", "migrate 0.5.0 -> 2.0.0 {", StringComparison.Ordinal);
        MigrationPlan plan = Plan(Song(V1, noMigration), "2.0.0");
        Assert.False(plan.Ok);
        Assert.StartsWith("AMP2222", plan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_is_followed_and_the_highest_impact_wins()
    {
        string v3 = V2("impact none\n    set gap = old.gap").Replace("version 2.0.0", "version 3.0.0", StringComparison.Ordinal).Replace("migrate 1.0.0 -> 2.0.0", "migrate 2.0.0 -> 3.0.0", StringComparison.Ordinal);
        string v2 = V2("impact musicalChange\n    rename copies -> repeats\n    set gap = old.spacing");
        MigrationPlan plan = Plan(Song(V1, v2, v3), "3.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(MigrationImpact.MusicalChange, plan.Impact);
    }

    [Fact]
    public void A_component_can_replace_another_one()
    {
        string thrice = "component demo.fx.thrice version 1.0.0 {\n  id \"com.demo.thrice\"\n  input source: Notes\n  property repeats: Int = 3\n  property gap: Duration = 1bar\n  output notes: Notes\n  migrate com.demo.twice@1.0.0 -> 1.0.0 {\n    impact musicalChange\n    rename copies -> repeats\n    set gap = old.spacing\n  }\n  process {\n    for i in 0..repeats {\n      emit source |> shift(i * gap)\n    }\n  }\n}\n\n";
        MigrationPlan plan = Plan(Song(V1, thrice), "com.demo.thrice@1.0.0");
        Assert.True(plan.Ok, plan.Problem);
        Assert.Equal("demo.fx.thrice", plan.BlockType);
        Assert.StartsWith("demo.fx.thrice doubled", plan.Text[plan.Text.IndexOf("demo.fx.thrice doubled", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void A_statement_that_cannot_be_read_is_reported_as_amp2221()
    {
        MigrationPlan plan = Plan(Song(V1, V2("impact equivalent\n    frobnicate copies")), "2.0.0");
        Assert.False(plan.Ok);
        Assert.StartsWith("AMP2221", plan.Problem, StringComparison.Ordinal);
        ComponentDefinition broken = Assert.Single(Parser.Parse(V2("impact equivalent\n    frobnicate copies"), Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        Assert.Contains(ComponentMigrations.Check(broken), i => i.Code == "AMP2221" && i.Severity == Severity.Error);
    }

    [Fact]
    public void A_value_of_the_wrong_type_after_the_migration_is_refused()
    {
        MigrationPlan plan = Plan(Song(V1, V2("impact equivalent\n    rename copies -> repeats\n    set repeats = 2.5\n    set gap = old.spacing")), "2.0.0");
        Assert.False(plan.Ok);
        Assert.Contains("not an Int", plan.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("old.a * 2", "6")]
    [InlineData("old.b * 2 + 1bar", null)]
    [InlineData("old.b + 1bars", "3bars")]
    [InlineData("max(old.a, 5)", "5")]
    [InlineData("clamp(old.a, 0, 2)", "2")]
    [InlineData("round(7 / 2)", "4")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("\"text\"", "\"text\"")]
    [InlineData("old.a / 0", null)]
    [InlineData("old.missing", null)]
    [InlineData("whatever", null)]
    public void Expressions_are_typed_bounded_and_keep_their_units(string expression, string? expected)
    {
        Dictionary<string, string> old = new() { ["a"] = "3", ["b"] = "2bars" };
        string? value = ComponentMigrations.Evaluate(expression, old, out string? failure);
        Assert.Equal(expected, value);
        Assert.Equal(expected is null, failure is not null);
    }

    [Fact]
    public void An_expression_is_bounded()
    {
        string tooLong = string.Join(" + ", Enumerable.Repeat("1", 300));
        Assert.Null(ComponentMigrations.Evaluate(tooLong, new Dictionary<string, string>(), out string? failure));
        Assert.Contains("at most", failure, StringComparison.Ordinal);
        string deep = new string('(', 60) + "1" + new string(')', 60);
        Assert.Null(ComponentMigrations.Evaluate(deep, new Dictionary<string, string>(), out string? nested));
        Assert.Contains("nested", nested, StringComparison.Ordinal);
    }
}
