---
title: Sources and instruments
order: 10
lead: Where sound comes from: instruments that play notes, recordings, and silence.
---
## Instruments that play notes

An instrument block takes **notes** in and gives **audio** out. Write the notes in a `midi-clip` (or read them from a MIDI file), and connect it.

{{blocks synth; piano; guitar; bass; drums; sampler; gm; midi-render; delay-llama}}

Which one? A `synth` is the most flexible and has no files to carry. `piano`, `guitar`, `bass` and `drums` are ready-made instruments. `sampler` plays your own recordings as an instrument. `gm` and `midi-render` play notes through a SoundFont, a file with real instrument recordings.

## A voice made of vowels

The `delay-llama` block sings: notes in, a humming, oohing and aahing voice out. It is explained on its own page, with its settings, in [the voices page of the Music track](#/learn/music/expert/delay-llama).

## Recordings

{{blocks import-audio=Brings a recording into the song. It remembers what it contained, so the song can be built again; ambience=Turns a recording (rain, a room tone, a crowd) into a seamless background of any length; silence}}

{{try Make a layered scene}}
Combine three things: an `ambience` of rain under everything, a `synth` pad, and a `drums` part. Notice how the ambience makes the song feel like it is somewhere.
{{/try}}
