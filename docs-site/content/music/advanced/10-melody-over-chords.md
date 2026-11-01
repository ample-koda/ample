---
title: Melody over chords
order: 70
lead: A melody sits on top of the chords. Some notes agree with them, some create a nice clash.
---
## Chord tones and passing notes

When the chord is C, the notes C, E and G sound stable (they are the **chord tones**). The other notes of the scale are **passing tones**: they move between the stable ones.

{{pv {"title":"Chord: C. Melody uses only chord tones.","src":"seq","p":{"notes":[64,67,72,67,64,60,64,67],"step":0.45}}}}
{{pv {"title":"Melody with passing notes in between","src":"seq","p":{"notes":[64,65,67,69,67,65,64,62],"step":0.4}}}}

## Tension

A note a semitone from a chord tone is very tense (for instance F against E in a C chord). It is useful when it quickly resolves.

{{pv {"title":"Tense F resolving to E","src":"seq","p":{"notes":[65,64,null,65,64],"step":0.6}}}}

## Call and response

A phrase asks, a second phrase answers. This is the simplest way to write a melody that feels complete.

{{deeper Approach notes}}
An **approach note** is a note one step above or below a chord tone, played just before it. It adds a bit of tension that resolves immediately. Jazz players approach target notes from a semitone below or above.

{{pv {"title":"Approach notes: a semitone below each target","src":"seq","p":{"notes":[63,64,66,67,69,71,71,72],"step":0.35}}}}
{{/deeper}}

{{deeper Blue notes and the pentatonic}}
Over a major chord, the minor pentatonic often works as well, because its notes rarely clash. The bend between the minor and the major third (the "blue" third) is the heart of blues and rock guitar. A simple recipe for improvising: use the pentatonic, aim for the chord tones at the start of each bar, and leave rests.
{{/deeper}}

{{ample Write the melody in a `midi-clip` over a `chord` part, and listen to them at once. | midi-clip, chord, arpeggiator}}
