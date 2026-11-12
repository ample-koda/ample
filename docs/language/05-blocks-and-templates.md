# 05 Blocks and templates

A **template** defines a block type: its ports, parameters, class, version, whether it is dynamic, and which plugin it needs. The catalogue is data: one file per template in `../../spec/templates/<id>.json`, validated by `../../spec/schema/template.schema.json`. The generated help (`help.html`) renders one card per template; this page defines the rules.

The provisional `voice-clone` block reads a project voice sample from `file` and speaks its `text`. `model` selects Chatterbox (the default), Qwen3 or XTTS (non-commercial; it renders only after the licence is accepted in the settings; a song written before 2026-11-12 without a `model` line now gets Chatterbox, so write `model xtts` to keep the old voice); `language` is passed to that model. Qwen3 additionally needs `reference_text`, unless `auto_detect on` is selected. The sample is hashed into the build plan, so changing it invalidates the generated take. Common audio formats are accepted and converted to WAV internally when needed. Model weights are configured locally and are not downloaded by the plugin.

## Version pin

A block header has no class and no number: a block follows the current version of its type (every built-in block is `1.0`). To say which version a block was written for, add `pinned@<major.minor>` after the name:

```text
eq tone pinned@1.0 {
}
```

A pin is a request, and lint L006 reports a pinned block whose version is older than the type's current one. Block versions are `major.minor` only (no patch, no `^` or `~`); components keep full semantic versions (`pinned@^1.2`). A change that alters the sound is a new major version. Live blocks cannot be pinned: bake them.

## Parameters

`splitter` takes `audio` and exposes mono audio outputs `left` and `right`. It preserves channel level and duration; mono input is duplicated onto both outputs. The node preview plays the original input. Connect a render chain's materialized audio output to the splitter; the splitter itself does not forward render recipes.

```amplet
splitter channels {
  from level.audio -> audio
}
eq left_eq {
  from channels.left -> audio
}
eq right_eq {
  from channels.right -> audio
}
```

The `volume` effect accepts either audio (`audio`) or a render recipe (`render`). It exposes three fixed audio outputs: `left` and `right` are individual mono output channels; `audio` carries both output channels as stereo (before v1 it was called `mixed`). The `render` output continues the recipe. The block preview plays the mixed output.

The two channel faders use `left_gain` and `right_gain` (−60 to +12 dB, default 0). With `linked on` (default), moving either fader moves both by the same amount and preserves their difference, stopping at the shared bounds. Properties can set exact values independently. Linking is a UI setting and does not alter audio by itself. The existing `gain` parameter remains a common gain, default 0 dB. Positive gain can clip rendered PCM.

`mono on` averages the input channels before applying common gain, separate channel gains and mutes. Left and right remain separate ports with their connections intact; turning mono off restores stereo routing. `left off` and `right off` silence their respective output channels. Mono input feeds both channels. All settings are available in source and Properties; the canvas and advanced controls provide L/R faders, meters, mutes and Mono. `enabled off` bypasses processing while preserving every output.

```amplet
volume level {
  left_gain -3 dB
  right_gain -6 dB
  linked on
  left on
  right off
  mono off
  from kit.audio -> audio
}
```
- A block accepts only the parameters its template lists (`AMP2003` otherwise; the hint lists the valid ones).
- The template gives each parameter a type (`number` or `text`) and a default. A missing parameter takes the default.
- A parameter appears once per block (`AMP1109`).
- Seed parameters (`seed`, `lm_seed`) are the word `derived` or an unsigned 64-bit integer 0 to 18446744073709551615 (`AMP2011`). A custom seed is allowed and recorded (L009).
- A template may declare `modalEditor: true` for text that benefits from the property's expanded `…` editor, and `multiline: true` when line breaks are meaningful. Multiline values are stored as the existing `\n` source escape and restored as line breaks by the property editor; a single-line value visibly wraps but line breaks are converted to spaces when saved.
- A template may declare `affectsOutput: false` on a parameter that is an execution policy rather than a sound setting: a hint about how the renderer gets to its result (for example a time/memory trade-off) where every option gives byte-identical audio. It defaults to `true`. A studio panel may show such parameters apart from the musical controls (denoise's `optimize` does); a renderer that sets it false must prove the claim (same input, same output, for every option) before relying on it, since nothing else enforces it.

The universal block header flag `disabled` is independent of template parameters and effect `enabled off` bypass. It keeps the block visible but returns no take. Blocks depending on its required inputs, including audio and render effect chains, become implicitly disabled without a build failure; the block reports the warning AMP5253 so it is visible why it produced nothing. Optional inputs, such as mixer channels, omit the disabled branch so other channels can still render. A mixer with no connected inputs or only disabled inputs produces no take and is implicitly disabled. Implicit disabling is derived from connections and disappears when an upstream input becomes active; it does not write a `disabled` flag into downstream source.

Audio sources expose a render output alongside ordinary audio. Built-in audio effects accept an `audio` input for post-render processing or a `render` recipe input for a source variant. Both connections may be present when at least one is disabled; only active connections determine the effect input. The next audio connection materializes the whole recipe as a variant take; explicitly building a render-connected effect also materializes that effect's variant. Intermediate render links create no takes. Effects remain independent blocks with their own parameters.

Block takes use local time: their first placed part starts at local bar 1, without leading silence from the arrangement position. Moving the whole block preserves its take and the takes of its audio/render effects; relative gaps between multiple parts remain part of its content. Mixer and master inputs apply arrangement placement and range cropping, and their plans include that placement. Moving a block invalidates the arranged mix, which can be regenerated using cached local takes. The `local-v1` plan marker separates these takes from earlier song-length renders; existing selected frozen takes retain their original content until explicitly replaced.

The midi-clip template also accepts an optional structured notes value. It can be a space-separated list of `bar:beat:length:pitch:velocity` entries or a persistent `notes { note { ... } }` body with named fields. Beat and length use quarter-note beats with up to six decimal places by default; set `ppq` to use exact integer ticks per quarter note instead. Eight-field entries can append `channel:bank:program` to retain source instrument routing; generated nine-field entries add a tie identity so bar-split sustains remain distinct from repeated notes. A compact entry may append `;pan=`, `;fine=`, `;rel=`, `;cut=`, `;slide=` or `;port=`; the language server completes these keys after a semicolon, and invalid values produce AMP2012. The expanded body accepts `bar`, `start`, `length`, `pitch`, `velocity`, optional routing fields and optional `tie-id`; `#` comments stay in source beside the note or field they describe. Notes are polyphonic, must fit within one 4/4 bar, and represent a tie across a bar as one entry in each bar. GM/SF2, synth and sample-set piano/bass/guitar render per-note expression; slide renders a glide without a retrigger. MIDI module import/export retains supported note-start expression and writes pitch-bend ramps for slide notes. Portamento settings round-trip as CC65/CC5; renderer portamento is not implemented. The empty default keeps existing chords and melody behavior unchanged. The short `melody` form has dynamics: a note can carry its own velocity (`c4:1:110`), a marker word `pp p mp mf f ff` (velocity 32 to 112, `f` = 96 = the default) sets the velocity of the notes after it, and `<` (crescendo) or `>` (decrescendo) ramps the notes after it, by their place in time, to the next marker word (to `ff` or `pp` at the end of the melody when none follows): `melody "mp c4 d4 < e4 g4 ff c5:4"`. A note with its own velocity is never changed by a marker, and markers take no time.

Compact notes additionally accept `;tie=N` (integer 0 through 2000000) to join consecutive bar-split pieces without explicitly setting channel, bank or program. Expanded `tie-id` also works without routing fields. Nine-field ties remain supported; writing both forms on one entry, or repeating `tie`, is invalid. A long piano-roll note is stored as these valid bar segments; the editor merges them for display and resizing. This preserves the connected instrument's inherited routing when a note grows over a bar line.

The `midi` block exposes `notes`, the complete typed `sequence`, and `audio` ports. Connect the sequence to `midi-render` to render the file through a project SF2 file or the installed GM pack with a gain control. MIDI, `midi-render` and GM instrument blocks default to an integer soft limiter before PCM output; set `limiter off` only when hard clipping is wanted. The song mixer also soft-limits summed inputs. If an SF2 bank lacks a source bank/program, the GM renderer uses the matching GM program (or piano) and reports the fallback. The notes port remains available for piano, GM and other instrument blocks.

The `midi` data block reads a project `.mid` or `.midi` file. Its `track` parameter is `all` or a zero-based track index; its `notes:midi` output has the same shape as `midi-clip`, and its optional `warnings:text` output describes unsupported note-adjacent events. The block may also retain `source-file`, `variant-file` and `variant-module` for an editable MIDI variant: **Edit MIDI variant** opens the module, saving it exports a new active MIDI asset, and the context menu can switch between Original and Edited.

**File parameters and what can be dropped on them.** A parameter of `kind: file` lists the extensions it takes in `accept` (for example `.mid`, `.midi` for the `file` of `midi`; `.sf2` for `gm` and `midi-render`); a parameter of `kind: sampleset` takes a folder. The web app uses these lists to decide which blocks a file can be used in (Use in song, drag and drop). A new file parameter that should take drops needs an `accept` list.

## Fades: fadein, fadeout and the fade and crossfade blocks
A fade is a value that goes from silence to full (a fade in) or from full to exact silence (a fade out). It is written as the value of a volume `gain` or of the `mix` of an effect:

```
volume v {
  from drums.audio -> audio
  gain fadeout(5, 2, equal)      # from bar 5 over two bars, ends in silence
}
```

- `fadein(start, length [, shape])` and `fadeout(start, length [, shape])`. A fade in is silent before its start and full after its end; a fade out is full before its start and **exactly silent** after its end.
- **Units:** a plain number is **bars** (`5` is the start of bar 5, `5.5` is half way through it; the start counts from bar 1; the length `0.5` is half a bar). A number with an `s` is **seconds** (`12s`, `1.5s`); a start in seconds counts from the beginning of the song. Start and length may use different units. Up to six decimals.
- **Shapes:** `lin` (a straight line in volume, the default), `equal` (equal power: a fade in and a fade out of the same length add up to constant power for unrelated sounds, so use it for a crossfade) and `exp` (starts slowly, ends fast).
- All arithmetic is integer, so every machine gets the same bytes. A fade on a pan, a cutoff or a threshold is refused with AMP5371.

The **`fade`** block does the same with one audio input and one output (`direction` out or in, `start`, `length`, `shape`). The **`crossfade`** block has two inputs, `a` and `b`: `a` fades out and `b` fades in over the same stretch (`start`, `length`, `shape`, equal power by default). Both build the same fade text and use the same engine as the formula, so a block and a formula with the same settings give the same bytes. The curve editor (the ∿ button) has a **Fade…** button for a gain and a mix.

**Patterns and sections.** `drums` and `guitar` take two optional parameters, `patterns` (named patterns, `name=grids / name=grids`) and `song` (the sections in order, `verse*4 fill chorus*8`); see Samples.

**Time stretch.** `stretch.speed` is a percentage from 25 to 400; 50 plays at half speed and keeps pitch. It also accepts `auto(bar:percent, ...)`, such as `speed auto(1:100, 9:50)`, for a linear speed ramp by bar. The rendered block keeps its length. This does not change the project's tempo.

## Example, needs and sound source in a template
Besides ports and parameters, a template file in `spec/templates/` carries three fields for people and tools:

- `example` (required): the text of a small `.amplet` song in v1 form that uses a block of this type. A test parses every example with the spec and requires no errors and a block of the template's type. The docs show it on the card of the block, and demos and mp3 renders can be made from it.
- `needs` (optional list of sentences): what the block needs besides its input, for example a drum kit folder (a `sampleset` parameter), a SoundFont or recording (a `file` parameter) or a plugin. It is derived from the parameters of kind `sampleset`, `file` and `engine` and from the plugin.
- `soundSource` (optional, `true`): the block makes a sound by itself from notes, files or settings, so its example can be rendered to hear it. Effects are not sound sources: they need a source in front of them.

Adding a template means adding its `example`; the `description` should say in one or two sentences what the block does, what it needs and what it gives.

## Dynamic blocks and plugins
A template can be `dynamic` (its result can change, for example an AI take) and can require a **plugin**. A block whose plugin is not installed is `AMP5001`, a warning: AMPLE reports it instead of silently skipping the block. Plugins are listed in `../../spec/plugins.json` (opt-in or built in).

## The catalogue (spec 0.1, provisional)
`midi-clip`, `midi`, `gm`, `synth` (a small subtractive synth on MIDI notes), `sampler` (one sample played on notes, moved in pitch from a root note), `import-audio` (plays a WAV file of the project), the MIDI effects `transpose`, `midi-humanize`, `quantize`, `arpeggiator` and `chord` (notes in, notes out, between a clip and an instrument), `send` (a shared effect bus: `through` and `send` outputs), `drums`, `bass`, `piano`, the effects `eq`, `distortion`, `delay`, `reverb`, `compressor`, `filter`, `limiter`, `width`, `pan`, `gate`, `deesser`, `chorus`, `flanger`, `phaser`, `bitcrusher`, `pitch`, `stretch` and `duck` (audio in, audio out, with an `enabled off` bypass), `ai-voice`, `ace-music`, `mood-scene`, `choir`, `lyrics`, `weather`, `import-audio`, `mixer`, `master`, `variants`, `scorer`, `group`. `gm` plays a zero-based General MIDI program and bank from the installed MuseScore General SF2 pack or a project-local `.sf2` file. `ai-voice` is for isolated vocals: its `voice` parameter is a **vocal direction**, not a selected speaker identity, and it requires linked lyrics. Both ACE blocks expose `guidance` (CFG) as a stored generation parameter, visibly resolved to 7 for Base/SFT and 1 for Turbo; changing it changes the requested ACE guidance scale and can change the sound. Keep it fixed when comparing seeds. `ace-music` intentionally allows accompaniment and has optional lyrics, so it can make an instrumental. `mood-scene.style` supplies reusable scene direction to either ACE block. Adding or changing a template is a language change ([99](99-maintenance.md)).

**Delay in time with the song.** A `delay` block takes `sync` (`off` by default, `1/1` through `1/32`, and the triplets `1/4t`, `1/8t`, `1/16t`) to lock the echo time to the tempo: `sync 1/8` puts the echo one eighth note after the sound, in whole samples of the bar, and `time` is not read while `sync` is on. A song without `sync` renders exactly as before (the new parameter is optional, so no `ample` number changes).

**Filter wobble.** A `filter` block can move its cutoff with `lfo_rate` (`off`, `1/1` through `1/32`, and triplet rates `1/4t`, `1/8t`, `1/16t`), `lfo_depth` (0–8 octaves above the base cutoff), and `lfo_shape` (`sine`, `triangle`, `saw`, `square`). The oscillator phase follows the song bar, so rendering a selected range keeps the same phase as rendering the whole song.

## Example
```amplet run
composition "demo" { ample 1  tempo 92  master-seed 424242 }
midi-clip chords {
  chords Am F C G
}
piano piano pinned@2.0 at 1 for 8 as verse {
  from chords.notes -> notes
}
```

`chords` takes one chord per bar. A chord is a root and a quality (`Am`, `F`, `G7`, `Cmaj7`, `Dsus4`, `Bbm7`, `Fm6`, `Cadd9`); `C/E` is a slash chord that puts the named bass note lowest (a first inversion here; a bass note that is not in the chord is added below it); two chords joined by a dot (`Am.F`) share one bar, two beats each. Without a slash or a dot a chord sounds exactly as before (root position).

## Parameter names for time and speed

One scheme for every effect: a **duration** is written in milliseconds with its unit (`attack 10 ms`), a **position in a file** in seconds, a **free-running speed** as `rate` in Hz, and a **tempo lock** as a note value (`sync`). A template may list `aliases` on a parameter: extra names the parser accepts and rewrites to the real name before anything else reads the song. `chorus`, `flanger` and `phaser` accept `rate 0.5` (Hz) for `period 2000 ms` (the value is converted); `filter` accepts `lfo_sync` for `lfo_rate`; `arpeggiator` accepts `sync` for `rate`. The old names stay valid. Completion offers the real names only.
## Effect limits

`filter` has `resonance` (0 to 100 %, automatable like the cutoff: `resonance auto(1:0, 9:90)`); its `type` can also be `bandpass` or `notch`, and `slope` (dB per octave: 12 default, 24, 36 or 48) runs the filter two, three or four times for a steeper cut. `eq` has a low shelf, a high shelf and one fixed mid band, no free peak; for free bands use `peq` (parametric EQ: six bands `band1_type` (peak, lowshelf, highshelf, notch, lowcut, highcut; lowcut and highcut are 12 dB per octave high-pass and low-pass), `band1_hz`, `band1_gain`, `band1_q` up to `band6_...`; `band1_enabled off` leaves a band out, and a band at 0 dB changes nothing). `gate` reacts to the level, so on a steady pad it does nothing: use `tremolo` with `shape gate`, a `sync` note value and a step `pattern` such as `"x.xx.xx.x.xx.x.x"` (x open, . closed, the letters `a` to `i` a level from 10 to 90 percent open such as `"xdx.bxdx.xdx.x"`; depth 100 for silence): a tempo-locked trance gate with a 2 ms fade at every change. See `plan/effect-limits.md` for what would fix these.
