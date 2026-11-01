---
title: Bass and groove
order: 50
lead: The bass connects the rhythm with the chords. Together with the kick it forms the groove.
---
## Root and rhythm

The bass mostly plays the **root** of the chord, in the rhythm of the kick. Because it is low, a few well-chosen notes are enough.

{{pv {"title":"A bass line","src":"seq","p":{"notes":[45,45,null,45,48,null,45,43],"step":0.5,"wave":"sawtooth","len":0.9}}}}

## Kick and bass lock

Play kick and bass together. When they hit at the same moment they sound like one big thump. That locking is the groove.

{{pv {"title":"Kick and bass together","src":"grid","p":{"bpm":100,"kick":"x..x..x.","snare":"....x...","hat":"x.x.x.x."}}}}

## Make room

Low sounds fill a lot of space. Two bass parts at once sound muddy. If you must, keep one short and let the other ring.

{{deeper Ducking}}
**Ducking** makes one sound go quiet whenever another plays (usually the bass or pad dips when the kick hits). It keeps the kick clear and makes music "pump".
{{/deeper}}

{{try Listen for the gaps}}
Play the bass line, then notice where it is silent. Those gaps are as important as the notes. Without them the line would sound busy and blurred.
{{/try}}

{{deeper Bass patterns to try}}
- **Root-fifth**: alternate the root and the fifth. Simple, works in country and pop.
- **Octave**: jump between the root and the note an octave above. Typical for disco.
- **Walking**: one note per beat, stepping through the chord notes and the scale between chords. The sound of jazz.
- **Pedal**: stay on one note while the chords above change. Creates tension.

{{pv {"title":"Octave bass (disco)","src":"seq","p":{"notes":[45,57,45,57,45,57,45,57],"step":0.25,"wave":"sawtooth","len":0.9}}}}
{{pv {"title":"Walking bass","src":"seq","p":{"notes":[45,48,50,52,53,52,50,48],"step":0.5,"wave":"sawtooth","len":0.9}}}}
{{/deeper}}

{{deeper Why the bass is so hard to mix}}
Low notes take a lot of energy, and small speakers cannot reproduce them. A bass that sounds great on headphones may disappear on a phone. A trick is to add some harmonics (with light saturation) so the ear "fills in" the missing low note from the overtones.
{{/deeper}}

{{ample A `bass` block plays low notes, and `duck` lowers a sound when another plays. | bass, duck, synth}}
