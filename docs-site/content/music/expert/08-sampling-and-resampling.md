---
title: Sampling, pitch and reverse
order: 70
lead: Record a sound and play it back changed: higher, lower, backward, in pieces.
---
## Sampling

A **sample** is a recording used as an instrument. A single recorded note can be played at many pitches by playing it faster (higher) or slower (lower).

{{pv {"title":"A melody, then a fifth higher","src":"pluck","fx":"pitch","p":{"semitones":7}}}}

## Reverse

Playing a sound backward gives swells: the sound grows, then stops sharply.

{{pv {"title":"A plucked melody, reversed","src":"pluck","fx":"reverse"}}}

## Chop and granular

**Chopping** cuts a recording into pieces and rearranges them. **Granular** processing turns a sound into tiny grains (tens of milliseconds) and plays them as a cloud.

{{deeper Why faster is higher}}
If you play a recording at twice the speed, every wave arrives twice as often: the frequency doubles, which is one octave up. That is also why a tape played too fast sounds like a chipmunk.
{{/deeper}}

{{deeper Time-stretching versus pitch shifting}}
If you play a recording faster, it gets higher; slower, lower. That is the plain way and changes both at once. Modern tools can separate the two: **time-stretching** changes the length without changing the pitch, and **pitch shifting** changes the pitch without changing the length. They split the sound into very small pieces and rearrange them. The more you change, the more artefacts you hear, a "robotic" or "watery" quality, which is sometimes used on purpose.
{{/deeper}}

{{deeper Looping and finding loop points}}
A sample loops without a click when it starts and ends at the same level, ideally where the wave crosses zero. A crossfade between the end and the start hides the join. For a rhythmic loop, cut at an exact number of beats so the loop stays in time with the song.
{{/deeper}}

{{deeper Granular in practice}}
A grain is a very short piece of sound (5 to 100 ms). A granular tool plays many grains at once, choosing where in the sample each one starts. Slow position changes give a frozen, shifting texture. Random positions give clouds. It is a way of making new sounds from old ones, and a fast route to atmospheres.
{{/deeper}}

{{ample `sampler`, `pitch`, `reverse`, `chop`, `granular`. | sampler, pitch, reverse, chop, granular}}
