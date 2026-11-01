---
title: Syncopation and swing
order: 60
lead: Putting accents where you do not expect them makes music groove and bounce.
---
## Off the beat

**Syncopation** means placing an accent between the beats or on a normally weak beat. A steady rhythm becomes a lively one.

{{pv {"title":"Plain: hits on every beat","src":"grid","p":{"bpm":100,"kick":"x...x...","snare":"x...x...","hat":"xxxxxxxx"}}}}
{{pv {"title":"Syncopated: hits between the beats","src":"grid","p":{"bpm":100,"kick":"x..x..x.","snare":"..x...x.","hat":"xxxxxxxx"}}}}

## Backbeat

The snare on 2 and 4 is itself a syncopation compared to a marching beat. It is the sound of rock, soul and pop.

## Swing

In swing, pairs of eighth notes are not even: the first is long and the second is short. The ratio is about 2 to 1. It gives jazz its skipping feel.

{{pv {"title":"Straight eighths","src":"seq","p":{"notes":[60,64,60,64,60,64,60,64],"step":0.25}}}}
{{pv {"title":"Swung eighths (long, short)","src":"seq","p":{"notes":[60,null,64,60,null,64,60,null,64],"step":0.2}}}}

{{deeper Polyrhythm}}
Two rhythms at once with different groupings, for instance three against four. It sounds complicated at first, but once you feel both pulses it creates a rolling sensation common in African and Latin music.
{{/deeper}}

{{deeper Clave: a rhythm that organises a song}}
In Latin music a short repeating pattern, the **clave**, is the backbone. The most common, the 3-2 son clave, has five hits across two bars: three in the first bar and two in the second. Everything else (bass, piano) is built around it. It is a good example of how a rhythm can be a framework rather than just a beat.
{{/deeper}}

{{deeper Humanise your rhythm}}
Machines hit exactly on the grid. People are slightly early or late, and louder or softer. Adding tiny random shifts (a few milliseconds) and velocity changes makes a loop feel natural. Do not overdo it: too much looks like a mistake, too little feels like a robot.
{{/deeper}}

{{ample Shift timing with `quantize` (snap to grid) and `midi-humanize` (slightly loose). | quantize, midi-humanize, sequencer}}
