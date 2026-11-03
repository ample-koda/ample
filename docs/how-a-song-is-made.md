> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built; the clickable sketch only simulates it (its preview plays a stand-in synth, but it does follow the chords you type for piano and bass).

# How the blocks make a song

## 1. The idea in one sentence
The graph is a **dependency graph of typed values on one shared timeline**. A link does not carry sound first; it says "this block needs the result of that block". Building walks the graph from the sources to the master and each block produces its typed output, which is cached by hash.

## 2. Four kinds of values (the port colours)
| Type | What flows | Example |
| --- | --- | --- |
| **midi** (notes) | events with a position on the timeline: bar.beat, length, pitch or chord, velocity | `chords` produces `Am F C G`, one chord per bar, as note events |
| **text** | words with timing hints | `lyrics`: each line or word has a target time (bar, seconds, first 5 s checks) |
| **data** | recorded facts, usually locked | `weather` fetched once, frozen with `lock` (for example "rainy, 11 degrees") |
| **audio** | a rendered signal placed on the timeline | drum kit output, piano, a vocal take, the mix |

## 3. The shared timeline is what keeps everything in sync
- There is **one tempo map** (tempo, time signature, changes) and **one bar grid**. Bars and beats convert to seconds through that map.
- Every block that makes something is **anchored on the timeline**: `at bar 9 for 8 bars`. The arrangement view shows exactly these anchors; the block graph shows how the blocks depend on each other. Same project, two views of the same text.
- Notes and drum triggers are **exact**: they are events at known positions, so two blocks that read the same chords are in sync by construction, to the sample.

## 4. Walking through the example graph
1. **chords** (stable, textual MIDI): `Am F C G`, 8 bars. Output `notes` = chord events (bar 1 Am, bar 2 F, ...). This is the harmonic spine.
2. **piano** reads `notes` and plays voicings of each chord with its soundset (a sampler, pinned by hash). Output: audio, exact and repeatable.
3. **bass** reads `notes` too and plays a pattern (for example the root on beats 1 and 3, a fill before a chord change). Same events, same timing, so it locks with the piano.
4. **drums** have a trigger input that is optional: by default they play their pattern against the bar grid; connecting chords lets a pattern accent chord changes. Output: audio.
5. **lyrics** produce timed words. **weather** produces data (mood words, tempo hint, season).
6. **chorus_vox** (dynamic, an AI voice) takes `lyrics`, `melody` (the chords, which give key and progression, and optionally a melody line) and `style` (the weather data) and builds a **recipe** for the voice engine: caption/tags, lyrics, tempo, key, length, a seed derived from the master seed and the block name. The engine returns a **take** (audio) that is stored in the take bank by hash.
7. **mixer** sums the audio inputs on the timeline at their anchors with gains and pans. **master** (EQ, compressor, limiter) shapes the sum; its output is the master audio you play or release.

## 5. Where "sync" is exact and where it is measured
| Part | How it syncs | Honest note |
| --- | --- | --- |
| Piano, bass, drums, any MIDI-driven sampler | by construction: events on the shared grid | exact, deterministic |
| Audio imports (a recorded loop) | placed on the grid; optionally warped or trimmed with a stated policy | exact placement, warping is a choice |
| **AI voice** | **measured, then checked**: the take has its own length and timing | the model does not obey a bar grid exactly; AMPLE places the take, measures word timing (opt-in plugin), compares with the lyric hints, and reports the error; **variants** let you pick the take that sits best; policies: warn (default), retry within a budget, pick best, nudge |
| Harmony between voice and band | the voice is conditioned on key, tempo and chord progression | following a given melody exactly is not proven yet; chords constrain the style, not every note |

## 6. What a build does
1. Order the needed blocks (sources first).
2. For each block compute its **cache key** from template, version, parameters, node seed, baked flag and the keys of everything upstream. A matching cache means "will only be checked" (greyish green); no match means "must run" (grey).
3. Run block by block: MIDI and sampler blocks are cheap and exact; dynamic blocks call plugins asynchronously with progress and store takes; each finished block lights up.
4. Change the chords: the chords block, piano, bass, voice, mixer and master turn grey again, drums and lyrics stay cached.
5. **Preview** of any block with an audio output builds what is missing first, then plays that block (for the mixer or master: everything upstream mixed together).

## 7. Where the song actually lives
The **text** (`.amplet`) is the song; the graph and the arrangement are views of it, and visual edits become patches to the text. A tiny excerpt of what the text could look like:
```
composition "my-song" { ample 1  tempo 92  master-seed 424242 }
midi chords   { chords "Am F C G"  bars 8 }
sampler piano using felt-piano   { from chords.notes }
sampler bass  using analog-808   { from chords.notes  pattern root-1-3 }
sampler drums using kit/tape     { pattern beat-A }
voice chorus_vox using anna      { lyrics chorus  melody chords.notes  style weather  at bar 9 for 8 bars }
mixer mix { drums, bass, piano, chorus_vox  gains 0 0 -2 0 }
master    { limiter -1 dB  target -14 LUFS }
```

## 8. What the sketch does and does not do
- It follows the dependency graph, the cache colours and the progress per block.
- Its preview sound is a **stand-in synthesizer**: piano and bass follow the chords you type in the chords block (one chord per bar, major or minor by name), drums play a fixed pattern, the voice is a seeded tone line. It is not the real engine and the voice is not a real take.
- It does not measure word timing, place takes with warping, or apply the voice-engine recipe; those are designed in [Variants](variants.md), [Bake](bake.md), [Modules](modules.md) and Tech.
