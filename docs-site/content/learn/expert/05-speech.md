---
title: Speech
lead: A voice made from text, on your own computer, the same every time.
---
## What the speech block is

The `speech` block turns text into a voice. It needs no account, no download and no internet connection: the voice is made inside AMPLE, so the same song with the same seed gives the same sound every time. It speaks American English.

There are two voices. `humanico` is the default and the one to use. `retro` is an older, thinner voice that stays available. Pick one with `engine "retro"`.

{{amplet Speak a sentence}}
composition "hello speech" { ample 1  tempo 120  master-seed 7 }

speech voice at 1 for 1 {
  text "I am a robot."
}

master master {
  from voice.audio -> audio
}
{{/amplet}}

A new speech block in the Studio starts with this sentence, so it builds at once.

## How long the block is

A block that you place with `at … for …` lasts exactly that long. The speech starts at the beginning of the placement, and what does not fit is cut off.

A block without a placement lasts as long as its speech, rounded up to whole bars. The sentence above is one bar at 120 bpm, not the eight bars that a block without a length gets elsewhere. This holds for text mode; a block in words mode follows the notes it is connected to.

## Text or phonemes

Give the block `text` or `phonemes`, never both, and never an empty one. A line `text ""` is an error ("must not be empty"): leave the line out instead. Text with no words in it, such as `"..."`, is silence.

Numbers and abbreviations are spoken as words. To control the pronunciation exactly, write phonemes: the CMU symbols, separated by spaces, with a stress digit on the vowels (0 none, 1 main, 2 second).

{{amplet Exact pronunciation with phonemes}}
composition "phonemes" { ample 1  tempo 120  master-seed 7 }

speech voice at 1 for 1 {
  phonemes "HH AH0 L OW1  W ER1 L D"
}

master master {
  from voice.audio -> audio
}
{{/amplet}}

## Rate, pitch and level

`rate` is the speaking speed (1 is the normal speed, from 0.1 to 2). `pitch_hz` is the pitch of the voice in text mode (45 to 700 Hz, default 105). `gain` adds or removes decibels (-60 to +12). One voice at gain 0 peaks around -13 dBFS, so it leaves room; if a gain pushes the sound over the limit, the take reports it (`gain-clipped`) instead of hiding it.

## One word per note

In `words` mode the voice does not read a sentence: it says **one word per note**. Connect a Notes block (a MIDI clip, a melody) to the `notes` input. The first note says the first word, the second note the second word, and so on; the pitch of each note sets the pitch of its word. Chords are played in order of pitch; extra notes are silent, and a text with fewer words than the song has notes stops after its last word.

{{amplet A word on every note}}
composition "sing" { ample 1  tempo 100  master-seed 7 }

midi-clip tune at 1 for 2 {
  notes "1:0:0.5:60:100 1:1:0.5:62:100 1:2:0.5:64:100 1:3:0.5:67:100"
}

speech voice at 1 for 2 {
  mode "words"
  text "Get down get funky"
  duration_policy "fit"
  gain 12
  from tune.notes -> notes
}

master master {
  from voice.audio -> audio
}
{{/amplet}}

Words mode shares its level between up to four voices, so each word is quieter than in text mode; the example adds `gain 12`. The `duration_policy` decides how long a word lasts: `natural` lets the word finish (it can run past its note), `gate` cuts it at the end of the note, and `fit` speeds the word up, at most to `fit_max_rate` times, and cuts what still does not fit. At most four words can sound at the same time.

To make the voice sing, put it through an autotune set to a key and add some effects, as the Funk It Up demo does (a General MIDI band, sung words in E dorian, 90 seconds): open it in Remix, or find it in the examples folder as `26-funky-speech`.

## In the Studio

Add a **Speech** block, open its window (the arrow on the block) and type the text in the text field. The box "Write phonemes instead of text" switches the field to phonemes; the unused line is removed from the song. Mode shows the controls that belong to it: the pitch in text mode, and the duration policy and the reference note in words mode. Build and Play work as for every block; the sound comes from a build, not from the browser, so there is no instant preview of the voice.

## In Remix

Remix builds speech on the server and gives the limits next to the message when a song is over one. The limits are 8 speech blocks per song, 60 seconds and 4096 characters per block, 120 seconds and 16384 characters per song, and a limit on the memory that the blocks use at the same time. The text of a shared song is part of what is shared. Two demos start from the Demos button: "Hello speech" and "Funk It Up".

## When something goes wrong

| Message | What it means |
|---|---|
| The speech text must not be empty | A `text` or `phonemes` line has no text. Write the words or remove the line. |
| Provide either text or phonemes, not both | The block has both. Keep one. |
| Give text or phonemes to speak | The block has neither. |
| Words mode needs at least one positive Notes event | The block is in words mode with text but no notes are connected. |
| Words mode supports at most 4 overlapping Notes voices | More than four words would sound at once: shorten the notes or use `gate`. |
| A speech limit of Remix is exceeded (AMPL924) | A Remix limit above is passed. The message names it. |

{{blocks speech}}
