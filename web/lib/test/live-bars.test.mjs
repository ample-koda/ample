import test from 'node:test';
import assert from 'node:assert/strict';
import { fitLiveBars, liveBarsNote, LIVE_BYTES, LIVE_BYTES_PER_FRAME } from '../live-bars.mjs';
import { synthWorkingSetBytes } from '../clientside-synth.mjs';

const per = (bpm, rate = 44100) => Math.floor(4 * 60 * rate / bpm);
test('short songs play whole', () => { assert.equal(fitLiveBars(32, per(120)), 32); assert.equal(liveBarsNote(32, 32), ''); });
test('long songs fit the synth working set and say so', () => {
  const p = per(120), bars = fitLiveBars(400, p);
  assert.ok(bars >= 1 && bars < 400);
  assert.ok(synthWorkingSetBytes(bars * p, 2) <= 128 * 1024 * 1024);
  assert.equal(liveBarsNote(400, bars), `previewing bars 1-${bars} of 400`);
  assert.equal(LIVE_BYTES_PER_FRAME, synthWorkingSetBytes(1, 2));
  assert.ok(LIVE_BYTES < 128 * 1024 * 1024);
});
test('never fewer than one bar', () => { assert.equal(fitLiveBars(5, 1e9), 1); });
