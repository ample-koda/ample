---
title: Space, reverb and echo
order: 110
lead: Reverb and delay put a sound in a room. They create depth: close sounds are dry, far sounds are wet.
---
## Reverb

**Reverb** is the sound of a room around the original sound: thousands of tiny echoes that blur together. A short reverb is a small room. A long reverb is a cathedral.

{{pv {"title":"Small room","src":"pluck","fx":"reverb","p":{"decay":0.25,"mix":0.4}}}}
{{pv {"title":"Big hall","src":"pluck","fx":"reverb","p":{"decay":0.9,"mix":0.7}}}}

## Delay

A **delay** is an echo: a copy of the sound a bit later. Set to a rhythm (like a dotted eighth) it makes music dance.

{{pv {"title":"A dotted echo","src":"pluck","fx":"delay","p":{"time":0.375,"feedback":0.5,"mix":0.6}}}}

## Depth

Put the important sound dry and close, the background sounds wetter. Too much reverb on everything is mush.

{{deeper Pre-delay}}
A small gap before the reverb starts lets the sound stay clear and then bloom. Singers often use it so their words stay sharp.
{{/deeper}}

{{deeper Types of reverb}}
| Type | Character | Typical use |
|---|---|---|
| Room | small, natural | drums, live feel |
| Hall | large, smooth | strings, pads, ballads |
| Plate | bright, dense | vocals, snare |
| Spring | twangy, metallic | guitar |
| Convolution | a recording of a real place | any space you like |
{{/deeper}}

{{deeper Delay tricks}}
- **Ping-pong**: the echoes alternate between left and right.
- **Filtered feedback**: each repeat gets darker, so the echo sits behind the dry sound.
- **Tempo-synced**: set the delay to a note value (a quarter, a dotted eighth) so it fits the groove.
- **Slapback**: one very short echo (80-120 ms) for a vintage feel.
{{/deeper}}

{{ample `reverb` sets size, decay and mix; `delay` sets time and feedback. | reverb, delay, convolution}}
