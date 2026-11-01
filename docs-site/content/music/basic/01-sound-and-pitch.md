---
title: Sound and pitch
order: 10
lead: A sound is a vibration. Faster vibration is a higher note.
---
## Vibration

When you pluck a guitar string it moves back and forth very fast. That moves the air, and the air moves your ear. How many times per second it moves is the **frequency**, measured in **hertz (Hz)**.

- Slow vibration (around 60 Hz) is a low, deep sound.
- Fast vibration (around 2000 Hz) is a high, thin sound.

{{pv {"title":"A low tone, a middle tone, a high tone","src":"seq","p":{"notes":[36,48,60,72,84],"step":0.75,"wave":"sine","len":1.1}}}}

## Pitch is what we call "high" and "low"

Doubling the frequency gives you the same note, one **octave** higher. That is why a man and a woman can sing "the same" song: they sing the same notes an octave apart.

{{pv {"title":"The same note, an octave apart","src":"seq","p":{"notes":[57,69,57,69],"step":1,"wave":"sine"}}}}

## Tone colour

A flute and a violin can play the same pitch and still sound different. That difference is the **timbre** (say "tam-ber"), the colour of a sound. It comes from the extra vibrations that ride on top of the main one.

{{pv {"title":"Same note, four colours: sine, triangle, square, saw","src":"seq","p":{"notes":[57,57,57,57],"step":1,"wave":"sine"},"note":"This one only plays a sine. Compare with the next."}}}
{{pv {"title":"A saw wave: bright and buzzy","src":"seq","p":{"notes":[57,57,57,57],"step":1,"wave":"sawtooth"}}}}

{{deeper Why 440?}}
Pitch is just a number of vibrations per second. Orchestras agreed that the A above middle C is 440 Hz so that everyone is in tune together. Each note up the keyboard is about 6% faster than the one before. After twelve such steps you have doubled: an octave.
{{/deeper}}

{{deeper Harmonics: why instruments sound different}}
A plucked string does not vibrate in only one way. It vibrates as a whole (the **fundamental**, the note you hear) and, at the same time, in halves, thirds, quarters... Those extra vibrations are the **harmonics** (or overtones). Their frequencies are simple multiples of the fundamental: 2x, 3x, 4x, 5x and so on.

| Harmonic | Frequency (for A = 110 Hz) | Interval above the fundamental |
|---|---|---|
| 1 (fundamental) | 110 Hz | - |
| 2 | 220 Hz | octave |
| 3 | 330 Hz | octave + fifth |
| 4 | 440 Hz | two octaves |
| 5 | 550 Hz | two octaves + major third |

How loud each harmonic is decides the timbre. A sine wave has only the fundamental (pure, hollow). A square wave has the odd harmonics (hollow, clarinet-like). A saw wave has all of them (bright, brassy). A clarinet, a violin and your voice differ in exactly this recipe.

This also explains why the octave, the fifth and the major third sound so natural together: they are already inside every single note.
{{/deeper}}

{{deeper Equal temperament and cents}}
Pure intervals use exact ratios (a perfect fifth is 3:2). But if you tune a keyboard with pure intervals you can only play well in one key. The solution in use for centuries is **equal temperament**: the octave is divided into 12 equal steps. Each semitone is the same ratio, about 1.0595 (the twelfth root of 2).

Everything is slightly out of tune compared with the pure ratios, but evenly, so every key is usable. Small differences are measured in **cents**: a semitone is 100 cents. Most people notice a difference of about 5 to 10 cents when notes play together.
{{/deeper}}

{{ample A sound source is a `synth` block. Its wave is a setting: sine, triangle, square or saw. | synth, /learn/ample/basic/what-is-sound=Learn: what is sound}}
