---
title: Notes and the keyboard
order: 70
lead: There are only twelve note names. A keyboard shows them all, and then repeats.
---
## Twelve notes

The white keys are **C D E F G A B** and then the pattern starts over (a higher C). The black keys are in between. That gives twelve notes before it repeats; the repeat is the octave you heard earlier.

{{piano {"from":60,"to":72,"caption":"Click the keys. C is the white key just left of the two black keys."}}}

## Sharps and flats

A black key has two names. The black key between C and D is **C sharp (C#)** if you come from C (a bit higher), or **D flat (Db)** if you come from D (a bit lower). It is the same key.

## Semitones and tones

The distance between two neighbouring keys (including black) is a **semitone**, the smallest step in most music. Two semitones make a **whole tone**.

{{piano {"from":60,"to":72,"hi":[60,61,62],"play":"scale","caption":"C, then a semitone up (C#), then a whole tone from C (D)."}}}

{{deeper Why seven letters but twelve notes?}}
The letter names were made for the seven white notes. The black notes came later and got names by adding sharp or flat. The system is old and a bit odd, but you only need to know it once.
{{/deeper}}

{{deeper Octave names and MIDI numbers}}
Every note has a letter and an octave number: C4 is middle C. Careful: some programs call middle C "C3" and some "C4". The convention in this course is the common one, C4 = MIDI note 60.

| Note | MIDI number |
|---|---|
| C3 | 48 |
| C4 (middle C) | 60 |
| A4 | 69 (440 Hz) |
| C5 | 72 |

Each step up the keyboard adds one. That makes calculation simple: a major chord on C4 is 60, 64, 67; the same chord a fifth higher is 67, 71, 74.
{{/deeper}}

{{deeper Sharps, flats and enharmonics}}
C# and Db are the same key on a piano but are spelled differently depending on the key you are in. In a key with sharps you write C#, in a key with flats you write Db. This spelling is called **enharmonic**. It matters only for reading and theory; on a keyboard or in MIDI it is the same note.
{{/deeper}}

{{ample Notes in AMPLE are numbers or names such as `C4`. Middle C is 60 in MIDI numbers. | midi-clip, transpose}}
