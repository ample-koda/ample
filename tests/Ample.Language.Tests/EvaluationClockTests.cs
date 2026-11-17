using System.Diagnostics;

namespace Ample.Language.Tests;

public sealed class EvaluationClockTests
{
    private const string ObserveClock =
        "helper timed(Notes n) -> Notes { debug currentTime().hour; return n |> transpose(+7) }";

    [Fact]
    public void The_pipeline_budget_uses_the_injected_monotonic_time()
    {
        long now = 0;
        NotePipeline.Context context = new()
        {
            Budget = TimeSpan.FromSeconds(1),
            TimeSource = () => now,
            Source = _ =>
            {
                now += 2 * Stopwatch.Frequency;
                return new NotePipeline.SourceInfo(new PipeValue { Span = NotePipeline.Bar }, "test", null);
            },
        };
        PipeResult result = NotePipeline.Evaluate("theme.notes |> transpose(+7)", context);
        Assert.Contains(result.Errors, issue => issue.Id == "cap-time");
    }

    [Fact]
    public void A_slow_observer_cannot_spend_the_musical_wall_clock_budget()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(ObserveClock, 0)], issues);
        Assert.DoesNotContain(issues, issue => issue.Issue.Id == "helper-effect");
        long now = 0;
        int reads = 0;
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        NotePipeline.Context context = new()
        {
            Helpers = table,
            Budget = TimeSpan.FromSeconds(1),
            TimeSource = () => now,
            Clock = () =>
            {
                now += 100 * Stopwatch.Frequency;
                reads++;
                return new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            },
            Source = _ => new NotePipeline.SourceInfo(notes, "test", null),
            Observations = new ObservationSink(level: LogLevel.Off),
        };
        PipeResult disabled = NotePipeline.Evaluate("timed(theme.notes)", context);
        Assert.Empty(disabled.Errors);
        Assert.Equal(0, reads);
        context.Observations = new ObservationSink(level: LogLevel.Debug);
        PipeResult enabled = NotePipeline.Evaluate("timed(theme.notes)", context);
        Assert.Empty(enabled.Errors);
        Assert.Equal(1, reads);
        Assert.Equal(disabled.Recipe, enabled.Recipe);
        Assert.Equal(disabled.Notes, enabled.Notes);
        Assert.False(enabled.Dynamic);
        Assert.True(Assert.Single(context.Observations.Events, entry => entry.Level == EventLevel.Debug).Volatile);
        Assert.Single(context.Observations.Events, entry => entry.Code == "AMP6007");
    }

    [Fact]
    public void An_unresolved_observation_never_calls_the_musical_resolver()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        const string helper = "helper inspectNotes(Notes n) -> Notes { debug other.notes; return n }";
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.Empty(issues);
        List<string> reads = [];
        List<string> peeks = [];
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        NotePipeline.Context context = new()
        {
            Helpers = table,
            Observations = new ObservationSink(level: LogLevel.Debug),
            PeekSource = name =>
            {
                peeks.Add(name);
                return null;
            },
            Source = name =>
            {
                reads.Add(name);
                return new NotePipeline.SourceInfo(notes, "test", null);
            },
        };
        PipeResult result = NotePipeline.Evaluate("inspectNotes(theme.notes)", context);
        Assert.Empty(result.Errors);
        Assert.Equal(["theme"], reads);
        Assert.Equal(["other"], peeks);
        Assert.Equal("unknown-reference", Assert.Single(context.Observations.Events).Code);
    }

    [Fact]
    public void Observation_redacts_sensitive_values_and_secret_named_fields_before_storage()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        const string helper = """
            dynamic helper observed(Notes n) -> Notes {
              let key = environment("AMPLE_TEST")
              debug(key + " suffix", context={sample: key, token: "field fixture", count: 1})
              return n
            }
            """;
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.Empty(issues);
        const string planted = "test value";
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        NotePipeline.Context context = new()
        {
            Helpers = table,
            Environment = _ => planted,
            Source = _ => new NotePipeline.SourceInfo(notes, "test", null),
            Observations = new ObservationSink(level: LogLevel.Debug),
        };
        PipeResult result = NotePipeline.Evaluate("observed(theme.notes)", context);
        Assert.Empty(result.Errors);
        Assert.True(result.Dynamic);
        LogEvent entry = Assert.Single(context.Observations.Events);
        Assert.Equal(ObsRedactor.Redacted, entry.Message);
        Assert.NotNull(entry.ContextJson);
        Assert.DoesNotContain(planted, entry.ContextJson);
        Assert.DoesNotContain("field fixture", entry.ContextJson);
        Assert.Contains("\"count\":1", entry.ContextJson);
    }

    [Fact]
    public void An_observation_cannot_change_a_shared_notes_value_or_the_main_evaluation_artifacts()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        const string watched = """
            helper observed(Notes n) -> Notes {
              debug(n |> transpose(+12), context={nearby: other.notes})
              return n |> transpose(+2)
            }
            """;
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(watched, 0)], issues);
        Assert.Empty(issues);
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        string hash = NotePipeline.NotesHash(notes);
        NotePipeline.Context context = new()
        {
            Helpers = table,
            Source = _ => new NotePipeline.SourceInfo(notes, hash, null),
            PeekSource = _ => new NotePipeline.SourceInfo(notes, hash, null),
        };
        PipeResult quiet = NotePipeline.Evaluate("observed(theme.notes)", context);
        context.Observations = new ObservationSink(level: LogLevel.Debug);
        PipeResult observed = NotePipeline.Evaluate("observed(theme.notes)", context);
        Assert.Empty(observed.Errors);
        Assert.Single(context.Observations.Events);
        Assert.Equal(hash, NotePipeline.NotesHash(notes));
        Assert.Equal(quiet.Notes, observed.Notes);
        Assert.Equal(quiet.Recipe, observed.Recipe);
        Assert.Equal(quiet.Fuel, observed.Fuel);
        Assert.Equal(quiet.Dynamic, observed.Dynamic);
        Assert.Equal(quiet.DynamicInputs, observed.DynamicInputs);
        Assert.Equal(quiet.Seeds, observed.Seeds);
        Assert.Equal(quiet.Streams, observed.Streams);
        Assert.Equal(quiet.Warnings, observed.Warnings);
        Assert.Empty(context.StageIssues);
    }
}
