---
title: Controls
order: 40
lead: A control is a number that changes over time. It can steer almost any parameter.
---
## Three ways to get one

- **A plain number** is a control that never changes: `cutoff 800`.
- **A ramp by bar**: `auto(bar:value, ...)` goes from one value to the next, bar by bar. `cutoff auto(1:300, 5:6000)` opens the filter from 300 to 6000 over four bars.
- **A block that makes a control**: `curve` (draw it over the song), `follow` (the level of another sound, so the music listens), `map` (changes the range or shape of a control), `fire` (turns a control into notes) and `variants` (switch between versions of a part).

{{blocks curve; follow; map; fire; variants}}

{{amplet A filter that opens while its resonance grows}}
composition "Opening filter" { ample 1  tempo 120  master-seed 20261017 }

midi-clip chords at 1 for 4 {
  chords Am F C G
  bars 4
}

synth pad at 1 for 4 {
  wave saw
  gain -14 dB
  from chords.notes -> notes
}

filter sweep at 1 for 4 {
  type lowpass
  cutoff auto(1:300, 5:6000)
  resonance auto(1:10, 5:60)
  from pad.audio -> audio
}

master out {
  from sweep.audio -> audio
}
{{/amplet}}

## A control waits for its maker

A block that reads a control waits until the block that makes it is built. A loop of controls that depend on each other is an error (AMP2010). One control can steer several parameters at once.

{{deeper Ducking by hand}}
`follow` a kick drum to get a control that goes up on each hit, pass it through `map` to turn it round, and connect the result to the volume of a pad. The pad dips on every kick. This is what the `duck` block does for you in one step.
{{/deeper}}

{{ample The Music course shows what a moving parameter sounds like. | /learn/music/expert/modulation=Modulation, follow, curve, filter}}
