# 17 - Notes: what every note needs, writing them as formulas, and proving nothing changed

Status: built. This page is the check that the built-in operators can write **anything** a note, and every setting of a note, needs; how the assistant finds the shortest way to write notes; and how "the result is the same" is proved instead of hoped for.

## 1. What a note is, and where each part can be written

| Part of a note | In the notes text | How a formula makes or changes it |
| --- | --- | --- |
| start, length | `bar:beat:length` (beats are exact decimals, 6 places; in ticks when the clip or channel has `ppq`) | `shift`, `stretch`, `quantize`, `legato`, `humanize(timing=...)`, `bars/beats/time/mask/cut/remove/replace`, `reverse`, `repeat`, `concat`, `merge`; generators `euclid`, `chords`, `scale`, `walk`; the rhythm of a literal |
| pitch | `pitch` | `transpose`, `snap/keep/avoid`, `clamp`, `fold`, `randomize(pitch=...)`, `scramble`, **`set(pitch=...)`** (a number, a note name like C4, or a list that cycles) |
| velocity | `velocity` | `randomize(velocity=...)`, `humanize(velocity=...)`, **`set(velocity=...)`** |
| channel, bank, program | `:channel:bank:program` | **`set(channel=, bank=, program=)`** (`channel=-1` takes the routing away) |
| tie (a note over a bar line) | `;tie=N` on each piece | written by the pipeline itself: a note that crosses a bar line is written as tied pieces, so it plays as one note |
| pan, fine tuning, release, cut, slide, portamento, bend | `;pan= ;fine= ;rel= ;cut= ;slide= ;port= ;bend=` | **`set(pan=, fine=, rel=, cut=, slide=, port=, bend=)`** (`rel=-1` takes the release away); a setting that is its default is left out |
| bend and controllers of a channel (CC 7, 11, 10, 64, pitch bend) | `<channel>.events` row of a sequencer pattern (`bar:tick:c\|p:data1:data2`) | data, not formulas: they come from an import and are edited as data |
| clip: `ppq`, `bend` curve and `bend_range` | parameters of the `midi-clip` | `auto(...)` curves for `bend` |

Not covered, by design: tempo-dependent meters (one meter for the whole song: `meter` on the composition line), tempo changes inside a song (an import puts the notes at their real time at the first tempo), and anything that is not a note (audio). A pipeline works in exact micro-beats; a tick of a MIDI file that is not a whole number of micro-beats is rounded, at most 0.5 micro-beat, far below one sample.

**The check.** `NotePatternsTests` writes random notes with every one of these settings in any mix (150 notes over 20 bars, over bar lines, with and without routing) and requires that `NotePatterns.Find` gives an expression that was evaluated and compared with the notes it came from and is the same. If a setting or a shape could not be written, that test would fail.

## 2. set(...)

`notes |> set(pitch="38 50 45", velocity="105 82")` gives the first note pitch 38 and velocity 105, the second 50 and 82, the third 45 and 105, and starts the lists again after the last value. The order of the notes is their start, then their pitch (before the change), so chords are handled in a fixed order. A single number sets every note. The keys are `pitch velocity pan fine rel cut slide port bend channel bank program`; every value is checked against the range of the setting (`program must be from 0 to 127`). Notes keep when they sound: `set` never moves or lengthens a note.

The shape that compresses best is **a rhythm that repeats plus values that cycle**:

```
1:0:0.5:38:100 1:0.75:0.25:50:100 1:1.5:0.5:45:100 |> bars(1..=1) |> repeat(32) |> set(pitch="38 50 45 50 43 55 45 55", velocity="105 82 91")
```

`bars(1..=1)` fixes the length of the rhythm at one bar, so `repeat(32)` puts the copies exactly one bar apart.

## 3. Formula rows in a sequencer

A row of a sequencer pattern that starts with `=` is a formula: a note pipeline that is worked out for that pattern (its bar 1 is the first bar of the pattern, the tempo of the song is used for lengths in seconds). A formula row has no helpers and no references to clips (only literals, generators and operators); long formulas that need helpers belong in a `midi-clip`. The sequencer, the build, the check of the assistant (`AiService.ProblemsOf`, which reports a bad row as AMP5380 with the reason) and `ample_resolve_notes` all read a row the same way (`SequenceRows.TryRead`); a new reader of rows must use it too (regression 2026-10-28, see `AGENTS.md` section 3). In the sequencer window a formula row shows `ƒ formula` and an *Edit formula* button; the roll opens only rows of notes.

## 4. Finding the shape (ample_find_patterns)

`ample_find_patterns block=<midi-clip or sequencer> [channel=<name>]` cuts the notes into windows of p bars (p from 1 to 16, and the whole length), keeps the periods where no note goes over the end of a window, and writes each as a pipeline: one segment per run of windows with the same rhythm, each `rhythm |> bars(1..=p) |> repeat(m) |> set(...)`, joined with `merge(... |> shift(n bars))`. Only the settings that differ from the rhythm get a list, and a list is cut to its smallest cycle. **Every candidate is evaluated and compared with the original notes** (section 5); `verified: true` means every note, length and setting is the same. The shortest verified candidate comes first, and the answer says how much shorter it is. For a sequencer it gives a formula row per pattern and the channels that play exactly the same notes.

What it does not find: a motif that rises by a constant step, or is transposed per bar, or an arpeggio built from chords. Those need a helper (`14-helpers-types-and-dynamic.md`, a `for` loop with `emit`); the assistant writes it and proves it with `ample_resolve_notes` (its expression can call the helpers declared in the song). When nothing shorter exists the answer says so, and the notes stay: a performance that was played by a person (all the velocities different) is often not mathematical.

## 5. The same, exactly (ample_resolve_notes)

Notes of a clip, of a sequencer channel (over the whole playlist) or of a pipeline are read into one form (`NoteSemantics`): start and length in absolute micro-beats, every setting, the pieces of a tied note put together. Two lists are **the same** when every note of one has its note in the other with the same pitch, velocity, routing and settings, and its start and length differ by at most 2 micro-beats. The answer says what is missing, extra or changed. `ample_import_score compress=true` uses the same check before it writes a formula row.

## 6. A score in pieces, short

The import of a score writes patterns of `bars_per_pattern` bars; a pattern that says exactly what an earlier one says is not written again (the playlist plays the earlier one), a pattern that shares most rows with an earlier one is a variation (`from=`) that lists only what differs, and with `compress=true` a row is a verified formula when that is at least 40 percent shorter.
