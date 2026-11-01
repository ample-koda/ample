---
title: Sound design recipes
order: 90
lead: Combine what you have learned to make textures.
---
## Warm pad

A saw pad, a slow low-pass filter, chorus and a long reverb.

{{pv {"title":"A pad with a slow filter","src":"pad","fx":"filter","p":{"from":300,"to":2500}}}}

## Pluck with echo

A short synth note with a short decay and a rhythmic delay.

{{pv {"title":"Plucks with a dotted echo","src":"pluck","fx":"delay","p":{"time":0.375,"feedback":0.4,"mix":0.5}}}}

## Pumping chords

Sustained chords with the volume ducked by a kick.

{{pv {"title":"Pad with pumping","src":"pad","fx":"duck"}}}

{{try Make a variation}}
Take one recipe above. Change one thing: the filter speed, the delay time, the amount. Write down what you changed and what you heard. That is how sound designers learn.
{{/try}}

{{deeper Layering without mud}}
When you stack sounds, give each layer a purpose and its own range. For a big pad: one layer for the body (low-mid), one for the air (high, filtered bright), one for movement (a slow-modulated copy). Use EQ to cut each layer where the others live. If it gets cloudy, mute a layer; often the sound improves.
{{/deeper}}

{{deeper Textures from noise}}
Noise is a free resource. Filtered, it becomes wind (a band-pass sweep), rain (a high band with random amplitude), or a snare body (short noise burst). A slow low-pass sweep on noise is a riser. A sample with a low volume under a pad adds air and the feeling of a real space.
{{/deeper}}

{{ample Combine blocks in a chain to follow a recipe. | synth, filter, delay, reverb, duck}}
