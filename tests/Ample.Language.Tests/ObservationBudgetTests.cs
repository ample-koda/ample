using System.Diagnostics;

namespace Ample.Language.Tests;

/// <summary>WP4b of plan/diagnostics/PLAN.md (R2.8, D28): the session limits of observation follow the configuration, exhaustion is reported once, and a denied statement is never evaluated.</summary>
public sealed class ObservationBudgetTests
{
    private const string Helper = "helper seen(Notes n) -> Notes { for i in 0..5 { debug i } return n }";

    private static ObservationConfig Config(params (string Key, string Value)[] values)
    {
        Dictionary<string, (string, int)> layer = new(StringComparer.Ordinal) { ["logging.level"] = ("debug", 1) };
        foreach ((string key, string value) in values) layer[key] = (value, 1);
        return ObservationConfig.Resolve([new ConfigLayerInput(new ConfigSource(ConfigLayer.Session, "test"), layer)]);
    }

    private static PipeResult Run(NotePipeline.Context context)
    {
        List<HelperIssue> issues = [];
        context.Helpers = NotePipeline.HelperTable.Build([(Helper, 0)], issues);
        Assert.Empty(issues);
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        context.Source = _ => new NotePipeline.SourceInfo(notes, "test", null);
        return NotePipeline.Evaluate("seen(theme.notes)", context);
    }

    [Fact]
    public void A_low_configured_fuel_limit_ends_observation_and_says_so_once()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        ObservationConfig config = Config(("logging.fuel", "4"), ("logging.maxPerSite", "1000"));
        ObservationSink sink = new(config.Budgets, LogLevel.Debug);
        PipeResult result = Run(new NotePipeline.Context { Observations = sink, ObservationOptions = config });
        Assert.Empty(result.Errors);
        Assert.Single(sink.Events, entry => entry.Code == "AMP6007");
        LogEvent[] stored = [.. sink.Events.Where(entry => entry.Level == EventLevel.Debug)];
        Assert.True(stored.Length is > 0 and < 5, $"stored {stored.Length}");
        SiteCounter site = Assert.Single(sink.Sites);
        Assert.Equal(5, site.Count);   // every statement is counted, only the evaluation stops
    }

    [Fact]
    public void The_default_limits_do_not_stop_a_small_loop_and_the_notes_do_not_change()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        ObservationConfig low = Config(("logging.fuel", "4"));
        PipeResult limited = Run(new NotePipeline.Context { Observations = new ObservationSink(low.Budgets, LogLevel.Debug), ObservationOptions = low });
        ObservationSink plain = new(level: LogLevel.Debug);
        PipeResult open = Run(new NotePipeline.Context { Observations = plain });
        Assert.DoesNotContain(plain.Events, entry => entry.Code == "AMP6007");
        Assert.Equal(5, plain.Events.Count(entry => entry.Level == EventLevel.Debug) + plain.Sites.Sum(s => (int)s.Suppressed));
        Assert.Equal(open.Recipe, limited.Recipe);
        Assert.Equal(open.Notes, limited.Notes);
    }

    private static (PipeResult Result, ObservationSink Sink, int Reads) RunSlow(long readTicks, ObservationConfig? config)
    {
        long now = 0;
        int reads = 0;
        const string slow = "helper slow(Notes n) -> Notes { for i in 0..5 { debug currentTime().hour } return n }";
        List<HelperIssue> issues = [];
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        ObservationSink sink = new(config?.Budgets, LogLevel.Debug);
        NotePipeline.Context context = new()
        {
            Helpers = NotePipeline.HelperTable.Build([(slow, 0)], issues),
            TimeSource = () => now,
            Clock = () =>
            {
                now += readTicks;
                reads++;
                return new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            },
            Source = _ => new NotePipeline.SourceInfo(notes, "test", null),
            Observations = sink,
            ObservationOptions = config,
        };
        return (NotePipeline.Evaluate("slow(theme.notes)", context), sink, reads);
    }

    [Fact]
    public void A_statement_that_reaches_its_own_time_cap_ends_evaluation_for_the_session_with_one_notice()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        (PipeResult result, ObservationSink sink, int reads) = RunSlow(Stopwatch.Frequency * 3 / 10, null);   // each clock read costs 300 ms, over the 250 ms cap
        Assert.Empty(result.Errors);
        Assert.Equal(1, reads);   // the first statement is cut, the other four are only counted
        Assert.Single(sink.Events, entry => entry.Code == "AMP6007");
        Assert.Equal(5, Assert.Single(sink.Sites).Count);
    }

    [Fact]
    public void A_configured_time_budget_is_the_session_limit_and_small_statements_add_up_to_it()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        ObservationConfig config = Config(("logging.timeBudget", "100"));
        (PipeResult result, ObservationSink sink, int reads) = RunSlow(Stopwatch.Frequency * 6 / 100, config);   // 60 ms per statement, 100 ms for the session
        Assert.Empty(result.Errors);
        Assert.Equal(2, reads);   // 60 ms, then 120 ms is over the 100 ms session budget: the rest is only counted
        Assert.Single(sink.Events, entry => entry.Code == "AMP6007");
    }

    [Fact]
    public void Observation_time_that_stays_under_every_limit_is_never_reported()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        (PipeResult result, ObservationSink sink, int reads) = RunSlow(Stopwatch.Frequency / 10, null);   // 5 x 100 ms = 0.5 s
        Assert.Empty(result.Errors);
        Assert.Equal(4, reads);   // three stored events and the sampled 4th (a power of two); the 5th is only counted
        Assert.DoesNotContain(sink.Events, entry => entry.Code == "AMP6007");
    }
}
