# 13 - Seeds and randomness

Status: built, on the note pipelines (page 12). Everything here is deterministic: the same song gives the same notes on every machine, in JavaScript and in C#, and a song never changes because something unrelated changed.

## 1. The model in one page

| Thing | What it is | Has state? | Depends on |
| --- | --- | --- | --- |
| **seed** | a 64-bit key, a point in a tree of keys | no | master-seed (for `stable`), nothing (for explicit seeds) |
| **`stable("name")`** | the key of a song-global namespace | no | master-seed + name |
| **`.fork("label")`** | a child key of any seed | no | parent key + label |
| **`seed=123456`** | an explicit, pinned seed | no | only the number |
| **draw** | one value from a seed: `draw(key, purpose, address)` | no | key + what is drawn + where |
| **`random-stream`** | a named counter over one seed, declared in a clip | yes (a cursor) | seed + number of earlier `next()` calls in that clip |
| **`random-seed()`** | "pick one for me" | no | OS randomness once, then stored as an explicit seed |

Rules that follow:

- **Keys are derived by hashing**, not by `seed + offset`: `key' = first 8 bytes of SHA-256("ample-seed/1" \n kind \n parent \n label)`. A fork is a path, not a cursor. `stable("verse").fork("chords")` written in three places is the same key in all three, and none of them shares a hidden `next()`.
- **A draw is addressed**: `draw = first 8 bytes of SHA-256("ample-seed/1" \n "draw" \n key \n purpose \n address)`. The address is the explicit `round=` of a call, or, inside a per-note operator, the identity of the note (`start:pitch:duplicate`). It is never "the n-th random call so far". Adding a random call (or a note) somewhere else cannot move any other stable result.
- **Only a stream has an order.** `random-stream chords stable("verse").fork("chords")` declares a counter; `chords.next()` is `random(seed=<that seed>, round=<cursor>)` and then the cursor moves. Streams are declared in a clip (never inline), start at 0 every time the clip is worked out, and are reported in the provenance (seed and how many values were consumed).
- **`stable` follows the master-seed, explicit seeds do not.** Change `master-seed` and every `stable` result becomes a new, again reproducible, variant; `seed=123456` stays exactly as it was.
- **The recipe hash of a pipeline (and so every bake) contains the master-seed whenever the pipeline uses `stable(...)`** (directly or through a stream or a clip it reads). Changing the master-seed therefore makes such bakes stale, the same way changing the tempo does for seconds.

## 2. What is part of the identity of a random result (and what is not)

| In the derivation | Not in the derivation |
| --- | --- |
| master-seed (only through `stable`) | the name of the block or clip |
| the stable name and the fork labels | where the clip sits in the song (`at`, `for`) |
| the purpose (`humanize.timing`, `thin`, `random`...) | the order in which things are worked out |
| the address (round, or note identity, or bar) | other random calls, other notes |

So: **renaming a block, moving it, or adding a random call elsewhere never changes the music.** Renaming or re-labelling a stable name does (that is the point of a name), and the editor warns when a name is used only once and looks like a typo of another.

Per-note identity is `start:pitch:duplicate` in the notes the operator receives (clip-relative micro-beats). Adding or removing *other* notes does not move a note's draw; moving the note itself does, because it is a different note.

## 3. Syntax

```
seed=123456                          # explicit, pinned
seed=stable("verse")                 # follows master-seed
seed=stable("verse").fork("drums")   # a branch; forks nest: .fork("a").fork("b")
seed=stable("verse").fork(3)         # a fork label may be a whole number
seed=random-seed()                   # unresolved until you pick one (fix: "Pick a seed"); then it is an explicit seed
```

Values:

```
random(0..100, seed=S, round=0)            # whole number, half-open (..= includes the end); decimals and lengths work too: random(10ms..40ms, seed=S)
choose(a, b, c, seed=S, round=2)           # one of the listed values
chance(0.25, seed=S, round=4)              # 1 with that probability, else 0
```

The same seed, purpose and round always give the same value; another round gives another (stable) value. `round` defaults to 0.

Streams (declared in a clip, used in that clip):

```
midi-clip x at 1 for auto {
  random-stream drift stable("verse").fork("drift")
  notes a.notes |> shift(drift.next(0ms..=30ms)) |> transpose(drift.next(-2..=2))
}
```

Operators that use seeds (all in the operator catalog, all with `seed=`, all with the same model):
`humanize(timing=, velocity=, seed=)`, `randomize(velocity=lo..hi, pitch=lo..=hi, seed=)`, `thin(0.25, seed=, per=note|bar)`, `scramble(seed=)`, and the generator `walk(root, scale, steps=, step=, octave=, range=, seed=)`.

Helpers that keep notes out of a range or a set: `clamp(lo..=hi)` (cap at a ceiling and a floor), `fold(lo..=hi)` (move by octaves into the range, keeps the note name), `snap(root, scale)` (to the nearest scale tone), `keep(root, scale)` and `avoid(root, scale)` (drop notes inside / outside the scale). Generators: `chords("Am F C G", length=1bar, octave=4)` and `scale(root, kind, steps=8, step=0.5beat, octave=4)`.

## 4. Declared or implicit stable names?

Implicit on first use (chosen). A name is only a label of a branch in the key tree, so nothing has to exist before it is used, and a name used in two clips is the same namespace by construction. The risk is a typo that silently starts a new namespace; the editor answers that with a warning when a name is used once and is within two edits of a name used elsewhere ("did you mean ..."), with a quick fix. A declared registry would add ceremony for every name and would not stop a typo in the declaration. Inspect: hover shows the key and the namespaces list; `ample seeds` and the "Where does the randomness come from?" item of the roll list every use with its derived key and how many values were drawn.

## 5. Copy and duplicate

Stable names are song-global. When a clip that uses `stable(...)` is copied the editor asks, with the impact in words:

1. **Reuse** - keep the names: the copy plays exactly the same random choices (use it when the copy should match).
2. **Fork** - append `.fork("copy-n")` to every stable seed of the copy: still derived from the same parents, but its own deterministic branch (use it for a variation that stays related).
3. **Independent** - new song-global names (`pulse_2`, `pulse_3`, ...), one consistent mapping per copy: a fully separate family.

Explicit seeds (`seed=123456`) are copied as they are (they have no name to fork); the dialog says so.

## 6. Edge cases

- **Detach / bake / editable**: a baked or detached clip is plain notes; the randomness is frozen into them. Re-baking re-runs the same derivation, so it gives the same notes unless a seed, the master-seed (for `stable`) or the pipeline changed, and then the bake is stale (AMP2030) and says why.
- **Master-seed change**: all `stable` results change; explicit seeds do not; bakes that use `stable` become stale.
- **`random-seed()`**: never hidden. Until it is replaced by a number the pipeline refuses to build (AMP2039), with a fix that picks one (OS randomness, once) and writes it into the song. From then on it is an ordinary explicit seed.
- **A stream that is not declared, declared twice, or a plain seed used as a stream** are errors with a hint; a declared stream nobody reads is an info.
- **The same seed used twice by the same operator in one clip** gives identical offsets (correlated): an info says so.
- **Streams and bake**: a stream is a function of its declaration and the order of the calls, so it is reproducible; the provenance records its seed and consumption, and the recipe hash contains the declaration.
- **Evaluation order** never matters for stateless draws. It matters only for a stream, and the order is fixed: the source, then the steps left to right, arguments left to right.

## 7. Two evaluators

The JavaScript evaluator (`web/lib/note-pipeline.mjs`, `web/lib/note-random.mjs`) and the C# evaluator (`NotePipeline`, `NoteRandom`) give the same keys, draws, notes and recipe hashes. The shared golden vectors are `spec-tests/note-pipelines/seeds.json` (keys and draws) and the `random` cases of `core.json`.
