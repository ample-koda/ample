---
title: Stereo and panning
order: 100
lead: With two speakers (or ears) you can place sounds left, right or in the middle.
---
## Pan

**Panning** places a sound between the left and right speaker. Centre is the default; the kick and bass usually stay in the centre while other instruments are spread out.

{{pv {"title":"Panned hard left","src":"pluck","fx":"pan","p":{"pan":-0.9}}}}
{{pv {"title":"Panned right, then a sweep","src":"pluck","fx":"pan","p":{"sweep":true}}}}

## Width

A sound can feel wide when the two sides differ slightly. This is used for pads and stereo effects.

{{pv {"title":"A pad made wider","src":"pad","fx":"width"}}}

## Use headphones

Stereo is easiest to hear on headphones. If you only have one speaker, the left and right are mixed and the effect disappears.

{{deeper Mid and side}}
A stereo signal can be split into the **mid** (what both speakers share: the centre) and the **side** (what differs). Treating them separately lets you widen a mix by raising the side or tighten it by lowering the side. The rule of thumb: bass and kick in the middle, wide parts at the sides.
{{/deeper}}

{{deeper The Haas effect}}
If two copies of a sound arrive less than about 30 ms apart, your brain hears one sound coming from the side that arrived first. Delaying one channel by a few milliseconds makes a sound feel wide without any change in level. It is the secret behind many stereo widening tricks.
{{/deeper}}

{{ample `pan` places a sound; `width` makes it wider. | pan, width}}
