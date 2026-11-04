# Client-side preview demos

Keep these `.amplet` files current whenever a block's local preview support, parameters, fidelity or routing changes. Supported blocks have focused examples; `../clientside-demo.amplet` combines the baseline Mix chain in one AI-free project.

The baseline includes built-in `synth`, `bass`, `felt-piano`, exact seeded built-in `drums` and the optional MuseScore General `gm` soundfont. Canonical `midi-clip` events can pass through the five supported MIDI effects before local synth, bass, felt-piano or sampler preview. Piano, bass and guitar also accept a simple project folder with one mono note-named WAV per pitch and zero humanize; their focused demos document the sample-folder setting. Built-in drums use a cancellable worker and the effective AMPLE block seed. GM SoundFont, sample kits and unsupported/advanced MIDI remain server-rendered. The browser also locally previews `pitch`, `stretch`, `scratch`, `convolution`, `reverb`, `mixer`, `bus`, `send` and `master` in **Build and play preview**. Pitch uses the local `wsola` worker; `voice` uses the server fallback. Stretch uses the local constant-speed worker; `auto(...)` curves use the server fallback. This explicitly demonstrates the hybrid boundary; it does not claim a completely local chain. The focused sampler demo needs the named short WAV copied into its project before it can play.

To verify the optional branch, open the baseline project and disable `soundfont` using the block's enabled/disabled control. The built-in synth branch should still feed the send, mixer and master. Re-enable it to hear both branches. The GM example needs the installed `pack:musescore-general` asset. The baseline and convolution-focused demo need the generated or installed `pack:impulse-responses` pack; run `node tools/make-impulse-responses.mjs` to create its standard `room` preset. No generated audio is checked in. The hybrid preview uses the selected impulse locally when its FFT worker fits the 128 MiB estimate; otherwise it server-renders that block and identifies it in preview status.

## Focused demos

- [Mood scene](direction/mood-scene.amplet) (style data only; no audio generation)
- [Timed lyrics](lyrics/lyrics.amplet) (requires the companion `preview.lyr` in the project at `lyrics/preview.lyr`; data only, no singing)
- [ACE music](voices/ace-music.amplet) (ACE generation stays server-side; an existing take can feed local chorus/master preview; requires the timed-lyrics fixture)
- [AI voice](voices/ai-voice.amplet) (isolated vocal generation stays server-side; an existing vocal take can feed local reverb/master preview; requires the timed-lyrics fixture)
- [Choir layer](voices/choir.amplet) (owner reports server-side rendering; the checked-in ACE-Step manifest does not declare it; followed by local effects; requires the timed-lyrics fixture)
- [Voice clone](voices/voice-clone.amplet) (Python/model inference runs on the AMPLE host; requires the user to add an authorized reference recording at `recordings/reference.wav`)
- [Group](control/group.amplet) (AI-free synth routed through a local group pass-through into local reverb)
- [Scorer](control/scorer.amplet) (AI-free synth can play locally; scoring requires the server-side timing plugin)
- [Variants](control/variants.amplet) (AI-free synth source; generation stays server-side and only existing takes are auditionable)
- [Sequencer](advanced/sequencer.amplet) (embedded built-in bass and triangle channels with local named-lane preview; other channel types retain the server fallback)

The GM and MIDI render demos show their current server-render prerequisites. The sampled guitar demo previews locally when the project folder holds a simple mono note-named WAV set, a plain strum and zero humanize; complex sets use the server. The piano and bass demos also accept simple one-mono-WAV-per-note folders. The built-in drum example exercises the local exact seeded renderer; named sample kits remain server-side.

- [Local synth](instruments/synth.amplet) (direct simple `midi-clip` input; worker-based approximate audio)
- [MIDI clip](instruments/midi-clip.amplet) (local piano-roll data preview, auditioned through the downstream synth)
- [Built-in bass](instruments/bass.amplet) (direct simple `midi-clip`, built-in pattern, zero humanize; may be switched to a mono note-named project sample folder)
- [Sampler](instruments/sampler.amplet) (requires a short browser-decodable WAV at `recordings/sampler-preview.wav` in the demo project)
- [Built-in piano](instruments/piano.amplet) (triangle/saw oscillator preview; may be switched to a mono note-named project sample folder)
- [Built-in drums](instruments/drums.amplet) (local seeded integer renderer for `kit builtin` and the `beat-A`/`beat-B` sequence; sample kits remain server-rendered)
- [General MIDI](instruments/gm.amplet) (server-rendered; requires `pack:musescore-general`)
- [Sampled guitar](instruments/guitar.amplet) (local approximate path for plain mono note-named WAV samples at `samples/guitar`; complex sets use the server)
- [MIDI render](instruments/midi-render.amplet) (server-rendered; requires `midi/input.mid` and `pack:musescore-general`)

- [Bitcrusher](effects/bitcrusher.amplet)
- [Chorus](effects/chorus.amplet)
- [Flanger](effects/flanger.amplet)
- [Phaser](effects/phaser.amplet)
- [Meter](effects/meter.amplet)
- [Limiter](effects/limiter.amplet)
- [Compressor](effects/compressor.amplet)
- [Gate](effects/gate.amplet)
- [Granular](effects/granular.amplet) (uses the AMPLE control seed; locally processed in the hybrid preview when within the worker memory budget)
- [Mixer](mix/mixer.amplet)
- [Bus](mix/bus.amplet)
- [Send](mix/send.amplet)
- [Master](mix/master.amplet)
- [Reverse](effects/reverse.amplet)
- [Cut](effects/cut.amplet)
- [Fade](effects/fade.amplet)
- [Shift](effects/shift.amplet)
- [Crossfade](effects/crossfade.amplet)
- [Join](effects/join.amplet)
- [Splitter](effects/splitter.amplet)
- [Pan](effects/pan.amplet)
- [Width](effects/width.amplet)
- [Tremolo / autopan](effects/tremolo.amplet)
- [Volume](effects/volume.amplet)
- [Normalize](effects/normalize.amplet)
- [Delay](effects/delay.amplet)
- [Convolution](effects/convolution.amplet) (requires the installed `impulse-responses` pack)
- [Scratch](effects/scratch.amplet) (seed-free step-position effect, processed locally in the hybrid preview within the worker memory budget)
- [Pitch](effects/pitch.amplet) (`granular` and `wsola` methods use a local worker; `voice` uses server-rendered audio)
- [Stretch](effects/stretch.amplet) (constant speed uses a local worker; automated `auto(...)` speed curves use server-rendered audio)
- [Denoise](effects/denoise.amplet)
- [Reverb](effects/reverb.amplet)
- [De-esser](effects/deesser.amplet)
- [Distortion](effects/distortion.amplet)
- [Duck](effects/duck.amplet)
- [EQ](effects/eq.amplet)
- [Filter](effects/filter.amplet)
- [Transpose](midi-effects/transpose.amplet) (event transform; instrument audio remains server-rendered)
- [Arpeggiator](midi-effects/arpeggiator.amplet) (AI-free chord clip; the MIDI transform and instrument currently render on the server)
- [Quantize](midi-effects/quantize.amplet) (deliberately off-grid notes; exact local decimal/tie handling remains unimplemented)
- [MIDI humanize](midi-effects/midi-humanize.amplet) (fixed master seed; local preview must read the effective block seed to match the renderer)
- [Chord](midi-effects/chord.amplet) (event transform; instrument audio remains server-rendered)
- [Imported project audio](data/import-audio.amplet) (expects `recordings/preview.wav` in the demo project)
- [Ambience](data/ambience.amplet) (expects `recordings/room-tone.wav`; local seeded segment/crossfade preview with explicit server fallback)
- [MIDI file](data/midi.amplet) (expects `midi/input.mid`; paired file notes feed a local synth, while direct `midi.audio` still uses the installed server GM SoundFont)

The ambience demo exercises exact seeded segment selection and local crossfades; unsupported source files or layouts still use AMPLE's explicit fallback. The MIDI file demo routes paired file notes into the local synth. Connecting `midi.audio` directly still uses the server's bounded tempo handling and GM SoundFont implementation.

The reverse and denoise examples use a built-in MIDI phrase and synth source; those upstream blocks still render on the server while the named effect and final mix/master run locally.
