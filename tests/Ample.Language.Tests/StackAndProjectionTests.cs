namespace Ample.Language.Tests;

/// <summary>WP6 of plan/diagnostics/PLAN.md (D2, D10): the call stack of a failure and of an event, and the events that become diagnostics.</summary>
public sealed class StackAndProjectionTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\n";

    private static AmpleDocument Parse(string song) => Parser.Parse(Header + song, Corpus.Spec);

    private const string Clip = "\nmidi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> transpose(f())\n}\n";

    [Fact]
    public void A_failure_shows_the_chain_of_helpers_that_led_to_it()
    {
        AmpleDocument doc = Parse("helper inner() -> Int {\n  fail \"stop\"\n}\n\nhelper f() -> Int {\n  return inner()\n}\n" + Clip);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP6002");
        Assert.Contains("Called from: inner (song:", d.Hint, StringComparison.Ordinal);
        Assert.Contains("<- f", d.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stack_is_cut_at_the_configured_depth()
    {
        const string chain = "helper c() -> Int {\n  fail \"deep\"\n}\n\nhelper b() -> Int {\n  return c()\n}\n\nhelper f() -> Int {\n  return b()\n}\n";
        AmpleDocument doc = Parse("settings { logging.stackMaxDepth 2 }\n\n" + chain + Clip);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP6002");
        Assert.Equal(2, d.Hint.Split("<-", StringSplitOptions.None).Length);   // c and b only
    }

    [Fact]
    public void A_warn_statement_becomes_a_warning_with_its_event_code()
    {
        AmpleDocument doc = Parse("helper f() -> Int {\n  warn \"n is very large\", code=\"N_LARGE\"\n  return 1\n}\n" + Clip);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP6001");
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal("n is very large [N_LARGE]", d.Message);
        Assert.Equal(Header.Count(c => c == '\n') + 2, d.Line);   // the line of the statement, 1-based
    }

    [Fact]
    public void Ordinary_logs_are_not_diagnostics_whatever_the_level()
    {
        AmpleDocument doc = Parse("settings { logging.level trace }\n\nhelper f() -> Int {\n  log \"hello\"\n  debug 1\n  trace 2\n  return 1\n}\n" + Clip);
        Assert.DoesNotContain(doc.Diagnostics, x => x.Code is "AMP6001" or "AMP6006");
        Assert.Contains(doc.Observations, e => e.Level == EventLevel.Info);   // they are in the log view
    }

    [Fact]
    public void An_observation_that_cannot_be_evaluated_is_an_info_and_the_program_goes_on()
    {
        AmpleDocument doc = Parse("settings { logging.level debug }\n\nhelper f() -> Int {\n  debug 1 / 0\n  return 1\n}\n" + Clip);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP6006");
        Assert.Equal(Severity.Info, d.Severity);
        Assert.Contains("divide-by-zero", d.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Diagnostics, x => x.Severity == Severity.Error);
    }

    [Fact]
    public void A_warning_carries_a_stack_only_when_the_configuration_asks_for_it()
    {
        const string helpers = "helper inner() -> Int {\n  warn \"careful\"\n  return 1\n}\n\nhelper f() -> Int {\n  return inner()\n}\n";
        Diagnostic plain = Assert.Single(Parse(helpers + Clip).Diagnostics, x => x.Code == "AMP6001");
        Assert.DoesNotContain("Called from", plain.Hint, StringComparison.Ordinal);
        Diagnostic withStack = Assert.Single(Parse("settings { logging.stack warnings }\n\n" + helpers + Clip).Diagnostics, x => x.Code == "AMP6001");
        Assert.Contains("Called from: inner (", withStack.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_is_counted_once_and_never_duplicated_by_the_projection()
    {
        AmpleDocument doc = Parse("helper f() -> Int {\n  repeat 5 { warn \"again\" }\n  return 1\n}\n" + Clip);
        Assert.Equal(doc.Observations.Count(e => e.Level == EventLevel.Warning), doc.Diagnostics.Count(x => x.Code == "AMP6001"));
    }

    [Fact]
    public void The_projected_codes_of_the_plan_exist_in_the_catalog()
    {
        string[] codes = ["AMP6001", "AMP6002", "AMP6003", "AMP6004", "AMP6005", "AMP6006", "AMP6007", "AMP6011", "AMP6012", "AMP6014", "AMP6016", "AMP6018", "AMP6020"];
        string catalog = Corpus.Read("spec/diagnostics.json");
        Assert.All(codes, code => Assert.Contains("\"code\": \"" + code + "\"", catalog, StringComparison.Ordinal));
    }
}
