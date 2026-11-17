using System.Diagnostics;

namespace Ample.Language.Tests;

public sealed class AssertionForkTests
{
    [Fact]
    public void Assertion_fuel_is_separate_and_exhaustion_is_not_a_pass()
    {
        NotePipeline.Context context = new() { Observations = new ObservationSink() };
        for (int i = 0; i < 20; i++) context.Assertions.Charge(NotePipeline.AssertionBudget.StatementFuel);
        Assert.Equal(0, context.Assertions.Available);
        Assert.Equal(NotePipeline.AssertionCondition.Unavailable, NotePipeline.EvaluateAssertionCondition("true", context));
        Assert.Equal(20_000, context.Observations.RemainingObservationBudget(null).Fuel);
        Assert.Empty(context.Observations.Events);
    }

    [Fact]
    public void Sharing_large_notes_is_free_but_an_assertion_cannot_allocate_past_its_memory_budget()
    {
        const int overBudget = NotePipeline.AssertionBudget.MemoryMb * 1_048_576 / 64 + 1;
        PipeNote note = new(0, NotePipeline.Beat, 60, 100, string.Empty, string.Empty);
        PipeValue shared = new() { Span = NotePipeline.Bar, Events = [.. Enumerable.Repeat(note, overBudget)] };
        NotePipeline.Context context = new()
        {
            Limit = overBudget,
            PeekSource = _ => new NotePipeline.SourceInfo(shared, "test", null),
        };
        Assert.Equal(NotePipeline.AssertionCondition.Passed, NotePipeline.EvaluateAssertionCondition("theme.notes", context));
        Assert.Equal(NotePipeline.AssertionCondition.Unavailable,
            NotePipeline.EvaluateAssertionCondition("theme.notes |> transpose(+1)", context));
        Assert.Equal(overBudget, shared.Events.Count);
        Assert.Equal(note, shared.Events[0]);
    }

    [Fact]
    public void Depth_faults_are_unavailable_and_a_missing_peek_never_resolves_a_block()
    {
        List<HelperIssue> issues = [];
        const string helper = "helper descend(Int n) -> Bool { if n == 0 { return true } return descend(n - 1) }";
        NotePipeline.Context context = new()
        {
            Helpers = NotePipeline.HelperTable.Build([(helper, 0)], issues),
            Source = _ => throw new InvalidOperationException("Assertion must only peek"),
        };
        Assert.Empty(issues);
        Assert.Equal(NotePipeline.AssertionCondition.Passed, NotePipeline.EvaluateAssertionCondition("descend(15)", context));
        Assert.Equal(NotePipeline.AssertionCondition.Unavailable, NotePipeline.EvaluateAssertionCondition("descend(16)", context));
        Assert.Equal(NotePipeline.AssertionCondition.Unavailable, NotePipeline.EvaluateAssertionCondition("theme.notes", context));
    }

    [Fact]
    public void Assertion_ignores_elapsed_time_and_keeps_cancellation_live()
    {
        using CancellationTokenSource cancellation = new();
        long now = 0;
        NotePipeline.Context context = new()
        {
            TimeSource = () => now += 100 * Stopwatch.Frequency,
            Budget = TimeSpan.Zero,
            Cancellation = cancellation.Token,
            PeekSource = _ => new NotePipeline.SourceInfo(new PipeValue { Span = NotePipeline.Bar }, "test", null),
        };
        Assert.Equal(NotePipeline.AssertionCondition.False,
            NotePipeline.EvaluateAssertionCondition("theme.notes |> transpose(+1)", context));
        context.PeekSource = _ =>
        {
            cancellation.Cancel();
            return new NotePipeline.SourceInfo(new PipeValue { Span = NotePipeline.Bar }, "test", null);
        };
        Assert.Throws<OperationCanceledException>(() => NotePipeline.EvaluateAssertionCondition("theme.notes", context));
    }

    [Fact]
    public void A_fork_does_not_execute_nested_diagnostics_or_allow_volatile_reads()
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Record | SyntaxFeatures.Assert | SyntaxFeatures.Observe);
        const string helper = "helper inspect() -> Bool { debug 1 / 0; assert false; return true }";
        List<HelperIssue> issues = [];
        NotePipeline.Context context = new()
        {
            Helpers = NotePipeline.HelperTable.Build([(helper, 0)], issues),
            Observations = new ObservationSink(level: LogLevel.Debug),
            Clock = () => throw new InvalidOperationException("Assertion must not read the clock"),
        };
        Assert.Empty(issues);
        Assert.Equal(NotePipeline.AssertionCondition.Passed, NotePipeline.EvaluateAssertionCondition("inspect()", context));
        Assert.Equal(NotePipeline.AssertionCondition.Raised, NotePipeline.EvaluateAssertionCondition("currentTime().hour > 0", context));
        Assert.Empty(context.Observations.Events);
    }
}
