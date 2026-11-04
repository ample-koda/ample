---
title: Bars and time
summary: How a song is counted in bars and beats, and how to place a block in time.
---

# Bars and time

Music is counted in **bars**. A bar is a small chunk of the song with a steady number of **beats** in it. In AMPLE every bar has four beats (you count "one, two, three, four, one, two..."). A song of eight bars has 32 beats.

How fast the beats go is the **tempo**, in beats per minute. The song of this guide has a tempo of 100: one hundred beats in a minute.

## From bars to seconds

If you know the tempo, you can work out how long a bar lasts:

| Tempo | One beat | One bar (4 beats) | Eight bars |
|---|---|---|---|
| 60 | 1 second | 4 seconds | 32 seconds |
| 100 | 0.6 seconds | 2.4 seconds | 19.2 seconds |
| 120 | 0.5 seconds | 2 seconds | 16 seconds |

The rule: **a bar takes 4 × 60 ÷ tempo seconds**.

## Seeing the bars

Open the **Arrangement** tab at the bottom of the window. It shows the song along a line of time. The numbers on the ruler on top are the bars, and the seconds are written underneath.

![[shot:bars-timeline|The Arrangement: bars on the ruler, seconds under it, and one row for each block.]]

Every row is a block, and the coloured bar in it shows during which bars the block plays. Click the ruler and drag to choose some bars; the header then offers to play just those.

## Placing a block in time

In the text, a block is placed with `at` and `for`:

```
bass bass stable@1.1 at 1 for 8 {
```

This reads: start **at bar 1** and last **for 8 bars**. A block that has no `at` simply takes the whole song. The first bar is bar 1, and bar 5 begins right after bar 4 ends.

You can give a block several places, and name them:

```
piano piano pinned@2.0 at 1 for 8 as verse, 9 for 8 as chorus {
```

The piano now plays in two stretches, bars 1 to 8 and bars 9 to 16. Those named stretches are what music calls **sections**.

![[shot:bars-source|The Source view: the first lines of the song with their bar numbers.]]

> **Try it** In the Source view, change `for 8` of the bass to `for 4`, press Apply, and look at the Arrangement: the bass row is now half as long. Build and listen to what changed.

## Steps: bars inside a bar

For drums a bar is cut into **16 steps** (you can change that with the **steps** field of a drums block, see Drums and patterns). Each step is a sixteenth of a bar, so every beat has four of them. In a drum pattern each step either has a hit (`x`) or stays quiet (`.`):

```
kick:x...x...x...x...
```

This kick hits on steps 1, 5, 9 and 13: once on every beat.

Next: what happens when you press Build, Play and Stop, in Build, play and stop.
