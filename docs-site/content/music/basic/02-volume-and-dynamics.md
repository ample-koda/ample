---
title: Loud and soft
order: 20
lead: Volume is how big the vibration is. In music, changing it on purpose is called dynamics.
---
## Volume

A bigger vibration is a louder sound. We measure loudness in **decibels (dB)**. You do not need the maths, only this: **+6 dB is clearly louder, -6 dB is clearly softer**, and a change of 1 dB is hard to hear.

{{pv {"title":"Getting louder (a crescendo)","src":"seq","p":{"notes":[60,62,64,65,67,69,71,72],"step":0.5,"wave":"triangle","vel":[0.15,0.25,0.35,0.5,0.65,0.8,0.95,1]}}}}
{{pv {"title":"Getting softer (a diminuendo)","src":"seq","p":{"notes":[72,71,69,67,65,64,62,60],"step":0.5,"wave":"triangle","vel":[1,0.95,0.8,0.65,0.5,0.35,0.25,0.15]}}}}

## Dynamics

Words from classical music still describe this:

| Word | Meaning |
|---|---|
| piano (p) | soft |
| forte (f) | loud |
| crescendo | gradually louder |
| diminuendo | gradually softer |

A song that is always at the same volume is tiring. Change it: a quiet verse and a loud chorus feels like a story.

{{try Hear the contrast}}
Play the two previews above. Which one feels like "something is coming"? Which one feels like "the end"? That feeling is dynamics doing its work.
{{/try}}

{{deeper What a decibel really is}}
The decibel is a **ratio**, not an amount. It compares two levels on a logarithmic scale, because our ears work that way: to hear a sound as "twice as loud" you need about **10 dB more**, which is ten times the power.

| Change | Sounds like |
|---|---|
| +1 dB | hard to notice |
| +3 dB | clearly louder, double the power |
| +6 dB | much louder, double the signal height |
| +10 dB | about twice as loud |
| -10 dB | about half as loud |

In digital audio the top of the scale is **0 dBFS** (full scale), the loudest value that can be stored. Every other level is negative: -6 dBFS is half the maximum height, -20 dBFS is a quiet but healthy recording level. If a sound tries to go above 0, it **clips** and sounds harsh.
{{/deeper}}

{{deeper Velocity: how hard a note is played}}
On a keyboard, how hard you press decides how loud and how bright a note is. This is **velocity**, a number from 1 to 127 in MIDI. A song played with every note at 100 sounds like a machine. Real players vary: they press the strong beats a bit harder and the weak ones softer. Adding small velocity variations is one of the quickest ways to make programmed music feel human.
{{/deeper}}

{{ample Level is `gain` in dB, for example `gain -18 dB`. Fades change it over time.| volume, fade}}
