---
title: Chords
order: 110
lead: Notes played together make a chord. The mood of a chord comes from its intervals.
---
## Triads

Take a note, add the note a third above and the note a fifth above that. That is a **triad**, the building block of most songs.

| Chord | Intervals | Sound |
|---|---|---|
| major | major third + minor third | bright, happy |
| minor | minor third + major third | dark, sad |
| diminished | minor third + minor third | tense, unstable |
| augmented | major third + major third | dreamy, odd |

{{pv {"title":"C major","src":"chords","p":{"chords":[[60,64,67]],"step":2,"wave":"triangle"}}}}
{{pv {"title":"C minor","src":"chords","p":{"chords":[[60,63,67]],"step":2,"wave":"triangle"}}}}
{{pv {"title":"Diminished, then augmented","src":"chords","p":{"chords":[[60,63,66],[60,64,68]],"step":1.6,"wave":"triangle"}}}}
{{piano {"from":60,"to":72,"hi":[60,64,67],"play":"chord","caption":"C major: C, E, G. Press play to hear them together."}}}

## Names

A chord is named after its lowest (root) note: **C**, **Am** (A minor), **G7**. If it has an "m" it is minor, if it has nothing it is major.

## Arpeggios

Playing the notes of a chord one after the other is an **arpeggio**. It is the same chord, spread out in time.

{{pv {"title":"A minor, F, C, G: as chords","src":"chords","p":{"chords":[[57,60,64],[53,57,60],[60,64,67],[55,59,62]],"step":1,"wave":"triangle"}}}}
{{pv {"title":"The same four chords as an arpeggio","src":"seq","p":{"notes":[57,60,64,60,53,57,60,57,60,64,67,64,55,59,62,59],"step":0.25,"len":1.2}}}}

{{deeper Seventh chords}}
Add one more third on top of a triad and you get a **seventh chord** (e.g. G7). It sounds richer and wants to move on. We use them a lot in the Advanced part.
{{/deeper}}

{{deeper How chords are built from a scale}}
Take any scale and stack notes by skipping every other one: the first, the third and the fifth. Do that on every step of C major and you get a triad on each:

| Step | I | ii | iii | IV | V | vi | vii° |
|---|---|---|---|---|---|---|---|
| Chord | C | Dm | Em | F | G | Am | Bdim |
| Quality | major | minor | minor | major | major | minor | diminished |

Every key has the same pattern: three major, three minor and one diminished chord. That is why you can switch between keys and the same progression works.
{{/deeper}}

{{deeper Reading chord symbols}}
| Symbol | Meaning |
|---|---|
| C | C major |
| Cm | C minor |
| C7 | C dominant seventh |
| Cmaj7 | C major seventh |
| Cdim | C diminished |
| Csus4 | C with a fourth instead of the third |
| C/E | C chord with E in the bass |
{{/deeper}}

{{ample The `chord` and `arpeggiator` blocks turn a note into a chord or an arpeggio. | chord, arpeggiator}}
