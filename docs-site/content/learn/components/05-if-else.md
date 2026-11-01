---
title: if / else in a component
order: 50
lead: Pick a branch of an audio component from its properties.
---
## Choosing a branch

In the process of an audio component a branch can be chosen by a property: `if (tone) { ... } else if (level) { ... } else { ... }`. The condition is a Bool property, a number (0 is false) or a calculation. The branch that is taken is known when the graph is built, so the lines of the other branches are not part of it, and the song never builds sound it does not use.

A `let` in a branch is available after it, so every branch should give the same names. In a calculation a Bool is 1 or 0, and `if(condition, a, b)` picks one of two values.

## Silence where there is no sound

`silence()` is blank audio as long as the block it is in; `silence(10s)` is as long as you say. It stands where a branch has no sound.

{{amplet A reverb bed that can be switched off with a property}}
composition "If else" { ample 1  tempo 120  master-seed 20261017 }

component alex.fx.roomBed version 1.0.0 {
  input audio: Audio
  property room: Bool = true
  property level: Gain = -6dB
  output audio: Audio
  process {
    if (room) {
      let bed = audio |> reverb(size=60%) |> gain(level)
    } else {
      let bed = silence()
    }
    emit mix(audio, bed)
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

alex.fx.roomBed space at 1 for 2 {
  room true
  level -6dB
  from lead.audio -> audio
}

master out {
  from space.audio -> audio
}
{{/amplet}}

{{ample The block behind the silence. | silence, reverb, /learn/ample/components/audio-components=Audio components}}
