namespace Ample.Language.Tests;

public sealed class ExpandedMidiNotesTests
{
    [Fact]
    public void Expanded_notes_keep_exact_ticks_and_attached_comments_in_source()
    {
        const string compact = "18:22:72:121:96:5:0:60:2 18:94:26:72:121:5:0:60:2";
        Assert.True(ExpandedMidiNotes.TryExpand(compact, 480, out string expanded));
        expanded = expanded.Replace("  note {", "  # first note\n  note {", StringComparison.Ordinal);
        Assert.True(ExpandedMidiNotes.TryCompact(expanded, 480, out string rebuilt));
        Assert.Equal(compact, rebuilt);
        string source = "composition \"midi\" { ample 1  tempo 100 master-seed 1 }\nmidi-clip track {\n  ppq 480\n  " + expanded.Replace("\n", "\n  ", StringComparison.Ordinal) + "\n}\n";
        AmpleDocument doc = Parser.Parse(source, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(compact, Assert.Single(doc.Blocks).Parameters.Single(p => p.Name == "notes").Value);
        Assert.Contains("# first note", Formatter.Format(source, Corpus.Spec), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_expanded_notes_report_the_structured_notes_error_and_later_blocks_survive()
    {
        const string source = "composition \"midi\" { ample 1  tempo 100 master-seed 1 }\nmidi-clip track {\n  notes {\n    note {\n      bar 1\n      pitch 60\n    }\n  }\n}\nmixer mix {\n}\n";
        AmpleDocument doc = Parser.Parse(source, Corpus.Spec);
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2012");
        Assert.Contains(doc.Blocks, b => b.Name == "mix");
    }

    [Fact]
    public void Graph_parameter_edit_refuses_to_collapse_an_expanded_notes_body()
    {
        const string source = "composition \"midi\" { ample 1  tempo 100 master-seed 1 }\nmidi-clip track {\n  notes {\n    # attached\n    note {\n      bar 1\n      start 0\n      length 120\n      pitch 60\n      velocity 90\n    }\n  }\n}\n";
        AmpleDocument doc = Parser.Parse(source, Corpus.Spec);
        Assert.False(Edits.SetParameter(doc, "track", "notes", "1:0:120:60:90").Ok);
    }
}
