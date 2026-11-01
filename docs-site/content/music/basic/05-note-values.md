---
title: Note lengths and rests
order: 50
lead: Notes can be long or short. Silence counts too, and has its own sign.
---
## Splitting the beat

Take one beat. You can play it as one long note, or split it in two, or in four. These have names:

| Name | Beats (in 4/4) | Per bar |
|---|---|---|
| whole note | 4 | 1 |
| half note | 2 | 2 |
| quarter note | 1 | 4 |
| eighth note | 1/2 | 8 |
| sixteenth note | 1/4 | 16 |

{{pv {"title":"Quarter notes: one per beat","src":"seq","p":{"notes":[60,60,60,60,60,60,60,60],"step":0.5,"len":0.8,"wave":"triangle"}}}}
{{pv {"title":"Eighth notes: two per beat (twice as many)","src":"seq","p":{"notes":[60,60,60,60,60,60,60,60,60,60,60,60,60,60,60,60],"step":0.25,"len":0.8,"wave":"triangle"}}}}

## Rests

A **rest** is a silence with a length. A quarter rest is one silent beat. Rests give music room to breathe.

{{pv {"title":"Notes and rests: sound, sound, rest, sound","src":"seq","p":{"notes":[60,64,null,67,60,64,null,67],"step":0.5,"len":0.9,"wave":"triangle"}}}}

## Triplets

A **triplet** squeezes three notes in the space of two. It gives a rolling feel.

{{pv {"title":"Eighth notes, then triplets","src":"meter","p":{"beats":4,"bpm":80,"sub":3}}}}

{{deeper Dotted notes and ties}}
A **dot** after a note adds half of its length. A dotted quarter note lasts one and a half beats (a quarter plus an eighth). A **tie** joins two notes of the same pitch into one longer note, even across a bar line. Together they let you write any length.

| Written | Length (in 4/4 beats) |
|---|---|
| half note | 2 |
| dotted half | 3 |
| quarter | 1 |
| dotted quarter | 1.5 |
| dotted eighth | 0.75 |

The "dotted eighth + sixteenth" pattern is the long-short feel you hear in many marches and in the dotted delay in music production.
{{/deeper}}

{{deeper Other tuplets}}
A triplet puts three notes where two would be. You can do the same with other numbers: a **quintuplet** puts five in the space of four, a **sextuplet** six. They are used for fast runs and fills. Hearing them as a gentle "speeding up and rolling" is easier than counting them.
{{/deeper}}

{{ample Time in AMPLE is counted in bars, and notes inside a bar in steps. | midi-clip, quantize}}
