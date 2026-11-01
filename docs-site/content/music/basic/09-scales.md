---
title: Scales
order: 90
lead: A scale is a set of notes that sound good together. Most songs use only the notes of one scale.
---
## The major scale

Start on a note and go up using the pattern whole, whole, half, whole, whole, whole, half. From C that is all the white keys. It sounds bright and finished.

{{pv {"title":"C major scale","src":"seq","p":{"notes":[60,62,64,65,67,69,71,72],"step":0.45}}}}
{{piano {"from":60,"to":72,"hi":[60,62,64,65,67,69,71,72],"play":"scale","caption":"C major: all the white keys."}}}

## The minor scale

Lower the third, sixth and seventh by a semitone and you get the natural minor scale. It sounds darker and more serious.

{{pv {"title":"C natural minor scale","src":"seq","p":{"notes":[60,62,63,65,67,68,70,72],"step":0.45}}}}
{{piano {"from":60,"to":72,"hi":[60,62,63,65,67,68,70,72],"play":"scale","caption":"C minor: three black keys appear."}}}

## The pentatonic scale

Five notes only. It is hard to play a wrong note, which is why it is used everywhere in rock, blues and folk.

{{pv {"title":"C minor pentatonic","src":"seq","p":{"notes":[60,63,65,67,70,72],"step":0.55}}}}

## The key of a song

A song's **key** says which note is "home" and which scale it uses. "In C major" means: home is C, and most notes come from the white keys. Every note of a scale has a number: 1 is home, 5 is strong, 7 wants to go back to 1.

{{deeper Why do the notes sound like they belong together?}}
Notes in a scale are related by simple frequency ratios. The ear recognises that. Notes outside the scale still can be used: they feel like a surprise or a bit of tension, and that is a tool, not a mistake.
{{/deeper}}

{{deeper Build any major scale with the pattern}}
The major scale pattern is **W W H W W W H**, where W is a whole tone (2 semitones) and H a semitone (1). Start on any note and follow the pattern to get that key's major scale.

| Start | Notes |
|---|---|
| C | C D E F G A B C |
| G | G A B C D E F# G |
| F | F G A Bb C D E F |

Notice that G major needs one sharp (F#) and F major one flat (Bb). That is the reason for key signatures in the next course.
{{/deeper}}

{{deeper The blues scale}}
Take the minor pentatonic and add one "blue note", the flat fifth. Played between the fourth and fifth, it gives the gritty, bending sound of blues and rock.

{{pv {"title":"C blues scale","src":"seq","p":{"notes":[60,63,65,66,67,70,72],"step":0.5}}}}
{{/deeper}}

{{ample Scales appear as note names in a `midi-clip` block, and `transpose` moves everything to another key. | midi-clip, transpose, chord}}
