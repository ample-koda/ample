// Writes the built-in demos of AMPLE Remix into web/remix/demos: the hand-written ones from the texts below, the component demos from
// demo-projects/components, and catalog.json. Run it again after editing a text here; the demos in web/remix/demos are generated files (edit this script, not them).
// The sounds (web/remix/assets) are made by tools/lite-demos/make-assets.mjs. Usage: node tools/lite-demos/make-demos.mjs

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'web/remix/demos');
const KIT = { pack: 'drums-rusty', to: 'samples/kit', files: ['kick.wav', 'snare.wav', 'hat.wav', 'open.wav', 'tom.wav'] };
const demos = [];

const add = (id, name, description, category, level, tags, files, assets = [], extra = {}) => demos.push({ id, name, description, category, level, tags, files: Object.keys(files), assets, ...extra, _files: files });

// ------------------------------------------------------------------ Basics
add('hello-melody', 'Hello melody', 'A first tune on a synth: a clip of notes, a synth to play it, a master.', 'Basics', 'Beginner', ['synth', 'notes', 'first'], { 'song.amplet': fs.readFileSync(path.join(out, 'hello-melody/song.amplet'), 'utf8') });
add('chords-bass', 'Chords and a bass', 'Chords from names (Am F C G), a pad and a sine bass, mixed.', 'Basics', 'Beginner', ['chords', 'bass', 'mixer'], { 'song.amplet': fs.readFileSync(path.join(out, 'chords-bass/song.amplet'), 'utf8') });

add('basic-drums', 'Basic drums', 'A drum kit and a pattern: one grid per voice, sixteen steps to a bar, a little swing.', 'Basics', 'Beginner', ['drums', 'pattern', 'kit', 'swing'], {
  'song.amplet': `composition "basic drums" { ample 1  tempo 100  master-seed 3 }

# A drums block plays a kit: a folder of samples named after the voices (kick.wav, snare.wav, ...).
# One grid per voice, 16 steps make a bar: x a hit, X an accent, o a ghost note, a digit 1-9 the loudness of the hit.
drums beat at 1 for 4 {
  kit samples/kit
  pattern kick:x...x...x...x... snare:....X.......X... hat:x.x.x.x.x.x.x.x. open:..............x.
  swing 10
  humanize 20
}

master out {
  from beat.audio -> audio
}
` }, [KIT]);

add('bass-drums', 'Bass and drums', 'A bass line from a melody text, played by the built-in 808 bass, over a beat.', 'Basics', 'Beginner', ['bass', 'drums', 'melody', 'mixer'], {
  'song.amplet': `composition "bass and drums" { ample 1  tempo 100  master-seed 4 }

# melody "note:length ...": a note name and octave, then the length in beats (r:1 is a rest). Four beats make a bar.
midi-clip line at 1 for 4 {
  melody "e2:1 e2:1 g2:1 a2:1 e2:1 e2:1 b2:1 a2:1 c3:1 c3:1 b2:1 a2:1 g2:2 e2:2"
  bars 4
}

bass b at 1 for 4 {
  from line.notes -> notes
}

drums beat at 1 for 4 {
  kit samples/kit
  pattern kick:x...x...x...x... snare:....X.......X... hat:x.x.x.x.x.x.x.x.
  humanize 15
}

mixer mix {
  inputs 4
  gains "-4 0 0 0"
  from b.audio -> audio1
  from beat.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` }, [KIT]);

add('sections', 'An arrangement in sections', 'Blocks that start and stop: "at 5 for 8" puts a block on bars 5 to 12. Intro, verse and chorus out of the same few blocks.', 'Basics', 'Intermediate', ['arrangement', 'sections', 'at', 'for'], {
  'song.amplet': `composition "sections" { ample 1  tempo 110  master-seed 5 }

# "at 1 for 4" = from bar 1, four bars long. The same pattern is used in several places; only where it plays changes.
midi-clip riff at 1 for 4 {
  melody "a2:1 a2:1 c3:1 e3:1 a2:1 a2:1 d3:1 e3:1 f2:1 f2:1 a2:1 c3:1 g2:1 g2:1 b2:1 d3:1"
  bars 4
}

drums intro at 1 for 4 {
  kit samples/kit
  pattern hat:x.x.x.x.x.x.x.x.
}

drums verse at 5 for 8 {
  kit samples/kit
  pattern kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
}

drums chorus at 13 for 8 {
  kit samples/kit
  pattern kick:x...x...x...x... snare:....X.......X... hat:xxxxxxxxxxxxxxxx open:..x...x...x...x.
}

synth lead at 5 for 16 {
  wave saw
  cutoff 2500 Hz
  release 150 ms
  from riff.notes -> notes
}

mixer mix {
  inputs 4
  gains "-3 -3 -3 -9"
  from intro.audio -> audio1
  from verse.audio -> audio2
  from chorus.audio -> audio3
  from lead.audio -> audio4
}

master out {
  from mix.audio -> audio
}
` }, [KIT]);

add('seeded', 'Generative and seeded', 'A melody that walks over a scale and a Euclidean rhythm, both made from a seed: the same seed gives the same music every time.', 'Basics', 'Intermediate', ['generative', 'seed', 'walk', 'euclid'], {
  'song.amplet': `composition "seeded" { ample 1  tempo 100  master-seed 7 }

# The notes are not written: they are made. walk() steps over a scale, euclid() spreads hits over a bar.
# Change a seed (seed=7) and you get another piece; build again with the same seed and you get exactly the same one.
midi-clip tune at 1 for 8 {
  notes walk(D, minor, steps=32, step=1beat, octave=4, range=3, velocity=90, seed=7)
}

midi-clip pulse at 1 for 8 {
  notes euclid(5, 16, pitch=38, step=0.25beat, velocity=100) |> repeat(8)
}

synth lead at 1 for 8 {
  wave triangle
  release 400 ms
  from tune.notes -> notes
}

synth bass at 1 for 8 {
  wave square
  cutoff 900 Hz
  release 80 ms
  from pulse.notes -> notes
}

mixer mix {
  inputs 4
  gains "-4 -9 0 0"
  from lead.audio -> audio1
  from bass.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` });

// ------------------------------------------------------------------ Instruments
add('sampled-bass', 'A sampled bass', 'The bass block with a folder of recorded notes: AMPLE picks the nearest sample and moves it to the note.', 'Instruments', 'Intermediate', ['bass', 'samples', 'set'], {
  'song.amplet': `composition "sampled bass" { ample 1  tempo 90  master-seed 8 }

midi-clip line at 1 for 4 {
  melody "a1:1.5 a1:0.5 c2:1 e2:1 f1:1.5 f1:0.5 a1:1 c2:1 g1:1.5 g1:0.5 b1:1 d2:1 e1:2 e1:2"
  bars 4
}

# "set" names a folder of the project that holds one recording per note (a1.wav, c2.wav ...). Without it the built-in 808 plays.
bass b at 1 for 4 {
  set samples/bass
  from line.notes -> notes
}

master out {
  from b.audio -> audio
}
` }, [{ pack: 'bass-meat', to: 'samples/bass', files: ['a1.wav', 'c2.wav', 'eb1.wav', 'gb1.wav', 'a2.wav', 'c3.wav', 'gb2.wav'] }]);

add('guitar-strum', 'A strummed guitar', 'A guitar from recorded notes, strummed by a pattern (D down, u up, . rest): chords go in, a strum comes out.', 'Instruments', 'Intermediate', ['guitar', 'strum', 'chords', 'samples'], {
  'song.amplet': `composition "guitar" { ample 1  tempo 100  master-seed 9 }

midi-clip chords at 1 for 4 {
  chords Am F C G
  bars 4
}

guitar g at 1 for 4 {
  set samples/guitar
  strum D...D.u.D.u.D.u.
  voicing open
  from chords.notes -> notes
}

master out {
  from g.audio -> audio
}
` }, [{ pack: 'guitar-emily', to: 'samples/guitar', files: ['a2.wav', 'c3.wav', 'e2.wav', 'eb3.wav', 'gb2.wav', 'a3.wav', 'c4.wav', 'gb3.wav', 'a4.wav'] }]);

add('piano', 'The built-in piano', 'The piano block needs no files: chords and a melody on top of each other.', 'Instruments', 'Beginner', ['piano', 'chords', 'melody'], {
  'song.amplet': `composition "piano" { ample 1  tempo 84  master-seed 10 }

midi-clip chords at 1 for 8 {
  chords Am F C G Am F G E
  bars 8
}

midi-clip tune at 1 for 8 {
  melody "e5:2 c5:1 d5:1 e5:2 g5:2 f5:2 e5:1 d5:1 c5:2 e5:2 d5:4 e5:2 c5:2 b4:2 c5:2 a4:4 c5:2 b4:1 a4:1 e5:4"
  bars 8
}

piano keys at 1 for 8 {
  gain -6
  from chords.notes -> notes
}

piano melody at 1 for 8 {
  from tune.notes -> notes
}

mixer mix {
  inputs 4
  gains "-4 0 0 0"
  from keys.audio -> audio1
  from melody.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` });

add('delay-llama', 'Delay Llama', 'A chant sung by a llama (a formant voice): the vowel and the mouth move over time with auto(...), notes can glide and bend.', 'Instruments', 'Intermediate', ['llama', 'voice', 'automation', 'formant'], {
  'song.amplet': `composition "Delay Llama" { ample 1  tempo 84  master-seed 20261025 }

# A slow chant in A minor sung by the delay-llama block. auto(bar:value, ...) moves a value over the song:
# the vowel goes from u to a and back, the mouth opens and closes.
midi-clip chant at 1 for 8 {
  bars 8
  notes 1:0:2:57:92 1:2:2:60:96 2:0:3:64:104 2:3:1:62:96 3:0:4:60:100 4:0:2:57:92 4:2:2:55:90 5:0:2:57:100 5:2:2:64:106 6:0:4:69:112;bend=-300 7:0:2:67:100 7:2:2:64:96 8:0:4:57:104
}

delay-llama llama {
  vowel auto(1:100, 3:30, 5:0, 7:60, 9:100)
  mouth auto(1:15, 3:85, 6:95, 8:40, 9:20)
  voice 40
  glide 160 ms
  vibrato 40%
  unison 3
  echo 30%
  gain -4 dB
  from chant.notes -> notes
}

midi-clip pad-notes at 1 for 8 {
  bars 8
  chords "Am F C G Am F G Am"
}

synth pad at 1 for 8 {
  wave saw
  attack 600 ms
  release 900 ms
  cutoff 1400 Hz
  gain -16 dB
  from pad-notes.notes -> notes
}

mixer demo {
  inputs 4
  from llama.audio -> audio1
  from pad.audio -> audio2
}

master out {
  from demo.audio -> audio
}
` });

// ------------------------------------------------------------------ Effects
const SYNTH_LINE = `midi-clip tune at 1 for 4 {
  melody "a3:0.5 c4:0.5 e4:0.5 a4:0.5 g4:1 e4:1 f3:0.5 a3:0.5 c4:0.5 f4:0.5 e4:1 c4:1 g3:0.5 b3:0.5 d4:0.5 g4:0.5 f4:1 d4:1 e3:2 b3:2"
  bars 4
}`;

add('effects-chain', 'An effects chain', 'A synth through a filter, a little distortion, a delay and a reverb: each effect is a block that takes the sound of the one before.', 'Effects', 'Beginner', ['effects', 'chain', 'filter', 'delay', 'reverb', 'distortion'], {
  'song.amplet': `composition "effects chain" { ample 1  tempo 105  master-seed 11 }

${SYNTH_LINE}

synth lead at 1 for 4 {
  wave saw
  cutoff 9000 Hz
  from tune.notes -> notes
}

# Every effect takes "audio" and gives "audio"; "from a.audio -> audio" connects them in a row.
filter tone {
  type lowpass
  cutoff 1800 Hz
  from lead.audio -> audio
}

distortion grit {
  drive 9 dB
  mix 35%
  from tone.audio -> audio
}

delay echo {
  time 285 ms
  feedback 35%
  mix 25%
  from grit.audio -> audio
}

reverb room {
  size 55%
  mix 22%
  from echo.audio -> audio
}

master out {
  from room.audio -> audio
}
` });

add('filters-lfo', 'Filters and an LFO', 'A filter that sweeps by itself (lfo_rate, lfo_depth) and another that opens over the song with auto(...).', 'Effects', 'Intermediate', ['filter', 'lfo', 'automation', 'sweep'], {
  'song.amplet': `composition "filters" { ample 1  tempo 120  master-seed 12 }

midi-clip chords at 1 for 8 {
  chords Am F C G Am F G E
  bars 8
}

synth pad at 1 for 8 {
  wave saw
  attack 80 ms
  release 300 ms
  cutoff 9000 Hz
  from chords.notes -> notes
}

# lfo_rate is a note length (1/4 = a quarter note), lfo_depth how many octaves the cutoff moves.
filter wobble {
  type lowpass
  cutoff 900 Hz
  lfo_rate 1/4
  lfo_depth 1.5 oct
  lfo_shape triangle
  from pad.audio -> audio
}

# auto(bar:value, ...) writes a curve over the bars (whole numbers, the unit is the one of the parameter): the cutoff opens from 300 Hz to 5000 Hz over the 8 bars.
filter open {
  type lowpass
  cutoff auto(1:300, 9:5000)
  from wobble.audio -> audio
}

master out {
  from open.audio -> audio
}
` });

add('delay-reverb', 'Delay and reverb', 'Two ways to make space: a delay that repeats, a reverb that surrounds. A parallel mix keeps the dry sound.', 'Effects', 'Beginner', ['delay', 'reverb', 'space', 'mixer'], {
  'song.amplet': `composition "space" { ample 1  tempo 96  master-seed 13 }

midi-clip tune at 1 for 4 {
  melody "e5:0.5 r:1.5 g5:0.5 r:1.5 a5:0.5 r:1.5 e5:0.5 r:1.5 d5:0.5 r:1.5 b4:0.5 r:1.5 c5:4"
  bars 4
}

piano keys at 1 for 4 {
  from tune.notes -> notes
}

delay echo {
  time 0.75 beat
  feedback 45%
  mix 100%
  from keys.audio -> audio
}

reverb hall {
  size 85%
  decay 80%
  mix 100%
  from keys.audio -> audio
}

# The dry sound, the echo and the hall are three inputs of a mixer: gains are in dB, the dry one is the loudest.
mixer mix {
  inputs 4
  gains "0 -10 -8 0"
  from keys.audio -> audio1
  from echo.audio -> audio2
  from hall.audio -> audio3
}

master out {
  from mix.audio -> audio
}
` });

add('automation', 'Automation', 'Values that change over the song: auto(bar:value, ...) on a gain, a pan and a filter.', 'Effects', 'Intermediate', ['automation', 'auto', 'pan', 'gain', 'filter'], {
  'song.amplet': `composition "automation" { ample 1  tempo 110  master-seed 14 }

${SYNTH_LINE}

synth lead at 1 for 4 {
  wave square
  cutoff auto(1:600, 3:6000, 5:600)
  gain auto(1:-18, 2:-8, 5:-8)
  from tune.notes -> notes
}

pan swing {
  pan auto(1:-80, 3:80, 5:-80)
  from lead.audio -> audio
}

master out {
  from swing.audio -> audio
}
` });

add('sidechain', 'Sidechain pump', 'A pad that ducks on every kick: duck dips the sound on a pattern, so the beat stays in front.', 'Effects', 'Intermediate', ['duck', 'sidechain', 'pump', 'drums'], {
  'song.amplet': `composition "pump" { ample 1  tempo 124  master-seed 15 }

midi-clip chords at 1 for 4 {
  chords Am F C G
  bars 4
}

synth pad at 1 for 4 {
  wave saw
  attack 40 ms
  release 400 ms
  cutoff 2400 Hz
  from chords.notes -> notes
}

# duck dips the sound for 160 ms at every x of the pattern, like the sidechain of a compressor keyed by the kick.
duck pump {
  depth 12 dB
  release 220 ms
  pattern x...x...x...x...
  from pad.audio -> audio
}

drums beat at 1 for 4 {
  kit samples/kit
  pattern kick:x...x...x...x... hat:..x...x...x...x.
}

mixer mix {
  inputs 4
  gains "-5 0 0 0"
  from pump.audio -> audio1
  from beat.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` }, [KIT]);

add('lofi-crush', 'Lo-fi and a bit crusher', 'Fewer bits and a lower sample rate on the whole beat, a little chorus on the keys: dust on purpose.', 'Effects', 'Beginner', ['lofi', 'bitcrusher', 'chorus', 'drums'], {
  'song.amplet': `composition "lofi crush" { ample 1  tempo 82  master-seed 16 }

midi-clip chords at 1 for 4 {
  chords Fmaj7 Em7 Dm7 G7
  bars 4
}

piano keys at 1 for 4 {
  gain -4
  from chords.notes -> notes
}

chorus wobble {
  depth 7 ms
  period 2600 ms
  mix 60
  from keys.audio -> audio
}

drums beat at 1 for 4 {
  kit samples/kit
  pattern kick:x.....x...x..... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
  swing 18
  humanize 25
}

bitcrusher dust {
  bits 9
  downsample 3
  mix 70
  from beat.audio -> audio
}

mixer mix {
  inputs 4
  gains "0 -3 0 0"
  from wobble.audio -> audio1
  from dust.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` }, [KIT]);

// ------------------------------------------------------------------ Components (from demo-projects/components)
const comp = (file) => fs.readFileSync(path.join(root, 'demo-projects/components', file), 'utf8');
const lite = (text) => text.replaceAll('Testjes/Components/', '').replaceAll('components/components/', 'components/');
add('comp-notes', 'A reusable Notes component', 'A component is a typed block you write once and use many times: notes in, notes out, properties as knobs.', 'Components', 'Intermediate', ['component', 'amplec', 'notes'],
  { 'song.amplet': comp('01-basic.amplet'), 'components/triple-notes.amplec': comp('components/triple-notes.amplec') });
add('comp-audio', 'An audio component', 'An echo and a reverb packed into one block with two knobs, and a layout for its panel.', 'Components', 'Intermediate', ['component', 'audio', 'echo', 'ui'],
  { 'song.amplet': comp('06-audio-dream-echo.amplet'), 'components/dream-echo.amplec': comp('components/dream-echo.amplec') });
add('comp-nesting', 'Components that call components', 'The fanfare calls two other components like functions: octave layers, then a rising echo.', 'Components', 'Advanced', ['component', 'nesting', 'call'],
  { 'song.amplet': comp('12-nested-notes.amplet'), 'components/fanfare.amplec': comp('components/fanfare.amplec'), 'components/octave-layers.amplec': comp('components/octave-layers.amplec'), 'components/echo-notes.amplec': comp('components/echo-notes.amplec') });
add('comp-mixed', 'A component with an instrument inside', 'Notes go in, a choir of llamas and a reverb come out: one block, an instrument inside it.', 'Components', 'Advanced', ['component', 'llama', 'instrument', 'mixed'],
  { 'song.amplet': comp('11-mixed-llama-choir.amplet'), 'components/llama-choir.amplec': comp('components/llama-choir.amplec') });
add('comp-sidechain', 'A pump as a component', 'The sidechain pump of the Sidechain demo as a component with two knobs.', 'Components', 'Intermediate', ['component', 'duck', 'sidechain', 'audio'],
  { 'song.amplet': comp('08-audio-sidechain.amplet'), 'components/sidechain-pump.amplec': comp('components/sidechain-pump.amplec') }, [KIT]);

add('gm-sf2', 'A SoundFont of your own', 'The gm block plays a project SoundFont (an .sf2 file). This small one is an upright piano made of nine CC0 recordings; drop in your own .sf2 to change the sound.', 'Instruments', 'Intermediate', ['soundfont', 'sf2', 'gm', 'piano'], {
  'song.amplet': `composition "A SoundFont of your own" { ample 1  tempo 76  master-seed 31 }

midi-clip chords at 1 for 8 {
  chords Am F C G Am F G E
  bars 8
}

midi-clip tune at 1 for 8 {
  melody "e5:2 c5:1 d5:1 e5:2 g5:2 f5:2 e5:1 d5:1 c5:2 e5:2 d5:4 e5:2 c5:2 b4:2 c5:2 a4:4 c5:2 b4:1 a4:1 e5:4"
  bars 8
}

# file is the SoundFont of this project; bank and program pick the preset inside it
gm keys at 1 for 8 {
  file samples/piano.sf2
  bank 0
  program 0
  gain -6
  from chords.notes -> notes
}

gm melody at 1 for 8 {
  file samples/piano.sf2
  from tune.notes -> notes
}

mixer mix {
  inputs 4
  gains "-4 0 0 0"
  from keys.audio -> audio1
  from melody.audio -> audio2
}

master out {
  from mix.audio -> audio
}
` }, [{ pack: 'piano-sf2', to: 'samples', files: ['piano.sf2'] }]);

const CHANT = `midi-clip chant at 1 for 8 {
  bars 8
  notes 1:0:2:57:92 1:2:2:60:96 2:0:3:64:104 2:3:1:62:96 3:0:4:60:100 4:0:2:57:92 4:2:2:55:90 5:0:2:57:100 5:2:2:64:106 6:0:4:69:112 7:0:2:67:100 7:2:2:64:96 8:0:4:57:104
}

delay-llama llama {
  vowel auto(1:100, 3:30, 5:0, 7:60, 9:100)
  mouth auto(1:15, 3:85, 6:95, 8:40, 9:20)
  voice 40
  glide 120 ms
  vibrato 30%
  echo 0%
  gain -4 dB
  from chant.notes -> notes
}
`;
const voiceDemo = (id, name, description, level, tags, text) => add(id, name, description, 'Advanced', level, tags, { 'song.amplet': text });

voiceDemo('vocoder', 'Vocoder: the llama talks through a synth', 'A vocoder gives the spectrum of one sound (the voice, the modulator) to another (the carrier, a saw chord). The voice here is the built-in llama, so no recording is needed.', 'Advanced', ['vocoder', 'voice', 'llama', 'synth'], `composition "Vocoder" { ample 1  tempo 84  master-seed 41 }

${CHANT}
midi-clip pad-notes at 1 for 8 {
  bars 8
  chords "Am F C G Am F G Am"
}

synth carrier at 1 for 8 {
  wave saw
  cutoff 6000 Hz
  release 200 ms
  from pad-notes.notes -> notes
}

vocoder robot {
  bands 24
  sibilance 35 %
  from llama.audio -> modulator
  from carrier.audio -> carrier
}

mixer mix {
  inputs 4
  gains "0 -18 0 0"
  from robot.audio -> audio1
  from llama.audio -> audio2
}

master out {
  from mix.audio -> audio
}
`);

voiceDemo('autotune', 'Autotune on a voice', 'The llama sings slightly out of tune on purpose (a wide vibrato and glide); autotune pulls it to the scale. Switch it off with enabled off to hear the difference.', 'Intermediate', ['autotune', 'voice', 'llama', 'pitch'], `composition "Autotune" { ample 1  tempo 84  master-seed 42 }

${CHANT.replace('vibrato 30%', 'vibrato 60%').replace('glide 120 ms', 'glide 300 ms')}
autotune tuned {
  key "A minor"
  method spectral
  speed 20 ms
  amount 100%
  enabled on
  from llama.audio -> audio
}

master out {
  from tuned.audio -> audio
}
`);

voiceDemo('chop-voice', 'Chop a voice into slices', 'chop cuts a sound into slices (here every bar of the chant; with slices auto it finds the words from the level) and plays them back in a new order. The pattern names slices (1 is the first), - is a rest and 3*2 plays slice 3 twice in one step.', 'Intermediate', ['chop', 'slices', 'voice', 'llama'], `composition "Chop a voice" { ample 1  tempo 84  master-seed 43 }

${CHANT}
chop chopped {
  slices 0 2.9 5.7 8.6 11.4 14.3 17.1 20
  pattern "1 3 - 2 4*2 - 3 1"
  step 1
  from llama.audio -> audio
}

mixer mix {
  inputs 4
  gains "0 0 0 0"
  from chopped.audio -> audio1
}

master out {
  from mix.audio -> audio
}
`);

voiceDemo('pitch-stretch', 'Pitch and stretch: one voice, three', 'The same voice a fifth up (pitch), at half speed (stretch) and the original: a small choir made of one sound with no extra recordings.', 'Intermediate', ['pitch', 'stretch', 'voice', 'llama'], `composition "Pitch and stretch" { ample 1  tempo 84  master-seed 44 }

${CHANT}
pitch fifth {
  semitones 7
  method wsola
  from llama.audio -> audio
}

stretch slow {
  speed 50
  from llama.audio -> audio
}

mixer mix {
  inputs 4
  gains "0 -6 -8 0"
  from llama.audio -> audio1
  from fifth.audio -> audio2
  from slow.audio -> audio3
}

master out {
  from mix.audio -> audio
}
`);

// ------------------------------------------------------------------ Genres (songs made of components, kept in tools/lite-demos/genres; the paths of their imports are made project-relative)
const genreFiles = (folder) => {
  const base = path.join(root, 'tools/lite-demos/genres', folder);
  const files = {};
  const walk = (dir, prefix) => { for (const entry of fs.readdirSync(dir, { withFileTypes: true })) { if (entry.isDirectory()) walk(path.join(dir, entry.name), `${prefix}${entry.name}/`); else files[`${prefix}${entry.name}`] = fs.readFileSync(path.join(dir, entry.name), 'utf8').replaceAll(`Testjes/Components/${folder}/`, ''); } };
  walk(base, '');
  return files;
};
const genre = (folder, id, name, description, level, tags, assets = [KIT], extra = {}) => {
  const files = genreFiles(folder);
  const entry = Object.keys(files).find((f) => f.endsWith('.amplet'));
  add(id, name, description, 'Genres', level, tags, files, assets, { entry, ...extra });
};
genre('house', 'house', 'House', 'Deep house at 124 BPM: bass, offbeat keys, a pad and a pluck, arranged in sections by components (loop, arrange), a kick duck and a master.', 'Intermediate', ['house', 'components', 'arrangement', 'drums']);
genre('dnb', 'drum-and-bass', 'Drum and bass', 'Liquid drum and bass at 174 BPM: a reese bass, liquid keys and pad, the arrangement and the mix are components.', 'Intermediate', ['dnb', 'components', 'bass', 'drums']);
genre('lofi', 'lofi', 'Lo-fi', 'A lo-fi chill beat at 80 BPM: a warm bass, tape keys, a hazy pad and a muted lead, with a swung kit.', 'Intermediate', ['lofi', 'chill', 'components', 'drums']);
genre('synthwave', 'synthwave', 'Synthwave', 'Retro synthwave at 100 BPM: an analog bass, neon brass, a sparkling arpeggio and a lead.', 'Intermediate', ['synthwave', 'retro', 'arpeggio', 'components']);
genre('trance', 'trance', 'Trance', 'Uplifting trance at 138 BPM: a rolling bass, a supersaw, a pad and an anthem lead, with a build and a breakdown.', 'Intermediate', ['trance', 'supersaw', 'components', 'drums']);
genre('genambient', 'ambient', 'Generative ambient', 'Ambient with no written melody: two seeded random walks over a scale, a drone pad and a sub. Change a seed and it is another piece.', 'Intermediate', ['ambient', 'generative', 'seed', 'walk'], []);
genre('techno', 'techno', 'Techno', 'A modern techno track at 126 BPM from custom components: an acid bass, stabs, a pad, a lead, a drum bus and a kick pump, with the arrangement as properties.', 'Advanced', ['techno', 'components', 'ui', 'arrangement']);


// ------------------------------------------------------------------ Advanced (large songs about technique, kept in tools/lite-demos/advanced)
const advanced = (folder, id, name, description, level, tags, assets = [KIT]) => {
  const base = path.join(root, 'tools/lite-demos/advanced', folder);
  const files = {};
  const walk = (dir, prefix) => { for (const entry of fs.readdirSync(dir, { withFileTypes: true })) { if (entry.isDirectory()) walk(path.join(dir, entry.name), `${prefix}${entry.name}/`); else files[`${prefix}${entry.name}`] = fs.readFileSync(path.join(dir, entry.name), 'utf8'); } };
  walk(base, '');
  add(id, name, description, 'Advanced', level, tags, files, assets, { entry: 'song.amplet' });
};
advanced('mozart', 'mozart', 'Mozart: Eine kleine Nachtmusik', 'The exposition of Mozart K525, first movement (55 bars, 1:32), for string quintet. A sequencer block plays a .ampleseq score with one lane per voice; each voice has its own eq and seat, then a small room. Public domain score, MIT sounds.', 'Advanced', ['classical', 'strings', 'sequencer', 'ampleseq', 'mozart'], []);
advanced('midi-chains', 'midi-chains', 'MIDI effect chains', 'One short clip of root notes (about ten) becomes a pad, an arpeggiated pluck, a bass and a random shimmer, by chains of MIDI effects: chord, arpeggiator, quantize, humanize and transpose, with a loop component.', 'Advanced', ['midi', 'arpeggiator', 'chord', 'quantize', 'humanize', 'transpose', 'seed']);
advanced('transitions', 'transitions', 'Transitions', 'The moves between sections as effects: a reversed reverb swell into a downbeat, a DJ rewind, a crossfade from a clean to a crushed lead, a granular break with an auto-pan and a fade-out.', 'Advanced', ['reverse', 'rewind', 'crossfade', 'granular', 'fade', 'bitcrusher']);
advanced('porch-song', 'porch-song', 'Porch song', 'A whole little song in exactly 60 seconds: one guitar block with four named strums and a song order, a bass with no written notes (chords go down two octaves and an arpeggiator walks them), a beat per section, a 16-note piano hook and a fade-out.', 'Advanced', ['guitar', 'strum', 'song', 'arrangement', 'arpeggiator', 'fade'],
  [KIT, { pack: 'guitar-emily', to: 'samples/guitar', files: ['a2.wav', 'c3.wav', 'e2.wav', 'eb3.wav', 'gb2.wav', 'a3.wav', 'c4.wav', 'gb3.wav', 'a4.wav'] }, { pack: 'bass-meat', to: 'samples/bass', files: ['a1.wav', 'c2.wav', 'eb1.wav', 'gb1.wav', 'a2.wav', 'c3.wav', 'gb2.wav'] }]);
advanced('mix-routing', 'mix-routing', 'Mix routing', 'A 24-bar song about the wiring, not the notes: buses, send blocks into one shared reverb and echo, a compressor keyed by the kick, eq, width, a limiter and a meter.', 'Advanced', ['mixing', 'bus', 'send', 'sidechain', 'master']);

// ------------------------------------------------------------------ write
fs.mkdirSync(out, { recursive: true });
advanced('control-room', 'control-room', 'Llama control room', 'Played by curves: curve blocks make slow and fast moves, fire turns each crossing into a note, three llamas sing the notes with a mouth and a vowel that the curves steer, and a follow block listens to the llamas and ducks the pad whenever one sings.', 'Advanced', ['control', 'curve', 'fire', 'follow', 'map', 'llama', 'generative'], [KIT]);
advanced('cars-and-music', 'cars-and-music', 'Cars passing the music', 'A street answers the music: a follow block listens to rain with a car that passes and makes a control, and that control brings in a beat, swings an arpeggio from left to right, opens a filter (through a map in Hz), makes a reverb wetter and plays a low note for every car.', 'Advanced', ['control', 'follow', 'map', 'fire', 'pan', 'volume', 'rain', 'cars'], [KIT, { pack: 'rainy-cars', to: 'samples/street', files: ['rain.wav', 'car.wav'] }]);
for (const d of demos) {
  for (const [file, text] of Object.entries(d._files)) {
    const target = path.join(out, d.id, file);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, lite(text), 'utf8');
  }
}
fs.writeFileSync(path.join(out, 'catalog.json'), JSON.stringify(demos.map(({ _files, ...d }) => d), null, 2) + '\n');
console.log(`${demos.length} demos written to ${path.relative(root, out)}`);
