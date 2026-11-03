# AMPLE Jam (the Lite piano roll, 2026-10-20)

> Renamed on 2026-10-20: this was called "MIDI Loop Studio" (plan folder, file name and block ids keep the old name).

AMPLE Jam is the piano roll of AMPLE: a non-modal floating panel for editing a `midi-clip`. (Until 2026-10-21 there was also a heavier "Classic" roll; everything it did is
in Jam now and it has been removed.) Every gesture is one ordinary, undoable edit of the clip's `notes` text through the same store, so there is **one undo history**; the text
stays the truth. 
The FL-style `sequencer` block (a step sequencer block of its own) is a different thing; the Grid view is only another view of one clip's notes.

## Opening it

- Select a `midi-clip` and press **AMPLE Jam** (or **Track view**, which also shows the arrangement, sound and automation lanes). A clip written with `chords` or a `melody` is first
  written out as notes after you have seen how many notes that makes (one edit; Undo brings the chords back). A **MIDI** block opens **Show notes** read-only, with *Make an editable
  copy as notes* in the menu.
- **The lower lane** under the notes edits one value per note: velocity, pan, fine pitch, release or cutoff (the choice at its left). Drag a bar, Shift+drag for a ramp, double-click to reset.
- **The ☰ menu** folds its tools into groups that open one at a time: *On the selected notes* (reverse, speed, flip pitch, legato, staccato, remove overlaps, strum, chopper, humanize),
  *Create* (arpeggiate, riff machine, stamp a chord, hum a melody), *Scale* (root and scale, snap, fit, guess the key), *Colour groups and properties* (group colour tabs, show one group,
  group MIDI channel, exact bar/beat/length/pitch/velocity, slide and portamento), *Lanes and clips behind* (arrangement, sound and automation lanes; the other clips as faint
  outlines, copy them, chord lock). Settings open as a small card in the window; Enter applies and Esc closes. Edit as text and *Play the built song from here* are in the menu too.
- A small corner dot on a note marks slide or portamento (set it in *Properties of the selected notes*).
- **The view memory is per browser, not per project folder** (`ample.rollview:<project>:<song>` in the browser's storage): the view a clip opens in,
  zoom, scroll, snap, loop bracket, Roll/Grid mode and panel position. It never creates a song edit or an undo step and does not travel with the project.

## Window management (2026-10-20)

The panel is a small window. **Double-click the title bar** or press **–** (Collapse) to roll it up to its title bar (it can still be dragged; do it again to expand). The window slides sideways so the mouse stays on the title bar (or on the button): a second double-click in the same place expands it again, back to exactly where it was. The collapse button shows a longer ▁ while the window is rolled up. **▢** (Maximize) makes it a locked-in full-screen window on a dimmed backdrop; **Esc** or **❐** (Restore) leaves it and the previous place and size come back. Not shown on a phone, where the panel is already full screen. Code: `web/app/window-chrome.js`.

## Sequencer channels (LSQ, 2026-10-20)

A note channel of a `sequencer` pattern (the rows of an `.ampleseq` or an embedded sequencer, e.g. the Mozart K525 song) has a **🎹 AMPLE Jam** button in the sequencer workspace. It edits the same row text and writes it back into the pattern; **Classic** and **Lite** switch the row both ways. Differences from a clip: no neighbour strip and no remembered view (a row is not a block); Ctrl+Z / Ctrl+Shift+Z undo inside Lite only (every commit keeps the previous text), the sequencer's own Undo still steps through the same writes; drum channels (step rows) have no roll.

## Roll view

Toolbar order: transport (Play/Pause, Stop, Loop, Follow, metronome), Classic, Roll/Grid, seven tools (Draw P, Paint B, Delete D, Slice C, Select E, Playback Y, Zoom Z),
typing keyboard, MIDI, snap, zoom. Below it the optional neighbour strip, the ruler with the loop bracket, the grid with the piano keys, and an
always-on velocity strip.

| Gesture | Effect |
|---|---|
| Click / drag on empty grid (Draw) | add a note; a plain drag stretches it; length = last touched note, else the snap step |
| Drag a note body / its right edge | move / resize (several selected notes together); resizing across a bar line stores tied pieces |
| Shift+drag a note | clone it |
| Right-click, right-drag | delete |
| Ctrl+click, Ctrl+drag (Select) | select, rubber band; Ctrl+Shift+click adds; Ctrl+drag on the ruler selects the notes in a time range |
| Alt (held) | draw and drag off the grid (the snap box says `free`) |
| Alt+wheel on a note, Ctrl+Alt+wheel | velocity (Shift = 5 steps), fine tune; one edit shortly after the last step |
| Wheel, Shift+wheel, Ctrl+wheel, Alt+wheel on empty grid | scroll pitch, scroll time, zoom time, zoom rows (anchored at the pointer); middle-drag pans |
| PgUp / PgDn, Shift+1..3, Shift+4, Shift+5 | zoom, zoom levels, fit the clip, zoom to the selection |
| Ctrl+C / X / V | copy, cut, paste (at the pointer if over the grid, else at the playhead); every field is kept. Copy also puts the notes on the system clipboard as note text |
| Shift+Ctrl+V | paste note text from the system clipboard (the "MIDI clipboard": text copied from another clip, the Classic roll or any app) at the same place; text that holds no notes pastes nothing |
| Ctrl+B | duplicate the selection to the right |
| **Ctrl+D** | **deselect** (FL). In **Classic Ctrl+D still duplicates** |
| Ctrl+Q, Ctrl+Alt+Q | quick quantize the selection (or all notes) to the snap, 1/16 when snap is none; running it twice equals once |
| Shift+arrows, Ctrl+Up/Down, Alt+Left/Right | move the selection by a step / semitone, by an octave, nudge by 1/48 beat |
| Backspace, Delete | snap on/off, delete the selection |
| Space, Ctrl+Space | start/stop (stop returns to where play began), play/pause |
| Ctrl+T, `[` `]`, Alt+NumPad 2-6 | typing keyboard on/off, octave |
| Paint tool, Shift held | monophonic: each step column keeps one note, moving to another pitch in the column replaces it |
| Playback tool (Y) | a click starts playing from the snapped position (a playing roll jumps there); right-click stops |
| Zoom tool (Z) | drag a box to fill the view with it, click to zoom in around the pointer, right-click to zoom out; edits nothing |
| `?` | shortcut card (Esc closes only the card) |

Ruler: a click sets the playhead; a drag creates or edits the **loop bracket** (a Lite extension; FL selects a time range there); right-click clears,
double-click resets it to the whole clip. Edits made while playing are heard on the next lap. The playing notes and piano keys light up; pressing a
piano key or a typing-keyboard key plays that pitch (nothing is written). The **MIDI** button (where the browser has Web MIDI) only listens.

## Grid view (step sequencer)

The Roll/Grid toggle shows one row per sound and one cell per step over the **same notes**:

- Rows: the pitches present plus the rows Grid already showed; an empty clip starts with seven GM drum rows; **+ Row** adds any GM percussion row
  (added rows are not stored in the song).
- Step = the current snap grid (1/16 when snap is none). A note longer than a step fills every cell it covers (the later cells are drawn lighter and joined);
  clicking any covered cell clears the whole note. A note that starts off the grid shows in the cell it covers.
- Click toggles a step (new velocity = last used, 96 at first); a drag repeats what its first cell did, as one edit; Shift+click or Shift+drag
  accents (127); right-click or right-drag clears. The plain wheel over a step changes its velocity (this differs from Roll, which needs Alt).
- **M box** at the right of a row label mutes that row while playing (and for Play selected). It is a playback setting of this browser, remembered with the
  view memory; it never changes the song text and never silences the audition of a step you draw.
- **Alt+wheel** makes the rows taller or shorter (14 to 56 px) around the pointer; the height is remembered per clip like the other view settings.
- The tool strip is hidden in Grid. Ctrl+Q works in both views.

## Neighbouring-clips strip

A strip under the toolbar shows a chip for every other **placed** `midi-clip` part within 8 bars of the open clip (AMPLE
has one lane per block and an instrument takes one clip, so "same track" cannot be read, hence any lane). Clicking a chip opens that clip in place
(same position and size; the current panel closes) in its remembered view. **Back** (chip) or Alt+Left returns; with notes selected Alt+Left stays
the nudge. The strip edits nothing and hides when there is nothing to show.

## Not built / deliberate limits

- Recording onto the grid is **MLS09, not scheduled**. Pre-decided options if it is: reuse the FL block's record from a MIDI keyboard (FL07), count-in,
  independent metronome click, record-arm, overdub/replace, no auto-quantize by default with an optional snap-while-recording toggle, one commit per take.
- No per-clip meter: the grid and bar length stay 4/4 (PR22 is open). The "+ bar" control only extends the *view*, not the clip.
- The note **mute tool (T)** is not built: it needs a text-format key plus engine and reader support (plan decision 22, option A). Ghost notes are
  Classic-only; Ctrl+Left/Right time selection is not in Lite.
- **Shift-stretch** (FL: Shift+drag sets the note length) is not built because Shift+drag already clones a note (plan decision 32). A plain drag on empty
  grid already stretches the new note.
- The duplicate rule covers the whole span of a tied note: drawing on a tail piece does nothing, a move, clone or paste that lands a piece on another
  note's piece replaces that note, and a growing note stops before the next piece at its pitch.

## What is unverified

Tests are DOM and unit tests only (`web/lib/test/lite-*.test.mjs`, `spec-tests/parity/piano-roll-lite*.spec.ts`, including the scripted 12-step session and
a seeded UI fuzz in `piano-roll-lite-acceptance.spec.ts`, plus the two optional steps: an instrument setting changed while the roll is open, and drawing past the clip end). No sound is heard by any test. **Not verified by tests:** real audition
latency against the 40 ms / 15 ms budgets (and any Bluetooth delay), the loop seam by ear, drag smoothness, cursors, wheel feel, scale tint,
the hand-over between the modal Classic dialog and the non-modal Lite panel, and every FL "same/differs" verdict in `PARITY.md`, which the planner
could only check against the FL manual, not a running FL. Open decisions: view memory in browser storage, Split leaves Classic closed, Ctrl+Q alongside Ctrl+Alt+Q,
Alt+NumPad octave digits, draw drag stretch vs move, the Grid and strip rules above, and the meter per clip (PR22).
