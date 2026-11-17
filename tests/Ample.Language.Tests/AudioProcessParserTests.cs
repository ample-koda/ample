namespace Ample.Language.Tests;

/// <summary>WP13 of plan/diagnostics/PLAN.md: the statements of an audio process are read by the shared parser (in its audio mode), so a statement may span lines in any shape; the graph it builds is the same as before.</summary>
public sealed class AudioProcessParserTests
{
    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string Process(string body, string outputs = "  output audio: Audio\n") => $"component koda.fx.test version 1.0.0 {{\n  input audio: Audio\n  property level: Gain = -3dB\n  property wide: Bool = false\n{outputs}  process {{\n{body}\n  }}\n}}\n";

    private static AudioGraph Graph(string component) => AudioGraphBuilder.Build(Only(component), Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);

    private static string Shape(AudioGraph g) => string.Join("; ", g.Nodes.Select(n => $"{n.Id}:{n.Template}({string.Join(",", n.Parameters.Select(p => p.Item1 + "=" + p.Item2))})<-{string.Join(",", n.Inputs.Select(i => i.Item1 + "=" + i.Item2))}"));

    [Fact]
    public void The_shared_parser_reads_every_statement_kind_and_an_emit_with_its_output()
    {
        List<NotePipeline.AudioStatement> read = NotePipeline.ReadAudioProcess("    let wet = audio |> reverb(size=room)\n    if (level) {\n      warn \"loud\"\n    } else {\n      log \"soft\"\n    }\n    emit wet -> audio\n")!;
        Assert.Equal(["let", "if", "observe", "else", "observe", "end", "emit"], read.Select(s => s.Kind));
        Assert.Equal("wet = audio |> reverb(size=room)", read[0].Text);
        Assert.Equal("wet -> audio", read[^1].Text);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], read.Select(s => s.BodyLine));
    }

    [Fact]
    public void A_nested_component_with_a_pinned_version_is_one_stage()
    {
        NotePipeline.AudioStatement emit = Assert.Single(NotePipeline.ReadAudioProcess("emit audio |> koda.fx.dreamEcho@1.0.0(feedback=20%) |> gain(-3dB)")!);
        Assert.Equal("audio |> koda.fx.dreamEcho@1.0.0(feedback=20%) |> gain(-3dB)", emit.Text);
    }

    [Fact]
    public void A_let_whose_value_starts_on_the_next_line_builds_the_same_graph_as_on_one_line()
    {
        AudioGraph split = Graph(Process("    let wet =\n      audio\n        |> gain(level)\n    emit wet"));
        AudioGraph one = Graph(Process("    let wet = audio |> gain(level)\n    emit wet"));
        Assert.Empty(split.Issues);
        Assert.Equal(Shape(one), Shape(split));
        Assert.Equal(one.Outputs, split.Outputs);
    }

    [Fact]
    public void A_statement_an_audio_process_does_not_have_is_reported_on_its_line()
    {
        AudioGraph g = Graph(Process("    for i in 0..3 {\n      log \"x\"\n    }\n    emit audio"));
        ComponentIssue issue = Assert.Single(g.Issues, i => i.Code == "AMP2231");
        Assert.Contains("is not a statement of an audio process", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_the_parser_cannot_read_is_still_reported_line_by_line()
    {
        Assert.Null(NotePipeline.ReadAudioProcess("banana 3\nemit audio"));
        Assert.Contains(Graph(Process("    banana 3\n    emit audio")).Issues, i => i.Code == "AMP2231" && i.Message.Contains("banana", StringComparison.Ordinal));
    }
}
