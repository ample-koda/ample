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

    [Fact]
    public void Renaming_a_block_renames_it_in_the_view_block_too()
    {
        string song = Head + "\nview {\n  group g1 {\n    member kick\n    member snare\n  }\n  size kick 1.5\n  pin kick\n}\n";
        string renamed = Edits.Apply(song, Edits.RenameBlock(Parse(song), "kick", "big kick").Edits);
        Assert.Contains("    member \"big kick\"\n", renamed, StringComparison.Ordinal);
        Assert.Contains("  size \"big kick\" 1.5\n", renamed, StringComparison.Ordinal);
        Assert.Contains("  pin \"big kick\"\n", renamed, StringComparison.Ordinal);
        Assert.Contains("    member snare\n", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(renamed).Diagnostics, x => x.Code is "AMP1114" or "AMP1101");
    }

    [Fact]
    public void Sections_lines_and_order_are_accepted_in_the_view_block()
    {
        string song = Head + "\nview {\n  section \"Drop\" at 33 for 32\n  section Intro at 1 for 8\n  line kick \"Kick stack\"\n  order kick snare\n}\n";
        Assert.DoesNotContain(Parse(song).Diagnostics, x => x.Code is "AMP1114" or "AMP1121" or "AMP1122" or "AMP1123");
    }

    [Theory]
    [InlineData("section \"A\" at 0 for 8", "AMP1121")]
    [InlineData("section \"A\" at 1 for 0", "AMP1121")]
    [InlineData("section \"A\" at 250 for 8", "AMP1121")]
    [InlineData("section \"A\" at 1 for 8\n  section \"B\" at 8 for 4", "AMP1122")]
    [InlineData("section \"A\" at 1 for 8\n  section \"A\" at 9 for 4", "AMP1123")]
    [InlineData("section \"A\" at one for 8", "AMP1114")]
    [InlineData("line kick", "AMP1114")]
    [InlineData("order", "AMP1114")]
    public void A_bad_section_line_or_order_is_reported(string line, string code)
    {
        Assert.Contains(Parse(Head + "\nview {\n  " + line + "\n}\n").Diagnostics, x => x.Code == code);
    }

    [Fact]
    public void Sections_next_to_each_other_do_not_overlap()
    {
        Assert.DoesNotContain(Parse(Head + "\nview {\n  section \"A\" at 1 for 8\n  section \"B\" at 9 for 8\n}\n").Diagnostics, x => x.Code == "AMP1122");
    }

    [Fact]
    public void Renaming_a_block_renames_it_in_line_and_order_lines()
    {
        string song = Head + "\nview {\n  line kick \"Kick stack\"\n  order snare kick lead\n}\n";
        string renamed = Edits.Apply(song, Edits.RenameBlock(Parse(song), "kick", "big kick").Edits);
        Assert.Contains("  line \"big kick\" \"Kick stack\"\n", renamed, StringComparison.Ordinal);
        Assert.Contains("  order snare \"big kick\" lead\n", renamed, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_in_the_view_block_offers_its_words_and_the_block_names()
    {
        string song = Head + "\nview {\n  \n  line \n  group g {\n    \n    member k\n  }\n}\n";
        AmpleDocument doc = Parse(song);
        IReadOnlyList<CompletionItem> At(int line, int col) => Queries.CompletionAt(doc, Corpus.Spec, line, col);
        Assert.True(At(7, 3).Any(c => c.Label == "section"), "at 7:3 wants section but has " + string.Join(",", At(7, 3).Select(c => c.Label)));
        Assert.True(At(7, 3).Any(c => c.Label == "order"), "at 7:3 wants order but has " + string.Join(",", At(7, 3).Select(c => c.Label)));
        Assert.True(At(8, 8).Any(c => c.Label == "kick"), "at 8:8 wants kick but has " + string.Join(",", At(8, 8).Select(c => c.Label)));
        Assert.True(At(11, 14).Any(c => c.Label == "kick"), "at 11:14 wants kick but has " + string.Join(",", At(11, 14).Select(c => c.Label)));
        Assert.True(At(10, 5).Any(c => c.Label == "member"), "at 10:5 wants member but has " + string.Join(",", At(10, 5).Select(c => c.Label)));
        Assert.DoesNotContain(At(10, 5), c => c.Label == "section");
    }
}
