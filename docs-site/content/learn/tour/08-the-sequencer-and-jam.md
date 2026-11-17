---
title: The sequencer and AMPLE Jam
order: 65
lead: Beats and whole arrangements from patterns and a playlist, and a piano roll that opens on any row.
---
A `sequencer` block plays a **sequence**: channels (the voices), patterns (a few bars of what every channel plays) and a playlist (which pattern plays where). The sequence is a small text file that ends in `.ampleseq`, or it is kept inside the song itself. Open the block's menu and choose **Open the sequencer**, and the workspace below opens. The pictures on this page show the first part of Mozart's *Eine kleine Nachtmusik*: five string voices, seven patterns.

{{shot sequencer-workspace | The sequencer workspace on a song with five voices | Channels on the left; the patterns, their rows and the playlist on the right.}}

## Channels

Each voice is a **channel**: a drum with a sample, a notes channel that plays a built-in tone or a sample set, a General MIDI instrument, a guitar, or a loop. A channel has a colour, a name, a type, mute and solo, a level in dB and a pan. The colour follows the channel everywhere: the stripe in front of its rows, the outline of its notes in AMPLE Jam.

## Patterns

A **pattern** is a stretch of bars, with one row for every channel that plays in it. The tabs above the rows are the patterns; the small number on a tab is how many notes the pattern holds, so you can see at a glance which parts are busy. The line under the tabs names the pattern you are in, with its bars and notes, in the colour of the pattern.

{{shot sequencer-patterns | The pattern tabs and the rows of one pattern | A drum row is a row of steps. A notes row shows a small picture of its notes, so a busy voice and a silent one look different.}}

- **New**, **Duplicate** and **Variation** make a new pattern. A variation starts with the rows of its base and changes only what differs.
- **Double-click** a tab to rename it, **right-click** it for the actions, and **drag** it to another place in the row.
- **Drag a tab onto a track of the playlist** to put a clip of that pattern there.
- While a pattern plays, a thin **playhead line** runs over the bars of every row of that pattern, in the sequencer window as well as when the whole song plays.

## The playlist

The playlist puts the patterns in order on the timeline. Click an empty spot on a track to put the chosen pattern there, drag a clip to move it, and drag its right edge to make it longer (the pattern repeats). The clips of the pattern you are working on stay bright and the others are dimmed. **Fit** zooms so that the whole song is in view.

{{shot sequencer-playhead | The playhead line in the rows | While a pattern plays, a thin line runs over the bars of every row.}}

{{shot sequencer-playlist | The playlist of the whole movement | Seven clips in a row on track 1; every track has its number, and a track without clips is lower.}}

Drag over the bar numbers above the playlist to choose a part to repeat, and press **Play**. With **Loop** on, the chosen part (or the whole song) repeats. With **Live** on, a change you make while it plays comes in at the next loop. **Space** plays and stops.

## AMPLE Jam

A row of notes has a **Jam** button (the logo with a J). It opens AMPLE Jam, a piano roll on exactly the notes of that channel in that pattern: draw, paint, slice and hear them as you go. What you do is written back to the row when you apply the sequencer.

{{shot jam-roll | AMPLE Jam on one voice, with the other voices behind it | The bright notes are the voice being edited; the outlines are the other voices of the pattern, each in its own colour.}}

Press **Jam all voices** in the line of the pattern to open the Jam on the first voice with notes: the other voices show behind it as outlines in their own colour, so you can write a line that fits the others. A click on an outline opens that voice.

While the song plays, a second playhead in the colour of the brand runs over the roll and the roll scrolls along, so you see where the song is while you write. It disappears when the song stops. The lyrics editor marks the line that is being sung in the same way.

The lane under the roll has a **velocity scale**: faint lines with their values (a quarter, a half, three quarters and the top of the range), so you can read a bar and drag it to the value you want. Dragging a bar still sets the velocity of that note.

The **Grid** view shows the same notes as one row per sound, one cell per step, shaded by velocity.

{{shot jam-grid | AMPLE Jam in its Grid view | One row per sound, one cell per step.}}

{{try Write a second voice}}
Open a pattern in the sequencer, press **Jam all voices**, and look at the outlines of the other voices. Pick a different voice from the channel list, open its Jam, and write a few notes under the line you can see. Press **Play** with **Loop** on and **Live** on, and change a note while it plays: you hear it the next time the loop comes round.
{{/try}}
