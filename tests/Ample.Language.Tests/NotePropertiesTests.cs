namespace Ample.Language.Tests;

/// <summary>Block properties that are expressions: a helper call, a property of another block, a bake with an override; resolved with the helpers and the other blocks, typed by the template (a program is not an octave), reported, and written back as the plain value for a build.</summary>
public sealed class NotePropertiesTests
{
    private const string Head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nmidi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\n";

    private static string Player(string name, string body) => $"gm {name} at 1 for auto {{\n  file pack:musescore-general\n  from theme.notes -> notes\n{body}\n}}\n\n";

    private static AmpleDocument Parse(string song, DateTime? now = null, bool apply = false) => Parser.Parse(song, Corpus.Spec, new ParseOptions { ApplyNoteFlow = apply, Clock = () => now ?? new DateTime(2026, 10, 26, 12, 7, 0) });

    private static string ValueOf(AmpleDocument doc, string block, string name) => doc.Blocks.Single(b => b.Name == block).Parameters.Single(p => p.Name == name).Value;

    [Fact]
    public void A_property_can_be_a_helper_call_and_a_build_sees_the_plain_value()
    {
        string song = Head + "helper quiet(Notes n) -> Int { count(n) * -2 }\n\n" + Player("lead", "  gain quiet(theme.notes)");
        AmpleDocument doc = Parse(song, apply: true);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        PropertyBinding binding = Assert.Single(doc.Properties);
        Assert.Equal(("lead", "gain", "-4", "expression"), (binding.Block, binding.Name, binding.Value, binding.State));
        Assert.Equal("-4", ValueOf(doc, "lead", "gain"));
        Assert.Equal("quiet(theme.notes)", ValueOf(Parse(song), "lead", "gain"));   // the editors keep the text
        Assert.Equal("stable", doc.Stability);
    }

    [Fact]
    public void A_property_reads_a_property_of_another_block_and_a_cycle_is_reported()
    {
        string song = Head + Player("lead", "  gain -6") + Player("pad", "  gain lead.gain - 3");
        AmpleDocument doc = Parse(song, apply: true);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("-9", ValueOf(doc, "pad", "gain"));
        string cycle = Head + Player("a", "  gain b.gain + 1") + Player("b", "  gain a.gain + 1");
        Assert.Contains(Parse(cycle).Diagnostics, d => d.Code == "AMP2067" && d.Message.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_is_not_an_octave_and_values_stay_inside_the_range_of_the_template()
    {
        string wrong = Head + "helper pickRegister(Int minute) -> Octave {\n  if minute < 20 { return 3 }\n  return 4\n}\n\n" + Player("lead", "  program pickRegister(5)");
        Diagnostic mismatch = Assert.Single(Parse(wrong).Diagnostics, d => d.Code == "AMP2044");
        Assert.Contains("Octave", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("Program", mismatch.Message, StringComparison.Ordinal);
        string right = Head + "helper pick(Int minute) -> Program {\n  if minute < 20 { return 3 }\n  return 4\n}\n\n" + Player("lead", "  program pick(5)");
        Assert.Equal("3", ValueOf(Parse(right, apply: true), "lead", "program"));
        string far = Head + "helper loud() -> Int { 99 }\n\n" + Player("lead", "  gain loud()");
        Assert.Contains(Parse(far).Diagnostics, d => d.Code == "AMP2044" && d.Message.Contains("outside", StringComparison.Ordinal));
    }

    [Fact]
    public void A_baked_property_is_signed_and_an_override_keeps_its_own_value()
    {
        string helper = "helper quiet(Notes n) -> Int { count(n) * -2 }\n\n";
        AmpleDocument stale = Parse(Head + helper + Player("lead", "  gain bake quiet(theme.notes) as -4dB @00000000"));
        PropertyBinding binding = Assert.Single(stale.Properties);
        Assert.Equal("stale", binding.State);
        Diagnostic warning = Assert.Single(stale.Diagnostics, d => d.Code == "AMP2069");
        Assert.NotNull(warning.Fix);
        Assert.Equal("stale", stale.Stability);
        string signed = warning.Fix!.Text;   // as -4 @<hash>: the value bake again
        Assert.StartsWith("as -4 @", signed, StringComparison.Ordinal);
        AmpleDocument fresh = Parse(Head + helper + Player("lead", "  gain bake quiet(theme.notes) " + signed));
        Assert.Equal("baked", Assert.Single(fresh.Properties).State);
        string other = signed.Replace("as -4 ", "as -2 ", StringComparison.Ordinal);
        AmpleDocument overridden = Parse(Head + helper + Player("lead", "  gain bake quiet(theme.notes) " + other + " override"), apply: true);
        PropertyBinding kept = Assert.Single(overridden.Properties);
        Assert.Equal(("override", "-2", "-4"), (kept.State, kept.Value, kept.SourceText));
        Assert.Equal("-2", ValueOf(overridden, "lead", "gain"));
        Assert.Contains(overridden.Diagnostics, d => d.Code == "AMP2070");
        AmpleDocument dirty = Parse(Head + helper + Player("lead", "  gain bake quiet(theme.notes) " + other));
        Assert.Equal("dirty", Assert.Single(dirty.Properties).State);
    }

    [Fact]
    public void A_dynamic_property_makes_the_song_dynamic_until_it_is_baked()
    {
        string helper = "dynamic helper wobble() -> Int { currentTime().minute % 5 }\n\n";
        AmpleDocument open = Parse(Head + helper + Player("lead", "  gain wobble()"));
        Assert.True(Assert.Single(open.Properties).Dynamic);
        Assert.Contains(open.Diagnostics, d => d.Code == "AMP2073");
        Assert.Equal("dynamic", open.Stability);
        Assert.NotEmpty(open.StabilityNotes);
        AmpleDocument baked = Parse(Head + helper + Player("lead", "  gain bake wobble() as 2dB"));
        Assert.False(Assert.Single(baked.Properties).Dynamic);
        Assert.Equal("baked-dynamic", baked.Stability);
        Assert.Equal("2", ValueOf(Parse(Head + helper + Player("lead", "  gain bake wobble() as 2dB"), apply: true), "lead", "gain"));
    }

    [Fact]
    public void Plain_values_are_not_expressions()
    {
        Assert.False(NotePipeline.IsPropertyExpression("-9"));
        Assert.False(NotePipeline.IsPropertyExpression("30%"));
        Assert.False(NotePipeline.IsPropertyExpression("10 ms"));
        Assert.False(NotePipeline.IsPropertyExpression("pack:musescore-general"));
        Assert.False(NotePipeline.IsPropertyExpression("kit/tape"));
        Assert.False(NotePipeline.IsPropertyExpression("saw"));
        Assert.False(NotePipeline.IsPropertyExpression("auto(1:50, 3:80)"));      // automation is not a formula
        Assert.False(NotePipeline.IsPropertyExpression("fadeout(2bars)"));
        Assert.True(NotePipeline.IsPropertyExpression("quiet(theme.notes)"));
        Assert.True(NotePipeline.IsPropertyExpression("lead.gain - 3"));
        Assert.True(NotePipeline.IsPropertyExpression("bake f() as 1"));
        Assert.True(NotePipeline.IsPropertyExpression("(a + b) * 2"));
        Assert.Empty(Parse(Head + Player("lead", "  gain -9")).Properties);
    }

    private static string Apply(string song, EditResult result)
    {
        Assert.True(result.Ok, result.Error);
        string[] lines = song.Split('\n');
        TextEdit e = Assert.Single(result.Edits);
        lines[e.StartLine - 1] = lines[e.StartLine - 1][..(e.StartCol - 1)] + e.NewText + lines[e.StartLine - 1][(e.EndCol - 1)..];
        return string.Join('\n', lines);
    }

    [Fact]
    public void A_formula_is_never_overwritten_by_a_plain_value_the_modes_say_what_to_do()
    {
        string helper = "helper quiet(Notes n) -> Int { count(n) * -2 }\n\n";
        string song = Head + helper + Player("lead", "  gain quiet(theme.notes)");
        AmpleDocument doc = Parse(song);
        EditResult refused = Edits.SetParameter(doc, "lead", "gain", "-2");
        Assert.False(refused.Ok);
        Assert.Contains("AMP2081", refused.Error, StringComparison.Ordinal);
        Assert.True(Edits.SetParameter(doc, "lead", "gain", "quiet(theme.notes) + 1").Ok);   // editing the expression is an explicit expression edit
        Assert.False(Edits.SetPropertyValue(doc, "lead", "gain", "-2", "nonsense").Ok);

        string overridden = Apply(song, Edits.SetPropertyValue(doc, "lead", "gain", "-2", "override"));
        PropertyBinding kept = Assert.Single(Parse(overridden, apply: true).Properties);
        Assert.Equal(("override", "-2", "-4"), (kept.State, kept.Value, kept.SourceText));

        string baked = Apply(song, Edits.SetPropertyValue(doc, "lead", "gain", string.Empty, "bake"));
        Assert.Equal("baked", Assert.Single(Parse(baked).Properties).State);
        Assert.Equal(song, Apply(baked, Edits.SetPropertyValue(Parse(baked), "lead", "gain", string.Empty, "again")));   // make dynamic again: the expression as it was

        string literal = Apply(song, Edits.SetPropertyValue(doc, "lead", "gain", "-9", "literal"));
        Assert.Empty(Parse(literal).Properties);
        Assert.Contains("gain -9", literal, StringComparison.Ordinal);
        Assert.True(Edits.SetParameter(Parse(literal), "lead", "gain", "-3").Ok);   // plain again: plain edits work
    }
}
