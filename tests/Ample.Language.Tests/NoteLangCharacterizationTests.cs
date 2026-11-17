namespace Ample.Language.Tests;

/// <summary>
/// WP0 of plan/diagnostics/PLAN.md: pins what the Notes language does today (before the diagnostics, exceptions and assertions work), so that every later change to these behaviours is deliberate.
/// </summary>
public sealed class NoteLangCharacterizationTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";

    private static NotePipeline.HelperTable Table(string[] decls, List<HelperIssue>? issues = null) => NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), issues ?? []);

    private static PipeValue Value(string notes)
    {
        List<PipeIssue> bad = [];
        List<PipeNote> events = NotePipeline.ReadNotes(notes, bad);
        return new PipeValue { Events = events, Span = 4 * NotePipeline.Beat };
    }

    private static PipeResult Run(string expr, string[]? decls = null, LangLimits? limits = null, List<HelperIssue>? issues = null, PipeValue? theme = null, TimeSpan? budget = null)
    {
        PipeValue source = theme ?? Value(Theme);
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = Table(decls ?? [], issues),
            Limits = limits ?? new LangLimits(),
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(source, "test:theme", null) : new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\""),
        };
        if (budget is { } b) context.Budget = b;
        return NotePipeline.Evaluate(expr, context);
    }

    [Theory]
    [InlineData("fail \"Cannot continue\"")]
    [InlineData("throw \"Something went wrong\"")]
    [InlineData("require 1 > 0, \"x\"")]
    [InlineData("assert 1 > 0, \"x\"")]
    [InlineData("log \"Hello\"")]
    [InlineData("debug 1")]
    [InlineData("warn \"x\"")]
    public void The_words_of_the_statements_are_a_visible_error_while_their_feature_is_off(string statement)
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        List<HelperIssue> issues = [];
        string[] decls = [$"helper f(Notes n) -> Notes {{\n  {statement}\n  return n\n}}"];
        PipeResult result = Run("f(theme.notes)", decls, issues: issues);
        Assert.True(issues.Count > 0 || result.Errors.Count > 0, "the statement was accepted silently");
        Assert.Null(result.Notes);
    }

    [Fact]
    public void A_helper_that_emits_and_then_fails_gives_no_notes_at_all()
    {
        string[] decls = ["helper g(Notes s) -> Notes {\n  emit s\n  let x = 1 / 0\n  return s\n}"];
        PipeResult result = Run("g(theme.notes)", decls);
        Assert.Contains(result.Errors, e => e.Id == "divide-by-zero");
        Assert.Null(result.Notes);
    }

    [Theory]
    [InlineData("theme.notes |> transpose(+7)")]
    [InlineData("theme.notes |> shift(2bars)")]
    [InlineData("theme.notes |> merge(theme.notes |> shift(1bar))")]
    [InlineData("theme.notes |> transpose(-3) |> shift(1beat) |> merge(theme.notes)")]
    public void Operators_never_change_the_value_they_read(string expression)
    {
        PipeValue theme = Value(Theme);
        string before = NotePipeline.NotesHash(theme);
        long span = theme.Span;
        int count = theme.Events.Count;
        PipeResult result = Run(expression, theme: theme);
        Assert.Empty(result.Errors);
        Assert.Equal(before, NotePipeline.NotesHash(theme));
        Assert.Equal(span, theme.Span);
        Assert.Equal(count, theme.Events.Count);
    }

    [Fact]
    public void A_helper_that_works_on_its_argument_leaves_the_argument_alone()
    {
        string[] decls = ["helper twice(Notes n) -> Notes {\n  let a = n |> transpose(+12)\n  emit a\n  emit n |> shift(1bar)\n}"];
        PipeValue theme = Value(Theme);
        string before = NotePipeline.NotesHash(theme);
        PipeResult result = Run("twice(theme.notes)", decls, theme: theme);
        Assert.Empty(result.Errors);
        Assert.Equal(before, NotePipeline.NotesHash(theme));
    }

    [Fact]
    public void The_wall_clock_budget_is_checked_at_operator_steps_only()
    {
        PipeResult atStep = Run("theme.notes |> transpose(+7)", budget: TimeSpan.Zero);
        Assert.Contains(atStep.Errors, e => e.Id == "cap-time");
        PipeResult noStep = Run("theme.notes", budget: TimeSpan.Zero);
        Assert.DoesNotContain(noStep.Errors, e => e.Id == "cap-time");
    }

    [Fact]
    public void Fuel_is_counted_in_abstract_steps_and_stops_at_a_fixed_place()
    {
        string[] decls = ["helper busy(Notes n) -> Notes {\n  var k = 0\n  for i in 0..40 { k = k + 1 }\n  return n\n}"];
        long lowest = -1;
        for (long fuel = 1; fuel < 400; fuel++)
        {
            PipeResult r = Run("busy(theme.notes)", decls, new LangLimits(Fuel: fuel));
            if (r.Errors.All(e => e.Id != "fuel")) { lowest = fuel; break; }
        }

        Assert.Equal(FuelBoundary, lowest);
    }

    /// <summary>The smallest fuel with which the helper of the test above completes. The JS test (<c>note-lang.test.mjs</c>, WP0 characterization) asserts the same number: C# and JS must stop at the same place.</summary>
    internal const long FuelBoundary = 209;

    [Fact]
    public void An_error_inside_a_helper_is_reported_with_an_id_and_a_range()
    {
        string[] decls = ["helper g(Notes s) -> Notes {\n  let x = 1 / 0\n  return s\n}"];
        string expression = "g(theme.notes)";
        PipeResult result = Run(expression, decls);
        PipeIssue issue = Assert.Single(result.Errors);
        Assert.Equal("divide-by-zero", issue.Id);
        // the range (39..44) is an offset in the text of the helper, not in the expression (14 characters): positions need an origin (F6 of the plan)
        Assert.True(issue.Start >= expression.Length);
        Assert.Equal(39, issue.Start);
        Assert.Equal(44, issue.End);
    }
}
