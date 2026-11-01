---
title: Keys and the circle of fifths
order: 20
lead: A key is the home of a song. The circle of fifths is the map of all twelve keys.
---
## What a key tells you

"A song in C major" means the notes mostly come from the C major scale and that C feels like home. Change the key and every note moves up or down together. The song is the same, only higher or lower.

{{pv {"title":"A melody in C","src":"seq","p":{"notes":[60,62,64,60,64,67,64,60],"step":0.45}}}}
{{pv {"title":"The same melody in D (2 semitones up)","src":"seq","p":{"notes":[62,64,66,62,66,69,66,62],"step":0.45}}}}

## The circle of fifths

Put the twelve keys in a circle where every step is a **fifth** (7 semitones) up: C, G, D, A, E, B, F#... Neighbours on the circle share almost all their notes, so songs move easily between them.

| Key | Sharps / flats |
|---|---|
| C major | none |
| G major | 1 sharp |
| D major | 2 sharps |
| F major | 1 flat |
| B-flat major | 2 flats |

## Relative minor

Every major key has a **relative minor** that uses the same notes. C major and A minor share all the white keys. The only difference is which note is home.

{{piano {"from":57,"to":72,"hi":[57,59,60,62,64,65,67,69],"play":"scale","caption":"A minor: the same white keys as C major, starting from A."}}}

{{try Same notes, two homes}}
Play the C major scale from the previous course, then A natural minor (starting on A). Same keys, but one sounds bright and one sounds dark. That is the effect of choosing a different home.
{{/try}}

{{deeper Why fifths?}}
The perfect fifth is the second strongest relation after the octave (the third harmonic). Two keys a fifth apart share six of their seven notes, so moving between them is smooth. That is why the circle of fifths is the map of almost all Western harmony: close neighbours are easy to reach, far ones are a surprise.
{{/deeper}}

{{deeper The order of keys}}
Going up a fifth adds one sharp: C (0), G (1), D (2), A (3), E (4), B (5), F# (6). Going down a fifth adds one flat: F (1), Bb (2), Eb (3), Ab (4), Db (5). Where the two directions meet (F#/Gb) is the "far side" of the circle, the key furthest from C.
{{/deeper}}

{{ample `transpose` moves a whole part to another key. | transpose, midi-clip}}
