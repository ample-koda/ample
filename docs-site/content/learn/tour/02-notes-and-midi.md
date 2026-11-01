---
title: Notes and MIDI
order: 20
lead: Blocks that write, read and change notes.
---
## Writing and reading notes

{{blocks midi-clip; midi=Reads a Standard MIDI File and gives its notes; sequencer}}

A `midi-clip` is where you type notes. The `midi` block reads a MIDI file you already have. A `sequencer` is a step grid, for example for drums.

## Changing notes

These blocks take notes and give back changed notes. Chain them like effects, but on notes.

{{blocks chord; arpeggiator; transpose; quantize; midi-humanize}}

Typical chain: `midi-clip` → `quantize` (snap the notes to the grid) → `midi-humanize` (add a tiny natural looseness) → instrument.

## Notes from a control

A control (a number that changes over time) can also play notes. The `fire` block plays a note each time a control passes a level: a car passes, a hit plays.

{{blocks fire}}
