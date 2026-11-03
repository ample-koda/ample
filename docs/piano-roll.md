# Piano roll (AAA epic, 2026-10-14)

Covers `web/app/pianoroll.js` (items PR01-PR22 of the piano roll). The text stays the truth: every
gesture below is one ordinary edit of the clip's `notes` parameter (or, for the humanize block and arrangement parts, of another parameter or
block), always undoable. Partial features and engine limitations are called out where they apply.

The lighter companion editor, **AMPLE Jam** (Lite, same notes and undo), is described in [midi-loop-studio.md](midi-loop-studio.md). In Lite Ctrl+D deselects; here it still duplicates.

## Playback and long notes (2026-10-18)

Play/Space starts at the independent playhead, initially the clip start. Selecting or drawing notes and scrolling no longer change that position.
Click the ruler to seek; dragging it still pans. Alt+Play or Alt+Space explicitly previews from the selection. Play again pauses; Stop returns to
the clip start. Loop repeats the complete clip, including when playback starts halfway through it, and voices are cut at the loop seam.

Dragging a note edge can extend over several bars; the roll displays contiguous tied pieces as one note and resizes or deletes that whole note.
The saved text has one valid entry per bar of the song meter (4 beats in 4/4, 3 in 3/4, and so on). `;tie=N` joins pieces without forcing an instrument program/channel; legacy routed tie
fields still work. Other position edits of tied notes remain source-only. The right edge has a visible grip and can be grabbed even on short notes.
The roll follows the meter of the song (`meter N/D` on the composition line). A meter per clip (PR22) is not built; the ruler-grouping selector still changes the view only.

Play and hear-notes resolve the connected instrument through supported MIDI effects. Synth, built-in piano/bass, one-shot sampler and supported
plain WAV sample sets reuse the local preview renderers; GM uses the configured SoundFont audition endpoint. Seeded MIDI effects use the exact
effective seed. The label identifies instrument preview versus the sketch fallback; both are approximate listening previews, not stored renders.
Cold or unsupported voices use the audible sketch while supported voices warm in a bounded cache. Unsupported expression/sets remain explicit
fallbacks. This does not preview downstream audio effects or promise byte identity with a build. Closing the roll cancels local workers and playback.

## Rendering (PR01)

The three canvases (notes grid, arrangement/automation lanes, sound row) scale their backing store with `devicePixelRatio` (`sizeCanvas()`), so
they stay crisp on high-DPI screens; a `window` resize listener re-sizes them while the roll is open. Redraws go through a single
`requestAnimationFrame` dirty loop (`requestDraw()`), so a burst of drag/wheel/resize events collapses into one draw per frame. Notes are culled
to the visible time range by a binary search on a sorted-by-start index (`sortedRows`/`visibleRows`) before the per-note pixel check, so a very
large clip only pays for the notes actually on screen. `dialog.dataset.drawMs` reports the last frame's draw time (PR13's frame-time probe;
report-only, nothing fails on it). Canvas sizing and note glyph drawing live in `pianoroll-draw.js`; pointer coordinate conversion and note hit
testing live in `pianoroll-input.js`. The dialog keeps gesture lifecycle and drawing composition with the shared model, undo and selection state.

## Look (PR02, PR03)

By default a note is a vertical gradient keyed on velocity (cool blue to hot amber, `web/lib/pianoroll-theme.mjs`), with a 1px top highlight and a
glow when selected. The "high contrast" toggle restores the previous flat, solid fill (no glow), for low-vision or reduced-motion use.

A clip's `scale` parameter (`"c:minor"`, `"f#:dorian"`, ..., or `"none"`, parsed by `web/lib/scales.mjs`) drives: a root+scale selector in the
toolbar, row shading (out-of-scale rows dimmed, not hidden), a glowing border on root rows, a thin red outline on out-of-scale notes, a "Fit to
scale" button (moves the selection's out-of-scale notes to the nearest in-scale pitch, one edit), and a click-to-use Krumhansl-Schmuckler key
guess (`detectKey()`; never applied without a click). The scale table also lives in `spec/scales.json` (kept in sync by hand; the C# engine/LSP
side does not read it yet).

## Gestures (PR04)

| Gesture | Result |
|---|---|
| left-drag on empty grid | draw a note (length by drag); a plain click uses the last-used length |
| left-drag on a note / its edge | move / resize |
| Shift + left-drag on empty grid | brush: paints notes of the last-used length, one per grid step, never stretching |
| right-click on a note / right-drag | delete / eraser sweep (one undo step for the whole drag) |
| double-click a note | note properties popover (pitch, bar:beat, length, velocity, slide) |
| double-click in the lower lane | reset the nearest note's lane value to its default |
| double-click a ghost note | switch the roll to that clip (PR05) |
| wheel on a note | velocity +-1 (Shift +-5) |
| Ctrl+wheel / Alt+wheel / Shift+wheel | zoom time / zoom rows / scroll sideways (unchanged) |
| Ctrl + drag on empty grid | rubber-band selection (replaces the old Alt/right-button binding) |
| `,` / `.` | select the previous / next note in time |
| Delete/Backspace, arrows, Shift+arrows, Ctrl+A/C/X/V/D | as before |
| `S` | toggle Slide on the selection (not while step input owns `s` as a piano key) |
| Alt+U / Alt+S / Alt+R | hi-hat chopper / strum / configure humanize |
| Alt+Left | return from a ghost focus-switch |
| `?` | shortcuts card |

**Hum…** records and reviews a sung line, then converts the parsed note entries to the roll's absolute-time note values before adding them to the clip. Detected notes are estimates; correct them in the roll before building.

Not implemented: Alt+wheel fine-pitch on a note (needs PR06's per-note fine field to matter to the ear first; the data field exists, the gesture
does not yet), the "Unlimited Paint" brush drawing a *ramp* rather than a straight paint (shift+drag always uses one fixed length, not a stretch).

## Ghost notes and focus-switch (PR05)

Other midi-clip blocks whose arrangement parts overlap this clip's are drawn as faint read-only outlines under this clip's own notes (toggle:
"ghosts"), read from their `notes` text or rendered from `chords`/`melody` (own ppq and part offset are converted into this clip's tick/bar space).
"Ghost clips" has one visibility checkbox per source; hidden ghosts cannot be focused or copied. Double-clicking a shorthand ghost opens its
conversion prompt before editing.

## Per-note properties (PR06)

Entry grammar: `bar:start:length:pitch:velocity[:channel:bank:program[:tie]]` followed by zero or more `;key=value` pairs:

| Key | Range | Meaning |
|---|---|---|
| `pan` | -100..100 | stereo position of this note's voice |
| `fine` | -100..100 cents | fine pitch offset of this note only |
| `rel` | 0..5000 ms | release time of this note (no default: unset means the instrument's own) |
| `cut` | 0..100 % | low-pass cutoff (100 = no filter) |
| `slide` | 0 or 1 | this note glides the previous note's voice to its pitch (PR08) |
| `port` | 0..2000 ms | portamento time into this note from the previous one |

A value equal to its default (0, 0, -, 100, 0, 0) is never written, so a clip stays byte-identical until a property is actually used. Unknown
`;key=value` pairs are kept verbatim by the roll's own model (`web/lib/notes-model.mjs`) and re-emitted unchanged; the C# reader
(`StructuredMidiNotes.cs`) and the JS validator (`expanded-midi-notes.js`) agree on the same grammar and ranges, and the midi-clip renderer passes
the values through into its JSON note objects. The gm/SF2 renderer honours **pan**, **fine**, **rel** and **cut** in sound (see PR07 below);
**slide** and **port** are handled by the GM/SF2, synth and pitched sample-set renderers; one-shot sampler behavior remains unchanged. The language server completes these keys after `;`; invalid values produce AMP2012.

Edited MIDI-module export maps pan to CC10, fine pitch to pitch bend after setting the bend range to one semitone by RPN, release to CC72 and cutoff to CC74. Each distinct combination uses a free channel, separate from retained source channels; export fails if no free channel remains. Pan uses 0..127, release maps 0..5000 ms linearly to 0..127 and cutoff maps 0..100 linearly to 0..127. Import reconstructs values at each note start. Portamento maps to CC65/CC5 on an isolated channel. Slide notes export as a 32-point pitch-bend ramp plus a private AMPLE marker so reimport can restore the editable slide note; other MIDI applications can play the ramp and ignore the marker. These MIDI controls remain lossy representations.

## Lower lane (PR07)

A switcher (velocity / pan / fine pitch / release / cutoff) under the grid draws one stem per note. Pan and fine pitch are centred lanes (a value
draws up or down from a midline); the others draw from the baseline like velocity always did. Dragging re-picks the nearest note at the current x
on every move, so one drag paints a freehand ramp across several notes (one undo step). Shift-drag draws an exact straight line between the
first note and the pointer, recalculating every note in that span from the original values; double-click resets the nearest note to the lane's default.

**Engine:** `src/Ample.Audio/Engine/SamplerRenderers.cs`'s SF2 note player (`PlaySf2Note`) sets a note's resting pan (CC10), pitch bend (`fine`,
within the renderer's fixed +-200 cent bend range) and brightness/cutoff (`cut`, CC74) from its own values, reusing the exact mechanisms already
built for controller-lane automation; `rel` replaces the SF2 region's own release time outright when set. An actual CC10/CC74/PitchBend event in
the song still overrides the resting value from the frame it occurs, unchanged. Synth renderer v2 also applies pan, fine pitch, release and a per-note low-pass before its master filter. Sample-set piano, bass and guitar apply pan/fine/release/cutoff during sample playback and use integer pitch paths for slides. Drums and the one-shot sampler keep their existing clip-level sample behavior.

## Slide notes (PR08, GM/SF2 audio)

A note's `slide` flag is reachable via the `S` shortcut (toggles the selection) and the note-properties popover; a slide note shows a dashed
diagonal connector from the nearest earlier note. GM/SF2, synth and sample-set piano/bass/guitar renderers continue the most recently started voice on the same MIDI channel, glide in cents across the slide note's duration, and do not strike a second note. A slide without an active voice is silent. MIDI export writes the bend ramp and AMPLE marker described above.

## Chord stamps (PR09)

`spec/chords.json` / `web/lib/chords.mjs`: 47 chords (triads, suspended, sevenths, ninths, elevenths, thirteenths, sixths, jazz/altered, power,
cluster - scoped down from the plan's "about 80"). A dropdown plus a "Stamp" arm toggle: while armed, a plain click on empty grid plots the chord
rooted at the clicked row. The canvas shows an outline preview under the cursor. "Chord options" sets inversion, spread, drop-2, octave doubling
and a bass MIDI pitch; preview and placement use the same options.

## Hi-hat roll chopper (PR10) and strum (PR11)

Alt+U slices the selection into N equal pieces (`web/lib/chop.mjs`: 1/2 through 1/32, or 1/4t/1/8t/1/16t triplets) with exact tick arithmetic (the
pieces always sum to the original length), a gate-length percentage and a velocity ramp (flat/rising/falling/seeded humanised). Alt+S
(`web/lib/strum.mjs`) groups notes that start within a 32nd-note tolerance and staggers each group over a beat span following a curve
(even/slow-then-fast/fast-then-slow/guitar/harp), fading velocity toward the end; direction is down/up/alternate. Both open a plain popover
(division/curve/options + Apply), not a custom floating dial widget as sketched in the epic.

## Humanize (PR12)

Alt+R creates or updates a `midi-humanize` block, configures its velocity and timing parameters, and previews the exact drift with the same stable
random stream as the engine. The preview reads the block's effective seed, including a locked or overridden value; if the seed cannot be read,
preview and Bake are disabled. **Bake** applies the previewed changes directly to the notes as one undoable edit. When inserting a new block, the
roll routes the clip's existing MIDI outputs through it and preserves disabled connections.

## Performance, accessibility, polish (PR13)

A frame-time probe (`dialog.dataset.drawMs`) reports each draw's duration (never fails anything). Popovers return focus to the canvas on Cancel.
A `?` shortcuts card lists every gesture above. Not verified: touch gestures (no touch device in this pass), `prefers-reduced-motion` (there is no
CSS animation to gate - the only "motion" is per-frame canvas redraw, already driven by actual changes, not a timed animation), and a full
screen-reader audit beyond the existing note-navigation announcements.

## Extra tools (PR15-PR22)

- **PR15 arpeggiate, PR16 legato/staccato/remove-overlaps, PR17 flip/mirror/reverse/scale-time:** `web/lib/roll-tools.mjs`, reachable from a "Tools"
  dropdown in the toolbar (same-count transforms write back by index; arpeggiate replaces the selection, like the chopper). "Flip pitch" uses the
  selection midpoint; "Mirror pitch…" lets the user choose a MIDI pitch as the mirror axis.
- **PR18 colour groups:** `grp` (1-8, the same `;key=value` grammar as PR06, no engine change needed since the engine already passes unknown-but-
  valid-integer keys through). A group filter dims notes outside the chosen group, "Select group" selects them, a colour tab marks each note.
  Choose a group and MIDI channel, then use "Move group" to change every note in that group in one undoable edit.
- **PR19 scale-lock and chord-lock:** scale-lock snaps newly drawn, brushed and moved notes to the current scale; Up/Down transpose selected notes
  within the scale. Chord-lock uses sounding notes in visible chord/melody ghost clips for the same gestures. When both are enabled, pitches meet
  both locks; when no chord tone is sounding, scale-lock still applies on its own.
- **PR20 riff machine:** `web/lib/riff.mjs`, a seeded step generator (density, accent, pitch spread, optional scale snap) reachable from the Tools
  menu; scoped from "in the selection" to "from the cursor/selection start, for N bars" (the roll has no empty-time-range selection).
- **PR21 multi-clip editing:** "Copy ghosts" pastes every visible ghost note into the open clip; a "Reference clip" picker keeps any clip visible
  even without arrangement overlap. "Split edit" opens two independently editable roll dialogs side by side; both write normal undoable edits.
- **PR22 per-clip meter (not built):** the agreed design is a meter per clip. Arrangement `at N` stays on the project timeline; each clip's meter
  defines its own bar length in quarter-note beats. Four bars of 3/4 take 12 beats and do not auto-align to a project bar. Today the meter is one value for the whole song (a song-level `meter`); the view-only selector
  does not claim to change playback until a meter per clip is built end to end.

## What's still open

Remaining manual acceptance: a full screen-reader audit. PR22's agreed semantics are recorded, but a meter per clip must update
the parser, arrangement and playback together. The piano-roll tour example and in-app help entry are now present; automated verification follows
after the feature changes.
