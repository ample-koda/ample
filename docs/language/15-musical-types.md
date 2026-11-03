# 15. The musical type model (design, with the first part being built)

**Status:** the parts M1-M4 below (typed catalogue, static checker, spans, typed completion) are built; the helper language of page 14 builds on them. Page 14 section 3 is the general type system, this page is the *musical vocabulary* on top of it.

## 1. Goal and non-goals

Three things, nothing more: (1) find mistakes early (`walk(123, "minor", steps="sixteen", step=7)` is wrong before anything runs), (2) give completion, hover and fixes the exact set of valid values, (3) make musical intent explicit without a heavier syntax. A type is only worth having if it buys one of these.

## 2. Opinion: the minimal strong model

| Concept | Decision | Why |
| --- | --- | --- |
| `PitchClass` (C, C#, Db ... B) | **a type** (a closed vocabulary of 12 classes, spelling remembered for display) | needed by walk/scale/snap/keep/avoid/chords; `123` and `"C"` must be rejected |
| `Root`, `NoteName` | **not types**, the parameter name says "root" | a second name for the same vocabulary only adds noise to hovers |
| `Pitch` (C5, F#3, 60) | **a type** = pitch class + octave = MIDI 0..127; bare `Int` accepted with an info + fix | `where(pitch=...)`, `clamp`, `fold`, `euclid(pitch=)` |
| `Octave` | **refinement alias** `Int 0..=9`, not a value kind | the range check and the completion (2..6 first) are the benefit; no operations of its own |
| `Velocity` | refinement alias `Int 1..=127` | same |
| `Count` | refinement alias `Int 1..` (steps, hits, times, voices) | catches `0`, negatives and `"sixteen"` |
| `ScaleMode` (major, minor, dorian ...) | **a type**, a closed enum from `spec/scales.json` | a bare word, never a string |
| `Scale` (root + mode as one value) | **not yet** | root and mode stay two parameters (backwards compatible); a `Scale` value earns its place when helpers pass keys around: planned, not built |
| `Chord`, `Chords` (`List<Chord>`) | **types**; a chord token (`Cm`, `Ab`, `G7`, `F#m7b5`, `C/E`) is parsed once; `chords("Cm Ab")` keeps working (a string *literal* is parsed against the expected type) | helpers need not pass strings around, and the checker can underline the one bad chord |
| `Interval` | **a quantity** of pitch distance, stored as an exact rational number of semitones, with units `st`, `oct`, `cents` | see 3 |
| `Duration = Length \| Time`, `Length`, `Time` | as page 14 | see 4 |
| `Probability` | refinement of `Float` `0..=1`; `25%` converts to `0.25` | `thin`, `chance` |
| `Range<T>` | typed range (`1..=4` is `Range<Int>`, `0ms..=40ms` is `Range<Duration>`, `-5..=5` is `Range<Interval>`) | `random`, `randomize`, `where`, selectors |
| `RandomSource = Seed \| StableSeed \| RandomStream` | as page 13/14 | `seed=` |
| `Notes`, `Note` | as page 14 | |

**Overkill, left out on purpose:** separate `Semitones`/`Cents` types (they are units of `Interval`), `Frequency`, `Tempo`, `TimeSignature` (the meter is a setting of the composition line), `Degree`, `Voicing`, user generics. A union is only used where two members really are interchangeable (`Duration`, `RandomSource`, `Number`); there is no `String | Int | Float` anywhere in the musical API, and **a `String` is never accepted where a vocabulary type is expected, except a quoted literal of that vocabulary (compatibility, with a warning and a fix)**.

### Words are resolved by the expected type

A bare word in an argument is not a string: it is looked up in the vocabulary of the *expected* type. In a `ScaleMode` slot `minor` is a mode; in a `PitchClass` slot `C` is a pitch class; in a `Chords` slot `Cm` is a chord. A variable of the right type wins over a vocabulary member with the same name (and a lint warns about the shadowing). That gives exact completion (`walk(C, |` offers modes only), exact errors with nearest suggestions (`mnor` → `minor`) and no magic identifiers.

## 3. Interval

`transpose(+7)` today means semitones. I would not fix it to `Int`: an `Interval` is an **exact rational number of semitones** with units `st` (semitones, default), `oct` (12 st) and `cents` (1/100 st): `+7st`, `-1oct`, `50cents`. Bare numbers keep working in an `Interval` slot and mean semitones (silently: `transpose(+12)` is idiomatic; the hover shows `+12 -> +12st`; `AMP2047` is reserved and unused). Note events have integer pitches, so an operator that needs a whole number of semitones (`transpose`) reports a fractional interval (`+50cents`) as an error *for now* (`AMP2048` "microtonal needs pitch bend, which note events do not carry yet"): the type already allows microtonal values, only the consumer is restricted. Named intervals (`P5`, `m3`) can be added as vocabulary later without touching the model.

## 4. Durations, tempo, hashing

`0.5beat`, `2bars`, `250ms`, `1.5s` are exact quantities (rational, not floats) with a **dimension**: `Length` (bars, beats, ticks: independent of tempo) and `Time` (s, ms: depends on tempo). `Duration` accepts both, so `walk(..., step=0.5beat)` and `step=250ms` are both valid. A `Time` value turns into beats with the song tempo with a fixed rule (round half to even); the **recipe hash then contains the tempo** (already so for seconds in page 12), and the hover says "depends on tempo". A bare number in a `Duration` slot is an error with the fix "add a unit" offering `beat`, `bar`, `ms`, `s` (S2: the choice changes the music). Where only a `Length` makes sense (`bars(1..5)` positions, `quantize` grid), a `Time` value gets the conversion fix with the value at the current tempo.

## 5. Spans: known, derived, unresolved

Every `midi-clip` value gets a **span class** that the checker computes without running the notes:

* **Known**: plain literal or `editable` notes, a stored bake: the span is a constant.
* **Derived**: built only from static quantities (generators with literal arguments, operators with literal arguments over known/derived inputs): the span follows by arithmetic, for example `walk(... steps=16, step=0.5beat)` is 8 beats and therefore 2 bars.
* **Unresolved until evaluation**: depends on another block's evaluated notes, a file, a MIDI source, a dynamic call or a loop with a non-constant bound.

Hover shows the class and value (`for auto · derived: 16 × 0.5 beat = 2 bars`).

**Downstream `for auto`:** an instrument (`gm`, `synth`, `piano`, `sampler`, `bass`, `guitar`, `delay-llama`) may say `for auto` and then inherits the span of the notes that feed it (the longest, rounded up to whole bars). A fixed `for N` that differs from the statically known or derived span of its only source gets info `AMP2049` ("the clip is 2 bars, this block says 4") with the fix *Use auto span* (S2: it can shorten a tail on purpose). A literal `for` is **not** nagged when the source is unresolved, and no aggressive warning is given when a derived span could simply have been written by hand.

## 6. The typed catalogue (before / after)

`spec/note-ops.json` gets a `types` section (vocabularies, refinements, units) and every parameter a type from this page. The C# checker, the web completion and the docs read the same file. Examples of the new signatures:

```
walk(PitchClass root, ScaleMode scale, Count steps = 16, Duration step = 0.5beat, Octave octave = 5,
     Count range = 2, Velocity velocity = 90, RandomSource seed) -> Notes
scale(PitchClass root, ScaleMode scale, Count steps = 8, Duration step = 0.5beat, Octave octave = 4, Velocity velocity = 90) -> Notes
chords(Chords chords, Length length = 1bar, Octave octave = 3, Velocity velocity = 80) -> Notes
euclid(Count hits, Count steps, Pitch pitch = 36, Duration step = 0.25beat, Velocity velocity = 90, Int rotate = 0) -> Notes
arp(Duration rate) -> Notes -> Notes              shift(Duration by) -> Notes -> Notes
transpose(Interval by) -> Notes -> Notes          stretch(Number factor) -> Notes -> Notes
humanize(Duration timing, Velocity velocity, RandomSource seed) -> Notes -> Notes
thin(Probability p, RandomSource seed, per = note|bar) -> Notes -> Notes
choose(List<T> options, RandomSource seed, Int round = 0) -> T        chance(Probability p, RandomSource seed, Int round = 0) -> Int
snap/keep/avoid(PitchClass root, ScaleMode scale) -> Notes -> Notes     clamp/fold(Range<Pitch> range) -> Notes -> Notes
```

Before and after:

```
# before: all of this is accepted by the parser and fails (or silently misbehaves) later
midi-clip m at 1 for 2 {
  notes walk(123, "minor", steps="sixteen", step=7, octave=5.5, seed=stable("melody"))
    |> transpose(+7.5)
    |> humanize(timing=20, velocity=300, seed=stable("melody"))
}
gm m-play at 1 for 4 { from m.notes -> notes }

# after: five precise diagnostics at the exact ranges, each with a fix
#  walk(123 ...)           AMP2044  123 is not a PitchClass (C, C#, Db ... B)              fix: C, D, E ...
#  "minor"                 AMP2045  a quoted word where ScaleMode is expected              fix: minor (S0)
#  steps="sixteen"         AMP2044  sixteen is not a Count (a whole number, 1 or more)
#  step=7                  AMP2046  a Duration needs a unit                                fix: 7beat / 7bar / 7ms / 7s (S2)
#  octave=5.5              AMP2050  an Octave is a whole number from 0 to 9                fix: Round(5.5) (S1) / 5 / 6
#  transpose(+7.5)         AMP2048  a fractional interval needs microtonal support         (hover: +7.5st = 750 cents)
#  velocity=300            AMP2044  300 is not a Velocity (1..=127)
#  timing=20               AMP2046  a Duration needs a unit                                fix: 20ms
#  gm m-play ... for 4     AMP2049  the clip is 2 bars (known), this block says 4          fix: Use auto span
midi-clip m at 1 for auto {
  notes walk(C, minor, steps=16, step=0.5beat, octave=5, seed=stable("melody"))
    |> transpose(+7st)
    |> humanize(timing=20ms, velocity=8, seed=stable("melody"))
}
gm m-play at 1 for auto { from m.notes -> notes }
```

```
# before: chord strings everywhere
notes chords("Cm Ab Bb Cm", length=1bar, octave=3, velocity=70)      # keeps working
# after: the same, parsed to typed Chords at check time; a helper can take them as a type
helper bed(Chords progression, Octave octave = 3) -> Notes { chords(progression, octave=octave, length=1bar) }   # helper phase
notes chords("Cm Ab Xq Cm")      # AMP2044  "Xq" is not a chord symbol: it must start with a root, underlined on that token only
```

## 7. IntelliSense that follows from the types

* `walk(` offers the `PitchClass` vocabulary (and variables of that type); `walk(C, ` offers `ScaleMode` only; `step=` offers duration units and snippets (`0.5beat`, `250ms`, `1/3beat`); `octave=` offers 2..6 first; `seed=` offers `stable("…")`, forks of known names, explicit numbers and streams of the clip.
* After `|>` the *left* type decides (page 14), and each candidate shows its typed signature; hover on an argument shows its type, and **"converted"** when a bare number became an `Interval` or a `Duration` (for example `+7 → +7st`).
* Signature help lists the real parameter types, defaults and the return type, marks the current parameter, and shows an info line when a conversion applies.

## 8. Plan and status

| Chunk | Content | State |
| --- | --- | --- |
| M1 | `types` section and typed parameters in `spec/note-ops.json` (+ schema/validate-spec) | built |
| M2 | C# static checker (`NoteTypes`): arity, names, vocabulary, refinements, units, ranges, seeds, chord strings, diagnostics AMP2044–AMP2050, fixes, invalid-corpus cases, tests | built |
| M3 | span classes, `for auto` on instruments, AMP2049, `Use auto span` | built |
| M4 | web: typed catalogue module, typed completion, signature help, hover (JS reads the same spec) | built |
| M5 | `Scale` value, named intervals, microtonal pitch bend | not built |
