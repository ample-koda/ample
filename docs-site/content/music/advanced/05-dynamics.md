---
title: Compression and dynamics
order: 120
lead: A compressor turns down the loud parts so everything can be heard and the sound feels tighter.
---
## What it does

Loud parts get quieter, so the difference between loud and soft shrinks. Then you can raise the whole sound. The result is denser and more even.

{{pv {"title":"Uncompressed drums","src":"drums","fx":"dry"}}}
{{pv {"title":"Compressed hard","src":"drums","fx":"compressor","p":{"threshold":-40,"ratio":12}}}}

## Settings in plain words

- **Threshold**: from how loud on it starts.
- **Ratio**: how strongly it turns down.
- **Attack and release**: how fast it reacts and lets go.

## Gate

A **gate** does the opposite: it silences sounds that are below a level, which removes noise between notes.

{{pv {"title":"A gate chopping a pad","src":"pad","fx":"gate"}}}

## Pumping

If the compressor reacts to the kick, the other sounds dip with each beat. This "pumping" is a style in dance music.

{{pv {"title":"A pad that pumps","src":"pad","fx":"duck"}}}

{{deeper Attack and release in practice}}
A slow **attack** lets the first hit of a note pass before the compressor turns it down, so the sound keeps its punch. A fast attack squeezes the punch. A fast **release** lets the sound come back quickly and makes it pump. A slow release is smoother. There is no right setting, only what serves the sound.
{{/deeper}}

{{deeper Sidechain and parallel compression}}
**Sidechain**: the compressor listens to one sound (the kick) but turns down another (the bass or pad). It is how the pumping you heard works.

**Parallel compression**: mix a heavily compressed copy of a sound with the original. You get the density of the squeezed one and the life of the untouched one. A popular trick on drums.
{{/deeper}}

{{ample `compressor`, `gate`, `duck` and `limiter` control levels. | compressor, gate, duck, limiter}}
