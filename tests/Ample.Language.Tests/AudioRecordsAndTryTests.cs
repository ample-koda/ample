namespace Ample.Language.Tests;

/// <summary>WP13 of plan/diagnostics/PLAN.md: records, fail and require, and try/catch/finally in the process of an audio component. The process is built when the instance is expanded: a try whose body cannot be built is undone and its catch is built instead.</summary>
public sealed class AudioRecordsAndTryTests
{
    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string Process(string body) => "component koda.fx.test version 1.0.0 {\n  input audio: Audio\n  property level: Gain = -3dB\n  property room: Percent = 40%\n  property token: String = \"very-private\"\n  output audio: Audio\n  process {\n" + body + "\n  }\n}\n";

    private static AudioGraph Graph(string body) => AudioGraphBuilder.Build(Only(Process(body)), Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);

    private static string Shape(AudioGraph g) => string.Join("; ", g.Nodes.Select(n => $"{n.Id}:{n.Template}({string.Join(",", n.Parameters.Select(p => p.Item1 + "=" + p.Item2))})<-{string.Join(",", n.Inputs.Select(i => i.Item1 + "=" + i.Item2))}"));

    private static IEnumerable<ComponentIssue> Errors(AudioGraph g) => g.Issues.Where(i => i.Severity == Severity.Error);

    // ---- records

    [Fact]
    public void The_fields_of_a_record_are_read_where_a_value_is_read()
    {
        AudioGraph record = Graph("    let cfg = {wet: room, inner: {g: level}}\n    emit audio |> reverb(size=cfg.wet) |> gain(cfg.inner.g)");
        AudioGraph direct = Graph("    emit audio |> reverb(size=room) |> gain(level)");
        Assert.Empty(Errors(record));
        Assert.Equal(Shape(direct), Shape(record));
    }

    [Fact]
    public void A_field_decides_an_if_and_a_record_is_shown_in_a_message()
    {
        AudioGraph g = Graph("    let cfg = {wide: true, size: room}\n    if (cfg.wide) {\n      warn cfg\n    }\n    emit audio");
        Assert.Empty(Errors(g));
        AudioWarning shown = Assert.Single(g.Warnings);
        Assert.Contains("40%", shown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_that_holds_a_secret_stays_redacted_through_its_fields()
    {
        AudioGraph g = Graph("    let cfg = {key: token}\n    warn cfg.key\n    warn cfg\n    emit audio");
        Assert.Equal(2, g.Warnings.Count);
        Assert.All(g.Warnings, w => Assert.DoesNotContain("very-private", w.Message, StringComparison.Ordinal));
    }

    // ---- fail and require

    [Fact]
    public void Fail_and_a_require_that_does_not_hold_are_errors_of_the_instance()
    {
        Assert.Contains(Graph("    fail \"no room\"\n    emit audio").Issues, i => i.Code == "AMP6002" && i.Message == "no room");
        Assert.Contains(Graph("    require room > 50%, \"too small\"\n    emit audio").Issues, i => i.Code == "AMP6005" && i.Message == "too small");
        Assert.Empty(Errors(Graph("    require room < 50%, \"too large\"\n    emit audio")));
    }

    // ---- try, catch, finally

    [Fact]
    public void A_try_whose_body_cannot_be_built_is_undone_and_its_catch_is_built()
    {
        AudioGraph g = Graph("    try {\n      let wet = audio |> nosuchprocessor()\n      emit wet\n    } catch (e) {\n      warn e.code\n      emit audio |> gain(level)\n    }");
        Assert.Empty(Errors(g));
        Assert.Equal(Shape(Graph("    emit audio |> gain(level)")), Shape(g));
        Assert.StartsWith("AMP", Assert.Single(g.Warnings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fail_in_a_try_is_caught_with_its_message()
    {
        AudioGraph g = Graph("    try {\n      fail \"no room\"\n    } catch (Error e) {\n      warn e.message\n    }\n    emit audio");
        Assert.Empty(Errors(g));
        Assert.Equal("no room", Assert.Single(g.Warnings).Message);
    }

    [Fact]
    public void A_try_that_builds_skips_its_catch_and_a_finally_is_always_built()
    {
        AudioGraph g = Graph("    try {\n      emit audio |> gain(level)\n    } catch (e) {\n      warn \"never\"\n    } finally {\n      warn \"always\"\n    }");
        Assert.Empty(Errors(g));
        Assert.Equal("always", Assert.Single(g.Warnings).Message);
    }

    [Fact]
    public void A_text_that_is_wrong_is_not_caught_and_only_Error_can_be_caught()
    {
        Assert.Contains(Graph("    try {\n      for i in 0..3 {\n      }\n    } catch (e) {\n      warn \"x\"\n    }\n    emit audio").Issues, i => i.Code == "AMP2231");
        Assert.Contains(Graph("    try {\n      fail \"x\"\n    } catch (Timeout e) {\n      warn \"x\"\n    }\n    emit audio").Issues, i => i.Code == "AMP2231" && i.Message.Contains("Timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void In_a_song_a_caught_error_is_no_diagnostic_and_a_fail_is_one_of_the_instance()
    {
        const string header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";
        const string song = "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\nkoda.fx.test space {\n  from lead.audio -> audio\n}\n";
        string caught = header + Process("    let cfg = {size: room}\n    try {\n      fail \"no\"\n    } catch (e) {\n      emit audio |> reverb(size=cfg.size)\n    }") + "\n" + song;
        Assert.DoesNotContain(Parser.Parse(caught, Corpus.Spec).Diagnostics, d => d.Severity == Severity.Error);
        Assert.DoesNotContain(ComponentExpander.Expand(caught, Corpus.Spec).Diagnostics, d => d.Severity == Severity.Error);
        string failing = header + Process("    fail \"no room\"\n    emit audio") + "\n" + song;
        Assert.Contains(ComponentExpander.Expand(failing, Corpus.Spec).Diagnostics, d => d.Code == "AMP6002" && d.Severity == Severity.Error && d.Message.Contains("no room", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_in_a_nested_try_is_caught_by_the_inner_one()
    {
        AudioGraph g = Graph("    try {\n      try {\n        fail \"inner\"\n      } catch (e) {\n        warn \"inner caught\"\n      }\n      warn \"after\"\n    } catch (e) {\n      warn \"outer caught\"\n    }\n    emit audio");
        Assert.Empty(Errors(g));
        Assert.Equal(["inner caught", "after"], g.Warnings.Select(w => w.Message));
    }
}
