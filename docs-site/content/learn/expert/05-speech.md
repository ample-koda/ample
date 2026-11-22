---
title: Speech
lead: A voice made from text, on your own computer, the same every time.
---
## What the speech block is

The `speech` block turns text into a voice. It needs no account, no download and no internet connection: the voice is made inside AMPLE, so the same song with the same seed gives the same sound every time. It speaks American English.

There are three voices. `humanico` is the default and the one to start with. `retro` is an older, thinner voice that stays available. `flite` is a classic diphone voice that reads any English text. Pick one with `engine "retro"` or `engine "flite"`.

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

## The Flite voice

`flite` is a translation of Flite, the small speech synthesizer of Carnegie Mellon University, to C#, with its voice kal16 (a male American voice built from recordings of Alan W Black and Kevin Lenzo). It sounds closer to the text-to-speech of older systems than Humanico does, and it reads every word: numbers, dates, money and abbreviations are spoken, and a word it does not know is spelled out by letter-to-sound rules. A text of several sentences is spoken a sentence at a time. Like the other voices it needs no download and no internet, and the same song gives the same sound on every machine.

{{amplet Speak with the Flite voice}}
composition "flite speech" { ample 1  tempo 120  master-seed 7 }

speech narrator at 1 for 2 {
  engine "flite"
  text "Hello from AMPLE. Today is the 20th of November, 2026."
}

master master {
  from narrator.audio -> audio
}
{{/amplet}}

What you can set on a Flite block:

- `rate` is the speed (1 is normal) and `pitch_hz` the pitch of the voice (95 Hz when it is not written). `pitch_range` (0 to 200 Hz, default 11) is how far the pitch moves around it; 0 gives a flat voice.
- `phonemes` works as for the other voices: the same CMU symbols with stress digits. Flite speaks a phoneme string as one phrase without pauses, so it sounds flatter than text.
- `mode "words"` with a Notes block connected makes the voice sing: each note speaks the next word at one steady pitch from 50 to 480 Hz. A note outside that range is sung the same note an octave or more away. Flite cannot glide or add vibrato. It follows the same `duration_policy` as the other voices.
- `lead_trim "on"` removes the quiet that the voice puts before its first word (about a fifth of a second), so a line starts on the beat. `pause_scale` from 0 to 4 scales the pauses between words (0 removes them, 2 doubles them). `brightness` from 0 to 12 dB lifts the high end of the 16 kHz voice.

{{amplet A tighter Flite line}}
composition "flite tight" { ample 1  tempo 120  master-seed 7 }

speech narrator at 1 for 2 {
  engine "flite"
  text "Hello from AMPLE. Today is the 20th of November, 2026."
  lead_trim "on"
  pause_scale 0.6
}

master master {
  from narrator.audio -> audio
}
{{/amplet}}

Your own pronunciations and spelling (Flite only):

- `lexicon` gives words their own phones: `lexicon "ample = AE1 M P AX L; tomato = T AH0 M EY1 T OW0"`. An entry is `word = PHONEMES`, entries are separated by `;`. The phonemes are the CMU symbols of the `phonemes` parameter; every vowel carries a stress digit (0 no stress, 1 stress, 2 is read as 1 because the voice knows two stresses), `AX` needs none. A word matches without regard to case, in the words of the text. A number or a word with punctuation cannot have an entry: write the words that are spoken.
- `spell` reads words letter by letter: `spell "cpu usb"`, or `spell "all"` for every word of the text. A word is in `lexicon` or in `spell`, not in both.

The voice reads a list of letters that it cannot pronounce ("gpu", "usb") letter by letter by itself. Give such a word an entry in `lexicon` and it is said with your phones instead.

{{amplet Own pronunciations}}
composition "flite lexicon" { ample 1  tempo 120  master-seed 7 }

speech narrator at 1 for 2 {
  engine "flite"
  text "The ample tomato runs on a cpu."
  lexicon "ample = AE1 M P AX L; tomato = T AH0 M EY1 T OW0"
  spell "cpu"
}

master master {
  from narrator.audio -> audio
}
{{/amplet}}

Shaping single words in the text (Flite, text mode):

- `<pitch 220>word</pitch>` speaks the words between the tags at that pitch, from 50 to 480 Hz. `<pitch +3st>` is a shift in semitones (-24 to +24) of the pitch of the block.
- `<rate 1.3>words</rate>` speaks them at that speed (0.25 to 4; 1 is the speed of the block). Nested rates multiply.
- `<range 25>words</range>` sets how far the pitch moves around the pitch of the block, in Hz (0 to 200); 0 is flat.
- `<emph>word</emph>` stresses a word: three semitones higher and a bit longer.
- `<break 300ms/>` (or `<break 0.5s/>`) is a pause of exactly that length, up to 5 seconds.

The tags are not spoken and add no words. Tags may be nested. Write `&lt;`, `&gt;` and `&amp;` for the characters themselves; a `<` that is not followed by a letter or a slash is an ordinary character ("5 < 6"). In Live Preview the lit word is marked in the text as you wrote it, tags included. Tags work in text mode only: in words mode every word has its note.

{{amplet Words that stand out}}
composition "flite markup" { ample 1  tempo 120  master-seed 7 }

speech narrator at 1 for 2 {
  engine "flite"
  text "This is <emph>really</emph> good,<break 400ms/> and <rate 0.7>this part is slow</rate>."
}

master master {
  from narrator.audio -> audio
}
{{/amplet}}

### Three more voices: slt, rms and awb

`voice "slt"` (US English, female), `voice "rms"` (US English, male) and `voice "awb"` (Scottish English, male) are statistical voices from the CMU ARCTIC recordings. They sound smoother than kal16 and read the text the same way. Write the voice next to `engine "flite"`:

- They speak text. `rate`, `lead_trim`, `pause_scale`, `brightness`, the tags in the text, `lexicon` and `spell` all work as for kal16. They do not speak `phonemes` (the build says so with AMP5351).
- `mode "words"` makes them sing like kal16: each note speaks the next word at one steady pitch. The pitch they follow is from 74 to 650 Hz for slt, from 55 to 450 Hz for rms and from 114 to 486 Hz for awb; a note outside that range is sung the same note an octave or more away. Inside the range the pitch is within a few cents of the note.
- `pitch_hz` and `pitch_range` are optional. Without them the voice keeps the pitch and the range of its own recording (slt 172 Hz, rms 98 Hz, awb 132 Hz); then the sound is exactly the plain sound of the voice.
- They take more time and memory than kal16: about 5 MiB of memory for each second of speech, and a render runs about 15 times faster than real time. A short line is no problem; long speech in Remix may be refused for its memory.

The Flite software and the voices are free for any use: AMPLE contains a modified C# translation of the code and the voice data in a compact form, and keeps their notices (see THIRD-PARTY-NOTICES.md; the recordings of slt, rms and awb come from the CMU ARCTIC databases, LICENSES/cmu-arctic.txt). The translation is not made or approved by Carnegie Mellon University.

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

`rate` is the speaking speed (1 is the normal speed, from 0.1 to 2). `pitch_hz` is the pitch of the voice in text mode (45 to 700 Hz, default 105). `gain` adds or removes decibels (-60 to +12). `level` decides whether the three voices are equally loud: with `level "matched"` (the default) AMPLE adds a fixed amount to the gain of each voice (retro +5 dB, Humanico 0, Flite -3 dB), so the same `gain` sounds equally loud for all three, within a quarter of a loudness unit; `level "engine"` keeps the loudness that each voice makes. One voice at gain 0 peaks around -13 dBFS, so it leaves room; if a gain pushes the sound over the limit, the take reports it (`gain-clipped`) instead of hiding it.

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
