namespace Ample.Language.Tests;

public sealed class ViewBlockTests
{
    private const string Head = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\ndrums kick at 1 for 4 {\n}\nlayout kick 10 20\n";

    private const string Old = Head + "\n# view: the groups, block sizes and pinned blocks of the canvas (kept by AMPLE; the song does not depend on them)\n"
        + "# view group {\"id\":\"g1\",\"title\":\"Group \\\"1\\\"\",\"members\":[\"kick\",\"b c\"],\"x\":5,\"y\":6,\"labels\":{\"a:0\":\"Beat\"}}\n"
        + "# view size my block 1.25\n# view pin kick\n# view pin a b\n# view group {broken\n";

    private const string Block = "view {\n  group g1 {\n    title \"Group \\\"1\\\"\"\n    member kick\n    member \"b c\"\n    x 5\n    y 6\n    label \"a:0\" \"Beat\"\n  }\n  size \"my block\" 1.25\n  pin \"a b\"\n  pin kick\n}\n# not read: view group {broken";

    // Applies a fix the way the editors do (1-based lines and columns; ASCII text here).
    private static string Apply(string text, Fix fix)
    {
        string[] lines = text.Split('\n');
        int endLine = fix.EndLine > fix.Line ? fix.EndLine : fix.Line;
        string joined = string.Join('\n', lines);
        int From(int line, int col) => lines.Take(line - 1).Sum(l => l.Length + 1) + col - 1;
        return joined[..From(fix.Line, fix.Col)] + fix.Text + joined[From(endLine, fix.EndCol)..];
    }

    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec, ParseOptions.Editor);

    [Fact]
    public void The_old_comment_lines_get_one_hint_whose_fix_writes_the_block_and_leaves_nothing_behind()
    {
        Diagnostic d = Assert.Single(Parse(Old).Diagnostics, x => x.Code == "AMP1113");
        Assert.Equal(Severity.Info, d.Severity);
        Assert.Equal(6, d.Line);
        string upgraded = Apply(Old, d.Fix!);
        Assert.Equal(Head + "\n" + Block, upgraded.TrimEnd());
        Assert.DoesNotContain(Parse(upgraded).Diagnostics, x => x.Code is "AMP1113" or "AMP1114" or "AMP1101");
    }

    [Fact]
    public void A_line_that_does_not_belong_in_the_view_block_is_an_error_and_a_missing_brace_is_reported()
    {
        Assert.Contains(Parse(Head + "view {\n  colour red\n}\n").Diagnostics, x => x.Code == "AMP1114" && x.Line == 6);
        Assert.Contains(Parse(Head + "view {\n  pin kick\n").Diagnostics, x => x.Code == "AMP1101");
        Assert.DoesNotContain(Parse(Head + Block[..Block.IndexOf("\n# not read", StringComparison.Ordinal)] + "\n").Diagnostics, x => x.Code.StartsWith("AMP11", StringComparison.Ordinal));
    }

    [Fact]
    public void The_formatter_indents_the_view_block_and_a_block_added_to_the_song_goes_before_it()
    {
        string messy = Head + "view {\ngroup g1 {\nmember kick\n}\npin kick\n}\n";
        Assert.Equal(Head + "view {\n  group g1 {\n    member kick\n  }\n  pin kick\n}\n", Formatter.Format(messy, Corpus.Spec));
        EditResult added = Edits.AddBlock(Parse(messy), Corpus.Spec, "drums", "snare");
        Assert.True(added.Ok);
        Assert.True(Edits.Apply(messy, added.Edits).IndexOf("drums snare", StringComparison.Ordinal) < messy.IndexOf("view {", StringComparison.Ordinal) + 40);
        Assert.DoesNotContain(Parse(Edits.Apply(messy, added.Edits)).Diagnostics, x => x.Severity == Severity.Error);
    }
}
