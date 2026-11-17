namespace Ample.Language.Tests;

/// <summary>WP5b of plan/diagnostics/PLAN.md (D9r, D27, I15): assert is off by default, runs in the assertion fork when on, fails fatally (T2) and never changes the music.</summary>
public sealed class AssertTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100";

    private static ObservationConfig On => ObservationConfig.Resolve([new ConfigLayerInput(new ConfigSource(ConfigLayer.Song, "song", 3),
        new Dictionary<string, (string, int)> { ["diagnostics.assertions"] = ("on", 3), ["logging.level"] = ("debug", 3) })]);

    private static (PipeResult Result, ObservationSink Sink) Run(bool on, string expr, long? fuel, params string[] decls)
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
            ObservationOptions = on ? On : null,
            Clock = () => new DateTime(2026, 11, 16, 10, 5, 0),
        };
        if (fuel is { } f) context.Limits = new LangLimits { Fuel = f };
        return (NotePipeline.Evaluate(expr, context), sink);
    }

    private static string Helper(string statements) => "helper f(Int n) -> Notes {\n  " + statements + "\n  return theme.notes |> transpose(n)\n}";

    [Fact]
    public void Off_by_default_neither_the_condition_nor_the_message_is_evaluated()
    {
        (PipeResult result, _) = Run(false, "f(1)", null, Helper("assert 1 / 0 > 0, 1 / 0"));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void On_and_passing_the_output_is_identical_to_off()
    {
        string helper = Helper("assert n > 0, \"n must be positive\"");
        (PipeResult off, _) = Run(false, "f(3)", null, helper);
        (PipeResult on, _) = Run(true, "f(3)", null, helper);
        Assert.Empty(on.Errors);
        Assert.Equal(off.Notes, on.Notes);
        Assert.Equal(off.Recipe, on.Recipe);
        Assert.Equal(off.Dynamic, on.Dynamic);
    }

    [Fact]
    public void On_and_failing_the_evaluation_stops_with_the_message_or_the_text_of_the_condition()
    {
        (PipeResult withMessage, _) = Run(true, "f(0)", null, Helper("assert n > 0, \"n must be positive\""));
        Assert.Contains(withMessage.Errors, e => e.Id == "assert" && e.Message == "n must be positive");
        (PipeResult plain, _) = Run(true, "f(0)", null, Helper("assert n   >  0"));
        Assert.Contains(plain.Errors, e => e.Id == "assert" && e.Message == "Assertion failed: n > 0");
        Assert.Null(plain.Notes);
    }

    [Fact]
    public void A_failed_assertion_is_not_catchable_and_finally_still_runs()
    {
        (PipeResult result, ObservationSink sink) = Run(true, "f(0)", null, Helper("try { assert n > 0 } catch (e) { log \"caught\" } finally { log \"cleanup\" }"));
        Assert.Contains(result.Errors, e => e.Id == "assert");
        Assert.DoesNotContain(sink.Events, e => e.Message == "caught");
        Assert.Contains(sink.Events, e => e.Message == "cleanup");
    }

    [Fact]
    public void A_condition_that_raises_an_error_fails_the_assertion()
    {
        (PipeResult result, _) = Run(true, "f(0)", null, Helper("assert 4 / n > 1"));
        Assert.Contains(result.Errors, e => e.Id == "assert");
    }

    [Fact]
    public void A_condition_that_cannot_be_evaluated_within_its_budget_fails_with_its_own_id()
    {
        (PipeResult result, _) = Run(true, "f(1)", null, "helper spin(Int n) -> Bool {\n  var i = 0\n  while i < 9000 max 9000 { i = i + 1 }\n  return n > 0\n}", "helper f(Int n) -> Notes {\n  repeat 12 { assert spin(n) }\n  return theme.notes\n}");
        Assert.Contains(result.Errors, e => e.Id == "assert-unavailable");
    }

    [Fact]
    public void The_statement_costs_the_same_main_fuel_on_or_off()
    {
        string helper = Helper("assert n > 0 && n < 100, \"range\"");
        long Lowest(bool on)
        {
            for (long fuel = 5; fuel < 200; fuel++)
            {
                if (!Run(on, "f(3)", fuel, helper).Result.Errors.Any(e => e.Id == "fuel")) return fuel;
            }

            return -1;
        }

        Assert.Equal(Lowest(false), Lowest(true));
        Assert.True(Lowest(false) > 0);
    }

    [Fact]
    public void An_assertion_inside_a_helper_called_by_a_condition_does_nothing()
    {
        (PipeResult result, _) = Run(true, "f(1)", null, "helper inner(Int n) -> Bool {\n  assert n > 100\n  return n > 0\n}", Helper("assert inner(n)"));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void The_music_does_not_depend_on_the_switch_when_everything_passes()
    {
        string helper = Helper("assert n > 0\n  log n");
        Assert.Equal(Run(false, "f(2)", null, helper).Result.Notes, Run(true, "f(2)", null, helper).Result.Notes);
    }

    [Fact]
    public void The_assert_feature_is_shipped()
    {
        Assert.True(SyntaxGate.Supports(SyntaxFeatures.Assert));
    }
}
