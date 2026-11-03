# The sequencer block

> Status (2026-10-08): built (plan fl-block). An **advanced** block: the node editor and the source stay the main ways to work.

A sequencer block is a small studio in one block: **channels** (the sounds), **patterns** (what each channel plays), a **playlist** (which patterns play when) and **lanes** (extra outputs that carry the channels you pick). Everything is text, in a project file sequences/<name>.seq that the block names with file.

```
sequencer sq at 1 for 8 {
  file sequences/groove.seq
  lanes lane1=kick+hat lane2=bass lane3=lead
}
```

## The file
```
sequencer 1
channel kick drum sample=samples/kit/kick.wav vol=-2 color=#e8505b
channel bass bass vol=-6
channel lead notes wave=triangle set=samples/felt shift=0
channel pad loop file=samples/loops/pad.wav bars=2
pattern verse bars=2 color=#4a90d9
  kick x...x...x...x...|x...x...x..xx...
  bass 1:0:1:45:100 2:0:2:43:100
  lead 1:0:1:69:96 1:1:1:72:96
  pad on
playlist
  clip verse track=1 at=1 for=4
  section Verse at=1 for=4 color=#4a90d9
```
- **Channel types:** drum (a WAV file and steps), loop (an audio file that starts every bars bars), notes, bass and piano (the compact notes of the piano roll, played by a built-in tone, wave=square|triangle|saw, or by a sample set, set=samples/folder; shift moves the pitch in semitones), **guitar** (a strum row over chords, played from a pitched sample set: set=samples/gtr voicing=open|triad|power stagger=22) and **gm** (notes played by a General MIDI SoundFont preset: file=pack:musescore-general or a .sf2 of the project, program=0..127, bank). Common settings: vol (dB), pan (-32 to 32), mute=on, solo=on, color.
- **Guitar row:** the strum (16 steps a bar, D d U u x ., bars separated by |), then the word chords and one chord name for each bar: `D...D.u.D.u.D.u. chords Am F`. Chord names: C, Am, F#m, Bb, G7, Cmaj7 (minor when the name has m, otherwise major).
- **Rows of a pattern:** a drum row has 16 steps a bar (x hit, X accent, o ghost, 1 to 9 velocity, . rest), bars separated by |; a shorter row repeats. A bar written with 12 characters is a bar of eighth triplets, with 24 sixteenth triplets and with 32 thirty-second notes (so `x..x..x..x..` is a triplet figure); a bar may use a different grid than the next. A notes row is the text of the piano roll (bar:beat:length:pitch:velocity, the bar inside the pattern). A loop plays in a pattern that has a row for it (pad on).
- **Length:** a sequencer that is placed (`at 1 for 4`) is as long as its placement; one that is not placed is as long as its playlist (the end of the last clip), not 8 bars.
- **Playlist:** a clip puts a pattern at a bar for a number of bars; when the clip is longer than the pattern the pattern repeats. A section is a named band and a marker (`marker Drop at=9`) a named flag: neither changes the sound. A **loop** (`loop at=1 for=4`) makes the bars from its start on play the loop bars again and again, to the end of the block.
- **Variations:** `pattern verse-b from=verse` starts with the rows and the length of verse; its own rows replace the row of the same channel, and a row that is just `-` silences that channel. A variation starts from a plain pattern (one level); in the workspace the Variation button makes one and an inherited row is drawn lighter.
- Lines starting with # are comments: they stay above the line that follows them (and at the very top and end of the file) when the editor writes the file, and go away with that line when you delete it; blank lines are not kept. Mistakes are AMP5380 with the line.

## Lanes
The block has the output mix (every channel) and lane1 to lane8. lanes lane1=kick+hat lane2=bass lane3=all says which channels each lane carries (channels joined by +, all for every channel). Only lanes that name channels come out; connecting a lane that carries nothing stops the block that reads it (AMP5251) instead of silently giving the mix. Change what a lane carries by changing one line. A lane can be rendered to a file on its own.

## The workspace
Select the block and press **Open the sequencer** (or use the block menu). The **channel rack** lists the channels with colour, type, mute, solo, level, pan and the sample, loop or tone; the **pattern tabs** hold the patterns (New, Duplicate, Rename, Delete, bars, colour) with a step grid for each drum channel, a **Piano roll** for notes channels and a switch for loops; the **playlist** has tracks with clips (click an empty spot to put the selected pattern there, drag to move, drag the right edge to resize, Delete removes) and section bands; the **lanes** strip has a chip per channel for each lane and Render to file per lane. **Apply and back** writes the file; **Undo and back** leaves it as it was; the save control and Ctrl+S work as everywhere. Recording into a channel is the piano roll's MIDI in.

## Playing in the workspace
The bar has a transport: **Play** (builds first when the file changed), **Stop**, **Repeat** (keep looping) and **Live**. Drag over the ruler of the playlist to choose the bars that repeat (a shaded band; the ✕ next to the label clears it), click a bar to start from there (also while it plays), press **⟳** on a section band to repeat that section, or **⟳ Play pattern** next to the pattern tabs to repeat the first place of that pattern. The playhead runs over the playlist. The loop plays through an audio element (`web/app/loopplayer.js`) and jumps back within a few milliseconds. With **Live** on, a change made while it plays is built in the background after a short pause and the new take replaces the old one exactly when the loop comes round again, never in the middle of a bar. This is render-and-swap, not synthesis in the browser. Closing with Undo and back puts the file back as it was before the live builds wrote it.

## Keeping the sequence in the song
The sequence can live in the song itself instead of a file: a `sequence { ... }` part of the block holds the text of a sequence file (every line indented two more than `sequence {`; the closing brace stands at the indent of the opening line):

```
sequencer band at 1 for 8 {
  lanes lane1=kick
  sequence {
    sequencer 1
    channel kick drum sample=samples/kick.wav
    pattern verse bars=2
      kick x...x...x...x...|x...x...x...x...
    playlist
      clip verse track=1 at=1 for=8
  }
}
```

It is used instead of `file`. In the sequencer workspace **Keep in the song** moves the sequence of the file into the block (the file stays where it is, the block just stops using it) and **Extract to a file…** writes it to a new `.ampleseq` file of the project and lets the block use that file again. The workspace opens and writes a sequence in the song the same way as a file. New sequence files get the extension `.ampleseq`; a `.seq` file of before still works.

## Not built yet
Automation clips on the playlist, time-stretched loops (a loop plays at its own speed), playback markers that start playing at a point (the loop range is built).
