namespace Ample.Server.Tests;

/// <summary>The song context that Koda's task reads (ample_song).</summary>
public sealed class AiContextTests
{
    [Fact]
    public void A_block_from_a_component_whose_line_lies_beyond_the_song_has_no_comment_and_does_not_throw()
    {
        string[] lines = ["composition \"s\" { ample 1 }", "# the riff", "midi-clip riff at 1 for 1 {", "}"];

        // a block of an expanded component can carry a line number of the component's own text (here 40, the song has 4 lines)
        Assert.Empty(AiContext.CommentAbove(lines, 41));
        Assert.Empty(AiContext.CommentAbove(lines, 5));
    }

    [Fact]
    public void The_comment_lines_right_above_a_block_are_its_comment()
    {
        string[] lines = ["composition \"s\" { ample 1 }", "# first", "# second", "midi-clip riff at 1 for 1 {", "}"];

        Assert.Equal(["first", "second"], AiContext.CommentAbove(lines, 3));   // the block header is line 3 (counted from 0)
        Assert.Empty(AiContext.CommentAbove(lines, 1));
        Assert.Empty(AiContext.CommentAbove(lines, 0));
    }
}
