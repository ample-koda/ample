# 12 - Note pipelines and note bake

Status: built (2026-10-28; spec 0.5): the pipelines, `for auto`, the bake stage and the live diagnostics are implemented in C# and in the browser. Nothing in this page changes the meaning of a file that parses today: a plain `notes` literal stays a literal.
The page is the contract for two evaluators (JavaScript in the browser, C# on the server) that must agree byte for byte; the shared test corpus is `spec-tests/note-pipelines/`, the operator catalog is `spec/note-ops.json`.

## 1. Idea

Note data is a value. A `midi-clip` can say where its notes come from with a pipeline instead of a literal:

```
midi-clip melody at 1 for auto {
  notes take.notes
    |> bars(1..=8)
    |> transpose(-12)
    |> bake lowered {
      input-hash "..."  notes-hash "..."  span 8bars
      notes 1:0:1:48:100 1:1:1:50:100
    }
    |> humanize(timing=20ms, seed=7)
}
```

The graph keeps its blocks (`transpose`, `quantize`, `midi-humanize`...) for live instrument chains. A pipeline step and the block of the same name use the same operator code (one catalog entry, one implementation per language), so they cannot drift apart.

## 2. The `Notes` value

- Events: `at` (micro-beats from the origin of the value, may be negative inside a pipeline), `len`, `pitch` 0..127, `vel` 1..127 and the per-note extensions of the compact format (`;pan= ;bend= ...`), which every operator carries along untouched.
- **Span**: the length of the value, needed by `concat`, loops and window selection. Span is explicit when the source knows it (a bake records it, a window has the length of the window, a clip has its `for`); otherwise it is the end of the last note rounded up to a whole bar (minimum one bar).
- A value is never shorter than its notes: after `shift`, `merge` and `replace` the span grows (in whole bars) to cover the furthest note end.
- Output order is canonical: by `at`, then `pitch`, then the order of arrival (stable).
- Resolving to text: bars are 1-based, a note that crosses a bar line is written as consecutive pieces of the same pitch that share a `;tie=N` id (N from 1,000,000 up, unless the note has its own), so they play as one note instead of being struck again (the existing roll convention, see `splitAtBars`); beats are decimals with up to six places, trailing zeros removed. Notes that start before the origin are dropped with a warning (`before-origin`).

## 3. Time model

- One exact unit: the micro-beat (`1 beat = 1_000_000`; `1 bar` is 4 beats in 4/4 and the beats of the song's `meter` otherwise). No floating point anywhere: literals are exact decimals (BigInt rationals).
- Units on quantities: `bar`/`bars`, `beat`/`beats`, `s`, `ms`, `ticks` (ticks follow the clip's `ppq`). A unitless number is only legal where the operator says so (`transpose(+12)` semitones, bar coordinates inside `bars(...)`).
- **Coordinates and lengths.** Inside `bars(a..b)` / `beats(a..b)` the numbers are *coordinates*, 1-based like bar numbers (`1` is the start of the first bar, `1.5` the middle of it). Inside `time(a..b)` they are seconds from the origin (0-based). `shift`, `stretch` and other operators take *lengths* with a unit (`shift(+250ms)`).
- **Ranges are half-open**: `bars(1..5)` is four bars. `bars(1..=4)` is the inclusive spelling for whole bars (`..=` requires an integer end). A fractional position is legal but gives the warning `fractional-bar` (non-fatal, no automatic fix); `..=` with a fraction is read as `..` plus the same warning.
- Seconds and milliseconds convert with the song tempo: `micro-beats = round_half_even(seconds * tempo / 60 * 1_000_000)`, computed as one exact rational. The tempo is part of the hash of every stage that uses seconds. Placement (`at`, `for`) stays whole bars (see AMP1104); sub-bar offsets belong in the pipeline.

## 4. Operators (catalog: `spec/note-ops.json`)

| Kind | Operators | Meaning |
| --- | --- | --- |
| source | `block.port`, literal entries, `editable { ... }` | `take.notes` reads the `notes` output of a block (same spelling as `from take.notes -> in`) |
| select | `bars`, `beats`, `time`, `mask` (range arguments of `remove`, `cut`, `replace` and `mask` are written like the select steps: `bars(1..2)`, `beats(..)`, `time(..)`) | `bars/beats/time(range)` keep the notes that **start** inside the window, shorten those that run past its end, and move the window start to the origin; the span becomes the window length. `mask(range)` does the same but keeps positions and the span |
| delete/insert | `remove(range)`, `cut(range)`, `replace(range, notes)` | `remove` deletes the notes starting in the range and leaves a gap; `cut` also closes the gap (later notes move earlier, span shrinks); `replace` removes the range and puts the given notes there (rebased to the start of the range) |
| transform | `transpose(st)`, `shift(length)` | `transpose` clamps pitch to 0..127 and semitones to +-48, exactly like the block; `shift` moves every note (negative allowed; notes moved before the start are dropped with a warning) |
| combine | `concat(notes)`, `merge(notes)` | `concat` puts the argument after the span of the value; `merge` layers both (span = the longer one) |
| freeze | `bake name { ... }` | see section 6 |

More operators (all in the catalog, all deterministic, all in both evaluators):

| Kind | Operators | Meaning |
| --- | --- | --- |
| transform | `reverse()`, `stretch(f)`, `legato()`, `quantize(grid)` | mirror inside the span; multiply starts, lengths and span by a factor between 1/64 and 64; every note lasts until the next start; snap starts to a grid (ties to even) |
| seeded | `humanize(timing=20ms, velocity=4, seed=7)` | small changes from a splitmix64 generator seeded by `seed` (required; the notes are taken in canonical order); the same notes in JavaScript and C# |
| generate | `repeat(n)`, `arp(rate)`, `euclid(hits, steps, pitch=, step=, velocity=, rotate=)` | copies one after the other (n at most 1024); spread chords over their length; a Euclidean rhythm as a source (steps at most 256) |
| select | `where(pitch=lo..hi, velocity=lo..hi)` | keep notes whose pitch/velocity is in the range (half-open, `..=` includes the end) |

Notes keep their length when `shift`, `humanize` or `merge` move them, so a note can run past the end of the value (the span grows to cover it); `legato()` leaves the last note as it is. Cut the result with `bars(1..=n)` (or `mask`) when a pipeline must end on a bar line. Arguments may be positional or named (`name=value`). A `midi` block (a project MIDI file) is a valid source: `song.notes` reads all its notes (or the chosen track). Every operator declares: parameter types and units, whether it needs a seed, a version (part of every hash), and a cost class. Not yet: `chords`, `scale`.

## 5. Where each kind of error is found

| Time | Examples |
| --- | --- |
| Live, static (no evaluation) | syntax, unknown operator (with "did you mean"), arity, argument and unit types, reversed constant range, fractional bar, unknown reference, reference to a non-notes port, reference cycle, invalid literal note, missing seed, stale or dirty bake (hash compare), static upper bound of the event count against the project cap, `for auto` without a known span |
| Bake time | real note count, empty result, span, fuel and wall-clock use, memory, cap hit, source take missing, notes before the origin, cancellation |
| Build time | rendering, instruments, pinned files, downstream rebuilds |

Symbolic ids used by the corpus until codes are allocated: `unknown-operator`, `unknown-reference`, `reference-cycle`, `range-reversed`, `fractional-bar` (warning), `missing-unit`, `wrong-unit`, `bad-argument`, `before-origin` (warning), `stale-bake`, `dirty-bake`, `cap-events`, `evaluators-disagree`. Codes come from the free ranges (AMP11xx syntax, AMP20xx semantic, AMP57xx runtime) when the parser lands; every static code needs an invalid case in `spec-tests/invalid`.

## 6. Bake

`|> bake name { ... }` stores the output of everything before it in the pipeline.

```
|> bake lowered {
     strict                      # optional: a manual change is an error instead of a warning
     input-hash "sha256:..."     # recipe hash up to this stage
     notes-hash "sha256:..."     # signature of the stored notes
     span 8bars
     notes 1:0:1:48:100 ...      # or:  file "bakes/melody-lowered.notes"   (any file you choose)
   }
```

- **Recipe (Merkle) hash.** `input-hash` = hash of the normalized expression up to this stage + operator versions + tempo (when seconds are used) + seeds + the hashes of what it reads: a baked block or stage contributes its stored `notes-hash`, an unbaked formula its own recipe hash, a MIDI file its file hash. Staleness therefore needs no evaluation and is visible while typing.
- **Signature.** `notes-hash` signs the stored data (inline or in the file). A manual change makes the stage `dirty`: warning, or error with `strict`. Fixes: Accept (re-sign; the stage becomes `editable` or is detached into a literal) or Re-bake (revert).
- **States:** fresh, stale (recipe changed), dirty (data changed), editable (owned by hand), detached (bake removed, notes kept as a literal).
- **Build policy:** a bake is authoritative: downstream uses the stored notes (reproducible); a stale bake warns and offers Re-bake, Detach, Use live formula. Unbaked pipelines are evaluated by the build (pure, capped) and their resolved-notes hash goes into the plan id.
- **Edits live only in literals** (`editable { notes ... }` or a detached bake), never as a patch over derived output.
- Naming: the block header flag `bake` (freeze a take) and the plan intent `bake` keep their meaning; this feature is called *note bake*.

## 6b. Randomness

Seeds, forks, streams and every seeded operator share one model, described in `13-randomness.md`. The recipe hash of a pipeline contains the master-seed when it uses `stable(...)`.

## 7. Limits

Three numbers guard a pipeline: the limit of the **server** (2,000,000 notes, fixed), the limit of the **project** (default 200,000 when building and 100,000 in the editor; raise it on purpose with `settings { notes.maxEvents 500000 }`, never past the server's limit) and a **time budget** of 30 seconds. A static upper bound (a repeat multiplies, a concat adds) warns before anything runs (AMP2037); `repeat`, `arp` and every step check the real size before they build it (AMP5701).

Server hard cap (configuration only) > project setting (lower default, raisable up to the hard cap on purpose) > file or section directive (may use what the project allows). Counters: events, fuel steps, wall clock, memory. Loops exist only with constant bounds; there is no `while`, so an endless loop cannot be written. A bake is evaluated into a temporary result, validated, then committed in one journaled write; cancel or failure leaves the source untouched.

## 8. Two evaluators

The JavaScript evaluator (`web/lib/note-pipeline.mjs`) serves the live editor, Jam and the preview. The C# evaluator is authoritative: it runs on save, on "Verify", before a bake commit and in the build. Both run the same corpus; a disagreement is the diagnostic `evaluators-disagree` with both hashes and never silently picks one. Builds never depend on the JavaScript result.

## 9. Compatibility

- A `notes` value that starts with a digit entry (`1:0:...`) or `{` is the literal it always was; a value that starts with an identifier or `(` is an expression. Existing songs, demos and plan ids do not change, and the midi-clip renderer keeps its version: the build resolves an expression to the same notes JSON the literal path produces.
- New syntax is additive in a minor spec version; older tools see an unknown parameter value, never a changed meaning.
