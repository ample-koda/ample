namespace Ample.Language.Tests;

/// <summary>WP4b of plan/diagnostics/PLAN.md (D21, 11h): a sensitive value (read from the environment) must never reach an event, whichever route it takes: text, list, record, seed, error and counters.</summary>
public sealed class ObservationDisclosureTests
{
    private const string Planted = "planted-credential-value";

    [Theory]
    [InlineData("key")]
    [InlineData("key + \" suffix\"")]
    [InlineData("[key, 1]")]
    [InlineData("{inner: {deep: key}}")]
    [InlineData("stable(key)")]
    [InlineData("stable(key).fork(\"x\")")]
    [InlineData("stable(key + \"fixed\").fork(key)")]
    [InlineData("at([key], 5)")]
    [InlineData("key + 1")]
    [InlineData("choose([key, key], 0)")]
    public void A_sensitive_value_never_reaches_an_event_by_any_route(string expression)
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record);
        string helper = "dynamic helper observed(Notes n) -> Notes {\n  let key = environment(\"AMPLE_TEST\")\n  debug(" + expression + ", context={value: " + expression + ", note: \"x\"})\n  log " + expression + "\n  return n\n}";
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.Empty(issues);
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        ObservationSink sink = new(level: LogLevel.Debug);
        NotePipeline.Context context = new()
        {
            Helpers = table,
            Environment = _ => Planted,
            Source = _ => new NotePipeline.SourceInfo(notes, "test", null),
            Observations = sink,
        };
        _ = NotePipeline.Evaluate("observed(theme.notes)", context);
        Assert.NotEmpty(sink.Events);
        foreach (LogEvent entry in sink.Events)
        {
            string everything = string.Join("|", entry.Message, entry.Hint, entry.Code, entry.ContextJson, entry.SiteKey, entry.Site, string.Join(",", entry.Stack));
            Assert.DoesNotContain(Planted, everything, StringComparison.Ordinal);
        }

        foreach (SiteCounter site in sink.Sites)
        {
            Assert.DoesNotContain(Planted, site.LastEvaluatedJson ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
