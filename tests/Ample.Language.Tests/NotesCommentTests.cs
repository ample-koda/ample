namespace Ample.Language.Tests;

/// <summary>A comment after the notes of a clip is a comment: <c>notes 1:0:1:60:100 # first note</c> was an AMP2012 error.</summary>
public sealed class NotesCommentTests
{
    private static AmpleDocument Parse(string notesLine) =>
        Parser.Parse($"composition \"x\" {{ ample 1  tempo 100  master-seed 1 }}\nmidi-clip a at 1 for 2 {{\n  {notesLine}\n}}\n", Corpus.Spec);

    [Theory]
    [InlineData("notes 1:0:1:60:100 # first note")]
    [InlineData("notes 1:0:1:60:100 2:0:1:62:100   # two notes")]
    [InlineData("notes 1:0:1:60:100;pan=20 # panned")]
    public void A_trailing_comment_after_structured_notes_is_not_an_error(string line)
    {
        AmpleDocument doc = Parse(line);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "AMP2012");
        Parameter notes = doc.Blocks.Single().Parameters.Single(p => p.Name == "notes");
        Assert.DoesNotContain('#', notes.Value);
    }

    [Fact]
    public void Notes_without_a_comment_and_a_wrong_entry_are_unchanged()
    {
        Assert.DoesNotContain(Parse("notes 1:0:1:60:100 2:0:1:62:100").Diagnostics, d => d.Code == "AMP2012");
        Assert.Contains(Parse("notes 1:0:x:60:100 # a bad entry").Diagnostics, d => d.Severity == Severity.Error);
    }
}
