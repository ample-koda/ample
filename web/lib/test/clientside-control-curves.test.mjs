// The controls and automated mixes of the client preview (web/lib/clientside-control-curves.mjs) against the integer arithmetic of the server
import test from 'node:test';
import assert from 'node:assert/strict';
import { blendMix, connectedPoints, controlAt, curveAt, curveControl, fixed, followControl, mapControl, milliseconds, mixPercent } from '../clientside-control-curves.mjs';

test('numbers and times read as ControlBlocks.Fixed and Milliseconds', () => {
  assert.equal(fixed('0.5', 0), 500); assert.equal(fixed('30 %', 0), 300); assert.equal(fixed('-1.25', 0), -1250); assert.equal(fixed('x', 7), 7);
  assert.equal(milliseconds('80 ms', 0), 80); assert.equal(milliseconds('1.5 s', 0), 1500);
});

test('a control is read with a truncating straight line (ControlCurve.At)', () => {
  const c = { firstBar: 1, unit: null, values: Int32Array.from([0, 1000, 1000]) };
  assert.equal(controlAt(c, 0, 1000, 4000), 0);
  assert.equal(controlAt(c, 1, 2000, 4000), 500);   // half a value step at 2000 Hz
  assert.equal(controlAt(c, 99999, 1000, 4000), 1000);
});

test('points are joined per sample with truncation (Automation.CurveAt)', () => {
  const curve = curveAt([{ sample: 0, value: 0 }, { sample: 3, value: 10 }], 1, 100, 5);
  assert.deepEqual([...curve], [0, 3, 6, 10, 10]);
});

test('curve: a plain number all the time, auto(...) per mille', () => {
  assert.ok(curveControl({ value: '0.25' }, 1, 1, 1000, 1000).values.every((v) => v === 250));
  const ramp = curveControl({ value: 'auto(1:0, 2:1000)' }, 1, 2, 1000, 1000).values;
  assert.equal(ramp[0], 0); assert.equal(ramp[500], 500); assert.equal(ramp[1000], 1000);
});

test('map rescales, inverts and limits like MapRenderer', () => {
  const source = { firstBar: 1, unit: null, values: Int32Array.from([0, 500, 1000, 2000]) };
  assert.deepEqual([...mapControl({ out_low: '0', out_high: '200' }, source).values], [0, 100000, 200000, 200000]);
  assert.deepEqual([...mapControl({ invert: 'on' }, source).values], [1000, 500, 0, 0]);
});

test('follow: a loud start gives a high level, then the release lets it fall; auto settings are left to the server', () => {
  const rate = 8000, mono = new Int32Array(rate);
  for (let i = 0; i < 2000; i++) mono[i] = i % 2 ? 20000 : -20000;
  const made = followControl({ attack: '5 ms', release: '50 ms', baseline: 'off' }, mono, rate, 1);
  assert.ok(made.level.values[200] > 900);
  assert.ok(made.level.values[900] < 50);
  assert.equal(followControl({ band: 'auto' }, mono, rate, 1), null);
});

test('a connected general control is scaled to the range of the parameter', () => {
  const points = connectedPoints({ firstBar: 1, unit: null, values: Int32Array.from([500, 500]) }, { min: 0, max: 100 }, 1, 1, 1000, 1000);
  assert.equal(points[0].value, 50);
  assert.equal(points[0].sample, 0);
});

test('an automated mix blends dry and wet per sample with whole numbers (Blend)', () => {
  const percent = mixPercent([{ sample: 0, value: 0 }, { sample: 2, value: 100 }], 1, 100, 1000, 3);
  assert.deepEqual([...percent], [0, 50, 100]);
  const out = blendMix({ channels: [Float32Array.from([0.5, 0.5, 0.5])] }, { sampleRate: 1000, channels: [Float32Array.from([0, 0, 0])] }, percent);
  assert.deepEqual([...out.channels[0]].map((v) => v * 32768), [16384, 8192, 0]);
});
