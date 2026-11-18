namespace Ample.Storage.Projects;

/// <summary>
/// The text of a new song: the composition line and a small starter song (a pad, a bass, a kick and a hat, through a mixer to the master) that plays at once and shows
/// what a song is made of. It uses built-in synths only, so it sounds in an empty project too. Remix has the same body (web/remix/lite-templates.mjs; a test keeps them equal).
/// </summary>
public static class StarterSong
{
    /// <summary>Gets what follows the composition line: comments, the clips, the synths, the mixer and the master.</summary>
    public const string Body = """

# A starter song: chords, a bass and a beat, through a mixer to the master.
# Build it to hear it (Ctrl+Enter), or press Listen to hear it while you edit.

# The chords, one a bar. Try Dm G Em Am, or write more bars.
midi-clip chordsClip at 1 for 4 {
  chords Am F C G
}

# The bass: the root of each chord, one note a beat (a2 is the A in the second octave).
midi-clip bassClip at 1 for 4 {
  melody "a2:1 a2:1 a2:1 a2:1 f2:1 f2:1 f2:1 f2:1 c3:1 c3:1 c3:1 c3:1 g2:1 g2:1 g2:1 g2:1"
}

# One bar of beat, as notes (bar:beat:length:pitch:velocity): a kick on every beat and a hat between them. It is repeated for four bars.
midi-clip kickBar at 1 for 1 {
  notes 1:0:0.3:36:120 1:1:0.3:36:100 1:2:0.3:36:110 1:3:0.3:36:100
}
midi-clip hatBar at 1 for 1 {
  notes 1:0.5:0.05:90:70 1:1.5:0.05:90:60 1:2.5:0.05:90:70 1:3.5:0.05:90:60
}
midi-clip kickPart at 1 for 4 {
  notes kickBar.notes |> repeat(4)
}
midi-clip hatPart at 1 for 4 {
  notes hatBar.notes |> repeat(4)
}

# The sounds: a pad, a bass, a kick and a hat. Change a wave or a gain and listen.
synth pad at 1 for 4 {
  wave saw
  attack 300 ms
  release 600 ms
  detune 10
  cutoff 2400 Hz
  gain -16 dB
  from chordsClip.notes -> notes
}
synth bass at 1 for 4 {
  wave triangle
  attack 5 ms
  decay 150 ms
  sustain 70
  release 80 ms
  gain -8 dB
  from bassClip.notes -> notes
}
synth kick at 1 for 4 {
  wave sine
  attack 1 ms
  decay 160 ms
  sustain 0
  release 40 ms
  gain -6 dB
  from kickPart.notes -> notes
}
synth hat at 1 for 4 {
  wave noise
  attack 1 ms
  decay 40 ms
  sustain 0
  release 20 ms
  cutoff 9000 Hz
  gain -22 dB
  from hatPart.notes -> notes
}

# The mixer sets how loud each one is (a gain in dB for every input), and the master is what you hear.
mixer console {
  inputs 4
  gains "0 0 0 0"
  from pad.audio -> audio1
  from bass.audio -> audio2
  from kick.audio -> audio3
  from hat.audio -> audio4
}

master out {
  from console.audio -> audio
}
""";

    /// <summary>Makes the text of a new song.</summary>
    /// <param name="name">The name on the composition line.</param>
    /// <param name="tempo">The tempo, already written as the file wants it (for example 100).</param>
    /// <param name="masterSeed">The master seed, already written.</param>
    /// <returns>The text of the song, with line feeds.</returns>
    public static string Text(string name, string tempo, string masterSeed) =>
        $"composition \"{name}\" {{ ample 1  tempo {tempo}  master-seed {masterSeed} }}\n" + Body.ReplaceLineEndings("\n") + "\n";
}
