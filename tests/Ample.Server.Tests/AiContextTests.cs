using System.Text.Json.Nodes;

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

    [Theory]
    [InlineData("voice")]
    [InlineData("clone a voice")]
    [InlineData("text to speech")]
    [InlineData("xtts")]
    public void The_voice_howto_tells_Koda_never_to_choose_or_suggest_XTTS_on_its_own(string query)
    {
        string text = AiContext.HowTo(query).ToJsonString();

        Assert.Contains("NEVER choose", text);
        Assert.Contains("non-commercial", text);
        Assert.Contains("BY NAME", text);
        Assert.Contains("chatterbox", text);
    }

    private static string HowToText(string query) =>
        string.Join('\n', AiContext.HowTo(query)["howtos"]!.AsArray().Select(h => h!["about"]!.GetValue<string>() + ": " + h["howto"]!.GetValue<string>()));

    // Koda said "shift only works with audio, AMPLE has no MIDI note delay" (owner report, 2026-11-17); docs/language/12-note-pipelines.md and
    // 14-helpers-types-and-dynamic.md have shift(length) on Notes and the canonVoice helper. The how-tos and the skill text must say so.
    [Theory]
    [InlineData("start the notes later")]
    [InlineData("melody later")]
    [InlineData("shift notes")]
    [InlineData("canon")]
    [InlineData("midi delay")]
    [InlineData("delay the melody")]
    public void Koda_knows_shift_as_a_notes_operator_and_not_only_as_the_audio_shift_block(string query)
    {
        JsonObject found = AiContext.HowTo(query);
        string text = HowToText(query);

        Assert.True(found["found"]!.GetValue<bool>(), query);
        Assert.Contains("shift(3beats)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("only works with audio", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no MIDI", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_canon_howto_keeps_moving_transposing_and_combining_apart_and_proposes_no_midi_delay_block()
    {
        string text = HowToText("canon");

        Assert.Contains("shift(2bars)", text, StringComparison.Ordinal);
        Assert.Contains("transpose(+7)", text, StringComparison.Ordinal);
        Assert.Contains("COMBINE", text, StringComparison.Ordinal);
        Assert.Contains("helper canonVoice(Notes source, Number interval, Duration delay) -> Notes", text, StringComparison.Ordinal);
        Assert.Contains("do not propose a new MIDI delay block", text, StringComparison.Ordinal);
        Assert.Contains("a pipeline reads the notes the clip plays", text, StringComparison.Ordinal);   // a melody or chords clip is a source too (2026-11-17)
    }

    [Fact]
    public void The_koda_profile_moves_notes_with_the_notes_operator_and_never_with_the_audio_shift_block()
    {
        // Koda still answered "the shift block is only for audio" in a quick edit (owner, 2026-11-17): the profile Koda always reads says it itself
        Assert.Contains("never a `shift` block, that block moves AUDIO", AiService.KodaProfile, StringComparison.Ordinal);
        Assert.Contains("`notes theme.notes |> shift(3beats)`", AiService.KodaProfile, StringComparison.Ordinal);
        Assert.Contains("Do not propose a MIDI delay block", AiService.KodaProfile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skill_text_moves_notes_with_the_notes_operator_and_audio_with_the_shift_block()
    {
        Assert.Contains("MIDI notes need no block: the Notes operator `shift(length)`", AiService.SkillText, StringComparison.Ordinal);
        Assert.Contains("For audio use a `shift` block", AiService.SkillText, StringComparison.Ordinal);
    }
}
