---
title: Audio components
order: 40
lead: A component can also be a chain of effects, or an instrument with effects, instead of note code.
---
## An effect chain as a block

A component with an audio port is an **audio component**. It has the same declaration, versions and seal, but its `process` is a graph of audio processors. The processors are the effect blocks (`delay`, `reverb`, `chorus`, `compressor`, `eq`, `filter`, `limiter`...), written as steps of a pipeline.

{{amplet A wet and dry chain as one block}}
composition "Audio component" { ample 1  tempo 120  master-seed 20261017 }

component alex.fx.wetDry version 1.0.0 {
  input audio: Audio
  property echo: Duration = 0.5beat
  property feedback: Percent = 35%
  property dryLevel: Gain = -3dB
  output audio: Audio
  process {
    let dry = audio
    let wet = audio |> delay(time=echo, feedback=feedback) |> reverb(size=50%)
    emit mix(dry |> gain(dryLevel), wet |> gain(-8dB))
  }
}

midi-clip riff at 1 for 2 {
  melody "c4:0.5 e4:0.5 g4:0.5 r:2.5 c5:0.5 g4:0.5 e4:0.5 r:2.5"
  bars 2
}

synth lead at 1 for 2 {
  wave triangle
  gain -12 dB
  from riff.notes -> notes
}

alex.fx.wetDry space at 1 for 2 {
  echo 0.5beat
  feedback 35%
  from lead.audio -> audio
}

master out {
  from space.audio -> audio
}
{{/amplet}}

- **Audio is a graph, not a buffer.** `audio |> delay(...)` describes a node; the build renders the blocks it becomes. Each processor keeps its own cached result.
- **Arguments are typed by the unit of the block parameter**: a time for `ms` (`250ms`, `0.25beat`, `1bar`), a percent for `%`, a gain for `dB`, a frequency for `Hz`.
- **`gain(x)` is the `volume` block and `mix(a, b)` is a `bus`.** A second audio input is given by name, for example `compressor(threshold=-12dB, key=sidechain)`.
- **A name is defined before it is used**, so a graph cannot loop. Feedback exists only through the feedback parameter of a processor. A component has at most 64 processors and 8 outputs.
- **An instance** is connected like any block, and becomes ordinary blocks named after the instance and the processor (`space__delay1`), so logs and diagnostics say where something came from.

## Notes in, sound out

A component can have a notes input and an instrument inside: `emit notes |> piano() |> delay(time=250ms) |> reverb(size=room)`. The instruments are the blocks that read notes and give audio: `piano`, `bass`, `guitar`, `synth`, `gm`, `sampler` and `delay-llama`.

{{deeper Calling components from components}}
A process can call another component like a function: `audio |> alex.fx.dreamEcho(feedback=20%)`. Write the version after the name to keep the one you tested: `alex.fx.dreamEcho@1.0.0(...)`. A component never calls itself, directly or through others; the check names the circle.
{{/deeper}}

{{blocks delay; reverb; chorus; compressor; eq; filter; limiter}}
