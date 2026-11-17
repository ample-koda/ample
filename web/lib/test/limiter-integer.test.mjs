import test from 'node:test';
import assert from 'node:assert/strict';
import { gainQ15, LimiterCore } from '../dynamics-core.mjs';
import { loadVectors, serverChannels, quantizedInput, deviationDb } from '../dsp-conformance.mjs';

test('the Q15 gain table of the server', () => {
  assert.equal(gainQ15(0), 32768);
  assert.equal(gainQ15(-6), 16423);
  assert.equal(gainQ15(6), 65381);
});

test('the limiter reproduces the server vector in whole numbers (the release is an integer division: 20 % slower than the float version)', () => {
  const entry = loadVectors().cases.limiter, input = quantizedInput(), core = new LimiterCore(input.sampleRate, 2);
  core.configure(entry.params);
  const total = input.channels[0].length + core.latency, padded = input.channels.map((c) => { const copy = new Float32Array(total); copy.set(c); return copy; }), out = input.channels.map(() => new Float32Array(total));
  for (let s = 0; s < total; s += 128) core.process(padded, out, s, Math.min(total, s + 128));
  assert.ok(deviationDb(serverChannels(entry), out.map((c) => c.slice(core.latency)), input.sampleRate) <= -75);
});
