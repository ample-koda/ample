namespace Ample.Language.Tests;

/// <summary>WP9 of plan/diagnostics/PLAN.md: one representation of an observed value. The message is text for people (a text as it is, a single value as it is written,
/// a list or a record as compact JSON, a secret as the redaction mark), the same in a helper and in an audio process; the context is JSON; nothing is encoded twice.</summary>
public sealed class ObservationValueFormatTests
{
    private const string Planted = "planted-credential-value";

    private static List<LogEvent> Events(string statements)
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxGate.Implemented);
        string helper = "dynamic helper observed(Notes n) -> Notes {\n  let key = environment(\"AMPLE_TEST\")\n" + statements + "  return n\n}";
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id != "unused-variable");   // key is only used by some of the cases
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        ObservationSink sink = new(level: LogLevel.Trace);
        NotePipeline.Context context = new() { Helpers = table, Environment = _ => Planted, Source = _ => new NotePipeline.SourceInfo(notes, "test", null), Observations = sink };
        _ = NotePipeline.Evaluate("observed(theme.notes)", context);
        return [.. sink.Events];
    }

    private static string Message(string expression) => Assert.Single(Events("  log " + expression + "\n")).Message;

    [Theory]
    [InlineData("\"plain text\"", "plain text")]   // a text as it is, without quotes
    [InlineData("\"a \\ b\"", "a \\ b")]   // a backslash stays one backslash
    [InlineData("3", "3")]
    [InlineData("2.5", "2.5")]
    [InlineData("40%", "40%")]   // a single value as it is written, not "40%"
    [InlineData("-3dB", "-3dB")]
    [InlineData("true", "true")]
    [InlineData("[1, 2]", "[1,2]")]
    [InlineData("{a: 1, b: \"x\"}", "{\"a\":1,\"b\":\"x\"}")]
    [InlineData("{level: 40%, tags: [\"a\"]}", "{\"level\":\"40%\",\"tags\":[\"a\"]}")]   // inside JSON a quantity is a JSON string
    [InlineData("{path: \"a \\ b\"}", "{\"path\":\"a \\\\ b\"}")]   // escaped once, inside the JSON only
    public void A_value_is_shown_as_text_for_people(string expression, string expected) => Assert.Equal(expected, Message(expression));

    [Fact]
    public void A_secret_is_the_redaction_mark_without_quotes_and_a_secret_field_is_redacted_in_place()
    {
        Assert.Equal(ObsRedactor.Redacted, Message("key"));
        string record = Message("{token: key, count: 2}");
        Assert.DoesNotContain(Planted, record, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", record, StringComparison.Ordinal);
        Assert.Contains(ObsRedactor.Redacted, record, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_text_is_cut_and_the_context_is_valid_json_that_is_not_a_text()
    {
        Assert.EndsWith("…", Message("\"" + new string('x', 300) + "\""), StringComparison.Ordinal);
        LogEvent withContext = Assert.Single(Events("  debug \"x\", context={note: \"q\", n: 2}\n"));
        System.Text.Json.JsonElement context = ObsRedactor.ParseContext(withContext.ContextJson)!.Value;
        Assert.Equal(System.Text.Json.JsonValueKind.Object, context.ValueKind);
        Assert.Equal("q", context.GetProperty("note").GetString());
        Assert.Null(ObsRedactor.ParseContext(null));
    }

    [Theory]
    [InlineData("log label", "quiet")]
    [InlineData("log room", "40%")]
    [InlineData("log room * 2", "80%")]
    [InlineData("log {r: room}", "{\"r\":\"40%\"}")]
    [InlineData("log token", "[redacted]")]
    public void An_audio_process_shows_a_value_the_same_way(string statement, string expected)
    {
        Dictionary<string, string> scope = new() { ["label"] = "\"quiet\"", ["room"] = "40%", ["token"] = "\"" + Planted + "\"" };
        NotePipeline.AudioObservation shown = NotePipeline.EvaluateAudioObservation(statement, scope, new HashSet<string>(), 120, out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(expected, shown.Message);
    }
}
