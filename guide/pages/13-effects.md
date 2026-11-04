---
title: Effects
summary: Shape a sound with an EQ, a delay, a reverb and more, and hear what each one does.
---

# Effects

An **effect** is a block that sits in the cable between a sound and the mixer and changes the sound that goes through. Put a reverb after a piano and the piano seems to stand in a room; put an EQ after a bass and you make it warmer or thinner.

![[shot:effects-graph|A bass goes through an EQ, a delay and a reverb on its way to the mixer.]]

Read the chain from left to right: the bass block makes the sound, `tone` (an EQ) shapes it, `echo` (a delay) repeats it, `room` (a reverb) puts it in a space, and the mixer takes the result.

## Add an effect

- Press the **+ EQ** button on a block that makes sound: AMPLE adds an EQ block after it, connects it and opens its controls.
- Or add any effect from the **Library** on the left, and connect it yourself: the sound goes into its `in` port and comes out of `out`.

## The advanced controls

Every effect has an **Advanced** button that opens a panel with knobs and a picture of what the effect does.

![[shot:effects-advanced|The EQ panel: three bands (low, mid, high) with the corner frequencies, and the approximate response curve.]]

Turn a knob, and the curve follows. To listen while you turn, use **Build and use input**: it makes the sound of the block before the effect available in the panel, so you hear the effect on it.

## The usual effects

| Effect | What it does | Its main settings |
|---|---|---|
| **EQ** | turns the low, middle or high of a sound up or down | `low`, `mid`, `high` in dB |
| **Filter** | removes the highs (lowpass) or the lows (highpass) | `type`, `cutoff` in Hz |
| **Delay** | repeats the sound after a short time | `time` in ms, `feedback`, `mix` |
| **Reverb** | puts the sound in a room | `size`, `decay`, `mix` |
| **Compressor** | evens out loud and soft parts | `threshold`, `ratio`, `attack`, `release` |
| **Distortion** | makes the sound rougher | `drive` |

Many effects (the delay, the reverb) have a **mix** setting: how much of the changed sound you hear next to the original. An EQ or a filter has no mix: it changes everything that goes through. A good rule is to start low and turn it up until you notice it, then back a little.

## Switch an effect off

`enabled off` keeps the block in the song but lets the sound pass untouched. It is the quickest way to check whether an effect improves anything: switch it off, build, listen.

## In the text

```
eq tone stable@1.0 {
  low 4 dB
  from bass.audio -> in
}
delay echo stable@1.0 {
  time 375 ms
  feedback 35%
  mix 28%
  from tone.out -> in
}
reverb room stable@1.0 {
  size 45%
  decay 50%
  mix 22%
  from echo.out -> in
}
```

Next: the whole song on one timeline, in The arrangement.
