---
title: Frequencies and EQ
order: 90
lead: Every sound lives in a band of low, mid and high. EQ lets you turn bands up and down.
---
## The bands

| Band | Range | What lives there |
|---|---|---|
| sub and bass | 20-250 Hz | kick, bass, the "weight" |
| mids | 250 Hz-4 kHz | voices, most instruments |
| highs | 4-20 kHz | air, hats, "sparkle" |

## EQ

An **equaliser** boosts or cuts a band. Cutting is usually safer than boosting.

{{pv {"title":"Bright: a boost around 4 kHz","src":"drums","fx":"eq","p":{"hz":4000,"db":12}}}}
{{pv {"title":"Muffled: low-pass filter closing","src":"pad","fx":"filter","p":{"from":6000,"to":300}}}}

## Why it matters

Two sounds in the same band fight for space. If the bass and the kick both want 60 Hz, one must give way. That is what **mixing** is mostly about.

{{try Take something away}}
Play the bright preview, then the muffled one. Which words come to mind? "Open", "dull", "thin"? Giving a sound words is how you learn to EQ by ear.
{{/try}}

{{deeper A cheat sheet per instrument}}
| Range | What lives there | What to do |
|---|---|---|
| 20-60 Hz | sub (kick body, deep bass) | keep for kick and bass only |
| 60-250 Hz | bass, warmth | too much: muddy |
| 250-500 Hz | "boxy" body | cut a little if it sounds cloudy |
| 500 Hz-2 kHz | the presence of voices and guitars | too much: honky |
| 2-5 kHz | clarity, attack | too much: harsh |
| 5-10 kHz | brightness, hi-hat sizzle | add for air |
| 10-20 kHz | air | a light shelf can open the sound |
{{/deeper}}

{{deeper High-pass everything that is not bass}}
Most instruments have rumble below their useful range. A **high-pass filter** (it lets highs pass, cuts the lows) at 80-120 Hz on anything but the kick and bass cleans the low end. It frees space and makes the bass stronger without touching it.
{{/deeper}}

{{ample The `eq` block boosts or cuts a band, `filter` removes it. | eq, filter}}
