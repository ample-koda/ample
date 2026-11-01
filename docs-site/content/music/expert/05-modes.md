---
title: Modes
order: 10
lead: The same seven notes, started from a different home, give seven different moods.
---
## One scale, seven homes

Play the white keys from C to C: major. Play them from D to D: a different mood, called **Dorian**. Each starting note gives a **mode**. Here they are all started from C, so you can compare the colours.

| Mode | Mood | Difference from major |
|---|---|---|
| Ionian | bright (the major scale) | none |
| Dorian | cool, jazzy minor | minor with a raised 6th |
| Phrygian | dark, Spanish | minor with a lowered 2nd |
| Lydian | floating, dreamy | major with a raised 4th |
| Mixolydian | bluesy, relaxed | major with a lowered 7th |
| Aeolian | sad (the natural minor) | the minor scale |

{{pv {"title":"C Dorian","src":"seq","p":{"notes":[60,62,63,65,67,69,70,72],"step":0.45}}}}
{{pv {"title":"C Phrygian","src":"seq","p":{"notes":[60,61,63,65,67,68,70,72],"step":0.45}}}}
{{pv {"title":"C Lydian","src":"seq","p":{"notes":[60,62,64,66,67,69,71,72],"step":0.45}}}}
{{pv {"title":"C Mixolydian","src":"seq","p":{"notes":[60,62,64,65,67,69,70,72],"step":0.45}}}}

{{piano {"from":60,"to":72,"hi":[60,62,64,66,67,69,71,72],"play":"scale","caption":"Lydian: the major scale with one raised note (F#). Hear the dreamy lift."}}}

## Using a mode

Keep a drone or a repeated chord on the home note and play melodies from the mode. The mood appears because of the one note that differs.

{{try Spot the odd note}}
Play C major (Basic course) and then C Mixolydian. Only one note is different. Can you hear which one? It is the seventh, lowered.
{{/try}}

{{deeper How the modes are built}}
Each mode is the major scale (the white keys) played from a different starting note. The pattern of whole (W) and half (H) steps shifts with it.

| Mode | Start (on white keys) | Pattern |
|---|---|---|
| Ionian | C | W W H W W W H |
| Dorian | D | W H W W W H W |
| Phrygian | E | H W W W H W W |
| Lydian | F | W W W H W W H |
| Mixolydian | G | W W H W W H W |
| Aeolian | A | W H W W H W W |
| Locrian | B | H W W H W W W |

The **character note** of each mode is the one that differs most from plain major or minor: the raised 6th of Dorian, the flat 2nd of Phrygian, the raised 4th of Lydian, the flat 7th of Mixolydian. Build your melodies and chords around it to bring the mode out.
{{/deeper}}

{{deeper Chords that belong to a mode}}
A mode suggests its own chord pairs. In **Dorian**, i and IV (Dm and G) sound cool and jazzy. In **Mixolydian**, I and bVII (G and F) are the sound of classic rock. In **Lydian**, I and II (F and G) float. In **Phrygian**, i and bII (Em and F) are dark and Spanish. Playing just two such chords over a drone is enough to establish the mode.

{{pv {"title":"Mixolydian: C, then Bb (bVII)","src":"chords","p":{"chords":[[60,64,67],[58,62,65],[60,64,67],[58,62,65]],"step":1,"wave":"triangle"}}}}
{{pv {"title":"Phrygian: E minor, then F (bII)","src":"chords","p":{"chords":[[52,55,59],[53,57,60],[52,55,59],[53,57,60]],"step":1,"wave":"triangle"}}}}
{{/deeper}}

{{ample Modes are just different note lists for a `midi-clip` or `chord` part. | midi-clip, chord}}
