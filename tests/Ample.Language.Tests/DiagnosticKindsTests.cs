using System.Text.Json;

namespace Ample.Language.Tests;

/// <summary>Which errors make the Blocks view unusable and which only mark one block (plan/editor-tabs.md, part 4).</summary>
public sealed class DiagnosticKindsTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    [Fact]
    public void An_unknown_parameter_marks_its_block_and_does_not_break_the_graph()
    {
        string song = Header + "synth lead at 1 for 4 {\n  wave saw\n  nosuchparameter 3\n}\n\nmaster out {\n  from lead.audio -> audio\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);

        Diagnostic error = Assert.Single(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("lead", DiagnosticKinds.BlockOf(doc, error));
        Assert.False(DiagnosticKinds.BreaksGraph(error.Code));
        Assert.False(DiagnosticKinds.GraphBroken(doc));
        Assert.Equal(2, doc.Blocks.Count);   // the graph is there
    }

    [Fact]
    public void A_block_that_is_not_closed_breaks_the_graph()
    {
        string song = Header + "synth lead at 1 for 4 {\n  wave saw\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);

        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP1101");
        Assert.True(DiagnosticKinds.GraphBroken(doc));
    }

    [Fact]
    public void A_missing_composition_line_breaks_the_graph_and_belongs_to_no_block()
    {
        AmpleDocument doc = Parser.Parse("synth lead at 1 for 4 {\n  wave saw\n}\n", Corpus.Spec);

        Diagnostic missing = Assert.Single(doc.Diagnostics, d => d.Code == "AMP1001");
        Assert.True(DiagnosticKinds.GraphBroken(doc));
        Assert.Null(DiagnosticKinds.BlockOf(doc, missing));
    }

    [Fact]
    public void A_warning_never_breaks_the_graph_even_with_a_breaking_code()
    {
        AmpleDocument clean = Parser.Parse(Header + "synth lead at 1 for 4 {\n  wave saw\n}\n", Corpus.Spec);
        Assert.False(DiagnosticKinds.GraphBroken(clean));
    }

    [Fact]
    public void Every_graph_breaking_code_exists_in_the_spec_as_an_error()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "global.json"))) root = Path.GetDirectoryName(root)!;
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "spec", "diagnostics.json")));
        Dictionary<string, string> severities = spec.RootElement.GetProperty("diagnostics").EnumerateArray().ToDictionary(d => d.GetProperty("code").GetString()!, d => d.GetProperty("severity").GetString()!);

        foreach (string code in DiagnosticKinds.GraphBreakingCodes)
        {
            Assert.True(severities.TryGetValue(code, out string? severity), code + " is not in spec/diagnostics.json");
            Assert.Equal("error", severity);
        }
    }
}
