---
title: Drums and patterns
summary: Make a drum beat with steps, name your patterns and string them into sections.
---

# Drums and patterns

A drum beat in AMPLE is a grid. Each row is a drum (kick, snare, hat), each column is a **step**, and a click puts a hit on it or takes it away. Select the drums block, open the **Steps** tab on the right, and the grid is there.

![[shot:patterns-panel|The Steps tab of a drums block. The button marked 1 opens the same grid in a bigger view.]]

## The grid in the text

A pattern is also text. One row per drum, with `x` for a hit and `.` for a rest:

```
pattern kick:x...x...x...x... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
```

Every row has 16 steps, one bar. The kick above hits on every beat; the snare on beats two and four; the hat on every other step. A row of fewer steps repeats inside the bar, and a row of more steps goes on over the next bars: 32 steps are two bars.

| Mark | Means |
|---|---|
| `x` | a normal hit |
| `X` | an accent: harder |
| `o` | a ghost note: very quiet |
| `.` | nothing |

The sound comes from a **sample set**: a folder with `kick.wav`, `snare.wav` and `hat.wav`. Choose it in the **sample kit** field, or write `kit samples/kit`. Without a kit the drums block plays a built-in synthesised drum set, which is handy for sketching. It reads the same grids: kick, snare, hat, openhat, clap, rim, tom, lowtom, hightom and crash, and the grid in the Steps tab is there for it too.

## Slower or faster drums: steps

A bar has 16 steps, and a step lasts a sixteenth of a bar. When the song goes faster, so do the drums. The **steps** field changes that: it says how many steps a bar has. With `steps 8` every step lasts twice as long, so the same pattern plays at half the speed, whatever the tempo of the song is; with `steps 32` it plays twice as fast.

```
drums beat at 1 for 8 {
  pattern kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
  steps 8
}
```

The pattern above has 16 steps, but the bar has 8, so it lasts two bars. A grid that does not fit the bars (20 steps in a bar of 16) gets a warning. The guitar block has the same **steps** field for its strum.

## Several patterns and sections

One bar of beat gets boring fast. The drums block can keep **named patterns** and play them one after the other as **sections**. In the text:

```
patterns verse=kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x. / fill=kick:xxxxxxxxxxxxxxxx snare:x.x.x.x.xxxxxxxx hat:xxxxxxxxxxxxxxxx
song verse*4 fill chorus*4 fill verse*2
```

The first line defines the patterns (separated by `/`). The second is the **song** of the drum machine: play `verse` four times, then a `fill`, then four bars of `chorus`, and so on. `*4` means "this many bars".

In the panel you do not have to type this. The **pattern chooser** has New, Duplicate, Rename and Delete, and under it the **sections** are drawn as a small timeline: one coloured block per section, as wide as its bars, with a bar ruler above it. A section is a pattern with a number of bars. The names (such as `bare`, `fill`, `verse`) are whatever the song's writer chose; the same name always has the same colour, so repeats are easy to see. Click a block to edit its pattern in the grid below, drag its right edge to change the bars, drag the block to move it, or right-click it for Duplicate, Set the bars and Remove. With the keyboard, Alt+Left and Alt+Right move a block, + and − change the bars and Delete removes it. The **+** at the end adds a section. While the drums play, a thin line runs through the timeline.

## The Patterns view

Press the **Patterns view** button for a bigger grid, with every drum on its own lane and the sections below.

![[shot:patterns-view|The Patterns view: the pattern chooser, the grid with one lane per drum, and the sections.]]

> **Tip** Drag a sample from the project panel onto a lane in the Patterns view: that drum now plays that sample.

Build, and the whole sequence of sections plays in order.

![[shot:patterns-built|After the build the drums block has one take that holds all the sections in a row.]]

Next: how files in your project fit together, in Files and the project panel.
