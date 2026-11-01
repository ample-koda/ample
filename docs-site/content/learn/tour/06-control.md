---
title: Control and automation
order: 60
lead: A control is a number that changes over time. It can steer any parameter.
---
## What a control is

Most parameters can be a number or a **control**. A control can be a drawn curve, an LFO (a slow wave), or follow the level of another sound. One control can steer several parameters at once.

{{blocks curve; follow; map; fire; variants}}

- `curve` is a control you draw over the song.
- `follow` turns the level of a sound into a control (the music listens to a sound).
- `map` changes a control's range or shape, for example 0 to 1 into 20 to 120.
- `fire` turns a control into notes (see Notes and MIDI).
- `variants` lets a song have several versions of one part that you can switch between.

{{deeper Example: ducking by hand}}
`follow` a kick drum to get a control that goes up on each hit. Pass it through `map` to invert it. Connect the result to the volume of a pad. The pad dips on every kick, which is the pumping effect, built from three simple blocks.
{{/deeper}}
