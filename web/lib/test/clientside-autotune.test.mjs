import test from 'node:test';
import assert from 'node:assert/strict';
import { applyCorrections, autotuneSettings, corrections, detectPitch, formatFixes, keyScale, noteName, noteNumber, noteRuns, parseFixes } from '../clientside-autotune.mjs';

const RATE = 16000;
const tone = (hz, seconds = 3) => Float32Array.from({ length: Math.floor(seconds * RATE) }, (_, i) => {
  const t = i / RATE;
  return (Math.sin(2 * Math.PI * hz * t) + 0.5 * Math.sin(4 * Math.PI * hz * t) + 0.25 * Math.sin(6 * Math.PI * hz * t)) * 0.18;
});
const pitch = (sound) => {
  const voiced = [...detectPitch(sound, RATE).midi].slice(50, -50).filter((m) => !Number.isNaN(m)).sort((a, b) => a - b);
  return voiced[Math.floor(voiced.length / 2)];
};
const tune = (sound, params, melody = [], fixes = []) => {
  const track = detectPitch(sound, RATE);
  const { correction } = corrections(track, autotuneSettings(params, track), melody, fixes);
  return applyCorrections([sound], RATE, track, correction)[0];
};

test('the pitch is followed and a sharp voice is pulled to the note', () => {
  assert.ok(Math.abs(pitch(tone(440)) - 69) < 0.1);
  const sharp = tone(452);
  assert.ok(pitch(sharp) > 69.35);
  const tuned = tune(sharp, { key: 'chromatic', speed: '0 ms', humanize: '0%' });
  assert.equal(tuned.length, sharp.length);
  assert.ok(Math.abs(pitch(tuned) - 69) < 0.1, `pitch ${pitch(tuned)}`);
});

test('the melody and the fixes decide the note', () => {
  const melody = [{ from: 0, to: 3 * RATE, note: 48 }];
  assert.ok(Math.abs(pitch(tune(tone(440), { speed: '0', humanize: '0' }, melody)) - 72) < 0.15);
  const fixes = parseFixes('0s-3s B4', RATE, RATE * 2);
  assert.ok(Math.abs(pitch(tune(tone(440), { key: 'chromatic', speed: '0', humanize: '0' }, melody, fixes)) - 71) < 0.15);
});

test('fixes and keys are read, written back and wrong ones are explained', () => {
  const fixes = parseFixes('7.9s-8.4s G4 vibrato 0%; 4.2s-4.7s E4; 1-2 off', 1000, 2000);
  assert.deepEqual(fixes.map((f) => f.note), [null, 64, 67]);
  assert.equal(formatFixes([{ from: 4.2, to: 4.7, note: 64, vibrato: null }, { from: 1, to: 2, note: null }]), '1s-2s off; 4.2s-4.7s E4');
  assert.throws(() => parseFixes('at 4 seconds sing e', 1000, 2000), /not a fix/);
  assert.equal(noteNumber('Bb2'), 46);
  assert.equal(noteName(73), 'C#5');
  assert.throws(() => keyScale('H lydian', { midi: [] }), /not a key/);
  assert.deepEqual(keyScale('A minor', { midi: [] }).scale, [true, false, true, false, true, true, false, true, false, true, false, true]);
  const notes = [67, 69, 71, 72, 74, 76, 78, 79, 67, 71, 74, 79];
  const song = Float32Array.from(notes.flatMap((n) => [...tone(440 * 2 ** ((n - 69) / 12), 0.5)]));
  assert.equal(keyScale('auto', detectPitch(song, RATE)).name, 'G major');
});

test('the notes are grouped into runs for the window', () => {
  const track = detectPitch(tone(452, 1), RATE);
  const { target } = corrections(track, autotuneSettings({ key: 'chromatic' }, track));
  const runs = noteRuns(track, target, RATE);
  assert.equal(runs.length, 1);
  assert.equal(runs[0].note, 69);
  assert.ok(runs[0].sung > 69.3);
});

test('short pieces of notes join their neighbour', async () => {
  const { mergeRuns } = await import('../clientside-autotune.mjs');
  const runs = [{ from: 0, to: 0.5, note: 60, sung: 60.2 }, { from: 0.5, to: 0.54, note: 61, sung: 60.6 }, { from: 0.54, to: 1, note: 60, sung: 60.1 }, { from: 2, to: 2.03, note: 64, sung: 64 }];
  const merged = mergeRuns(runs);
  assert.equal(merged.length, 1);
  assert.deepEqual([merged[0].from, merged[0].to, merged[0].note], [0, 1, 60]);
});

test('a pitch curve is read like the engine reads it, and bends a tone', async () => {
  const { pitchCurve } = await import('../clientside-pitch.mjs');
  // 120 BPM at 16 kHz: a bar is 32000 samples; from 0 to 12 semitones over bars 1 to 3
  const curve = pitchCurve('auto(1:0, 3:12)', '', RATE, 120, 1, 3 * 32000);
  assert.equal(curve[0], 0);
  assert.equal(curve[32000], 6);
  assert.equal(curve[3 * 32000 - 1], 12);
  // with range 12 the values are percent of an octave
  assert.equal(pitchCurve('auto(1:0, 3:100)', '12', RATE, 120, 1, 3 * 32000)[32000], 6);
  const { bendChannels } = await import('../clientside-autotune.mjs');
  const bent = bendChannels([tone(440, 2)], RATE, () => 12)[0];
  assert.ok(Math.abs(pitch(bent) - 81) < 0.3, `pitch ${pitch(bent)}`);
});

test('the bend of a clip and of its notes is read like the engine reads it', async () => {
  const { clipBendCurve } = await import('../clientside-pitch.mjs');
  const { parse, format } = await import('../notes-model.mjs');
  // a note with a bend keeps it word for word
  assert.equal(format(parse('1:0:2:60:100;bend=200', 1_000_000)), '1:0:2:60:100;bend=200');
  // 120 BPM at 16 kHz: a bar is 32000 samples; a note of two beats (16000 samples) glides to +2 at its end
  const curve = clipBendCurve({ notes: '1:0:2:60:100;bend=200' }, RATE, 120, 1, 32000);
  assert.equal(curve[0], 0);
  assert.ok(Math.abs(curve[8000] - 1) < 0.01);
  assert.equal(curve[20000], 0);
  // the curve of the clip: percent of bend_range (2 when empty)
  const wheel = clipBendCurve({ bend: 'auto(1:0, 2:100)' }, RATE, 120, 1, 32000);
  assert.ok(Math.abs(wheel[16000] - 1) < 0.01);
  assert.equal(clipBendCurve({ notes: '1:0:2:60:100' }, RATE, 120, 1, 100), null);
});
