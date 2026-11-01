---
title: Dividing the beat: triplets, tuplets and polyrhythm
order: 60
lead: What happens when you fit three, five or seven notes where two would normally go, and when two layers disagree on purpose.
---
## Triplets

A triplet fits **three** equal notes into the time of two. Count it "tri-pl-et". It is the basis of the twelve-pulse rhythms of the previous page.

{{pv {"title":"Two notes per beat, then three","src":"rhythm","p":{"bpm":120,"bars":2,"cycle":2,"layers":[{"n":91,"at":[0,0.5]},{"n":84,"at":[1,1.333,1.667]},{"n":67,"at":[0,1],"v":0.5}]}}}}

## Tuplets

Any number of notes can be squeezed into one beat: five (a quintuplet, counted "hip-po-pot-a-mus") or seven. The ear hears them as a quick rush or a ripple. Below: five notes in one beat.

{{pv {"title":"Five notes in one beat","src":"rhythm","p":{"bpm":120,"bars":2,"cycle":2,"layers":[{"n":91,"at":[0,0.2,0.4,0.6,0.8]},{"n":67,"at":[0,1],"v":0.5}]}}}}

## 3:2 polyrhythm

Two layers play different numbers of hits in the same time: **three against two**. They meet only at the start of each cycle. A well-known way to learn it is a sentence: **"Nice cup of tea"**. Say it slowly: "Nice" (both layers), "cup" (the three), "of" (the two), "tea" (the three).

`3:  x . x . x .`

`2:  x . . x . .`

{{pv {"title":"Three against two","src":"rhythm","p":{"bpm":120,"bars":2,"layers":[{"n":91,"at":[0,2]},{"n":79,"at":[0,1.333,2.667]}]}}}}

## 4:3 polyrhythm

Four against three works the same way with a longer sentence: **"Pass the god damn butter"**. "Pass" (both), "the" (the four), "god" (the three), "damn" (the four), "but" (the three), "ter" (the four).

{{pv {"title":"Four against three","src":"rhythm","p":{"bpm":120,"bars":2,"layers":[{"n":91,"at":[0,1,2,3]},{"n":79,"at":[0,1.333,2.667]}]}}}}

{{deeper Origin}}
Polyrhythm is at the heart of many West African traditions, where several drums and bells each keep their own cycle. Western classical music uses it too, for example in Chopin's Fantaisie-Impromptu, where the hands play four against three.
{{/deeper}}

{{ample Each layer is a series of `notes` at fractions of a beat (1.333 and 2.667 for three notes per bar). Two `synth` blocks with different pitches would let you shape each layer on its own. | midi-clip, synth, /learn/ample/basic/bars-and-time=Bars and time in AMPLE}}
