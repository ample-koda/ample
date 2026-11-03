using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public class ChordTonesTests
{
    [Fact]
    public void Plain_triads_sound_as_before()
    {
        Assert.Equal(BuiltInRenderers.Triad("Am"), BuiltInRenderers.ChordTones("Am"));
        Assert.Equal(BuiltInRenderers.Triad("F"), BuiltInRenderers.ChordTones("F"));
        Assert.Equal(BuiltInRenderers.Triad("Bb"), BuiltInRenderers.ChordTones("Bb"));
        Assert.Equal(BuiltInRenderers.Triad("F#m"), BuiltInRenderers.ChordTones("F#m"));
    }

    [Fact]
    public void Richer_qualities_are_read()
    {
        Assert.Equal([48, 52, 55, 59], BuiltInRenderers.ChordTones("Cmaj7"));
        Assert.Equal([55, 59, 62, 65], BuiltInRenderers.ChordTones("G7"));
        Assert.Equal([50, 55, 57], BuiltInRenderers.ChordTones("Dsus4"));
    }

    [Fact]
    public void A_slash_chord_puts_its_bass_note_lowest()
    {
        Assert.Equal([52, 55, 60], BuiltInRenderers.ChordTones("C/E"));   // first inversion
        Assert.Equal([55, 60, 64], BuiltInRenderers.ChordTones("C/G"));   // second inversion
        Assert.Equal([38, 48, 52, 55], BuiltInRenderers.ChordTones("C/D"));   // not a tone of the chord: an extra bass note below
    }
}
