---
title: Synthesis and envelopes
order: 40
lead: Build a sound from a wave, then shape it with a filter and an envelope.
---
## The signal path

Almost every synthesiser works in the same order:

1. **Oscillator**: makes the raw wave (sine, triangle, square, saw).
2. **Filter**: removes some of the highs or lows.
3. **Amplifier**: sets the loudness over time.

{{pv {"title":"A saw wave through a filter that opens","src":"pad","fx":"filter","p":{"from":200,"to":7000}}}}

## Envelope: ADSR

An **envelope** shapes the volume of each note from start to end. It has four parts:

| Part | Meaning |
|---|---|
| Attack | time to reach full volume |
| Decay | time to fall to the sustain level |
| Sustain | the level while the key is held |
| Release | time to fade after the key is released |

A **short attack** with a quick fall sounds like a pluck. A **long attack** and a long release sounds like a slow pad.

{{pv {"title":"Pluck: fast attack, quick decay","src":"pluck"}}}
{{pv {"title":"Pad: slow attack, long release","src":"pad"}}}

## LFO

A **low-frequency oscillator** (LFO) is a slow wave used to move a setting, for example the pitch (vibrato) or the filter (wobble).

{{deeper Subtractive vs additive}}
Subtractive synthesis starts with a rich wave and removes parts with a filter. Additive builds a sound by stacking sine waves. Subtractive is easier to play with, additive is closer to how timbre is explained.
{{/deeper}}

{{deeper Waveforms and their harmonics}}
| Wave | Harmonics present | Sound |
|---|---|---|
| Sine | only the fundamental | pure, soft, flute-like |
| Triangle | odd harmonics, fast falling | mellow, hollow |
| Square | odd harmonics, slowly falling | woody, clarinet-like |
| Saw | all harmonics | bright, buzzy, brass and strings |
| Noise | everything, no pitch | hiss, cymbals, wind |

Filtering a saw wave is the basis of subtractive synthesis. A **resonance** setting boosts the area around the cutoff and gives the filter its "wow".
{{/deeper}}

{{deeper Envelope recipes}}
| Sound | Attack | Decay | Sustain | Release |
|---|---|---|---|---|
| Pluck | very short | short | low | short |
| Pad | long | long | high | long |
| Bass | short | medium | medium | short |
| Brass | medium | short | high | short |
| Percussion | instant | very short | none | very short |

{{pv {"title":"Short pluck envelope","src":"seq","p":{"notes":[57,60,64,69],"step":0.5,"len":0.3}}}}
{{pv {"title":"Long pad envelope","src":"chords","p":{"chords":[[57,60,64]],"step":3,"wave":"sawtooth"}}}}
{{/deeper}}

{{deeper FM and wavetable in a few words}}
**FM** (frequency modulation) makes one oscillator change the pitch of another extremely fast. The result is a rich, often metallic spectrum from just two oscillators: bells, electric pianos, harsh basses. **Wavetable** synthesis holds many wave shapes in a table and lets you move between them over time, giving sweeping, evolving sounds. Both create harmonics that are hard to get from a simple filter.
{{/deeper}}

{{ample The `synth` block has the oscillator, `filter` shapes the sound, and `fade` and `volume` are the envelope. | synth, filter, fade}}
