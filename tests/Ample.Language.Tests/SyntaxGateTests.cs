namespace Ample.Language.Tests;

/// <summary>
/// The syntax gate of the diagnostics work (plan/diagnostics/PLAN.md, D17): a feature that is off is a clear error (AMP6020) and never an ignored statement; with the feature on the statements parse in both spellings.
/// </summary>
public sealed class SyntaxGateTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";

    private const SyntaxFeatures On = SyntaxFeatures.Observe | SyntaxFeatures.Assert | SyntaxFeatures.Record;

    private static List<HelperIssue> IssuesOf(string body)
    {
        List<HelperIssue> issues = [];
        _ = NotePipeline.HelperTable.Build([($"helper f(Notes n, Int voices = 2) -> Notes {{\n{body}\n  return n\n}}", 0)], issues);
        return issues;
    }

    private static PipeResult Run(string body)
    {
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([($"helper f(Notes n, Int voices = 2) -> Notes {{\n{body}\n  return n |> transpose(+7)\n}}", 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id is "syntax" or "feature-off");
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 4 * NotePipeline.Beat };
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            Helpers = table,
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
        };
        return NotePipeline.Evaluate("f(theme.notes)", context);
    }

    [Theory]
    [InlineData("  fail \"stop\"")]
    [InlineData("  throw \"stop\"")]
    [InlineData("  require voices > 0, \"x\"")]
    [InlineData("  assert voices > 0, \"x\"")]
    [InlineData("  try {\n    let a = 1\n  }")]
    public void A_feature_that_is_off_is_a_clear_error_and_never_ignored(string body)
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        HelperIssue issue = Assert.Single(IssuesOf(body));
        Assert.Equal("feature-off", issue.Issue.Id);
        Assert.Contains("not supported yet", issue.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_build_has_only_finished_features_on_and_the_error_reaches_the_diagnostics_as_AMP6020()
    {
        Assert.Equal(
            SyntaxFeatures.Observe | SyntaxFeatures.Record | SyntaxFeatures.Fail | SyntaxFeatures.Require
            | SyntaxFeatures.Throw | SyntaxFeatures.TryCatch | SyntaxFeatures.ErrorDecl | SyntaxFeatures.Assert, SyntaxGate.Enabled);
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper f() -> Int {\n  assert true\n  return 1\n}\n";
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP6020");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Equal(4, d.Line);   // 1-based
    }

    [Fact]
    public void A_feature_without_its_runtime_cannot_be_switched_on()
    {
        Assert.Equal(SyntaxFeatures.None, SyntaxGate.Shipped & ~SyntaxGate.Implemented);   // a shipped feature always has its runtime
        Assert.Equal(SyntaxFeatures.None, SyntaxGate.Enabled & ~SyntaxGate.Implemented);
    }

    [Fact]
    public void The_gate_is_scoped_to_the_using_block()
    {
        using (SyntaxGate.Use(SyntaxFeatures.None)) Assert.Single(IssuesOf("  assert voices > 0, \"x\""));
        Assert.Empty(IssuesOf("  assert voices > 0, \"x\""));
    }

    [Theory]
    [InlineData("  log \"Hello\"")]
    [InlineData("  log(\"Hello\")")]
    [InlineData("  debug voices")]
    [InlineData("  debug(voices)")]
    [InlineData("  trace voices * 2")]
    [InlineData("  warn \"Unknown preset\", code=\"PRESET_UNKNOWN\", context={preset: voices}")]
    [InlineData("  warn(\n    \"Unknown preset\",\n    code=\"PRESET_UNKNOWN\",\n    context={preset: voices, nested: {a: 1, b: \"x\"}}\n  )")]
    [InlineData("  warn \"Unknown preset\",\n       code=\"PRESET_UNKNOWN\",\n       hint=\"Use normal\"")]
    [InlineData("  log (voices + 1) * 2")]
    [InlineData("  assert voices > 0, \"Too many voices\"")]
    [InlineData("  assert(voices <= 8, \"Too many voices\")")]
    [InlineData("  assert (voices > 0) && (voices < 9), \"x\"")]
    [InlineData("  log \"a (b) c\"")]
    [InlineData("  log n |> count()")]
    [InlineData("  log(n |> count())")]
    public void With_the_feature_on_both_spellings_parse_and_do_not_change_the_result(string statement)
    {
        string plain = Run(string.Empty).Notes!;
        using IDisposable scope = SyntaxGate.Use(On);
        PipeResult result = Run(statement);
        Assert.Empty(result.Errors);
        Assert.Equal(plain, result.Notes);
    }

    [Fact]
    public void A_statement_after_the_last_expression_does_not_hide_the_value_of_the_helper()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([("helper g(Notes n) -> Notes {\n  n |> transpose(+1)\n  log \"done\"\n}", 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id is "syntax" or "feature-off" or "no-value" or "no-effect");
        Assert.NotNull(table);
    }

    [Fact]
    public void A_variable_that_is_only_used_in_a_log_counts_as_used()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        List<HelperIssue> issues = [];
        _ = NotePipeline.HelperTable.Build([("helper g(Notes n) -> Notes {\n  let shown = 3\n  log shown\n  return n\n}", 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id == "unused-variable");
    }

    [Theory]
    [InlineData("  log", "needs a message or a condition")]
    [InlineData("  log \"a\", \"b\"", "takes one message")]
    [InlineData("  log \"x\", foo=1", "has no argument")]
    [InlineData("  log \"x\", code=\"A\", code=\"B\"", "given twice")]
    [InlineData("  log(code=\"A\")", "takes one message")]
    [InlineData("  assert", "needs a message or a condition")]
    [InlineData("  assert 1, 2, 3", "optional message")]
    [InlineData("  log(\"x\"", "never closed")]
    [InlineData("  let r = {a: 1, a: 2}", "given twice")]
    [InlineData("  let r = {a 1}", "Expected")]
    public void A_mistake_in_a_statement_is_one_clear_syntax_error(string body, string expected)
    {
        using IDisposable scope = SyntaxGate.Use(On);
        HelperIssue issue = Assert.Single(IssuesOf(body));
        Assert.Equal("syntax", issue.Issue.Id);
        Assert.Contains(expected, issue.Issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_variable_that_looks_like_a_word_is_not_a_statement()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        Assert.Empty(IssuesOf("  var log = 1\n  log = log + 1"));
    }

    public static IEnumerable<object[]> GateCases() =>
        System.Text.Json.JsonDocument.Parse(Corpus.Read("spec-tests/note-lang/feature-gate.json")).RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => new object[] { c.GetProperty("id").GetString()!, c.GetProperty("body").GetString()! });

    [Fact]
    public void The_shipped_set_is_the_shared_set_that_the_js_evaluator_compares_itself_with()
    {
        System.Text.Json.JsonElement gate = System.Text.Json.JsonDocument.Parse(Corpus.Read("spec-tests/note-lang/feature-gate.json")).RootElement;
        string[] shared = [.. gate.GetProperty("shipped").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal)];
        string[] mine = [.. Enum.GetValues<SyntaxFeatures>().Where(f => f != SyntaxFeatures.None && SyntaxGate.Shipped.HasFlag(f)).Select(f => f.ToString()).Order(StringComparer.Ordinal)];
        Assert.Equal(shared, mine);
    }

    [Theory]
    [MemberData(nameof(GateCases))]
    public void The_shared_gate_cases_are_feature_off_here_too(string id, string body)
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        HelperIssue issue = Assert.Single(IssuesOf(body));
        Assert.True(issue.Issue.Id == "feature-off", id);
    }
}
