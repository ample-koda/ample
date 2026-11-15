import test from 'node:test';
import assert from 'node:assert/strict';
import { barLevels, barList, clippedBars, compareBars, compareText } from '../performance-compare.mjs';
import { saveModel, songText } from '../performance-save.mjs';
import { createLaneBuses, songBalance, songCutoffHz } from '../performance-audio.mjs';

test('the pan of a lane is the balance of the song: the far side gets quieter, the near side is left alone, in steps of 1/32', () => {
  assert.deepEqual(songBalance(0), { left: 1, right: 1 });
  const hard = songBalance(1);
  assert.equal(hard.right, 1);
  assert.ok(Math.abs(hard.left) < 1e-9, 'all the way right: nothing left');
  const quarter = songBalance(0.25);   // 8 of 32
  assert.ok(Math.abs(quarter.left - Math.sqrt(0.75)) < 1e-9 && quarter.right === 1);
  assert.deepEqual(songBalance(-0.25), { left: 1, right: quarter.left });
  assert.deepEqual(songBalance(0.5 / 32), songBalance(1 / 32), 'a position between two steps goes to the nearer one, as the saved pan does');
});

test('the filter of a lane has its -3 dB point where the filter block of the song has it', () => {
  assert.ok(Math.abs(songCutoffHz(1000) - 1554) < 1e-9);
  assert.equal(songCutoffHz(20000, 44100), 44100 * 0.45, 'never above what the context can do');
});

test('a lane bus balances with two gains when the context can split channels, and sets the Q of the filter', () => {
  const log = [];
  const param = (name) => ({ value: 0, setTargetAtTime(v) { log.push([name, v]); this.value = v; }, cancelScheduledValues() {} });
  const node = (name, extra = {}) => ({ name, connect() {}, disconnect() {}, ...extra });
  let gains = 0;
  const ctx = {
    currentTime: 1, sampleRate: 48000,
    createGain: () => node('gain', { gain: param(`gain${gains++}`) }),
    createBiquadFilter: () => node('filter', { frequency: param('cutoff'), Q: { value: 1 } }),
    createChannelSplitter: () => node('split'), createChannelMerger: () => node('merge'),
    createStereoPanner: () => { throw new Error('the stereo panner of the browser is not used'); },
  };
  const buses = createLaneBuses({ ctx, output: node('out') });
  buses.input('A');
  buses.apply(new Map([['A', { gain: 1, pan: 0.5, cutoff: 1000 }]]));
  const left = log.filter(([n]) => n === 'gain2').at(-1)[1], right = log.filter(([n]) => n === 'gain3').at(-1)[1];
  assert.ok(Math.abs(left - Math.sqrt(0.5)) < 1e-9 && right === 1);
  assert.ok(Math.abs(log.filter(([n]) => n === 'cutoff').at(-1)[1] - 1554) < 1e-6);
});

const cap = (runs, accents = []) => ({ beatsPerBar: 4, runs, accents });

test('a stop inside a bar is an exact volume step in the saved song, and a stop on a bar line needs none', () => {
  const model = saveModel(cap([{ clip: 'a', lane: 'L', from: 0, to: 9.5 }, { clip: 'b', lane: 'M', from: 0, to: 8 }]));
  const [a, b] = model.runs;
  assert.equal(a.stopBar, 3.375);   // 1 + 9.5 / 4
  assert.equal(b.stopBar, undefined);
  const text = songText({ title: 't', tempo: 120, model, files: new Map([['a', 'c/A.wav'], ['b', 'c/B.wav']]) });
  assert.match(text, /volume a-1-stop \{\n  gain auto\(1:0, 3.3735:0, 3.375:-60\)\n  from a-1.audio -> audio\n\}/);
  assert.match(text, /bus lane-L \{\n  inputs 2\n  from a-1-stop.audio -> audio1\n\}/);
  assert.ok(!text.includes('b-1-stop'));
});

test('a one-shot is a block that plays once, a cut momentary sound ends where the pad was let go, and both sit on their lane', () => {
  const model = saveModel(cap([{ clip: 'loop', lane: 'Drums', from: 0, to: 16 }], [
    { clip: 'zap', lane: 'FX', from: 4, to: 5.2, endSeconds: 0.6 },
    { clip: 'hit', lane: 'FX', from: 8.5, to: 9 },
  ]));
  assert.equal(model.once, 2);
  const [loop, zap, hit] = model.runs;
  assert.deepEqual([loop.once, zap.once, hit.once], [false, true, true]);
  assert.deepEqual([zap.bar, zap.bars, zap.endSeconds, zap.exact], [2, 1, 0.6, true]);
  assert.deepEqual([hit.bar, hit.bars, hit.offsetBeats], [3, 1, 0.5]);
  const text = songText({ title: 't', tempo: 120, model, files: new Map([['loop', 'c/L.wav'], ['zap', 'c/Z.wav'], ['hit', 'c/H.wav']]) });
  assert.match(text, /import-audio zap-1 at 2 for 1 \{\n  file "c\/Z.wav"\n  loop off\n  end 0.6\n\}/);
  assert.match(text, /import-audio hit-1 at 3 for 1 \{\n  file "c\/H.wav"\n  loop off\n  offset 0.5 beats\n\}/);
  assert.match(text, /import-audio loop-1 at 1 for 4 \{\n  file "c\/L.wav"\n  loop on\n\}/);
  assert.match(text, /bus lane-FX \{\n  inputs 2\n  from zap-1.audio -> audio1\n  from hit-1.audio -> audio2\n\}/);
});

test('the level of every bar of chunks, and the bars side by side', () => {
  const l = new Float32Array(400), r = new Float32Array(400);
  l.fill(0.5, 0, 200); r.fill(0.5, 0, 200);   // bars 1 and 2 are loud, bar 3 is silent, bar 4 is half as loud
  l.fill(0.25, 300, 400); r.fill(0.25, 300, 400);
  const levels = barLevels([{ l, r }], 0, 100, 4);
  assert.ok(Math.abs(levels[0] - 20 * Math.log10(0.5)) < 1e-9 && Math.abs(levels[1] - 20 * Math.log10(0.5)) < 1e-9);
  assert.equal(levels[2], -120);
  assert.ok(Math.abs(levels[3] - 20 * Math.log10(0.25)) < 1e-9);
  const shifted = barLevels([{ l: l.slice(0, 250), r: r.slice(0, 250) }, { l: l.slice(250), r: r.slice(250) }], 100, 100, 3);   // two chunks, starting inside the first
  assert.ok(Math.abs(shifted[0] - 20 * Math.log10(0.5)) < 1e-9 && shifted[1] === -120 && Math.abs(shifted[2] - 20 * Math.log10(0.25)) < 1e-9);
  const same = compareBars(levels, levels);
  assert.deepEqual([same.compared, same.worst.diff, same.meanAbs], [3, 0, 0]);   // the silent bar is not compared
  assert.match(compareText(same), /every bar within 1 dB/);
  const off = compareBars([-10, -10, -10], [-10, -13, -10.2]);
  assert.deepEqual(off.worst, { bar: 2, diff: -3 });
  assert.match(compareText(off), /1 bar differs more than 1 dB; the largest is bar 2 \(-3.0 dB\)/);
  assert.equal(compareBars([-10], [-90]).worst.diff, -50, 'a sound that is gone is a large difference, measured against the floor');
  assert.match(compareText(compareBars([-90], [-90])), /both silent/);
});

test('the bars where the sum went past full scale, and how they are said', () => {
  const peaks = [{ time: 10, peak: 0.8 }, { time: 12.1, peak: 1.2 }, { time: 12.3, peak: 1.5 }, { time: 14.2, peak: 1.1 }, { time: 3, peak: 9 }];
  const clipped = clippedBars(peaks, 10, 2);   // bars of two seconds from t=10; the peak before the first launch does not count
  assert.deepEqual(clipped.bars, [2, 3]);
  assert.ok(Math.abs(clipped.peakDb - 20 * Math.log10(1.5)) < 1e-9);
  assert.equal(clippedBars([{ time: 11, peak: 0.99 }], 10, 2).peakDb, null);
  assert.equal(barList([2]), 'bar 2');
  assert.equal(barList([2, 3]), 'bars 2 and 3');
  assert.equal(barList([2, 3, 4, 9]), 'bars 2 to 4, 9');
  assert.equal(barList([1, 3, 5, 7, 9, 11]), 'bars 1, 3, 5, 7 and more');
});
