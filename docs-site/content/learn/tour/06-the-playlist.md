---
title: The Playlist
order: 60
lead: The same song as lines with clips, chains and cables.
---
The Playlist is a second way to look at a song in the AMPLE Studio. A **line** is everything you hear as one sound: an instrument, the effects after it and the bus it plays into. Open a line to see its chain. It is switched on in the **?** menu at the top right of the Studio (**Playlist view (beta)**).

## What you can do

- Move, resize, draw, split, repeat and delete clips, or copy a clip as a new block.
- Put an effect into a line, take it out, move it a step or switch it off.
- Add, remove, rename, reorder and group lines.
- Route a line to another bus, add a send, or let a line duck another with a sidechain.
- Name a stretch of bars as a section (Intro, Verse, Drop).
- See the curve of every automated parameter under its line and open the curve editor with a click.
- See the patterns of a drums song and the lanes of a sequencer under their line, and move, resize, repeat and remove them there. Open a module from its line, and read a small graph of the picked line.
- Find your way in a long song with the overview strip, and see what the last gesture wrote into the song.
- Mute and solo lines to hear a quick preview.

## A song to try

This small song is made to be read in the Playlist. It has three lines (the drums, the bass and the keys), one note source that feeds two instruments, a sidechain from the drums into the bass, a curve that opens the filter of the keys, a drums song with a fill, and three sections at the end of the file.

{{amplet A small song to read in the Playlist}}
# A small song to read in the Playlist: three lines, a sidechain, a curve and some sections.
composition "Playlist demo" { ample 1  tempo 120  master-seed 20261105 }

# One note source feeds two instruments, so the Playlist shows it on both lines and marks it as shared.
midi-clip chords {
  chords Am F C G
  bars 8
}

# The drums play the sections of their song in order: verse three times, then a fill, and again.
drums beat at 1 for 16 {
  patterns verse=x...x...x...x... / fill=xxxxxxxxxxxxxxxx
  song verse*3 fill
}

bass low at 1 for 16 {
  from chords.notes -> notes
}

piano keys at 5 for 12 {
  from chords.notes -> notes
}

# A sidechain: the drums duck the bass. The Playlist draws it as a dashed link between the two lines.
compressor low-duck {
  threshold -24 dB
  ratio 4
  from low.audio -> audio
  from beat.audio -> key
}

# A curve: the cutoff opens from bar 1 to bar 9. The Playlist shows it as its own row under the keys.
filter keys-sweep {
  type lowpass
  cutoff auto(1:800, 9:4000)
  from keys.audio -> audio
}

reverb keys-room {
  mix 25 %
  from keys-sweep.audio -> audio
}

bus beat-bus {
  from beat.audio -> audio1
}

bus low-bus {
  from low-duck.audio -> audio1
}

bus keys-bus {
  from keys-room.audio -> audio1
}

mixer mix {
  inputs 4
  from beat-bus.audio -> audio1
  from low-bus.audio -> audio2
  from keys-bus.audio -> audio3
}

master master {
  from mix.audio -> audio
}

view {
  section "Intro" at 1 for 4
  section "Groove" at 5 for 8
  section "Break" at 13 for 4
  line keys-bus "Keys with a sweep"
}
{{/amplet}}

Press **Remix** to open the song in AMPLE Remix, build it and change it. Remix has no Playlist, but it plays the song, and the `view` block at the end (the sections and the name of a line) does nothing to the sound.

To see it in the Playlist, copy the song into a new song in the Studio, turn on **Playlist view (beta)** in the **?** menu and open the **Playlist** tab. Open all the lines and you will find:

- the drums with a row of their patterns (verse, verse, verse, fill) that you can drag longer or shorter;
- the bass with the notes block marked as shared, and a dashed link from the drums for the sidechain;
- the keys, named in the song as "Keys with a sweep", with the curve of the cutoff as a row of its own (click it to edit the curve);
- the sections Intro, Groove and Break above the bars.

Try to put a reverb after the bass, drag the keys clip to a later bar, or route a line to another bus, and then look at the Source tab to see the text that each of them wrote.

Every change is the same text you could write in the song yourself, and one gesture is one undo step. A name, a section and the order of the lines are kept in a `view` block at the end of the song; they never change how it sounds.

The Playlist is in the Studio. Lite and Remix open the same songs but do not have it.
