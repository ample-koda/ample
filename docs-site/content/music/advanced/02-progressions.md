---
title: Chord progressions
order: 30
lead: A progression is a path through chords. It creates tension, relief and the feeling that a song goes somewhere.
---
## Home, away, back

Number the chords of a key with roman numerals: I is home, IV is a step away, V creates tension and wants to go home, vi is the sad cousin.

| In C | I | ii | iii | IV | V | vi |
|---|---|---|---|---|---|---|
| Chord | C | Dm | Em | F | G | Am |

## The famous four

I - V - vi - IV is the progression of countless pop songs.

{{pv {"title":"C - G - Am - F","src":"chords","p":{"chords":[[60,64,67],[55,59,62],[57,60,64],[53,57,60]],"step":1,"wave":"triangle"}}}}
{{pv {"title":"The same progression, played as an arpeggio","src":"seq","p":{"notes":[60,64,67,64,55,59,62,59,57,60,64,60,53,57,60,57],"step":0.25,"len":1.2}}}}

## ii - V - I

The jazz favourite. Dm7 leads to G7, which resolves to Cmaj7.

{{pv {"title":"Dm7 - G7 - Cmaj7","src":"chords","p":{"chords":[[62,65,69,72],[55,59,62,65],[60,64,67,71]],"step":1.3,"wave":"triangle"}}}}

## Make it your own

- Start somewhere else on the loop (vi - IV - I - V sounds more sad).
- Hold a chord longer or change twice per bar.
- Swap one chord for a related one (F for Dm).

{{deeper Chord functions}}
Chords do jobs. The **tonic** (I) is home. The **subdominant** (IV) moves away. The **dominant** (V) creates tension and returns home. Almost all common progressions are built from these three jobs. Seeing the job helps you replace chords without breaking the song.
{{/deeper}}

{{deeper Cadences: how phrases end}}
A **cadence** is a short chord pattern that ends a phrase, like punctuation.

| Cadence | Chords | Effect |
|---|---|---|
| Authentic | V - I | a full stop |
| Plagal | IV - I | a gentle "amen" |
| Half | ends on V | a comma, wants more |
| Deceptive | V - vi | a surprise: you expected I |

{{pv {"title":"Authentic: G then C","src":"chords","p":{"chords":[[55,59,62],[60,64,67]],"step":1.5,"wave":"triangle"}}}}
{{pv {"title":"Deceptive: G then Am","src":"chords","p":{"chords":[[55,59,62],[57,60,64]],"step":1.5,"wave":"triangle"}}}}
{{/deeper}}

{{deeper Progressions and their moods}}
| Progression (in major) | Mood |
|---|---|
| I - IV - V - I | classic, bright, folk and rock |
| I - V - vi - IV | uplifting, modern pop |
| vi - IV - I - V | the same four, but sadder (starts on minor) |
| I - vi - IV - V | the 1950s doo-wop |
| ii - V - I | jazz, smooth resolution |
{{/deeper}}

{{ample Write a chord as a note list and `chord` or `midi-clip` will play it. | chord, midi-clip, arpeggiator}}
