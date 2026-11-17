namespace Ample.Language.Tests;

/// <summary>WP4a of plan/diagnostics/PLAN.md (D5): records are an immutable value type: a literal, nested records, field access, the type Record, field-level taint, and never a recipe input.</summary>
public sealed class RecordTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";

    private const string Phase = "dynamic helper phase() -> Int { return currentTime().minute % 8 }";

    private static PipeResult Run(string expr, params string[] decls)
    {
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 4 * NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), []),
            Clock = () => new DateTime(2026, 11, 16, 10, 5, 0),
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
        };
        return NotePipeline.Evaluate(expr, context);
    }

    private static PipeResult Good(string expr, params string[] decls)
    {
        PipeResult r = Run(expr, decls);
        Assert.Empty(r.Errors);
        return r;
    }

    [Fact]
    public void A_record_literal_has_fields_that_are_read_with_a_dot()
    {
        PipeResult r = Good("f()", "helper f() -> Notes {\n  let r = {steps: 3, label: \"x\", on: true}\n  return theme.notes |> transpose(r.steps)\n}");
        Assert.Equal(Good("theme.notes |> transpose(3)").Notes, r.Notes);
    }

    [Fact]
    public void Records_nest_and_hold_expressions_and_lists()
    {
        PipeResult r = Good("f(2)", "helper f(Int n) -> Notes {\n  let r = {inner: {a: n * 2, b: [n, 5]}, \"quoted key\": 1}\n  return theme.notes |> transpose(r.inner.a + at(r.inner.b, 1))\n}");
        Assert.Equal(Good("theme.notes |> transpose(9)").Notes, r.Notes);
    }

    [Fact]
    public void A_record_is_a_typed_value_for_parameters_returns_and_is()
    {
        PipeResult r = Good("go(theme.notes, {steps: 4})", "helper pick(Record r) -> Int { return r.steps }", "helper go(Notes n, Record r) -> Notes { return n |> transpose(pick(r)) }");
        Assert.Equal(Good("theme.notes |> transpose(4)").Notes, r.Notes);
        PipeResult isRecord = Good("f()", "helper f() -> Notes {\n  let r = {}\n  return theme.notes |> transpose(r is Record ? 2 : 3)\n}");
        Assert.Equal(Good("theme.notes |> transpose(2)").Notes, isRecord.Notes);
    }

    [Fact]
    public void A_list_of_records_can_be_looped()
    {
        PipeResult r = Good("f()", "helper f() -> Notes {\n  for item in [{n: 1}, {n: 2}] { emit theme.notes |> transpose(item.n) }\n}");
        Assert.Empty(r.Errors);
        Assert.Equal(8, r.Notes!.Split(' ').Length);
    }

    [Fact]
    public void An_unknown_field_is_a_clear_error_that_names_the_fields()
    {
        PipeResult r = Run("f()", "helper f() -> Notes {\n  let r = {a: 1, b: 2}\n  return theme.notes |> transpose(r.c)\n}");
        PipeIssue issue = Assert.Single(r.Errors);
        Assert.Equal("unknown-field", issue.Id);
        Assert.Contains("it has a, b", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_cannot_be_given_to_an_operator_so_it_never_reaches_a_recipe()
    {
        PipeResult r = Run("f()", "helper f() -> Notes {\n  let r = {a: 1}\n  return theme.notes |> transpose(r)\n}");
        Assert.Contains(r.Errors, e => e.Id == "type-mismatch");
        Assert.Null(r.Notes);
    }

    [Fact]
    public void A_field_keeps_its_own_taint_and_a_sibling_field_stays_plain()
    {
        string go = "helper go(Record r, Bool dynamicField) -> Notes { return theme.notes |> transpose(dynamicField ? r.t : r.n) }";
        PipeResult dynamicField = Good("go({t: phase(), n: 3}, true)", Phase, go);
        Assert.True(dynamicField.Dynamic);
        Assert.True(dynamicField.Tainted);
        PipeResult plainField = Good("go({t: phase(), n: 3}, false)", Phase, go);
        Assert.True(plainField.Dynamic);   // the clock was read: the evaluation is dynamic whatever is used afterwards
        Assert.False(plainField.Tainted);   // but the field that was used does not come from it
    }

    [Fact]
    public void A_record_in_a_dynamic_value_keeps_the_taint_when_it_is_passed_on_whole()
    {
        PipeResult r = Good("go({t: phase()})", Phase, "helper go(Record r) -> Notes { return theme.notes |> transpose(r.t) }");
        Assert.True(r.Tainted);
    }

    [Fact]
    public void The_record_feature_is_shipped_and_a_record_in_a_head_does_not_break_a_block()
    {
        Assert.True(SyntaxGate.Supports(SyntaxFeatures.Record));
        PipeResult r = Good("f(1)", "helper f(Int n) -> Notes {\n  if {a: n}.a > 0 { return theme.notes |> transpose(1) }\n  return theme.notes\n}");
        Assert.Equal(Good("theme.notes |> transpose(1)").Notes, r.Notes);
    }
}
