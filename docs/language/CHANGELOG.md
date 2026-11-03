# Language changelog

## Note pipelines and note bake (2026-10-26, draft)
- Musical types (docs/language/15-musical-types.md): every note operator parameter has a type (PitchClass, ScaleMode, Count, Octave, Velocity, Pitch, Probability, Interval, Duration, Window, Range, RandomSource); the typed catalogue is spec/note-ops.json v2. New spellings +7st, -1oct, 50cents, 25% and note names (C2) mean the same as the plain numbers (same recipe). New codes AMP2044 (wrong type), AMP2045 (quoted word), AMP2046 (missing unit), AMP2048 (fractional interval), AMP2049 (fixed span differs from the notes), AMP2050 (not whole), AMP2051 (unit of the wrong kind); AMP2047 is reserved. An instrument may say for auto and follows the notes that feed it.
- Seeds and randomness (docs/language/13-randomness.md): `stable("name")` namespaces derived by hashing from the master-seed, `.fork(...)`, explicit seeds, `random-seed()` that must be picked, addressed draws, `random`, `choose`, `chance`, declared `random-stream` clip lines with `name.next()`, the seeded operators humanize, randomize, thin, scramble and the generator walk, the generators chords and scale, the helpers clamp, fold, snap, keep and avoid; new codes AMP2038-AMP2043. Humanize now draws per note (addressed) instead of in order, so it gives other numbers than the first draft.
- More operators (reverse, stretch, legato, quantize, seeded humanize, repeat, arp, where, euclid as a source), named arguments, MIDI file blocks as a source, the limit of the song (settings notes.maxEvents) under the limit of the server, and AMP2037 (the pipeline may be too large).
- A midi-clip `notes` value may be a pipeline expression (`notes take.notes |> bars(1..=4) |> transpose(+12)`), with `editable { }` literals and `bake name { }` stages that store signed notes (inline or in a file). A part may be written `for auto` (the length of the notes). Plain literals are unchanged; new codes AMP1111, AMP2023-AMP2036, AMP5701. See docs/language/12-note-pipelines.md.

## Piano-roll ties (2026-10-18)
- Compact notes accept `;tie=0..2000000` to join bar-split sustains while keeping instrument routing inherited. Legacy nine-field ties remain valid;
  duplicate or conflicting tie identities are rejected. Expanded `tie-id` also works without explicit channel/bank/program.
- The built-in midi-clip renderer is version 4 so cached note output includes the new tie semantics.

## Render stream extension (implementation in progress; verification paused)
- Added the `render` port type, render outputs on audio sources, and `render_in` inputs on built-in audio effects. Effects may use their existing audio input or the render recipe input; both may be connected when one or both connections are disabled.
- Added AMP2020 for connecting both effect input modes while both are active. Source variants now retain ordered render recipes, materialize at audio boundaries, and record effect parameters and seeds in the plan. Full build, cache, UI and conformance acceptance remains in progress; testing is paused at the owner's request.
- Added `docs/examples/16-audio-vs-render/`, a self-contained song with matching EQ/reverb chains on ordinary audio and render paths, mixed for direct comparison.

## Added in MIDI M03 (2026-10-04)
- midi-clip is version 1.1 and accepts optional structured polyphonic notes as bar:beat:length:pitch:velocity entries. Beat values use integer microbeats internally with up to six decimal places in source; ties crossing bars are split. Existing chord and melody songs are unchanged.
- Added static diagnostic AMP2012 for malformed structured MIDI note entries. Migration: none; the new parameter defaults to empty.

## Added in MIDI M13 (2026-10-04)
- midi-clip retains imported note onset and duration in source PPQ ticks, including output metadata, while preserving readable beat fields.

## Added in MIDI M15 (2026-10-04)
- The MIDI file template exposes note, typed sequence and audio outputs. The midi-render template accepts sequence or note input and provides a SoundFont-backed audio conversion path.

## Added in MIDI M02 (2026-10-04)
- Added the `midi` source template for bounded `.mid`/`.midi` project files, deterministic track selection and `notes:midi` rendering, with a text warnings output.
- Added build diagnostics AMP5501–AMP5503 for invalid assets, unsupported exact note timing and missing track indices.

## 0.1 draft (2026-10-03)
- Added `mood-scene` (a stable `style:data` source) and `ace-music` (a live ACE-Step block with optional `lyrics:text`, optional `melody:midi`, optional `style:data` and `audio:audio`). `ai-voice` remains a live ACE-Step block with required lyrics and a vocal-only contract.
- Template parameters can now describe `modalEditor` and `multiline` property editing behavior. The display label and short documentation are also template data.
- Migration: existing `ai-voice voice` values retain their parameter name, but the UI calls it Vocal direction. Existing inline `lyrics text` values still render only when the linked file has no content; move new work into the Lyrics editor file.

## 0.1 draft (2026-10-02)
- First written specification: lexical rules, grammar, error recovery, types and ports, template rules, seeds and determinism, references, diagnostics and lint, formatting, versioning, maintenance.
- Data seeded from the clickable sketch: 14 templates, 31 static diagnostic codes (AMP1xxx, AMP2xxx, AMP5001, AMP5002), 9 lint rules, plus the runtime notices AMP5101 and AMP5102 (regeneration safety).
- Conformance corpus: 15 valid, 42 invalid (one per code and lint rule), 8 recovery, 10 format pairs, 7 round trip.
- Known gaps: the statement-style syntax of `../examples/`, `include` and imports, `.amplelrc`, inner graphs of groups, defining new block types (all in `../open-questions.md`).

## Added in chunk 94 (2026-10-03)
- New statement `use <module>[@version] as <name> { ... }` and the module file format (`module`, `param`, `input`, `output`); new static code AMP1110 and runtime codes AMP5320 to AMP5328; keyword `use`. Nothing that parsed before changes meaning (a block type called `use` did not exist). Migration: none.

## Added after chunk 98 (2026-10-03)
- `drums` is now 1.3 (S07): optional `choke` parameter (`open:hat,pedal`; rules separated by `;`) cuts a voice when a closing voice is hit, with a 7 ms fade. Sample folders may hold a `set.json` with `gain` (dB) and `tune` (semitones) for the whole set. Songs that say `drums ... stable@1.2` stay valid (the class is stable, not pinned). Migration: none.
- Sampler velocity layers (S05): sample files `name_v<L>_r<R>.wav` are layer L, round robin R; accents, strokes and note velocity choose the layer. `melody` notes take an optional third field, the MIDI velocity (`c4:1:110`, default 96, unchanged output). Round robins (S06) are chosen per hit from a separate seeded stream (no change to humanize bytes). Built-in drums, bass, piano and guitar renderers are version 2. Migration: none, folders without layer files sound the same.
- Template parameters can say how the property panel edits them: `kind` (text, number, choice, combo, seed, sampleset, file, pattern, channels) with `options`, `min`, `max`, `step`, `unit`, `placeholder`, `accept` and `count`. Parameters no longer start with made-up text; an empty value shows a placeholder. Migration: none, old values keep their meaning.
- Six audio effect templates (category Effects, `stable@1.0`): `eq`, `distortion`, `delay`, `reverb`, `compressor` and `filter`. Each has one required `in:audio`, one `out:audio` and an `enabled on|off` bypass. Integer arithmetic only, so they are bit-reproducible. Migration: none.
- `midi-clip` has an optional `melody` parameter (note name, octave, length in beats; `r` is a rest). When it is set, the clip plays the melody instead of the chords. Notes now carry `beat` and `beats`; the built-in piano plays them at that place and length (notes without them still last the whole bar). Migration: none.
- `lyrics` is now 1.1: the legacy inline `text` parameter is gone. The words live in the lyrics file, which the lyrics editor creates and names (`lyrics/<block name>.lyr`) the first time you open it. Migration: move old inline text into a lyrics file (`[bar N] words` per line) and set `file`; songs that still say `text` get AMP2003.
- `piano` is now 2.1 and `bass` 1.2: `set` can name a sample folder of the project (note-named WAV files, like the guitar), and both have a `gain` in dB. The built-in sounds are unchanged. Migration: none.
- `ai-voice` is now 0.10: the optional parameter `sing` (bars) says how long the voice sings. ACE fills all the time it is given, so a voice longer than its words repeats the last sounds; with `sing` the voice is made that long and the rest of the block stays silent (short fade at the end). Empty means the whole block, as before. Migration: none.
- `ai-voice` `sing` also takes the word `auto`: the voice is as long as the linked lyrics file needs (from the start of the block to the end of the last timed line; the last line gets as many bars as the one before it). The build replaces the word by the number, so the plan holds the real length.
- The lyrics file can hold section tags as lines of their own (`[Verse]`, `[Chorus]`, `[Outro]`). They have no bar, are not timed, are passed to ACE with the words and show as heading rows in the lyrics editor. Migration: none (such lines used to be reported as unreadable).
- `ai-voice` is now 0.11: the optional parameter `fit` (`off` or `stretch`). ACE tends to sing a little faster than the music and then runs out; `stretch` slows the voice down without changing its pitch so it ends where the lyrics end (at most 1.6 times as long). The factor is written into the take (`voiceStretch`). Default `off`. Migration: none.
- `ai-voice` 0.12 and `ace-music` 0.2: `steps` is empty by default, which takes the number of steps the chosen ACE model wants (turbo 8, sft and base 50). A block that says `steps` itself keeps that number. The model is a plugin setting (`synthModel`). Migration: none.
- New statement `settings { plugin.name value ... }` for the plugin settings of one song (new static code AMP1112 for a malformed line). The settings of a song are part of the plan of the blocks of that plugin, so choosing another ACE model or isolation gives a new take instead of the cached one. The plugin settings that were stored once per project (`.ample/plugin-settings.json`) are now only the defaults for new songs: a build reads the settings of the song, not those of the project. Migration: a song that relied on project-level settings needs a settings line (the plugin settings screen has "Use the project defaults" and "Apply and back" writes it).
- Plugins can report progress with sub-steps (`ctx.progress(fraction, step, stepFraction)`), shown as bars in the node editor; the server sends the event `build.block`. No change to songs.
- A song that lacks a setting its plugin marks as required fails its blocks with `AMP5370`, which names the setting and the project default; the node menu offers "Use the project default" (writes it into the song's settings line and builds again). New songs are made with **+ New song** (`POST /api/songs`) and start with the project defaults in their settings line. No change to existing songs.
- New effect block `duck` (stable@1.0, category Effects): the sound dips at every hit of a 16-step `pattern` (default every beat) by `depth` dB and comes back over `release` ms, like a sidechain pumping effect. Integer gain, deterministic. Migration: none.
- `ai-voice` `sing auto` also looks at the words: the length is the smaller of the lyrics timeline and an estimate from the text (about four syllables a second plus a breath per line), so a short text is no longer stretched over a long block. Voices with `sing auto` get a new take once.
- ACE plugin 0.5.1: a voice or music block that sits later in the song (`at 5 for 4`) is now made as long as its own bars and placed where the block starts; before, the sound began at the start of the song. Voice takes are made again once.
- ACE plugin 0.6.0: the setting `backend` (`server`, `cli`, `server-then-cli`) with `aceBinFolder` and `cliLmModel`. A plugin can mark settings every song must state itself (`x-songRequired`); a song that does not gets the editor warning `AMP2013` with a quick fix (the project default) and `AMP5370` when it is built. The example songs now state `acestep.backend server`. Migration: songs that use `ai-voice` or `ace-music` need `settings { acestep.backend server }` (the quick fix writes it).

# 2026-10-04
- Added the `midi` file block with project-contained inputs, deterministic track selection, canonical notes output and unsupported-event warnings.
