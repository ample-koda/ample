namespace Ample.Language.Tests;

/// <summary>Text made with +, String(value) and sprintf in helpers, observations and audio processes: the same text everywhere, a secret stays a secret
/// through every way of making text (the result of a text operation carries the marks of what went in), and the limits hold.</summary>
public sealed class TextBuildingTests
{
    private const string Planted = "planted-credential-value";

    private static List<LogEvent> Events(string statements)
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxGate.Implemented);
        string helper = "dynamic helper observed(Notes n) -> Notes {\n  let key = environment(\"AMPLE_TEST\")\n" + statements + "  return n\n}";
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id != "unused-variable");
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        ObservationSink sink = new(level: LogLevel.Trace);
        NotePipeline.Context context = new() { Helpers = table, Environment = _ => Planted, Source = _ => new NotePipeline.SourceInfo(notes, "test", null), Observations = sink };
        _ = NotePipeline.Evaluate("observed(theme.notes)", context);
        return [.. sink.Events];
    }

    private static string Message(string expression) => Assert.Single(Events("  log " + expression + "\n")).Message;

    [Theory]
    [InlineData("\"Bar \" + 4", "Bar 4")]
    [InlineData("4 + \" bars\"", "4 bars")]
    [InlineData("\"Tempo: \" + 128 + \" BPM\"", "Tempo: 128 BPM")]
    [InlineData("\"Gain: \" + String(-6dB)", "Gain: -6dB")]
    [InlineData("String(2bars)", "8beats")]
    [InlineData("sprintf(\"Bar %03d at %.1f%%\", 4, 75.55)", "Bar 004 at 75.6%")]
    [InlineData("2beats", "2beats")]   // a length in the log reads as beats, the same text as String(2beats)
    public void Made_text_is_logged_as_it_reads(string expression, string expected) => Assert.Equal(expected, Message(expression));

    [Theory]
    [InlineData("\"Secret: \" + String(key)")]
    [InlineData("\"Secret: \" + key")]
    [InlineData("sprintf(\"Secret: %s\", key)")]
    [InlineData("sprintf(\"%s\", sprintf(\"%s\", key))")]   // formatting again does not clean it
    [InlineData("\"n=\" + count(key)")]   // what is computed from a secret is secret too
    [InlineData("sprintf(key)")]   // a secret format
    public void Text_made_from_a_secret_is_redacted(string expression)
    {
        string message = Message(expression);
        Assert.Equal(ObsRedactor.Redacted, message);
        Assert.DoesNotContain(Planted, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_made_text_stays_redacted_in_a_record_a_hint_and_the_context()
    {
        LogEvent e = Assert.Single(Events("  warn \"check\", hint=\"use \" + key, context={label: sprintf(\"k=%s\", key), n: 2}\n"));
        Assert.Equal(ObsRedactor.Redacted, e.Hint);
        Assert.DoesNotContain(Planted, e.ContextJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("\"n\":2", e.ContextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Planted, Message("{m: \"k=\" + key}"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_sprintf_never_shows_a_secret_in_its_message()
    {
        LogEvent e = Assert.Single(Events("  log sprintf(\"%d\", key)\n"));
        Assert.DoesNotContain(Planted, e.Message, StringComparison.Ordinal);
        Assert.Equal("bad-argument", e.Code);
    }

    [Theory]
    [InlineData("log \"room \" + String(room)", "room 40%")]
    [InlineData("log sprintf(\"%s at %d\", label, 3)", "quiet at 3")]
    [InlineData("log \"token \" + token", "[redacted]")]
    [InlineData("log sprintf(\"%s\", token)", "[redacted]")]
    public void An_audio_process_makes_text_the_same_way(string statement, string expected)
    {
        Dictionary<string, string> scope = new() { ["label"] = "\"quiet\"", ["room"] = "40%", ["token"] = "\"" + Planted + "\"" };
        NotePipeline.AudioObservation shown = NotePipeline.EvaluateAudioObservation(statement, scope, new HashSet<string>(), 120, out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(expected, shown.Message);
    }

    private static PipeIssue Failure(string body, string value = Planted)
    {
        using IDisposable gate = SyntaxGate.Use(SyntaxGate.Implemented);
        string helper = "dynamic helper g() -> Int {\n  let key = environment(\"AMPLE_TEST\")\n" + body + "\n}";
        List<HelperIssue> issues = [];
        NotePipeline.HelperTable table = NotePipeline.HelperTable.Build([(helper, 0)], issues);
        Assert.DoesNotContain(issues, i => i.Issue.Id != "unused-variable");
        PipeValue notes = new() { Span = NotePipeline.Bar, Events = NotePipeline.ReadNotes("1:0:1:60:100", []) };
        NotePipeline.Context context = new() { Helpers = table, Environment = _ => value, Source = _ => new NotePipeline.SourceInfo(notes, "test", null) };
        return Assert.Single(NotePipeline.Evaluate("theme.notes |> transpose(g())", context).Errors);
    }

    // the twin of web/lib/test/text-secrets.test.mjs: the same mistakes, the same promise
    [Theory]
    [InlineData("  return count(sprintf(\"%d\", key))", true)]
    [InlineData("  return count(sprintf(\"%f\", key))", true)]
    [InlineData("  return count(sprintf(\"%d\", \"k=\" + key))", true)]
    [InlineData("  return count(sprintf(\"%.2f\", String(key)))", true)]
    [InlineData("  return count(key + true)", false)]   // the message names the Bool, not the text
    [InlineData("  return count(sprintf(\"%s!\", key) + [1])", false)]
    [InlineData("  return count({k: key} + \"x\")", true)]
    [InlineData("  return count(key - 1)", true)]
    [InlineData("  return Int(key)", true)]
    public void A_wrong_operation_on_a_secret_never_shows_it_in_the_error(string body, bool shown)
    {
        PipeIssue e = Failure(body);
        Assert.DoesNotContain(Planted, e.Message, StringComparison.Ordinal);
        if (shown) Assert.Contains(ObsRedactor.Redacted, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%q-")]
    [InlineData("%.f")]
    [InlineData("%100d")]
    [InlineData("%d and %d ")]
    public void A_secret_format_never_shows_its_placeholders(string start)
    {
        PipeIssue e = Failure("  return count(sprintf(key, 1))", start + Planted);
        Assert.Equal("bad-argument", e.Id);
        Assert.DoesNotContain(Planted, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"%", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  return count(String(1, 2))")]
    [InlineData("  return count(String())")]
    [InlineData("  return count(String(value=1))")]
    public void String_takes_exactly_one_value(string body) => Assert.Equal("bad-argument", Failure(body).Id);

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123456789012345680000d, "123456789012345680000")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(-0.0, "0")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void A_computed_decimal_is_written_as_JavaScript_writes_it(double value, string expected) => Assert.Equal(expected, NotePipeline.NumberTextForTests(value));
}
