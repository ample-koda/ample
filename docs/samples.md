# Real instruments: sample drums and guitar

General MIDI tracks can also use the `gm` block and the licensed MuseScore General SF2 pack. Fetch the full GM bank with `node tools/fetch-gm-soundfont.mjs --download`; verify its pinned digest with `--verify`. The `gm` block supports project-contained SoundFonts and `pack:musescore-general`; converted MIDI modules use the pack alias. The SF2 renderer reads only samples selected by the requested bank, program, key and velocity zones and keeps all pitch, envelope and loop arithmetic integer-based. Modulators and SF3 are not yet supported.

Status (2026-10-03): built as part of chunk 98 follow-up. Tested in `tests/Ample.Core.Tests/SamplerTests.cs` (21 tests), `web/lib/test/stepseq.test.mjs` (the pattern grammar of the editors), `web/lib/test/numfield.test.mjs` (number fields) and in the browser (`spec-tests/parity/samples.spec.ts` for building, `samples-ui.spec.ts` for the editors).

The built-in drums, bass and piano are small synthesised sounds. The **drums** block (with a sample kit) and the **guitar** block play **recordings** that you keep in the project, and every knob stays text: the pattern, the strum, the chords, the swing.

## Sample sets

A sample set is a folder of WAV files inside the project, for example `samples/rusty-kit/` or `samples/emily-guitar/`. A block names it with `kit` (drums) or `set` (guitar); a name with a `/` that is a folder holding WAV files is a sample set, anything else (the built-in `kit/tape`, `felt-piano`) is the old synthesised sound.

- **Formats:** 8, 16, 24 or 32-bit PCM and 32-bit float WAV, mono or stereo (mixed down), any sample rate (converted to the build rate with a fixed-point linear interpolation).
- **Where they come from:** your own recordings or free ones. The demo project uses two CC0 sets by Karoryfer Samples (Swirly Drums and Emilyguitar, from the sfzinstruments collection on GitHub). Keep a `samples/README.txt` with the sources and licences; the credits sheet only knows what the library knows.
- **Layers and round robins on disk (2026-10-03):** one flat folder stays. `name.wav` is layer 1, round robin 1; other files are `name_v<L>_r<R>.wav` (1-based, at most 3 digits, `_` as separator because notes contain `#`; `name_v2.wav` and `name_r2.wav` alone are allowed; anything else is a plain name). `NoteOf` and the voice lookup read the base name. The engine still plays layer 1, round robin 1 only (a folder with extra files builds the same bytes as before), but every file is hashed into the plan, so adding one makes a new take. An optional `set.json` next to the WAVs (`layersOffered`, `layersTaken`, `roundRobinsTaken`) records what the source had and what the fetch took. `GET /api/samples` groups by base name and returns `layers`, `roundRobins`, `offered` and per voice `voices`; the picker says "N layers on disk, engine plays M".
- **In plans and the cache:** the hash of every file of the set is part of the plan of the take (input kind `sample`, with the project path). Replace a sample and the block makes a new take; all other blocks stay cached. A take's plan says exactly which files made it.

## Drums

```
drums beat at 1 for 8 {
  kit samples/rusty-kit
  pattern kick:x.......x.....x. snare:....X.......X..o hat:x.x.x.x.x.x.x.x.
  swing 12
  humanize 25
}
```

- **The kit folder** holds one file per voice, named after the voice: `kick.wav`, `snare.wav`, `hat.wav` (and any other name you use in a pattern: `tom.wav`, `crash.wav`, `hat-open.wav`).
- **The pattern** is a name (`beat-A`, `beat-B`, which still work and use `kick`, `snare`, `hat`) or one grid per voice, `voice:steps`, separated by spaces. **16 steps make a bar** (12 in 3/4, or the **steps** of the block, see below); a shorter grid repeats inside the bar (`kick:x...` is four on the floor of sixteenths), a longer grid goes on over the next bars (32 steps are two bars, 48 three). `x` is a hit, `X` an accent, `o` a ghost note, `.` a rest.
- **steps** (1 to 64, drums 1.5): the steps in a bar. Without it a bar has four steps for every beat (16 in 4/4, 12 in 3/4). A step lasts a bar divided by this number, so the same pattern plays at another speed whatever the tempo of the song is: `steps 8` makes every step twice as long (the pattern of 16 steps now lasts two bars, at half the speed), `steps 32` half as long. This is how a drum layer keeps its speed in a song of another tempo. A grid that does not fit the bars (a 20-step grid in a bar of 16) gets the warning AMP2095.
- **swing** (0 to 60): how far the odd steps are pushed later, as a percentage of a step.
- **humanize** (0 to 100): a little deterministic variation in volume (up to 20 %) and timing (up to 5 ms), drawn from the seed of the block: the same song always sounds the same, another seed sounds different.
- **choke** (optional text, template drums 1.3): rules separated by `;`, each `voice:closer,closer` (for example `choke open:hat,pedal`). A hit of a closer voice cuts the sounding hits of the first voice from that moment, with a 7 ms fade. The hits are collected first and mixed in the order they were made, so no rule gives the same bytes as before. A rule without a colon or closers fails with AMP5351.
- **step velocity** (drums renderer version 4): besides `x` (hit), `X` (accent) and `o` (ghost) a step may be a digit 1 (softest) to 9 (loudest), for example `kick:9...5...9...3...`. In the studio, shift+click on a step cycles through the digits 3, 5, 7 and 9, and the velocity lane under each voice sets any digit by clicking or dragging the bars.
- **voice options in the pattern** (drums renderer version 4): a voice grid shorter than 16 steps repeats (a grid of 12 steps is a bar of three beats repeated; the step editor's length box writes this). A voice grid may end with `@` and options separated by commas: `m` mutes the voice, `l<dB>` sets its level (-24 to 12), `p<pan>` pans it (-32 left to 32 right), `s<swing>` gives the voice its own swing in percent (0 to 60), `t<semitones>` tunes the voice up or down (-24 to 24, for toms and percussion), for example `kick:x...x...@l-3,p-8,s30`. A pattern with any panned voice renders stereo (equal-power pan table); without pan the output stays mono. An unknown option fails with AMP5351.
- **map** (optional text, template drums 1.4): replaces voices of the kit by samples of other kit folders, entries separated by `;`, each `voice:folder/sample` (for example `map kick:samples/other/kick2;snare:samples/other/clap`). The mapped kit folders go into the plan of the take. A malformed entry fails with AMP5351, a sample that is not in the folder with AMP5350.
- **fallback** (optional text, template drums 1.4): a kit folder used for pattern voices the kit does not have. Without it a missing voice is an error (AMP5350); with it the voice plays from the fallback kit and the build warns with AMP5256.
- A voice that has no file, or a grid with other characters, fails the block with AMP5350 or AMP5351 and says what the folder has or what is allowed.

## Named patterns and sections (2026-10-08)

A drums block (and a guitar block, for its strum) can hold several **named patterns** and a **song** that puts them in order:

```
drums beat at 1 for 16 {
  kit samples/rusty-kit
  pattern kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
  patterns verse=kick:x...x...x...x... snare:....x.......x... hat:x.x.x.x.x.x.x.x. / fill=kick:xxxxxxxx snare:xxxxxxxx hat:xxxxxxxx
  song pattern*3 fill verse*4 fill
}
```

- `patterns`: `name=voice grids` entries separated by ` / `. Names are letters, digits, `-` and `_`; `pattern`, `beat-A` and `beat-B` are taken. The grids are the same as in `pattern`, with all voice options.
- `song`: the sections in order, each a pattern name and optionally `*bars` (one bar when it is left out). The word `pattern` is the plain pattern of the block; `beat-A` and `beat-B` work too. Bar k of the block plays entry k of the sequence; the sequence repeats when the block is longer. Without `song` the plain pattern plays on every bar, as before.
- A name in `song` that is not a pattern, a repeat that is not a whole number from 1 to 999 and a pattern defined twice are AMP5351. The built-in sound (no kit) takes `patterns` and `song` too.
- For the guitar the entries are strums (`verse=D...D.u.D.u. / fill=DdDdDdDd...`) and the word `pattern` means the plain `strum`.
- In the properties panel the grid has a pattern chooser (New, Duplicate, Rename, Delete) and a strip of sections (chips with their bars, move, remove, drag to reorder); the **Patterns view** (the button in the panel, the block menu) shows the lanes with the samples, the grid and the sections full width. Bass and piano have no pattern grammar of their own; they get one in the planned sequencer block.

## Velocity layers

A voice or note may have several recordings for soft and hard playing. `name.wav` is layer 1, round robin 1; `name_v<L>_r<R>.wav` is layer L, round robin R (`kick_v2_r1.wav`, `c3_v3_r1.wav`). With N the highest layer number of a voice or note, a velocity v (0 to 32768) plays layer `min(N-1, v*N/32769)` (0 based, integer arithmetic); a layer without a file is played by the nearest layer that has one (the lower on a tie). A folder without layer files sounds exactly as before.

- **Drums:** `o` is velocity 14000, `x` 26000, `X` 32768 (humanize changes it a little first).
- **Guitar:** `D` 32768, `U` 26000, `d` 20000, `u` 16000.
- **Piano and bass:** every note of the `midi-clip` has a MIDI velocity (1 to 127, default 96) that is scaled to 0..32768 for the layer and also scales the gain (96 is the old level). In a `melody` write it as a third field: `c4:1:110`. The bass uses the velocity of the lowest note of the bar.
- Every layer file is hashed into the plan, so replacing `kick_v2_r1.wav` makes a new take.

## SFZ sets (2026-10-15, Phase 1 "basis" of `plan/sfz-import/README.md`)

A `set` (not yet `kit`) value may name an `.sfz` file instead of a folder of note-named WAVs, for example `set samples/karoryfer/unruly-drums.sfz`. AMPLE reads the file's own opcodes instead of a filename convention, so a pack's own mapping is used as-is — no manual fetch-and-rename step is needed for a pitched instrument.

- **What is read today:** `<global>`/`<master>`/`<group>`/`<region>` header inheritance, `#include` (verbatim, nested, relative to the *including file's own folder*), `#define` (textual substitution), `sample` (with `default_path`), `lokey`/`hikey`/`key`, `pitch_keycenter` (default: `key`, else `lokey`), `lovel`/`hivel` (0-127, scaled to AMPLE's 0-32768 velocity range the same way MIDI velocity is), `tune` (cents) and `transpose` (semitones, both rounded to AMPLE's whole-semitone `Region.Tune`), `volume`/`gain` (dB), `seq_position` (round robin position), `group`/`off_by` (SFZ03, drum kits only, see below), `amp_velcurve_000`..`amp_velcurve_127` (a static velocity-to-amplitude table, linearly interpolated between the points given, holding the nearest point's level outside its range; applied on top of `volume`/`gain` at note time, replacing the default linear velocity response for that region only — layer/round-robin selection itself stays linear), and `cutoff`/`fil_type`/`fil_veltrack` (a static or velocity-tracked low-pass only: `lpf_1p` one stage, `lpf_2p` — the SFZ default when `fil_type` is left out — two stages in series, reusing the engine's existing integer one-pole filter; `fil_veltrack` (cents, -9600 to 9600) moves the cutoff at max velocity relative to velocity 0, using the same cents-to-ratio math pitch-shifting already uses; any other `fil_type` and `resonance` are kept-and-warned, no filter envelope yet).
- **Also read today (2026-10-15):** `lfo01_freq`/`lfo01_pitch`/`lfo01_delay` (a pitch LFO, triangle wave only - forces the whole block through the per-frame expressive playback path); `fileg_attack`/`decay`/`sustain`/`release`/`hold`/`depth` (a filter envelope, same force-expressive note); `trigger=release` (a release-triggered companion region, see "SFZ sets and release samples" below) and, for a pitched `set` rendered by the `piano` block only, `trigger=first`/`legato`; `xfin_lovel`/`xfin_hivel`/`xfout_lovel`/`xfout_hivel` (a one-sided velocity-edge gain taper on the region `RegionResolver.Choose` already picked, not a true two-region crossfade); `resonance` (dB, an approximation - a gain bump just below the cutoff, not a true resonant filter pole); and, for a drums kit, `off_time` (overrides the block's choke fade) and `polyphony`/`note_polyphony` (caps simultaneous hits of one voice, first-come-first-served).
- **What is not read yet, and is kept-and-warned (AMP5394) rather than silently dropped:** `pan` (no per-region pan field exists yet), `offset`/`end` (sample trimming), `pitch_keytrack` other than 100, non-low-pass filter types, `trigger=first`/`legato` for bass/guitar (see below), `off_mode=normal`, the `<curve>`-block form of curves, LFOs beyond the first/pitch-only, and every `*_onccN` opcode.
- **`*_onccN` (live MIDI CC modulation) is permanently out of scope, not "later":** `amplitude_onccN`, `cutoff_onccN`, `pitch_onccN` and the rest exist for a live performer moving a controller while playing; AMPLE builds a song offline from a static, programmatic `.amplet` sequence, so there is no live CC stream at render time for these opcodes to read. They are read, kept and reported with AMP5394 like any other unread opcode — never silently dropped — so a pack that leans on them for its character is at least visible as "present but not applied." If AMPLE ever grows a live-performance mode this is worth revisiting, but it is not expected to.
- **Loops and amplitude envelopes:** `loop_mode=loop_continuous` and `loop_sustain` both use AMPLE's sustain-loop playback; `no_loop`, `one_shot` and an absent mode do not loop. `loop_start`/`loop_end` are sample-native frame positions, rescaled to the build rate; an omitted end uses the whole decoded sample. `loop_crossfade` is in seconds. AMPLE does not distinguish continuous from sustain looping after note-off. `ampeg_attack`/`decay`/`release`/`hold` (seconds) and `ampeg_sustain` (percent) configure the region's integer ADSR (`hold` is a plateau at full level between attack and decay); an absent envelope leaves the old fixed fade. These apply to pitched `set` regions; filter envelopes and release-triggered samples remain open. See `plan/sfz-import/README.md`.
- **A `kit` (drums) value may also name an `.sfz` file (SFZ05):** each region's key is mapped to a voice name through the General MIDI percussion map (`36`→`kick`, `38`→`snare`, `42`/`44`→`hat`, `46`→`hat-open`, `49`/`52`/`55`/`57`→`crash`, `51`/`53`/`59`→`ride`, every other tom key→`tom`, ...) instead of becoming a pitched region — this is a real, documented standard, not a guess, but several related GM keys deliberately collapse onto one AMPLE voice name (every tom key becomes `tom`). A key the map does not know becomes `key<N>` (still addressable in a `pattern`, just without a friendly name) and warns (AMP5394). A region needs `key`/`pitch_keycenter`/`lokey` to be read at all.
- **`group`/`off_by` (SFZ03, drum kits only):** every region sharing a `group` number is closed by a hit of any region declaring that number as its `off_by` — checked at region precision (SFZ12, 2026-10-16): the choke-and-fade mixing pass looks at each hit's own resolved `Region.Group`/`Region.OffBy`, so when one AMPLE voice name covers more than one region with different group membership (for example a soft, ungrouped articulation layered with a hard, grouped one on the same key), only the hit whose own region is actually in the group gets cut — not every hit of that voice name. `SfzReader` still also derives the older `voice:closer1,closer2` text form into its result (for the documented wire format), but the `drums` block's own hand-written `choke` parameter and the SFZ-sourced region choke are now two independent checks feeding the one mixing pass, not one merged into the other. `off_mode=normal` is not read yet (kept-and-warned); `off_time` overrides the block's whole-block choke fade (see above).
- **Paths are root-jailed, one level wider than the `.sfz` file itself (SFZ06):** every `#include` and `sample=` path must resolve inside the whole project (a project-relative `set`/`kit` value) or inside the matched pack's own folder (a `pack:` value) — never anywhere else on disk. This is wider than just the `.sfz` file's own folder tree on purpose: a real pack's programs and samples commonly sit in sibling folders (confirmed against Karoryfer's "Frankensnare", whose `Programs/*.sfz` reach `../Samples/...`), which a same-folder-only jail would reject outright.
- **Only WAV (PCM or float) samples are read, same as a plain folder set** — a real pack's samples are often FLAC; `fetch-audio-packs.mjs`'s own ffmpeg conversion is still the only path to use a FLAC-only pack's audio with AMPLE today.
- **In plans and the cache:** the `.sfz` file itself, every file it `#include`s and every sample it references are each hashed into the plan (`kind: sample`), exactly like every WAV of a folder-based set today — change any one of them and the block makes a new take, changing an unrelated file next to them does not.
- A damaged or missing `.sfz` file, or one with no usable region, fails the block with AMP5350 (wrapping the underlying AMP5395 detail).

## Per-set gain and tuning

An optional `set.json` in the sample folder, `{ "gain": -3, "tune": 1 }`, evens out packs: `gain` is whole dB (-24 to 12) applied to every region of the set, `tune` is whole semitones (-24 to 24) by which the recordings sound sharp (they are played that much lower, drums included). Block parameters (`gain`, strum, pattern) stay as they are on top. The file is hashed into the plan (`set.json` as a `sample` input), so editing it makes a new take; a file that is not such an object fails the block with AMP5350. No file means gain 0 and tune 0, unchanged bytes.

## Sustain loops and an integer envelope (S08, 2026-10-15)

`set.json` may also carry two optional, independent entries. Neither changes a set that does not use them: without them a region's `LoopMode` stays `none` and the note fades the same fixed way it always has.

- **`loops`**, per base name (the same name a `.wav`/`_v<L>_r<R>.wav` or an SFZ region resolves to): `{ "loops": { "arco": { "start": 1000, "end": 5000, "crossfade": 200 } } }`. `start`/`end`/`crossfade` are sample frames at the project's build rate. Playback runs the raw recording from its own start up to `end`; once a note needs more frames than the recording has, it wraps back to `start` and repeats forever, blending the last `crossfade` frames of each pass into the material just before `start` so the wrap point is not an audible click (`LoopPlayer`, already used standalone before this). A note shorter than the recording is unaffected — only a note that actually runs out of recorded audio ever loops.
- **`envelope`**: one integer ADSR for the whole set, in whole milliseconds and a 0-100 sustain percentage: `{ "envelope": { "attack": 5, "hold": 0, "decay": 50, "sustain": 80, "release": 200 } }`. Applied per note in place of the renderer's usual fixed fade (today: play the recording, then a fixed ~120 ms linear fade-out): attack ramps from silence, an optional `hold` plateaus at full level, decay eases to the sustain level, the note holds at sustain for as long as it is held, then release fades from wherever the envelope had reached. An explicit per-note `rel=` (note expression) always wins over the set's envelope for that one note — a performance override is more specific than the sample's own authored envelope.
- **Where this is wired in today:** the guitar/bass/piano sampled-set note paths (`PlayNote`/`PlayExpressiveNote`, including `.sfz`-sourced pitched sets). The drums `kit` path reads the **`envelope`** since 2026-10-20 (each hit runs through the set's attack/decay/sustain/release over its own length) but never **`loops`**: a one-shot drum hit has nothing to sustain.
- Every region's loop/envelope choice is deterministic and integer (no floating point, no real-time clock): the same song and seed render the same bytes on every machine, exactly like the rest of the sampler.

## Round robins

Several files for the same voice, note and layer (`kick_v1_r1.wav`, `kick_v1_r2.wav`, ...) are different recordings of the same hit. Each hit picks one of them from its own deterministic stream: a `StableRng` seeded with the block seed xor a constant xor a hash of the voice name (drums: one stream per voice; guitar, piano and bass: one stream for the notes). It is a different stream from the humanize one, so the humanize timing and volume of existing takes do not change. The streams start again at every render: the same song gives the same hits on two runs and on another path, another seed gives other choices. The renderers of drums, bass, piano and guitar are version 2.

## Guitar

```
guitar gitaar at 1 for 8 {
  set samples/emily-guitar
  strum D...D.u.D...D.u.
  voicing open
  stagger 22
  gain 0
  from chords.notes -> notes
}
```

- **The set folder** holds recorded notes named by note: `e2.wav`, `a2.wav`, `c3.wav`, `eb3.wav`, `gb3.wav`, `c#4.wav` (C4 is middle C). A note that is not recorded is made from the **nearest** recording, played faster or slower (like a tape), so a set with a recording every third semitone is enough.
- **The chords** come in as notes (the `notes` input, for example from a `midi-clip`). The root of each bar's chord decides the strings.
- **voicing:** `open` (five strings: the root low, the fifth, the octave, the third above that and a high fifth), `triad` (root, third, fifth around middle C) or `power` (root, fifth, octave).
- **strum:** 16 steps per bar (or the **steps** parameter of the block: `steps 8` makes every step twice as long, so the strum plays at half speed whatever the tempo of the song is); several bars separated by `|` cycle by bar number. `D` a loud down-strum, `d` a soft one, `U` a loud up-strum, `u` a soft one, `x` mutes the strings, `.` lets them ring. A strum plays the strings one after the other, **stagger** milliseconds apart (0 to 80, default 22); an up-strum plays them in the opposite order. Strings ring until the next strum.
- **gain** in dB (-24 to 12).
- A set without note-named files, a strum with other characters or an unknown voicing fails the block with AMP5350 or AMP5351.

## In the web UI

Select a drums or guitar block and the properties panel shows a step editor above the parameters (the same text parameters stay below it, and the source stays the truth):

- **Drums:** a **sample kit** chooser (the folders of the project that hold WAV files; the built-in sound is listed as such) and a grid with one row per voice, as wide as the **steps** of the block (16 without it). With the built-in sound the voices are kick, snare, hat, openhat, clap, rim, tom, lowtom, hightom and crash (no pan and tune there). A click cycles a step through rest, hit (x), accent (X), ghost note (o). Add a voice of the kit, remove one, start from a preset (beat-A, beat-B) or clear the grid; a voice without a file is struck through in red with the message the build would give. Typed number fields (with arrows, and optional sliders, see [Web UI](web-ui.md)) write **steps**, **swing** and **humanize**; the guitar has **stagger** and **gain**.
- **Guitar:** a **sample set** chooser (folders with note-named files), a strum row (rest, D, d, U, u, x per step), extra bars for a pattern that changes per bar, and the voicing.
- A pattern or strum that cannot be read as a grid is not hidden: the panel says why and offers a reset; the text field stays editable.
- The editors are disabled while the block is locked or the song has errors, like the other parameters.

## Determinism

Internally a folder becomes an `Instrument` (a list of immutable `Region` records: key and velocity range, root, tune, gain, loop fields, group, sequence position, sample) and `RegionResolver.Pick` / `PickVoice` choose the region for a note or voice, velocity and counter. Drums, guitar, piano and bass all play through it (the guitar shares the nearest-note code of piano and bass). One file is one region, so sound and bytes are unchanged.

All of it is integer arithmetic: the same files, the same song and the same seed give the same samples on every machine (the pitch ratios come from the decimal twelfth root of two, not from floating-point maths). The renderers say so in their toolprint (`ample-sampler`).

## Words from a file

A `lyrics` block names a file with `file lyrics/song.lyr`. When that file exists and is not empty, **its lines are the words** (without the `[bar N]` markers); otherwise the `text` parameter is used (lines separated by ` / `). The hash of the file is part of the plan, so editing the words in the lyrics editor makes the voice a new take, and the web UI marks the build as out of date through `GET /api/doc/inputs`.

## Not built

- No audition of single samples and no waveform view in the UI yet: sets are folders on disk, and the step editors only write the text parameters.
- Sample sets are looked up in the project only (not in linked folders or packs yet).
- The demo packs still hold one hit per note (the mf round robin 1 and the vl8 hits); layers and round robins are for packs that bring them.
- The `bass` and `piano` blocks still synthesise; a sampled piano is the same as the guitar block with another set.
- SFZ reading (see "SFZ sets" above) does not yet cover release-triggered samples, filter modulation/envelope, LFOs or `<curve>` blocks; `plan/sfz-import/README.md` tracks the remaining work.

## Building a set with the assistant (2026-10-20)

The assistant can make a sample set itself. Plan: `plan/sample-set-assistant/README.md`.

- **`ample_sample_set_add`** turns an audio file into one WAV of a set: `source` is a project audio file, or `workspace/<path>` for a fetched preview (mp3, flac, ogg, aiff, m4a; decoded to WAV with ffmpeg),
  `destination` a new `.wav` inside the set folder named by note (`samples/campfire/c4.wav`; a layer is `c4_v2_r1.wav`). It never overwrites and changes only the staged proposal.
- **`loop: true`** finds loop points for that sample and writes them into the folder's `set.json` under its base name (a layer loops under the same name). The search is deterministic: the
  loop starts after the attack (20 to 50 percent of the sample), ends in the tail (55 to 95 percent), and the pair whose ~10 ms of audio before the end best matches the audio before the start wins
  (normalised correlation times how alike the energy is); both points move to the nearest rising zero crossing and the crossfade is ~5 ms. The answer carries `start`, `end`, `crossfade` and a
  `quality` (near 1 is a clean loop). A sample under a quarter of a second, or with no matching stretch, gets `found: false` and plays as a one-shot (right for a pluck). The points are written as plain
  numbers: a build never depends on the finder.
- **`envelope`** writes one ADSR for the whole set (`attack`, `hold`, `decay` and `release` in ms, `sustain` 0-100) next to the loops; a later call keeps the loops already there.
- The block or channel then plays the folder with `set=samples/campfire` (a drums block with `kit=`). No `.sf2` is written (a set is a folder, hashed into the plan like any sample).
