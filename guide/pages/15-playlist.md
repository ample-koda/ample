---
title: The Playlist
summary: See every sound of the song as a line with clips, and arrange, route and mix them from one place.
---

# The Playlist

The **Playlist** is a second way to look at the same song. Where the Arrangement has one row for every block, the Playlist has one **line** for every sound you hear: an instrument, the effects after it, and the bus it plays into. Open a line and you see its whole chain.

It is switched off until you want it. Open the **?** menu at the top right and turn on **Playlist view (beta)** (on a narrow screen it is in the **More** menu, the dots). A new **Playlist** tab appears next to **Arrangement**. Turning it off again changes nothing in your song.

## Reading it

Each line has a name on the left, the clips of its sound on the right, and a small status that says whether it is built. A line that has two sources, such as a kick that changes halfway, shows both as clips on one line. Lines that belong together sit under a group heading; a group you made in the node editor is the same group here.

Under the lines are the blocks that every sound passes through at the end (the mix bus and the master), and the waveform of the built song. Blocks that do not reach the mix are listed under **Loose blocks**, with the reason, so nothing is ever hidden.

A thin strip above the rows shows the whole song with its sections; click or drag in it to move around, or use the arrow keys when it has the focus. The ruler counts bars and seconds. It follows the meter of your song, so a song in 3/4 has shorter bars. Drag over the ruler to choose some bars, hold Shift to loop them, and click once to move the playhead.

## Changing the song

Everything you can do here writes the same text you could write yourself in the **Source** tab, and one gesture is one undo step.

- **Move a clip** by dragging it. **Resize** it by its right edge. With the keyboard, the arrow keys move a selected clip by one step and Shift with the arrows resizes it. Choose the step in **Snap**.
- **Draw a clip** by dragging over an empty stretch of a line.
- **Right-click a clip** to split it, repeat it after itself, copy it as a new block, move it to another line, or delete it. A second part of a block plays the block again from its start, so build and listen after a split.
- **Open a line** with its arrow to see the chain. Right-click a block of the chain to put an effect in after it, take it out, move it a step, or switch it off.
- **Right-click a line** to rename it, move it up or down, route it to another bus, add a send, group it with other lines, or remove it. **+ Line** adds a new sound on its own bus.
- **Right-click the ruler** to make a **section** (Intro, Verse, Drop). Sections show in the Arrangement too.

> **Note** A name you give to a line, a section, and the order of the lines are kept at the end of your song, in a `view` block. They never change how the song sounds.

## Drums, sequencers and modules

Open a line that has a **drums** block with a song (`song verse*4 fill`) and a row under it shows the patterns as coloured sections, with the same colours as in the drums editor. A **sequencer** block shows one row for each of its lanes, with the clips of its own playlist. Right-click a **module** and choose **Open the module** to see its own lines; the **← Song** button at the left of the toolbar brings you back.

A parameter that has an automation (a curve such as `cutoff auto(1:1800, 33:3800)`) gets a row of its own with the curve drawn over the bars. Click it to open the curve editor. Right-click a block of the chain and choose **Automate a parameter** to start one.

You can change the patterns here too. Drag the right edge of a drums section to give it more bars, drag it to move it, or right-click it to repeat it, give it another pattern, or remove it. In a sequencer lane, drag a clip to move it, right-click it to change its pattern or delete it, and right-click an empty stretch to add one. A clip that plays several lanes sits on all of them and moves with all of them.

The strip at the bottom shows a small graph of the line you picked: the notes, the sound, the effects and the bus as boxes from left to right. A sidechain shows as a dashed line from a box above. Click a box to pick that block.

Below the rows, the strip lists the lines that the last change removed and added, so you can see what a gesture wrote into the song. Hold Shift and drag over an empty stretch to pick several clips, which then move together.

## Routing

Press **Routing** to see how the lines connect. A cable runs from each line to the bus it plays into. Dashed cables are sends and sidechain keys. Drag the round port of a line onto another bus to re-route it. If the new connection is refused, the old one stays.

## Mute and solo

**M** and **S** on a line only change what you hear in the preview. Press **Hear it** in the banner to play the lines that are not muted. The preview is a quick mix in the browser, so it does not have the master processing; the build never uses mute or solo.

> **Tip** The Playlist is for working on a whole song. For one sound, the node editor and the piano roll are still the place.

Next: the keys that make all this faster, in Keyboard shortcuts.
