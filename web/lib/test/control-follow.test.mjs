import assert from 'node:assert/strict';
import test from 'node:test';
import { bandOf, fireNotes, followSeries } from '../control-follow.mjs';

const rate = 8000;
const burst = (from, to, total, hz = 200) => Float32Array.from({ length: total }, (_, i) => (i >= from && i < to ? 0.6 * Math.sin(2 * Math.PI * hz * i / rate) : 0));

test('follow rises with a passing sound and the gate holds, like the server', () => {
  const audio = { sampleRate: rate, channels: [burst(rate, 2 * rate, 4 * rate)] };
  const { values, gate } = followSeries(audio, { attack: '20 ms', release: '100 ms', baseline: 'off', hold: '200 ms', threshold: '0.3', normalize: 'on' });
  assert.equal(values.length, 4001);
  assert.ok(values[500] < 50 && values[1500] > 600 && values[3500] < 100);
  assert.equal(gate[500], 0); assert.equal(gate[1500], 1000); assert.equal(gate[2100], 1000); assert.equal(gate[3000], 0);
  assert.equal(followSeries(audio, { band: 'auto' }), null);
});

test('bands: low keeps a low tone, high drops it', () => {
  const x = burst(0, rate, rate, 100), e = (a) => a.reduce((s, v) => s + v * v, 0);
  assert.ok(e(bandOf(x, rate, 'low')) > 5 * e(bandOf(x, rate, 'high')));
});

test('fire plays a note each time the control crosses the level, with a minimum gap', () => {
  const values = new Float32Array(6000);
  for (let k = 1000; k < 1500; k++) values[k] = 800;
  for (let k = 1700; k < 2200; k++) values[k] = 800;
  for (let k = 4000; k < 4500; k++) values[k] = 800;
  const spb = rate * 2, notes = fireNotes(values, { threshold: '0.5', note: '36', gap: '800 ms', length: '250 ms' }, { samplesPerBar: spb, rate, bars: 3 });
  assert.deepEqual(notes.map((n) => n.pitch), [36, 36]);   // the crossing at 1700 is inside the gap of the one at 1000
  assert.equal(notes[0].velocity, 100);
  assert.ok(fireNotes(values, { threshold: '0.5', direction: 'falling', note: '40' }, { samplesPerBar: spb, rate, bars: 3 }).length >= 1);
});
