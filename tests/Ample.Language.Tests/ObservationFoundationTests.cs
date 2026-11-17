using System.Text.RegularExpressions;

namespace Ample.Language.Tests;

/// <summary>
/// WP2 of plan/diagnostics/PLAN.md: the observation channel (events, bounded sink, redaction), the site resolver and the failure model with its class table. Nothing here runs AMPLE code yet: it is the foundation
/// that the observation runtime (WP4) and the exceptions (WP5a) stand on, so its limits and its determinism are tested at their edges.
/// </summary>
public sealed class ObservationFoundationTests
{
    private static readonly SourceSite Here = new("song", 4, 3, 10);

    private static string Key(EventLevel level, int line = 4) => ObservationSink.SiteKeyOf(new SourceSite("song", line, 3, 10), null, level);

    private static LogEvent Event(EventDecision d, EventLevel level, string message = "m", string? json = null) =>
        new(d.Seq, level, d.SiteKey, Here, null, message, null, json, []);

    private static EventDecision Emit(ObservationSink sink, EventLevel level, int line = 4, string? json = null)
    {
        EventDecision d = sink.Begin(level, Key(level, line));
        if (d.Evaluate) sink.Add(d, Event(d, level, json: json));
        return d;
    }

    // ---- redaction and caps

    [Theory]
    [InlineData("token", true)]
    [InlineData("apiKey", true)]
    [InlineData("api-key", true)]
    [InlineData("API_KEY", true)]
    [InlineData("accessToken", true)]
    [InlineData("user_password", true)]
    [InlineData("Authorization", true)]
    [InlineData("sessionId", true)]
    [InlineData("key", false)]   // a musical key is not a secret
    [InlineData("tonic", false)]
    [InlineData("keyboard", false)]
    [InlineData("voices", false)]
    [InlineData("provider", false)]
    public void A_field_name_that_says_secret_is_redacted_and_a_musical_name_is_not(string name, bool secret) => Assert.Equal(secret, ObsRedactor.IsSecretName(name));

    [Fact]
    public void The_value_of_a_secret_field_and_a_sensitive_value_never_reach_the_text()
    {
        ObsValue value = new ObsRecord([("provider", new ObsText("example")), ("token", new ObsText("s3cret")), ("nested", new ObsRecord([("apiKey", new ObsText("abc")), ("key", new ObsText("C"))])), ("fromEnvironment", new ObsText("hunter2", true))]);
        string json = ObsRedactor.ToJson(value);
        Assert.Equal("{\"provider\":\"example\",\"token\":\"[redacted]\",\"nested\":{\"apiKey\":\"[redacted]\",\"key\":\"C\"},\"fromEnvironment\":\"[redacted]\"}", json);
        Assert.DoesNotContain("s3cret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_is_bounded_in_depth_items_text_and_bytes_with_a_visible_mark()
    {
        ObsValue deep = new ObsNumber("1");
        for (int i = 0; i < 12; i++) deep = new ObsList([deep]);
        Assert.Contains("too deep", ObsRedactor.ToJson(deep), StringComparison.Ordinal);
        Assert.Contains("+36 more", ObsRedactor.ToJson(new ObsList([.. Enumerable.Range(0, 100).Select(i => (ObsValue)new ObsNumber(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))])), StringComparison.Ordinal);
        string longText = ObsRedactor.ToJson(new ObsText(new string('a', 1000)));
        Assert.True(longText.Length < 300);
        Assert.EndsWith("…\"", longText, StringComparison.Ordinal);
        string big = ObsRedactor.ToJson(new ObsRecord([.. Enumerable.Range(0, 60).Select(i => ("field" + i, (ObsValue)new ObsText(new string('x', 200))))]));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(big) <= 4096);
        Assert.Contains("(cut)", big, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_value_always_gives_the_same_text()
    {
        ObsValue v = new ObsRecord([("b", new ObsNumber("2")), ("a", new ObsList([new ObsBool(true), new ObsNull(), new ObsText("x\n\"y\"")]))]);
        Assert.Equal(ObsRedactor.ToJson(v), ObsRedactor.ToJson(v));
        Assert.Equal("{\"b\":2,\"a\":[true,null,\"x\\n\\\"y\\\"\"]}", ObsRedactor.ToJson(v));   // source order, escaped
    }

    // ---- the sink

    [Fact]
    public void An_ordinary_log_below_the_level_costs_nothing_and_warnings_and_errors_are_always_registered()
    {
        ObservationSink sink = new(level: LogLevel.Off);
        Assert.False(sink.Begin(EventLevel.Info, Key(EventLevel.Info)).Registered);
        Assert.False(sink.Begin(EventLevel.Trace, Key(EventLevel.Trace)).Registered);
        Assert.True(sink.Begin(EventLevel.Warning, Key(EventLevel.Warning)).Registered);
        Assert.True(sink.Begin(EventLevel.Error, Key(EventLevel.Error)).Registered);
        Assert.Equal(0, sink.CountOf(EventClass.Log));   // nothing was counted for the disabled ones
        ObservationSink debug = new(level: LogLevel.Debug);
        Assert.True(debug.Allows(EventLevel.Info));
        Assert.True(debug.Allows(EventLevel.Debug));
        Assert.False(debug.Allows(EventLevel.Trace));
    }

    [Fact]
    public void One_site_stores_its_first_events_counts_the_rest_exactly_and_samples_at_powers_of_two()
    {
        ObservationSink sink = new(new ObservationBudgets(MaxPerSite: 3), LogLevel.Info);
        List<EventDecision> decisions = [.. Enumerable.Range(1, 10).Select(i => Emit(sink, EventLevel.Info, json: "{\"i\":" + i + "}"))];
        Assert.Equal([true, true, true, false, false, false, false, false, false, false], decisions.Select(d => d.Store));
        Assert.Equal([4L, 8L], decisions.Where(d => d.Sample).Select(d => d.Ordinal));   // the ordinals that are powers of two after the stored ones
        Assert.Equal(3, sink.Events.Count);
        SiteCounter site = Assert.Single(sink.Sites);
        Assert.Equal(10, site.Count);
        Assert.Equal(3, site.Stored);
        Assert.Equal(7, site.Suppressed);
        Assert.Equal(10, site.LastSeenOrdinal);
        Assert.Equal(8, site.LastEvaluatedOrdinal);   // the last evaluated event says which one it was: never presented as the last value
        Assert.Equal("{\"i\":8}", site.LastEvaluatedJson);
    }

    [Fact]
    public void Each_class_has_its_own_budget_so_trace_can_never_push_a_warning_out()
    {
        ObservationSink sink = new(new ObservationBudgets(MaxPerSite: 3, MaxTotal: 4, TraceMaxPerSite: 2, TraceMaxTotal: 5, DiagnosticMaxTotal: 6), LogLevel.Trace);
        for (int i = 0; i < 50; i++) Emit(sink, EventLevel.Trace, i);   // 50 different sites flood the trace class
        for (int i = 0; i < 10; i++) Emit(sink, EventLevel.Warning, 100 + i);
        for (int i = 0; i < 10; i++) Emit(sink, EventLevel.Debug, 200 + i);
        Assert.Equal(5, sink.StoredOf(EventClass.Trace));
        Assert.Equal(6, sink.StoredOf(EventClass.Diagnostic));   // all six that the warning budget allows, whatever trace did
        Assert.Equal(4, sink.StoredOf(EventClass.Log));
        Assert.Equal(50, sink.CountOf(EventClass.Trace));   // the counters are exact
        Assert.Equal(10, sink.CountOf(EventClass.Diagnostic));
    }

    [Fact]
    public void The_number_of_sites_that_are_followed_is_bounded_and_the_rest_share_one_bucket()
    {
        ObservationSink sink = new(new ObservationBudgets(MaxSites: 5), LogLevel.Info);
        for (int i = 0; i < 100; i++) Emit(sink, EventLevel.Info, i);
        Assert.Equal(6, sink.Sites.Count);
        SiteCounter other = Assert.Single(sink.Sites, s => s.SiteKey == ObservationSink.OtherSites);
        Assert.Equal(95, other.Count);
    }

    [Fact]
    public void Overflowed_trace_sites_cannot_use_the_warning_storage_slot()
    {
        ObservationSink sink = new(new ObservationBudgets(MaxPerSite: 1, TraceMaxPerSite: 1, MaxSites: 1), LogLevel.Trace);
        Emit(sink, EventLevel.Trace, 1);
        Emit(sink, EventLevel.Trace, 2);
        Assert.True(Emit(sink, EventLevel.Warning, 3).Store);
        Assert.Equal(1, sink.StoredOf(EventClass.Diagnostic));
        Assert.Equal(2, sink.Sites.Count(site => site.SiteKey == ObservationSink.OtherSites));
    }

    [Fact]
    public void The_same_calls_give_the_same_events_in_the_same_order_with_the_same_numbers()
    {
        static (string, string) Run()
        {
            ObservationSink sink = new(new ObservationBudgets(MaxPerSite: 2), LogLevel.Debug);
            for (int i = 0; i < 40; i++) Emit(sink, i % 3 == 0 ? EventLevel.Warning : EventLevel.Debug, i % 5, "{\"i\":" + i + "}");
            return (string.Join("|", sink.Events.Select(e => $"{e.Seq}:{e.Level}:{e.SiteKey}:{e.ContextJson}")), string.Join("|", sink.Sites.Select(s => $"{s.SiteKey}:{s.Count}:{s.Stored}:{s.LastEvaluatedOrdinal}")));
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void A_message_that_varies_does_not_make_a_site_per_message()
    {
        ObservationSink sink = new(level: LogLevel.Info);
        for (int i = 0; i < 20; i++)
        {
            EventDecision d = sink.Begin(EventLevel.Info, Key(EventLevel.Info));
            if (d.Evaluate) sink.Add(d, Event(d, EventLevel.Info, "message " + i));
        }

        Assert.Single(sink.Sites);
    }

    // ---- places

    [Fact]
    public void An_offset_in_the_text_of_a_helper_becomes_a_line_and_column_of_the_file()
    {
        string helper = "helper g(Notes s) -> Notes {\n  let x = 1 / 0\n  return s\n}";
        int start = helper.IndexOf("1 / 0", StringComparison.Ordinal);
        SourceSite site = SiteResolver.At("song", helper, 19, start, start + 5);   // the declaration starts on the 20th line (0-based 19)
        Assert.Equal(new SourceSite("song", 21, 11, 16), site);   // line 20 holds the head, line 21 the statement; "  let x = " is ten characters
    }

    [Fact]
    public void A_place_in_an_expanded_song_maps_back_to_the_line_that_was_written()
    {
        int[] origin = [0, 1, 2, 2, 2, 3];   // three expanded lines stand for the third written line
        Assert.Equal(3, SiteResolver.Original(new SourceSite("song", 5, 1, 4), origin).Line);
        Assert.Equal(9, SiteResolver.Original(new SourceSite("song", 9, 1, 4), origin).Line);   // a line that is not in the map stays
    }

    // ---- failures

    private static FailureClassTable Table() => FailureClassTable.Parse(Corpus.Read("spec/failure-classes.json"));

    [Fact]
    public void Every_failure_id_that_the_csharp_evaluator_raises_has_a_class()
    {
        FailureClassTable table = Table();
        string[] sources = ["src/Ample.Language/NoteLang.Interp.cs", "src/Ample.Language/NoteLang.cs", "src/Ample.Language/NoteLang.Parser.cs", "src/Ample.Language/NotePipeline.cs", "src/Ample.Language/NoteLang.Variables.cs"];
        HashSet<string> ids = [.. sources.SelectMany(s => Regex.Matches(Corpus.Read(s), "LangFailure\\(\"([a-z-]+)\"").Select(m => m.Groups[1].Value)), "cap-time", "missing-unit"];
        Assert.Contains("divide-by-zero", ids);
        foreach (string id in ids)
        {
            FailureClassRow? row = table.Find(id);
            Assert.True(row is not null, $"the failure id {id} is raised by the C# evaluator but has no class in spec/failure-classes.json");
            Assert.Contains("csharp", row!.Runtimes);
        }
    }

    [Fact]
    public void The_classes_say_what_a_catch_and_a_finally_may_do()
    {
        FailureClassTable table = Table();
        Assert.True(table.Find("divide-by-zero")!.Catchable);
        Assert.True(table.Find("divide-by-zero")!.RunsFinally);
        Assert.False(table.Find("fail")!.Catchable);
        Assert.True(table.Find("fail")!.RunsFinally);   // a controlled fatal: finally runs under the reserve
        Assert.False(table.Find("assert")!.Catchable);
        Assert.False(table.Find("fuel")!.Catchable);
        Assert.False(table.Find("fuel")!.RunsFinally);   // a safety stop: no user code after it
        Assert.False(table.Find("cancelled")!.RunsFinally);
        Assert.False(table.Find("stop-at")!.RunsFinally);
        Assert.Equal("DIV_ZERO", table.Find("divide-by-zero")!.Code);
        Assert.All(table.Rows.Where(r => r.Catchable), r => Assert.False(string.IsNullOrEmpty(r.Code)));   // every catchable failure has a stable code
        Assert.All(table.Rows.Where(r => !r.Catchable), r => Assert.Null(r.Code));
    }

    [Fact]
    public void A_failure_that_is_more_severe_ranks_higher_and_keeps_what_it_displaces()
    {
        Failure caught = new("divide-by-zero", FailureClass.Catchable, "Error", "Division by zero", "DIV_ZERO", Here, [], 1);
        Failure fatal = new("fail", FailureClass.Fatal, "Error", "stop", null, Here, [], 2) { Displaced = [caught], Suppressed = [new FailureSummary("Error", "x", null)], SuppressedCount = 1 };
        Failure safety = new("fuel", FailureClass.Safety, "Error", "out of fuel", null, Here, [], 3);
        Assert.True(caught.Rank < fatal.Rank);
        Assert.True(fatal.Rank < safety.Rank);
        Assert.Equal(new Failure("stop-at", FailureClass.Stop, "Stop", "stop", null, null, [], 0).Rank, safety.Rank);
        Assert.Same(caught, Assert.Single(fatal.Displaced));
    }
}
