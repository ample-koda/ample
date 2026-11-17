namespace Ample.Language.Tests;

/// <summary>WP5a of plan/diagnostics/PLAN.md (D6-D8): fail stops the evaluation,
/// require checks a condition; both are part of the meaning (hashed as written), their messages hold values only, and a sensitive value is redacted.</summary>
public sealed class FailRequireTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100";

    private static PipeResult Run(string expr, Func<string, string?>? environment, params string[] decls)
    {
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 2 * NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), []),
            Environment = environment,
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
        };
        return NotePipeline.Evaluate(expr, context);
    }

    [Fact]
    public void Fail_stops_the_evaluation_with_its_message_and_gives_no_notes()
    {
        PipeResult r = Run("f()", null, "helper f() -> Notes {\n  fail \"The preset is unknown\"\n}");
        PipeIssue issue = Assert.Single(r.Errors);
        Assert.Equal("fail", issue.Id);
        Assert.Equal("The preset is unknown", issue.Message);
        Assert.Null(r.Notes);
    }

    [Fact]
    public void Fail_in_a_branch_that_does_not_run_changes_nothing()
    {
        PipeResult r = Run("f(1)", null, "helper f(Int n) -> Notes {\n  if n > 5 { fail \"too big\" }\n  return theme.notes\n}");
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Notes);
    }

    [Fact]
    public void Fail_ends_a_path_so_the_helper_needs_no_value_after_it()
    {
        PipeResult r = Run("f(1)", null, "helper f(Int n) -> Notes {\n  if n > 5 { return theme.notes } else { fail \"nope\" }\n}");
        Assert.Single(r.Errors, e => e.Id == "fail");
    }

    [Fact]
    public void Require_passes_silently_and_fails_with_the_text_of_the_condition()
    {
        const string helper = "helper f(Int n) -> Notes {\n  require n   >  0\n  return theme.notes\n}";
        Assert.Empty(Run("f(1)", null, helper).Errors);
        PipeIssue issue = Assert.Single(Run("f(0)", null, helper).Errors);
        Assert.Equal("require", issue.Id);
        Assert.Equal("Requirement failed: n > 0", issue.Message);   // the condition as written, whitespace collapsed; no position
    }

    [Fact]
    public void Require_takes_a_message_and_a_hint()
    {
        PipeIssue issue = Assert.Single(Run("f(0)", null, "helper f(Int n) -> Notes {\n  require(n > 0, \"n must be positive\", hint=\"Use 1 or more\")\n  return theme.notes\n}").Errors);
        Assert.Equal("n must be positive", issue.Message);
        Assert.Equal("Use 1 or more", issue.Suggestion);
    }

    [Fact]
    public void The_message_never_shows_a_sensitive_value()
    {
        const string planted = "planted-credential-value";
        PipeResult r = Run("f()", _ => planted, "dynamic helper f() -> Notes {\n  let key = environment(\"AMPLE_TEST\")\n  fail key + \" is wrong\"\n}");
        PipeIssue issue = Assert.Single(r.Errors);
        Assert.DoesNotContain(planted, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(planted, issue.Suggestion ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_and_require_are_part_of_the_meaning_unlike_an_observation()
    {
        NotePipeline.HelperTable plain = NotePipeline.HelperTable.Build([("helper f(Int n) -> Int {\n  return n\n}", 0)], []);
        NotePipeline.HelperTable withRequire = NotePipeline.HelperTable.Build([("helper f(Int n) -> Int {\n  require n > 0\n  return n\n}", 0)], []);
        NotePipeline.HelperTable withLog = NotePipeline.HelperTable.Build([("helper f(Int n) -> Int {\n  log n\n  return n\n}", 0)], []);
        using IDisposable scope = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Fail | SyntaxFeatures.Require);
        static IEnumerable<string> Semantic(NotePipeline.HelperTable t) => t.ClosureHashes("f(1)").Where(h => !h.StartsWith("source:", StringComparison.Ordinal));
        Assert.NotEqual(Semantic(plain), Semantic(withRequire));
        Assert.Equal(Semantic(plain), Semantic(withLog));
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("require")]
    public void The_failing_statements_are_shipped(string word)
    {
        Assert.True(SyntaxGate.Supports(word == "fail" ? SyntaxFeatures.Fail : SyntaxFeatures.Require));
    }
}
