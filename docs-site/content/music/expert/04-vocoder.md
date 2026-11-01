---
title: Voices and the vocoder
order: 80
lead: Make an instrument speak, or make a voice sound like a machine.
---
## How it works

A **vocoder** needs two sounds: a **voice** (the modulator) and a rich sound such as a chord (the carrier). The voice is split into frequency bands. The loudness of each band controls the same band of the carrier. The result: the chord appears to say the words.

{{pv {"title":"A chord that talks (a rough stand-in)","src":"pad","fx":"vocoder","note":"A scripted voice on a saw chord: a rough stand-in for the real block."}}}

## Related effects

- **Auto-tune** nudges a voice to the nearest note: subtle correction or a robotic effect.
- **Pitch shift** moves a voice up or down.
- **Harmoniser** adds extra voices to make a choir.

{{deeper Bands and intelligibility}}
The more frequency bands a vocoder uses, the more of the voice it keeps. Four bands give a vague robot hint, sixteen make words clear. Consonants (s, t, k) are noisy and have no pitch, so most vocoders add a small amount of noise to carry them. If words are hard to understand, boost the highs of the voice before the vocoder and add a touch of noise.
{{/deeper}}

{{deeper Voice effects compared}}
| Effect | What it does | Sound |
|---|---|---|
| Vocoder | one sound speaks with another's spectrum | robot, choir of a synth |
| Auto-tune (strong) | snaps the pitch to the scale | hard, electronic |
| Pitch shift | moves the voice up or down | chipmunk or deep |
| Formant shift | changes the vocal tract size, not the pitch | bigger or smaller person |
| Harmoniser | adds notes in harmony | choir |
{{/deeper}}

{{ample The `vocoder` block takes a voice and a carrier. | vocoder, autotune, choir}}
