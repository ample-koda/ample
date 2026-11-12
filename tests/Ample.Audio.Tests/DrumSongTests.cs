using Ample.Audio.Engine;
using Ample.Core.Abstractions;

namespace Ample.Core.Tests;

[Trait("Category", "Patterns")]
public sealed class DrumSongTests
{
    private static readonly string[] BuiltIn = ["beat-A", "beat-B"];

    [Fact]
    public void Named_patterns_and_a_song_are_read_and_expanded_one_name_per_bar()
    {
        DrumSong song = DrumSong.Parse("verse=kick:x... snare:..x. / fill=kick:xxxx", "verse*2 fill pattern beat-B*2", BuiltIn);
        Assert.Equal(["verse", "fill"], song.Patterns.Keys);
        Assert.Equal("kick:x... snare:..x.", song.Patterns["verse"]);
        Assert.True(song.HasSong);
        Assert.Equal(["verse", "verse", "fill", "pattern", "beat-B", "beat-B"], song.Expand());
    }

    [Fact]
    public void The_sequence_repeats_and_a_block_without_a_song_plays_the_plain_pattern()
    {
        DrumSong song = DrumSong.Parse(string.Empty, "beat-A*2 beat-B", BuiltIn);
        IReadOnlyList<string> sequence = song.Expand();
        Assert.Equal(["beat-A", "beat-A", "beat-B", "beat-A", "beat-A", "beat-B"], Enumerable.Range(0, 6).Select(i => DrumSong.NameAtBar(sequence, i)));
        DrumSong none = DrumSong.Parse(string.Empty, string.Empty, BuiltIn);
        Assert.False(none.HasSong);
        Assert.Equal(DrumSong.MainName, DrumSong.NameAtBar(none.Expand(), 7));
    }

    [Theory]
    [InlineData("verse=", "")]
    [InlineData("=kick:x...", "")]
    [InlineData("a b=kick:x...", "")]
    [InlineData("verse=kick:x... / verse=kick:.x..", "")]
    [InlineData("pattern=kick:x...", "")]
    [InlineData("beat-A=kick:x...", "")]
    [InlineData("verse=kick:x...", "chorus")]
    [InlineData("verse=kick:x...", "verse*0")]
    [InlineData("verse=kick:x...", "verse*1000")]
    [InlineData("verse=kick:x...", "verse*x")]
    public void A_wrong_name_repeat_or_reference_is_AMP5351(string patterns, string song) =>
        Assert.Equal("AMP5351", Assert.Throws<AmpleException>(() => DrumSong.Parse(patterns, song, BuiltIn)).DiagnosticCode);

    [Fact]
    public void The_message_names_the_patterns_that_exist()
    {
        AmpleException e = Assert.Throws<AmpleException>(() => DrumSong.Parse("verse=kick:x...", "chorus", BuiltIn));
        Assert.Contains("verse", e.Message, StringComparison.Ordinal);
        Assert.Contains("beat-A", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_around_the_parameters_are_ignored()
    {
        DrumSong song = DrumSong.Parse("\"verse=kick:x...\"", "\"verse*2\"", BuiltIn);
        Assert.Equal(["verse", "verse"], song.Expand());
    }
}
