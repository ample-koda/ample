namespace Ample.Language.Tests;

/// <summary>WP5a of plan/diagnostics/PLAN.md (D6-D8, D20, D26): throw, error values, try/catch/finally, what a catch can handle, what finally runs after, and the isolation of the error value.</summary>
public sealed class TryCatchTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100";

    private static (PipeResult Result, ObservationSink Sink) Run(string expr, LangLimits? limits = null, params string[] decls)
    {
        List<PipeIssue> bad = [];
        PipeValue theme = new() { Events = NotePipeline.ReadNotes(Theme, bad), Span = 2 * NotePipeline.Beat };
        ObservationSink sink = new(level: LogLevel.Debug);
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Helpers = NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), []),
            Source = name => name == "theme" ? new NotePipeline.SourceInfo(theme, "test:theme", null) : new NotePipeline.SourceInfo(null, null, "none"),
            Observations = sink,
            Clock = () => new DateTime(2026, 11, 16, 10, 5, 0),
        };
        if (limits is { } l) context.Limits = l;
        return (NotePipeline.Evaluate(expr, context), sink);
    }

    private static string Shifted(int steps) => Run("theme.notes |> transpose(" + steps + ")", null).Result.Notes!;

    private static void Gives(int steps, string expr, params string[] decls)
    {
        (PipeResult result, _) = Run(expr, null, decls);
        Assert.Empty(result.Errors);
        Assert.Equal(Shifted(steps), result.Notes);
    }

    [Fact]
    public void A_thrown_text_is_caught_with_its_message_and_origin()
    {
        Gives(3, "f()", "helper f() -> Notes {\n  try { throw \"boom\" } catch (Error e) {\n    return theme.notes |> transpose(e.message == \"boom\" && e.origin == \"string\" ? 3 : 9)\n  }\n  return theme.notes\n}");
    }

    [Fact]
    public void A_bare_catch_and_a_typed_catch_match_by_type_and_the_first_match_wins()
    {
        const string helper = "helper f(Int n) -> Notes {\n  try {\n    require n > 0\n    return theme.notes |> transpose(1)\n  } catch (RequirementError e) { return theme.notes |> transpose(2)\n  } catch (e) { return theme.notes |> transpose(3) }\n}";
        Gives(1, "f(1)", helper);
        Gives(2, "f(0)", helper);
        Gives(3, "g()", "helper g() -> Notes {\n  try { throw \"x\" } catch (RequirementError r) { return theme.notes |> transpose(2) } catch (e) { return theme.notes |> transpose(3) }\n  return theme.notes\n}");
    }

    [Fact]
    public void Operation_errors_are_catchable_with_a_stable_code()
    {
        Gives(5, "f(0)", "helper f(Int n) -> Notes {\n  try { let x = 4 / n\n    return theme.notes |> transpose(Int(x)) } catch (e) { return theme.notes |> transpose(e.code == \"DIV_ZERO\" ? 5 : 9) }\n}");
    }

    [Fact]
    public void Fail_and_programming_errors_are_not_caught()
    {
        (PipeResult failed, _) = Run("f()", null, "helper f() -> Notes {\n  try { fail \"stop\" } catch (e) { return theme.notes }\n  return theme.notes\n}");
        Assert.Contains(failed.Errors, e => e.Id == "fail");
        (PipeResult broken, _) = Run("f()", null, "helper f() -> Notes {\n  try { let x = theme.notes |> transpose(\"a\") } catch (e) { return theme.notes }\n  return theme.notes\n}");
        Assert.NotEmpty(broken.Errors);
    }

    [Fact]
    public void Finally_runs_after_a_normal_exit_a_return_and_a_caught_error_and_may_assign()
    {
        Gives(11, "f()", "helper f() -> Notes {\n  var n = 0\n  try { n = 1 } finally { n = n + 10 }\n  return theme.notes |> transpose(n)\n}");
        Gives(7, "g()", "helper g() -> Notes {\n  var n = 0\n  try { throw \"x\" } catch (e) { n = 2 } finally { n = n + 5 }\n  return theme.notes |> transpose(n)\n}");
    }

    [Fact]
    public void Finally_runs_while_a_fatal_failure_unwinds_and_after_an_uncaught_throw_but_not_after_a_safety_stop()
    {
        (PipeResult failed, ObservationSink sink) = Run("f()", null, "helper f() -> Notes {\n  try { fail \"stop\" } finally { log \"cleanup\" }\n  return theme.notes\n}");
        Assert.Contains(failed.Errors, e => e.Id == "fail");
        Assert.Contains(sink.Events, e => e.Message == "cleanup");
        (PipeResult thrown, ObservationSink sink2) = Run("f()", null, "helper f() -> Notes {\n  try { throw \"x\" } finally { log \"cleanup\" }\n  return theme.notes\n}");
        Assert.Contains(thrown.Errors, e => e.Id == "throw" && e.Message == "x");
        Assert.Contains(sink2.Events, e => e.Message == "cleanup");
        (PipeResult stopped, ObservationSink sink3) = Run("f()", new LangLimits { Fuel = 50 }, "helper f() -> Notes {\n  try { repeat 5000 { let a = 1 } } finally { log \"cleanup\" }\n  return theme.notes\n}");
        Assert.Contains(stopped.Errors, e => e.Id == "fuel");
        Assert.DoesNotContain(sink3.Events, e => e.Message == "cleanup");
    }

    [Fact]
    public void What_a_try_body_emitted_is_dropped_when_a_catch_runs()
    {
        Gives(2, "f()", "helper f() -> Notes {\n  try { emit theme.notes |> transpose(1)\n    throw \"x\" } catch (e) { emit theme.notes |> transpose(2) }\n}");
    }

    [Fact]
    public void A_failure_in_finally_is_suppressed_when_it_is_not_more_severe_and_the_primary_stays()
    {
        (PipeResult result, _) = Run("f()", null, "helper f() -> Notes {\n  try { throw \"first\" } finally { require 1 > 2, \"second\" }\n  return theme.notes\n}");
        Assert.Contains(result.Errors, e => e.Id == "throw" && e.Message == "first");
        Gives(1, "g()", "helper g() -> Notes {\n  try {\n    try { throw \"first\" } finally { require 1 > 2, \"second\" }\n  } catch (e) { return theme.notes |> transpose(e.suppressedCount) }\n  return theme.notes\n}");
        Gives(5, "h()", "helper h() -> Notes {\n  try {\n    try { throw \"first\" } finally { require 1 > 2, \"second\" }\n  } catch (e) { return theme.notes |> transpose(e.message == \"first\" && at(e.suppressed, 0).message == \"second\" ? 5 : 9) }\n  return theme.notes\n}");
    }

    private const string Loop = "repeat 1000 { let a = 1 }";   // about 3,100 steps: alone it fits the reserve of 5,000, two do not

    private static string Helper(string body) => "helper f() -> Notes {\n  " + body + "\n  return theme.notes\n}";

    private static int Reserve(string body, out PipeResult result, out ObservationSink sink)
    {
        (result, sink) = Run("f()", null, Helper(body));
        return result.Warnings.Count(w => w.Id == "unwind-reserve");
    }

    [Fact]
    public void One_finally_that_fits_the_unwind_reserve_gives_no_notice()
    {
        Assert.Equal(0, Reserve("try { fail \"x\" } finally { " + Loop + " }", out PipeResult result, out _));
        Assert.Contains(result.Errors, e => e.Id == "fail" && e.Message == "x");
    }

    [Fact]
    public void Nested_finally_blocks_share_one_unwind_reserve_and_the_notice_is_given_once()
    {
        string body = "try { try { fail \"x\" } finally { " + Loop + " } } finally { " + Loop + " }";
        Assert.Equal(1, Reserve(body, out PipeResult result, out _));
        Assert.Contains(result.Errors, e => e.Id == "fail" && e.Message == "x");   // the primary does not change
    }

    [Fact]
    public void An_exhausted_reserve_skips_every_remaining_finally()
    {
        string body = "try { try { try { fail \"x\" } finally { " + Loop + " } } finally { " + Loop + " } } finally { log \"outer\" }";
        Assert.Equal(1, Reserve(body, out PipeResult result, out ObservationSink sink));
        Assert.DoesNotContain(sink.Events, e => e.Message == "outer");
        Assert.Contains(result.Errors, e => e.Id == "fail" && e.Message == "x");
    }

    [Fact]
    public void A_try_inside_a_finally_does_not_replenish_the_reserve_and_the_original_failure_stays_primary()
    {
        string body = "try { try { fail \"x\" } finally { try { fail \"y\" } finally { " + Loop + " } } } finally { " + Loop + " }";
        Assert.Equal(1, Reserve(body, out PipeResult result, out _));
        Assert.Contains(result.Errors, e => e.Id == "fail" && e.Message == "x");
        Assert.DoesNotContain(result.Errors, e => e.Message == "y");   // the second failure of equal severity is suppressed
    }

    [Fact]
    public void A_fatal_failure_in_finally_replaces_a_catchable_primary()
    {
        (PipeResult result, _) = Run("f()", null, "helper f() -> Notes {\n  try { throw \"first\" } finally { fail \"worse\" }\n  return theme.notes\n}");
        Assert.Contains(result.Errors, e => e.Id == "fail" && e.Message == "worse");
        Assert.DoesNotContain(result.Errors, e => e.Id == "throw");
    }

    [Fact]
    public void A_new_error_thrown_in_a_catch_remembers_the_handled_one_as_its_cause()
    {
        Gives(4, "f()", "helper f() -> Notes {\n  try {\n    try { throw \"first\" } catch (a) { throw \"second\" }\n  } catch (b) { return theme.notes |> transpose(b.message == \"second\" && b.cause.message == \"first\" ? 4 : 9) }\n  return theme.notes\n}");
    }

    [Fact]
    public void Error_values_carry_code_context_and_cause_and_a_rethrow_keeps_them()
    {
        Gives(6, "f()", "helper f() -> Notes {\n  try {\n    try { throw Error(\"bad preset\", code=\"PRESET\", context={name: \"x\", count: 6}) } catch (a) { throw a }\n  } catch (b) { return theme.notes |> transpose(b.code == \"PRESET\" && b.type == \"Error\" ? b.context.count : 9) }\n  return theme.notes\n}");
    }

    [Fact]
    public void Only_the_whitelisted_members_of_an_error_can_be_read()
    {
        foreach (string member in new[] { "site", "stack", "line", "hint", "displaced" })
        {
            (PipeResult result, _) = Run("f()", null, "helper f() -> Notes {\n  try { throw \"x\" } catch (e) { return theme.notes |> transpose(Int(e." + member + ")) }\n  return theme.notes\n}");
            Assert.Contains(result.Errors, e => e.Id == "unknown-field");
        }
    }

    [Fact]
    public void A_finally_block_cannot_return_emit_or_throw()
    {
        foreach (string statement in new[] { "return theme.notes", "emit theme.notes", "throw \"y\"" })
        {
            List<HelperIssue> issues = [];
            _ = NotePipeline.HelperTable.Build([("helper f() -> Notes {\n  try { let a = 1 } finally { " + statement + " }\n  return theme.notes\n}", 0)], issues);
            Assert.Contains(issues, i => i.Issue.Id == "finally-flow");
        }
    }

    [Fact]
    public void An_error_raised_under_a_dynamic_decision_makes_what_the_catch_does_dynamic()
    {
        const string phase = "dynamic helper phase() -> Int { return currentTime().minute % 8 }";
        (PipeResult result, _) = Run("f()", null, phase, "helper f() -> Notes {\n  var n = 1\n  try { if phase() > 100 { n = 0 } else { throw \"x\" } } catch (e) { n = 2 }\n  return theme.notes |> transpose(n)\n}");
        Assert.Empty(result.Errors);
        Assert.True(result.Dynamic);
        Assert.True(result.Tainted);
    }

    [Fact]
    public void A_helper_that_ends_in_try_needs_a_value_on_every_path()
    {
        List<HelperIssue> issues = [];
        _ = NotePipeline.HelperTable.Build([("helper f() -> Notes {\n  try { return theme.notes } catch (e) { return theme.notes }\n}", 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id == "no-value");
        issues.Clear();
        _ = NotePipeline.HelperTable.Build([("helper g() -> Notes {\n  try { return theme.notes } catch (e) { let a = 1 }\n}", 0)], issues);
        Assert.Contains(issues, i => i.Issue.Id == "no-value");
    }

    [Fact]
    public void The_catchable_failures_of_the_runtime_match_the_table_of_failure_classes()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "spec", "failure-classes.json"));
        FailureClassTable table = FailureClassTable.Parse(json);
        string[] t1 = [.. table.Rows.Where(r => r.Catchable).Select(r => r.Id).Order(StringComparer.Ordinal)];
        Assert.Equal(["conversion", "divide-by-zero", "require", "throw", "unknown-field"], t1);
    }
}
