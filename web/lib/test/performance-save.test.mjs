import test from 'node:test';
import assert from 'node:assert/strict';
import { BUS_INPUTS, saveModel, slug, songText } from '../performance-save.mjs';

const capture = (runs, beatsPerBar = 4) => ({ beatsPerBar, runs });

test('block names are letters, digits, _ and - and start with a letter', () => {
  assert.equal(slug('Hall (live chain)'), 'Hall-live-chain');
  assert.equal(slug('  '), 'clip');
  assert.equal(slug('808 bass'), 'c-808-bass');
  assert.equal(slug('Kick', 'x'), 'Kick');
  assert.equal(slug('é à ü'), 'e-a-u');
});

test('the first launch is bar 1 and the others keep their distance; a launch on the bar lines is exact', () => {
  const model = saveModel(capture([
    { clip: 'kick', lane: 'Drums', from: 7.5, to: 23.5 },
    { clip: 'pad', lane: 'Chords', from: 11.5, to: 27.5 },
    { clip: 'beat', lane: 'Drums', from: 23.5, to: 39.5 },
  ]), (id) => id.toUpperCase());
  assert.deepEqual(model.runs.map((r) => [r.clip, r.bar, r.bars, r.offsetBeats, r.exact]), [['kick', 1, 4, 0, true], ['pad', 2, 4, 0, true], ['beat', 5, 4, 0, true]]);
  assert.equal(model.approximate, 0);
  assert.equal(model.runs[0].label, 'KICK');
});

test('a launch inside a bar gets an offset in beats and a stop inside a bar plays on to the end of that bar, and is counted', () => {
  const model = saveModel(capture([{ clip: 'a', lane: 'L', from: 0, to: 9.5 }, { clip: 'b', lane: 'M', from: 2, to: 12 }]));
  const [a, b] = model.runs;
  assert.deepEqual([a.bar, a.bars, a.exact], [1, 3, false]);
  assert.deepEqual([b.bar, b.bars, b.offsetBeats, b.exact], [1, 3, 2, true]);
  assert.equal(model.approximate, 1);
});

test('a bar that is a little early or late is still that bar; a run is at least a bar; nothing gives nothing', () => {
  const model = saveModel(capture([{ clip: 'a', lane: 'L', from: 0, to: 4.01 }, { clip: 'b', lane: 'L', from: 3.99, to: 4.3 }]));
  assert.deepEqual(model.runs.map((r) => [r.bar, r.bars]), [[1, 1], [2, 1]]);   // 3.99 beats is the start of bar 2
  assert.equal(saveModel(capture([])).runs.length, 0);
  assert.equal(saveModel(undefined).runs.length, 0);
  assert.equal(saveModel(capture([{ clip: 'a', lane: 'L', from: 2, to: 2 }, { clip: 'b', lane: 'L', from: Number.NaN, to: 3 }])).runs.length, 0);
});

test('the song has a block for every run, a bus for every lane and a set bus, and no master', () => {
  const model = saveModel(capture([
    { clip: 'kick', lane: 'Drums', from: 0, to: 8 }, { clip: 'pad', lane: 'Chords', from: 4, to: 12 }, { clip: 'kick', lane: 'Drums', from: 16, to: 20 },
    { clip: 'pad', lane: 'Chords', from: 18, to: 24 },
  ]), (id) => (id === 'kick' ? 'Kick' : 'Pad'));
  const text = songText({ title: 'Live 1', source: 'night', when: '2026-11-15 11:42', tempo: 126, model, files: new Map([['kick', 'set-clips/Kick.wav'], ['pad', 'set-clips/Pad.wav']]) });
  assert.match(text, /^composition "Live 1" \{ ample 1  tempo 126  master-seed 1 \}/);
  assert.match(text, /import-audio Kick-1 at 1 for 2 \{\n  file "set-clips\/Kick.wav"\n  loop on\n\}/);
  assert.match(text, /import-audio Kick-2 at 5 for 1 /);
  assert.match(text, /import-audio Pad-1 at 2 for 2 /);
  assert.match(text, /import-audio Pad-2 at 5 for 2 \{\n  file "set-clips\/Pad.wav"\n  loop on\n  offset 2 beats\n\}/);
  assert.match(text, /bus lane-Drums \{\n  inputs 2\n  from Kick-1.audio -> audio1\n  from Kick-2.audio -> audio2\n\}/);
  assert.match(text, /bus set \{\n  inputs 2\n  from lane-Drums.audio -> audio1\n  from lane-Chords.audio -> audio2\n\}/);
  assert.ok(!/^master /m.test(text));
  assert.match(text, /# Saved from a performance of "night" on 2026-11-15 11:42/);
});

test('more runs than a bus takes are spread over more busses of the lane, which the set bus sums', () => {
  const runs = Array.from({ length: BUS_INPUTS + 3 }, (_, k) => ({ clip: 'hat', lane: 'Hats', from: k * 4, to: k * 4 + 4 }));
  const text = songText({ title: 'Many', tempo: 120, model: saveModel(capture(runs)), files: new Map([['hat', 'c/Hat.wav']]) });
  assert.match(text, /bus lane-Hats \{\n  inputs 16\n/);
  assert.match(text, /bus lane-Hats-2 \{\n  inputs 3\n/);
  assert.match(text, /bus lane-Hats-sum \{\n  inputs 2\n  from lane-Hats.audio -> audio1\n  from lane-Hats-2.audio -> audio2\n\}/);
  assert.match(text, /bus set \{\n  inputs 2\n  from lane-Hats-sum.audio -> audio1\n\}/);
  assert.equal([...text.matchAll(/^import-audio /gm)].length, BUS_INPUTS + 3);
});

test('a run without a file is left out, and too many lanes say so', () => {
  const model = saveModel(capture([{ clip: 'a', lane: 'L', from: 0, to: 4 }, { clip: 'b', lane: 'L', from: 0, to: 4 }]));
  const text = songText({ title: 'x', tempo: 100, model, files: new Map([['a', 'c/A.wav']]) });
  assert.equal([...text.matchAll(/^import-audio /gm)].length, 1);
  const many = saveModel(capture(Array.from({ length: BUS_INPUTS + 1 }, (_, k) => ({ clip: `c${k}`, lane: `L${k}`, from: 0, to: 4 }))));
  assert.throws(() => songText({ title: 'y', tempo: 100, model: many, files: new Map(many.runs.map((r) => [r.clip, 'c/x.wav'])) }), /more than a bus takes/);
});

test('the moves of the mix of a lane are automation on its bus and on a pan and a filter block after it; a lane that did not move has none (phase 5)', async () => {
  const { createMixLog, laneCurves } = await import('../performance-mixlog.mjs');
  const log = createMixLog();
  const lane = (gain, cutoff, pan = 0) => new Map([['Drums', { gain: 1, pan: 0, cutoff: null }], ['Chords', { gain, pan, cutoff }]]);
  log.record(0, lane(1, null));
  for (let k = 1; k <= 20; k++) log.record(k * 0.4, lane(1, Math.round(20000 * (0.02 ** (k / 20)))));   // the filter of the chords closes over eight beats
  log.record(12, lane(0, 400));   // a mute at bar 4
  const model = saveModel(capture([{ clip: 'kick', lane: 'Drums', from: 0, to: 16 }, { clip: 'pad', lane: 'Chords', from: 0, to: 16 }]), (id) => id);
  const mix = new Map(['Drums', 'Chords'].map((name) => [name, laneCurves(log.snapshots(), name, { shiftBeats: model.shiftBeats, beatsPerBar: 4 })]));
  const text = songText({ title: 'Mixed', tempo: 120, model, files: new Map([['kick', 'c/Kick.wav'], ['pad', 'c/Pad.wav']]), mix });
  assert.match(text, /bus lane-Chords \{\n  inputs 2\n  from pad-1.audio -> audio1\n\}/);
  assert.match(text, /volume lane-Chords-volume \{\n  gain auto\(1:0, 3.96:0, 4:-60\)\n  from lane-Chords.audio -> audio\n\}/, 'the volume is a volume block: the gain of a bus is not automated');
  assert.match(text, /filter lane-Chords-filter \{\n  cutoff auto\(1:20000, [^)]+\)\n  from lane-Chords-volume.audio -> audio\n\}/);
  assert.match(text, /bus lane-Drums \{\n  inputs 2\n  from kick-1.audio -> audio1\n\}/, 'the lane that did not move has no extras');
  assert.ok(!text.includes('lane-Drums-filter') && !text.includes('lane-Drums-pan') && !text.includes('lane-Chords-pan'));
  assert.match(text, /bus set \{\n  inputs 2\n  from lane-Chords-filter.audio -> audio1\n  from lane-Drums.audio -> audio2\n\}/);   // the lanes in the order they first played
});

test('a lane with a kept mix that never moved gets constants', async () => {
  const { createMixLog, laneCurves } = await import('../performance-mixlog.mjs');
  const log = createMixLog();
  log.record(0, new Map([['A', { gain: 0.5, pan: 0.5, cutoff: 800 }]]));
  const model = saveModel(capture([{ clip: 'x', lane: 'A', from: 0, to: 4 }]));
  const text = songText({ title: 'Kept', tempo: 100, model, files: new Map([['x', 'c/X.wav']]), mix: new Map([['A', laneCurves(log.snapshots(), 'A')]]) });
  assert.match(text, /volume lane-A-volume \{\n  gain -6 dB\n  from lane-A.audio -> audio\n\}/);
  assert.match(text, /pan lane-A-pan \{\n  pan 16\n  from lane-A-volume.audio -> audio\n\}/);
  assert.match(text, /filter lane-A-filter \{\n  cutoff 800 Hz\n  from lane-A-pan.audio -> audio\n\}/);
});
